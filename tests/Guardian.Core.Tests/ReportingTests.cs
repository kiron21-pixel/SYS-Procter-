using Guardian.Core.Platform;
using Guardian.Core.Reporting;

namespace Guardian.Core.Tests;

public class ReportingTests
{
    [Fact]
    public void Summary_never_overclaims_even_when_everything_passes()
    {
        var reports = Enumerable.Range(0, 5).Select(i => CheckReport.Completed($"c{i}", "t", Finding.NoIssueFound, "ok", [])).ToList();
        var s = ReportLanguage.Summarize(reports);
        Assert.Null(ReportLanguage.FindOverclaim(s));
        Assert.Contains("not a guarantee", s);
    }

    [Fact]
    public void Checks_that_did_not_run_are_counted_honestly()
    {
        var s = ReportLanguage.Summarize([
            CheckReport.Completed("a", "a", Finding.NoIssueFound, "ok", []),
            CheckReport.NeedsPermission("b", "b", "needs admin"),
            CheckReport.NotAvailable("c", "c", "missing"),
        ]);
        Assert.Contains("1 of 3 checks ran", s);
        Assert.Contains("2 could not run", s);
    }

    [Theory]
    [InlineData("Your PC is virus-free!")]
    [InlineData("Fully optimized")]
    [InlineData("guaranteed safe")]
    public void Overclaims_are_detected(string text) => Assert.NotNull(ReportLanguage.FindOverclaim(text));

    [Fact]
    public void No_source_or_ui_text_contains_an_overclaim()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.EndsWith("ReportLanguage.cs"))
            .Select(f => (f, claim: ReportLanguage.FindOverclaim(File.ReadAllText(f))))
            .Where(x => x.claim is not null)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Read_only_runner_refuses_commands_not_on_the_allow_list()
    {
        var inner = new RecordingRunner();
        var allowed = new ReadOnlyCommand("/usr/bin/spctl", ["--status"]);
        var runner = new ReadOnlyCommandRunner(inner, [allowed]);

        var refused = await runner.RunAsync(new ReadOnlyCommand("/usr/sbin/spctl", ["--master-disable"]));
        var ok = await runner.RunAsync(allowed);

        Assert.False(refused.Started);
        Assert.True(ok.Started);
        Assert.Equal(["/usr/bin/spctl --status"], inner.Calls);
    }

    [Fact]
    public async Task Path_argument_is_placed_after_double_dash()
    {
        var inner = new RecordingRunner();
        var lsof = new ReadOnlyCommand("/usr/sbin/lsof", ["-t"]);
        await new ReadOnlyCommandRunner(inner, [lsof]).RunWithPathAsync(lsof, "-rf");
        Assert.Equal(["/usr/sbin/lsof -t -- -rf"], inner.Calls);
    }

    [Theory]
    [InlineData(19045, true)]
    [InlineData(22631, true)]
    [InlineData(26100, true)]
    [InlineData(19044, false)]
    public void Windows_support_matrix(int build, bool targeted) => Assert.Equal(targeted, SupportMatrix.Windows(new Version(10, 0, build)).Targeted);

    [Theory]
    [InlineData("ubuntu", "24.04", true)]
    [InlineData("ubuntu", "24.10", false)]
    [InlineData("debian", "12", true)]
    [InlineData("fedora", "42", true)]
    [InlineData("fedora", "39", false)]
    [InlineData("arch", null, false)]
    public void Linux_support_matrix(string id, string? ver, bool targeted) => Assert.Equal(targeted, SupportMatrix.Linux(id, ver).Targeted);

    internal static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Guardian.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
