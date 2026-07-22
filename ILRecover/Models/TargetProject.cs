using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ILRecover.Pdb;

namespace ILRecover.Models;

public sealed record TargetProject(
    string AssemblyPath,
    string PdbPath,
    string Name,
    List<ProjectReferenceInfo> ProjectRefs,
    PdbMethodRemapOptions PdbMethodRemapOptions)
{
    public string CreateOutputDirectory(string rootOutputDir) => Path.Combine(rootOutputDir, Name);
}
