using System.Text.RegularExpressions;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static string NormalizeTopLevelSpacing(string source) =>
        ModifierRegex().Replace(source, "}" + Environment.NewLine + Environment.NewLine);

    private string PostProcessSource(string source)
    {
        source = RemoveDecompilerNoiseAttributes(source);
        source = NormalizeGeneratedMainMethodName(source);
        source = RemoveInvalidUsingDirectives(source);
        source = FixInvalidRefMemberAccess(source);

        source = FormatSource(source);
        source = FixInvalidRefMemberAccess(source);
        return RemoveInvalidUsingDirectives(source);
    }

    private static string RemoveDecompilerNoiseAttributes(string source) =>
        NoiseAttributeRegex().Replace(source, string.Empty);

    private static string NormalizeGeneratedMainMethodName(string source) =>
        GeneratedMainMethodNameRegex().Replace(source, "Main");

    private static string FixInvalidRefMemberAccess(string source) =>
        InvalidRefMemberAccessRegex().Replace(source, "$1.$2");

    [GeneratedRegex(@"<Main>\$", RegexOptions.Multiline)]
    private static partial Regex GeneratedMainMethodNameRegex();

    [GeneratedRegex(@"\}(\r?\n)(?=(\[|public\b|internal\b|protected\b|private\b|sealed\b|abstract\b|static\b|partial\b|class\b|struct\b|interface\b|enum\b|record\b))")]
    private static partial Regex ModifierRegex();

    [GeneratedRegex(@"^\s*\[(assembly|module)\s*:\s*((global::)?System\.Runtime\.CompilerServices\.)?(CompilerGenerated(Attribute)?|Nullable(Attribute|ContextAttribute|PublicOnlyAttribute)|RefSafetyRulesAttribute|EmbeddedAttribute)(\([^\]]*\))?\]\s*\r?\n|^\s*\[((global::)?System\.Runtime\.CompilerServices\.)?(CompilerGenerated(Attribute)?|Nullable(Attribute|ContextAttribute|PublicOnlyAttribute)|RefSafetyRulesAttribute|EmbeddedAttribute)(\([^\]]*\))?\]\s*\r?\n", RegexOptions.Multiline)]
    private static partial Regex NoiseAttributeRegex();

    [GeneratedRegex(@"\(\([^)]+\)\(ref\s+([^\)]+)\)\)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex InvalidRefMemberAccessRegex();
}