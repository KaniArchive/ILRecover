using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.Pdb;

public static class PdbReader
{
    public static List<PdbSourceInfo> ReadSourceFiles(string pdbPath)
    {
        using var fs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        var reader = provider.GetMetadataReader();

        return
        [
            .. reader.Documents
                .AsValueEnumerable()
                .Select(reader.GetDocument)
                .Select(d => reader.GetString(d.Name))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new PdbSourceInfo(p, IsGenerated(p)))
        ];
    }

    public static Dictionary<int, IReadOnlyList<LocalVariableDebugInfo>> ReadMethodLocalVariables(string pdbPath)
    {
        using var fs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        var reader = provider.GetMetadataReader();

        var result = new Dictionary<int, List<LocalVariableDebugInfo>>();

        foreach (var scopeHandle in reader.LocalScopes)
        {
            var scope = reader.GetLocalScope(scopeHandle);
            var methodRow = MetadataTokens.GetRowNumber(scope.Method);

            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = reader.GetLocalVariable(localHandle);
                var name = reader.GetString(local.Name);
                if (string.IsNullOrWhiteSpace(name) || IsGeneratedLocal(name))
                    continue;

                if (!result.TryGetValue(methodRow, out var list))
                {
                    list = [];
                    result[methodRow] = list;
                }

                list.Add(new LocalVariableDebugInfo(local.Index, name, scope.StartOffset, scope.Length));
            }
        }

        return result.ToDictionary(
            pair => pair.Key, IReadOnlyList<LocalVariableDebugInfo> (pair) => pair.Value
                .AsValueEnumerable()
                .OrderBy(local => local.StartOffset)
                .ThenBy(local => local.SlotIndex)
                .ToList());
    }

    private static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/")
               || p.EndsWith(".g.cs")
               || p.EndsWith(".Generated.cs")
               || p.Contains("MemoryPack.Generator")
               || p.Contains("MessagePack.Generator")
               || p.EndsWith(".Designer.cs");
    }

    private static bool IsGeneratedLocal(string name) =>
        name.StartsWith("CS$", StringComparison.Ordinal)
        || name.StartsWith("<", StringComparison.Ordinal)
        || name.Contains("__", StringComparison.Ordinal);
}