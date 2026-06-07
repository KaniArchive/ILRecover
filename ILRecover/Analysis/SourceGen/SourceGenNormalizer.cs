using ICSharpCode.Decompiler.CSharp.Syntax;
using ILRecover.Models;

namespace ILRecover.Analysis.SourceGen;

internal static partial class SourceGenNormalizer
{
    public static bool ShouldMergeGeneratedCompanion(SourceFileMap file) =>
        ShouldMergeMemoryPackGeneratedCompanion(file)
        && ShouldMergeMessagePackGeneratedCompanion(file);

    public static void Normalize(SyntaxTree tree)
    {
        NormalizeMemoryPack(tree);
        NormalizeMessagePack();
    }
}