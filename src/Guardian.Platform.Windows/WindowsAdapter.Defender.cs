using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Microsoft.Win32;

namespace Guardian.Platform.Windows;

/// <summary>Defender health, the daily scan schedule, SmartScreen and pending Windows updates. All read-only.</summary>
public sealed partial class WindowsAdapter
{
    private async Task<CheckReport> ProtectionCheck(CancellationToken ct)
    {
        const string id = "win.defender-protection", title = "Defender protection features";
        if (_protection is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_protection, ct: ct);
        var p = r.Succeeded ? DefenderParsers.Protection(r.StdOut.Trim()) : null;
        if (p is null)
            return CheckReport.NotAvailable(id, title, "Defender's settings could not be read. This is normal when another antivirus is the active one.",
                new Evidence(_protection.Display, Trim(r.StdErr)));
        return DefenderProtectionReport.Build(p);
    }

    private async Task<CheckReport> ScheduleCheck(CancellationToken ct)
    {
        const string id = "win.defender-schedule", title = "Daily Defender scan";
        var s = await Antivirus.GetScheduleAsync(ct);
        var ev = new List<Evidence> { new("Task Scheduler", s.Evidence) };
        if (!s.Installed)
            return CheckReport.Completed(id, title, Finding.Attention, "Guardian's daily Defender scan is not set up. (Defender still runs its own background scans.)", ev,
                "Turn it on in the Protection tab. This needs Safe Maintenance mode.");
        ev.Add(new("Schedule", $"{s.Kind} scan daily at {s.Time:HH\\:mm}"));
        ev.Add(new("Last run", s.LastRun is { } l ? $"{l.ToLocalTime():yyyy-MM-dd HH:mm}: {s.LastResult}" : "not run yet"));
        var failed = s.LastResult?.StartsWith("Did not complete", StringComparison.Ordinal) == true;
        var overdue = s.LastRun is { } last && DateTimeOffset.Now - last > TimeSpan.FromDays(3);
        return failed || overdue
            ? CheckReport.Completed(id, title, Finding.Attention, failed ? "The last scheduled scan did not complete." : "The daily scan has not run for more than 3 days.", ev,
                "Check that the laptop is on at the scheduled time, or run a scan now from the Protection tab.")
            : CheckReport.Completed(id, title, Finding.NoIssueFound, $"Defender {s.Kind?.ToString().ToLowerInvariant()} scan runs daily at {s.Time:HH\\:mm}.", ev);
    }

    private Task<CheckReport> SmartScreenCheck(CancellationToken ct)
    {
        const string id = "win.smartscreen", title = "SmartScreen (blocks dangerous downloads and apps)";
        var policy = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", null);
        var setting = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", null) as string;
        var ev = new List<Evidence>
        {
            new(@"HKLM\...\Policies\Microsoft\Windows\System\EnableSmartScreen", policy?.ToString() ?? "not set"),
            new(@"HKLM\...\CurrentVersion\Explorer\SmartScreenEnabled", setting ?? "not set (Windows default: on)"),
        };
        var off = (policy is int pv && pv == 0) || string.Equals(setting, "Off", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(off
            ? CheckReport.Completed(id, title, Finding.Problem, "SmartScreen for apps and files is turned off.", ev,
                "Turn on 'Check apps and files' in Windows Security > App & browser control > Reputation-based protection. Guardian does not change it.")
            : CheckReport.Completed(id, title, Finding.NoIssueFound, "SmartScreen for apps and files is on.", ev,
                "SmartScreen in the Edge browser is a separate setting and is not checked here."));
    }

    private async Task<CheckReport> PendingUpdatesCheck(CancellationToken ct)
    {
        const string id = "win.pending-updates", title = "Windows updates waiting to install (contacts Windows Update)";
        if (_pendingUpdates is null) return CheckReport.NotAvailable(id, title, "Windows PowerShell was not found.");
        var r = await _run.RunAsync(_pendingUpdates, TimeSpan.FromMinutes(10), ct);
        if (!r.Succeeded) return CheckReport.Failed(id, title, "Windows Update could not be searched.", new Evidence("Microsoft.Update.Session", Trim(r.StdErr)));
        var updates = DefenderParsers.PendingUpdates(r.StdOut.Trim());
        var critical = updates.Count(u => u.Severity is "Critical" or "Important");
        var ev = updates.Select(u => new Evidence(u.Severity ?? "Unrated", u.Title)).ToList();
        return updates.Count == 0
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "Windows Update has no updates waiting.", [new("Microsoft.Update.Session", "0 updates")])
            : CheckReport.Completed(id, title, critical > 0 ? Finding.Problem : Finding.Attention, $"{updates.Count} update(s) waiting, {critical} critical or important.", ev,
                "Install them in Settings > Windows Update. Guardian does not install Windows updates.");
    }
}

/// <summary>Turns Defender's settings into an honest report. OS-neutral so it can be unit-tested anywhere.</summary>
public static class DefenderProtectionReport
{
    public static CheckReport Build(DefenderProtection p)
    {
        const string id = "win.defender-protection", title = "Defender protection features";
        static string OnOff(bool? b) => b switch { true => "on", false => "OFF", null => "unknown" };
        var ev = new List<Evidence>
        {
            new("Real-time protection", OnOff(p.RealTime)),
            new("Tamper protection (stops malware turning Defender off)", OnOff(p.Tamper)),
            new("Behaviour monitoring", OnOff(p.Behavior)),
            new("Download and attachment scanning", OnOff(p.Ioav)),
            new("Cloud-delivered protection", p.Maps is null ? "unknown" : p.Maps == 0 ? "OFF" : "on"),
            new("Potentially unwanted app blocking", p.Pua switch { 1 => "on", 2 => "audit only", null => "unknown", _ => "off" }),
            new("Virus definitions age", p.SignatureAgeDays is { } a ? $"{a} day(s)" : "unknown"),
            new("Defender mode", p.Mode ?? "unknown"),
        };
        if (p.Mode is not null && p.Mode.Contains("Passive", StringComparison.OrdinalIgnoreCase))
            return CheckReport.Completed(id, title, Finding.Attention, "Defender is in passive mode, so another antivirus is responsible for real-time protection.", ev,
                "Make sure your other antivirus is up to date. Guardian does not switch antivirus products.");
        var off = new List<string>();
        if (p.RealTime == false) off.Add("real-time protection");
        if (p.Tamper == false) off.Add("tamper protection");
        if (p.Maps == 0) off.Add("cloud-delivered protection");
        if (p.Behavior == false) off.Add("behaviour monitoring");
        var stale = p.SignatureAgeDays is > 3;
        if (off.Count == 0 && !stale)
            return CheckReport.Completed(id, title, Finding.NoIssueFound, "Defender's main protection features are on and its virus definitions are recent.", ev);
        var summary = string.Join(" ", new[]
        {
            off.Count > 0 ? $"Turned off: {string.Join(", ", off)}." : null,
            stale ? $"Virus definitions are {p.SignatureAgeDays} days old." : null,
        }.Where(x => x is not null));
        return CheckReport.Completed(id, title, p.RealTime == false || p.Tamper == false ? Finding.Problem : Finding.Attention, summary, ev,
            "Open Windows Security > Virus & threat protection > Manage settings and turn these on; use 'Protection updates' for definitions. Guardian does not change Defender's settings itself.");
    }
}
