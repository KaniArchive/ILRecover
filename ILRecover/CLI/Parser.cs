using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Analysis;
using ILRecover.Analysis.Decompiler;
using ILRecover.Helpers;
using System.Reflection.PortableExecutable;
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
            var analyzer = new AssemblyAnalyzer(target.AssemblyPath, target.PdbPath);
            var result = analyzer.Analyze();
            Log.Success($"Mapped: {result.Mapped.Count} Skipped: {result.Skipped.Count}");

            Log.Info("Writing csproj...");
            var builder = new RecoveredProjectFileBuilder(
                target.AssemblyPath,
                outputDir,
                target.Name,
                target.ProjectRefs,
                csVersionStr,
                dotnet,
                dependencyDirs);
            var projectPath = builder.Build();
            projectPaths.Add(projectPath);
            Log.Success($"Wrote: {projectPath}");

            Log.Info("Decompiling...");
            var phase = new DecompilerPhase(
                target.AssemblyPath,
                result.Mapped,
                outputDir,
                csVersionStr,
                dotnet,
                dependencyDirs,
                target.PdbPath);
            phase.Run();

            Log.Success($"Done: {outputDir}");
        }

        Log.Info("Writing solution...");
        var solutionPath = new RecoveredSolutionFileBuilder(output, solution, projectPaths).Build();
        Log.Success($"Wrote: {solutionPath}");

        Log.Success("All Done!");
    }

    private static List<TargetProject> ValidateAndResolveTargets(string inputFolder)
    {
        if (!Directory.Exists(inputFolder))
        {
            Log.Error($"Input folder not found: {inputFolder}");
            Log.Shutdown();
            Environment.Exit(1);
        }

        var targets = Directory.GetFiles(inputFolder)
            .AsValueEnumerable()
            .Where(IsTargetAssemblyPath)
            .Where(HasManagedMetadata)
            .Select(p => (
                AssemblyPath: p,
                PdbPath: Path.ChangeExtension(p, ".pdb"),
                Name: Path.GetFileNameWithoutExtension(p)
            ))
            .Where(t => File.Exists(t.PdbPath))
            .ToList();

        if (targets.Count == 0)
        {
            Log.Error($"No target managed assemblies with matching pdbs found in {inputFolder}");
            Log.Shutdown();
            Environment.Exit(1);
        }

        var projectNames = targets
            .AsValueEnumerable()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetReferences = targets
            .AsValueEnumerable()
            .Select(t => (
                t.Name,
                References: ReadAssemblyReferenceNames(t.AssemblyPath)
                    .Where(name => !projectNames.Contains(name))
                    .ToList()))
            .ToDictionary(t => t.Name, t => t.References, StringComparer.OrdinalIgnoreCase);

        return targets
            .AsValueEnumerable()
            .Select(t => new TargetProject(
                t.AssemblyPath,
                t.PdbPath,
                t.Name,
                BuildProjectRefs(t.AssemblyPath, t.Name, projectNames, targetReferences)))
            .ToList();
    }

    private static bool IsTargetAssemblyPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasManagedMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    private static List<ProjectReferenceInfo> BuildProjectRefs(
        string dllPath,
        string projectName,
        IReadOnlySet<string> projectNames,
        IReadOnlyDictionary<string, List<string>> targetReferences) =>
        ReadAssemblyReferenceNames(dllPath)
            .Where(projectNames.Contains)
            .Where(name => !string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProjectReferenceInfo(
                name,
                Path.Combine("..", name, $"{name}.csproj"),
                targetReferences.TryGetValue(name, out var dependencies) ? dependencies : []))
            .ToList();

    private static List<string> ReadAssemblyReferenceNames(string dllPath)
    {
        var file = new PEFile(dllPath);

        return file.Metadata.AssemblyReferences
            .AsValueEnumerable()
            .Select(handle => file.Metadata.GetString(file.Metadata.GetAssemblyReference(handle).Name))
            .ToList();
    }

    private sealed record TargetProject(
        string AssemblyPath,
        string PdbPath,
        string Name,
        List<ProjectReferenceInfo> ProjectRefs);
}
