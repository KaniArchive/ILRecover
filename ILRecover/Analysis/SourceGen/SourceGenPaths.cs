using ILRecover.Helpers;

namespace ILRecover.Analysis.SourceGen;

internal static class SourceGenPaths
{
    public static bool IsMemoryPackGeneratorPath(string path) =>
        path.NormalizePath().Contains("MemoryPack.Generator", StringComparison.OrdinalIgnoreCase);

    public static bool IsMessagePackGeneratorPath(string path) =>
        path.NormalizePath().Contains("MessagePack.Generator", StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownGeneratorPath(string path) =>
        IsMemoryPackGeneratorPath(path) || IsMessagePackGeneratorPath(path);
}
