namespace ILRecover.Analysis.SourceGen;

internal static class SourceGenPaths
{
    public static bool IsMemoryPackGeneratorPath(string path) =>
        Normalize(path).Contains("MemoryPack.Generator", StringComparison.OrdinalIgnoreCase);

    public static bool IsMessagePackGeneratorPath(string path) =>
        Normalize(path).Contains("MessagePack.Generator", StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownGeneratorPath(string path) =>
        IsMemoryPackGeneratorPath(path) || IsMessagePackGeneratorPath(path);

    private static string Normalize(string path) => path.Replace('\\', '/');
}
