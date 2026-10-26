using Guardian.Core.Cleanup;
using Guardian.Core.Inventory;
using Guardian.Core.Performance;
using Guardian.Core.Platform;
using Guardian.Core.Reporting;
using Guardian.Core.Scheduling;

namespace Guardian.Platform.Linux;

/// <summary>
/// Linux adapter for Ubuntu, Debian and Fedora. Every command is read-only, run by absolute path, and
/// on an explicit allow-list. Package-manager commands are only used for the package manager that
/// matches the detected distribution.
/// </summary>
public sealed class LinuxAdapter : IPlatformAdapter
{
    private readonly ReadOnlyCommandRunner _run;
    private readonly IReadOnlyDictionary<string, string> _osRelease;
    private readonly Dictionary<string, string> _tools = new();

    // Allow-listed commands, built from absolute paths found at startup.
    private ReadOnlyCommand? _ufwActive, _ufwStatus, _firewalldState, _nftActive;
    private ReadOnlyCommand? _aptSimulate, _dnfCheck, _dpkgQueryUnattended, _dnfAutoTimer, _dnf5AutoTimer, _needsRestarting;
    private ReadOnlyCommand? _getenforce, _clamVersion, _lsblk, _systemdUserUnits, _dpkgList, _rpmList, _flatpakList, _snapList;

    public LinuxAdapter(ICommandRunner? runner = null, string osReleasePath = "/etc/os-release")
    {
        _osRelease = File.Exists(osReleasePath) ? OsRelease.Parse(File.ReadAllText(osReleasePath)) : new Dictionary<string, string>();
        var pm = OsRelease.PackageManagerFor(_osRelease);
        var allow = BuildAllowList(pm);
        _run = new ReadOnlyCommandRunner(runner ?? new ProcessCommandRunner(), allow);

        var id = _osRelease.GetValueOrDefault("ID");
        var ver = _osRelease.GetValueOrDefault("VERSION_ID");
        var (targeted, note) = SupportMatrix.Linux(id, ver);
        Info = new PlatformInfo
        {
            Family = OsFamily.Linux,
            OsName = _osRelease.GetValueOrDefault("PRETTY_NAME") ?? "Linux",
            OsVersion = ver ?? Environment.OSVersion.Version.ToString(),
            Architecture = PlatformInfo.DetectArchitecture(),
            IsElevated = LinuxNative.EffectiveUserId() == 0,
            DistributionId = id,
            PackageManager = pm,
            IsSupportedVersion = targeted,
            SupportNote = note + " " + SupportMatrix.ReleaseStatus,
            DetectedTools = _tools.Keys.Order().ToList(),
        };

        SecurityChecks =
        [
            new DelegateCheck("linux.distribution", "Operating system and distribution", DistributionCheck),
            new DelegateCheck("linux.firewall", "Firewall", FirewallCheck),
            new DelegateCheck("linux.updates", "Pending system updates", UpdatesCheck),
            new DelegateCheck("linux.auto-updates", "Automatic security updates", AutoUpdatesCheck),
            new DelegateCheck("linux.reboot", "Restart required after updates", RebootCheck),
            new DelegateCheck("linux.mac", "Mandatory access control (AppArmor / SELinux)", MacCheck),
            new DelegateCheck("linux.encryption", "Disk encryption", EncryptionCheck),
            new DelegateCheck("linux.antimalware", "Anti-malware scanner (ClamAV, optional)", ClamCheck),
        ];
        Performance = new LinuxPerformanceProbe();
        Startup = new LinuxStartupInventory(_run, _systemdUserUnits);
        Applications = new LinuxApplicationInventory(_run, pm, _dpkgList, _rpmList, _flatpakList, _snapList);
        Cleanup = new LinuxCleanupPlatform();
        Scheduler = new SystemdUserTimerScheduler();
    }

    public PlatformInfo Info { get; }
    public IReadOnlyList<ISecurityCheck> SecurityChecks { get; }
    public IPerformanceProbe Performance { get; }
    public IStartupInventory Startup { get; }
    public IApplicationInventory Applications { get; }
    public ICleanupPlatform Cleanup { get; }
    public INativeScheduler Scheduler { get; }

    private string? Tool(string name)
    {
        var p = ToolLocator.FindUnix(name);
        if (p is not null) _tools[name] = p;
        return p;
    }

    private List<ReadOnlyCommand> BuildAllowList(string? pm)
    {
        var list = new List<ReadOnlyCommand>();
        ReadOnlyCommand? Add(string? exe, params string[] args)
        {
            if (exe is null) return null;
            var c = new ReadOnlyCommand(exe, args);
            list.Add(c);
            return c;
        }
        var systemctl = Tool("systemctl");
        if (Tool("ufw") is { } ufw)
        {
            _ufwActive = Add(systemctl, "is-active", "ufw");
            _ufwStatus = Add(ufw, "status");
        }
        _firewalldState = Add(Tool("firewall-cmd"), "--state");
        if (Tool("nft") is not null) _nftActive = Add(systemctl, "is-active", "nftables");

        // Package-manager commands only for the matching family. Both are read-only:
        // "apt-get -s" is a simulation; "dnf check-update --cacheonly" reads cached metadata.
        if (pm == "apt")
        {
            _aptSimulate = Add(Tool("apt-get"), "-s", "-o", "Debug::NoLocking=true", "upgrade");
            var dpkgQuery = Tool("dpkg-query");
            _dpkgQueryUnattended = Add(dpkgQuery, "-W", "-f=${Status}", "unattended-upgrades");
            _dpkgList = Add(dpkgQuery, "-W", "-f=${Package}\t${Version}\t${Maintainer}\n");
        }
        else if (pm == "dnf")
        {
            _dnfCheck = Add(Tool("dnf"), "check-update", "--cacheonly", "-q");
            _dnfAutoTimer = Add(systemctl, "is-enabled", "dnf-automatic.timer");
            _dnf5AutoTimer = Add(systemctl, "is-enabled", "dnf5-automatic.timer");
            _needsRestarting = Add(Tool("needs-restarting"), "-r");
            _rpmList = Add(Tool("rpm"), "-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\n");
        }
        _getenforce = Add(Tool("getenforce"));
        _clamVersion = Add(Tool("clamscan"), "--version");
        _lsblk = Add(Tool("lsblk"), "-J", "-o", "NAME,TYPE,MOUNTPOINT");
        _systemdUserUnits = Add(systemctl, "--user", "list-unit-files", "--state=enabled", "--no-legend", "--no-pager", "--plain");
        _flatpakList = Add(Tool("flatpak"), "list", "--app", "--columns=name,version,origin");
        _snapList = Add(Tool("snap"), "list");
        return list;
    }

    private Task<CheckReport> DistributionCheck(CancellationToken ct)
    {
        const string id = "linux.distribution", title = "Operating system and distribution";
        if (_osRelease.Count == 0) return Task.FromResult(CheckReport.NotAvailable(id, title, "/etc/os-release could not be read, so the distribution is unknown."));
        var ev = new List<Evidence>
        {
            new("/etc/os-release", $"ID={Info.DistributionId}; VERSION_ID={Info.OsVersion}; ID_LIKE={_osRelease.GetValueOrDefault("ID_LIKE") ?? "-"}"),
            new("Package manager", Info.PackageManager ?? "none supported"),
            new("Architecture", Info.Architecture),
            new("Privileges", Info.IsElevated ? "root" : "standard user"),
        };
        return Task.FromResult(CheckReport.Completed(id, title, Info.IsSupportedVersion ? Finding.NoIssueFound : Finding.Attention,
            Info.SupportNote ?? "", ev));
    }

    private async Task<CheckReport> FirewallCheck(CancellationToken ct)
    {
        const string id = "linux.firewall", title = "Firewall";
        var ev = new List<Evidence>();
        if (_ufwActive is not null)
        {
            var active = await _run.RunAsync(_ufwActive, ct: ct);
            ev.Add(new(_ufwActive.Display, active.StdOut.Trim() is { Length: > 0 } s ? s : $"exit {active.ExitCode}"));
            if (Info.IsElevated && _ufwStatus is not null)
            {
                var st = await _run.RunAsync(_ufwStatus, ct: ct);
                ev.Add(new(_ufwStatus.Display, st.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? ""));
                return st.StdOut.Contains("Status: active")
                    ? CheckReport.Completed(id, title, Finding.NoIssueFound, "ufw is active.", ev, permissionGranted: true)
                    : CheckReport.Completed(id, title, Finding.Attention, "ufw is installed but not active.", ev, "Consider enabling it yourself with 'sudo ufw enable'. Guardian does not change firewall settings.", true);
            }
            if (active.StdOut.Trim() == "active")
                return CheckReport.Completed(id, title, Finding.NoIssueFound, "The ufw service is running. Rule details need administrator rights and were not read.", ev, permissionGranted: false);
            return CheckReport.Completed(id, title, Finding.Attention,
                "The ufw service is not running. (Without administrator rights Guardian can't read its rules.)", ev,
                "Check with 'sudo ufw status'. Guardian does not change firewall settings.", false);
        }
        if (_firewalldState is not null)
        {
            var st = await _run.RunAsync(_firewalldState, ct: ct);
            ev.Add(new(_firewalldState.Display, (st.StdOut + st.StdErr).Trim()));
            return st.StdOut.Trim() == "running"
                ? CheckReport.Completed(id, title, Finding.NoIssueFound, "firewalld is running.", ev)
                : CheckReport.Completed(id, title, Finding.Attention, "firewalld is installed but not running.", ev, "Check with 'sudo firewall-cmd --state'. Guardian does not change firewall settings.");
        }
        if (_nftActive is not null)
        {
            var st = await _run.RunAsync(_nftActive, ct: ct);
            ev.Add(new(_nftActive.Display, st.StdOut.Trim()));
            return CheckReport.Completed(id, title, st.StdOut.Trim() == "active" ? Finding.NoIssueFound : Finding.Inconclusive,
                st.StdOut.Trim() == "active" ? "The nftables service is active. Guardian did not inspect the ruleset." : "No ufw or firewalld found; the nftables service is not active. A firewall may still be configured another way.", ev);
        }
        return CheckReport.NotAvailable(id, title, "No supported firewall front-end (ufw, firewalld) or nftables service was found. Guardian cannot tell whether a firewall is configured another way.");
    }

    private async Task<CheckReport> UpdatesCheck(CancellationToken ct)
    {
        const string id = "linux.updates", title = "Pending system updates";
        if (Info.PackageManager is null) return CheckReport.NotSupported(id, title, "No supported package manager for this distribution. Guardian will not guess which command to run.");
        if (_aptSimulate is not null)
        {
            var r = await _run.RunAsync(_aptSimulate, TimeSpan.FromSeconds(120), ct);
            if (!r.Succeeded) return CheckReport.Failed(id, title, "apt simulation did not complete.", new Evidence(_aptSimulate.Display, Trim(r.StdErr)));
            var (total, sec) = LinuxParsers.AptSimulatedUpgrades(r.StdOut);
            var ev = new List<Evidence> { new(_aptSimulate.Display, $"{total} upgradable, {sec} from a security pocket (simulation only; based on the last 'apt update')") };
            return total == 0
                ? CheckReport.Completed(id, title, Finding.NoIssueFound, "No pending upgrades in the local package lists.", ev, "Package lists may be out of date; your system updater refreshes them.")
                : CheckReport.Completed(id, title, sec > 0 ? Finding.Problem : Finding.Attention, $"{total} package upgrade(s) pending, {sec} security.", ev,
                    "Install updates with your system's Software Updater or 'sudo apt upgrade'. Guardian does not install OS packages.");
        }
        if (_dnfCheck is not null)
        {
            var r = await _run.RunAsync(_dnfCheck, TimeSpan.FromSeconds(120), ct);
            var ev = new List<Evidence> { new(_dnfCheck.Display, $"exit {r.ExitCode} (100 = updates available, 0 = none; uses cached metadata only)") };
            return r.ExitCode switch
            {
                0 => CheckReport.Completed(id, title, Finding.NoIssueFound, "No pending updates in cached metadata.", ev, "Cached metadata may be out of date."),
                100 => CheckReport.Completed(id, title, Finding.Attention, $"{LinuxParsers.DnfCheckUpdateCount(r.StdOut)} update(s) pending.", ev,
                    "Install updates with GNOME Software or 'sudo dnf upgrade'. Guardian does not install OS packages."),
                _ => CheckReport.Failed(id, title, "dnf could not check for updates (no cached metadata or an error).", ev.Append(new Evidence("stderr", Trim(r.StdErr))).ToArray()),
            };
        }
        return CheckReport.NotAvailable(id, title, $"The {Info.PackageManager} tools were not found.");
    }

    private async Task<CheckReport> AutoUpdatesCheck(CancellationToken ct)
    {
        const string id = "linux.auto-updates", title = "Automatic security updates";
        if (Info.PackageManager == "apt" && _dpkgQueryUnattended is not null)
        {
            var r = await _run.RunAsync(_dpkgQueryUnattended, ct: ct);
            var installed = r.StdOut.Contains("install ok installed");
            const string conf = "/etc/apt/apt.conf.d/20auto-upgrades";
            var enabled = File.Exists(conf) && File.ReadAllText(conf).Contains("Unattended-Upgrade \"1\"");
            var ev = new List<Evidence> { new(_dpkgQueryUnattended.Display, installed ? "installed" : "not installed"), new(conf, File.Exists(conf) ? (enabled ? "Unattended-Upgrade \"1\"" : "present, not enabled") : "missing") };
            return installed && enabled
                ? CheckReport.Completed(id, title, Finding.NoIssueFound, "unattended-upgrades is installed and enabled.", ev)
                : CheckReport.Completed(id, title, Finding.Attention, "Automatic security updates do not appear to be enabled.", ev,
                    "Your distribution's documentation explains enabling unattended-upgrades. Guardian does not change update settings.");
        }
        if (Info.PackageManager == "dnf" && _dnfAutoTimer is not null)
        {
            var a = await _run.RunAsync(_dnfAutoTimer, ct: ct);
            var b = _dnf5AutoTimer is null ? CommandResult.NotStarted("") : await _run.RunAsync(_dnf5AutoTimer, ct: ct);
            var ev = new List<Evidence> { new(_dnfAutoTimer.Display, a.StdOut.Trim()), new(_dnf5AutoTimer?.Display ?? "dnf5-automatic", b.StdOut.Trim()) };
            return a.StdOut.Trim() == "enabled" || b.StdOut.Trim() == "enabled"
                ? CheckReport.Completed(id, title, Finding.NoIssueFound, "dnf-automatic timer is enabled.", ev)
                : CheckReport.Completed(id, title, Finding.Attention, "dnf-automatic is not enabled. GNOME Software may still install updates on its own schedule.", ev);
        }
        return CheckReport.NotSupported(id, title, "Automatic update detection is implemented for apt (unattended-upgrades) and dnf (dnf-automatic) only.");
    }

    private async Task<CheckReport> RebootCheck(CancellationToken ct)
    {
        const string id = "linux.reboot", title = "Restart required after updates";
        if (Info.PackageManager == "apt")
        {
            const string flag = "/var/run/reboot-required";
            var needed = File.Exists(flag);
            return CheckReport.Completed(id, title, needed ? Finding.Attention : Finding.NoIssueFound,
                needed ? "A restart is required to finish installing updates." : "No restart is pending.",
                [new(flag, needed ? "present" : "absent")], needed ? "Restart when convenient. Guardian never restarts the computer." : null);
        }
        if (Info.PackageManager == "dnf")
        {
            if (_needsRestarting is null) return CheckReport.NotAvailable(id, title, "'needs-restarting' (dnf-utils) is not installed.");
            var r = await _run.RunAsync(_needsRestarting, ct: ct);
            return r.ExitCode switch
            {
                0 => CheckReport.Completed(id, title, Finding.NoIssueFound, "No restart is pending.", [new(_needsRestarting.Display, "exit 0")]),
                1 => CheckReport.Completed(id, title, Finding.Attention, "A restart is recommended to finish updates.", [new(_needsRestarting.Display, Trim(r.StdOut))], "Restart when convenient. Guardian never restarts the computer."),
                _ => CheckReport.Failed(id, title, "needs-restarting did not complete.", new Evidence(_needsRestarting.Display, Trim(r.StdErr))),
            };
        }
        return CheckReport.NotSupported(id, title, "Restart detection is implemented for apt- and dnf-based distributions only.");
    }

    private async Task<CheckReport> MacCheck(CancellationToken ct)
    {
        const string id = "linux.mac", title = "Mandatory access control (AppArmor / SELinux)";
        const string aa = "/sys/module/apparmor/parameters/enabled";
        if (File.Exists(aa))
        {
            var v = File.ReadAllText(aa).Trim();
            return CheckReport.Completed(id, title, v == "Y" ? Finding.NoIssueFound : Finding.Attention,
                v == "Y" ? "AppArmor is enabled in the kernel." : "AppArmor is present but not enabled.", [new(aa, v)]);
        }
        if (_getenforce is not null)
        {
            var r = await _run.RunAsync(_getenforce, ct: ct);
            var mode = r.StdOut.Trim();
            return CheckReport.Completed(id, title, mode == "Enforcing" ? Finding.NoIssueFound : Finding.Attention,
                $"SELinux mode: {mode}.", [new(_getenforce.Display, mode)],
                mode == "Enforcing" ? null : "Guardian does not change SELinux settings.");
        }
        return CheckReport.NotAvailable(id, title, "Neither AppArmor nor SELinux was detected.");
    }

    private async Task<CheckReport> EncryptionCheck(CancellationToken ct)
    {
        const string id = "linux.encryption", title = "Disk encryption";
        if (_lsblk is null) return CheckReport.NotAvailable(id, title, "lsblk is not installed.");
        var r = await _run.RunAsync(_lsblk, ct: ct);
        if (!r.Succeeded) return CheckReport.Failed(id, title, "lsblk did not complete.", new Evidence(_lsblk.Display, Trim(r.StdErr)));
        var (rootEnc, any, rootFound) = LinuxParsers.LsblkEncryption(r.StdOut);
        var ev = new List<Evidence> { new(_lsblk.Display, $"root found: {rootFound}; root on dm-crypt: {rootEnc}; any dm-crypt device: {any}") };
        if (!rootFound) return CheckReport.Completed(id, title, Finding.Inconclusive, "The root file system was not found in lsblk output (containers and some setups hide it).", ev);
        return rootEnc
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, "The root file system is on an encrypted (dm-crypt/LUKS) device.", ev)
            : CheckReport.Completed(id, title, Finding.Attention, "The root file system does not appear to be encrypted.", ev,
                "Full-disk encryption is normally chosen at install time. Guardian never changes disk encryption.");
    }

    private async Task<CheckReport> ClamCheck(CancellationToken ct)
    {
        const string id = "linux.antimalware", title = "Anti-malware scanner (ClamAV, optional)";
        if (_clamVersion is null) return CheckReport.NotAvailable(id, title, "ClamAV is not installed. It is optional on Linux; Guardian does not bundle or install it.");
        var r = await _run.RunAsync(_clamVersion, ct: ct);
        var parsed = LinuxParsers.ClamVersion(r.StdOut);
        if (parsed is null) return CheckReport.Failed(id, title, "Could not read the ClamAV version.", new Evidence(_clamVersion.Display, Trim(r.StdOut + r.StdErr)));
        var (engine, sigDate) = parsed.Value;
        var ev = new List<Evidence> { new(_clamVersion.Display, Trim(r.StdOut)) };
        if (sigDate is null) return CheckReport.Completed(id, title, Finding.Inconclusive, $"ClamAV {engine} is installed; signature date unknown.", ev);
        var age = DateTimeOffset.UtcNow - sigDate.Value;
        return age.TotalDays <= 7
            ? CheckReport.Completed(id, title, Finding.NoIssueFound, $"ClamAV {engine} is installed; signatures are {age.TotalDays:0} day(s) old.", ev)
            : CheckReport.Completed(id, title, Finding.Attention, $"ClamAV signatures are {age.TotalDays:0} days old.", ev, "Check that freshclam is running. Guardian does not change ClamAV's configuration.");
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s.Trim();
}
