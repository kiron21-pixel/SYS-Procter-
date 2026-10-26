using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Guardian.Core.Inventory;
using Guardian.Core.Modes;
using Guardian.Core.Performance;
using Guardian.Core.Reporting;

namespace Guardian.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GuardianHost _host;
    private List<AppRow> _allApps = [];

    public MainViewModel(GuardianHost host)
    {
        _host = host;
        var p = host.Adapter.Info;
        PlatformLine = $"{p.OsName}  ·  {p.Architecture}  ·  {(p.IsElevated ? "Administrator" : "Standard user")}";
        SupportNote = p.SupportNote ?? "";
        Features = FeatureStatus.For(p);
        foreach (var c in host.Adapter.SecurityChecks)
            Checks.Add(new CheckRowViewModel(CheckReport.NotImplementedYet(c.Id, c.Title, c.OnDemandOnly ? "On-demand check. Press Run to start it." : "Not run yet.")
                with { Implemented = true, Support = SupportStatus.Supported }, c.OnDemandOnly, () => c.RunAsync()));
        host.Modes.ModeChanged += _ => RefreshMode();
        RefreshMode();
        RefreshActivity();
    }

    // ---- Overview ---------------------------------------------------------
    public string PlatformLine { get; }
    public string SupportNote { get; }
    public IReadOnlyList<FeatureRow> Features { get; }
    [ObservableProperty] private string _overallSummary = "No checks have run yet. Guardian starts in Observe Only mode and has not changed anything.";
    [ObservableProperty] private bool _isBusy;

    // ---- Security ---------------------------------------------------------
    public ObservableCollection<CheckRowViewModel> Checks { get; } = [];

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var reports = await Task.Run(() => _host.RunDefaultChecksAsync());
            foreach (var r in reports)
                if (Checks.FirstOrDefault(c => c.Report.CheckId == r.CheckId) is { } row) row.Report = r;
            OverallSummary = ReportLanguage.Summarize(reports);
            await RefreshPerformanceAsync();
        }
        finally { IsBusy = false; }
    }

    // ---- Performance ------------------------------------------------------
    [ObservableProperty] private string _memoryText = "Not measured yet.";
    [ObservableProperty] private string _uptimeText = "";
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<ProcessRow> Processes { get; } = [];

    [RelayCommand]
    private async Task RefreshPerformanceAsync()
    {
        var snap = await Task.Run(() => _host.Adapter.Performance.CaptureAsync());
        MemoryText = snap.Memory is { } m ? $"{Bytes.Format(m.AvailableBytes)} available of {Bytes.Format(m.TotalBytes)}" : snap.Reports[0].Summary;
        UptimeText = $"Up for {(int)snap.Uptime.TotalDays} d {snap.Uptime.Hours} h";
        Disks.Clear();
        foreach (var d in snap.Disks) Disks.Add(new(d.Name, $"{Bytes.Format(d.FreeBytes)} free of {Bytes.Format(d.TotalBytes)} ({d.FreePercent:0}%)"));
        Processes.Clear();
        foreach (var pr in snap.TopProcessesByMemory) Processes.Add(new(pr.Name, pr.Pid, Bytes.Format(pr.WorkingSetBytes)));
    }

    // ---- Cleanup ----------------------------------------------------------
    public ObservableCollection<CandidateRowViewModel> Candidates { get; } = [];
    public ObservableCollection<HeldRow> Held { get; } = [];
    [ObservableProperty] private string _cleanupSummary = "Scan to see which temporary files pass every safeguard. Scanning never deletes anything.";
    [ObservableProperty] private string _cleanupMessage = "";

    [RelayCommand]
    private async Task ScanCleanupAsync()
    {
        var result = await Task.Run(() => _host.Scanner.Scan());
        Candidates.Clear();
        foreach (var c in result.Eligible.OrderByDescending(c => c.SizeBytes)) Candidates.Add(new(c));
        var skipped = string.Join(", ", result.SkippedByReason.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
        CleanupSummary = $"{result.Eligible.Count} file(s), {Bytes.Format(result.EligibleBytes)}, passed every safeguard out of {result.FilesExamined} examined." +
                         (skipped.Length > 0 ? $" Skipped: {skipped}." : "") + (result.LimitReached ? " Scan limit reached." : "");
        CleanupMessage = "";
    }

    [RelayCommand]
    private void MoveSelected()
    {
        var selected = Candidates.Where(c => c.IsSelected).Select(c => c.Candidate).ToList();
        if (selected.Count == 0) { CleanupMessage = "Select at least one file first."; return; }
        var results = _host.Cleaner.MoveToHolding(selected);
        var moved = results.Count(r => r.Moved);
        CleanupMessage = $"{moved} of {selected.Count} moved to the holding area." +
                         string.Concat(results.Where(r => !r.Moved).Take(5).Select(r => $"\n{System.IO.Path.GetFileName(r.Path)}: {r.Message}"));
        foreach (var row in Candidates.Where(c => results.Any(r => r.Moved && r.Path == c.Path)).ToList()) Candidates.Remove(row);
        RefreshActivity();
    }

    [RelayCommand]
    private void Restore(HeldRow row)
    {
        var r = _host.Cleaner.Restore(row.Id);
        CleanupMessage = r.Message;
        RefreshActivity();
    }

    // ---- Startup & apps ---------------------------------------------------
    public ObservableCollection<StartupRow> StartupItems { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];
    [ObservableProperty] private string _inventorySummary = "Load the inventory to list startup items and installed applications. Nothing is changed.";
    [ObservableProperty] private string _appFilter = "";

    partial void OnAppFilterChanged(string value) => ApplyAppFilter();

    [RelayCommand]
    private async Task LoadInventoryAsync()
    {
        var startup = await Task.Run(() => _host.Adapter.Startup.ListAsync());
        var apps = await Task.Run(() => _host.Adapter.Applications.ListAsync());
        StartupItems.Clear();
        foreach (var s in startup.Items) StartupItems.Add(new(s.Name, s.Mechanism, s.Scope.ToString(), s.Location));
        _allApps = apps.Items.Select(AppRow.From).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ApplyAppFilter();
        InventorySummary = string.Join(" ", startup.Reports.Concat(apps.Reports).Select(r => $"{r.Title}: {r.Summary}"));
    }

    private void ApplyAppFilter()
    {
        Apps.Clear();
        foreach (var a in _allApps.Where(a => AppFilter.Length == 0 || a.Name.Contains(AppFilter, StringComparison.OrdinalIgnoreCase)).Take(500)) Apps.Add(a);
    }

    // ---- Mode -------------------------------------------------------------
    [ObservableProperty] private string _modeTitle = "";
    [ObservableProperty] private string _modeExplanation = "";
    [ObservableProperty] private bool _canMoveFiles;
    [ObservableProperty] private bool _isObserveOnly;
    [ObservableProperty] private OperatingMode? _pendingMode;
    [ObservableProperty] private string _confirmationText = "";
    [ObservableProperty] private string _modeMessage = "";

    public string PendingTitle => PendingMode is { } m ? ModeDescriptions.Title(m) : "";
    public string PendingExplanation => PendingMode is { } m ? ModeDescriptions.Explanation(m) : "";
    public string PendingPhrase => PendingMode is { } m ? ModeDescriptions.RequiredConfirmation(m) : "";
    public bool HasPending => PendingMode is not null;
    public string ObserveExplanation => ModeDescriptions.Explanation(OperatingMode.ObserveOnly);
    public string MaintenanceExplanation => ModeDescriptions.Explanation(OperatingMode.SafeMaintenance);
    public string RepairExplanation => ModeDescriptions.Explanation(OperatingMode.ControlledRepair);

    partial void OnPendingModeChanged(OperatingMode? value)
    {
        OnPropertyChanged(nameof(PendingTitle));
        OnPropertyChanged(nameof(PendingExplanation));
        OnPropertyChanged(nameof(PendingPhrase));
        OnPropertyChanged(nameof(HasPending));
    }

    private void RefreshMode()
    {
        var m = _host.Modes.Current;
        ModeTitle = ModeDescriptions.Title(m);
        ModeExplanation = ModeDescriptions.Explanation(m);
        CanMoveFiles = _host.Gate.Check(Core.Modes.OperationKind.SafeMaintenance).Allowed;
        IsObserveOnly = m == OperatingMode.ObserveOnly;
    }

    [RelayCommand] private void AskSafeMaintenance() { PendingMode = OperatingMode.SafeMaintenance; ConfirmationText = ""; ModeMessage = ""; }
    [RelayCommand] private void AskControlledRepair() { PendingMode = OperatingMode.ControlledRepair; ConfirmationText = ""; ModeMessage = ""; }
    [RelayCommand] private void CancelModeChange() { PendingMode = null; ConfirmationText = ""; }

    [RelayCommand]
    private void ConfirmModeChange()
    {
        if (PendingMode is not { } target) return;
        try
        {
            _host.Modes.RaiseTo(target, Environment.UserName, ConfirmationText);
            ModeMessage = $"{ModeDescriptions.Title(target)} enabled. Your consent was recorded with the time and your user name.";
            PendingMode = null;
        }
        catch (ModeChangeRefusedException ex) { ModeMessage = ex.Message; }
    }

    [RelayCommand]
    private void ReturnToObserveOnly()
    {
        _host.Modes.LowerTo(OperatingMode.ObserveOnly);
        ModeMessage = "Back in Observe Only. Guardian will not change your computer.";
        PendingMode = null;
    }

    // ---- Activity ---------------------------------------------------------
    public ObservableCollection<OperationRow> Operations { get; } = [];

    private void RefreshActivity()
    {
        var recent = _host.Database.Recent();
        Operations.Clear();
        foreach (var r in recent) Operations.Add(OperationRow.From(r));
        Held.Clear();
        foreach (var r in recent) if (OperationRow.Held(r) is { } h) Held.Add(h);
    }

    public string DataFolder => _host.DataDirectory;
}
