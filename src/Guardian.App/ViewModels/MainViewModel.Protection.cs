using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Guardian.Core.Antivirus;
using Guardian.Core.Platform;

namespace Guardian.App.ViewModels;

public sealed record ThreatRow(string When, string Name, string Severity, string Status, string Files);

/// <summary>Protection tab: the OS antivirus engine, Guardian's daily scan, and recent detections.</summary>
public sealed partial class MainViewModel
{
    public bool ProtectionAvailable => _host.Antivirus is not null;
    public string EngineName => _host.Antivirus?.Engine.EngineName ?? "";
    public string ProtectionUnavailableText => _host.Adapter.Info.Family switch
    {
        OsFamily.MacOS => "On macOS, XProtect scans automatically in the background and Apple does not let other apps start XProtect scans. The Security tab shows XProtect, Gatekeeper and firewall status.",
        OsFamily.Linux => "Scheduled ClamAV scanning on Linux is not implemented yet. The Security tab shows whether ClamAV is installed and up to date.",
        _ => "No supported antivirus engine was found.",
    };

    [ObservableProperty] private string _scanTime = "07:30";
    [ObservableProperty] private bool _fullScan;
    [ObservableProperty] private string _scheduleText = "Checking…";
    [ObservableProperty] private bool _scheduleInstalled;
    [ObservableProperty] private string _protectionMessage = "";
    [ObservableProperty] private bool _isScanning;
    public ObservableCollection<ThreatRow> Threats { get; } = [];

    public string ScanCommandText => _host.Antivirus is { } av
        ? $"Guardian will add one task to Windows Task Scheduler (\\AI Laptop Guardian\\Daily Defender Scan) that runs:\n{av.Engine.DescribeScanCommand(FullScan ? ScanKind.Full : ScanKind.Quick)}\nDefender decides what is a threat and quarantines or removes it using its own settings. You can restore anything it quarantined from Windows Security > Protection history."
        : "";

    partial void OnFullScanChanged(bool value) => OnPropertyChanged(nameof(ScanCommandText));

    [RelayCommand]
    private async Task RefreshProtectionAsync()
    {
        if (_host.Antivirus is not { } av) return;
        var s = await Task.Run(() => av.Engine.GetScheduleAsync());
        ScheduleInstalled = s.Installed;
        ScheduleText = s.Installed
            ? $"On: {s.Kind?.ToString().ToLowerInvariant()} scan every day at {s.Time:HH\\:mm}. Last run: {(s.LastRun is { } l ? $"{l.ToLocalTime():yyyy-MM-dd HH:mm}, {s.LastResult}" : "not yet")}."
            : "Off. Defender still protects you in real time and runs its own background scans.";
        var (threats, _) = await Task.Run(() => av.Engine.RecentThreatsAsync(TimeSpan.FromDays(30)));
        Threats.Clear();
        foreach (var t in threats)
            Threats.Add(new(t.DetectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?", t.Name, t.Severity, t.Status, string.Join(", ", t.Resources.Take(3))));
    }

    [RelayCommand]
    private async Task EnableDailyScanAsync()
    {
        if (_host.Antivirus is not { } av) return;
        if (!TimeOnly.TryParse(ScanTime, System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            ProtectionMessage = "Enter the time as HH:MM, for example 07:30.";
            return;
        }
        var r = await av.EnableDailyScanAsync(time, FullScan ? ScanKind.Full : ScanKind.Quick);
        ProtectionMessage = r.Done ? $"Daily {(FullScan ? "full" : "quick")} scan set for {time:HH\\:mm}. If the laptop is off then, it runs as soon as it's back on." : r.Message;
        RefreshActivity();
        await RefreshProtectionAsync();
    }

    [RelayCommand]
    private async Task DisableDailyScanAsync()
    {
        if (_host.Antivirus is not { } av) return;
        var r = await av.DisableDailyScanAsync();
        ProtectionMessage = r.Done ? "Daily scan removed. Defender's own protection is unchanged." : r.Message;
        RefreshActivity();
        await RefreshProtectionAsync();
    }

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        if (_host.Antivirus is not { } av || IsScanning) return;
        IsScanning = true;
        ProtectionMessage = "Quick scan running. This usually takes a few minutes…";
        try
        {
            var r = await Task.Run(() => av.ScanNowAsync(ScanKind.Quick));
            ProtectionMessage = r.Done ? "Quick scan finished. Any detections are listed below." : r.Message;
        }
        finally { IsScanning = false; }
        RefreshActivity();
        await RefreshProtectionAsync();
    }

    // ---- New startup items ------------------------------------------------
    [ObservableProperty] private string _newStartupSummary = "";

    [RelayCommand]
    private void MarkStartupReviewed()
    {
        _host.Database.MarkAllReviewed();
        NewStartupSummary = "All startup items marked as reviewed. Guardian will flag anything added from now on.";
    }
}
