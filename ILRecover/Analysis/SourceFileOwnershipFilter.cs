using ILRecover.Models;
using ZLinq;
using ILRecover.Helpers;
using static ILRecover.Helpers.TypeNameHelper;

namespace ILRecover.Analysis;

public sealed record SourceFileOwnershipFilterResult(
    IReadOnlyList<SourceFileMap> Mapped,
    int RejectedTypeCount,
    int UnmappedTypeCount);

public static class SourceFileOwnershipFilter
{
    public static SourceFileOwnershipFilterResult Apply(
        IReadOnlyList<SourceFileMap> sourceFiles,
        bool allowUnmapped,
        IReadOnlyList<string>? skippedPaths = null,
        IReadOnlyList<string>? typeFullNames = null,
        IReadOnlyList<string>? externalSourcePaths = null,
        bool externalPriority = false)
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

        var remappedRejectedFiles = allowUnmapped
            ? FindRejectedDocumentMatches(sourceFiles, skippedPaths ?? [], externalSourcePaths ?? [], unmappedRoots,
                externalPriority)
            : [];

        var remappedRejectedRoots = remappedRejectedFiles
            .AsValueEnumerable()
            .SelectMany(file => file.TypeFullNames)
            .ToHashSet(StringComparer.Ordinal);

        var rescuedSkippedFiles = allowUnmapped
            ? FindSkippedRescueFiles(skippedPaths ?? [], typeFullNames ?? [], keptRoots, rejectedRoots,
                remappedRejectedRoots)
            : [];

        if (allowUnmapped)
        {
            mapped.AddRange(remappedRejectedFiles);
            mapped.AddRange(rescuedSkippedFiles);
            mapped.AddRange(unmappedRoots
                .AsValueEnumerable()
                .Where(root => !remappedRejectedRoots.Contains(root))
                .Where(root => rescuedSkippedFiles
                    .AsValueEnumerable()
                    .All(file => !file.TypeFullNames.Contains(root, StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(root => root, StringComparer.Ordinal)
                .Select(CreateUnmappedFile)
                .ToList());
        }

        return new SourceFileOwnershipFilterResult(
            mapped,
            rejectedRoots.Count,
            allowUnmapped
                ? rescuedSkippedFiles.Count + unmappedRoots
                    .Where(root => !remappedRejectedRoots.Contains(root))
                    .Where(root => rescuedSkippedFiles
                        .AsValueEnumerable()
                        .All(file => !file.TypeFullNames.Contains(root, StringComparer.Ordinal)))
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                : 0);
    }

    private static List<SourceFileMap> FindSkippedRescueFiles(
        IReadOnlyList<string> skippedPaths,
        IReadOnlyList<string> typeFullNames,
        IReadOnlySet<string> keptRoots,
        IReadOnlySet<string> rejectedRoots,
        IReadOnlySet<string> remappedRejectedRoots)
    {
        if (skippedPaths.Count == 0 || typeFullNames.Count == 0)
            return [];

        var rootsBySimpleName = typeFullNames
            .AsValueEnumerable()
            .Where(typeName => !IsCompilerGenerated(typeName))
            .Select(GetRoot)
            .Distinct(StringComparer.Ordinal)
            .GroupBy(GetSimple, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<SourceFileMap>();
        var rescuedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skippedPath in skippedPaths)
        {
            var stem = skippedPath.GetFileStem();
            if (string.IsNullOrWhiteSpace(stem))
                continue;

            if (!rootsBySimpleName.TryGetValue(skippedPath.GetPrimaryFileStem(), out var candidates) &&
                !rootsBySimpleName.TryGetValue(stem, out candidates))
                continue;

            var usableCandidates = candidates
                .AsValueEnumerable()
                .Where(root => !keptRoots.Contains(root)
                               && !rejectedRoots.Contains(root)
                               && !remappedRejectedRoots.Contains(root))
                .ToList();

            if (usableCandidates.Count != 1 || !rescuedRoots.Add(usableCandidates[0]))
                continue;

            result.Add(CreateRescuedSkippedFile(skippedPath, usableCandidates[0]));
        }

        return result;
    }

    private static List<SourceFileMap> FindRejectedDocumentMatches(
        IReadOnlyList<SourceFileMap> sourceFiles,
        IReadOnlyList<string> skippedPaths,
        IReadOnlyList<string> externalSourcePaths,
        IReadOnlyList<string> rejectedRoots,
        bool externalPriority)
    {
        if (rejectedRoots.Count == 0)
            return [];

        var usedPaths = sourceFiles
            .AsValueEnumerable()
            .Where(HasContent)
            .Select(file => file.RelativePath.NormalizePath())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pdbDocuments = sourceFiles
            .AsValueEnumerable()
            .Select(file => file.RelativePath)
            .Concat(skippedPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !usedPaths.Contains(path.NormalizePath()))
            .ToList();

        var externalDocuments = externalSourcePaths
            .AsValueEnumerable()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !usedPaths.Contains(path.NormalizePath()))
            .ToList();

        var documentTiers = externalPriority
            ? [externalDocuments, pdbDocuments]
            : new[] { pdbDocuments, externalDocuments };

        var result = new List<SourceFileMap>();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rejectedRoots.GroupBy(GetSimple, StringComparer.OrdinalIgnoreCase))
        {
            var remainingRoots = group.ToList();
            foreach (var documents in documentTiers)
            {
                if (remainingRoots.Count == 0)
                    break;

                var matches = FindMatchingDocuments(remainingRoots, documents, claimedPaths);
                foreach (var match in matches)
                {
                    claimedPaths.Add(match.RelativePath);
                    result.Add(CreateMatchedDocumentFile(match.RelativePath, match.RootTypeName));
                }

                var matchedRoots = matches
                    .AsValueEnumerable()
                    .Select(match => match.RootTypeName)
                    .ToHashSet(StringComparer.Ordinal);
                remainingRoots = remainingRoots
                    .AsValueEnumerable()
                    .Where(root => !matchedRoots.Contains(root))
                    .ToList();
            }
        }

        return result;
    }

    private static List<(string RelativePath, string RootTypeName)> FindMatchingDocuments(
        IReadOnlyList<string> rootTypeNames,
        IReadOnlyList<string> availableDocuments,
        IReadOnlySet<string> claimedPaths)
    {
        var simpleName = GetSimple(rootTypeNames[0]);
        var candidateDocuments = availableDocuments
            .AsValueEnumerable()
            .Where(path => !claimedPaths.Contains(path)
                           && NameEquals(path.GetFileStem(), simpleName))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateDocuments.Count == 0)
        {
            candidateDocuments = availableDocuments
                .AsValueEnumerable()
                .Where(path => !claimedPaths.Contains(path)
                               && NameEquals(path.GetPrimaryFileStem(), simpleName))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (rootTypeNames.Count == 1 && candidateDocuments.Count == 1)
            return [(candidateDocuments[0], rootTypeNames[0])];

        return rootTypeNames
            .AsValueEnumerable()
            .Select(root => (
                RootTypeName: root,
                Matches: candidateDocuments
                    .AsValueEnumerable()
                    .Where(path => NamespaceMatchesPath(root, path))
                    .ToList()))
            .Where(match => match.Matches.Count == 1)
            .Select(match => (match.Matches[0], match.RootTypeName))
            .GroupBy(match => match.Item1, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToList();
    }

    private static SourceFileMap CreateMatchedDocumentFile(string relativePath, string rootTypeName) =>
        new(
            relativePath,
            relativePath,
            false,
            [],
            [rootTypeName],
            [],
            true);

    private static SourceFileMap CreateRescuedSkippedFile(string relativePath, string rootTypeName) =>
        new(
            relativePath,
            Path.Combine("Unmapped", relativePath),
            false,
            [],
            [rootTypeName],
            [],
            true);

    private static List<string> GetRootTypeNames(SourceFileMap sourceFile) =>
        sourceFile.TypeFullNames
            .AsValueEnumerable()
            .Where(typeName => !IsCompilerGenerated(typeName))
            .Select(GetRoot)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static SourceFileMap FilterSourceFile(SourceFileMap sourceFile, IReadOnlySet<string> ownedRoots) =>
        sourceFile with
        {
            Methods = sourceFile.Methods
                .AsValueEnumerable()
                .Where(method => ownedRoots.Contains(GetRoot(method.TypeFullName)))
                .ToList(),
            DeclaredTypeFullNames = sourceFile.TypeFullNames
                .AsValueEnumerable()
                .Where(typeName => ownedRoots.Contains(GetRoot(typeName)))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            TypeDeclarations = (sourceFile.TypeDeclarations ?? [])
                .AsValueEnumerable()
                .Where(typeDeclaration => ownedRoots.Contains(GetRoot(typeDeclaration.TypeFullName)))
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
        var simpleName = GetSimple(rootTypeName);
        var fullStem = relativePath.GetFileStem();
        var primaryStem = relativePath.GetPrimaryFileStem();

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
            .Select(GetSimple)
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
                yield return part.SanitizeFileNamePart();

        yield return GetSimple(rootTypeName).SanitizeFileNamePart() + ".cs";
    }

    private static bool IsStrongPrefix(string fileStem, string simpleName) =>
        fileStem.Length >= 4
        && simpleName.Length > fileStem.Length
        && simpleName.StartsWith(fileStem, StringComparison.OrdinalIgnoreCase);

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

    private static bool NamespaceMatchesPath(string rootTypeName, string relativePath)
    {
        var namespaceName = GetNamespace(rootTypeName);
        if (string.IsNullOrWhiteSpace(namespaceName))
            return false;

        var namespaceParts = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var directoryParts = relativePath.GetDirectoryParts();
        if (directoryParts.Count < namespaceParts.Length)
            return false;

        var offset = directoryParts.Count - namespaceParts.Length;
        return namespaceParts
            .AsValueEnumerable()
            .Select((part, index) => NameEquals(part, directoryParts[offset + index]))
            .All(matches => matches);
    }

}
