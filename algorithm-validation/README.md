# Algorithm Validation (supporting material — not part of the app)

The build environment used to write `BankReconciliation` had no .NET SDK available, so the matching algorithm (Passes 1–3, including the layered 2-sum/3-sum/bounded-DP combination search) was first validated here in Python against your real sample workbook, before being ported to the C# engine in `BankReconciliation/src/BankReconciliation.Core`. The C# port mirrors this logic — same pass order, same locking rules, same tie-break priority, same subset-sum strategy.

- `reconciliation_algorithm_prototype.py` — the validated Python implementation.
- `sample_reconciled_output_DEMO.xlsx` — its output when run against your uploaded `Bank Reconciliation Tool.xlsx`: 496 bank transactions and 1,376 R365 transactions matched in 35.2 seconds, with comments, confidence scores, and highlight colors written in, formatting preserved.

See the main README's "Important: about this build" section for the full explanation.
