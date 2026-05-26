using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using ZLinq;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynCSharp = Microsoft.CodeAnalysis.CSharp;
using RoslynSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private string RestoreUsingDirectives(string source)
    {
        source = RemoveInvalidUsingDirectives(source);
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
            .Select(namespaceName => namespaceName!)
            .ToHashSet(StringComparer.Ordinal);

        var missingTypeReferences = ExtractMissingTypeReferences(syntaxTree, root);

        var namespacesToAdd = missingTypeReferences
            .AsValueEnumerable()
            .Select(reference => ResolveNamespaceForIdentifier(reference, currentNamespace, importedNamespaces))
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

        var syntaxTree = RoslynCSharp.CSharpSyntaxTree.ParseText(trimmed + Environment.NewLine + "file class __UsingValidationType {};");
        return syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
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

    private string? ResolveNamespaceForIdentifier(
        MissingTypeReference reference,
        string? currentNamespace,
        IReadOnlySet<string>? importedNamespaces = null)
    {
        if (!TryGetCandidateNamespaces(reference, out var candidates))
            return null;

        var namespaces = candidates
            .AsValueEnumerable()
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var preferredNamespaces = GetPreferredCandidateNamespaces(reference)
            .AsValueEnumerable()
            .Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (importedNamespaces is not null)
        {
            var alreadyImportedNamespace = namespaces
                .AsValueEnumerable()
                .FirstOrDefault(namespaceName => importedNamespaces.Contains(namespaceName));
            if (!string.IsNullOrWhiteSpace(alreadyImportedNamespace))
                return null;
        }

        var preferredNamespace = ResolvePreferredNamespace(preferredNamespaces, currentNamespace, importedNamespaces);
        if (!string.IsNullOrWhiteSpace(preferredNamespace))
            return preferredNamespace;

        return ResolvePreferredNamespace(namespaces, currentNamespace, importedNamespaces);
    }

    private string? ResolvePreferredNamespace(
        IReadOnlyList<string> namespaces,
        string? currentNamespace,
        IReadOnlySet<string>? importedNamespaces)
    {
        switch (namespaces.Count)
        {
            case 0:
                return null;
            case 1:
                return IsUndesirableNamespace(namespaces[0]) ? null : namespaces[0];
        }

        var filteredNamespaces = namespaces
            .AsValueEnumerable()
            .Where(namespaceName => !IsUndesirableNamespace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (filteredNamespaces.Count == 1)
            return filteredNamespaces[0];

        if (filteredNamespaces.Count > 0)
            namespaces = filteredNamespaces;

        var preferredNonSystemNamespaces = namespaces
            .AsValueEnumerable()
            .Where(namespaceName => !namespaceName.Equals("System", StringComparison.Ordinal)
                                    && !namespaceName.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

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

        if (importedNamespaces is not null)
        {
            var importedNamespaceRoots = importedNamespaces
                .AsValueEnumerable()
                .Select(GetNamespaceRoot)
                .Where(namespaceRoot => !string.IsNullOrWhiteSpace(namespaceRoot))
                .ToHashSet(StringComparer.Ordinal);

            var preferredImportedRootNamespaces = namespaces
                .Where(namespaceName => importedNamespaceRoots.Contains(GetNamespaceRoot(namespaceName)))
                .ToList();

            if (preferredImportedRootNamespaces.Count == 1)
                return preferredImportedRootNamespaces[0];
        }

        if (preferredNonSystemNamespaces.Count == 1)
            return preferredNonSystemNamespaces[0];

        var preferredSystemNamespaces = namespaces
            .Where(namespaceName => namespaceName.Equals("System", StringComparison.Ordinal)
                                    || namespaceName.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        return preferredSystemNamespaces.Count == 1 ? preferredSystemNamespaces[0] : null;
    }

    private static bool IsUndesirableNamespace(string namespaceName) =>
        namespaceName.StartsWith("MS.Internal.", StringComparison.Ordinal)
        || namespaceName.Contains(".Internal.", StringComparison.Ordinal)
        || namespaceName.Contains(".ABI.", StringComparison.Ordinal)
        || namespaceName.StartsWith("<", StringComparison.Ordinal);

    private static string GetNamespaceRoot(string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
            return string.Empty;

        var separatorIndex = namespaceName.IndexOf('.');
        return separatorIndex < 0 ? namespaceName : namespaceName[..separatorIndex];
    }

    private IEnumerable<string> GetPreferredCandidateNamespaces(MissingTypeReference reference)
    {
        if (reference.Arity > 0)
        {
            var preferredIndexByArity = GetPreferredTypeNamespaceIndexByArity();
            if (preferredIndexByArity.TryGetValue(BuildTypeLookupKey(reference.Identifier, reference.Arity), out var arityCandidates))
                return arityCandidates;

            if (reference.IsAttribute
                && preferredIndexByArity.TryGetValue(BuildTypeLookupKey(reference.Identifier + "Attribute", reference.Arity), out arityCandidates))
                return arityCandidates;
        }

        var preferredTypeNamespaceIndex = GetPreferredTypeNamespaceIndex();
        if (preferredTypeNamespaceIndex.TryGetValue(reference.Identifier, out var candidates))
            return candidates;

        if (reference.IsAttribute
            && !reference.Identifier.EndsWith("Attribute", StringComparison.Ordinal)
            && preferredTypeNamespaceIndex.TryGetValue(reference.Identifier + "Attribute", out candidates))
            return candidates;

        return [];
    }

    private bool TryGetCandidateNamespaces(MissingTypeReference reference, out HashSet<string> candidates)
    {
        if (reference.Arity > 0)
        {
            var typeNamespaceIndexByArity = GetTypeNamespaceIndexByArity();
            if (typeNamespaceIndexByArity.TryGetValue(BuildTypeLookupKey(reference.Identifier, reference.Arity), out candidates!))
                return true;

            if (reference.IsAttribute
                && typeNamespaceIndexByArity.TryGetValue(BuildTypeLookupKey(reference.Identifier + "Attribute", reference.Arity), out candidates!))
                return true;
        }

        var typeNamespaceIndex = GetTypeNamespaceIndex();
        if (typeNamespaceIndex.TryGetValue(reference.Identifier, out candidates!))
            return true;

        if (reference.IsAttribute
            && !reference.Identifier.EndsWith("Attribute", StringComparison.Ordinal)
            && typeNamespaceIndex.TryGetValue(reference.Identifier + "Attribute", out candidates!))
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
        _typeNamespaceIndexByArity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _preferredTypeNamespaceIndexByArity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var assemblyPath in assemblyPaths)
            AddAssemblyTypesToNamespaceIndex(
                result,
                _preferredTypeNamespaceIndex,
                _typeNamespaceIndexByArity,
                _preferredTypeNamespaceIndexByArity,
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

    private Dictionary<string, HashSet<string>> GetTypeNamespaceIndexByArity()
    {
        if (_typeNamespaceIndexByArity is not null)
            return _typeNamespaceIndexByArity;

        _ = GetTypeNamespaceIndex();
        return _typeNamespaceIndexByArity ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    }

    private Dictionary<string, HashSet<string>> GetPreferredTypeNamespaceIndexByArity()
    {
        if (_preferredTypeNamespaceIndexByArity is not null)
            return _preferredTypeNamespaceIndexByArity;

        _ = GetTypeNamespaceIndex();
        return _preferredTypeNamespaceIndexByArity ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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
        Dictionary<string, HashSet<string>> indexByArity,
        Dictionary<string, HashSet<string>> preferredIndexByArity,
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

                var rawName = reader.GetString(typeDefinition.Name);
                var name = NormalizeMetadataTypeName(rawName);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                AddNamespace(index, name, namespaceName);

                var arity = GetMetadataTypeArity(rawName);
                if (arity > 0)
                    AddNamespace(indexByArity, BuildTypeLookupKey(name, arity), namespaceName);

                if (!includeInPreferredIndex)
                    continue;

                AddNamespace(preferredIndex, name, namespaceName);

                if (arity > 0)
                    AddNamespace(preferredIndexByArity, BuildTypeLookupKey(name, arity), namespaceName);
            }
        }
        catch
        {
        }
    }

    private static void AddNamespace(Dictionary<string, HashSet<string>> index, string name, string namespaceName)
    {
        if (!index.TryGetValue(name, out var namespaces))
        {
            namespaces = new HashSet<string>(StringComparer.Ordinal);
            index[name] = namespaces;
        }

        namespaces.Add(namespaceName);
    }

    private static string NormalizeMetadataTypeName(string name)
    {
        var arityIndex = name.IndexOf('`');
        return arityIndex >= 0 ? name[..arityIndex] : name;
    }

    private static int GetMetadataTypeArity(string name)
    {
        var arityIndex = name.IndexOf('`');
        if (arityIndex < 0)
            return 0;

        return int.TryParse(name[(arityIndex + 1)..], out var arity) ? arity : 0;
    }

    private static string BuildTypeLookupKey(string name, int arity) => $"{name}`{arity}";

    private IReadOnlyList<MissingTypeReference> ExtractMissingTypeReferences(
        Microsoft.CodeAnalysis.SyntaxTree syntaxTree,
        RoslynSyntax.CompilationUnitSyntax root)
    {
        var compilation = RoslynCSharp.CSharpCompilation.Create(
            _assemblyName + ".Usings",
            [syntaxTree],
            GetFormattingReferences(),
            new RoslynCSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree, true);
        var references = new Dictionary<string, MissingTypeReference>(StringComparer.Ordinal);

        foreach (var typeSyntax in root.DescendantNodes().OfType<RoslynSyntax.TypeSyntax>())
            AddMissingTypeReferencesFromTypeSyntax(typeSyntax, semanticModel, references);

        foreach (var attributeSyntax in root.DescendantNodes().OfType<RoslynSyntax.AttributeSyntax>())
            AddMissingTypeReferencesFromNameSyntax(attributeSyntax.Name, semanticModel, references, true);

        foreach (var memberAccess in root.DescendantNodes().OfType<RoslynSyntax.MemberAccessExpressionSyntax>())
        {
            var leftMostExpression = GetLeftMostExpression(memberAccess.Expression);

            switch (leftMostExpression)
            {
                case RoslynSyntax.IdentifierNameSyntax identifierName
                    when StartsWithUppercase(identifierName.Identifier.ValueText):
                    AddMissingTypeReferenceIfUnresolved(
                        identifierName,
                        identifierName.Identifier.ValueText,
                        0,
                        semanticModel,
                        references,
                        false);
                    break;
                case RoslynSyntax.GenericNameSyntax genericName
                    when StartsWithUppercase(genericName.Identifier.ValueText):
                    AddMissingTypeReferenceIfUnresolved(
                        genericName,
                        genericName.Identifier.ValueText,
                        genericName.TypeArgumentList.Arguments.Count,
                        semanticModel,
                        references,
                        false);
                    foreach (var typeArgument in genericName.TypeArgumentList.Arguments.OfType<RoslynSyntax.TypeSyntax>())
                        AddMissingTypeReferencesFromTypeSyntax(typeArgument, semanticModel, references);
                    break;
            }
        }

        return references.Values.ToList();
    }

    private static void AddMissingTypeReferencesFromTypeSyntax(
        RoslynSyntax.TypeSyntax typeSyntax,
        SemanticModel semanticModel,
        IDictionary<string, MissingTypeReference> references)
    {
        if (IsResolvedType(semanticModel.GetTypeInfo(typeSyntax).Type))
            return;

        foreach (var node in typeSyntax.DescendantNodesAndSelf())
            switch (node)
            {
                case RoslynSyntax.IdentifierNameSyntax identifierName
                    when StartsWithUppercase(identifierName.Identifier.ValueText):
                    AddMissingTypeReferenceIfUnresolved(
                        identifierName,
                        identifierName.Identifier.ValueText,
                        0,
                        semanticModel,
                        references,
                        false);
                    break;
                case RoslynSyntax.GenericNameSyntax genericName
                    when StartsWithUppercase(genericName.Identifier.ValueText):
                    AddMissingTypeReferenceIfUnresolved(
                        genericName,
                        genericName.Identifier.ValueText,
                        genericName.TypeArgumentList.Arguments.Count,
                        semanticModel,
                        references,
                        false);
                    break;
            }
    }

    private static void AddMissingTypeReferencesFromNameSyntax(
        RoslynSyntax.NameSyntax nameSyntax,
        SemanticModel semanticModel,
        IDictionary<string, MissingTypeReference> references,
        bool isAttribute)
    {
        if (IsResolvedType(semanticModel.GetSymbolInfo(nameSyntax).Symbol as ITypeSymbol))
            return;

        switch (nameSyntax)
        {
            case RoslynSyntax.IdentifierNameSyntax identifierName
                when StartsWithUppercase(identifierName.Identifier.ValueText):
                AddMissingTypeReferenceIfUnresolved(
                    identifierName,
                    identifierName.Identifier.ValueText,
                    0,
                    semanticModel,
                    references,
                    isAttribute);
                break;
            case RoslynSyntax.GenericNameSyntax genericName
                when StartsWithUppercase(genericName.Identifier.ValueText):
                AddMissingTypeReferenceIfUnresolved(
                    genericName,
                    genericName.Identifier.ValueText,
                    genericName.TypeArgumentList.Arguments.Count,
                    semanticModel,
                    references,
                    isAttribute);
                foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
                    AddMissingTypeReferencesFromTypeSyntax(typeArgument, semanticModel, references);
                break;
            case RoslynSyntax.QualifiedNameSyntax qualifiedName:
                AddMissingTypeReferencesFromNameSyntax(qualifiedName.Left, semanticModel, references, isAttribute);
                AddMissingTypeReferencesFromNameSyntax(qualifiedName.Right, semanticModel, references, isAttribute);
                break;
            case RoslynSyntax.AliasQualifiedNameSyntax aliasQualifiedName:
                AddMissingTypeReferencesFromNameSyntax(aliasQualifiedName.Name, semanticModel, references, isAttribute);
                break;
        }
    }

    private static void AddMissingTypeReferenceIfUnresolved(
        RoslynCSharp.CSharpSyntaxNode node,
        string identifier,
        int arity,
        SemanticModel semanticModel,
        IDictionary<string, MissingTypeReference> references,
        bool isAttribute)
    {
        if (IsResolvedType(semanticModel.GetSymbolInfo(node).Symbol as ITypeSymbol)
            || IsResolvedType(semanticModel.GetTypeInfo(node).Type))
            return;

        var reference = new MissingTypeReference(identifier, arity, isAttribute);
        references[reference.Key] = reference;
    }

    private static bool IsResolvedType(ITypeSymbol? typeSymbol) =>
        typeSymbol is not null && typeSymbol.TypeKind != TypeKind.Error;

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

    private readonly record struct MissingTypeReference(string Identifier, int Arity, bool IsAttribute)
    {
        public string Key => string.Join("|", Identifier, Arity, IsAttribute);
    }
}
