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
    /// <param name="solution">-sl, Solution name.</param>
    /// <param name="dotnet">-dn, Target framework.</param>
    public static void Run(
        string input,
        string output,
        int csVersion,
        string? solution = null,
        string? dotnet = null,
        params string[]? dependencies) =>
        Parser.Execute(input, output, csVersion, dependencies, solution, dotnet);
}