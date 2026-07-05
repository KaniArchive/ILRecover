using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.ILSpyX.PdbProvider;
using ILRecover.Analysis.SourceGen;
using ILRecover.Helpers;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.Pdb;

public static class PdbReader
{
    private static readonly Guid TypeDefinitionDocumentKind = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");

    public static PdbFormat DetectFormat(string pdbPath)
    {
        using var fs = File.OpenRead(pdbPath);
        Span<byte> header = stackalloc byte[4];
        if (fs.Read(header) != header.Length)
            return PdbFormat.Portable;

        return header[0] == 0x4D && header[1] == 0x69 && header[2] == 0x63 && header[3] == 0x72
            ? PdbFormat.Windows
            : PdbFormat.Portable;
    }

    public static List<PdbSourceInfo> ReadSourceFiles(string pdbPath)
    {
        if (DetectFormat(pdbPath) == PdbFormat.Windows)
            return ReadWindowsSourceFiles(pdbPath);

        using var fs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        var reader = provider.GetMetadataReader();

        return
        [
            .. reader.Documents
                .AsValueEnumerable()
                .Select(reader.GetDocument)
                .Select(document => reader.GetString(document.Name))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new PdbSourceInfo(path, IsGenerated(path)))
        ];
    }

    private static List<PdbSourceInfo> ReadWindowsSourceFiles(string pdbPath)
    {
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(pdbPath));
        return Regex.Matches(text, @"[A-Za-z]:\\[^\x00\r\n\t<>|""?*]+?\.cs")
            .AsValueEnumerable()
            .Select(match => match.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(path => path.NormalizePathKey(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new PdbSourceInfo(path, IsGenerated(path)))
            .ToList();
    }

    public static Dictionary<int, IReadOnlyList<LocalVariableDebugInfo>> ReadMethodLocalVariables(
        string assemblyPath,
        string pdbPath,
        PdbMethodDebugMap? methodDebugMap = null)
    {
        using var file = new PEFile(assemblyPath);
        var debugInfo = DebugInfoUtils.FromFile(file, pdbPath);
        if (debugInfo is null)
            return [];

        try
        {
            var result = new Dictionary<int, IReadOnlyList<LocalVariableDebugInfo>>();
            foreach (var methodHandle in file.Metadata.MethodDefinitions)
            {
                var methodRow = MetadataTokens.GetRowNumber(methodHandle);
                var pdbMethodRow = methodDebugMap?.GetPdbRow(methodRow) ?? methodRow;
                var pdbMethodHandle = MetadataTokens.MethodDefinitionHandle(pdbMethodRow);
                var variables = debugInfo.GetVariables(pdbMethodHandle)
                    .AsValueEnumerable()
                    .Where(variable => !string.IsNullOrWhiteSpace(variable.Name) && !IsGeneratedLocal(variable.Name))
                    .Select(variable => new LocalVariableDebugInfo(variable.Index, variable.Name, 0, 0))
                    .ToList();

                if (variables.Count > 0)
                    result[methodRow] = variables;
            }

            return result;
        }
        finally
        {
            if (debugInfo is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public static Dictionary<int, string> ReadMethodDocumentPaths(string assemblyPath, string pdbPath)
    {
        using var file = new PEFile(assemblyPath);
        var debugInfo = DebugInfoUtils.FromFile(file, pdbPath);
        if (debugInfo is null)
            return [];

        try
        {
            var result = new Dictionary<int, string>();
            foreach (var methodHandle in file.Metadata.MethodDefinitions)
            {
                var documentPath = debugInfo.GetSequencePoints(methodHandle)
                    .AsValueEnumerable()
                    .Where(point => !string.IsNullOrWhiteSpace(point.DocumentUrl) && point.StartLine != 0xFEEFEE)
                    .Select(point => point.DocumentUrl)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(documentPath))
                    result[MetadataTokens.GetRowNumber(methodHandle)] = documentPath;
            }

            return result;
        }
        finally
        {
            if (debugInfo is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public static Dictionary<int, List<string>> ReadTypeDefinitionDocumentPaths(string pdbPath)
    {
        if (DetectFormat(pdbPath) == PdbFormat.Windows)
            return [];

        using var fs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        var reader = provider.GetMetadataReader();
        var result = new Dictionary<int, List<string>>();

        foreach (var customDebugHandle in reader.CustomDebugInformation)
        {
            var customDebugInfo = reader.GetCustomDebugInformation(customDebugHandle);
            if (customDebugInfo.Parent.Kind != HandleKind.TypeDefinition || customDebugInfo.Kind.IsNil ||
                customDebugInfo.Value.IsNil)
                continue;

            if (reader.GetGuid(customDebugInfo.Kind) != TypeDefinitionDocumentKind)
                continue;

            result[MetadataTokens.GetRowNumber((TypeDefinitionHandle)customDebugInfo.Parent)] =
                ReadTypeDefinitionDocuments(reader, customDebugInfo.Value);
        }

        return result;
    }

    private static List<string> ReadTypeDefinitionDocuments(MetadataReader reader, BlobHandle value)
    {
        var documents = new List<string>();
        var blobReader = reader.GetBlobReader(value);
        while (blobReader.RemainingBytes > 0)
        {
            var documentRowId = blobReader.ReadCompressedInteger();
            if (documentRowId <= 0)
                continue;

            var documentHandle = MetadataTokens.DocumentHandle(documentRowId);
            if (documentHandle.IsNil)
                continue;

            var documentPath = reader.GetString(reader.GetDocument(documentHandle).Name);
            if (!string.IsNullOrWhiteSpace(documentPath))
                documents.Add(documentPath.NormalizePathKey());
        }

        return documents;
    }

    private static bool IsGenerated(string path)
    {
        var p = path.NormalizePath();
        return p.Contains("/obj/")
               || p.EndsWith(".g.cs")
               || p.EndsWith(".Generated.cs")
               || SourceGenPaths.IsKnownGeneratorPath(p)
               || p.EndsWith(".Designer.cs");
    }

    private static bool IsGeneratedLocal(string name) =>
        name.StartsWith("CS$", StringComparison.Ordinal)
        || name.StartsWith("<", StringComparison.Ordinal)
        || name.Contains("__", StringComparison.Ordinal);
}
