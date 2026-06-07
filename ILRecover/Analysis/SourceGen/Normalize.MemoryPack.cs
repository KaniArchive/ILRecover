using ICSharpCode.Decompiler.CSharp.Syntax;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.Analysis.SourceGen;

internal static partial class SourceGenNormalizer
{
    private static bool ShouldMergeMemoryPackGeneratedCompanion(SourceFileMap file) =>
        !SourceGenPaths.IsMemoryPackGeneratorPath(file.OriginalPath);

    private static void NormalizeMemoryPack(SyntaxTree tree)
    {
        foreach (var type in tree.Descendants.OfType<TypeDeclaration>().ToList())
        {
            if (!IsMemoryPackType(type))
                continue;

            RemoveBaseTypes(type, "IMemoryPackable", "IMemoryPackFormatterRegister");

            foreach (var nestedType in type.Members.OfType<TypeDeclaration>().ToList())
                if (nestedType.BaseTypes.Any(baseType => AstTypeContains(baseType, "MemoryPackFormatter")))
                    nestedType.Remove();
        }
    }

    private static bool IsMemoryPackType(TypeDeclaration type) =>
        HasAttribute(type, "MemoryPackable")
        || type.BaseTypes.Any(baseType =>
            AstTypeContains(baseType, "IMemoryPackable")
            || AstTypeContains(baseType, "IMemoryPackFormatterRegister"));

    private static void RemoveBaseTypes(TypeDeclaration type, params string[] typeNames)
    {
        foreach (var baseType in type.BaseTypes.ToList()
                     .AsValueEnumerable()
                     .Where(baseType => typeNames.Any(typeName => AstTypeContains(baseType, typeName))))
            baseType.Remove();
    }

    private static bool HasAttribute(EntityDeclaration declaration, string name) =>
        declaration.Attributes
            .AsValueEnumerable()
            .SelectMany(section => section.Attributes)
            .Any(attribute => AstTypeContains(attribute.Type, name));

    private static bool AstTypeContains(AstType type, string name) =>
        type.ToString().Contains(name, StringComparison.Ordinal);
}