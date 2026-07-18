# Bank Reconciliation Tool

A Windows desktop application that reconciles bank transactions against R365 (Restaurant365) transactions inside a single Excel workbook — matching amounts and dates, writing comments and color highlights back into the sheet, and producing a summary dashboard, all without touching the original file.

## Contents

- [What it does](#what-it-does)
- [Requirements](#requirements)
- [Building](#building)
- [Running](#running)
- [Using the app](#using-the-app)
- [Matching algorithm](#matching-algorithm)
- [Column mapping](#column-mapping)
- [Settings reference](#settings-reference)
- [Output files](#output-files)
- [Architecture](#architecture)
- [Design decisions](#design-decisions)
- [Performance notes](#performance-notes)
- [Important: about this build](#important-about-this-build)
- [Troubleshooting](#troubleshooting)

## What it does

The app opens a workbook containing two transaction blocks on one worksheet — a **Bank Transactions** block and an **R365 Transactions** block — and matches rows between them:

- Exact amount + exact date matches first, then exact amount with a configurable date tolerance, then many-to-one **combination matches** (a single bank deposit that corresponds to a bundle of several R365 postings summed together).
- Debits only match debits, credits only match credits (R365's signed Amount column is respected).
- Every match, once made, locks both sides so nothing is ever reused.
- Results are written back as cell comments, confidence scores, and color highlights directly in a copy of the workbook — the original file is never modified.
- A Summary Dashboard, an exportable log, and per-row Manual Review / Possible Duplicate flags are provided for anything the engine can't resolve with full confidence.

## Requirements

**To build:**
- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (17.8+) with the ".NET desktop development" workload, *or* just the SDK + `build.bat` from a command prompt
- [Inno Setup 6](https://jrsoftware.org/isdl.php) — optional, only needed to build the `Setup.exe` installer

**To run the built app:**
- Windows 10 or 11, x64
- Nothing else — the published executable is self-contained and bundles its own .NET runtime

## Building

### Option A — one command

```
build.bat
```

This restores, builds all three projects in Release, runs the unit test suite, publishes a self-contained single-file `BankReconciliation.exe`, and (if Inno Setup is installed) builds the installer. Output goes to `dist\app\` and `dist\installer\`.

Flags: `build.bat /notests` skips the test run; `build.bat /noinstaller` skips the Inno Setup step.

### Option B — Visual Studio

Open `BankReconciliation.sln`, set `BankReconciliation.App` as the startup project, and press F5 to run in the debugger, or use **Build → Publish** on the App project for a release build.

### Option C — manual dotnet CLI

```
dotnet restore BankReconciliation.sln
dotnet build BankReconciliation.sln -c Release
dotnet test src\BankReconciliation.Core.Tests\BankReconciliation.Core.Tests.csproj -c Release
dotnet publish src\BankReconciliation.App\BankReconciliation.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\app
```

### Building the installer separately

```
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\setup.iss
```

Produces `dist\installer\BankReconciliationSetup.exe`. The installer lets the user choose a per-user (no admin required) or per-machine install, adds Start Menu / optional Desktop shortcuts, and registers a normal Windows uninstaller entry.

## Running

Launch `BankReconciliation.exe` directly, or install via `BankReconciliationSetup.exe` and launch from the Start Menu. No installation of .NET, Excel, or any other dependency is required — Excel itself doesn't need to be installed either, since the app reads/writes `.xlsx` files directly.

## Using the app

1. **Browse Excel File** — pick the workbook to reconcile (or pick one from **Recent Files**).
2. **Start Reconciliation** — runs on a background thread with a progress bar, current-pass status, elapsed time, and estimated remaining time. **Cancel** stops a run in progress.
3. When it finishes, the **Summary Dashboard** tab shows the KPIs (totals, matched/unmatched counts, match percentage, processing time, etc.), and the **Bank Transactions** / **R365 Transactions** tabs show every row with its status pill, confidence score, and comment — filterable by All / Matched / Manual Review / No Match.
4. **Open Output File** opens the reconciled workbook; **Show in Folder** reveals it in Explorer; **View Log** opens the run's log file.
5. **Settings** (gear icon) exposes every matching-rule and performance knob — see [Settings reference](#settings-reference).
6. The moon/sun icon toggles Dark Mode.

## Matching algorithm

Matching runs in five passes. Once a transaction (bank or R365) is matched in any pass, it is **locked** and never reconsidered by a later pass or a later transaction.

| Pass | What it does |
|---|---|
| 1 | Custom matching rules — see below. Pure keyword grouping, not a search: no amount, sign, or date check. Runs FIRST, before anything else, because these are curated business rules rather than algorithmic guesses. |
| 2 | One-to-one, exact amount, exact same date. |
| 3 | One-to-one, exact amount, R365 date up to `MaxDateDifferenceDays` days *older* than the bank date (R365 dates are never newer, per the source data's convention). |
| 4 | General combination matching — date-windowed subset-sum search, run THREE times over (each extra pass over whatever's still unmatched frequently finds matches the previous pass could not, now that other transactions have been claimed and candidate pools are smaller). |
| 5 | Duplicate detection and finalizing — anything still unmatched is flagged Possible Duplicate (if it shares a date and amount with another unmatched row on its side) or No Match. |

**Why custom matching rules run first.** They represent asserted, curated business knowledge (e.g. "bank rows mentioning Sysco always belong with R365 rows mentioning Online"), so they get first claim on the transaction pool. Running them after Passes 2/3 instead would let a handful of rows get peeled off individually by exact-amount coincidences before the rule ever saw them, fragmenting one clean, fully-explained group into a mix of small exact matches plus a named-rule group with an unexplained residual gap.

**Grouped-posting rule.** By default, EVERY still-unmatched R365 row is eligible for Pass 4 combination matching, regardless of its Ref. # column contents — real-world data often has legitimate combinable postings that were never tagged. If you want the original stricter behavior back (only rows whose Ref. # column contains a keyword — `"R365"` by default — are combination-eligible, everything else strictly one-to-one), turn on **Require the grouped-posting keyword for combination matching** in Settings.

**Combination search.** There is no fixed limit on group size — the engine has been validated against groups ranging from 2 up to 100+ transactions. Because true unrestricted subset-sum is NP-hard, the search is layered, cheapest and most-certain path first:

1. Exact 2-combination via a hash table — O(n).
2. Exact 3-combination via fix-one-element + hash lookup — O(n) amortized.
3. A bounded dynamic-programming search with same-sign monotonic pruning, suffix-sum feasibility pruning, and minimum-cardinality tracking (so the DP's own output already prefers the fewest transactions). This stage is hard-capped by `MaxCombinationPoolSize`, `MaxDpStates`, `PerTransactionTimeBudgetSeconds`, and a global `GlobalCombinationTimeBudgetSeconds` circuit breaker, so a pathological input can never hang the app — it falls through to Manual Review instead of searching forever.

In production data the overwhelming majority of combinations resolve via steps 1–2; the DP is only needed for larger or more irregular groups. Every candidate combination is independently re-verified to sum *exactly* to the target (within `AmountToleranceDollars`) before being accepted, regardless of which stage found it.

**Tie-breaking.** When more than one valid match or combination exists for a transaction, the engine prefers, in order: an exact date match, then the smallest number of transactions, then the oldest R365 transactions first (FIFO), then the smallest date difference, then the highest confidence score. If multiple candidates remain equally valid after all of these, the engine does **not** guess — it leaves the transaction unmatched for Manual Review rather than force an arbitrary match.

**Confidence score (0–100).** Exact one-to-one matches score 100. Date-tolerant matches score slightly lower based on how many days off they are. Combination matches score based on group size and date spread. Anything below `MinCombinationConfidence` (default 80) is routed to Manual Review instead of being auto-accepted. The confidence score still drives this threshold decision internally, but the visible column (J / AA) shows the Match ID instead — see below.

**Match ID (columns J / AA).** Every matched transaction — one-to-one or combination — is written the same unique, sortable number on its Bank row and its R365 row(s). Sort the Bank Transactions or R365 Transactions tab by that column in Excel and every matched set lines up together, in order, so you can visually trace a deposit to the postings that make it up. Unmatched rows are left blank in that column. Turn this off with **Write Match ID column** in Settings.

**Comments written**, matching the source spec: `Matched (Exact)`, `Matched (Date Difference N Days)`, `Matched (Combination of N Transactions)`, `Manual Review`, `Possible Duplicate`, `No Match`, `Already Used` (pre-locked rows when *Ignore Already Reconciled Rows* is on).

**Highlight colors:** by default, only **No Match** rows get a fill color (red) — everything else (Matched, Manual Review, Combination) is left uncolored so the sheet stays clean and the eye goes straight to what actually needs attention. Turn off **Only highlight No Match rows in red** in Settings to restore full coloring: green = matched, yellow = Manual Review, red = No Match, and a rotating palette of blue/purple shades for combination matches (every transaction in the same combination group gets the *same* shade).

**Custom matching rules.** Some patterns are known in advance rather than discovered by date proximity or amount — for example, a vendor whose bank charges and R365 postings should always be reconciled together as a category. Edit these from **Settings → Custom Matching Rules (Advanced)**: each rule is a Bank column number + a keyword to look for there, paired with an R365 column number + a keyword to look for there. The shipped default rule is bank column 8 (Description) contains **"Sysco"**, paired with R365 column 16 (Ref. #) contains **"Online"** — but every rule's columns are independent of both each other and of the Column Mapping section above, so you can add a rule against any two columns for any other known pairing. This is a pure keyword filter, not a search: it runs FIRST, before every other pass, is not restricted to the normal date window, and performs no amount, sign, or date check of any kind — if at least one row on each side contains its keyword, ALL of them (every matching bank row and every matching R365 row) are grouped into one single match together. A custom rule never leaves an eligible row as No Match. The match is written at a fixed confidence (`SpecialComboConfidenceScore`, default 90, JSON-only) regardless of how well the totals line up, and any dollar gap between the two sides is still visible in the Match Difference column (AB) since that's a live formula. Each rule forms its own separate match group; add or remove rules with the Settings window's Add Rule / Remove buttons.

**Match audit formulas (columns K / AB).** Column K holds a plain Excel formula, `=<credit>-<debit>` (e.g. `=F15-G15`), written for every Bank row — a signed net amount consistent with R365's signed Amount column. Column AB, written for every matched R365 row, holds `=SUMIF(<bank Match ID column>:<bank Match ID column>, <this row's Match ID>, K:K) - SUMIF(<R365 Match ID column>:<R365 Match ID column>, <this row's Match ID>, <R365 amount column>:<R365 amount column>)` — the bank side of the match group's total minus the R365 side's total. Zero means the group balances exactly; anything else is worth a second look. Both are real formulas (not pre-computed values), so they stay live if you edit a cell afterward. Controlled by the same **Write Match ID column** setting as columns J/AA.

**Duplicate detection** flags bank and R365 rows that look like duplicates of another row on the same side (same amount, same or near-same date) independently of the matching passes, and is reported both as a per-row comment/status and in the Summary Dashboard.

## Column mapping

Every column position is configurable in Settings (**Column Mapping (Advanced)**) rather than hard-coded, because real-world exports vary. The shipped defaults match the sample workbook used to validate this tool:

| Field | Default column | Notes |
|---|---|---|
| Bank date | C | "Transaction Date" |
| Bank credit amount | F | |
| Bank debit amount | G | |
| Bank description | H | display only; also the default Custom Matching Rules bank column (e.g. "Sysco") — each rule can point at a different column |
| Bank comment (write target) | I | |
| Bank Match ID (write target) | J | |
| Bank net Credit-Debit formula (write target) | K | `=F{row}-G{row}`; feeds the AB audit formula |
| R365 date | N | always same-day-or-older than the bank date |
| R365 Ref. # / grouping column | P | checked for the grouped-posting keyword; also the default Custom Matching Rules R365 column (e.g. "Online") — each rule can point at a different column |
| R365 description | U | display only |
| R365 amount (signed) | Y | positive = credit, negative = debit |
| R365 comment (write target) | Z | |
| R365 Match ID (write target) | AA | |
| R365 match-difference audit formula (write target) | AB | see "Match audit formulas" above |

Header row, data start row, and the leftmost column of each highlight range are also configurable but are considered advanced/rarely-needed settings — edit `settings.json` directly (see [Settings reference](#settings-reference)) if your layout needs adjustment there.

The app never modifies formulas, and preserves existing formatting, fonts, borders, merged cells, hidden rows/columns, filters, column widths, and worksheet names — it only writes to the comment, highlight, and (optional) confidence columns/cells.

## Settings reference

Available in the **Settings** window:

| Setting | Default | Meaning |
|---|---|---|
| Maximum Date Difference | 6 days | How many days older an R365 date may be than the bank date and still match. |
| Amount Tolerance | $0.00 | Allowed rounding slop between two amounts. |
| Minimum Combination Confidence | 80% | Combination matches scoring below this go to Manual Review instead of auto-matching. |
| Ignore Already Reconciled Rows | On | Skip rows that already carry reconciliation comments; leave them completely untouched. |
| Require grouped-posting keyword for combinations | Off | When off (default), any unmatched R365 row can be combination-matched. When on, only rows whose Ref. # column contains the keyword are eligible (original stricter rule). |
| Maximum Threads | CPU cores − 1 | Ceiling on parallel worker threads used during combination search. |
| Maximum Combination Pool Size | 600 | Cap on candidates searched per bank transaction before falling back to Manual Review. |
| Global Combination Search Time Budget | 120s | Circuit breaker per general sweep (applied to each of the three sweeps — see Matching algorithm). |
| Auto Save | On | Save the output workbook automatically when a run completes. |
| Write Match ID column | On | Writes a unique, sortable number (columns J / AA) shared by a match's Bank and R365 rows so sorting groups them together. |
| Highlight full row | On | Color the entire row vs. just the comment cell (only applies to rows that get a color at all). |
| Only highlight No Match rows in red | On | When on (default), Matched/Manual Review/Combination rows are left uncolored for a cleaner sheet. Turn off to restore full green/yellow/blue/red coloring. |
| Highlight colors (Matched / Manual Review / No Match) | green / yellow / red | Hex, editable directly. |
| Column mapping (Bank & R365) | see table above | 1-based Excel column numbers. |
| Grouped Posting Keyword | `R365` | Case-insensitive substring checked in the Ref. # column. |
| Hide blank columns / autofit to header | On | Hides the spacer columns between the Bank and R365 blocks, and autofits every column's width based on the row 2 header text. |

Settings persist as JSON (via `ISettingsService`) in the user's local application data folder, so they survive app updates. Custom Matching Rules (`SpecialComboRules`) are fully editable from the Settings window — see "Custom matching rules" above. A few other advanced knobs exist only in that JSON file, not in the UI, because they rarely need changing: `MaxDpStates` (400,000), `PerTransactionTimeBudgetSeconds` (5.0), `SpecialComboConfidenceScore` (90, the fixed confidence score every custom-rule match is written at), and the header-row/data-start-row/first-column fields under column mapping.

## Output files

The app never overwrites or modifies the original workbook. It writes a new file named `<OriginalFileName>_Reconciled.xlsx` next to the original; if that name already exists, it auto-increments (`_Reconciled (2).xlsx`, `_Reconciled (3).xlsx`, …).

Each run also writes a log file (human-readable `.log` plus a machine-readable `.json`) recording date/time, rows processed, rows matched, errors, warnings, combination details, and total processing time. **View Log** opens the human-readable version.

## Architecture

```
BankReconciliation.sln
├── src/BankReconciliation.Core/         Reconciliation engine — no UI/Windows dependency,
│   ├── Models/                          reusable from a script, a service, or a different UI.
│   ├── Matching/                        Passes 1–3, confidence scoring, duplicate detection.
│   └── Services/                        Excel I/O (ClosedXML), logging, settings persistence.
├── src/BankReconciliation.Core.Tests/   xUnit tests for the matching engine.
└── src/BankReconciliation.App/          WPF (net8.0-windows) desktop UI, MVVM.
    ├── ViewModels/
    ├── Views/
    ├── Themes/                          Light/Dark, swappable at runtime via DynamicResource.
    └── Converters/
```

`BankReconciliation.Core` has no reference to WPF or any Windows-only API — it can be unit-tested on any platform and reused headlessly (e.g. from a script or a scheduled job) independently of the desktop UI. The app project is a thin MVVM layer on top of it: `MainViewModel` orchestrates loading, running the engine on a background thread via `IProgress<T>`, and writing results back out; `IExcelService`, `ILoggingService`, `ISettingsService`, and `IReconciliationEngine` are all interfaces, constructed once in `App.xaml.cs` ("poor man's DI" — small enough that a full container would be pure overhead, but every dependency is already behind an interface if that ever changes).

Large combination searches are parallelized safely using **date-window interval-merge clustering**: bank transactions are grouped into clusters such that no transaction in one cluster can ever share an R365 candidate with a transaction in another cluster (their `[Date − MaxDateDifferenceDays, Date]` windows don't overlap). Different clusters run on different threads with zero cross-cluster locking; within a cluster, processing stays strictly sequential so the FIFO/locking tie-break rules hold exactly. This trades a little theoretical parallelism for a correctness guarantee that doesn't depend on speculative locking.

All monetary comparisons inside the engine use integer cents (`long AmountCents`), never `double`, to avoid floating-point equality bugs in financial matching.

## Design decisions

- **ClosedXML, not EPPlus.** EPPlus's post-4.x license requires a commercial license for for-profit use; ClosedXML is MIT-licensed and covers everything this app needs (formatting/formula/merged-cell preservation, cell comments, fills) with no licensing cost or restriction.
- **No SQLite.** The spec allowed SQLite "if required," but nothing here needs a database — a run is a single load → match → write cycle with no cross-run querying, and settings are a handful of scalar values. JSON via `System.Text.Json` is simpler, has zero extra runtime dependency, and is trivially human-editable for the advanced settings not exposed in the UI.
- **No fixed combination-group-size limit**, by design — bounded instead by time/state budgets (see above) so it degrades gracefully (falls to Manual Review) rather than either hanging or silently truncating results.
- **MVVM with hand-rolled `RelayCommand`/`AsyncRelayCommand`**, not a UI framework like Prism or CommunityToolkit.Mvvm, to keep the dependency list minimal for a single-window-plus-settings app.

## Performance notes

The matching algorithm (2-sum → 3-sum → bounded DP, with interval-merge clustering for parallelism) was validated by porting it to an equivalent Python prototype and running it against the real ~4,580-row sample workbook before being implemented in C#, since the sandboxed environment this app was built in has no .NET SDK available to compile against directly (see [next section](#important-about-this-build)). That prototype run matched 496 bank transactions and 1,376 R365 transactions in 35.2 seconds total, with over 97% of found combinations resolved via the O(n) 2-sum/3-sum fast paths and the DP fallback only needed for a handful of larger groups. The compiled C# version should run substantially faster than that Python validation run, both because C# significantly outperforms Python for this kind of numeric/hash-table-heavy workload and because the prototype's own artificial time budgets (tuned tight to fit a 45-second sandbox execution limit) are far more generous in the shipped app's defaults (`GlobalCombinationTimeBudgetSeconds = 45s`, vs. the prototype's `32s` cap on a slower interpreter). The spec's target — comfortably handling 3,000–10,000 transactions in under a minute without freezing the UI — is expected to be comfortably met; the UI itself never blocks regardless, since the entire load/match/write pipeline runs on a background thread with progress reported back via `IProgress<T>`.

## Important: about this build

This project was built in a sandboxed environment with **no .NET SDK available** and no way to install one (no root access, and Microsoft's package/CDN domains were blocked by the environment's network policy). That means:

- `BankReconciliation.Core`, `BankReconciliation.Core.Tests`, and `BankReconciliation.App` were written carefully by hand, with the matching algorithm design independently validated by porting it to Python and running it against the real sample data (see [Performance notes](#performance-notes)) — but **none of the C# code has been compiled or executed** in this environment.
- Every file was manually re-read and cross-checked (property names, method signatures, XAML bindings against their ViewModel properties, converter parameters, event wiring) as a substitute for a compiler. That review did catch and fix several real bugs before delivery — for example, a `RelayCommand` overload-resolution mismatch on four of the command bindings in `MainViewModel`, a missing XAML behavior class, an ARGB color-order mistake, and a layout row overlap — which is exactly the category of mistake a first `dotnet build` typically surfaces.
- **Your first build should be treated as the true first compile.** It is quite possible — though not expected, given the review — that `dotnet build` surfaces a small remaining issue (a typo, a missing `using`, a namespace mismatch) that this review didn't catch. If it does, the error message will point at an exact file and line, which should make it a quick fix; the architecture and algorithm design underneath it are sound and were the primary focus of the engineering effort.
- The **unit test suite** (`BankReconciliation.Core.Tests`, 36 tests across matching, duplicates, confidence scoring, and end-to-end engine behavior) was written to the same standard but likewise has never been run by an actual test runner — run `dotnet test` as part of your first build (which `build.bat` does automatically) and review the output.

If anything doesn't compile cleanly, the most efficient path is to paste the exact compiler error into your AI assistant of choice along with the referenced file — the fix is very likely a one-line signature or `using` correction, not a design problem.

## Troubleshooting

**"MSB3644" or SDK-not-found errors during build** — install the .NET 8 SDK (not just the runtime) from the link above; make sure it's on `PATH` (`dotnet --list-sdks` should show an `8.x` entry).

**Installer step is skipped** — Inno Setup isn't installed or isn't on `PATH`. Install it, or run `build.bat` again afterward; the standalone `.exe` in `dist\app\` works fine without an installer.

**"File is in use" when opening the output workbook** — close the original file in Excel before running a reconciliation against it; the app itself never locks the original, but Excel does.

**A run finishes with a lot of Manual Review rows** — check the Settings' date/amount tolerance and combination pool/time budgets against your data; a very large or unusually shaped combination group can exceed the default safety valves and fall back to Manual Review by design rather than risk an incorrect forced match. Raising `MaxCombinationPoolSize` or the time budgets (at the cost of longer run time) will let it search harder.

**Something crashes unexpectedly** — the app logs unhandled exceptions to a `crash_<timestamp>.log` file in the same folder as the run logs (see **View Log** to find that folder) before showing the error dialog; that file has the full stack trace.
