using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Models;
using ILRecover.Pdb;
using ZLinq;

namespace ILRecover.Analysis;

public class AssemblyAnalyzer(string dllPath, string pdbPath)
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
        var methodLocalVariables = PdbReader.ReadMethodLocalVariables(pdbPath);
        var commonSourceRoot = FindCommonSourceRoot(pdbSources.Select(source => source.OriginalPath));

        var file = new PEFile(dllPath);
        var mdReader = file.Metadata;

        var typeNames = BuildTypeNameLookup(mdReader);
        var docToMethods = BuildDocumentMethodMap(mdReader, pdbPath, typeNames, methodLocalVariables);

        var sourceByNormalizedPath = pdbSources
            .AsValueEnumerable()
            .GroupBy(s => NormalizePath(s.OriginalPath))
            .ToDictionary(g => g.Key, g => g.First());

        var mapped = new List<SourceFileMap>();
        var skipped = new List<string>();

        foreach (var (normalizedDoc, methods) in docToMethods)
        {
            if (!sourceByNormalizedPath.TryGetValue(normalizedDoc, out var source))
                continue;

            var userMethods = methods
                .AsValueEnumerable()
                .Where(m => !IsCompilerGenerated(m.TypeFullName))
                .ToList();

            if (userMethods.Count == 0 && !source.IsGenerated)
            {
                skipped.Add(source.OriginalPath);
                continue;
            }

            var relative = ToRelativePath(source.OriginalPath, commonSourceRoot);
            var declaredTypeFullNames = methods
                .AsValueEnumerable()
                .Select(method => method.TypeFullName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            mapped.Add(new SourceFileMap(source.OriginalPath, relative, source.IsGenerated, userMethods, declaredTypeFullNames));
        }

        skipped.AddRange(pdbSources
            .AsValueEnumerable()
            .Where(source => !docToMethods.ContainsKey(NormalizePath(source.OriginalPath)))
            .Select(source => source.OriginalPath)
            .ToList());

        return new AnalysisResult(mapped, skipped, pdbSources);
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
        string pdbPath,
        Dictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyDictionary<int, IReadOnlyList<LocalVariableDebugInfo>> methodLocalVariables)
    {
        var result = new Dictionary<string, List<SourceFileMethodEntry>>(StringComparer.OrdinalIgnoreCase);
        var generatedMethodOwners = BuildGeneratedMethodOwnerMap(mdReader, typeNames, methodLocalVariables);

        using var pdbFs = File.OpenRead(pdbPath);
        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbFs);
        var pdbReader = pdbProvider.GetMetadataReader();

        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName))
                continue;

            var typeDef = mdReader.GetTypeDefinition(typeHandle);

            foreach (var methodHandle in typeDef.GetMethods())
            {
                var rowNumber = MetadataTokens.GetRowNumber(methodHandle);
                var debugHandle = MetadataTokens.MethodDebugInformationHandle(rowNumber);

                MethodDebugInformation debugInfo;
                try
                {
                    debugInfo = pdbReader.GetMethodDebugInformation(debugHandle);
                }
                catch
                {
                    continue;
                }

                if (debugInfo.Document.IsNil) continue;

                var doc = pdbReader.GetDocument(debugInfo.Document);
                var docPath = pdbReader.GetString(doc.Name);
                if (string.IsNullOrWhiteSpace(docPath)) continue;

                var normalized = NormalizePath(docPath);
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


    private static Dictionary<string, SourceFileMethodEntry> BuildGeneratedMethodOwnerMap(
        MetadataReader reader,
        Dictionary<TypeDefinitionHandle, string> typeNames,
        IReadOnlyDictionary<int, IReadOnlyList<LocalVariableDebugInfo>> methodLocalVariables)
    {
        var result = new Dictionary<string, SourceFileMethodEntry>(StringComparer.Ordinal);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName) || IsCompilerGenerated(typeName))
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
        if (!IsCompilerGenerated(typeName))
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
        var normalized = originalPath.Replace('\\', '/');

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
        var directorySegments = paths
            .AsValueEnumerable()
            .Select(path => path.Replace('\\', '/'))
            .Select(path => Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        if (directorySegments.Count == 0)
            return null;

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

        return string.Join('/', directorySegments[0][..commonLength]) + "/";
    }

    private static bool IsCompilerGenerated(string typeName) => typeName.StartsWith('<') || typeName.Contains("+<");

    private static string NormalizePath(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
