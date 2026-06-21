using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX.PdbProvider;
using ILRecover.Analysis.SourceGen;
using ILRecover.Helpers;
using ILRecover.Models;
using ILRecover.Pdb;
using Microsoft.CodeAnalysis;
using ZLinq;
using SyntaxTree = ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase(
    string dllPath,
    IReadOnlyList<SourceFileMap> mapped,
    string outputDir,
    string? csVersion = null,
    string? dotnetVersion = null,
    IReadOnlyList<string>? dependencySearchDirs = null,
    string? pdbPath = null,
    bool enablePdbMethodRemapping = false)
{
    private readonly string _assemblyName = Path.GetFileNameWithoutExtension(dllPath);

    private readonly Dictionary<string, IReadOnlyDictionary<string, SyntaxTree>> _documentSliceCache =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<SourceDocumentSliceRequest>> _sliceRequestCache =
        new(StringComparer.Ordinal);

    private IReadOnlyList<MetadataReference>? _formattingReferences;
    private IReadOnlyList<SourceFileMap>? _sliceSourceFiles;

    public void Run()
    {
        var debugInfoProvider = BuildDebugInfoProvider();
        try
        {
            var decompiler = BuildDecompiler(debugInfoProvider);
            var userFiles = ExpandFilesWithGeneratedCompanions(mapped);
            _sliceSourceFiles = userFiles;

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in userFiles)
            {
                if (!file.DecompileWholeTypes &&
                    file.Methods.Count == 0 &&
                    (file.TypeDeclarations is null || file.TypeDeclarations.Count == 0)) continue;

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

                    Log.Success(normalizedRelativePath);
                }
                catch (Exception ex)
                {
                    Log.Error($"Skip: {normalizedRelativePath} ({ex.Message})");
                }
            }
        }
        finally
        {
            if (debugInfoProvider is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private List<SourceFileMap> ExpandFilesWithGeneratedCompanions(IReadOnlyList<SourceFileMap> sourceFiles)
    {
        var generatedCompanionsByType = sourceFiles
            .AsValueEnumerable()
            .Where(file => file.IsGenerated && SourceGenNormalizer.ShouldMergeGeneratedCompanion(file))
            .SelectMany(file => file.TypeFullNames
                .AsValueEnumerable()
                .Distinct(StringComparer.Ordinal)
                .Select(typeFullName => (typeFullName: GetRootTypeName(typeFullName), file)))
            .GroupBy(pair => pair.typeFullName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.file).Distinct().ToList(),
                StringComparer.Ordinal);

        var expanded = new List<SourceFileMap>();

        foreach (var file in sourceFiles.Where(file => !file.IsGenerated))
        {
            var methods = file.Methods.ToList();
            var typeDeclarations = (file.TypeDeclarations ?? []).ToList();

            foreach (var typeFullName in file.TypeFullNames.Select(GetRootTypeName).Distinct(StringComparer.Ordinal))
            {
                if (!generatedCompanionsByType.TryGetValue(typeFullName, out var companions))
                    continue;

                foreach (var companion in companions)
                {
                    methods.AddRange(companion.Methods);
                    if (companion.TypeDeclarations is not null)
                        typeDeclarations.AddRange(companion.TypeDeclarations);
                }
            }

            expanded.Add(file with
            {
                Methods = methods
                    .DistinctBy(method => method.MethodHandle)
                    .ToList(),
                TypeDeclarations = typeDeclarations
                    .DistinctBy(typeDeclaration => typeDeclaration.TypeHandle)
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
        if (file.DecompileWholeTypes)
            return DecompileWholeTypes(decompiler, file);

        var typeNames = file.TypeFullNames
            .AsValueEnumerable()
            .Where(typeName => !IsCompilerGenerated(typeName))
            .Select(GetRootTypeName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (typeNames.Count == 0) return null;

        SyntaxTree? combinedTree = null;

        foreach (var typeName in typeNames)
        {
            var filteredTree = SliceTypeForFile(decompiler, file, typeName);
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
        return PostProcessSource(source);
    }

    private string? DecompileWholeTypes(CSharpDecompiler decompiler, SourceFileMap file)
    {
        var typeNames = file.TypeFullNames
            .AsValueEnumerable()
            .Where(typeName => !IsCompilerGenerated(typeName))
            .Select(GetRootTypeName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (typeNames.Count == 0)
            return null;

        SyntaxTree? combinedTree = null;
        foreach (var typeName in typeNames)
        {
            var tree = decompiler.DecompileType(new FullTypeName(typeName));
            SourceGenNormalizer.Normalize(tree);
            combinedTree = combinedTree is null
                ? tree
                : MergeSyntaxTrees(combinedTree, tree);
        }

        if (combinedTree is null)
            return null;

        ResolveFileLocalUsings(combinedTree, decompiler);
        return PostProcessSource(SyntaxTreeToString(combinedTree));
    }

    private SyntaxTree? SliceTypeForFile(
        CSharpDecompiler decompiler,
        SourceFileMap file,
        string typeName)
    {
        try
        {
            var normalizedPath = file.RelativePath.Replace('\\', '/');
            if (!GetDocumentSlices(decompiler, typeName).TryGetValue(normalizedPath, out var tree))
                return null;

            var clone = (SyntaxTree)tree.Clone();
            SourceGenNormalizer.Normalize(clone);
            return clone;
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

    private IReadOnlyDictionary<string, SyntaxTree> GetDocumentSlices(CSharpDecompiler decompiler, string rootTypeName)
    {
        if (_documentSliceCache.TryGetValue(rootTypeName, out var cached))
            return cached;

        var slices = decompiler.DecompileTypeToSourceDocumentSlices(
                new FullTypeName(rootTypeName),
                BuildSourceDocumentSliceRequests(rootTypeName))
            .GroupBy(slice => slice.DocumentUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().SyntaxTree,
                StringComparer.OrdinalIgnoreCase);

        _documentSliceCache[rootTypeName] = slices;
        return slices;
    }

    private List<SourceDocumentSliceRequest> BuildSourceDocumentSliceRequests(string rootTypeName)
    {
        if (_sliceRequestCache.TryGetValue(rootTypeName, out var cached))
            return cached;

        var requests = (_sliceSourceFiles ?? mapped)
            .AsValueEnumerable()
            .Where(file => file.TypeFullNames.Any(typeName =>
                string.Equals(GetRootTypeName(typeName), rootTypeName, StringComparison.Ordinal)))
            .Select(file => new SourceDocumentSliceRequest(
                file.RelativePath.Replace('\\', '/'),
                file.Methods
                    .AsValueEnumerable()
                    .Where(method => string.Equals(GetRootTypeName(method.TypeFullName), rootTypeName,
                        StringComparison.Ordinal))
                    .Select(method => (EntityHandle)method.MethodHandle)
                    .ToList(),
                (file.TypeDeclarations ?? [])
                .AsValueEnumerable()
                .Where(typeDeclaration => string.Equals(GetRootTypeName(typeDeclaration.TypeFullName), rootTypeName,
                    StringComparison.Ordinal))
                .Select(typeDeclaration => (EntityHandle)typeDeclaration.TypeHandle)
                .ToList(),
                file.IsGenerated))
            .GroupBy(request => request.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceDocumentSliceRequest(
                group.Key,
                group.SelectMany(request => request.MemberHandles).Distinct().ToList(),
                group.SelectMany(request => request.TypeDeclarationHandles).Distinct().ToList(),
                group.All(request => request.IsGenerated)))
            .ToList();

        _sliceRequestCache[rootTypeName] = requests;
        return requests;
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
        var targetFrameworkId = GetTargetFrameworkId(file);
        var resolver = new UniversalAssemblyResolver(dllPath, false, targetFrameworkId);
        AddResolverSearchDirectories(resolver);

        return new CSharpDecompiler(dllPath, resolver, settings)
        {
            DebugInfoProvider = debugInfoProvider
        };
    }

    private string GetTargetFrameworkId(PEFile file)
    {
        if (string.IsNullOrWhiteSpace(dotnetVersion))
            return file.DetectTargetFrameworkId();

        var normalized = dotnetVersion.Trim();
        if (normalized.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..];

        return normalized.StartsWith(".NETCoreApp,Version=", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $".NETCoreApp,Version=v{normalized}";
    }

    private IDebugInfoProvider? BuildDebugInfoProvider()
    {
        if (string.IsNullOrWhiteSpace(pdbPath) || !File.Exists(pdbPath))
            return null;

        try
        {
            var debugInfoProvider = DebugInfoUtils.FromFile(new PEFile(dllPath), pdbPath);
            if (debugInfoProvider is null || !enablePdbMethodRemapping)
                return debugInfoProvider;

            var methodDebugMap = PdbMethodMapper.Build(dllPath, pdbPath);
            return new RemappedDebugInfoProvider(debugInfoProvider, methodDebugMap);
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
}
