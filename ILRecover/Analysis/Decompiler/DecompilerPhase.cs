using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ILRecover.Helpers;
using ILRecover.Models;
using ILRecover.Pdb;
using ZLinq;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase(
    string dllPath,
    IReadOnlyList<SourceFileMap> mapped,
    string outputDir,
    string? csVersion = null,
    IReadOnlyList<string>? dependencySearchDirs = null,
    string? editorConfigPath = null,
    string? pdbPath = null)
{
    private readonly string _assemblyName = Path.GetFileNameWithoutExtension(dllPath);
    private readonly string? _editorConfigPath = string.IsNullOrWhiteSpace(editorConfigPath) ? null : Path.GetFullPath(editorConfigPath);
    private IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference>? _formattingReferences;
    private Dictionary<string, HashSet<string>>? _typeNamespaceIndex;
    private Dictionary<string, HashSet<string>>? _preferredTypeNamespaceIndex;
    private Dictionary<string, HashSet<string>>? _typeNamespaceIndexByArity;
    private Dictionary<string, HashSet<string>>? _preferredTypeNamespaceIndexByArity;

    public void Run()
    {
        using var debugInfoProvider = BuildDebugInfoProvider();
        var decompiler = BuildDecompiler(debugInfoProvider);
        var userFiles = ExpandFilesWithGeneratedCompanions(mapped);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in userFiles)
        {
            if (file.Methods.Count == 0) continue;

            var normalizedRelativePath = NormalizeOutputRelativePath(file.RelativePath);
            var outputPath = Path.Combine(outputDir, normalizedRelativePath);
            if (written.Contains(outputPath)) continue;

            try
            {
                var source = DecompileFile(decompiler, file);
                if (source is null) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, source);
                written.Add(outputPath);

                Log.Info(normalizedRelativePath);
            }
            catch (Exception ex)
            {
                Log.Error($"Skip: {normalizedRelativePath} ({ex.Message})");
            }
        }
    }

    private List<SourceFileMap> ExpandFilesWithGeneratedCompanions(IReadOnlyList<SourceFileMap> sourceFiles)
    {
        var generatedCompanionsByType = sourceFiles
            .AsValueEnumerable()
            .Where(file => file.IsGenerated && file.OriginalPath.Contains("/Generated/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => file.TypeFullNames
                .AsValueEnumerable()
                .Distinct(StringComparer.Ordinal)
                .Select(typeFullName => (typeFullName, file)))
            .GroupBy(pair => pair.typeFullName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.file).Distinct().ToList(),
                StringComparer.Ordinal);

        var expanded = new List<SourceFileMap>();

        foreach (var file in sourceFiles.Where(file => !file.IsGenerated))
        {
            var methods = file.Methods.ToList();

            foreach (var typeFullName in file.TypeFullNames)
            {
                if (!generatedCompanionsByType.TryGetValue(typeFullName, out var companions))
                    continue;

                foreach (var companion in companions)
                    methods.AddRange(companion.Methods);
            }

            expanded.Add(file with
            {
                Methods = methods
                    .DistinctBy(method => method.MethodHandle)
                    .ToList()
            });
        }

        return expanded;
    }

    private string NormalizeOutputRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return relativePath;

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var prefix = _assemblyName + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];

        return normalized;
    }

    private string? DecompileFile(CSharpDecompiler decompiler, SourceFileMap file)
    {
        var methodsByType = file.Methods
            .AsValueEnumerable()
            .Where(method => !IsCompilerGenerated(method.TypeFullName))
            .GroupBy(method => GetRootTypeName(method.TypeFullName), StringComparer.Ordinal)
            .ToList();

        if (methodsByType.Count == 0) return null;

        SyntaxTree? combinedTree = null;

        foreach (var typeGroup in methodsByType)
        {
            var filteredTree = SliceTypeForFile(decompiler, file, typeGroup.Key);
            if (filteredTree is null)
                continue;

            combinedTree = combinedTree is null
                ? filteredTree
                : MergeSyntaxTrees(combinedTree, filteredTree);
        }

        if (combinedTree is null)
            return null;

        ResolveFileLocalUsings(combinedTree, decompiler);
        var source = SyntaxTreeToString(combinedTree);
        return PostProcessSource(file, source);
    }

    private SyntaxTree? SliceTypeForFile(
        CSharpDecompiler decompiler,
        SourceFileMap file,
        string typeName)
    {
        try
        {
            return decompiler.DecompileTypeToSourceDocumentSlices(
                    new FullTypeName(typeName),
                    BuildSourceDocumentSliceRequests(typeName))
                .FirstOrDefault(slice => slice.DocumentUrl.Equals(file.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?.SyntaxTree;
        }
        catch
        {
            return null;
        }
    }

    private static string GetRootTypeName(string typeFullName)
    {
        var nestedIdx = typeFullName.IndexOf('+');
        return nestedIdx >= 0 ? typeFullName[..nestedIdx] : typeFullName;
    }

    private List<SourceDocumentSliceRequest> BuildSourceDocumentSliceRequests(string rootTypeName)
    {
        return mapped
            .Where(file => file.TypeFullNames.Any(typeName => string.Equals(GetRootTypeName(typeName), rootTypeName, StringComparison.Ordinal)))
            .Select(file => new SourceDocumentSliceRequest(
                file.RelativePath.Replace('\\', '/'),
                file.Methods
                    .AsValueEnumerable()
                    .Where(method => string.Equals(GetRootTypeName(method.TypeFullName), rootTypeName, StringComparison.Ordinal))
                    .Select(method => (EntityHandle)method.MethodHandle)
                    .ToList(),
                file.IsGenerated))
            .GroupBy(request => request.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceDocumentSliceRequest(
                group.Key,
                group.SelectMany(request => request.MemberHandles).Distinct().ToList(),
                group.All(request => request.IsGenerated)))
            .ToList();
    }

    private CSharpDecompiler BuildDecompiler(IDebugInfoProvider? debugInfoProvider)
    {
        var langVersion = csVersion switch
        {
            "14" or "latest" => LanguageVersion.Latest,
            "13" => LanguageVersion.CSharp13_0,
            "12" => LanguageVersion.CSharp12_0,
            "11" => LanguageVersion.CSharp11_0,
            "10" => LanguageVersion.CSharp10_0,
            "9" => LanguageVersion.CSharp9_0,
            "8" => LanguageVersion.CSharp8_0,
            _ => LanguageVersion.Latest
        };

        var settings = new DecompilerSettings(langVersion)
        {
            ThrowOnAssemblyResolveErrors = false,
            RemoveDeadCode = false,
            RemoveDeadStores = false,
            UseDebugSymbols = debugInfoProvider is not null
        };

        var file = new PEFile(dllPath);
        var resolver = new UniversalAssemblyResolver(dllPath, false, file.DetectTargetFrameworkId());
        AddResolverSearchDirectories(resolver);

        return new CSharpDecompiler(dllPath, resolver, settings)
        {
            DebugInfoProvider = debugInfoProvider
        };
    }

    private PortablePdbDebugInfoProvider? BuildDebugInfoProvider()
    {
        if (string.IsNullOrWhiteSpace(pdbPath) || !File.Exists(pdbPath))
            return null;

        try
        {
            return new PortablePdbDebugInfoProvider(dllPath, pdbPath);
        }
        catch
        {
            return null;
        }
    }

    private void AddResolverSearchDirectories(UniversalAssemblyResolver resolver)
    {
        var dllDirectory = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(dllDirectory) && Directory.Exists(dllDirectory))
            resolver.AddSearchDirectory(dllDirectory);

        if (dependencySearchDirs is null || dependencySearchDirs.Count == 0)
        {
            Log.Warning("No dependency search directories were provided. Use -dp to resolve referenced assemblies");
            return;
        }

        foreach (var dependencyDir in dependencySearchDirs)
        {
            if (string.IsNullOrWhiteSpace(dependencyDir))
                continue;

            var fullPath = Path.GetFullPath(dependencyDir);
            if (Directory.Exists(fullPath))
                resolver.AddSearchDirectory(fullPath);
            else
                Log.Warning($"Dependency search directory not found: {fullPath}");
        }
    }

    private static bool IsCompilerGenerated(string typeName) => typeName.StartsWith('<') || typeName.Contains("+<");

    private sealed record MethodDebugSignature(
        string TypeFullName,
        string MethodName,
        int ParameterCount,
        bool IsConstructor
    );
}
