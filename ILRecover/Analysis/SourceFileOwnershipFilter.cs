using ILRecover.Models;
using ZLinq;

namespace ILRecover.Analysis;

public sealed record SourceFileOwnershipFilterResult(
    IReadOnlyList<SourceFileMap> Mapped,
    int RejectedTypeCount,
    int UnmappedTypeCount);

public static class SourceFileOwnershipFilter
{
    public static SourceFileOwnershipFilterResult Apply(
        IReadOnlyList<SourceFileMap> sourceFiles,
        bool allowUnmapped)
    {
        var mapped = new List<SourceFileMap>();
        var keptRoots = new HashSet<string>(StringComparer.Ordinal);
        var rejectedRoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceFile in sourceFiles)
        {
            if (sourceFile.IsGenerated)
            {
                mapped.Add(sourceFile);
                foreach (var root in GetRootTypeNames(sourceFile))
                    keptRoots.Add(root);
                continue;
            }

            var roots = GetRootTypeNames(sourceFile).ToList();
            var ownedRoots = roots
                .AsValueEnumerable()
                .Where(root => IsOwnedByFile(sourceFile.RelativePath, root, roots))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var root in roots.Where(root => !ownedRoots.Contains(root)))
                rejectedRoots.Add(root);

            foreach (var root in ownedRoots)
                keptRoots.Add(root);

            var filteredFile = FilterSourceFile(sourceFile, ownedRoots);
            if (HasContent(filteredFile))
                mapped.Add(filteredFile);
        }

        var unmappedRoots = rejectedRoots
            .AsValueEnumerable()
            .Where(root => !keptRoots.Contains(root))
            .OrderBy(root => root, StringComparer.Ordinal)
            .ToList();

        if (allowUnmapped)
            mapped.AddRange(unmappedRoots.Select(CreateUnmappedFile));

        return new SourceFileOwnershipFilterResult(mapped, rejectedRoots.Count, allowUnmapped ? unmappedRoots.Count : 0);
    }

    private static List<string> GetRootTypeNames(SourceFileMap sourceFile) =>
        sourceFile.TypeFullNames
            .AsValueEnumerable()
            .Where(typeName => !IsCompilerGenerated(typeName))
            .Select(GetRootTypeName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static SourceFileMap FilterSourceFile(SourceFileMap sourceFile, IReadOnlySet<string> ownedRoots) =>
        sourceFile with
        {
            Methods = sourceFile.Methods
                .AsValueEnumerable()
                .Where(method => ownedRoots.Contains(GetRootTypeName(method.TypeFullName)))
                .ToList(),
            DeclaredTypeFullNames = sourceFile.TypeFullNames
                .AsValueEnumerable()
                .Where(typeName => ownedRoots.Contains(GetRootTypeName(typeName)))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            TypeDeclarations = (sourceFile.TypeDeclarations ?? [])
                .AsValueEnumerable()
                .Where(typeDeclaration => ownedRoots.Contains(GetRootTypeName(typeDeclaration.TypeFullName)))
                .ToList()
        };

    private static bool HasContent(SourceFileMap sourceFile) =>
        sourceFile.DecompileWholeTypes
        || sourceFile.Methods.Count > 0
        || (sourceFile.TypeDeclarations is not null && sourceFile.TypeDeclarations.Count > 0)
        || (sourceFile.DeclaredTypeFullNames is not null && sourceFile.DeclaredTypeFullNames.Count > 0);

    private static bool IsOwnedByFile(
        string relativePath,
        string rootTypeName,
        IReadOnlyList<string> fileRoots)
    {
        var simpleName = GetSimpleTypeName(rootTypeName);
        var fullStem = Path.GetFileNameWithoutExtension(relativePath);
        var primaryStem = GetPrimaryStem(fullStem);

        if (NameEquals(simpleName, fullStem) || NameEquals(simpleName, primaryStem))
            return true;

        if (StartsWithName(fullStem, simpleName + "."))
            return true;

        if (IsStrongPrefix(fullStem, simpleName) || IsStrongPrefix(primaryStem, simpleName))
            return true;

        var fileTokens = SplitNameTokens(fullStem);
        if (fileTokens.Count == 0 || fileTokens[0].Length < 3)
            return false;

        var firstToken = fileTokens[0];
        var matchingRootCount = fileRoots
            .AsValueEnumerable()
            .Select(GetSimpleTypeName)
            .Count(typeName => SplitNameTokens(typeName).FirstOrDefault() is { } token
                               && NameEquals(token, firstToken));

        return matchingRootCount >= 2 && SplitNameTokens(simpleName).FirstOrDefault() is { } typeFirstToken
                                      && NameEquals(typeFirstToken, firstToken);
    }

    private static SourceFileMap CreateUnmappedFile(string rootTypeName)
    {
        var relativePath = Path.Combine(GetUnmappedPathParts(rootTypeName).ToArray());
        return new SourceFileMap(
            relativePath,
            relativePath,
            false,
            [],
            [rootTypeName],
            [],
            true);
    }

    private static IEnumerable<string> GetUnmappedPathParts(string rootTypeName)
    {
        yield return "Unmapped";

        var namespaceName = GetNamespace(rootTypeName);
        if (!string.IsNullOrWhiteSpace(namespaceName))
            foreach (var part in namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
                yield return SanitizePathPart(part);

        yield return SanitizePathPart(GetSimpleTypeName(rootTypeName)) + ".cs";
    }

    private static bool IsStrongPrefix(string fileStem, string simpleName) =>
        fileStem.Length >= 4
        && simpleName.Length > fileStem.Length
        && simpleName.StartsWith(fileStem, StringComparison.OrdinalIgnoreCase);

    private static string GetPrimaryStem(string fileStem)
    {
        var index = fileStem.IndexOf('.');
        return index < 0 ? fileStem : fileStem[..index];
    }

    private static List<string> SplitNameTokens(string name)
    {
        var tokens = new List<string>();
        var start = 0;

        for (var i = 1; i < name.Length; i++)
        {
            if (name[i] is '.' or '_' or '-')
            {
                AddToken(name[start..i]);
                start = i + 1;
                continue;
            }

            if (!char.IsUpper(name[i]))
                continue;

            var previous = name[i - 1];
            var next = i + 1 < name.Length ? name[i + 1] : '\0';
            if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next)))
            {
                AddToken(name[start..i]);
                start = i;
            }
        }

        AddToken(name[start..]);
        return tokens;

        void AddToken(string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
                tokens.Add(token);
        }
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithName(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string GetRootTypeName(string typeFullName)
    {
        var nestedIndex = typeFullName.IndexOf('+');
        return nestedIndex < 0 ? typeFullName : typeFullName[..nestedIndex];
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

    private static string GetNamespace(string typeName)
    {
        var rootTypeName = GetRootTypeName(typeName);
        var namespaceIndex = rootTypeName.LastIndexOf('.');
        return namespaceIndex < 0 ? string.Empty : rootTypeName[..namespaceIndex];
    }

    private static string SanitizePathPart(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private static bool IsCompilerGenerated(string typeName) => typeName.StartsWith('<') || typeName.Contains("+<");
}
