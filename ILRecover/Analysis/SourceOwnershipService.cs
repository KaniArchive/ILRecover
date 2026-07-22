using ILRecover.Helpers;
using ILRecover.Models;

namespace ILRecover.Analysis;

public sealed class SourceOwnershipService(SourceOwnershipOptions options)
{
    private readonly IReadOnlyList<string> _externalSourcePaths = LoadExternalSourcePaths(options);

    public SourceFileOwnershipFilterResult Apply(AnalysisResult result)
    {
        if (!options.Enabled)
            return new SourceFileOwnershipFilterResult(result.Mapped, 0, 0);

        return SourceFileOwnershipFilter.Apply(
            result.Mapped,
            options.AllowUnmapped,
            result.SkippedRelativePaths,
            result.TypeFullNames,
            _externalSourcePaths,
            options.ExternalPriority);
    }

    public void WarnIgnoredOptions()
    {
        if (!options.Enabled && !string.IsNullOrWhiteSpace(options.SourcePathListPath))
            Log.Warning("--source-paths ignored without --class-own-file");
        if (options.Enabled && !options.AllowUnmapped && !string.IsNullOrWhiteSpace(options.SourcePathListPath))
            Log.Warning("--source-paths ignored without --allow-unmapped");
        if (options.ExternalPriority && _externalSourcePaths.Count == 0)
            Log.Warning("--external-priority was set without --source-paths");
    }

    private static IReadOnlyList<string> LoadExternalSourcePaths(SourceOwnershipOptions options) =>
        options.Enabled && options.AllowUnmapped
            ? ReadExternalSourcePaths(options.SourcePathListPath)
            : [];

    private static IReadOnlyList<string> ReadExternalSourcePaths(string? sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourcePaths))
            return [];

        var fullPath = Path.GetFullPath(sourcePaths);
        if (!File.Exists(fullPath))
            Fail($"Source path list not found: {fullPath}");

        return File.ReadLines(fullPath)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Fail(string message)
    {
        Log.Error(message);
        Log.Shutdown();
        Environment.Exit(1);
    }
}
