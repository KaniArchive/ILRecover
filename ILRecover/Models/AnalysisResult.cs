using ILRecover.Pdb;

namespace ILRecover.Models;

public record AnalysisResult(
    IReadOnlyList<SourceFileMap> Mapped,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<PdbSourceInfo>? AllSources = null,
    IReadOnlyList<string>? TypeFullNames = null,
    IReadOnlyList<string>? SkippedRelativePaths = null
);
