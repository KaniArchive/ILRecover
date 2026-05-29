namespace ILRecover.Models;

public record LocalVariableDebugInfo(
    int SlotIndex,
    string Name,
    int StartOffset,
    int Length
);