using System.Collections.Immutable;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.TypeSystem;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZLinq;
using Attribute = ICSharpCode.Decompiler.CSharp.Syntax.Attribute;
using SyntaxTree = ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static string RemoveInvalidUsingDirectives(string source)
    {
        var normalizedSource = source.Replace("\r\n", "\n");
        var lines = normalizedSource.Split('\n');
        var filteredLines = lines
            .AsValueEnumerable()
            .Where(line => !IsInvalidUsingDirective(line))
            .ToArray();

        return string.Join(Environment.NewLine, filteredLines);
    }

    private static bool IsInvalidUsingDirective(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("using ", StringComparison.Ordinal)
            && !trimmed.StartsWith("global using ", StringComparison.Ordinal))
            return false;

        if (!trimmed.EndsWith(';'))
            return false;

        var syntaxTree = CSharpSyntaxTree.ParseText(
            trimmed + Environment.NewLine + "file class __UsingValidationType {};");
        return syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private void ResolveFileLocalUsings(SyntaxTree tree, CSharpDecompiler decompiler)
    {
        var originalImportedNamespaces = CollectExistingUsingNamespaces(tree);
        RemoveExistingUsingDeclarations(tree);

        var currentTypeDefinition = FindPrimaryTypeDefinition(tree);
        if (currentTypeDefinition is null)
            return;

        var importedNamespaces = originalImportedNamespaces
            .Union(CollectImportedNamespaces(tree, currentTypeDefinition.Namespace))
            .ToImmutableHashSet(StringComparer.Ordinal);
        AddUsingDeclarations(tree, importedNamespaces);
        FullyQualifyAmbiguousTypeReferences(tree, decompiler, currentTypeDefinition, importedNamespaces);
        RemoveUnusedUsingDeclarations(tree);
    }

    private static IImmutableSet<string> CollectExistingUsingNamespaces(SyntaxTree tree) =>
        tree.Children
            .OfType<UsingDeclaration>()
            .Select(usingDeclaration => usingDeclaration.Namespace)
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .ToImmutableHashSet(StringComparer.Ordinal);

    private static void RemoveExistingUsingDeclarations(SyntaxTree tree)
    {
        foreach (var usingDeclaration in tree.Children.OfType<UsingDeclaration>().ToList())
            usingDeclaration.Remove();
    }

    private IImmutableSet<string> CollectImportedNamespaces(SyntaxTree tree, string currentNamespace)
    {
        var collector = new FileLocalImportCollector(currentNamespace);
        tree.AcceptVisitor(collector);
        return collector.ImportedNamespaces.ToImmutableHashSet(StringComparer.Ordinal);
    }

    private static void AddUsingDeclarations(SyntaxTree tree, IImmutableSet<string> importedNamespaces)
    {
        if (importedNamespaces.Count == 0)
            return;

        var insertionPoint = tree.Children.LastOrDefault(node => node is PreProcessorDirective
        {
            Type: PreProcessorDirectiveType.Define
        });

        foreach (var namespaceName in importedNamespaces
                     .OrderBy(name => name.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                     .ThenBy(name => name, StringComparer.Ordinal))
        {
            tree.InsertChildAfter(insertionPoint, new UsingDeclaration(namespaceName), SyntaxTree.MemberRole);
            insertionPoint = insertionPoint is null
                ? tree.Children.FirstOrDefault(node => node is UsingDeclaration)
                : insertionPoint.NextSibling;
        }
    }

    private void FullyQualifyAmbiguousTypeReferences(
        SyntaxTree tree,
        CSharpDecompiler decompiler,
        ITypeDefinition currentTypeDefinition,
        IImmutableSet<string> importedNamespaces)
    {
        var usingScope = CreateUsingScope(decompiler, currentTypeDefinition.Namespace, importedNamespaces);
        var resolver =
            new CSharpResolver(new CSharpTypeResolveContext(decompiler.TypeSystem.MainModule, usingScope,
                currentTypeDefinition));
        var astBuilder = new TypeSystemAstBuilder(resolver)
        {
            AlwaysUseShortTypeNames = false,
            AddResolveResultAnnotations = true,
            UseNullableSpecifierForValueTypes = true,
            UseAliases = true,
            AlwaysUseGlobal = false
        };

        tree.AcceptVisitor(new FileLocalTypeQualificationVisitor(astBuilder, resolver, currentTypeDefinition.Namespace));
    }

    private UsingScope CreateUsingScope(CSharpDecompiler decompiler, string currentNamespace,
        IImmutableSet<string> importedNamespaces)
    {
        var resolvedNamespaces = importedNamespaces
            .Select(namespaceName => decompiler.TypeSystem.GetNamespaceByFullName(namespaceName))
            .Where(namespaceValue => namespaceValue is not null)
            .Cast<INamespace>()
            .ToImmutableArray();

        var usingScope = new UsingScope(
            new CSharpTypeResolveContext(decompiler.TypeSystem.MainModule),
            decompiler.TypeSystem.RootNamespace,
            resolvedNamespaces);

        if (string.IsNullOrWhiteSpace(currentNamespace))
            return usingScope;

        return currentNamespace
            .Split('.')
            .Aggregate(usingScope, CreateNestedUsingScope);
    }

    private static UsingScope CreateNestedUsingScope(UsingScope parentScope, string namespacePart)
    {
        var namespaceValue = parentScope.Namespace.GetChildNamespace(namespacePart)
            ?? new DummyNamespace(parentScope.Namespace, namespacePart);
        return new UsingScope(
            new CSharpTypeResolveContext(parentScope.Namespace.Compilation.MainModule, parentScope),
            namespaceValue,
            []);
    }

    private static ITypeDefinition? FindPrimaryTypeDefinition(SyntaxTree tree) =>
        tree.GetTypes()
            .AsValueEnumerable()
            .Select(typeDeclaration => typeDeclaration.GetSymbol() as ITypeDefinition)
            .FirstOrDefault(typeDefinition => typeDefinition is not null);

    private static void RemoveUnusedUsingDeclarations(SyntaxTree tree)
    {
        var usedNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in tree.Descendants.OfType<AstType>())
            switch (type.GetResolveResult())
            {
                case TypeResolveResult typeResolveResult
                    when !string.IsNullOrWhiteSpace(typeResolveResult.Type.Namespace):
                    usedNamespaces.Add(typeResolveResult.Type.Namespace);
                    break;
                case NamespaceResolveResult namespaceResolveResult
                    when !string.IsNullOrWhiteSpace(namespaceResolveResult.Namespace.FullName):
                    usedNamespaces.Add(namespaceResolveResult.Namespace.FullName);
                    break;
            }

        foreach (var invocation in tree.Descendants.OfType<InvocationExpression>())
            if (invocation.GetResolveResult() is CSharpInvocationResolveResult
                {
                    IsExtensionMethodInvocation: true,
                    Member.DeclaringType: { } declaringType
                } && !string.IsNullOrWhiteSpace(declaringType.Namespace))
                usedNamespaces.Add(declaringType.Namespace);

        foreach (var usingDeclaration in tree.Children.OfType<UsingDeclaration>().ToList())
            if (!usedNamespaces.Contains(usingDeclaration.Namespace))
                usingDeclaration.Remove();
    }

    private sealed class FileLocalImportCollector(string currentNamespace) : DepthFirstAstVisitor
    {
        public HashSet<string> ImportedNamespaces { get; } = new(StringComparer.Ordinal);

        private bool IsParentOfCurrentNamespace(string namespaceName)
        {
            if (namespaceName.Length == 0)
                return true;

            if (!currentNamespace.StartsWith(namespaceName, StringComparison.Ordinal))
                return false;

            return currentNamespace.Length == namespaceName.Length || currentNamespace[namespaceName.Length] == '.';
        }

        public override void VisitSimpleType(SimpleType simpleType)
        {
            AddImportedNamespace(simpleType.GetResolveResult());

            base.VisitSimpleType(simpleType);
        }

        public override void VisitMemberType(MemberType memberType)
        {
            AddImportedNamespace(memberType.GetResolveResult());

            base.VisitMemberType(memberType);
        }

        private void AddImportedNamespace(ResolveResult resolveResult)
        {
            var namespaceName = resolveResult switch
            {
                TypeResolveResult { Type: { } type } => type.Namespace,
                NamespaceResolveResult namespaceResolveResult => namespaceResolveResult.NamespaceName,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(namespaceName) || IsParentOfCurrentNamespace(namespaceName))
                return;

            ImportedNamespaces.Add(namespaceName);
        }
    }

    private sealed class DummyNamespace(INamespace parentNamespace, string name) : INamespace
    {
        string INamespace.ExternAlias => string.Empty;

        string INamespace.FullName => NamespaceDeclaration.BuildQualifiedName(parentNamespace.FullName, name);

        public string Name => name;

        ICSharpCode.Decompiler.TypeSystem.SymbolKind ICSharpCode.Decompiler.TypeSystem.ISymbol.SymbolKind =>
            ICSharpCode.Decompiler.TypeSystem.SymbolKind.Namespace;

        INamespace INamespace.ParentNamespace => parentNamespace;

        IEnumerable<INamespace> INamespace.ChildNamespaces => [];

        IEnumerable<ITypeDefinition> INamespace.Types => [];

        IEnumerable<IModule> INamespace.ContributingModules => [];

        ICompilation ICompilationProvider.Compilation => parentNamespace.Compilation;

        INamespace? INamespace.GetChildNamespace(string name) => null;

        ITypeDefinition? INamespace.GetTypeDefinition(string name, int typeParameterCount) => null;
    }

    private sealed class FileLocalTypeQualificationVisitor(
            TypeSystemAstBuilder astBuilder,
            CSharpResolver resolver,
            string currentNamespace)
        : DepthFirstAstVisitor
    {
        public override void VisitSimpleType(SimpleType simpleType)
        {
            RewriteType(simpleType);
            base.VisitSimpleType(simpleType);
        }

        public override void VisitMemberType(MemberType memberType)
        {
            if (memberType.Parent is AstType)
            {
                base.VisitMemberType(memberType);
                return;
            }

            RewriteType(memberType);
            base.VisitMemberType(memberType);
        }

        private void RewriteType(AstType type)
        {
            if (type.GetResolveResult() is not TypeResolveResult typeResolveResult)
                return;

            astBuilder.NameLookupMode = GetNameLookupMode(type);

            var previousAlwaysUseShortTypeNames = astBuilder.AlwaysUseShortTypeNames;
            if (IsNestedType(typeResolveResult.Type))
                astBuilder.AlwaysUseShortTypeNames = false;

            AstType replacement;
            try
            {
                replacement = type.Parent is Attribute
                    ? astBuilder.ConvertAttributeType(typeResolveResult.Type)
                    : ConvertType(typeResolveResult.Type);
            }
            finally
            {
                astBuilder.AlwaysUseShortTypeNames = previousAlwaysUseShortTypeNames;
            }

            type.ReplaceWith(replacement);
        }

        private static bool IsNestedType(IType type) =>
            type.DeclaringType is not null
            || type.GetDefinition()?.DeclaringTypeDefinition is not null;

        private AstType ConvertType(IType type)
        {
            var convertedType = astBuilder.ConvertType(type);
            if (!IsFullyQualifiedMemberType(convertedType))
                return convertedType;
            if (IsNestedType(type))
                return convertedType;

            return TryCreateShorterType(type) ?? convertedType;
        }

        private AstType? TryCreateShorterType(IType type)
        {
            foreach (var candidate in GetTypeNameCandidates(type))
            {
                if (!ResolvesToType(candidate, type))
                    continue;

                var candidateType = BuildType(candidate, type.TypeArguments);
                candidateType.AddAnnotation(new TypeResolveResult(type));
                return candidateType;
            }

            return null;
        }

        private IEnumerable<string[]> GetTypeNameCandidates(IType type)
        {
            if (string.IsNullOrWhiteSpace(type.Namespace))
                yield break;

            var fullNameParts = type.Namespace.Split('.').Append(type.Name).ToArray();
            var currentNamespaceParts = string.IsNullOrWhiteSpace(currentNamespace)
                ? []
                : currentNamespace.Split('.');
            var commonPrefixLength = fullNameParts
                .Take(fullNameParts.Length - 1)
                .Zip(currentNamespaceParts)
                .TakeWhile(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal))
                .Count();

            for (var i = Math.Max(commonPrefixLength, 0); i < fullNameParts.Length - 1; i++)
                yield return fullNameParts[i..];
        }

        private bool ResolvesToType(string[] nameParts, IType expectedType)
        {
            if (nameParts.Length == 0)
                return false;

            var currentResult = ResolveCandidateRoot(nameParts[0], expectedType);
            if (currentResult is null)
                return false;

            for (var i = 1; i < nameParts.Length; i++)
            {
                currentResult = ResolveCandidateMember(
                    currentResult,
                    nameParts[i],
                    i == nameParts.Length - 1 ? expectedType : null);
                if (currentResult is null)
                    return false;
            }

            return currentResult is TypeResolveResult typeResolveResult
                && TypeMatches(typeResolveResult.Type, expectedType);
        }

        private ResolveResult? ResolveCandidateRoot(string identifier, IType expectedType)
        {
            var result = resolver.LookupSimpleNameOrTypeName(
                identifier,
                identifier == expectedType.Name ? GetLocalTypeArguments(expectedType) : [],
                astBuilder.NameLookupMode);
            return result.IsError ? null : result;
        }

        private ResolveResult? ResolveCandidateMember(ResolveResult target, string identifier, IType? expectedType)
        {
            var result = resolver.ResolveMemberAccess(
                target,
                identifier,
                expectedType is not null && identifier == expectedType.Name ? GetLocalTypeArguments(expectedType) : [],
                astBuilder.NameLookupMode);
            return result.IsError ? null : result;
        }

        private static IReadOnlyList<IType> GetLocalTypeArguments(IType type)
        {
            var outerTypeParameterCount = type.DeclaringType?.TypeParameterCount ?? 0;
            return type.TypeArguments.Skip(outerTypeParameterCount).ToArray();
        }

        private static bool TypeMatches(IType resolvedType, IType expectedType) =>
            resolvedType.GetDefinition() is { } resolvedDefinition
            && expectedType.GetDefinition() is { } expectedDefinition
            && string.Equals(resolvedDefinition.FullName, expectedDefinition.FullName, StringComparison.Ordinal)
            && resolvedType.TypeParameterCount == expectedType.TypeParameterCount;

        private AstType BuildType(string[] nameParts, IReadOnlyList<IType> typeArguments)
        {
            AstType result = new SimpleType(nameParts[0]);
            for (var i = 1; i < nameParts.Length; i++)
                result = new MemberType(result, nameParts[i]);

            foreach (var typeArgument in typeArguments)
                result.AddChild(astBuilder.ConvertType(typeArgument), Roles.TypeArgument);

            return result;
        }

        private static bool IsFullyQualifiedMemberType(AstType type) =>
            type is MemberType { Target: MemberType or SimpleType };

        private static NameLookupMode GetNameLookupMode(AstType type)
        {
            var lookupMode = type.GetNameLookupMode();
            if (lookupMode != NameLookupMode.Type)
                return lookupMode;

            var outermostType = type;
            while (outermostType.Parent is AstType parentType)
                outermostType = parentType;

            return outermostType.Parent is TypeReferenceExpression
                ? NameLookupMode.Expression
                : lookupMode;
        }
    }
}
