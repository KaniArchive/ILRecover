using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.DebugInfo;
using SequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace ILRecover.Pdb;

public sealed class RemappedDebugInfoProvider(
    IDebugInfoProvider inner,
    PdbMethodDebugMap methodDebugMap) : IDebugInfoProvider, IDisposable
{
    public string Description => inner.Description;

    public string SourceFileName => inner.SourceFileName;

    public IList<SequencePoint> GetSequencePoints(MethodDefinitionHandle method) =>
        inner.GetSequencePoints(Map(method));

    public IList<Variable> GetVariables(MethodDefinitionHandle method) =>
        inner.GetVariables(Map(method));

    public bool TryGetName(MethodDefinitionHandle method, int index, out string name) =>
        inner.TryGetName(Map(method), index, out name);

    public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo) =>
        inner.TryGetExtraTypeInfo(Map(method), index, out extraTypeInfo);

    public void Dispose()
    {
        if (inner is IDisposable disposable)
            disposable.Dispose();
    }

    private MethodDefinitionHandle Map(MethodDefinitionHandle method)
    {
        var actualRow = MetadataTokens.GetRowNumber(method);
        var pdbRow = methodDebugMap.GetPdbRow(actualRow);
        return pdbRow == actualRow ? method : MetadataTokens.MethodDefinitionHandle(pdbRow);
    }
}
