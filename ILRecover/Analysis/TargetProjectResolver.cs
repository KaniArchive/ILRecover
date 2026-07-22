using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Helpers;
using ILRecover.Models;
using ILRecover.Pdb;
using ZLinq;

namespace ILRecover.Analysis;

public sealed class TargetProjectResolver(RecoverOptions options)
{
    public List<TargetProject> Resolve()
    {
        if (!Directory.Exists(options.Input))
            Fail($"Input folder not found: {options.Input}");

        var targets = Directory.GetFiles(options.Input)
            .AsValueEnumerable()
            .Where(IsTargetAssemblyPath)
            .Where(HasManagedMetadata)
            .Select(path => (
                AssemblyPath: path,
                PdbPath: Path.ChangeExtension(path, ".pdb"),
                Name: Path.GetFileNameWithoutExtension(path)
            ))
            .Where(target => File.Exists(target.PdbPath))
            .ToList();

        if (targets.Count == 0)
            Fail($"No target managed assemblies with matching pdbs found in {options.Input}");

        var projectNames = targets
            .AsValueEnumerable()
            .Select(target => target.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetReferences = targets
            .AsValueEnumerable()
            .Select(target => (
                target.Name,
                References: ReadAssemblyReferenceNames(target.AssemblyPath)
                    .Where(name => !projectNames.Contains(name))
                    .ToList()))
            .ToDictionary(target => target.Name, target => target.References, StringComparer.OrdinalIgnoreCase);

        return targets
            .AsValueEnumerable()
            .Select(target => new TargetProject(
                target.AssemblyPath,
                target.PdbPath,
                target.Name,
                BuildProjectRefs(target.AssemblyPath, target.Name, projectNames, targetReferences),
                options.PdbMethodRemapOptions.IsEnabledFor(target.Name)
                    ? options.PdbMethodRemapOptions
                    : PdbMethodRemapOptions.Disabled))
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

    private static void Fail(string message)
    {
        Log.Error(message);
        Log.Shutdown();
        Environment.Exit(1);
    }
}
