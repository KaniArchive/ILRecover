using ZLinq;

namespace ILRecover.Models;

public record SourceFileMap(
    string OriginalPath,
    string RelativePath,
    bool IsGenerated,
    IReadOnlyList<SourceFileMethodEntry> Methods,
    IReadOnlyList<string>? DeclaredTypeFullNames = null,
    IReadOnlyList<SourceFileTypeDeclarationEntry>? TypeDeclarations = null
)
{
    public IReadOnlyList<string> TypeFullNames =>
        (DeclaredTypeFullNames ?? Methods
            .AsValueEnumerable()
            .Select(m => m.TypeFullName)
            .ToList())
        .Concat((TypeDeclarations ?? [])
            .AsValueEnumerable()
            .Select(entry => entry.TypeFullName)
            .ToList())
        .Distinct(StringComparer.Ordinal)
        .ToList();
}