using ILRecover.Models;
using ZLinq;

namespace ILRecover.Analysis.Csproj;

public record ClassifiedReference(string AssemblyName, ReferenceKind Kind, string? Path = null);

public static class DllClassifier
{
    public static List<ClassifiedReference> Classify(
        IEnumerable<string> assemblyRefs,
        HashSet<string> projectDlls,
        IEnumerable<string> searchFolders)
    {
        var availableHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in searchFolders.AsValueEnumerable().Where(Directory.Exists))
        foreach (var path in Directory.GetFiles(folder, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            availableHints.TryAdd(name, path);
        }

        var results = new List<ClassifiedReference>();

        foreach (var name in assemblyRefs)
        {
            if (projectDlls.Contains(name))
            {
                results.Add(new ClassifiedReference(name, ReferenceKind.ProjectRef));
                continue;
            }

            if (availableHints.GetValueOrDefault(name) is { } hintPath)
            {
                results.Add(new ClassifiedReference(name, ReferenceKind.Hint, hintPath));
                continue;
            }

            results.Add(new ClassifiedReference(name, ReferenceKind.Skip));
        }

        return results;
    }
}