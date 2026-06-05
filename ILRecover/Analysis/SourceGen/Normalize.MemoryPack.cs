using ICSharpCode.Decompiler.CSharp.Syntax;
using ILRecover.Models;

namespace ILRecover.Analysis.SourceGen;

internal static partial class SourceGenNormalizer
{
    private static bool ShouldMergeMemoryPackGeneratedCompanion(SourceFileMap file)
    {
        return !SourceGenPaths.IsMemoryPackGeneratorPath(file.OriginalPath);
    }

    private static void NormalizeMemoryPack(SyntaxTree tree)
    {
        foreach (var type in tree.Descendants.OfType<TypeDeclaration>())
        {
            if (!HasAttribute(type, "MemoryPackable"))
                continue;

            RemoveBaseTypes(type, "IMemoryPackable", "IMemoryPackFormatterRegister");

            foreach (var nestedType in type.Members.OfType<TypeDeclaration>().ToList())
            {
                if (nestedType.BaseTypes.Any(baseType => AstTypeContains(baseType, "MemoryPackFormatter")))
                    nestedType.Remove();
            }
        }
    }

    private static void RemoveBaseTypes(TypeDeclaration type, params string[] typeNames)
    {
        foreach (var baseType in type.BaseTypes.ToList())
        {
            if (typeNames.Any(typeName => AstTypeContains(baseType, typeName)))
                baseType.Remove();
        }
    }

    private static bool HasAttribute(EntityDeclaration declaration, string name)
    {
        return declaration.Attributes
            .SelectMany(section => section.Attributes)
            .Any(attribute => AstTypeContains(attribute.Type, name));
    }

    private static bool AstTypeContains(AstType type, string name) =>
        type.ToString().Contains(name, StringComparison.Ordinal);
}
