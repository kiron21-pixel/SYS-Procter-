namespace Guardian.Core.Modes;

/// <summary>Persists the current mode and consent history. Implemented by Guardian.Data.</summary>
public interface IModeStore
{
    /// <summary>Returns null when no mode was ever stored (a fresh install).</summary>
    OperatingMode? LoadMode();
    void SaveMode(OperatingMode mode, ConsentRecord? consent);
    IReadOnlyList<ConsentRecord> LoadConsentHistory();
}

public sealed class ModeChangeRefusedException(string message) : Exception(message);

/// <summary>
/// Owns the current operating mode. A fresh install is always Observe Only. Raising the mode needs a
/// typed confirmation; lowering it never does.
/// </summary>
public sealed class ModeManager(IModeStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public OperatingMode Current => store.LoadMode() ?? OperatingMode.ObserveOnly;

    public event Action<OperatingMode>? ModeChanged;

    public ConsentRecord RaiseTo(OperatingMode target, string userName, string confirmationTyped)
    {
        if (target <= Current)
            throw new ModeChangeRefusedException($"{ModeDescriptions.Title(target)} is not higher than the current mode.");
        var required = ModeDescriptions.RequiredConfirmation(target);
        if (!string.Equals(confirmationTyped.Trim(), required, StringComparison.Ordinal))
            throw new ModeChangeRefusedException($"To enable {ModeDescriptions.Title(target)}, type exactly: {required}");

        var consent = new ConsentRecord(target, userName, _clock.GetUtcNow(), ModeDescriptions.Explanation(target), confirmationTyped.Trim());
        store.SaveMode(target, consent);
        ModeChanged?.Invoke(target);
        return consent;
    }

    /// <summary>Lowering permissions is always allowed and needs no confirmation.</summary>
    public void LowerTo(OperatingMode target)
    {
        if (target >= Current) return;
        store.SaveMode(target, null);
        ModeChanged?.Invoke(target);
    }
}

public sealed record GateDecision(bool Allowed, string Reason);

/// <summary>Every operation that could change the system asks the gate first.</summary>
public sealed class OperationGate(ModeManager modes)
{
    public GateDecision Check(OperationKind kind) => (kind, modes.Current) switch
    {
        (OperationKind.ReadOnly, _) => new(true, "Read-only operations are allowed in every mode."),
        (OperationKind.SafeMaintenance, >= OperatingMode.SafeMaintenance) => new(true, "Safe Maintenance is enabled."),
        (OperationKind.SafeMaintenance, _) => new(false, "Safe Maintenance is not enabled. Guardian is in Observe Only mode and will not change the system."),
        (OperationKind.Repair, OperatingMode.ControlledRepair) => new(true, "Controlled Repair is enabled; the operation still needs its own approval."),
        (OperationKind.Repair, _) => new(false, "Controlled Repair is not enabled."),
        _ => new(false, "Unknown operation kind."),
    };
}
