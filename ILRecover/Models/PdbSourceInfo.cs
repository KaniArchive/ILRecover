using ILRecover.Helpers;

namespace ILRecover.Pdb;

public record PdbSourceInfo(string OriginalPath, bool IsGenerated)
{
    public bool IsGeneratedCompanion =>
        IsGenerated
        && OriginalPath.NormalizePath().Contains("/Generated/", StringComparison.OrdinalIgnoreCase);
}
