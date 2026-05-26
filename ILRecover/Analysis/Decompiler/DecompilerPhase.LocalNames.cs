using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Models;
using Microsoft.CodeAnalysis;
using ZLinq;
using RoslynCSharp = Microsoft.CodeAnalysis.CSharp;
using RoslynSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private SyntaxNode ApplyLocalVariableNameRewrite(SourceFileMap file, SyntaxNode root)
    {
        return root;
    }

    private Dictionary<string, IReadOnlyList<string>> BuildMethodLocalRenamePlans(SourceFileMap file)
    {
        var methodDebugSignatures = GetMethodDebugSignatures();
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var methodGroup in file.Methods
                     .GroupBy(method => MetadataTokens.GetRowNumber(method.MethodHandle)))
        {
            if (!methodDebugSignatures.TryGetValue(methodGroup.Key, out var signature))
                continue;

            var localNames = methodGroup
                .AsValueEnumerable()
                .SelectMany(method => method.LocalVariables)
                .OrderBy(local => local.StartOffset)
                .ThenBy(local => local.SlotIndex)
                .Select(local => local.Name)
                .Where(IsRestorableLocalName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (localNames.Count == 0)
                continue;

            var key = BuildMethodIdentity(signature.TypeFullName, signature.MethodName, signature.ParameterCount,
                signature.IsConstructor);
            if (!result.TryGetValue(key, out var existing) || localNames.Count > existing.Count)
                result[key] = localNames;
        }

        return result;
    }

    private Dictionary<int, MethodDebugSignature> GetMethodDebugSignatures()
    {
        if (_methodDebugSignatures is not null)
            return _methodDebugSignatures;

        var file = new PEFile(dllPath);
        var reader = file.Metadata;
        var result = new Dictionary<int, MethodDebugSignature>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeFullName = BuildTypeFullName(reader, typeHandle);
            var typeDefinition = reader.GetTypeDefinition(typeHandle);

            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDefinition = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(methodDefinition.Name);
                var parameterCount = methodDefinition.GetParameters()
                    .Count(parameterHandle => reader.GetParameter(parameterHandle).SequenceNumber > 0);

                result[MetadataTokens.GetRowNumber(methodHandle)] = new MethodDebugSignature(
                    typeFullName,
                    methodName,
                    parameterCount,
                    methodName is ".ctor" or ".cctor");
            }
        }

        _methodDebugSignatures = result;
        return result;
    }

    private static string BuildTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var typeDefinition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(typeDefinition.Name);
        var declaringTypeHandle = typeDefinition.GetDeclaringType();
        if (!declaringTypeHandle.IsNil)
            return BuildTypeFullName(reader, declaringTypeHandle) + "+" + name;

        var ns = reader.GetString(typeDefinition.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
    }

    private static bool IsRestorableLocalName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && RoslynCSharp.SyntaxFacts.IsValidIdentifier(name)
        && !name.StartsWith("<", StringComparison.Ordinal)
        && !name.StartsWith("CS$", StringComparison.Ordinal);

    private static string BuildMethodIdentity(string typeFullName, string methodName, int parameterCount,
        bool isConstructor) =>
        string.Join("|", typeFullName, methodName, parameterCount, isConstructor ? "ctor" : "method");

    private sealed class LocalVariableNameMethodRewriter(
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, IReadOnlyList<string>> methodLocalNames)
        : RoslynCSharp.CSharpSyntaxRewriter
    {
        private readonly Stack<string> _namespaces = new();
        private readonly Stack<string> _typeNames = new();

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(
            RoslynSyntax.FileScopedNamespaceDeclarationSyntax node)
        {
            _namespaces.Push(node.Name.ToString());
            var result = base.VisitFileScopedNamespaceDeclaration(node);
            _namespaces.Pop();
            return result;
        }

        public override SyntaxNode? VisitNamespaceDeclaration(RoslynSyntax.NamespaceDeclarationSyntax node)
        {
            _namespaces.Push(node.Name.ToString());
            var result = base.VisitNamespaceDeclaration(node);
            _namespaces.Pop();
            return result;
        }

        public override SyntaxNode? VisitClassDeclaration(RoslynSyntax.ClassDeclarationSyntax node)
        {
            _typeNames.Push(node.Identifier.ValueText);
            var result = base.VisitClassDeclaration(node);
            _typeNames.Pop();
            return result;
        }

        public override SyntaxNode? VisitStructDeclaration(RoslynSyntax.StructDeclarationSyntax node)
        {
            _typeNames.Push(node.Identifier.ValueText);
            var result = base.VisitStructDeclaration(node);
            _typeNames.Pop();
            return result;
        }

        public override SyntaxNode? VisitRecordDeclaration(RoslynSyntax.RecordDeclarationSyntax node)
        {
            _typeNames.Push(node.Identifier.ValueText);
            var result = base.VisitRecordDeclaration(node);
            _typeNames.Pop();
            return result;
        }

        public override SyntaxNode? VisitMethodDeclaration(RoslynSyntax.MethodDeclarationSyntax node)
        {
            var rewritten = RenameMethodLocalsIfPossible(
                node,
                node.Identifier.ValueText,
                node.ParameterList.Parameters.Count,
                false);

            return base.VisitMethodDeclaration(rewritten);
        }

        public override SyntaxNode? VisitConstructorDeclaration(RoslynSyntax.ConstructorDeclarationSyntax node)
        {
            var rewritten = RenameMethodLocalsIfPossible(
                node,
                ".ctor",
                node.ParameterList.Parameters.Count,
                true);

            return base.VisitConstructorDeclaration(rewritten);
        }

        private TNode RenameMethodLocalsIfPossible<TNode>(
            TNode node,
            string methodName,
            int parameterCount,
            bool isConstructor)
            where TNode : RoslynCSharp.CSharpSyntaxNode
        {
            var typeFullName = BuildCurrentTypeFullName();
            if (string.IsNullOrWhiteSpace(typeFullName))
                return node;

            var key = BuildMethodIdentity(typeFullName, methodName, parameterCount, isConstructor);
            if (!methodLocalNames.TryGetValue(key, out var desiredLocalNames))
                return node;

            var localDeclarations = node.DescendantNodes()
                .AsValueEnumerable()
                .OfType<RoslynSyntax.LocalDeclarationStatementSyntax>()
                .Where(local =>
                    local.Modifiers.AsValueEnumerable().All(modifier =>
                        modifier.RawKind != (int)RoslynCSharp.SyntaxKind.ConstKeyword))
                .Where(local => local.Declaration.Variables.Count == 1)
                .ToList();

            if (localDeclarations.Count != desiredLocalNames.Count)
                return node;

            var parameterNames = node.DescendantNodes()
                .AsValueEnumerable()
                .OfType<RoslynSyntax.ParameterSyntax>()
                .Select(parameter => parameter.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);

            if (desiredLocalNames.Count != desiredLocalNames.Distinct(StringComparer.Ordinal).Count() ||
                desiredLocalNames.Any(parameterNames.Contains))
                return node;

            var symbolRenameMap = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            var declarationRenameMap = new Dictionary<int, string>();

            for (var i = 0; i < localDeclarations.Count; i++)
            {
                var variable = localDeclarations[i].Declaration.Variables[0];
                var symbol = semanticModel.GetDeclaredSymbol(variable);
                var desiredName = desiredLocalNames[i];
                if (symbol is null)
                    return node;

                symbolRenameMap[symbol] = desiredName;
                declarationRenameMap[variable.Identifier.SpanStart] = desiredName;
            }

            var referenceRenameMap = node.DescendantNodes()
                .AsValueEnumerable()
                .OfType<RoslynSyntax.IdentifierNameSyntax>()
                .Select(identifier => new
                {
                    identifier,
                    symbol = semanticModel.GetSymbolInfo(identifier).Symbol
                })
                .Where(x => x.symbol is not null && symbolRenameMap.ContainsKey(x.symbol))
                .ToDictionary(
                    x => x.identifier.Identifier.SpanStart,
                    x => symbolRenameMap[x.symbol!]);

            return (TNode)new LocalSyntaxRenameRewriter(declarationRenameMap, referenceRenameMap).Visit(node);
        }

        private string BuildCurrentTypeFullName()
        {
            if (_typeNames.Count == 0)
                return string.Empty;

            var typeNames = _typeNames.Reverse().ToArray();
            var typePart = string.Join('+', typeNames);
            if (_namespaces.Count == 0)
                return typePart;

            return _namespaces.Peek() + "." + typePart;
        }
    }

    private sealed class LocalSyntaxRenameRewriter(
        IReadOnlyDictionary<int, string> declarationRenameMap,
        IReadOnlyDictionary<int, string> referenceRenameMap)
        : RoslynCSharp.CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitVariableDeclarator(RoslynSyntax.VariableDeclaratorSyntax node)
        {
            if (declarationRenameMap.TryGetValue(node.Identifier.SpanStart, out var name))
                node = node.WithIdentifier(RoslynCSharp.SyntaxFactory.Identifier(name).WithTriviaFrom(node.Identifier));

            return base.VisitVariableDeclarator(node);
        }

        public override SyntaxNode? VisitIdentifierName(RoslynSyntax.IdentifierNameSyntax node)
        {
            if (referenceRenameMap.TryGetValue(node.Identifier.SpanStart, out var name))
                node = node.WithIdentifier(RoslynCSharp.SyntaxFactory.Identifier(name).WithTriviaFrom(node.Identifier));

            return base.VisitIdentifierName(node);
        }
    }
}
