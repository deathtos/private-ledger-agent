namespace PrivateLedgerAgent.Ledger;

/// <summary>
/// One row of grounding data. Amount is signed: negative = money out, positive = money in.
/// RefId ("file.csv#L42") is the exact physical line in the source file.
/// </summary>
public sealed record Transaction(
    string RefId,
    string SourceFile,
    int LineNumber,
    DateOnly Date,
    string Description,
    decimal Amount,
    string Category,
    string CategorySource,
    string Account)
{
    public bool IsOutflow => Amount < 0;
    public bool IsInflow => Amount > 0;
}
