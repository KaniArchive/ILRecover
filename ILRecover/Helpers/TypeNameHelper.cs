namespace ILRecover.Helpers;

public static class TypeNameHelper
{
    public static string GetRoot(string typeName)
    {
        var nestedIndex = typeName.IndexOf('+');
        return nestedIndex < 0 ? typeName : typeName[..nestedIndex];
    }

    public static string GetSimple(string typeName)
    {
        var nestedIndex = typeName.LastIndexOf('+');
        var namespaceIndex = typeName.LastIndexOf('.');
        var index = Math.Max(nestedIndex, namespaceIndex);
        var simpleName = index < 0 ? typeName : typeName[(index + 1)..];
        var arityIndex = simpleName.IndexOf('`');
        return arityIndex < 0 ? simpleName : simpleName[..arityIndex];
    }

    public static string GetNamespace(string typeName)
    {
        var rootTypeName = GetRoot(typeName);
        var namespaceIndex = rootTypeName.LastIndexOf('.');
        return namespaceIndex < 0 ? string.Empty : rootTypeName[..namespaceIndex];
    }

    public static bool IsCompilerGenerated(string typeName) =>
        typeName.StartsWith('<') || typeName.Contains("+<");

    public static bool IsNested(string typeName) => typeName.Contains('+', StringComparison.Ordinal);
}
