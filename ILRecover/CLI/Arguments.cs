namespace ILRecover.CLI;

public static class Args
{
    /// <summary>
    ///     Decompile .NET assemblies back to C# source
    /// </summary>
    /// <param name="input">-i, Path to the directory containing the target .dll/.exe and .pdb files.</param>
    /// <param name="output">-o, Path for the decompiled output.</param>
    /// <param name="csVersion">-cs, C# language version (8, 9, 10, 11, 12, 13, 14).</param>
    /// <param name="dependencies">-dp, Path to the folder containing DLL references.</param>
    /// <param name="solution">-sl, Name of the solution.</param>
    /// <param name="dotnet">-dn, Target .NET framework (e.g. net9.0).</param>
    /// <param name="shift">-sh, Assembly name patterns that enable shifted PDB method-row recovery.</param>
    public static void Run(
        string input,
        string output,
        int csVersion,
        string? solution = null,
        string? dotnet = null,
        string[]? shift = null,
        params string[]? dependencies) =>
        Parser.Execute(input, output, csVersion, dependencies, solution, dotnet, shift);
}
