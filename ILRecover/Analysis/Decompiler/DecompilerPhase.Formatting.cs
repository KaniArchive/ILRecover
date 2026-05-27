using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Options;
using ZLinq;
using CSharpFormattingOptions = Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynCSharp = Microsoft.CodeAnalysis.CSharp;
using RoslynFormatter = Microsoft.CodeAnalysis.Formatting.Formatter;
using RoslynSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using SyntaxTree = ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree;

namespace ILRecover.Analysis.Decompiler;

public partial class DecompilerPhase
{
    private static string SyntaxTreeToString(SyntaxTree tree)
    {
        var writer = new StringWriter();
        var formatter = FormattingOptionsFactory.CreateAllman();
        tree.AcceptVisitor(new CSharpOutputVisitor(writer, formatter));
        var source = RemoveIlComments(writer.ToString());
        source = NormalizeTopLevelSpacing(source);
        return source.Trim() + Environment.NewLine;
    }

    private string FormatSource(string source)
    {
        var parseOptions = new RoslynCSharp.CSharpParseOptions(RoslynCSharp.LanguageVersion.Preview);
        var syntaxTree = RoslynCSharp.CSharpSyntaxTree.ParseText(source, parseOptions);

        if (syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return source.TrimEnd() + Environment.NewLine;

        var root = syntaxTree.GetRoot();
        root = ApplyLazySingletonSimplification(root);
        root = ApplyCallerMemberNameSimplification(root);
        root = ApplyEditorConfigDrivenSimplifications(root);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.WithOptions(ApplyEditorConfigOptions(workspace.Options));
        workspace.TryApplyChanges(solution);
        var formatted = RoslynFormatter.Format(root, workspace).ToFullString();
        return formatted.TrimEnd() + Environment.NewLine;
    }

    private SyntaxNode ApplyEditorConfigDrivenSimplifications(SyntaxNode root)
    {
        var source = root.ToFullString();
        source = SimplifyBracesFromEditorConfig(source);
        source = SimplifyExpressionBodiesFromEditorConfig(source);

        var syntaxTree = RoslynCSharp.CSharpSyntaxTree.ParseText(source);
        return syntaxTree.GetRoot();
    }

    private string SimplifyBracesFromEditorConfig(string source) =>
        !GetEditorConfigBoolean("csharp_prefer_braces", true) ? source : source;

    private string SimplifyExpressionBodiesFromEditorConfig(string source)
    {
        if (!GetEditorConfigBoolean("csharp_style_expression_bodied_methods", true))
            return source;

        source = ExpressionBodyTaskCompletedRegex().Replace(source,
            "public Task $1($2) => Task.CompletedTask;" + Environment.NewLine);

        return source;
    }

    private OptionSet ApplyEditorConfigOptions(OptionSet options)
    {
        options = options.WithChangedOption(
            new OptionKey(CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers),
            GetEditorConfigBoolean("csharp_new_line_before_members_in_object_initializers", false));

        return options;
    }

    private bool GetEditorConfigBoolean(string key, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(_editorConfigPath) || !File.Exists(_editorConfigPath))
            return defaultValue;

        foreach (var rawLine in File.ReadLines(_editorConfigPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("[", StringComparison.Ordinal))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
                continue;

            var currentKey = line[..separatorIndex].Trim();
            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
                continue;

            var rawValue = line[(separatorIndex + 1)..].Trim();
            var value = rawValue.Split(':')[0].Trim();

            if (bool.TryParse(value, out var boolValue))
                return boolValue;

            return defaultValue;
        }

        return defaultValue;
    }

    private SyntaxNode ApplyLazySingletonSimplification(SyntaxNode root)
    {
        var syntaxTree = RoslynCSharp.CSharpSyntaxTree.ParseText(root.ToFullString());
        var compilation = RoslynCSharp.CSharpCompilation.Create(
            _assemblyName + ".LazySingleton",
            [syntaxTree],
            GetFormattingReferences(),
            new RoslynCSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree, true);
        return new LazySingletonInstanceRewriter(semanticModel).Visit(syntaxTree.GetRoot());
    }

    private SyntaxNode ApplyCallerMemberNameSimplification(SyntaxNode root)
        => new CallerMemberNameConstructorRewriter().Visit(root);

    private IReadOnlyList<MetadataReference> GetFormattingReferences()
    {
        if (_formattingReferences is not null)
            return _formattingReferences;

        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
                referencePaths.Add(path);

        foreach (var directory in GetFormattingReferenceDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.GetFiles(directory, "*.dll"))
                referencePaths.Add(Path.GetFullPath(path));
        }

        referencePaths.Add(Path.GetFullPath(dllPath));

        _formattingReferences = referencePaths
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();

        return _formattingReferences;
    }

    private IEnumerable<string> GetFormattingReferenceDirectories()
    {
        var dllDirectory = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(dllDirectory))
        {
            yield return dllDirectory;

            var parentDirectory = Directory.GetParent(dllDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                yield return parentDirectory;
                yield return Path.Combine(parentDirectory, "OR");
                yield return Path.Combine(parentDirectory, "Source");
            }
        }

        foreach (var sharedFrameworkDirectory in GetSharedFrameworkDirectories())
            yield return sharedFrameworkDirectory;

        if (dependencySearchDirs is null)
            yield break;

        foreach (var dependencyDir in dependencySearchDirs)
        {
            if (string.IsNullOrWhiteSpace(dependencyDir))
                continue;

            yield return Path.GetFullPath(dependencyDir);
        }
    }

    private static IEnumerable<string> GetSharedFrameworkDirectories()
    {
        string runtimeDirectory;

        try
        {
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        }
        catch
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            yield break;

        DirectoryInfo runtimeDirectoryInfo;

        try
        {
            runtimeDirectoryInfo = new DirectoryInfo(Path.GetFullPath(runtimeDirectory));
        }
        catch
        {
            yield break;
        }

        var sharedRoot = runtimeDirectoryInfo.Parent?.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(sharedRoot) || !Directory.Exists(sharedRoot))
            yield break;

        foreach (var frameworkDirectory in Directory.EnumerateDirectories(sharedRoot))
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(frameworkDirectory))
                yield return versionDirectory;
        }
    }

    [GeneratedRegex(@"public\s+Task\s+(\w+)\(([^\)]*)\)\s*\{\s*return\s+Task\.CompletedTask;\s*\}",
        RegexOptions.Multiline)]
    private static partial Regex ExpressionBodyTaskCompletedRegex();

    private sealed class LazySingletonInstanceRewriter(SemanticModel semanticModel) : RoslynCSharp.CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitMemberAccessExpression(RoslynSyntax.MemberAccessExpressionSyntax node)
        {
            if (node.Name.Identifier.ValueText != "Instance" ||
                node.Expression is not RoslynSyntax.GenericNameSyntax genericName ||
                genericName.TypeArgumentList.Arguments.Count != 1)
                return base.VisitMemberAccessExpression(node);

            var targetTypeSyntax = genericName.TypeArgumentList.Arguments[0];
            var targetTypeSymbol = semanticModel.GetTypeInfo(targetTypeSyntax).Type;
            if (targetTypeSymbol is null || !InheritsSelfReferencingGenericBase(targetTypeSymbol, genericName.Identifier.ValueText))
                return base.VisitMemberAccessExpression(node);

            var replacementExpressionText = targetTypeSyntax.ToString();
            var rewrittenNode = (RoslynSyntax.MemberAccessExpressionSyntax)(base.VisitMemberAccessExpression(node) ?? node);

            return RoslynCSharp.SyntaxFactory.MemberAccessExpression(
                RoslynCSharp.SyntaxKind.SimpleMemberAccessExpression,
                RoslynCSharp.SyntaxFactory.ParseExpression(replacementExpressionText),
                rewrittenNode.OperatorToken,
                rewrittenNode.Name);
        }

        private static bool InheritsSelfReferencingGenericBase(ITypeSymbol typeSymbol, string genericBaseName)
        {
            for (var baseType = typeSymbol.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (baseType.Name != genericBaseName || baseType.TypeArguments.Length != 1)
                    continue;

                return SymbolEqualityComparer.Default.Equals(baseType.TypeArguments[0], typeSymbol);
            }

            return false;
        }
    }

    private sealed class CallerMemberNameConstructorRewriter : RoslynCSharp.CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitConstructorDeclaration(RoslynSyntax.ConstructorDeclarationSyntax node)
        {
            node = (RoslynSyntax.ConstructorDeclarationSyntax)(base.VisitConstructorDeclaration(node) ?? node);

            var initializer = node.Initializer;
            if (initializer is null || initializer.ArgumentList.Arguments.Count == 0)
                return node;

            var lastArgument = initializer.ArgumentList.Arguments.Last();
            if (lastArgument.Expression is not RoslynSyntax.LiteralExpressionSyntax
                {
                    RawKind: (int)RoslynCSharp.SyntaxKind.StringLiteralExpression
                } literal)
                return node;

            var expectedCallerName =
                node.Modifiers.Any(modifier => modifier.RawKind == (int)RoslynCSharp.SyntaxKind.StaticKeyword)
                    ? ".cctor"
                    : ".ctor";

            if (!string.Equals(literal.Token.ValueText, expectedCallerName, StringComparison.Ordinal) ||
                node.Parent is not RoslynSyntax.TypeDeclarationSyntax containingType)
                return node;

            var parameterIndex = initializer.ArgumentList.Arguments.Count - 1;
            if (parameterIndex < 0)
                return node;

            var constructorExists = containingType.Members
                .AsValueEnumerable()
                .OfType<RoslynSyntax.ConstructorDeclarationSyntax>()
                .Where(ctor => ctor != node)
                .Any(ctor => MatchesCallerMemberNameConstructor(ctor, node.Identifier.ValueText,
                    initializer.ArgumentList.Arguments.Count, parameterIndex));

            if (!constructorExists)
                return node;

            var newArguments = initializer.ArgumentList.Arguments.RemoveAt(parameterIndex);
            return node.WithInitializer(
                initializer.WithArgumentList(initializer.ArgumentList.WithArguments(newArguments)));
        }

        private static bool MatchesCallerMemberNameConstructor(
            RoslynSyntax.ConstructorDeclarationSyntax candidate,
            string constructorName,
            int argumentCount,
            int parameterIndex)
        {
            if (candidate.Identifier.ValueText != constructorName)
                return false;

            var parameters = candidate.ParameterList.Parameters;
            if (parameters.Count != argumentCount)
                return false;

            if (parameterIndex >= parameters.Count)
                return false;

            var parameter = parameters[parameterIndex];
            if (parameter.Default is null)
                return false;

            return parameter.AttributeLists
                .AsValueEnumerable()
                .SelectMany(attributeList => attributeList.Attributes)
                .Any(attribute => IsCallerMemberNameAttribute(attribute.Name));
        }

        private static bool IsCallerMemberNameAttribute(RoslynSyntax.NameSyntax nameSyntax) =>
            nameSyntax.ToString() is "CallerMemberName" or "CallerMemberNameAttribute"
                or "System.Runtime.CompilerServices.CallerMemberNameAttribute";
    }
}
