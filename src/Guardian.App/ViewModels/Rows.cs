using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Recovery;
using Guardian.Core.Reporting;

namespace Guardian.App.ViewModels;

public static class Tones
{
    public static readonly IBrush Good = new SolidColorBrush(Color.Parse("#2E7D32"));
    public static readonly IBrush Attention = new SolidColorBrush(Color.Parse("#ED6C02"));
    public static readonly IBrush Problem = new SolidColorBrush(Color.Parse("#D32F2F"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A8A90"));

    public static IBrush For(CheckReport r) => r.Finding switch
    {
        Finding.NoIssueFound => Good,
        Finding.Attention => Attention,
        Finding.Problem => Problem,
        _ => Neutral,
    };
}

/// <summary>One check, with every honest-reporting field visible.</summary>
public sealed partial class CheckRowViewModel : ObservableObject
{
    [ObservableProperty] private CheckReport _report;
    [ObservableProperty] private bool _isRunning;

    public CheckRowViewModel(CheckReport report, bool onDemand = false, Func<Task<CheckReport>>? run = null)
    {
        _report = report;
        IsOnDemand = onDemand;
        _run = run;
    }

    private readonly Func<Task<CheckReport>>? _run;
    public bool IsOnDemand { get; }

    public string Title => Report.Title;
    public string Summary => Report.Summary;
    public string Status => ReportLanguage.Describe(Report.Finding);
    public IBrush Tone => Tones.For(Report);
    public string Facts =>
        $"Implemented: {(Report.Implemented ? "yes" : "no")}  ·  On this OS: {ReportLanguage.Describe(Report.Support)}  ·  " +
        $"Permission: {Report.PermissionGranted switch { null => "not needed", true => "granted", false => "not granted" }}  ·  " +
        $"Ran: {(Report.Ran ? "yes" : "no")}  ·  Succeeded: {Report.Succeeded switch { null => "n/a", true => "yes", false => "no" }}  ·  " +
        $"Changes made: {(Report.ChangesMade.Count == 0 ? "none" : string.Join(", ", Report.ChangesMade))}";
    public string Evidence => Report.Evidence.Count == 0 ? "No evidence collected." : string.Join("\n", Report.Evidence.Select(e => $"• {e.Source}: {e.Detail}"));
    public string? FollowUp => Report.FollowUp;
    public bool HasFollowUp => !string.IsNullOrEmpty(Report.FollowUp);

    partial void OnReportChanged(CheckReport value)
    {
        foreach (var p in new[] { nameof(Title), nameof(Summary), nameof(Status), nameof(Tone), nameof(Facts), nameof(Evidence), nameof(FollowUp), nameof(HasFollowUp) })
            OnPropertyChanged(p);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task RunAsync()
    {
        if (_run is null || IsRunning) return;
        IsRunning = true;
        try { Report = await _run(); }
        finally { IsRunning = false; }
    }
}

public sealed partial class CandidateRowViewModel(CleanupCandidate candidate) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public CleanupCandidate Candidate { get; } = candidate;
    public string Path => Candidate.Path;
    public string Size => Bytes.Format(Candidate.SizeBytes);
    public string Age => $"{(DateTime.UtcNow - Candidate.LastWriteUtc).TotalDays:0} days old";
}

public sealed record HeldRow(Guid Id, string OriginalPath, string MovedAt, string Size);

public sealed record OperationRow(string When, string What, string Target, string Status, string Undo, string? Error)
{
    public static OperationRow From(OperationRecord r) => new(
        r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        r.Description,
        r.Target,
        r.Status.ToString(),
        r.Reversibility switch
        {
            Reversibility.UndoByGuardian => "Can be restored by Guardian",
            Reversibility.ManualProcedure => "Manual recovery: " + r.RollbackProcedure,
            Reversibility.Irreversible => "Cannot be undone. " + r.RollbackProcedure,
            _ => "Nothing changed",
        },
        r.Error);

    public static HeldRow? Held(OperationRecord r)
    {
        if (r.Kind != HoldingAreaCleaner.OperationKindName || r.Status != OperationStatus.Succeeded || r.Reversibility != Reversibility.UndoByGuardian || r.RollbackData is null) return null;
        var h = JsonSerializer.Deserialize<HeldFile>(r.RollbackData)!;
        return new HeldRow(r.Id, h.OriginalPath, r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Bytes.Format(h.SizeBytes));
    }
}

public sealed record DiskRow(string Name, string Detail);
public sealed record ProcessRow(string Name, int Pid, string Memory);
public sealed record StartupRow(string Name, string Mechanism, string Scope, string Location);
public sealed record AppRow(string Name, string Version, string Publisher, string Source)
{
    public static AppRow From(InstalledApplication a) => new(a.Name, a.Version ?? "", a.Publisher ?? "", a.Source);
}
