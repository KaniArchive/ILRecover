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
        var preferredLocalNames = GetPreferredCollapsedVarNames(file);
        source = RemoveDecompilerNoiseAttributes(source);
        source = RemoveInvalidUsingDirectives(source);
        source = FixInvalidRefMemberAccess(source);

        if (Path.GetFileName(file.RelativePath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
            source = RewriteCompilerGeneratedProgram(source);

        source = RestoreUsingDirectives(source);
        source = FixCollapsedVarDeclarations(source, preferredLocalNames);
        source = FormatSource(file, source);
        source = FixCollapsedVarDeclarations(source, preferredLocalNames);
        source = FixInvalidRefMemberAccess(source);
        return RemoveInvalidUsingDirectives(source);
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

    private static HashSet<string> GetPreferredCollapsedVarNames(SourceFileMap file) =>
        file.Methods.AsValueEnumerable()
            .SelectMany(method => method.LocalVariables)
            .Select(local => local.Name)
            .Where(IsRestorableLocalName)
            .ToHashSet(StringComparer.Ordinal);

    private static string FixCollapsedVarDeclarations(string source, IReadOnlySet<string> preferredLocalNames)
    {
        source = CollapsedUsingVarRegex().Replace(source, "using var ");
        source = CollapsedVarDeclarationRegex().Replace(source, "$1var $2 =");
        source = CollapsedUsingVarDoubleIdentifierRegex().Replace(source,
            match => $"{match.Groups[1].Value}using var {ResolveCollapsedVarIdentifier(match.Groups[2].Value, match.Groups[3].Value, preferredLocalNames)} =");
        source = CollapsedVarDoubleIdentifierRegex().Replace(source,
            match => $"{match.Groups[1].Value}var {ResolveCollapsedVarIdentifier(match.Groups[2].Value, match.Groups[3].Value, preferredLocalNames)} =");
        return source;
    }

    private static string FixInvalidRefMemberAccess(string source) =>
        InvalidRefMemberAccessRegex().Replace(source, "$1.$2");

    private static string ResolveCollapsedVarIdentifier(
        string firstIdentifier,
        string secondIdentifier,
        IReadOnlySet<string> preferredLocalNames)
    {
        if (string.Equals(firstIdentifier, secondIdentifier, StringComparison.Ordinal))
            return firstIdentifier;

        var firstMatches = preferredLocalNames.Contains(firstIdentifier);
        var secondMatches = preferredLocalNames.Contains(secondIdentifier);

        if (firstMatches && !secondMatches)
            return firstIdentifier;

        if (!firstMatches && secondMatches)
            return secondIdentifier;

        return secondIdentifier;
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

    [GeneratedRegex(@"(^[ \t]*|[;{(][ \t]*)var([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedVarDeclarationRegex();

    [GeneratedRegex(@"(^[ \t]*|[;{(][ \t]*)using\s+var\s*([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedUsingVarDoubleIdentifierRegex();

    [GeneratedRegex(@"(^[ \t]*|[;{(][ \t]*)var\s*([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline)]
    private static partial Regex CollapsedVarDoubleIdentifierRegex();

    [GeneratedRegex(@"\(\([^)]+\)\(ref\s+([^\)]+)\)\)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex InvalidRefMemberAccessRegex();
}
