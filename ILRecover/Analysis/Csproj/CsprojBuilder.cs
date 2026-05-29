using System.Reflection.Metadata;
using System.Text;
using ICSharpCode.Decompiler.Metadata;
using ILRecover.Helpers;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.Analysis.Csproj;

public class CsprojBuilder(
    string dllPath,
    string outputDir,
    string projectName,
    string sdk,
    string depsFolder,
    List<ProjectRefEntry> knownProjectRefs,
    string? overrideCsVersion = null,
    IReadOnlyList<string>? dependencyDirs = null)
{
    public void Build()
    {
        var file = new PEFile(dllPath);
        var reader = file.Metadata;

        var targetFramework = ReadTargetFramework(reader);
        var rootNamespace = ReadRootNamespace(reader);
        var nullable = DetectNullable(reader);
        var csVersion = overrideCsVersion ?? DetectCsVersion(targetFramework);

        var assemblyRefs = file.Metadata.AssemblyReferences
            .AsValueEnumerable()
            .Select(h => file.Metadata.GetString(file.Metadata.GetAssemblyReference(h).Name))
            .ToList();

        var projectRefNames = knownProjectRefs
            .AsValueEnumerable()
            .Select(p => p.AssemblyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referenceSearchDirs = dependencyDirs is { Count: > 0 }
            ? dependencyDirs.ToList()
            : [depsFolder];

        var classified = DllClassifier.Classify(assemblyRefs, projectRefNames, referenceSearchDirs);

        var hints = classified.AsValueEnumerable().Where(r => r.Kind == ReferenceKind.Hint).ToList();
        var projRefs = knownProjectRefs
            .AsValueEnumerable()
            .Where(p => classified.AsValueEnumerable()
                .Any(r => r.AssemblyName == p.AssemblyName && r.Kind == ReferenceKind.ProjectRef))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"<Project Sdk=\"{sdk}\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");
        if (!string.IsNullOrEmpty(rootNamespace))
            sb.AppendLine($"    <RootNamespace>{rootNamespace}</RootNamespace>");
        sb.AppendLine($"    <AssemblyName>{projectName}</AssemblyName>");
        sb.AppendLine($"    <Nullable>{(nullable ? "enable" : "disable")}</Nullable>");
        sb.AppendLine($"    <LangVersion>{csVersion}</LangVersion>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("  </PropertyGroup>");

        if (projRefs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var pr in projRefs)
                sb.AppendLine($"    <ProjectReference Include=\"{pr.RelativeCsprojPath}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        if (hints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var hr in hints.AsValueEnumerable().OrderBy(h => h.AssemblyName))
            {
                var hintPath = Path.GetRelativePath(outputDir, hr.Path!);

                sb.AppendLine($"    <Reference Include=\"{hr.AssemblyName}\">");
                sb.AppendLine($"      <HintPath>{hintPath}</HintPath>");
                sb.AppendLine("    </Reference>");
            }

            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{projectName}.csproj");
        File.WriteAllText(outputPath, sb.ToString());
        Log.Info($"Wrote: {outputPath}");
    }

    private static string DetectCsVersion(string tfm) =>
        tfm switch
        {
            _ when tfm.StartsWith("net10") => "14",
            _ when tfm.StartsWith("net9") => "13",
            _ when tfm.StartsWith("net8") => "12",
            _ when tfm.StartsWith("net7") => "11",
            _ when tfm.StartsWith("net6") => "10",
            _ when tfm.StartsWith("net5") => "9",
            _ when tfm.StartsWith("netcoreapp3") => "8",
            _ => "latest"
        };

    private static string ReadTargetFramework(MetadataReader reader)
    {
        var value = reader.GetAssemblyDefinition()
            .GetCustomAttributes()
            .AsValueEnumerable()
            .Select(reader.GetCustomAttribute)
            .Where(attr => attr.Constructor.Kind == HandleKind.MemberReference)
            .Select(attr => new
            {
                attr,
                memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor)
            })
            .Where(x => x.memberRef.Parent.Kind == HandleKind.TypeReference)
            .Select(x => new
            {
                x.attr,
                typeRef = reader.GetTypeReference((TypeReferenceHandle)x.memberRef.Parent)
            })
            .Where(x => reader.GetString(x.typeRef.Name) == "TargetFrameworkAttribute")
            .Select(x => x.attr.DecodeValue(new StringAttributeDecoder()))
            .FirstOrDefault(v => v.FixedArguments.Length > 0);

        return value is { }
            ? ConvertTfm(value.FixedArguments[0].Value?.ToString() ?? "")
            : "net6.0";
    }

    private static string ConvertTfm(string raw) =>
        raw switch
        {
            _ when raw.StartsWith(".NETCoreApp,Version=v") =>
                "net" + raw[".NETCoreApp,Version=v".Length..],
            _ when raw.StartsWith(".NETStandard,Version=v") =>
                "netstandard" + raw[".NETStandard,Version=v".Length..],
            _ when raw.StartsWith(".NETFramework,Version=v") =>
                "net" + raw[".NETFramework,Version=v".Length..].Replace(".", ""),
            _ => raw
        };

    private static string ReadRootNamespace(MetadataReader reader)
    {
        var namespaces = reader.TypeDefinitions
            .AsValueEnumerable()
            .Select(h => reader.GetString(reader.GetTypeDefinition(h).Namespace))
            .Where(ns => !string.IsNullOrEmpty(ns))
            .ToHashSet();

        if (namespaces.Count == 0) return "";

        var parts = namespaces.First().Split('.').ToList();
        foreach (var ns in namespaces.Skip(1))
        {
            var segs = ns.Split('.');
            var i = 0;
            while (i < parts.Count && i < segs.Length && parts[i] == segs[i]) i++;
            parts = parts[..i];
            if (parts.Count == 0) break;
        }

        return string.Join('.', parts);
    }

    private static bool DetectNullable(MetadataReader reader) =>
        reader.GetAssemblyDefinition().GetCustomAttributes()
            .AsValueEnumerable()
            .Select(reader.GetCustomAttribute)
            .Where(attr => attr.Constructor.Kind == HandleKind.MemberReference)
            .Select(attr => reader.GetMemberReference((MemberReferenceHandle)attr.Constructor))
            .Where(memberRef => memberRef.Parent.Kind == HandleKind.TypeReference)
            .Select(memberRef => reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent))
            .Any(typeRef => reader.GetString(typeRef.Name) is "NullableContextAttribute" or "NullableAttribute");
}