namespace ILRecover.Pdb;

public sealed record PdbMethodDebugMap(
    IReadOnlyDictionary<int, string> DocumentPathsByMethodRow,
    IReadOnlyDictionary<int, int> PdbRowByActualMethodRow
)
{
    public static PdbMethodDebugMap Identity(IReadOnlyDictionary<int, string> documentPathsByMethodRow) =>
        new(documentPathsByMethodRow, new Dictionary<int, int>());

    public int GetPdbRow(int actualMethodRow) =>
        PdbRowByActualMethodRow.GetValueOrDefault(actualMethodRow, actualMethodRow);
}
