using System.Reflection.Metadata;

namespace ILRecover.Models;

public record SourceFileMethodEntry(
    string TypeFullName,
    MethodDefinitionHandle MethodHandle,
    IReadOnlyList<LocalVariableDebugInfo> LocalVariables
);
