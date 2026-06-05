using ICSharpCode.Decompiler.CSharp.Syntax;
using ILRecover.Models;

namespace ILRecover.Analysis.SourceGen;

internal static partial class SourceGenNormalizer
{
    private static bool ShouldMergeMessagePackGeneratedCompanion(SourceFileMap file)
    {
        return !SourceGenPaths.IsMessagePackGeneratorPath(file.OriginalPath);
    }

    private static void NormalizeMessagePack(SyntaxTree tree)
    {
    }
}
