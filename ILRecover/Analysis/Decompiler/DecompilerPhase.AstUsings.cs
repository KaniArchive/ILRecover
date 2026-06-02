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
        RemoveExistingUsingDeclarations(tree);

        var currentTypeDefinition = FindPrimaryTypeDefinition(tree);
        if (currentTypeDefinition is null)
            return;

        var importedNamespaces = CollectImportedNamespaces(tree, currentTypeDefinition.Namespace);
        AddUsingDeclarations(tree, importedNamespaces);
        FullyQualifyAmbiguousTypeReferences(tree, decompiler, currentTypeDefinition, importedNamespaces);
        RemoveUnusedUsingDeclarations(tree);
    }

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

        tree.AcceptVisitor(new FileLocalTypeQualificationVisitor(astBuilder));
    }

    private UsingScope CreateUsingScope(CSharpDecompiler decompiler, string currentNamespace,
        IImmutableSet<string> importedNamespaces)
    {
        var resolvedNamespaces = importedNamespaces
            .Select(namespaceName => decompiler.TypeSystem.GetNamespaceByFullName(namespaceName))
            .Where(namespaceValue => namespaceValue is not null)
            .Cast<INamespace>()
            .ToImmutableArray();

        var currentNamespaceValue = string.IsNullOrWhiteSpace(currentNamespace)
            ? decompiler.TypeSystem.RootNamespace
            : decompiler.TypeSystem.GetNamespaceByFullName(currentNamespace) ?? decompiler.TypeSystem.RootNamespace;

        return new UsingScope(
            new CSharpTypeResolveContext(decompiler.TypeSystem.MainModule),
            currentNamespaceValue,
            resolvedNamespaces);
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

    private sealed class FileLocalTypeQualificationVisitor(TypeSystemAstBuilder astBuilder) : DepthFirstAstVisitor
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

            astBuilder.NameLookupMode = type.GetNameLookupMode();

            var previousAlwaysUseShortTypeNames = astBuilder.AlwaysUseShortTypeNames;
            if (IsNestedType(typeResolveResult.Type))
                astBuilder.AlwaysUseShortTypeNames = false;

            AstType replacement;
            try
            {
                replacement = type.Parent is Attribute
                    ? astBuilder.ConvertAttributeType(typeResolveResult.Type)
                    : astBuilder.ConvertType(typeResolveResult.Type);
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
    }
}
