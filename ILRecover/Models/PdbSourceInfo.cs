namespace ILRecover.Pdb;

public record PdbSourceInfo(string OriginalPath, bool IsGenerated)
{
    public bool IsGeneratedCompanion =>
        IsGenerated
        && OriginalPath.Replace('\\', '/').Contains("/Generated/", StringComparison.OrdinalIgnoreCase);
}
