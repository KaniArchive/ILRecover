using ILRecover.Pdb;

namespace ILRecover.Models;

public sealed record RecoverOptions(
    string Input,
    string Output,
    string? CSharpVersion,
    IReadOnlyList<string> DependencySearchDirs,
    string? SolutionName,
    string? DotNetVersion,
    PdbMethodRemapOptions PdbMethodRemapOptions,
    SourceOwnershipOptions SourceOwnership);

public sealed record SourceOwnershipOptions(
    bool Enabled,
    bool AllowUnmapped,
    string? SourcePathListPath,
    bool ExternalPriority)
{
    public static SourceOwnershipOptions Disabled { get; } = new(false, false, null, false);
}
