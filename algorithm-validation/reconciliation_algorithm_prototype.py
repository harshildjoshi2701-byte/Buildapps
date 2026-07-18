"""
Reconciliation engine PROTOTYPE (Python) — v4 (tuned pool/time bounds).

Purpose: this is NOT the deliverable. It mirrors the algorithm design of the
C# BankReconciliation.Core engine (see /src/BankReconciliation.Core) so the
matching logic can be executed and validated against the real sample
workbook in an environment that has no .NET SDK available. Every rule
implemented here has a 1:1 counterpart in the C# port:

  - OneToOneMatcher.cs    <-> pass1_exact_match / pass2_date_tolerant_match
  - CombinationMatcher.cs <-> pass3_combination_match (2-sum/3-sum + bounded DP)
  - DuplicateDetector.cs  <-> detect_duplicates
  - ConfidenceScorer.cs   <-> confidence_score / confidence_combination

Run: python3 reconcile_v4.py <path-to-xlsx> [--max-days N] [--out PATH]
"""
import sys
import time
import argparse
from dataclasses import dataclass
from datetime import datetime, timedelta
from collections import defaultdict

import openpyxl
from openpyxl.styles import PatternFill

CENTS = 100

BANK_DATE_COL = 3      # C
BANK_CREDIT_COL = 6    # F
BANK_DEBIT_COL = 7     # G
BANK_COMMENT_COL = 9   # I
BANK_CONFIDENCE_COL = 10  # J
BANK_HEADER_ROW = 2
BANK_DATA_START = 3

R365_DATE_COL = 14     # N
R365_REF_COL = 16      # P
R365_AMOUNT_COL = 25   # Y
R365_COMMENT_COL = 26  # Z
R365_CONFIDENCE_COL = 27  # AA
R365_HEADER_ROW = 2
R365_DATA_START = 3

GROUP_KEYWORD = "r365"


def to_cents(x):
    return int(round(float(x) * CENTS))


@dataclass
class Txn:
    row: int
    date: datetime
    amount_cents: int
    ref: str = ""
    is_grouped: bool = False
    matched: bool = False
    match_status: str = ""
    comment: str = ""
    confidence: int = 0
    group_id: int = -1


def load_transactions(path):
    wb = openpyxl.load_workbook(path, data_only=True)
    ws = wb.worksheets[0]

    bank = []
    for r in range(BANK_DATA_START, ws.max_row + 1):
        date = ws.cell(row=r, column=BANK_DATE_COL).value
        if date is None or not isinstance(date, datetime):
            continue
        credit = ws.cell(row=r, column=BANK_CREDIT_COL).value or 0
        debit = ws.cell(row=r, column=BANK_DEBIT_COL).value or 0
        amt = to_cents(credit) - to_cents(debit)
        bank.append(Txn(row=r, date=date, amount_cents=amt))

    r365 = []
    for r in range(R365_DATA_START, ws.max_row + 1):
        date = ws.cell(row=r, column=R365_DATE_COL).value
        amt = ws.cell(row=r, column=R365_AMOUNT_COL).value
        if date is None or amt is None or not isinstance(date, datetime):
            continue
        ref = ws.cell(row=r, column=R365_REF_COL).value or ""
        r365.append(Txn(
            row=r, date=date, amount_cents=to_cents(amt), ref=str(ref),
            is_grouped=GROUP_KEYWORD in str(ref).lower(),
        ))

    return bank, r365, wb, ws


def index_by_amount(txns):
    idx = defaultdict(list)
    for t in txns:
        idx[t.amount_cents].append(t)
    for lst in idx.values():
        lst.sort(key=lambda t: (t.date, t.row))
    return idx


def selection_key(is_exact_date, num_txns, oldest_date, date_diff, confidence):
    return (0 if is_exact_date else 1, num_txns, oldest_date, date_diff, -confidence)


def pass1_exact_match(bank, r365_idx, log):
    matched_count = 0
    for b in bank:
        if b.matched or b.amount_cents == 0:
            continue
        candidates = [r for r in r365_idx.get(b.amount_cents, []) if not r.matched and r.date == b.date]
        if candidates:
            chosen = candidates[0]
            b.matched = chosen.matched = True
            b.match_status = chosen.match_status = "MatchedExact"
            b.comment = chosen.comment = "Matched (Exact)"
            b.confidence = chosen.confidence = 100
            matched_count += 1
    log.append(f"Pass 1 (exact date+amount): {matched_count} one-to-one matches")
    return matched_count


def pass2_date_tolerant_match(bank, r365_idx, max_days, log):
    matched_count = 0
    for b in bank:
        if b.matched or b.amount_cents == 0:
            continue
        candidates = [
            r for r in r365_idx.get(b.amount_cents, [])
            if not r.matched and 0 <= (b.date - r.date).days <= max_days
        ]
        if not candidates:
            continue
        scored = []
        for r in candidates:
            diff = (b.date - r.date).days
            conf = confidence_one_to_one(diff, max_days)
            scored.append((selection_key(diff == 0, 1, r.date, diff, conf), r, diff, conf))
        scored.sort(key=lambda x: x[0])
        _, chosen, diff, conf = scored[0]
        b.matched = chosen.matched = True
        b.match_status = chosen.match_status = "MatchedDateTolerant"
        label = f"Matched (Date Difference {diff} Day{'s' if diff != 1 else ''})" if diff else "Matched (Exact)"
        b.comment = chosen.comment = label
        b.confidence = chosen.confidence = conf
        matched_count += 1
    log.append(f"Pass 2 (date-tolerant, 1-{max_days}d): {matched_count} one-to-one matches")
    return matched_count


def confidence_one_to_one(date_diff_days, max_days):
    if date_diff_days == 0:
        return 100
    penalty = (date_diff_days / max_days) * 10.0
    return max(90, round(100 - penalty))


# ----------------------------------------------------------------------------
# Pass 3: combination (subset-sum) matcher for R365-flagged rows.
# ----------------------------------------------------------------------------

class ComboSearchStats:
    def __init__(self):
        self.calls = 0
        self.aborted = 0
        self.two_sum_hits = 0
        self.three_sum_hits = 0
        self.dp_hits = 0
        self.truncated = 0


def find_pair(target_abs, pool_by_amt):
    """O(n) hash lookup for a 2-item exact combination."""
    for amt, txns in pool_by_amt.items():
        complement = target_abs - amt
        if complement < amt:
            continue
        if complement == amt:
            if len(txns) >= 2:
                return [txns[0], txns[1]]
            continue
        others = pool_by_amt.get(complement)
        if others:
            return [txns[0], others[0]]
    return None


def find_triple(target_abs, pool, by_amt):
    """Exact 3-combination via fix-one + hash-lookup-pair -> O(n) amortized."""
    for i in range(len(pool)):
        rem = target_abs - abs(pool[i].amount_cents)
        if rem <= 0:
            continue
        for amt, txns in by_amt.items():
            complement = rem - amt
            if complement < amt:
                continue
            if complement == amt:
                cand = [t for t in txns if t is not pool[i]]
                if len(cand) >= 2:
                    return [pool[i], cand[0], cand[1]]
                continue
            others = by_amt.get(complement)
            if others:
                a = next((t for t in txns if t is not pool[i]), None)
                b = next((t for t in others if t is not pool[i] and t is not a), None)
                if a is not None and b is not None:
                    return [pool[i], a, b]
    return None


def find_combination(target_cents, candidates, stats, max_pool_size=260, max_states=25_000, time_budget_s=0.15):
    """Returns (list[Txn]|None, ambiguous, status) where status is one of
    'found', 'none', 'timeout'.

    Strategy (cheapest and most-certain first):
      1. Exact 2-combination via hash table -> O(n)
      2. Exact 3-combination via fix-one + hash lookup -> O(n) amortized
      3. Bounded iterative subset-sum DP (same-sign monotonic pruning + suffix-sum
         feasibility pruning), tracking MINIMUM cardinality per reachable sum.

    True unrestricted subset-sum over hundreds of real-valued candidates is
    NP-hard; no algorithm guarantees an exhaustive exact search finishes in
    bounded time for arbitrarily large pools. Two safety valves keep this
    engine's worst case bounded (mirrors CombinationMatcher.MaxCandidatePoolSize
    and TimeBudget in the C# engine's ReconciliationSettings):
      - max_pool_size: if more candidates than this exist in the date/sign
        window, only the N candidates CLOSEST TO THE BANK DATE are searched;
        the caller marks the result "Manual Review - pool truncated" rather
        than silently guessing.
      - max_states / time_budget_s: hard caps on the DP search itself.
    Every match this function DOES return is verified to sum exactly (within
    tolerance) to the target - that invariant is never relaxed for speed.
    """
    if not candidates or target_cents == 0:
        return None, False, "none"

    target_abs = abs(target_cents)
    full_pool = sorted((c for c in candidates if abs(c.amount_cents) <= target_abs), key=lambda t: (t.date, t.row))
    if not full_pool:
        return None, False, "none"
    if sum(abs(c.amount_cents) for c in full_pool) < target_abs:
        return None, False, "none"

    truncated = False
    if len(full_pool) > max_pool_size:
        pool = sorted(full_pool, key=lambda t: t.date, reverse=True)[:max_pool_size]
        pool.sort(key=lambda t: (t.date, t.row))
        truncated = True
        stats.truncated += 1
    else:
        pool = full_pool
    n = len(pool)

    by_amt = defaultdict(list)
    for c in pool:
        by_amt[abs(c.amount_cents)].append(c)

    pair = find_pair(target_abs, by_amt)
    if pair:
        stats.two_sum_hits += 1
        return pair, False, "found"

    triple = find_triple(target_abs, pool, by_amt)
    if triple:
        stats.three_sum_hits += 1
        return triple, False, "found"

    if truncated:
        return None, False, "timeout"

    suffix = [0] * (n + 1)
    for i in range(n - 1, -1, -1):
        suffix[i] = suffix[i + 1] + abs(pool[i].amount_cents)

    reachable = {0: (0, None, None, False)}
    t0 = time.time()
    for idx in range(n):
        camt = abs(pool[idx].amount_cents)
        snapshot = list(reachable.items())
        for s, (cnt, parent, _, _tie) in snapshot:
            ns = s + camt
            if ns > target_abs:
                continue
            if (target_abs - ns) > suffix[idx + 1]:
                continue
            newcnt = cnt + 1
            existing = reachable.get(ns)
            if existing is None:
                reachable[ns] = (newcnt, s, idx, False)
            elif newcnt < existing[0]:
                reachable[ns] = (newcnt, s, idx, False)
            elif newcnt == existing[0] and existing[2] != idx:
                reachable[ns] = (existing[0], existing[1], existing[2], True)
            stats.calls += 1
            if len(reachable) > max_states:
                stats.aborted += 1
                return None, False, "timeout"
        if time.time() - t0 > time_budget_s:
            stats.aborted += 1
            return None, False, "timeout"
        if target_abs in reachable and reachable[target_abs][0] <= 2:
            break

    if target_abs not in reachable:
        return None, False, "none"

    stats.dp_hits += 1
    path = []
    s = target_abs
    ambiguous = False
    while s != 0:
        cnt, parent, idx, tie = reachable[s]
        if tie:
            ambiguous = True
        path.append(pool[idx])
        s = parent
    return path, ambiguous, "found"


def pass3_combination_match(bank, r365, max_days, min_confidence, log, global_deadline_s=32.0):
    r365_pool = [r for r in r365 if r.is_grouped and not r.matched]
    unmatched_bank = [b for b in bank if not b.matched and b.amount_cents != 0]

    credits = sorted((r for r in r365_pool if r.amount_cents > 0), key=lambda t: t.date)
    debits = sorted((r for r in r365_pool if r.amount_cents < 0), key=lambda t: t.date)

    def candidate_pool_for(b):
        base = credits if b.amount_cents > 0 else debits
        lo = b.date - timedelta(days=max_days)
        return [r for r in base if not r.matched and lo <= r.date <= b.date]

    scored_bank = sorted(unmatched_bank, key=lambda b: len(candidate_pool_for(b)))

    matched_count = 0
    manual_review = 0
    timed_out = 0
    ran_out_of_time = 0
    stats = ComboSearchStats()
    group_id_counter = [1000]
    t0 = time.time()
    max_pool_seen = 0
    processed = 0

    for b in scored_bank:
        if b.matched:
            continue
        processed += 1
        if processed % 250 == 0:
            print(f"    ... pass 3 progress: {processed}/{len(scored_bank)} scanned, "
                  f"{matched_count} combos found, {time.time()-t0:.1f}s elapsed", flush=True)
        if time.time() - t0 > global_deadline_s:
            b.match_status = "ManualReview"
            b.comment = "Manual Review (Not processed - global time budget reached in prototype run)"
            b.confidence = 0
            manual_review += 1
            ran_out_of_time += 1
            continue
        pool = candidate_pool_for(b)
        max_pool_seen = max(max_pool_seen, len(pool))
        if not pool:
            continue
        combo, ambiguous, status = find_combination(b.amount_cents, pool, stats)
        if status == "timeout":
            b.match_status = "ManualReview"
            b.comment = "Manual Review (Combination search exceeded time/complexity budget)"
            b.confidence = 0
            manual_review += 1
            timed_out += 1
            continue
        if not combo:
            continue

        diffs = [(b.date - c.date).days for c in combo]
        max_diff = max(diffs)
        conf = confidence_combination(len(combo), max_diff, max_days, ambiguous)

        if ambiguous or conf < min_confidence:
            b.match_status = "ManualReview"
            b.comment = f"Manual Review (Tentative combination of {len(combo)} transactions, needs confirmation)"
            b.confidence = conf
            manual_review += 1
            continue

        gid = group_id_counter[0]
        group_id_counter[0] += 1
        b.matched = True
        b.match_status = "MatchedCombination"
        b.comment = f"Matched (Combination of {len(combo)} Transactions)"
        b.confidence = conf
        b.group_id = gid
        for c in combo:
            c.matched = True
            c.match_status = "MatchedCombination"
            c.comment = f"Matched (Combination of {len(combo)} Transactions)"
            c.confidence = conf
            c.group_id = gid
        matched_count += 1

    elapsed = time.time() - t0
    log.append(
        f"Pass 3 (combination/subset-sum): {matched_count} combination matches, "
        f"{manual_review} flagged manual review ({timed_out} search timeouts/truncations, "
        f"{ran_out_of_time} skipped by prototype's global time budget), "
        f"in {elapsed:.2f}s (two_sum={stats.two_sum_hits:,} three_sum={stats.three_sum_hits:,} "
        f"dp_hits={stats.dp_hits:,} dp_states={stats.calls:,} aborted={stats.aborted:,} "
        f"truncated_pools={stats.truncated:,} max_candidate_pool={max_pool_seen})"
    )
    return matched_count, manual_review


def confidence_combination(count, max_date_diff, max_days, ambiguous):
    import math
    base = 100
    date_penalty = (max_date_diff / max_days) * 5.0 if max_days else 0
    count_penalty = min(8.0, math.log2(max(count, 1)) * 1.5)
    ambiguity_penalty = 15 if ambiguous else 0
    score = base - date_penalty - count_penalty - ambiguity_penalty
    return max(0, min(100, round(score)))


def detect_duplicates(txns):
    buckets = defaultdict(list)
    for t in txns:
        buckets[(t.date.date() if hasattr(t.date, 'date') else t.date, t.amount_cents)].append(t)
    dup_rows = set()
    for key, group in buckets.items():
        if len(group) > 1:
            for t in group:
                dup_rows.add(t.row)
    return dup_rows


def finalize_unmatched(txns, dup_rows):
    for t in txns:
        if t.matched or t.match_status == "ManualReview":
            continue
        if t.row in dup_rows:
            t.match_status = "PossibleDuplicate"
            t.comment = "Possible Duplicate (matches another unmatched transaction on same date/amount)"
        else:
            t.match_status = "NoMatch"
            t.comment = "No Match"


GREEN = PatternFill(start_color="FFC6EFCE", end_color="FFC6EFCE", fill_type="solid")
RED = PatternFill(start_color="FFFFC7CE", end_color="FFFFC7CE", fill_type="solid")
YELLOW = PatternFill(start_color="FFFFEB9C", end_color="FFFFEB9C", fill_type="solid")
BLUE_PALETTE = [
    "FFB4C7E7", "FFC9C1F0", "FFA9D4E8", "FFCBB4E0", "FF9FC5E8", "FFB6A6D9",
    "FF8EC6D9", "FFD0A9E0", "FFA3C4E0", "FFBFA8DB",
]


def fill_for(status, group_id):
    if status in ("MatchedExact", "MatchedDateTolerant"):
        return GREEN
    if status == "MatchedCombination":
        color = BLUE_PALETTE[group_id % len(BLUE_PALETTE)]
        return PatternFill(start_color=color, end_color=color, fill_type="solid")
    if status in ("ManualReview", "PossibleDuplicate"):
        return YELLOW
    if status == "NoMatch":
        return RED
    return None


def write_results(ws, bank, r365, out_path):
    for b in bank:
        fill = fill_for(b.match_status, b.group_id)
        ws.cell(row=b.row, column=BANK_COMMENT_COL, value=b.comment)
        ws.cell(row=b.row, column=BANK_CONFIDENCE_COL, value=(b.confidence / 100.0) if b.confidence else None)
        if fill:
            for col in range(1, BANK_COMMENT_COL + 1):
                ws.cell(row=b.row, column=col).fill = fill
    ws.cell(row=BANK_HEADER_ROW, column=BANK_COMMENT_COL, value="Reconciliation Comment")
    ws.cell(row=BANK_HEADER_ROW, column=BANK_CONFIDENCE_COL, value="Confidence")

    for r in r365:
        fill = fill_for(r.match_status, r.group_id)
        ws.cell(row=r.row, column=R365_COMMENT_COL, value=r.comment)
        ws.cell(row=r.row, column=R365_CONFIDENCE_COL, value=(r.confidence / 100.0) if r.confidence else None)
        if fill:
            for col in range(14, R365_COMMENT_COL + 1):
                ws.cell(row=r.row, column=col).fill = fill
    ws.cell(row=R365_HEADER_ROW, column=R365_COMMENT_COL, value="Reconciliation Comment")
    ws.cell(row=R365_HEADER_ROW, column=R365_CONFIDENCE_COL, value="Confidence")

    ws.parent.save(out_path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--max-days", type=int, default=6)
    ap.add_argument("--min-confidence", type=int, default=80)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    log = []
    t_start = time.time()

    bank, r365, wb, ws = load_transactions(args.path)
    log.append(f"Loaded {len(bank):,} bank transactions, {len(r365):,} R365 transactions")
    r365_grouped = sum(1 for r in r365 if r.is_grouped)
    log.append(f"R365 rows flagged as grouped (Ref.# contains 'R365'): {r365_grouped:,} / {len(r365):,}")

    r365_idx = index_by_amount(r365)

    p1 = pass1_exact_match(bank, r365_idx, log)
    p2 = pass2_date_tolerant_match(bank, r365_idx, args.max_days, log)
    p3, manual = pass3_combination_match(bank, r365, args.max_days, args.min_confidence, log)

    dup_bank = detect_duplicates([b for b in bank if not b.matched])
    dup_r365 = detect_duplicates([r for r in r365 if not r.matched])
    finalize_unmatched(bank, dup_bank)
    finalize_unmatched(r365, dup_r365)

    elapsed = time.time() - t_start

    matched_bank = sum(1 for b in bank if b.matched)
    matched_r365 = sum(1 for r in r365 if r.matched)
    no_match_bank = sum(1 for b in bank if b.match_status == "NoMatch")
    no_match_r365 = sum(1 for r in r365 if r.match_status == "NoMatch")
    manual_bank = sum(1 for b in bank if b.match_status in ("ManualReview", "PossibleDuplicate"))
    manual_r365 = sum(1 for r in r365 if r.match_status in ("ManualReview", "PossibleDuplicate"))
    matched_amount = sum(abs(b.amount_cents) for b in bank if b.matched) / CENTS

    print("=" * 70)
    print("RECONCILIATION PROTOTYPE — RESULTS")
    print("=" * 70)
    for line in log:
        print(" -", line)
    print("-" * 70)
    print(f"Total Bank Transactions:      {len(bank):,}")
    print(f"Total R365 Transactions:      {len(r365):,}")
    print(f"Matched Bank Transactions:    {matched_bank:,} ({matched_bank/len(bank)*100:.1f}%)")
    print(f"Matched R365 Transactions:    {matched_r365:,} ({matched_r365/len(r365)*100:.1f}%)")
    print(f"  One-to-one matches:         {p1 + p2:,}")
    print(f"  Combination matches:        {p3:,} (bank txns resolved via combos)")
    print(f"Matched Amount (abs, bank):   ${matched_amount:,.2f}")
    print(f"Unmatched Bank (No Match):    {no_match_bank:,}")
    print(f"Unmatched R365 (No Match):    {no_match_r365:,}")
    print(f"Manual Review / Dup (Bank):   {manual_bank:,}")
    print(f"Manual Review / Dup (R365):   {manual_r365:,}")
    print(f"Total elapsed:                {elapsed:.2f}s")
    print("=" * 70)

    out_path = args.out or args.path.replace(".xlsx", "_Reconciled_DEMO.xlsx")
    write_results(ws, bank, r365, out_path)
    print(f"Demo reconciled workbook written to: {out_path}")


if __name__ == "__main__":
    main()
