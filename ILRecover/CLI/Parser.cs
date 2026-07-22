using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ILRecover.Analysis;
using ILRecover.Analysis.Decompiler;
using ILRecover.Helpers;
using ILRecover.Models;
using ILRecover.Pdb;

namespace ILRecover.CLI;

public static class Parser
{
    public static void Execute(
        string input,
        string output,
        int csVersion,
        string[]? dependencies,
        string? solution,
        string? dotnet,
        string[]? shift,
        int shiftMax,
        bool classOwnFile,
        bool allowUnmapped,
        string? sourcePaths,
        bool externalPriority)
    {
        if (shiftMax <= 0)
            Fail("--shift-max must be greater than 0");

        var options = new RecoverOptions(
            input,
            output,
            csVersion > 0 ? csVersion.ToString() : null,
            dependencies ?? [],
            solution,
            dotnet,
            new PdbMethodRemapOptions(shift ?? [], shiftMax),
            classOwnFile
                ? new SourceOwnershipOptions(true, allowUnmapped, sourcePaths, externalPriority)
                : SourceOwnershipOptions.Disabled with
                {
                    SourcePathListPath = sourcePaths,
                    ExternalPriority = externalPriority
                });

        Execute(options);
    }

    private static void Execute(RecoverOptions options)
    {
        var sourceOwnership = new SourceOwnershipService(options.SourceOwnership);
        sourceOwnership.WarnIgnoredOptions();

        var targets = new TargetProjectResolver(options).Resolve();
        var projectPaths = new List<string>();

        foreach (var target in targets)
            projectPaths.Add(RecoverTarget(options, sourceOwnership, target));

        Log.Info("Writing solution...");
        var solutionPath = new RecoveredSolutionFileBuilder(options.Output, options.SolutionName, projectPaths).Build();
        Log.Success($"Wrote: {solutionPath}");

        Log.Success("All Done!");
    }

    private static string RecoverTarget(
        RecoverOptions options,
        SourceOwnershipService sourceOwnership,
        TargetProject target)
    {
        Log.Info($"{target.Name}");

        var outputDir = target.CreateOutputDirectory(options.Output);
        CleanOutputDirectory(outputDir);

        var mapped = AnalyzeTarget(options, sourceOwnership, target);

        Log.Info("Writing csproj...");
        var projectPath = new RecoveredProjectFileBuilder(
            target.AssemblyPath,
            outputDir,
            target.Name,
            target.ProjectRefs,
            options.CSharpVersion,
            options.DotNetVersion,
            options.DependencySearchDirs).Build();
        Log.Success($"Wrote: {projectPath}");

        Log.Info("Decompiling...");
        new DecompilerPhase(target, mapped, outputDir, options).Run();

        Log.Success($"Done: {outputDir}");
        return projectPath;
    }

    private static IReadOnlyList<SourceFileMap> AnalyzeTarget(
        RecoverOptions options,
        SourceOwnershipService sourceOwnership,
        TargetProject target)
    {
        Log.Info("Analyzing...");
        var result = new AssemblyAnalyzer(target).Analyze();
        var ownershipResult = sourceOwnership.Apply(result);
        if (options.SourceOwnership.Enabled)
        {
            Log.Success(
                $"Mapped: {ownershipResult.Mapped.Count} Skipped: {result.Skipped.Count} Rejected: {ownershipResult.RejectedTypeCount} Unmapped: {ownershipResult.UnmappedTypeCount}");
        }
        else
        {
            Log.Success($"Mapped: {ownershipResult.Mapped.Count} Skipped: {result.Skipped.Count}");
        }

        return ownershipResult.Mapped;
    }

    private static void CleanOutputDirectory(string outputDir)
    {
        if (!Directory.Exists(outputDir))
            return;

        Log.Info("Cleaning Output...");
        Directory.Delete(outputDir, true);
    }

    private static void Fail(string message)
    {
        Log.Error(message);
        Log.Shutdown();
        Environment.Exit(1);
    }
}
