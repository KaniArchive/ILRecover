using ICSharpCode.Decompiler.Metadata;
using ZLinq;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynCSharp = Microsoft.CodeAnalysis.CSharp;
using RoslynSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private string RestoreUsingDirectives(string source)
    {
        var syntaxTree = RoslynCSharp.CSharpSyntaxTree.ParseText(source);
        if (syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            syntaxTree.GetRoot() is not RoslynSyntax.CompilationUnitSyntax root)
            return source;

        var currentNamespace = GetCurrentNamespace(root);
        var importedNamespaces = root.Usings
            .AsValueEnumerable()
            .Where(usingDirective => usingDirective.Alias is null && usingDirective.StaticKeyword.RawKind == 0)
            .Select(usingDirective => usingDirective.Name?.ToString())
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .ToHashSet(StringComparer.Ordinal);

        var namespacesToAdd = ExtractTypeLikeIdentifiers(root)
            .AsValueEnumerable()
            .Select(identifier => ResolveNamespaceForIdentifier(identifier, currentNamespace))
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Select(namespaceName => namespaceName!)
            .Where(namespaceName => !importedNamespaces.Contains(namespaceName))
            .Where(namespaceName => !string.Equals(namespaceName, currentNamespace, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(namespaceName => namespaceName.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(namespaceName => namespaceName, StringComparer.Ordinal)
            .ToList();

        return namespacesToAdd.Count == 0 ? source : InsertUsingDirectives(source, namespacesToAdd);
    }

    private static string InsertUsingDirectives(string source, IReadOnlyList<string> namespacesToAdd)
    {
        var normalizedSource = source.Replace("\r\n", "\n");
        var lines = normalizedSource.Split(['\n']).ToList();
        var insertIndex = 0;

        while (insertIndex < lines.Count)
        {
            var trimmed = lines[insertIndex].Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#') ||
                trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                trimmed.StartsWith("global using ", StringComparison.Ordinal))
            {
                insertIndex++;
                continue;
            }

            break;
        }

        var usingLines = namespacesToAdd
            .Select(namespaceName => $"using {namespaceName};")
            .ToList();

        if (usingLines.Count == 0)
            return source;

        lines.InsertRange(insertIndex, usingLines);

        var followingLineIndex = insertIndex + usingLines.Count;
        if (followingLineIndex < lines.Count && !string.IsNullOrWhiteSpace(lines[followingLineIndex]))
            lines.Insert(followingLineIndex, string.Empty);

        return string.Join(Environment.NewLine, lines);
    }

    private string? ResolveNamespaceForIdentifier(string identifier, string? currentNamespace)
    {
        if (!TryGetCandidateNamespaces(identifier, out var candidates))
            return null;

        var namespaces = candidates
            .AsValueEnumerable()
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var preferredNamespaces = GetPreferredCandidateNamespaces(identifier)
            .AsValueEnumerable()
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var preferredNamespace = ResolvePreferredNamespace(preferredNamespaces, currentNamespace);
        if (!string.IsNullOrWhiteSpace(preferredNamespace))
            return preferredNamespace;

        return ResolvePreferredNamespace(namespaces, currentNamespace);
    }

    private string? ResolvePreferredNamespace(IReadOnlyList<string> namespaces, string? currentNamespace)
    {
        switch (namespaces.Count)
        {
            case 0:
                return null;
            case 1:
                return namespaces[0];
        }

        var preferredNonSystemNamespaces = namespaces
            .AsValueEnumerable()
            .Where(namespaceName => !namespaceName.Equals("System", StringComparison.Ordinal)
                                    && !namespaceName.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        if (preferredNonSystemNamespaces.Count == 1)
            return preferredNonSystemNamespaces[0];

        var preferredProjectNamespaces = namespaces
            .Where(namespaceName => namespaceName.Equals(_assemblyName, StringComparison.Ordinal)
                                    || namespaceName.StartsWith(_assemblyName + ".", StringComparison.Ordinal))
            .ToList();

        if (preferredProjectNamespaces.Count == 1)
            return preferredProjectNamespaces[0];

        if (!string.IsNullOrWhiteSpace(currentNamespace))
        {
            var rootNamespace = currentNamespace.Split('.')[0];
            var preferredCurrentNamespaces = namespaces
                .Where(namespaceName => namespaceName.Equals(rootNamespace, StringComparison.Ordinal)
                                        || namespaceName.StartsWith(rootNamespace + ".", StringComparison.Ordinal))
                .ToList();

            if (preferredCurrentNamespaces.Count == 1)
                return preferredCurrentNamespaces[0];
        }

        var preferredSystemNamespaces = namespaces
            .Where(namespaceName => namespaceName.Equals("System", StringComparison.Ordinal)
                                    || namespaceName.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        return preferredSystemNamespaces.Count == 1 ? preferredSystemNamespaces[0] : null;
    }

    private IEnumerable<string> GetPreferredCandidateNamespaces(string identifier)
    {
        var preferredTypeNamespaceIndex = GetPreferredTypeNamespaceIndex();
        if (preferredTypeNamespaceIndex.TryGetValue(identifier, out var candidates))
            return candidates;

        if (!identifier.EndsWith("Attribute", StringComparison.Ordinal)
            && preferredTypeNamespaceIndex.TryGetValue(identifier + "Attribute", out candidates))
            return candidates;

        return [];
    }

    private bool TryGetCandidateNamespaces(string identifier, out HashSet<string> candidates)
    {
        var typeNamespaceIndex = GetTypeNamespaceIndex();
        if (typeNamespaceIndex.TryGetValue(identifier, out candidates!))
            return true;

        if (!identifier.EndsWith("Attribute", StringComparison.Ordinal)
            && typeNamespaceIndex.TryGetValue(identifier + "Attribute", out candidates!))
            return true;

        candidates = null!;
        return false;
    }

    private Dictionary<string, HashSet<string>> GetTypeNamespaceIndex()
    {
        if (_typeNamespaceIndex is not null)
            return _typeNamespaceIndex;

        var assemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preferredAssemblyNames = GetPreferredAssemblyNames();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
                assemblyPaths.Add(path);

        foreach (var directory in GetFormattingReferenceDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.GetFiles(directory, "*.dll"))
                assemblyPaths.Add(Path.GetFullPath(path));
        }

        assemblyPaths.Add(Path.GetFullPath(dllPath));

        _preferredTypeNamespaceIndex = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var assemblyPath in assemblyPaths)
            AddAssemblyTypesToNamespaceIndex(
                result,
                _preferredTypeNamespaceIndex,
                assemblyPath,
                preferredAssemblyNames.Contains(Path.GetFileNameWithoutExtension(assemblyPath)));

        _typeNamespaceIndex = result;
        return result;
    }

    private Dictionary<string, HashSet<string>> GetPreferredTypeNamespaceIndex()
    {
        if (_preferredTypeNamespaceIndex is not null)
            return _preferredTypeNamespaceIndex;

        _ = GetTypeNamespaceIndex();
        return _preferredTypeNamespaceIndex ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    }

    private HashSet<string> GetPreferredAssemblyNames()
    {
        var preferredAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileNameWithoutExtension(dllPath)
        };

        try
        {
            var file = new PEFile(dllPath);
            foreach (var handle in file.Metadata.AssemblyReferences)
            {
                var assemblyReference = file.Metadata.GetAssemblyReference(handle);
                var assemblyName = file.Metadata.GetString(assemblyReference.Name);
                if (!string.IsNullOrWhiteSpace(assemblyName))
                    preferredAssemblyNames.Add(assemblyName);
            }
        }
        catch
        {
        }

        return preferredAssemblyNames;
    }

    private static void AddAssemblyTypesToNamespaceIndex(
        Dictionary<string, HashSet<string>> index,
        Dictionary<string, HashSet<string>> preferredIndex,
        string assemblyPath,
        bool includeInPreferredIndex)
    {
        try
        {
            var file = new PEFile(assemblyPath);
            var reader = file.Metadata;

            foreach (var handle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(handle);
                if (!typeDefinition.GetDeclaringType().IsNil)
                    continue;

                var namespaceName = reader.GetString(typeDefinition.Namespace);
                if (string.IsNullOrWhiteSpace(namespaceName))
                    continue;

                var name = NormalizeMetadataTypeName(reader.GetString(typeDefinition.Name));
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!index.TryGetValue(name, out var namespaces))
                {
                    namespaces = new HashSet<string>(StringComparer.Ordinal);
                    index[name] = namespaces;
                }

                namespaces.Add(namespaceName);

                if (!includeInPreferredIndex)
                    continue;

                if (!preferredIndex.TryGetValue(name, out var preferredNamespaces))
                {
                    preferredNamespaces = new HashSet<string>(StringComparer.Ordinal);
                    preferredIndex[name] = preferredNamespaces;
                }

                preferredNamespaces.Add(namespaceName);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeMetadataTypeName(string name)
    {
        var arityIndex = name.IndexOf('`');
        return arityIndex >= 0 ? name[..arityIndex] : name;
    }

    private static HashSet<string> ExtractTypeLikeIdentifiers(RoslynSyntax.CompilationUnitSyntax root)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeSyntax in root.DescendantNodes().OfType<RoslynSyntax.TypeSyntax>())
            AddIdentifiersFromTypeSyntax(typeSyntax, identifiers);

        foreach (var attributeSyntax in root.DescendantNodes().OfType<RoslynSyntax.AttributeSyntax>())
            AddIdentifiersFromNameSyntax(attributeSyntax.Name, identifiers);

        foreach (var memberAccess in root.DescendantNodes().OfType<RoslynSyntax.MemberAccessExpressionSyntax>())
        {
            var leftMostExpression = GetLeftMostExpression(memberAccess.Expression);

            switch (leftMostExpression)
            {
                case RoslynSyntax.IdentifierNameSyntax identifierName
                    when StartsWithUppercase(identifierName.Identifier.ValueText):
                    identifiers.Add(identifierName.Identifier.ValueText);
                    break;
                case RoslynSyntax.GenericNameSyntax genericName
                    when StartsWithUppercase(genericName.Identifier.ValueText):
                    identifiers.Add(genericName.Identifier.ValueText);
                    foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
                        AddIdentifiersFromTypeSyntax(typeArgument, identifiers);
                    break;
            }
        }

        return identifiers;
    }

    private static void AddIdentifiersFromTypeSyntax(RoslynSyntax.TypeSyntax typeSyntax, HashSet<string> identifiers)
    {
        foreach (var node in typeSyntax.DescendantNodesAndSelf())
            switch (node)
            {
                case RoslynSyntax.IdentifierNameSyntax identifierName
                    when StartsWithUppercase(identifierName.Identifier.ValueText):
                    identifiers.Add(identifierName.Identifier.ValueText);
                    break;
                case RoslynSyntax.GenericNameSyntax genericName
                    when StartsWithUppercase(genericName.Identifier.ValueText):
                    identifiers.Add(genericName.Identifier.ValueText);
                    break;
            }
    }

    private static void AddIdentifiersFromNameSyntax(RoslynSyntax.NameSyntax nameSyntax, HashSet<string> identifiers)
    {
        switch (nameSyntax)
        {
            case RoslynSyntax.IdentifierNameSyntax identifierName
                when StartsWithUppercase(identifierName.Identifier.ValueText):
                identifiers.Add(identifierName.Identifier.ValueText);
                break;
            case RoslynSyntax.GenericNameSyntax genericName
                when StartsWithUppercase(genericName.Identifier.ValueText):
                identifiers.Add(genericName.Identifier.ValueText);
                foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
                    AddIdentifiersFromTypeSyntax(typeArgument, identifiers);
                break;
            case RoslynSyntax.QualifiedNameSyntax qualifiedName:
                AddIdentifiersFromNameSyntax(qualifiedName.Left, identifiers);
                AddIdentifiersFromNameSyntax(qualifiedName.Right, identifiers);
                break;
            case RoslynSyntax.AliasQualifiedNameSyntax aliasQualifiedName:
                AddIdentifiersFromNameSyntax(aliasQualifiedName.Name, identifiers);
                break;
        }
    }

    private static RoslynSyntax.ExpressionSyntax GetLeftMostExpression(RoslynSyntax.ExpressionSyntax expression)
    {
        while (expression is RoslynSyntax.MemberAccessExpressionSyntax memberAccess)
            expression = memberAccess.Expression;

        return expression;
    }

    private static string? GetCurrentNamespace(RoslynSyntax.CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
            switch (member)
            {
                case RoslynSyntax.FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    return fileScopedNamespace.Name.ToString();
                case RoslynSyntax.NamespaceDeclarationSyntax namespaceDeclaration:
                    return namespaceDeclaration.Name.ToString();
            }

        return null;
    }

    private static bool StartsWithUppercase(string value) =>
        !string.IsNullOrWhiteSpace(value) && char.IsUpper(value[0]);
}
