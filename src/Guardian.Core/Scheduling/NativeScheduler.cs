namespace Guardian.Core.Scheduling;

/// <summary>A recurring Guardian job. Scheduled jobs always run in the mode that is current at run time.</summary>
public sealed record ScheduledJob(string Name, string ExecutablePath, IReadOnlyList<string> Arguments, TimeOnly TimeOfDay, DayOfWeek? Weekly);

/// <summary>The native definition file for a job, and how to install or remove it.</summary>
public sealed record ScheduleDefinition(string Mechanism, string FileName, string Content, string InstallInstructions, string RemoveInstructions);

/// <summary>
/// Renders jobs for the OS's own scheduler (Task Scheduler, launchd, systemd user timers). Rendering is pure;
/// installing is left to the installer or to the user so that Guardian does not register system tasks on its own.
/// </summary>
public interface INativeScheduler
{
    string Mechanism { get; }
    IReadOnlyList<ScheduleDefinition> Render(ScheduledJob job);
}
