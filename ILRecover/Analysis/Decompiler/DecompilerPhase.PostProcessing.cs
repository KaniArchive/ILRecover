using System.Text.RegularExpressions;
using ILRecover.Models;
using ZLinq;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static string RemoveIlComments(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        return lines.AsValueEnumerable()
            .Where(line => !line.TrimStart().StartsWith("//IL_", StringComparison.Ordinal))
            .JoinToString(Environment.NewLine);
    }

    private static string NormalizeTopLevelSpacing(string source) =>
        ModifierRegex().Replace(source, "}" + Environment.NewLine + Environment.NewLine);

    private string PostProcessSource(SourceFileMap file, string source)
    {
        source = RemoveDecompilerNoiseAttributes(source);

        if (Path.GetFileName(file.RelativePath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
            source = RewriteCompilerGeneratedProgram(source);

        source = RestoreUsingDirectives(source);
        source = FormatSource(file, source);
        return FixCollapsedVarDeclarations(source);
    }

    private static string RemoveDecompilerNoiseAttributes(string source) =>
        NoiseAttributeRegex().Replace(source, string.Empty);

    private static string RewriteCompilerGeneratedProgram(string source)
    {
        var signatureMatch = SignatureRegex().Match(source);

        if (!signatureMatch.Success)
            return source;

        var bodyStart = source.IndexOf('{', signatureMatch.Index);
        if (bodyStart < 0)
            return source;

        var bodyEnd = FindMatchingBrace(source, bodyStart);
        if (bodyEnd < 0)
            return source;

        var body = source[(bodyStart + 1)..bodyEnd];
        body = UnindentOneLevel(body).Trim();

        return body + Environment.NewLine;
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '{':
                    depth++;
                    break;
                case '}':
                {
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
                }
            }
        }

        return -1;
    }

    private static string UnindentOneLevel(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith('\t'))
                lines[i] = lines[i][1..];
            else if (lines[i].StartsWith("    ", StringComparison.Ordinal))
                lines[i] = lines[i][4..];

        return string.Join(Environment.NewLine, lines);
    }

    private static string FixCollapsedVarDeclarations(string source)
    {
        source = CollapsedUsingVarDoubleIdentifierRegex().Replace(source, "using var $1 =");
        source = CollapsedVarDoubleIdentifierRegex().Replace(source, "var $1 =");
        source = CollapsedUsingVarRegex().Replace(source, "using var ");
        source = CollapsedVarDeclarationRegex().Replace(source, "var $1 =");
        return source;
    }

    [GeneratedRegex(@"private\s+static\s+async\s+Task\s+<Main>\$\s*\(string\[\]\s+args\)", RegexOptions.Multiline)]
    private static partial Regex SignatureRegex();

    [GeneratedRegex(
        @"\}(\r?\n)(?=(\[|public\b|internal\b|protected\b|private\b|sealed\b|abstract\b|static\b|partial\b|class\b|struct\b|interface\b|enum\b|record\b))")]
    private static partial Regex ModifierRegex();

    [GeneratedRegex(
        @"^\s*\[(assembly|module)\s*:\s*((global::)?System\.Runtime\.CompilerServices\.)?(CompilerGenerated(Attribute)?|Nullable(Attribute|ContextAttribute|PublicOnlyAttribute)|RefSafetyRulesAttribute|EmbeddedAttribute)(\([^\]]*\))?\]\s*\r?\n|^\s*\[((global::)?System\.Runtime\.CompilerServices\.)?(CompilerGenerated(Attribute)?|Nullable(Attribute|ContextAttribute|PublicOnlyAttribute)|RefSafetyRulesAttribute|EmbeddedAttribute)(\([^\]]*\))?\]\s*\r?\n",
        RegexOptions.Multiline)]
    private static partial Regex NoiseAttributeRegex();

    [GeneratedRegex(@"\busing\s+var(?=[A-Za-z_])", RegexOptions.Multiline)]
    private static partial Regex CollapsedUsingVarRegex();

    [GeneratedRegex(@"\bvar([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedVarDeclarationRegex();

    [GeneratedRegex(@"\busing\s+var\s+([A-Za-z_][A-Za-z0-9_]*)\s+[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedUsingVarDoubleIdentifierRegex();

    [GeneratedRegex(@"\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s+[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedVarDoubleIdentifierRegex();
}
