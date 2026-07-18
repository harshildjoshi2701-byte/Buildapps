using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using BankReconciliation.Core.Matching;
using BankReconciliation.Core.Models;
using BankReconciliation.Core.Services;

namespace BankReconciliation.App.ViewModels;

/// <summary>
/// Drives the main window end to end: pick a file, run the engine on a
/// background thread while reporting progress back to the UI thread, then
/// expose the results (summary KPIs + two result grids) and let the user
/// open the output file or the log.
///
/// Bank/R365RowsView are plain ObservableCollections that are manually kept
/// in sync with BankRows/R365Rows via RefreshFilteredViews(), rather than
/// wrapping them in a WPF ICollectionView. That's a deliberate change from
/// the more "idiomatic" CollectionViewSource.GetDefaultView() approach: on
/// some .NET 8 SDK patch versions the WPF XAML markup compiler's internal
/// temporary build pass fails to resolve System.Windows.Data.ICollectionView
/// specifically (while still resolving other WPF types fine), which blocks
/// the whole project from compiling. This approach sidesteps that entirely
/// while keeping the same filter-by-status behavior.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly IExcelService _excelService;
    private readonly IReconciliationEngine _engine;

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _runStopwatch = new();

    /// <summary>Snapshot of the most recent run's transaction lists, kept
    /// only so <see cref="ExportReport"/> can build a report on demand
    /// without re-running the engine. Not used for display — BankRows/R365Rows
    /// (via TransactionRowViewModel) serve that.</summary>
    private IReadOnlyList<TransactionRecord>? _lastBankTransactions;
    private IReadOnlyList<TransactionRecord>? _lastR365Transactions;

    public MainViewModel(
        ISettingsService settingsService,
        ILoggingService loggingService,
        IExcelService excelService,
        IReconciliationEngine engine)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _excelService = excelService;
        _engine = engine;

        Settings = _settingsService.Load();
        RecentFiles = new ObservableCollection<string>(Settings.RecentFiles);
        BankRows = new ObservableCollection<TransactionRowViewModel>();
        R365Rows = new ObservableCollection<TransactionRowViewModel>();

        BrowseCommand = new RelayCommand(Browse);
        OpenRecentFileCommand = new RelayCommand(p => LoadFile((string)p!));
        StartCommand = new AsyncRelayCommand(RunReconciliationAsync, () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenOutputCommand = new RelayCommand(OpenOutputFile, () => !string.IsNullOrEmpty(OutputFilePath));
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, () => !string.IsNullOrEmpty(OutputFilePath));
        OpenLogCommand = new RelayCommand(OpenLog, () => !string.IsNullOrEmpty(LastLogFilePath));
        ExportReportCommand = new RelayCommand(ExportReport, () => !string.IsNullOrEmpty(OutputFilePath));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ApplyStatusFilterCommand = new RelayCommand(p => StatusFilter = (string)p!);
    }

    // ---- Requests the view fulfills (file dialogs, window opens) ----
    public event Func<string?>? BrowseForFileRequested;
    public event Action<SettingsViewModel>? SettingsRequested;
    public event Action<string>? ErrorRequested;

    // ---- Commands ----
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenRecentFileCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ApplyStatusFilterCommand { get; }

    public ReconciliationSettings Settings { get; private set; }
    public ObservableCollection<string> RecentFiles { get; }
    public ObservableCollection<TransactionRowViewModel> BankRows { get; }
    public ObservableCollection<TransactionRowViewModel> R365Rows { get; }

    /// <summary>Filtered view of <see cref="BankRows"/> shown in the grid — kept
    /// in sync by <see cref="RefreshFilteredViews"/> rather than a live WPF
    /// ICollectionView (see class remarks).</summary>
    public ObservableCollection<TransactionRowViewModel> BankRowsView { get; } = new();

    /// <summary>Filtered view of <see cref="R365Rows"/> — see <see cref="BankRowsView"/>.</summary>
    public ObservableCollection<TransactionRowViewModel> R365RowsView { get; } = new();

    private string? _selectedFilePath;
    public string? SelectedFilePath
    {
        get => _selectedFilePath;
        set { if (SetField(ref _selectedFilePath, value)) { OnPropertyChanged(nameof(CanStart)); RelayCommand.RaiseCanExecuteChanged(); } }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { SetField(ref _isRunning, value); OnPropertyChanged(nameof(CanStart)); }
    }

    public bool CanStart => !IsRunning && !string.IsNullOrEmpty(SelectedFilePath) && File.Exists(SelectedFilePath);

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; set => SetField(ref _progressPercent, value); }

    private string _statusMessage = "Ready. Browse for a workbook to begin.";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private string _passMessage = string.Empty;
    public string PassMessage { get => _passMessage; set => SetField(ref _passMessage, value); }

    private string _elapsedDisplay = "00:00";
    public string ElapsedDisplay { get => _elapsedDisplay; set => SetField(ref _elapsedDisplay, value); }

    private string _remainingDisplay = "--:--";
    public string RemainingDisplay { get => _remainingDisplay; set => SetField(ref _remainingDisplay, value); }

    private string? _outputFilePath;
    public string? OutputFilePath
    {
        get => _outputFilePath;
        set { SetField(ref _outputFilePath, value); RelayCommand.RaiseCanExecuteChanged(); }
    }

    private string? _lastLogFilePath;
    public string? LastLogFilePath
    {
        get => _lastLogFilePath;
        set { SetField(ref _lastLogFilePath, value); RelayCommand.RaiseCanExecuteChanged(); }
    }

    private ReconciliationSummary? _summary;
    public ReconciliationSummary? Summary { get => _summary; set => SetField(ref _summary, value); }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; set => SetField(ref _hasResults, value); }

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetField(ref _statusFilter, value))
            {
                RefreshFilteredViews();
            }
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                RefreshFilteredViews();
            }
        }
    }

    /// <summary>Rebuilds BankRowsView/R365RowsView from BankRows/R365Rows using
    /// the current StatusFilter and SearchText. Called whenever either the
    /// underlying rows or either filter changes.</summary>
    private void RefreshFilteredViews()
    {
        BankRowsView.Clear();
        foreach (var row in BankRows.Where(RowFilter)) BankRowsView.Add(row);

        R365RowsView.Clear();
        foreach (var row in R365Rows.Where(RowFilter)) R365RowsView.Add(row);
    }

    private bool RowFilter(TransactionRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) && !MatchesSearch(row, SearchText)) return false;

        if (StatusFilter == "All") return true;
        return StatusFilter switch
        {
            "Matched" => row.Status is MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant or MatchStatus.MatchedCombination,
            "Manual Review" => row.Status is MatchStatus.ManualReview or MatchStatus.PossibleDuplicate,
            "No Match" => row.Status == MatchStatus.NoMatch,
            _ => true,
        };
    }

    /// <summary>Plain substring search (case-insensitive) across every field
    /// a user would plausibly recognize a transaction by — deliberately not
    /// restricted to Description, since the Grouping value or the engine's
    /// own comment ("Manual Review — Grouping ...") is often exactly what
    /// someone is trying to find at review time.</summary>
    private static bool MatchesSearch(TransactionRowViewModel row, string search) =>
        row.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.Comment.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.GroupingKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.AmountDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.DateDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase);

    public bool IsDarkMode
    {
        get => Settings.DarkMode;
        set
        {
            if (Settings.DarkMode == value) return;
            Settings.DarkMode = value;
            OnPropertyChanged();
            App.ApplyTheme(value);
            _settingsService.Save(Settings);
        }
    }

    private void Browse(object? _)
    {
        var path = BrowseForFileRequested?.Invoke();
        if (!string.IsNullOrWhiteSpace(path)) LoadFile(path);
    }

    private void LoadFile(string path)
    {
        SelectedFilePath = path;
        StatusMessage = $"Selected: {Path.GetFileName(path)}";
        HasResults = false;
        OutputFilePath = null;
    }

    private async Task RunReconciliationAsync()
    {
        if (string.IsNullOrEmpty(SelectedFilePath)) return;

        IsRunning = true;
        HasResults = false;
        ProgressPercent = 0;
        _cts = new CancellationTokenSource();
        _runStopwatch.Restart();
        BankRows.Clear();
        R365Rows.Clear();
        BankRowsView.Clear();
        R365RowsView.Clear();

        var progressReporter = new Progress<ReconciliationProgress>(OnProgress);
        LoadedWorkbook? loaded = null;

        try
        {
            StatusMessage = "Opening workbook…";
            loaded = await Task.Run(() => _excelService.Load(SelectedFilePath, Settings.Columns, Settings.IgnoreAlreadyReconciledRows), _cts.Token);

            StatusMessage = $"Loaded {loaded.BankTransactions.Count:N0} bank / {loaded.R365Transactions.Count:N0} R365 transactions. Reconciling…";

            var result = await _engine.RunAsync(loaded.BankTransactions, loaded.R365Transactions, Settings, progressReporter, _cts.Token);

            StatusMessage = "Writing results back to Excel…";
            var outputPath = _excelService.BuildOutputPath(SelectedFilePath);
            await Task.Run(() => _excelService.WriteResultsAndSave(
                loaded, loaded.BankTransactions, loaded.R365Transactions,
                Settings.Colors, Settings.Columns, Settings.WriteConfidenceColumn, Settings.HighlightFullRow,
                Settings.HighlightOnlyUnmatched, Settings.TidyColumnsOnOutput, outputPath), _cts.Token);

            result.Summary.OutputFilePath = outputPath;
            result.Log.SourceFile = SelectedFilePath;
            result.Log.OutputFile = outputPath;
            var logPath = _loggingService.WriteLog(result.Log);
            result.Summary.LogFilePath = logPath;

            Summary = result.Summary;
            OutputFilePath = outputPath;
            LastLogFilePath = logPath;
            PopulateGrids(result);
            HasResults = true;
            _lastBankTransactions = result.BankTransactions;
            _lastR365Transactions = result.R365Transactions;

            _settingsService.AddRecentFile(Settings, SelectedFilePath);
            RecentFiles.Clear();
            foreach (var f in Settings.RecentFiles) RecentFiles.Add(f);

            StatusMessage = $"Done. {result.Summary.MatchedBankTransactions:N0}/{result.Summary.TotalBankTransactions:N0} bank transactions matched " +
                             $"({result.Summary.BankMatchPercentage:F1}%) in {result.Summary.ProcessingTime.TotalSeconds:F1}s.";
            PassMessage = string.Empty;
            ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed — see error dialog.";
            ErrorRequested?.Invoke($"Reconciliation failed:\n\n{ex.Message}");
        }
        finally
        {
            loaded?.Dispose();
            _runStopwatch.Stop();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(ReconciliationProgress p)
    {
        ProgressPercent = p.OverallPercent;
        PassMessage = $"Pass {p.CurrentPass}/{p.TotalPasses}: {p.PassName} — {p.StatusMessage}";
        ElapsedDisplay = p.Elapsed.ToString(@"mm\:ss");
        RemainingDisplay = p.EstimatedRemaining is { } r ? r.ToString(@"mm\:ss") : "--:--";
    }

    /// <summary>Display order per spec: matched transactions first, unmatched
    /// last, "to make review much easier." Rows are grouped into three tiers
    /// — fully resolved matches, rows still needing attention, and rows with
    /// no candidate at all — or ties within a tier broken by original row
    /// number so the order stays stable and predictable.</summary>
    private static int DisplaySortRank(TransactionRecord t) => t.Status switch
    {
        MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant or MatchStatus.MatchedCombination => 0,
        MatchStatus.ManualReview or MatchStatus.PossibleDuplicate => 1,
        MatchStatus.NoMatch => 2,
        _ => 3, // Unmatched should never survive to display, but sorts last defensively if it does
    };

    private void PopulateGrids(ReconciliationResult result)
    {
        foreach (var b in result.BankTransactions.OrderBy(DisplaySortRank).ThenBy(t => t.RowNumber))
            BankRows.Add(new TransactionRowViewModel(b));
        foreach (var r in result.R365Transactions.OrderBy(DisplaySortRank).ThenBy(t => t.RowNumber))
            R365Rows.Add(new TransactionRowViewModel(r));
        RefreshFilteredViews();
    }

    private void Cancel() => _cts?.Cancel();

    private void OpenSettings(object? _)
    {
        var vm = new SettingsViewModel(_settingsService, Settings);
        vm.SettingsSaved += (_, _) =>
        {
            Settings = vm.Result;
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(IsDarkMode));
        };
        SettingsRequested?.Invoke(vm);
    }

    private void OpenOutputFile()
    {
        if (string.IsNullOrEmpty(OutputFilePath) || !File.Exists(OutputFilePath)) return;
        Process.Start(new ProcessStartInfo(OutputFilePath) { UseShellExecute = true });
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(OutputFilePath)) return;
        var dir = Path.GetDirectoryName(OutputFilePath);
        if (dir is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputFilePath}\"") { UseShellExecute = true });
    }

    private void OpenLog()
    {
        if (string.IsNullOrEmpty(LastLogFilePath) || !File.Exists(LastLogFilePath)) return;
        Process.Start(new ProcessStartInfo(LastLogFilePath) { UseShellExecute = true });
    }

    private void ExportReport()
    {
        if (_lastBankTransactions is null || _lastR365Transactions is null || string.IsNullOrEmpty(SelectedFilePath))
            return;

        try
        {
            var path = ReportExporter.ExportUnmatchedReport(_lastBankTransactions, _lastR365Transactions, SelectedFilePath);
            StatusMessage = $"Needs-review report exported: {Path.GetFileName(path)}";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorRequested?.Invoke($"Failed to export the needs-review report:\n\n{ex.Message}");
        }
    }

    private void ToggleTheme(object? _) => IsDarkMode = !IsDarkMode;
}
