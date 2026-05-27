namespace ILRecover.Models;

public record AnalysisResult(
    IReadOnlyList<SourceFileMap> Mapped,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<Pdb.PdbSourceInfo>? AllSources = null
);
