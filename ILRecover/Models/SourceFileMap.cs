using ZLinq;

namespace ILRecover.Models;

public record SourceFileMap(
    string OriginalPath,
    string RelativePath,
    bool IsGenerated,
    IReadOnlyList<SourceFileMethodEntry> Methods,
    IReadOnlyList<string>? DeclaredTypeFullNames = null
)
{
    public IReadOnlyList<string> TypeFullNames =>
        (DeclaredTypeFullNames is { Count: > 0 }
            ? DeclaredTypeFullNames
            : Methods
                .AsValueEnumerable()
                .Select(m => m.TypeFullName)
                .Distinct(StringComparer.Ordinal)
                .ToList())
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
