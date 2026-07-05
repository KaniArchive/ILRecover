using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Helpers;
using ILRecover.Models;
using ILRecover.Pdb;
using ZLinq;

namespace ILRecover.Analysis;

public class AssemblyAnalyzer(string dllPath, string pdbPath, bool enablePdbMethodRemapping = false)
{
    private static readonly HashSet<string> StateMachineAttributeNames =
    [
        "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
        "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
        "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"
    ];

    private readonly string _assemblyName = Path.GetFileNameWithoutExtension(dllPath);

    public AnalysisResult Analyze()
    {
        var pdbSources = PdbReader.ReadSourceFiles(pdbPath);
        var commonSourceRoot = FindCommonSourceRoot(pdbSources.Select(source => source.OriginalPath));

        var file = new PEFile(dllPath);
        var mdReader = file.Metadata;

        var typeNames = BuildTypeNameLookup(mdReader);
        var methodDebugMap = enablePdbMethodRemapping
            ? PdbMethodMapper.Build(dllPath, pdbPath, typeNames)
            : PdbMethodDebugMap.Identity(PdbReader.ReadMethodDocumentPaths(dllPath, pdbPath));
        var methodLocalVariables = PdbReader.ReadMethodLocalVariables(dllPath, pdbPath, methodDebugMap);
        var docToMethods = BuildDocumentMethodMap(mdReader, typeNames, methodLocalVariables, methodDebugMap);
        if (docToMethods.Count == 0)
            return BuildFallbackAnalysis(mdReader, typeNames, pdbSources, commonSourceRoot);

        var sourceByNormalizedPath = pdbSources
            .AsValueEnumerable()
            .GroupBy(s => s.OriginalPath.NormalizePathKey())
            .ToDictionary(g => g.Key, g => g.First());
        RemoveNonPreferredNestedTypeMethods(docToMethods, sourceByNormalizedPath);

        var typeDocuments = BuildTypeDocumentMap(docToMethods);
        var typeDeclarationsByDocument =
            BuildTypeDeclarationDocumentMap(mdReader, pdbPath, typeNames, sourceByNormalizedPath, typeDocuments);

        var mapped = new List<SourceFileMap>();
        var skipped = new List<string>();

        foreach (var normalizedDoc in docToMethods.Keys.Concat(typeDeclarationsByDocument.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceByNormalizedPath.TryGetValue(normalizedDoc, out var source))
                continue;

            docToMethods.TryGetValue(normalizedDoc, out var methods);
            methods ??= [];

            var userMethods = methods
                .AsValueEnumerable()
                .Where(m => !TypeNameHelper.IsCompilerGenerated(m.TypeFullName))
                .ToList();

            typeDeclarationsByDocument.TryGetValue(normalizedDoc, out var typeDeclarations);
            typeDeclarations ??= [];

            if (userMethods.Count == 0 && typeDeclarations.Count == 0 && !source.IsGenerated)
            {
                skipped.Add(source.OriginalPath);
                continue;
            }

            var relative = ToRelativePath(source.OriginalPath, commonSourceRoot);
            var declaredTypeFullNames = methods
                .AsValueEnumerable()
                .Select(method => method.TypeFullName)
                .Concat(typeDeclarations.AsValueEnumerable().Select(typeDeclaration => typeDeclaration.TypeFullName))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            mapped.Add(new SourceFileMap(source.OriginalPath, relative, source.IsGenerated, userMethods,
                declaredTypeFullNames, typeDeclarations));
        }

        skipped.AddRange(pdbSources
            .AsValueEnumerable()
            .Where(source => !docToMethods.ContainsKey(source.OriginalPath.NormalizePathKey())
                             && !typeDeclarationsByDocument.ContainsKey(source.OriginalPath.NormalizePathKey()))
            .Select(source => source.OriginalPath)
            .ToList());

        return new AnalysisResult(
            mapped,
            skipped,
            pdbSources,
            GetUserTypeNames(typeNames.Values),
            skipped.Select(path => ToRelativePath(path, commonSourceRoot)).ToList());
    }

    private AnalysisResult BuildFallbackAnalysis(
        MetadataReader mdReader,
        IReadOnlyDictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyList<PdbSourceInfo> pdbSources,
        string? commonSourceRoot)
    {
        var mapped = new List<SourceFileMap>();
        var sourceByTypeName = BuildFallbackSourceLookup(pdbSources);

        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName)
                || TypeNameHelper.IsCompilerGenerated(typeName)
                || TypeNameHelper.IsNested(typeName)
                || IsEmbeddedInteropType(mdReader, typeHandle))
                continue;

            var declaredTypeFullNames = new[] { typeName };
            var source = ResolveFallbackSource(typeName, pdbSources, sourceByTypeName);
            var originalPath = source?.OriginalPath ?? $"{typeName}.cs";
            var relativePath = source is null
                ? $"{typeName}.cs"
                : ToRelativePath(source.OriginalPath, commonSourceRoot);
            mapped.Add(new SourceFileMap(originalPath, relativePath, source?.IsGenerated ?? false, [],
                declaredTypeFullNames,
                [], true));
        }

        var mappedPaths = mapped
            .AsValueEnumerable()
            .Select(map => map.OriginalPath.NormalizePathKey())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = pdbSources
            .AsValueEnumerable()
            .Where(source => !mappedPaths.Contains(source.OriginalPath.NormalizePathKey()))
            .Select(source => source.OriginalPath)
            .ToList();

        return new AnalysisResult(
            mapped,
            skipped,
            pdbSources,
            GetUserTypeNames(typeNames.Values),
            skipped.Select(path => ToRelativePath(path, commonSourceRoot)).ToList());
    }

    private static IReadOnlyList<string> GetUserTypeNames(IEnumerable<string> typeNames) =>
        typeNames
            .AsValueEnumerable()
            .Where(typeName => !TypeNameHelper.IsCompilerGenerated(typeName) && !TypeNameHelper.IsNested(typeName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyDictionary<string, List<PdbSourceInfo>> BuildFallbackSourceLookup(
        IReadOnlyList<PdbSourceInfo> pdbSources) =>
        pdbSources
            .AsValueEnumerable()
            .GroupBy(source => source.OriginalPath.GetFileStem(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(source => source.IsGenerated)
                    .ThenBy(source => source.OriginalPath.Contains(".Designer.", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(source => source.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static PdbSourceInfo? ResolveFallbackSource(
        string typeName,
        IReadOnlyList<PdbSourceInfo> pdbSources,
        IReadOnlyDictionary<string, List<PdbSourceInfo>> sourceByTypeName)
    {
        var simpleName = typeName[(typeName.LastIndexOf('.') + 1)..];
        if (sourceByTypeName.TryGetValue(simpleName, out var exactMatches))
            return exactMatches.FirstOrDefault();

        return pdbSources
            .AsValueEnumerable()
            .Where(source => !source.IsGenerated)
            .OrderBy(source => LevenshteinDistance(simpleName, source.OriginalPath.GetFileStem()))
            .ThenBy(source => source.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var i = 0; i <= right.Length; i++)
            previous[i] = i;

        for (var i = 0; i < left.Length; i++)
        {
            current[0] = i + 1;
            for (var j = 0; j < right.Length; j++)
            {
                var cost = char.ToUpperInvariant(left[i]) == char.ToUpperInvariant(right[j]) ? 0 : 1;
                current[j + 1] = Math.Min(
                    Math.Min(current[j] + 1, previous[j + 1] + 1),
                    previous[j] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void RemoveNonPreferredNestedTypeMethods(
        Dictionary<string, List<SourceFileMethodEntry>> methodsByDocument,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath)
    {
        var methodsByType = methodsByDocument
            .AsValueEnumerable()
            .SelectMany(pair => pair.Value
                .AsValueEnumerable()
                .Select(method => (Document: pair.Key, Method: method)))
            .GroupBy(entry => entry.Method.TypeFullName, StringComparer.Ordinal)
            .ToList();

        methodsByDocument.Clear();

        foreach (var group in methodsByType)
        {
            var entries = group.ToList();
            if (TypeNameHelper.IsNested(group.Key))
            {
                var documents = entries
                    .AsValueEnumerable()
                    .Select(entry => entry.Document)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var preferredDocuments = PreferNestedTypeDocument(
                        group.Key,
                        AddExpectedNestedTypeDocumentCandidates(group.Key, documents, sourceByNormalizedPath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (preferredDocuments.Count > 0)
                {
                    if (entries.Any(entry => preferredDocuments.Contains(entry.Document)))
                    {
                        entries = entries
                            .AsValueEnumerable()
                            .Where(entry => preferredDocuments.Contains(entry.Document))
                            .ToList();
                    }
                    else if (preferredDocuments.Count == 1)
                    {
                        var preferredDocument = preferredDocuments.Single();
                        foreach (var entry in entries)
                            AddMethod(methodsByDocument, preferredDocument, entry.Method);
                        continue;
                    }
                }
            }

            foreach (var entry in entries)
                AddMethod(methodsByDocument, entry.Document, entry.Method);
        }

        static void AddMethod(
            Dictionary<string, List<SourceFileMethodEntry>> methodsByDocument,
            string document,
            SourceFileMethodEntry method)
        {
            if (!methodsByDocument.TryGetValue(document, out var methods))
            {
                methods = [];
                methodsByDocument[document] = methods;
            }

            if (methods.All(existing => existing.MethodHandle != method.MethodHandle))
                methods.Add(method);
        }
    }

    private static Dictionary<TypeDefinitionHandle, string> BuildTypeNameLookup(MetadataReader reader)
    {
        var result = new Dictionary<TypeDefinitionHandle, string>();

        foreach (var handle in reader.TypeDefinitions)
            result[handle] = BuildFullName(reader, handle);

        return result;
    }

    private static string BuildFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var typeDef = reader.GetTypeDefinition(handle);
        var name = reader.GetString(typeDef.Name);

        var declaringHandle = typeDef.GetDeclaringType();
        if (!declaringHandle.IsNil) return BuildFullName(reader, declaringHandle) + "+" + name;

        var ns = reader.GetString(typeDef.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static Dictionary<string, List<SourceFileMethodEntry>> BuildDocumentMethodMap(
        MetadataReader mdReader,
        Dictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyDictionary<int, IReadOnlyList<LocalVariableDebugInfo>> methodLocalVariables,
        PdbMethodDebugMap methodDebugMap)
    {
        var result = new Dictionary<string, List<SourceFileMethodEntry>>(StringComparer.OrdinalIgnoreCase);
        var generatedMethodOwners = BuildGeneratedMethodOwnerMap(mdReader, typeNames, methodLocalVariables);

        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName))
                continue;

            var typeDef = mdReader.GetTypeDefinition(typeHandle);

            foreach (var methodHandle in typeDef.GetMethods())
            {
                var rowNumber = MetadataTokens.GetRowNumber(methodHandle);
                if (!methodDebugMap.DocumentPathsByMethodRow.TryGetValue(rowNumber, out var docPath))
                    continue;

                if (string.IsNullOrWhiteSpace(docPath)) continue;

                var normalized = docPath.NormalizePathKey();
                var entry = MapMethodToSourceEntry(typeName, methodHandle, generatedMethodOwners,
                    GetMethodLocalVariables(methodHandle, methodLocalVariables));
                if (entry is null) continue;

                if (!result.TryGetValue(normalized, out var list))
                {
                    list = [];
                    result[normalized] = list;
                }

                list.Add(entry);
            }
        }

        return result;
    }


    private static Dictionary<string, List<SourceFileTypeDeclarationEntry>> BuildTypeDeclarationDocumentMap(
        MetadataReader mdReader,
        string pdbPath,
        IReadOnlyDictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath,
        IReadOnlyDictionary<string, HashSet<string>> typeDocuments)
    {
        var result = new Dictionary<string, List<SourceFileTypeDeclarationEntry>>(StringComparer.OrdinalIgnoreCase);
        var typeDefinitionDocuments = PdbReader.ReadTypeDefinitionDocumentPaths(pdbPath);

        foreach (var pair in typeDefinitionDocuments)
        {
            var typeHandle = MetadataTokens.TypeDefinitionHandle(pair.Key);
            if (!typeNames.TryGetValue(typeHandle, out var typeName)
                || TypeNameHelper.IsCompilerGenerated(typeName))
                continue;

            var ownerDocuments = pair.Value
                .Where(sourceByNormalizedPath.ContainsKey)
                .OrderBy(document => sourceByNormalizedPath[document].IsGenerated)
                .ThenBy(document => document, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (TypeNameHelper.IsNested(typeName) && typeDocuments.TryGetValue(typeName, out var exactTypeDocuments))
                ownerDocuments = ownerDocuments
                    .Where(exactTypeDocuments.Contains)
                    .ToList();

            if (TypeNameHelper.IsNested(typeName))
                ownerDocuments = PreferNestedTypeDocument(typeName,
                    AddExpectedNestedTypeDocumentCandidates(typeName, ownerDocuments, sourceByNormalizedPath));

            if (ownerDocuments.Count == 0)
                continue;

            foreach (var ownerDocument in ownerDocuments)
            {
                if (!result.TryGetValue(ownerDocument, out var declarations))
                {
                    declarations = [];
                    result[ownerDocument] = declarations;
                }

                declarations.Add(new SourceFileTypeDeclarationEntry(typeName, typeHandle));
            }
        }

        RemoveNonPreferredNestedTypeDeclarations(result, sourceByNormalizedPath);
        AddNestedTypeDeclarations(mdReader, typeNames, result, typeDocuments, sourceByNormalizedPath);
        RemoveNonPreferredNestedTypeDeclarations(result, sourceByNormalizedPath);
        return result;
    }

    private static void RemoveNonPreferredNestedTypeDeclarations(
        Dictionary<string, List<SourceFileTypeDeclarationEntry>> declarationsByDocument,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath)
    {
        var declarationsByType = declarationsByDocument
            .AsValueEnumerable()
            .SelectMany(pair => pair.Value
                .AsValueEnumerable()
                .Select(declaration => (Document: pair.Key, Declaration: declaration)))
            .GroupBy(entry => entry.Declaration.TypeFullName, StringComparer.Ordinal)
            .ToList();

        declarationsByDocument.Clear();

        foreach (var group in declarationsByType)
        {
            var entries = group.ToList();
            if (TypeNameHelper.IsNested(group.Key))
            {
                var documents = entries
                    .AsValueEnumerable()
                    .Select(entry => entry.Document)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var preferredDocuments = PreferNestedTypeDocument(
                        group.Key,
                        AddExpectedNestedTypeDocumentCandidates(group.Key, documents, sourceByNormalizedPath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (preferredDocuments.Count > 0)
                {
                    if (entries.Any(entry => preferredDocuments.Contains(entry.Document)))
                    {
                        entries = entries
                            .AsValueEnumerable()
                            .Where(entry => preferredDocuments.Contains(entry.Document))
                            .ToList();
                    }
                    else if (preferredDocuments.Count == 1)
                    {
                        var preferredDocument = preferredDocuments.Single();
                        foreach (var entry in entries)
                            AddDeclaration(declarationsByDocument, preferredDocument, entry.Declaration);
                        continue;
                    }
                }
            }

            foreach (var entry in entries)
                AddDeclaration(declarationsByDocument, entry.Document, entry.Declaration);
        }

        static void AddDeclaration(
            Dictionary<string, List<SourceFileTypeDeclarationEntry>> declarationsByDocument,
            string document,
            SourceFileTypeDeclarationEntry declaration)
        {
            if (!declarationsByDocument.TryGetValue(document, out var declarations))
            {
                declarations = [];
                declarationsByDocument[document] = declarations;
            }

            if (declarations.All(existing => existing.TypeHandle != declaration.TypeHandle))
                declarations.Add(declaration);
        }
    }

    private static List<string> PreferNestedTypeDocument(string typeName, List<string> documents)
    {
        var expectedFileName = GetNestedTypeDocumentFileName(typeName);
        if (expectedFileName is not null)
        {
            var preferredDocuments = documents
                .AsValueEnumerable()
                .Where(document => string.Equals(GetDocumentFileName(document), expectedFileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (preferredDocuments.Count > 0)
                return preferredDocuments;
        }

        var suffixMatchedDocuments = documents
            .AsValueEnumerable()
            .Where(document => NestedTypeNameMatchesPartialSuffix(typeName, document))
            .ToList();

        if (suffixMatchedDocuments.Count > 0)
            return suffixMatchedDocuments;

        var declaringTypeFileName = GetDeclaringTypeDocumentFileName(typeName);
        if (declaringTypeFileName is not null)
        {
            var declaringTypeDocuments = documents
                .AsValueEnumerable()
                .Where(document => string.Equals(GetDocumentFileName(document), declaringTypeFileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (declaringTypeDocuments.Count > 0)
                return declaringTypeDocuments;
        }

        return documents
            .AsValueEnumerable()
            .OrderBy(document => document, StringComparer.OrdinalIgnoreCase)
            .Take(1)
            .ToList();
    }

    private static List<string> AddExpectedNestedTypeDocumentCandidates(
        string typeName,
        List<string> documents,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath)
    {
        if (documents.Count == 0)
            return documents;

        var expectedFileNames = new[]
                { GetNestedTypeDocumentFileName(typeName), GetDeclaringTypeDocumentFileName(typeName) }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (expectedFileNames.Count == 0)
            return documents;

        var result = documents.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var directory = GetDocumentDirectory(document);
            foreach (var expectedFileName in expectedFileNames)
            {
                var candidate = string.IsNullOrEmpty(directory)
                    ? expectedFileName
                    : directory + "/" + expectedFileName;
                candidate = candidate.NormalizePathKey();
                if (sourceByNormalizedPath.ContainsKey(candidate))
                    result.Add(candidate);
            }
        }

        return result
            .AsValueEnumerable()
            .OrderBy(document => sourceByNormalizedPath.TryGetValue(document, out var source) && source.IsGenerated)
            .ThenBy(document => document, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDocumentDirectory(string document)
    {
        var normalized = document.NormalizePath();
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 ? normalized[..separatorIndex] : string.Empty;
    }

    private static string? GetDeclaringTypeDocumentFileName(string typeName)
    {
        var nestedParts = typeName.Split('+');
        if (nestedParts.Length < 2)
            return null;

        var rootTypeName = nestedParts[0];
        var rootSimpleName = rootTypeName[(rootTypeName.LastIndexOf('.') + 1)..];
        if (string.IsNullOrWhiteSpace(rootSimpleName))
            return null;

        var declaringTypeNameParts = nestedParts
            .AsValueEnumerable()
            .Skip(1)
            .Take(nestedParts.Length - 2)
            .Select(RemoveGenericArity)
            .Prepend(RemoveGenericArity(rootSimpleName))
            .ToList();

        return string.Join('.', declaringTypeNameParts) + ".cs";
    }

    private static bool NestedTypeNameMatchesPartialSuffix(string typeName, string document)
    {
        var nestedIndex = typeName.LastIndexOf('+');
        if (nestedIndex < 0 || nestedIndex == typeName.Length - 1)
            return false;

        var nestedSimpleName = RemoveGenericArity(typeName[(nestedIndex + 1)..]);
        var declaringTypeFileName = GetDeclaringTypeDocumentFileName(typeName);
        if (declaringTypeFileName is null)
            return false;

        var fileName = GetDocumentFileName(document);
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, declaringTypeFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        var declaringBaseName = declaringTypeFileName[..^3];
        if (!fileName.StartsWith(declaringBaseName + ".", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = fileName[(declaringBaseName.Length + 1)..^3];
        return suffix
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => NestedTypeNameEndsWithPartialName(nestedSimpleName, part));
    }

    private static bool NestedTypeNameEndsWithPartialName(string nestedSimpleName, string partialName)
    {
        if (string.IsNullOrWhiteSpace(partialName))
            return false;

        return nestedSimpleName.EndsWith(partialName, StringComparison.OrdinalIgnoreCase)
               || (partialName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                   && nestedSimpleName.EndsWith(partialName[..^1], StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDocumentFileName(string document)
    {
        var normalized = document.NormalizePath();
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static string? GetNestedTypeDocumentFileName(string typeName)
    {
        var nestedParts = typeName.Split('+');
        if (nestedParts.Length < 2)
            return null;

        var rootTypeName = nestedParts[0];
        var rootSimpleName = rootTypeName[(rootTypeName.LastIndexOf('.') + 1)..];
        if (string.IsNullOrWhiteSpace(rootSimpleName))
            return null;

        var typeNameParts = nestedParts
            .AsValueEnumerable()
            .Skip(1)
            .Select(RemoveGenericArity)
            .Prepend(RemoveGenericArity(rootSimpleName))
            .ToList();

        return string.Join('.', typeNameParts) + ".cs";
    }

    private static string RemoveGenericArity(string name)
    {
        var arityIndex = name.IndexOf('`');
        return arityIndex >= 0 ? name[..arityIndex] : name;
    }

    private static void AddNestedTypeDeclarations(
        MetadataReader reader,
        IReadOnlyDictionary<TypeDefinitionHandle, string> typeNames,
        Dictionary<string, List<SourceFileTypeDeclarationEntry>> declarationsByDocument,
        IReadOnlyDictionary<string, HashSet<string>> typeDocuments,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath)
    {
        var mappedHandles = declarationsByDocument.Values
            .AsValueEnumerable()
            .SelectMany(declarations => declarations)
            .Select(declaration => declaration.TypeHandle)
            .ToHashSet();

        var declarationDocumentsByType = declarationsByDocument
            .AsValueEnumerable()
            .SelectMany(pair =>
                pair.Value.AsValueEnumerable().Select(declaration => (pair.Key, declaration.TypeFullName)))
            .GroupBy(pair => pair.TypeFullName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.AsValueEnumerable().Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);

        foreach (var pair in typeNames)
        {
            var typeHandle = pair.Key;
            var typeName = pair.Value;
            if (!TypeNameHelper.IsNested(typeName)
                || TypeNameHelper.IsCompilerGenerated(typeName)
                || mappedHandles.Contains(typeHandle))
                continue;

            var declaringTypeHandle = reader.GetTypeDefinition(typeHandle).GetDeclaringType();
            if (declaringTypeHandle.IsNil || !typeNames.TryGetValue(declaringTypeHandle, out var declaringTypeName))
                continue;

            var ownerDocuments = ResolveOwnerDocuments(typeName, declaringTypeName, typeDocuments,
                declarationDocumentsByType, sourceByNormalizedPath);
            foreach (var ownerDocument in ownerDocuments)
            {
                if (!declarationsByDocument.TryGetValue(ownerDocument, out var declarations))
                {
                    declarations = [];
                    declarationsByDocument[ownerDocument] = declarations;
                }

                declarations.Add(new SourceFileTypeDeclarationEntry(typeName, typeHandle));
            }
        }
    }

    private static List<string> ResolveOwnerDocuments(
        string typeName,
        string declaringTypeName,
        IReadOnlyDictionary<string, HashSet<string>> typeDocuments,
        IReadOnlyDictionary<string, HashSet<string>> declarationDocumentsByType,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath)
    {
        var documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (typeDocuments.TryGetValue(typeName, out var ownMethodDocuments))
            documents.UnionWith(ownMethodDocuments);

        if (declarationDocumentsByType.TryGetValue(typeName, out var ownDeclarationDocuments))
            documents.UnionWith(ownDeclarationDocuments);

        if (documents.Count > 0)
            return PreferNestedTypeDocument(typeName, OrderDocuments(documents, sourceByNormalizedPath));

        if (typeDocuments.TryGetValue(declaringTypeName, out var methodDocuments))
            documents.UnionWith(methodDocuments);

        if (declarationDocumentsByType.TryGetValue(declaringTypeName, out var declarationDocuments))
            documents.UnionWith(declarationDocuments);

        return documents.Count > 0
            ? PreferNestedTypeDocument(typeName,
                AddExpectedNestedTypeDocumentCandidates(typeName, OrderDocuments(documents, sourceByNormalizedPath),
                    sourceByNormalizedPath))
            : [];
    }

    private static List<string> OrderDocuments(
        IEnumerable<string> documents,
        IReadOnlyDictionary<string, PdbSourceInfo> sourceByNormalizedPath) =>
        documents
            .AsValueEnumerable()
            .Where(sourceByNormalizedPath.ContainsKey)
            .OrderBy(document => sourceByNormalizedPath[document].IsGenerated)
            .ThenBy(document => document, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, HashSet<string>> BuildTypeDocumentMap(
        IReadOnlyDictionary<string, List<SourceFileMethodEntry>> docToMethods)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var pair in docToMethods)
        foreach (var method in pair.Value)
        {
            if (!result.TryGetValue(method.TypeFullName, out var documents))
            {
                documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[method.TypeFullName] = documents;
            }

            documents.Add(pair.Key);
        }

        return result;
    }


    private static Dictionary<string, SourceFileMethodEntry> BuildGeneratedMethodOwnerMap(
        MetadataReader reader,
        Dictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyDictionary<int, IReadOnlyList<LocalVariableDebugInfo>> methodLocalVariables)
    {
        var result = new Dictionary<string, SourceFileMethodEntry>(StringComparer.Ordinal);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName)
                || TypeNameHelper.IsCompilerGenerated(typeName))
                continue;

            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
                ProcessMethod(methodHandle, typeName);
        }

        return result;

        void ProcessMethod(MethodDefinitionHandle methodHandle, string typeName)
        {
            foreach (var attrHandle in reader.GetMethodDefinition(methodHandle).GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                var attrTypeName = GetCustomAttributeTypeFullName(reader, attr);

                if (attrTypeName == null || !StateMachineAttributeNames.Contains(attrTypeName))
                    continue;

                var generatedTypeName = TryReadStateMachineTypeName(reader.GetBlobReader(attr.Value));

                if (string.IsNullOrWhiteSpace(generatedTypeName))
                    continue;

                result[generatedTypeName] = new SourceFileMethodEntry(
                    typeName,
                    methodHandle,
                    GetMethodLocalVariables(methodHandle, methodLocalVariables)
                );
            }
        }
    }

    private static SourceFileMethodEntry? MapMethodToSourceEntry(
        string typeName,
        MethodDefinitionHandle methodHandle,
        IReadOnlyDictionary<string, SourceFileMethodEntry> generatedMethodOwners,
        IReadOnlyList<LocalVariableDebugInfo> localVariables)
    {
        if (!TypeNameHelper.IsCompilerGenerated(typeName))
            return new SourceFileMethodEntry(typeName, methodHandle, localVariables);

        if (generatedMethodOwners.GetValueOrDefault(typeName) is not { } owner)
            return null;

        return localVariables.Count > owner.LocalVariables.Count
            ? owner with { LocalVariables = localVariables }
            : owner;
    }

    private static IReadOnlyList<LocalVariableDebugInfo> GetMethodLocalVariables(
        MethodDefinitionHandle methodHandle,
        IReadOnlyDictionary<int, IReadOnlyList<LocalVariableDebugInfo>> methodLocalVariables) =>
        methodLocalVariables.GetValueOrDefault(MetadataTokens.GetRowNumber(methodHandle)) ?? [];

    private static string? GetCustomAttributeTypeFullName(MetadataReader reader, CustomAttribute attribute) =>
        attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition => GetTypeFullName(reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            HandleKind.MemberReference => GetTypeFullName(reader,
                reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            _ => null
        };

    private static bool IsEmbeddedInteropType(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        var typeDefinition = reader.GetTypeDefinition(typeHandle);
        return typeDefinition.Attributes.HasFlag(TypeAttributes.Import)
               || typeDefinition.GetCustomAttributes()
                   .AsValueEnumerable()
                   .Select(handle => GetCustomAttributeTypeFullName(reader, reader.GetCustomAttribute(handle)))
                   .Any(attributeName => attributeName is
                       "System.Runtime.InteropServices.TypeIdentifierAttribute" or
                       "System.Runtime.InteropServices.ComImportAttribute" or
                       "System.Runtime.InteropServices.ImportedFromTypeLibAttribute" or
                       "System.Runtime.InteropServices.PrimaryInteropAssemblyAttribute");
    }

    private static string? GetTypeFullName(MetadataReader reader, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeDefinition => BuildFullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => BuildTypeReferenceFullName(reader, (TypeReferenceHandle)handle),
            _ => null
        };

    private static string BuildTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var typeRef = reader.GetTypeReference(handle);
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string? TryReadStateMachineTypeName(BlobReader reader)
    {
        if (reader.RemainingBytes < 2)
            return null;

        var prolog = reader.ReadUInt16();
        if (prolog != 1)
            return null;

        var serializedTypeName = reader.ReadSerializedString();
        if (string.IsNullOrWhiteSpace(serializedTypeName))
            return null;

        var assemblySeparator = serializedTypeName.IndexOf(',');
        var typeName = assemblySeparator >= 0
            ? serializedTypeName[..assemblySeparator]
            : serializedTypeName;

        return typeName.Replace('/', '+');
    }

    private string ToRelativePath(string originalPath, string? commonSourceRoot)
    {
        var normalized = originalPath.NormalizePath();

        if (!string.IsNullOrWhiteSpace(commonSourceRoot)
            && normalized.StartsWith(commonSourceRoot, StringComparison.OrdinalIgnoreCase))
            return normalized[commonSourceRoot.Length..]
                .TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar);

        var doubledProjectRoot = $"{_assemblyName}/{_assemblyName}/";
        var doubledProjectIdx = normalized.IndexOf(doubledProjectRoot, StringComparison.OrdinalIgnoreCase);
        if (doubledProjectIdx >= 0)
            return normalized[(doubledProjectIdx + doubledProjectRoot.Length)..]
                .Replace('/', Path.DirectorySeparatorChar);

        var singleProjectRoot = $"{_assemblyName}/";
        var singleProjectIdx = normalized.IndexOf(singleProjectRoot, StringComparison.OrdinalIgnoreCase);
        if (singleProjectIdx >= 0)
            return normalized[(singleProjectIdx + singleProjectRoot.Length)..]
                .Replace('/', Path.DirectorySeparatorChar);

        return Path.GetFileName(originalPath);
    }

    private static string? FindCommonSourceRoot(IEnumerable<string> paths)
    {
        var directoryPaths = paths
            .AsValueEnumerable()
            .Select(GetNormalizedDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (directoryPaths.Count == 0)
            return null;

        var rootPrefix = GetRootPrefix(directoryPaths[0]);
        if (directoryPaths.Any(path => !string.Equals(GetRootPrefix(path), rootPrefix, StringComparison.Ordinal)))
            return null;

        var directorySegments = directoryPaths
            .AsValueEnumerable()
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var commonLength = directorySegments[0].Length;

        foreach (var segments in directorySegments.Skip(1))
        {
            var max = Math.Min(commonLength, segments.Length);
            var i = 0;
            while (i < max && string.Equals(directorySegments[0][i], segments[i], StringComparison.OrdinalIgnoreCase))
                i++;

            commonLength = i;
            if (commonLength == 0)
                return null;
        }

        return rootPrefix + string.Join('/', directorySegments[0][..commonLength]) + "/";
    }

    private static string GetNormalizedDirectoryPath(string path)
    {
        var normalized = path.NormalizePath();
        var fileNameIdx = normalized.LastIndexOf('/');
        return fileNameIdx < 0 ? string.Empty : normalized[..fileNameIdx];
    }

    private static string GetRootPrefix(string path)
    {
        if (path.StartsWith("//", StringComparison.Ordinal))
            return "//";

        return path.StartsWith("/", StringComparison.Ordinal) ? "/" : string.Empty;
    }

}
