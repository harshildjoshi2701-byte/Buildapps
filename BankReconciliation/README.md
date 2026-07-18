# Bank Reconciliation Tool

A Windows desktop application that reconciles bank transactions against R365 (Restaurant365) transactions across two worksheets in an Excel workbook — matching amounts, dates, and explicit Grouping-column linkages, writing comments and color highlights back into the sheets, and producing a summary dashboard, all without touching the original file.

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

The app opens a workbook containing two separate worksheets — a **Bank Transactions** sheet and an **R365 Transactions** sheet — and matches rows between them:

- Named/curated keyword rules first (e.g. "Sysco"), then explicit **Grouping-column** linkages (rows sharing a Grouping value are summed per side and compared as one unit — see below), then exact amount + exact date matches, then exact amount with a configurable date tolerance, then many-to-one **combination matches** (a single bank deposit that corresponds to a bundle of several R365 postings summed together).
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

1. **Browse Excel File** — pick the workbook to reconcile, pick one from **Recent Files**, or just **drag and drop** a `.xlsx` anywhere onto the window.
2. **Start Reconciliation** (`F5`) — runs on a background thread with a progress bar, current-pass status, elapsed time, and estimated remaining time. **Cancel** (`Escape`) stops a run in progress.
3. When it finishes, results are sorted **matched first, unmatched last** so the rows needing attention are easy to find. The **Summary Dashboard** tab shows the KPIs (totals, matched/unmatched counts, match percentage, processing time, etc.), and the **Bank Transactions** / **R365 Transactions** tabs show every row with its Grouping value, status pill, confidence score, and comment — filterable by All / Matched / Manual Review / No Match, and by free-text **Search** across description, comment, Grouping value, amount, date, and row number.
4. **Open Output File** opens the reconciled workbook; **Export Needs-Review Report** writes a CSV of just the No Match / Manual Review / Possible Duplicate rows, for sharing without the full workbook; **Show in Folder** reveals the output in Explorer; **View Log** opens the run's log file.
5. **Settings** (gear icon) exposes every matching-rule, worksheet/column-mapping, and performance knob — see [Settings reference](#settings-reference).
6. The moon/sun icon toggles Dark Mode. `Ctrl+O` opens the browse dialog from anywhere.

## Matching algorithm

Matching runs in six passes. Once a transaction (bank or R365) is matched in any pass, it is **locked** and never reconsidered by a later pass or a later transaction.

| Pass | What it does |
|---|---|
| 1 | Custom matching rules — see below. Pure keyword grouping, not a search: no amount, sign, or date check. Runs FIRST, before anything else, because these are curated business rules rather than algorithmic guesses. |
| 2 | Grouping-column bucket match — see below. Rows sharing a Grouping value are summed per side and compared as one unit; O(n) hash-bucket, not a search. |
| 3 | One-to-one, exact amount, exact same date. |
| 4 | One-to-one, exact amount, R365 date up to `MaxDateDifferenceDays` days *older* than the bank date (R365 dates are never newer, per the source data's convention). |
| 5 | General combination matching — date-windowed subset-sum search, run THREE times over (each extra pass over whatever's still unmatched frequently finds matches the previous pass could not, now that other transactions have been claimed and candidate pools are smaller). |
| 6 | Duplicate detection and finalizing — anything still unmatched is flagged Possible Duplicate (if it shares a date and amount with another unmatched row on its side) or No Match. |

**Why custom matching rules run first, and Grouping second.** Named rules represent asserted, curated business knowledge (e.g. "bank rows mentioning Sysco always belong with R365 rows also tagged Sysco"), so they get first claim on the transaction pool — see "Custom matching rules" below for why this specifically matters for the real workbook's data. Grouping-column matching runs immediately after, still ahead of every amount/date-based pass, because an explicit Grouping value is the next-most-certain signal available (stronger than a coincidental amount+date match). Running either after Pass 3/4 instead would let a handful of rows get peeled off individually by exact-amount coincidences before the rule/bucket ever saw them, fragmenting a clean, fully-explained group into a mix of small exact matches plus a residual, unexplained gap.

**Grouping column (new).** Both sheets carry a "Grouping" column. A populated Grouping value is an ASSERTION the workbook has already made about which rows belong together — not a hint to search for a combination — so matching it is a hash-bucket operation: sum every Bank row sharing that exact value, sum every R365 row sharing it, compare. If the two totals tie out (within `AmountToleranceDollars`), every row in the bucket is matched together as one unit (`Matched (Grouping "X", N Transactions)`). If both sides have rows for that value but the totals don't tie out, every row is still locked and grouped (so you can see the variance) but flagged `Manual Review` instead. If only one sheet has rows for that value, they're locked and grouped as `No Match` — the Match ID column still clusters them together so the combined total is visible, but there's nothing on the other sheet to reconcile against. A blank Grouping value falls through to the normal per-row matching passes below (Passes 3–5) unchanged; a non-blank value is resolved by this mechanism exclusively and never falls through to per-row matching, even if the bucket doesn't tie out — see `GroupingMatcher`'s code remarks for the full reasoning, including why it deliberately runs AFTER named rules rather than before (that ordering is what keeps a Sysco-style keyword value in the same Grouping column from being incorrectly bucketed and sum-compared).

**Grouped-posting rule (older, separate mechanism).** This is unrelated to the Grouping column above — it's the original per-row combination-eligibility flag, based on whether the R365 **Ref. #** column contains a keyword. By default, EVERY still-unmatched R365 row is eligible for Pass 5 combination matching, regardless of its Ref. # column contents — real-world data often has legitimate combinable postings that were never tagged. If you want the original stricter behavior back (only rows whose Ref. # column contains a keyword — `"R365"` by default — are combination-eligible, everything else strictly one-to-one), turn on **Require the grouped-posting keyword for combination matching** in Settings.

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

**Custom matching rules.** Some patterns are known in advance rather than discovered by date proximity or amount — for example, a vendor whose bank charges and R365 postings should always be reconciled together as a category, and whose totals were never expected to tie out exactly. Edit these from **Settings → Custom Matching Rules (Advanced)**: each rule is a Bank column number + a keyword to look for there, paired with an R365 column number + a keyword to look for there. The shipped default rule is bank column 8 contains **"Sysco"**, paired with the R365 **Grouping column** also containing **"Sysco"** — the real workbook uses that same Grouping column for both true numeric linking IDs (which DO sum-match, and are handled by the Grouping-column pass above) and the keyword "Sysco" (which never has). Keeping Sysco on this named-rule mechanism rather than the Grouping-column pass is deliberate; every rule's columns are independent of both each other and of the Column Mapping section above, so you can add a rule against any two columns for any other known pairing. This is a pure keyword filter, not a search: it runs FIRST, before every other pass (including Grouping), is not restricted to the normal date window, and performs no amount, sign, or date check of any kind — if at least one row on each side contains its keyword, ALL of them (every matching bank row and every matching R365 row) are grouped into one single match together. A custom rule never leaves an eligible row as No Match. The match is written at a fixed confidence (`SpecialComboConfidenceScore`, default 90, JSON-only) regardless of how well the totals line up, and any dollar gap between the two sides is still visible in the Match Difference column since that's a live formula. Each rule forms its own separate match group; add or remove rules with the Settings window's Add Rule / Remove buttons.

**Match audit formulas.** The Bank sheet's diff column holds a plain Excel formula, `=<credit>-<debit>` (e.g. `=F15-G15`), written for every Bank row — a signed net amount consistent with R365's signed Amount column. The R365 sheet's diff column, written for every matched R365 row, holds a cross-sheet formula — `=SUMIF('Bank Transactions'!<bank Match ID column>:<bank Match ID column>, <this row's Match ID>, 'Bank Transactions'!<bank diff column>:<bank diff column>) - SUMIF(<R365 Match ID column>:<R365 Match ID column>, <this row's Match ID>, <R365 amount column>:<R365 amount column>)` — the bank side of the match group's total minus the R365 side's total. Zero means the group balances exactly; anything else is worth a second look (this is exactly what surfaces a non-tying Grouping bucket's variance, too). Both are real formulas (not pre-computed values), so they stay live if you edit a cell afterward, and both reference the sheet each column actually lives on now that Bank and R365 are separate worksheets. Controlled by the same **Write Match ID column** setting as the Match ID columns.

**Duplicate detection** flags bank and R365 rows that look like duplicates of another row on the same side (same amount, same or near-same date) independently of the matching passes, and is reported both as a per-row comment/status and in the Summary Dashboard.

## Column mapping

Every column position — and both worksheet names — is configurable in Settings (**Column Mapping (Advanced)**) rather than hard-coded, because real-world exports vary and this workbook's own layout has already changed shape multiple times during development.

> **These defaults are placeholders, not confirmed values.** The workbook moved from one worksheet with Bank/R365 side by side to two separate sheets partway through development, in a session that could analyze the real file's *shape* (row/column counts, the Grouping column's behavior) but did not have an exact header-row listing to build the table below from. Verify every column number here against the real workbook's header row before trusting a run's results — Settings → Column Mapping is where to fix any that are wrong.

| Field | Default sheet | Default column | Notes |
|---|---|---|---|
| Bank date | Bank Transactions | C | "Transaction Date" |
| Bank credit amount | Bank Transactions | F | |
| Bank debit amount | Bank Transactions | G | |
| Bank description | Bank Transactions | H | display only |
| Bank **Grouping** | Bank Transactions | H | same slot as Description above — reported to occupy the position Description held before the sheet split; also the default Custom Matching Rules bank column (checks for "Sysco") |
| Bank comment (write target) | Bank Transactions | I | |
| Bank Match ID (write target) | Bank Transactions | J | |
| Bank net Credit-Debit formula (write target) | Bank Transactions | K | `=F{row}-G{row}`; feeds the R365-side audit formula |
| R365 date | R365 Transactions | A | always same-day-or-older than the bank date |
| R365 **Grouping** | R365 Transactions | B | reported to occupy the position a "Location #" column held before the sheet split; also the default Custom Matching Rules R365 column (checks for "Sysco") |
| R365 Ref. # (older, separate mechanism) | R365 Transactions | C | checked for the grouped-posting keyword — see "Grouped-posting rule (older, separate mechanism)" above; NOT the same column as Grouping |
| R365 description | R365 Transactions | D | display only |
| R365 amount (signed) | R365 Transactions | E | positive = credit, negative = debit |
| R365 comment (write target) | R365 Transactions | F | |
| R365 Match ID (write target) | R365 Transactions | G | |
| R365 match-difference audit formula (write target) | R365 Transactions | H | see "Match audit formulas" above |

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
| Column mapping (Bank & R365) | see table above | 1-based Excel column numbers, plus both worksheet names. |
| Grouped Posting Keyword | `R365` | Case-insensitive substring checked in the R365 Ref. # column — the OLDER, separate mechanism, not the Grouping column. |
| Autofit to header | On | Autofits every column's width on both sheets based on the header row text. No longer hides a spacer range — that existed only when Bank and R365 shared one worksheet. |

Settings persist as JSON (via `ISettingsService`) in the user's local application data folder, so they survive app updates. Custom Matching Rules (`SpecialComboRules`) are fully editable from the Settings window — see "Custom matching rules" above. A few other advanced knobs exist only in that JSON file, not in the UI, because they rarely need changing: `MaxDpStates` (400,000), `PerTransactionTimeBudgetSeconds` (5.0), `SpecialComboConfidenceScore` (90, the fixed confidence score every custom-rule match is written at), the header-row/data-start-row/first-column fields under column mapping, and — new, not yet UI-exposed — `BankGroupingColumn` / `R365GroupingColumn` (the Grouping column position on each sheet; see [Column mapping](#column-mapping)).

## Output files

The app never overwrites or modifies the original workbook. It writes a new file named `<OriginalFileName>_Reconciled.xlsx` next to the original; if that name already exists, it auto-increments (`_Reconciled (2).xlsx`, `_Reconciled (3).xlsx`, …).

Each run also writes a log file (human-readable `.log` plus a machine-readable `.json`) recording date/time, rows processed, rows matched, errors, warnings, combination details, and total processing time. **View Log** opens the human-readable version.

**Export Needs-Review Report** (footer button, available once a run has results) writes `<OriginalFileName>_NeedsReview.csv` next to the original — every No Match, Manual Review, and Possible Duplicate row from both sheets (Side, Row, Date, Amount, Grouping, Description, Status, Comment), same auto-increment-on-collision naming. Meant for sharing just the follow-up items without sending the full workbook. Plain CSV, not another `.xlsx`.

## Architecture

```
BankReconciliation.sln
├── src/BankReconciliation.Core/         Reconciliation engine — no UI/Windows dependency,
│   ├── Models/                          reusable from a script, a service, or a different UI.
│   ├── Matching/                        GroupingMatcher (new), CombinationMatcher,
│   │                                    OneToOneMatcher, confidence scoring, duplicate detection.
│   └── Services/                        Excel I/O (ClosedXML) across two worksheets, logging,
│                                        settings persistence, ReportExporter (CSV, new).
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

`BankReconciliation.Core` and `BankReconciliation.Core.Tests` — where all the actual reconciliation logic and financial correctness live — **have now been genuinely compiled and tested**, in a Linux CI-style environment that installed .NET 8 SDK via Ubuntu's own apt archive (Microsoft's own CDN was blocked by that environment's network policy, but Ubuntu's default package repo carries `dotnet-sdk-8.0` directly and isn't). This closes out the original build/test gap described below for the Core engine specifically:

- A from-scratch `dotnet build` of `BankReconciliation.Core` succeeds with 0 warnings, 0 errors.
- All 68 xUnit tests in `BankReconciliation.Core.Tests` pass: 45 from the original hand-written suite, 9 covering `GroupingMatcher`, 6 covering `ReportExporter`, and 8 integration-style tests that build real `.xlsx` files on disk with ClosedXML and drive them through the actual two-worksheet `ExcelService` — the biggest, riskiest rewrite this session, otherwise covered only by manual reasoning like the App layer below.
- Two real, pre-existing bugs were found and fixed by this process, not just theorized about: `ReconciliationEngine.cs` referenced `SearchStats` unqualified when it's actually nested inside `CombinationMatcher` (a straightforward compile error); and `ConfidenceScorer.Combination()`'s count-penalty formula penalized even a clean 2-transaction combination, contradicting its own doc comment, which treats 2 transactions as the intended zero-penalty floor — the formula was re-anchored there rather than loosening the test that caught it.

**`BankReconciliation.App` (the WPF UI) is still unverified** — `Microsoft.NET.Sdk.WindowsDesktop` has no Linux build, so there is no way to compile or run a WPF project outside Windows. Everything below, written for the *original* v7 delivery, still applies specifically to the App project:

- It was written carefully by hand and manually re-read and cross-checked (property names, method signatures, XAML bindings against their ViewModel properties, converter parameters, event wiring) as a substitute for a compiler — that review did catch and fix several real bugs before the original delivery (a `RelayCommand` overload-resolution mismatch, a missing XAML behavior class, an ARGB color-order mistake, a layout row overlap) — but it has never actually been compiled or run.
- **Your first Windows build of the App project should be treated as the true first compile of that layer.** The error message, if any, will point at an exact file and line, which should make it a quick fix; the architecture underneath it is sound and was the primary focus of the engineering effort.
- If anything doesn't compile cleanly, the most efficient path is to paste the exact compiler error into your AI assistant of choice along with the referenced file — the fix is very likely a one-line signature or `using` correction, not a design problem.

**One more open item specific to the Grouping-column work:** the Column Mapping defaults for the new two-worksheet layout (see [Column mapping](#column-mapping)) are best-effort placeholders inferred from a prior analysis of the real file's *shape*, not a confirmed header-row listing — verify/correct them in Settings before trusting a run's results.

## Troubleshooting

**"MSB3644" or SDK-not-found errors during build** — install the .NET 8 SDK (not just the runtime) from the link above; make sure it's on `PATH` (`dotnet --list-sdks` should show an `8.x` entry).

**Installer step is skipped** — Inno Setup isn't installed or isn't on `PATH`. Install it, or run `build.bat` again afterward; the standalone `.exe` in `dist\app\` works fine without an installer.

**"File is in use" when opening the output workbook** — close the original file in Excel before running a reconciliation against it; the app itself never locks the original, but Excel does.

**A run finishes with a lot of Manual Review rows** — check the Settings' date/amount tolerance and combination pool/time budgets against your data; a very large or unusually shaped combination group can exceed the default safety valves and fall back to Manual Review by design rather than risk an incorrect forced match. Raising `MaxCombinationPoolSize` or the time budgets (at the cost of longer run time) will let it search harder.

**Something crashes unexpectedly** — the app logs unhandled exceptions to a `crash_<timestamp>.log` file in the same folder as the run logs (see **View Log** to find that folder) before showing the error dialog; that file has the full stack trace.
