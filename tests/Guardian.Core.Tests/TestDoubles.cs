using Guardian.Core.Cleanup;
using Guardian.Core.Modes;
using Guardian.Core.Platform;
using Guardian.Core.Recovery;

namespace Guardian.Core.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class MemoryModeStore : IModeStore
{
    public OperatingMode? Mode;
    public List<ConsentRecord> Consents = [];
    public OperatingMode? LoadMode() => Mode;
    public void SaveMode(OperatingMode mode, ConsentRecord? consent) { Mode = mode; if (consent is not null) Consents.Add(consent); }
    public IReadOnlyList<ConsentRecord> LoadConsentHistory() => Consents;
}

public sealed class MemoryOperationLog : IOperationLog
{
    public readonly List<OperationRecord> Records = [];
    public void Append(OperationRecord record) => Records.Add(record);
    public void Update(OperationRecord record) => Records[Records.FindIndex(r => r.Id == record.Id)] = record;
    public OperationRecord? Get(Guid id) => Records.FirstOrDefault(r => r.Id == id);
    public IReadOnlyList<OperationRecord> Recent(int limit = 200) => Records.AsEnumerable().Reverse().Take(limit).ToList();
    public int CountFailures(string kind, string target, DateTimeOffset since) =>
        Records.Count(r => r.Kind == kind && r.Target == target && r.Status is OperationStatus.Failed or OperationStatus.PartiallyFailed && r.StartedAt >= since);
}

/// <summary>A cleanup platform whose answers the test controls. Defaults to "owned by me, not in use".</summary>
public sealed class FakeCleanupPlatform(string root) : ICleanupPlatform
{
    public Func<string, bool?> Owned { get; set; } = _ => true;
    public Func<string, bool?> InUse { get; set; } = _ => false;
    public IReadOnlyList<CleanupRoot> GetRoots() => [new CleanupRoot(root, "test temp")];
    public bool? IsOwnedByCurrentUser(string path) => Owned(path);
    public bool? IsInUse(string path) => InUse(path);
    public bool PathsAreCaseInsensitive => false;
}

public sealed class RecordingRunner : ICommandRunner
{
    public readonly List<string> Calls = [];
    public Func<string, IReadOnlyList<string>, CommandResult> Respond { get; set; } = (_, _) => new CommandResult(true, 0, "", "", false);
    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add(arguments.Count == 0 ? fileName : fileName + " " + string.Join(' ', arguments));
        return Task.FromResult(Respond(fileName, arguments));
    }
}

/// <summary>A temporary directory tree, deleted at the end of the test.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("guardian-test-").FullName;
    public string Temp => Path.Combine(Root, "tmp");
    public string Holding => Path.Combine(Root, "holding");

    public TempTree() => Directory.CreateDirectory(Temp);

    public string File(string relative, DateTime lastWriteUtc, string content = "x")
    {
        var p = Path.Combine(Temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        System.IO.File.SetCreationTimeUtc(p, lastWriteUtc);
        System.IO.File.SetLastWriteTimeUtc(p, lastWriteUtc);
        return p;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}
