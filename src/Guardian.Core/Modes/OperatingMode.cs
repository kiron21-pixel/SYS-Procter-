namespace Guardian.Core.Modes;

/// <summary>The three operating modes. The numeric order is the order of increasing permission.</summary>
public enum OperatingMode
{
    /// <summary>Mode A, the default: inspect and report, never change the system.</summary>
    ObserveOnly = 0,
    /// <summary>Mode B: narrowly defined low-risk maintenance, after explicit consent.</summary>
    SafeMaintenance = 1,
    /// <summary>Mode C: high-impact repairs, each one approved individually by the user.</summary>
    ControlledRepair = 2,
}

/// <summary>What an operation would do to the computer.</summary>
public enum OperationKind
{
    /// <summary>Reads state only. Allowed in every mode.</summary>
    ReadOnly,
    /// <summary>A low-risk change of a kind listed for Safe Maintenance.</summary>
    SafeMaintenance,
    /// <summary>A high-impact change. Needs Controlled Repair mode and a per-operation approval.</summary>
    Repair,
}

/// <summary>A record that the user explicitly agreed to a mode change.</summary>
public sealed record ConsentRecord(
    OperatingMode Mode,
    string UserName,
    DateTimeOffset GrantedAt,
    string StatementShown,
    string ConfirmationTyped);

public static class ModeDescriptions
{
    public static string Title(OperatingMode m) => m switch
    {
        OperatingMode.ObserveOnly => "Observe Only",
        OperatingMode.SafeMaintenance => "Safe Maintenance",
        OperatingMode.ControlledRepair => "Controlled Repair",
        _ => m.ToString(),
    };

    public static string Explanation(OperatingMode m) => m switch
    {
        OperatingMode.ObserveOnly =>
            "Guardian inspects security status, performance, startup items and cleanup candidates, and writes reports. It makes no changes to your computer.",
        OperatingMode.SafeMaintenance =>
            "Guardian may move verified, unused temporary files that you select into its own holding area (restorable for 7 days) and run read-only diagnostics. Every action is logged. It will not touch other applications, settings or personal files.",
        OperatingMode.ControlledRepair =>
            "Guardian may propose specific repairs. Each repair shows the evidence, exactly what it changes, its impact, backup and rollback options, and runs only after you approve that exact operation.",
        _ => "",
    };

    /// <summary>The words the user must type to confirm a mode upgrade. Typing, not a single click, is deliberate.</summary>
    public static string RequiredConfirmation(OperatingMode m) => m switch
    {
        OperatingMode.SafeMaintenance => "ENABLE SAFE MAINTENANCE",
        OperatingMode.ControlledRepair => "ENABLE CONTROLLED REPAIR",
        _ => "",
    };
}
