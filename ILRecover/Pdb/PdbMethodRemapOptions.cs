using ZLinq;

namespace ILRecover.Pdb;

public sealed class PdbMethodRemapOptions(IReadOnlyList<string> patterns)
{
    public static PdbMethodRemapOptions Disabled { get; } = new([]);

    public bool IsEnabledFor(string assemblyName) =>
        patterns
            .AsValueEnumerable()
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Any(pattern => MatchesPattern(assemblyName, pattern));

    private static bool MatchesPattern(string value, string pattern)
    {
        if (string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!pattern.Contains('*', StringComparison.Ordinal))
            return false;

        var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;

        foreach (var part in parts)
        {
            var nextIndex = value.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (nextIndex < 0)
                return false;

            index = nextIndex + part.Length;
        }

        return (pattern.StartsWith('*') ||
                value.StartsWith(parts.FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase))
               && (pattern.EndsWith('*') ||
                   value.EndsWith(parts.LastOrDefault() ?? "", StringComparison.OrdinalIgnoreCase));
    }
}