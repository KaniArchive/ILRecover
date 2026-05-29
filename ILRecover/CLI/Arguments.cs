namespace ILRecover.CLI;

public static class Args
{
    /// <summary>
    ///     DLL Decompiler
    /// </summary>
    /// <param name="input">-i, Input.</param>
    /// <param name="output">-o, Outputy.</param>
    /// <param name="csVersion">-cs, C# Version.</param>
    /// <param name="dependencies">-dp, Dependencies.</param>
    /// <param name="editorConfig">-ec, EditorConfig path.</param>
    public static void Run(
        string input,
        string output,
        int csVersion,
        string? editorConfig = null,
        params string[]? dependencies) =>
        Parser.Execute(input, output, csVersion, dependencies, editorConfig);
}