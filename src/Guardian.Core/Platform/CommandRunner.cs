using System.Diagnostics;

namespace Guardian.Core.Platform;

public sealed record CommandResult(bool Started, int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
    public static CommandResult NotStarted(string reason) => new(false, -1, "", reason, false);
}

/// <summary>Runs an external program. Never goes through a shell, so arguments are never re-interpreted.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>A command that is known to only read state. Adapters register these explicitly.</summary>
public sealed record ReadOnlyCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string Display => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // Keep output parseable regardless of the user's locale.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return CommandResult.NotStarted(ex.Message);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Only our own child process is stopped, never anything else on the system.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new CommandResult(true, -1, "", "Timed out", true);
            }
            return new CommandResult(true, process.ExitCode, await stdout, await stderr, false);
        }
    }
}

/// <summary>
/// The only runner observe-only code is given. It refuses any command that is not on the
/// read-only allow-list, so a bug in a check cannot turn into a system change.
/// </summary>
public sealed class ReadOnlyCommandRunner(ICommandRunner inner, IEnumerable<ReadOnlyCommand> allowList)
{
    private readonly HashSet<string> _allowed = allowList.Select(c => c.Display).ToHashSet(StringComparer.Ordinal);

    public bool IsAllowed(ReadOnlyCommand command) => _allowed.Contains(command.Display);

    public Task<CommandResult> RunAsync(ReadOnlyCommand command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsAllowed(command))
            return Task.FromResult(CommandResult.NotStarted($"Refused: '{command.Display}' is not on the read-only allow-list."));
        return inner.RunAsync(command.FileName, command.Arguments, timeout ?? TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>
    /// Runs an allow-listed command with one file path appended after "--", so the path can never be read as an option.
    /// Used for per-file read-only queries such as "lsof -t -- path".
    /// </summary>
    public Task<CommandResult> RunWithPathAsync(ReadOnlyCommand prefix, string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsAllowed(prefix))
            return Task.FromResult(CommandResult.NotStarted($"Refused: '{prefix.Display}' is not on the read-only allow-list."));
        return inner.RunAsync(prefix.FileName, [.. prefix.Arguments, "--", path], timeout ?? TimeSpan.FromSeconds(30), ct);
    }
}
