using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using ZLinq;

namespace ILRecover.Pdb;

public static class PdbMethodMapper
{
    private const int ClusterGap = 16;
    private const int MaxShift = 40000;
    private const int CandidateWindow = 8;

    public static PdbMethodDebugMap Build(string assemblyPath, string pdbPath)
    {
        using var file = new PEFile(assemblyPath);
        return Build(assemblyPath, pdbPath, BuildTypeNameLookup(file.Metadata));
    }

    public static PdbMethodDebugMap Build(
        string assemblyPath,
        string pdbPath,
        IReadOnlyDictionary<TypeDefinitionHandle, string> typeNames)
    {
        var documentPathsByPdbRow = PdbReader.ReadMethodDocumentPaths(assemblyPath, pdbPath);
        if (documentPathsByPdbRow.Count == 0 || PdbReader.DetectFormat(pdbPath) != PdbFormat.Portable)
            return new PdbMethodDebugMap(documentPathsByPdbRow, new Dictionary<int, int>());

        using var file = new PEFile(assemblyPath);
        var methodInfos = BuildMethodInfos(file.Metadata, typeNames);
        var methodRowsBySimpleTypeName = BuildMethodRowsBySimpleTypeName(methodInfos);
        var remappedRows = BuildRemappedRows(documentPathsByPdbRow, methodInfos, methodRowsBySimpleTypeName);

        if (remappedRows.Count == 0)
            return new PdbMethodDebugMap(documentPathsByPdbRow, new Dictionary<int, int>());

        var documentPathsByActualRow = new Dictionary<int, string>();
        foreach (var pair in documentPathsByPdbRow)
        {
            var actualRow = remappedRows.GetValueOrDefault(pair.Key, pair.Key);
            if (!methodInfos.ContainsKey(actualRow))
                continue;

            documentPathsByActualRow[actualRow] = pair.Value;
        }

        var pdbRowByActualRow = remappedRows
            .AsValueEnumerable()
            .GroupBy(pair => pair.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(pair => Math.Abs(pair.Value - pair.Key))
                    .ThenBy(pair => pair.Key)
                    .First()
                    .Key);

        return new PdbMethodDebugMap(documentPathsByActualRow, pdbRowByActualRow);
    }

    private static Dictionary<int, MethodInfo> BuildMethodInfos(
        MetadataReader metadata,
        IReadOnlyDictionary<TypeDefinitionHandle, string> typeNames)
    {
        var result = new Dictionary<int, MethodInfo>();

        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            if (!typeNames.TryGetValue(typeHandle, out var typeName))
                continue;

            var typeDefinition = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var row = MetadataTokens.GetRowNumber(methodHandle);
                var rootTypeName = GetRootTypeName(typeName);
                result[row] = new MethodInfo(
                    typeName,
                    rootTypeName,
                    GetSimpleTypeName(typeName),
                    GetSimpleTypeName(rootTypeName));
            }
        }

        return result;
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
        if (!declaringHandle.IsNil)
            return BuildFullName(reader, declaringHandle) + "+" + name;

        var ns = reader.GetString(typeDef.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static Dictionary<string, List<int>> BuildMethodRowsBySimpleTypeName(
        IReadOnlyDictionary<int, MethodInfo> methodInfos)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in methodInfos)
        {
            Add(pair.Value.TypeSimpleName, pair.Key);
            Add(pair.Value.RootSimpleName, pair.Key);
        }

        return result;

        void Add(string typeName, int row)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return;

            if (!result.TryGetValue(typeName, out var rows))
            {
                rows = [];
                result[typeName] = rows;
            }

            rows.Add(row);
        }
    }

    private static Dictionary<int, int> BuildRemappedRows(
        IReadOnlyDictionary<int, string> documentPathsByPdbRow,
        IReadOnlyDictionary<int, MethodInfo> methodInfos,
        IReadOnlyDictionary<string, List<int>> methodRowsBySimpleTypeName)
    {
        var result = new Dictionary<int, int>();

        foreach (var documentGroup in documentPathsByPdbRow
                     .AsValueEnumerable()
                     .GroupBy(pair => NormalizePath(pair.Value), StringComparer.OrdinalIgnoreCase))
        {
            var fileStem = Path.GetFileNameWithoutExtension(documentGroup.Key);
            if (string.IsNullOrWhiteSpace(fileStem))
                continue;

            var matchingRows = GetMatchingRows(fileStem, methodRowsBySimpleTypeName);
            if (matchingRows.Count == 0)
                continue;

            foreach (var cluster in BuildClusters(documentGroup.Select(pair => pair.Key), ClusterGap))
            {
                if (cluster.Count < 3)
                    continue;

                var score = FindBestShift(cluster, fileStem, methodInfos, matchingRows);
                if (!ShouldAccept(score, cluster.Count))
                    continue;

                foreach (var pdbRow in cluster)
                {
                    var actualRow = pdbRow + score.Shift;
                    if (methodInfos.ContainsKey(actualRow))
                        result[pdbRow] = actualRow;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<int> GetMatchingRows(
        string fileStem,
        IReadOnlyDictionary<string, List<int>> methodRowsBySimpleTypeName)
    {
        if (methodRowsBySimpleTypeName.TryGetValue(fileStem, out var exactRows))
            return exactRows;

        if (fileStem.Length < 8)
            return [];

        return methodRowsBySimpleTypeName
            .AsValueEnumerable()
            .Where(pair => fileStem.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value)
            .Distinct()
            .ToList();
    }

    private static ShiftScore FindBestShift(
        IReadOnlyList<int> rows,
        string fileStem,
        IReadOnlyDictionary<int, MethodInfo> methodInfos,
        IReadOnlyList<int> matchingRows) =>
        BuildCandidateShifts(rows, matchingRows)
            .AsValueEnumerable()
            .Select(shift => ScoreShift(rows, shift, fileStem, methodInfos))
            .OrderByDescending(score => score.ExactTypeNameMatches)
            .ThenByDescending(score => score.StemTypeNameMatches)
            .ThenByDescending(score => score.DominantTypeCount)
            .ThenBy(score => Math.Abs(score.Shift))
            .FirstOrDefault();

    private static bool ShouldAccept(ShiftScore score, int rowCount)
    {
        if (score.MappedCount != rowCount || score.Shift == 0)
            return false;

        var exactRatio = (double)score.ExactTypeNameMatches / rowCount;
        var stemRatio = (double)score.StemTypeNameMatches / rowCount;
        var dominantRatio = (double)score.DominantTypeCount / rowCount;

        return exactRatio >= 0.6 && stemRatio >= 0.75 && dominantRatio >= 0.75;
    }

    private static HashSet<int> BuildCandidateShifts(
        IReadOnlyList<int> rows,
        IReadOnlyList<int> matchingRows)
    {
        var shifts = new HashSet<int>();
        if (rows.Count == 0 || matchingRows.Count == 0)
            return shifts;

        if (rows.Count * matchingRows.Count <= 2000)
        {
            foreach (var sourceRow in rows)
            foreach (var targetRow in matchingRows)
                AddShiftNeighborhood(shifts, targetRow - sourceRow);
        }
        else
        {
            var sourceStart = rows[0];
            var sourceEnd = rows[^1];
            foreach (var range in BuildRanges(matchingRows))
            {
                AddShiftNeighborhood(shifts, range.Start - sourceStart);
                AddShiftNeighborhood(shifts, range.End - sourceEnd);

                var sourceMid = sourceStart + (sourceEnd - sourceStart) / 2;
                var targetMid = range.Start + (range.End - range.Start) / 2;
                AddShiftNeighborhood(shifts, targetMid - sourceMid);
            }
        }

        shifts.Add(0);
        return shifts;
    }

    private static void AddShiftNeighborhood(HashSet<int> shifts, int baseShift)
    {
        if (Math.Abs(baseShift) > MaxShift)
            return;

        for (var shift = baseShift - CandidateWindow; shift <= baseShift + CandidateWindow; shift++)
            if (Math.Abs(shift) <= MaxShift)
                shifts.Add(shift);
    }

    private static List<RowRange> BuildRanges(IReadOnlyList<int> rows)
    {
        var orderedRows = rows.Order().ToList();
        var ranges = new List<RowRange>();

        foreach (var row in orderedRows)
        {
            if (ranges.Count == 0 || row - ranges[^1].End > 1)
            {
                ranges.Add(new RowRange(row, row));
                continue;
            }

            ranges[^1] = ranges[^1] with { End = row };
        }

        return ranges;
    }

    private static ShiftScore ScoreShift(
        IReadOnlyList<int> rows,
        int shift,
        string fileStem,
        IReadOnlyDictionary<int, MethodInfo> methodInfos)
    {
        var mappedCount = 0;
        var exactMatches = 0;
        var stemMatches = 0;
        var mappedTypes = new List<string>();

        foreach (var row in rows)
        {
            if (!methodInfos.TryGetValue(row + shift, out var method))
                continue;

            mappedCount++;
            mappedTypes.Add(method.RootTypeName);

            var rootSimpleType = GetSimpleTypeName(method.RootTypeName);
            var nestedSimpleType = GetSimpleTypeName(method.TypeName);

            if (string.Equals(rootSimpleType, fileStem, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nestedSimpleType, fileStem, StringComparison.OrdinalIgnoreCase))
                exactMatches++;

            if (TypeMatchesFileStem(method, fileStem))
                stemMatches++;
        }

        var dominantCount = mappedTypes
            .AsValueEnumerable()
            .GroupBy(typeName => typeName, StringComparer.Ordinal)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();

        return new ShiftScore(shift, mappedCount, exactMatches, stemMatches, dominantCount);
    }

    private static List<List<int>> BuildClusters(IEnumerable<int> rows, int maxGap)
    {
        var clusters = new List<List<int>>();
        foreach (var row in rows.Order())
        {
            if (clusters.Count == 0 || row - clusters[^1][^1] > maxGap)
                clusters.Add([]);

            clusters[^1].Add(row);
        }

        return clusters;
    }

    private static bool TypeMatchesFileStem(MethodInfo method, string fileStem) =>
        method.RootSimpleName.Contains(fileStem, StringComparison.OrdinalIgnoreCase)
        || fileStem.Contains(method.RootSimpleName, StringComparison.OrdinalIgnoreCase)
        || method.TypeSimpleName.Contains(fileStem, StringComparison.OrdinalIgnoreCase)
        || fileStem.Contains(method.TypeSimpleName, StringComparison.OrdinalIgnoreCase);

    private static string GetRootTypeName(string typeName)
    {
        var nestedIndex = typeName.IndexOf('+');
        return nestedIndex < 0 ? typeName : typeName[..nestedIndex];
    }

    private static string GetSimpleTypeName(string typeName)
    {
        var nestedIndex = typeName.LastIndexOf('+');
        var namespaceIndex = typeName.LastIndexOf('.');
        var index = Math.Max(nestedIndex, namespaceIndex);
        var simpleName = index < 0 ? typeName : typeName[(index + 1)..];
        var arityIndex = simpleName.IndexOf('`');
        return arityIndex < 0 ? simpleName : simpleName[..arityIndex];
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private sealed record MethodInfo(
        string TypeName,
        string RootTypeName,
        string TypeSimpleName,
        string RootSimpleName);

    private readonly record struct ShiftScore(
        int Shift,
        int MappedCount,
        int ExactTypeNameMatches,
        int StemTypeNameMatches,
        int DominantTypeCount);

    private readonly record struct RowRange(int Start, int End);
}
