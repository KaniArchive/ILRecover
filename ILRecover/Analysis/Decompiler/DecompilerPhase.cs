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
        var preparedTypes = new Dictionary<string, DecompiledTypeCacheEntry>(StringComparer.Ordinal);

        var splitTypeNames = mapped
            .AsValueEnumerable()
            .Where(f => !f.IsGenerated)
            .SelectMany(f => f.TypeFullNames
                .AsValueEnumerable()
                .Select(GetRootTypeName)
                .Distinct(StringComparer.Ordinal))
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in userFiles)
        {
            if (file.Methods.Count == 0) continue;

            var normalizedRelativePath = NormalizeOutputRelativePath(file.RelativePath);
            var outputPath = Path.Combine(outputDir, normalizedRelativePath);
            if (written.Contains(outputPath)) continue;

            try
            {
                var source = DecompileFile(decompiler, file, splitTypeNames, preparedTypes);
                if (source is null) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, source);
                written.Add(outputPath);

                Log.Info(normalizedRelativePath);
            }
            catch (Exception ex)
            {
                Log.Error($"skip: {normalizedRelativePath} ({ex.Message})");
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

    private string? DecompileFile(
        CSharpDecompiler decompiler,
        SourceFileMap file,
        IReadOnlySet<string> splitTypeNames,
        IDictionary<string, DecompiledTypeCacheEntry> preparedTypes)
    {
        if (file.Methods.Count == 0)
            return DecompileTypeOnlyFile(decompiler, file, splitTypeNames, preparedTypes);

        var methodsByType = file.Methods
            .AsValueEnumerable()
            .Where(m => !IsCompilerGenerated(m.TypeFullName))
            .GroupBy(m => GetRootTypeName(m.TypeFullName), StringComparer.Ordinal)
            .ToList();

        if (methodsByType.Count == 0) return null;

        SyntaxTree? combinedTree = null;

        foreach (var typeGroup in methodsByType)
        {
            var typeName = typeGroup.Key;
            var selectedTokens = typeGroup
                .AsValueEnumerable()
                .Select(m => (EntityHandle)m.MethodHandle)
                .ToHashSet();

            var fullTypeTree = TryDecompileType(decompiler, preparedTypes, typeName);
            if (fullTypeTree is null) continue;

            var filteredTree = FilterTypeTree(
                fullTypeTree,
                selectedTokens,
                splitTypeNames.Contains(typeName),
                ShouldPreserveSharedTypeMembers(file.RelativePath, typeName));
            if (filteredTree is null) continue;

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

    private string? DecompileTypeOnlyFile(
        CSharpDecompiler decompiler,
        SourceFileMap file,
        IReadOnlySet<string> splitTypeNames,
        IDictionary<string, DecompiledTypeCacheEntry> preparedTypes)
    {
        var candidateTypeNames = FindTypesForRelativePath(file.RelativePath).ToList();
        if (candidateTypeNames.Count == 0)
            return null;

        SyntaxTree? combinedTree = null;
        foreach (var typeName in candidateTypeNames)
        {
            var fullTypeTree = TryDecompileType(decompiler, preparedTypes, typeName);
            if (fullTypeTree is null)
                continue;

            var filteredTree = FilterTypeTree(
                fullTypeTree,
                [],
                splitTypeNames.Contains(typeName),
                preserveSharedTypeMembers: true);
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

    private static SyntaxTree? TryDecompileType(
        CSharpDecompiler decompiler,
        IDictionary<string, DecompiledTypeCacheEntry> preparedTypes,
        string typeFullName)
    {
        try
        {
            if (!preparedTypes.TryGetValue(typeFullName, out var entry))
            {
                entry = decompiler.DecompileTypeForSlicing(new FullTypeName(typeFullName));
                preparedTypes[typeFullName] = entry;
            }

            return entry.CreateClone();
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

    private IEnumerable<string> FindTypesForRelativePath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        return mapped
            .Where(file => file.RelativePath.Replace('\\', '/').Equals(normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => file.DeclaredTypeFullNames ?? file.TypeFullNames)
            .Distinct(StringComparer.Ordinal);
    }

    private bool ShouldPreserveSharedTypeMembers(string relativePath, string rootTypeName)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        return string.Equals(normalizedRelativePath, GetSharedTypeMemberCarrierPath(rootTypeName), StringComparison.OrdinalIgnoreCase);
    }

    private string? GetSharedTypeMemberCarrierPath(string rootTypeName)
    {
        var simpleTypeName = rootTypeName[(rootTypeName.LastIndexOf('.') + 1)..];
        var candidates = mapped
            .Where(file => !file.IsGenerated
                           && file.TypeFullNames.Any(typeName => string.Equals(GetRootTypeName(typeName), rootTypeName, StringComparison.Ordinal)))
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates
            .OrderByDescending(path => path.EndsWith('/' + simpleTypeName + ".cs", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(path, simpleTypeName + ".cs", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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
        if (string.IsNullOrWhiteSpace(dllDirectory))
            return;

        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            dllDirectory
        };

        var parentDirectory = Directory.GetParent(dllDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            candidateDirs.Add(parentDirectory);
            candidateDirs.Add(Path.Combine(parentDirectory, "OR"));
            candidateDirs.Add(Path.Combine(parentDirectory, "Source"));
        }

        foreach (var candidateDir in candidateDirs.Where(Directory.Exists))
            resolver.AddSearchDirectory(candidateDir);

        if (dependencySearchDirs is null)
            return;

        foreach (var dependencyDir in dependencySearchDirs)
        {
            if (string.IsNullOrWhiteSpace(dependencyDir))
                continue;

            var fullPath = Path.GetFullPath(dependencyDir);
            if (Directory.Exists(fullPath))
                resolver.AddSearchDirectory(fullPath);
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
