using ZLinq;

namespace ILRecover.Models;

public record SourceFileMap(
    string OriginalPath,
    string RelativePath,
    bool IsGenerated,
    IReadOnlyList<SourceFileMethodEntry> Methods
)
{
    public IReadOnlyList<string> TypeFullNames =>
        Methods
            .AsValueEnumerable()
            .Select(m => m.TypeFullName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}