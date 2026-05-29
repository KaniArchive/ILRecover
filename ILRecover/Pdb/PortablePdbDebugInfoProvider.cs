using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.DebugInfo;
using DecompilerSequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace ILRecover.Pdb;

public sealed class PortablePdbDebugInfoProvider : IDebugInfoProvider, IDisposable
{
    private readonly string _moduleFileName;
    private readonly MetadataReaderProvider _provider;
    private readonly FileStream _stream;
    private bool _hasError;

    public PortablePdbDebugInfoProvider(string moduleFileName, string pdbPath)
    {
        _moduleFileName = moduleFileName;
        SourceFileName = pdbPath;
        _stream = File.OpenRead(pdbPath);
        _provider = MetadataReaderProvider.FromPortablePdbStream(_stream);
    }

    public string Description => _hasError
        ? $"Error while loading portable PDB: {SourceFileName}"
        : $"Loaded from portable PDB: {SourceFileName}";

    public string SourceFileName { get; }

    public IList<DecompilerSequencePoint> GetSequencePoints(MethodDefinitionHandle method)
    {
        var metadata = GetMetadataReader();
        var sequencePoints = new List<DecompilerSequencePoint>();
        if (metadata is null)
            return sequencePoints;

        try
        {
            var debugInfo = metadata.GetMethodDebugInformation(method);
            foreach (var point in debugInfo.GetSequencePoints())
            {
                var documentFileName = point.Document.IsNil
                    ? string.Empty
                    : metadata.GetString(metadata.GetDocument(point.Document).Name);

                sequencePoints.Add(new DecompilerSequencePoint
                {
                    Offset = point.Offset,
                    StartLine = point.StartLine,
                    StartColumn = point.StartColumn,
                    EndLine = point.EndLine,
                    EndColumn = point.EndColumn,
                    DocumentUrl = documentFileName
                });
            }
        }
        catch (BadImageFormatException)
        {
            return new List<DecompilerSequencePoint>();
        }

        return sequencePoints;
    }

    public IList<Variable> GetVariables(MethodDefinitionHandle method)
    {
        var metadata = GetMetadataReader();
        var variables = new List<Variable>();
        if (metadata is null)
            return variables;

        foreach (var scopeHandle in metadata.GetLocalScopes(method))
        {
            var scope = metadata.GetLocalScope(scopeHandle);
            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = metadata.GetLocalVariable(localHandle);
                variables.Add(new Variable(local.Index, metadata.GetString(local.Name)));
            }
        }

        return variables;
    }

    public bool TryGetName(MethodDefinitionHandle method, int index, [NotNullWhen(true)] out string? name)
    {
        var metadata = GetMetadataReader();
        name = null;
        if (metadata is null)
            return false;

        foreach (var scopeHandle in metadata.GetLocalScopes(method))
        {
            var scope = metadata.GetLocalScope(scopeHandle);
            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = metadata.GetLocalVariable(localHandle);
                if (local.Index != index)
                    continue;

                name = metadata.GetString(local.Name);
                return true;
            }
        }

        return false;
    }

    public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo)
    {
        var metadata = GetMetadataReader();
        extraTypeInfo = default;
        if (metadata is null)
            return false;

        LocalVariableHandle localVariableHandle = default;
        foreach (var scopeHandle in metadata.GetLocalScopes(method))
        {
            var scope = metadata.GetLocalScope(scopeHandle);
            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = metadata.GetLocalVariable(localHandle);
                if (local.Index != index)
                    continue;

                localVariableHandle = localHandle;
                break;
            }

            if (!localVariableHandle.IsNil)
                break;
        }

        foreach (var customDebugHandle in metadata.CustomDebugInformation)
        {
            var customDebugInfo = metadata.GetCustomDebugInformation(customDebugHandle);
            if (customDebugInfo.Parent.IsNil || customDebugInfo.Parent.Kind != HandleKind.LocalVariable)
                continue;

            if (localVariableHandle != (LocalVariableHandle)customDebugInfo.Parent)
                continue;

            if (customDebugInfo.Value.IsNil || customDebugInfo.Kind.IsNil)
                continue;

            var kind = metadata.GetGuid(customDebugInfo.Kind);
            if (kind == KnownGuids.TupleElementNames && extraTypeInfo.TupleElementNames is null)
            {
                var reader = metadata.GetBlobReader(customDebugInfo.Value);
                var names = new List<string?>();
                while (reader.RemainingBytes > 0)
                {
                    var length = reader.IndexOf(0);
                    var value = reader.ReadUTF8(length);
                    reader.ReadByte();
                    names.Add(string.IsNullOrWhiteSpace(value) ? null : value);
                }

                extraTypeInfo.TupleElementNames = names.ToArray();
            }
            else if (kind == KnownGuids.DynamicLocalVariables && extraTypeInfo.DynamicFlags is null)
            {
                var reader = metadata.GetBlobReader(customDebugInfo.Value);
                extraTypeInfo.DynamicFlags = new bool[reader.Length * 8];
                var bitIndex = 0;
                while (reader.RemainingBytes > 0)
                {
                    var value = reader.ReadByte();
                    for (var mask = 1; mask < 0x100; mask <<= 1)
                        extraTypeInfo.DynamicFlags[bitIndex++] = (value & mask) != 0;
                }
            }

            if (extraTypeInfo.TupleElementNames is not null && extraTypeInfo.DynamicFlags is not null)
                break;
        }

        return extraTypeInfo.TupleElementNames is not null || extraTypeInfo.DynamicFlags is not null;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _stream.Dispose();
    }

    private MetadataReader? GetMetadataReader()
    {
        try
        {
            _hasError = false;
            return _provider.GetMetadataReader();
        }
        catch (BadImageFormatException)
        {
            _hasError = true;
            return null;
        }
        catch (IOException)
        {
            _hasError = true;
            return null;
        }
    }
}