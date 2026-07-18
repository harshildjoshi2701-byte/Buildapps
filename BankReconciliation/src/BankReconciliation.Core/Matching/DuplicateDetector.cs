using BankReconciliation.Core.Models;

namespace BankReconciliation.Core.Matching;

/// <summary>
/// Flags "Possible Duplicate" transactions: rows that remain unmatched after
/// every earlier matching pass (see <see cref="ReconciliationEngine"/>) AND
/// share an identical (Date, Amount) with at least one other unmatched row
/// on the SAME side.
///
/// Rationale for running this only against the still-unmatched pool: two
/// genuinely duplicate bank deposits that each have their own corresponding
/// R365 posting are not a problem — they get matched normally (the
/// exact-match pass picks the FIFO-oldest for the first one, then the second
/// candidate satisfies the second one). It's only when a same-date/same-amount
/// pair is left over with no explanation that it's worth flagging as a
/// possible bookkeeping duplicate rather than a plain "No Match".
/// </summary>
public static class DuplicateDetector
{
    /// <summary>Returns the set of row numbers, among the given (already-filtered
    /// to unmatched) transactions, that share a (Date, AmountCents) key with
    /// at least one other transaction in the same set.</summary>
    public static HashSet<int> FindPossibleDuplicates(IEnumerable<TransactionRecord> unmatchedTransactions)
    {
        var buckets = new Dictionary<(DateTime Date, long AmountCents), List<TransactionRecord>>();
        foreach (var t in unmatchedTransactions)
        {
            var key = (t.Date.Date, t.AmountCents);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<TransactionRecord>();
                buckets[key] = list;
            }
            list.Add(t);
        }

        var duplicateRows = new HashSet<int>();
        foreach (var list in buckets.Values)
        {
            if (list.Count < 2) continue;
            foreach (var t in list)
                duplicateRows.Add(t.RowNumber);
        }
        return duplicateRows;
    }

    /// <summary>
    /// Applies final status to every transaction still Unmatched after all
    /// matching passes: PossibleDuplicate (yellow) if it shares date+amount
    /// with another unmatched row on its side, otherwise NoMatch (red).
    /// </summary>
    public static void FinalizeUnmatched(IEnumerable<TransactionRecord> transactions)
    {
        var stillUnmatched = transactions.Where(t => t.Status == MatchStatus.Unmatched).ToList();
        var duplicateRows = FindPossibleDuplicates(stillUnmatched);

        foreach (var t in stillUnmatched)
        {
            if (duplicateRows.Contains(t.RowNumber))
            {
                t.Status = MatchStatus.PossibleDuplicate;
                t.Comment = "Possible Duplicate (matches another unmatched transaction on the same date and amount)";
            }
            else
            {
                t.Status = MatchStatus.NoMatch;
                t.Comment = "No Match";
            }
        }
    }
}
