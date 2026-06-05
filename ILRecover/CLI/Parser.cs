using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ILRecover.Analysis;
using ILRecover.Analysis.Decompiler;
using ILRecover.Helpers;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.CLI;

public static class Parser
{
    public static void Execute(
        string input,
        string output,
        int csVersion,
        string[]? dependencies,
        string? solution,
        string? dotnet)
    {
        var csVersionStr = csVersion > 0 ? csVersion.ToString() : null;
        IReadOnlyList<string> dependencyDirs = dependencies ?? [];

        var targets = ValidateAndResolveTargets(input);
        var projectPaths = new List<string>();

        foreach (var target in targets)
        {
            Log.Info($"{target.Name}");

            var outputDir = Path.Combine(output, target.Name);

            if (Directory.Exists(outputDir))
            {
                Log.Info("Cleaning Output...");
                Directory.Delete(outputDir, true);
            }

            Log.Info("Analyzing...");
            var analyzer = new AssemblyAnalyzer(target.DllPath, target.PdbPath);
            var result = analyzer.Analyze();
            Log.Info($"Mapped: {result.Mapped.Count} Skipped: {result.Skipped.Count}");

            Log.Info("Writing csproj...");
            var builder = new RecoveredProjectFileBuilder(
                target.DllPath,
                outputDir,
                target.Name,
                target.ProjectRefs,
                csVersionStr,
                dotnet,
                dependencyDirs);
            var projectPath = builder.Build();
            projectPaths.Add(projectPath);
            Log.Info($"Wrote: {projectPath}");

            Log.Info("Decompiling...");
            var phase = new DecompilerPhase(target.DllPath, result.Mapped, outputDir, csVersionStr, dotnet, dependencyDirs,
                target.PdbPath);
            phase.Run();

            Log.Info($"Done: {outputDir}");
        }

        Log.Info("Writing solution...");
        var solutionPath = new RecoveredSolutionFileBuilder(output, solution, projectPaths).Build();
        Log.Info($"Wrote: {solutionPath}");

        Log.Info("All Done!");
    }

    private static List<TargetProject> ValidateAndResolveTargets(string dllFolder)
    {
        if (!Directory.Exists(dllFolder))
        {
            Log.Error($"Input folder not found: {dllFolder}");
            Log.Shutdown();
            Environment.Exit(1);
        }

        var targets = Directory.GetFiles(dllFolder, "*.dll")
            .AsValueEnumerable()
            .Select(p => (
                DllPath: p,
                PdbPath: Path.ChangeExtension(p, ".pdb"),
                Name: Path.GetFileNameWithoutExtension(p)
            ))
            .Where(t => File.Exists(t.PdbPath))
            .ToList();

        if (targets.Count == 0)
        {
            Log.Error($"No target dlls with matching pdbs found in {dllFolder}");
            Log.Shutdown();
            Environment.Exit(1);
        }

        var projectNames = targets
            .AsValueEnumerable()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return targets
            .AsValueEnumerable()
            .Select(t => new TargetProject(
                t.DllPath,
                t.PdbPath,
                t.Name,
                BuildProjectRefs(t.DllPath, t.Name, projectNames)))
            .ToList();
    }

    private static List<ProjectReferenceInfo> BuildProjectRefs(
        string dllPath,
        string projectName,
        IReadOnlySet<string> projectNames)
    {
        var file = new PEFile(dllPath);

        return file.Metadata.AssemblyReferences
            .AsValueEnumerable()
            .Select(handle => file.Metadata.GetString(file.Metadata.GetAssemblyReference(handle).Name))
            .Where(projectNames.Contains)
            .Where(name => !string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProjectReferenceInfo(name, Path.Combine("..", name, $"{name}.csproj")))
            .ToList();
    }

    private sealed record TargetProject(
        string DllPath,
        string PdbPath,
        string Name,
        List<ProjectReferenceInfo> ProjectRefs);
}
