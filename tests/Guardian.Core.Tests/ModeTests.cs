using Guardian.Core.Modes;

namespace Guardian.Core.Tests;

public class ModeTests
{
    [Fact]
    public void Fresh_install_is_observe_only()
    {
        var modes = new ModeManager(new MemoryModeStore());
        Assert.Equal(OperatingMode.ObserveOnly, modes.Current);
    }

    [Fact]
    public void Raising_mode_requires_the_exact_typed_phrase()
    {
        var store = new MemoryModeStore();
        var modes = new ModeManager(store);
        Assert.Throws<ModeChangeRefusedException>(() => modes.RaiseTo(OperatingMode.SafeMaintenance, "shan", "yes"));
        Assert.Throws<ModeChangeRefusedException>(() => modes.RaiseTo(OperatingMode.SafeMaintenance, "shan", "enable safe maintenance"));
        Assert.Equal(OperatingMode.ObserveOnly, modes.Current);

        var consent = modes.RaiseTo(OperatingMode.SafeMaintenance, "shan", "ENABLE SAFE MAINTENANCE");
        Assert.Equal(OperatingMode.SafeMaintenance, modes.Current);
        Assert.Equal("shan", consent.UserName);
        Assert.Single(store.Consents);
    }

    [Fact]
    public void Safe_maintenance_phrase_does_not_unlock_controlled_repair()
    {
        var modes = new ModeManager(new MemoryModeStore());
        Assert.Throws<ModeChangeRefusedException>(() => modes.RaiseTo(OperatingMode.ControlledRepair, "shan", "ENABLE SAFE MAINTENANCE"));
    }

    [Fact]
    public void Lowering_mode_needs_no_confirmation()
    {
        var modes = new ModeManager(new MemoryModeStore { Mode = OperatingMode.ControlledRepair });
        modes.LowerTo(OperatingMode.ObserveOnly);
        Assert.Equal(OperatingMode.ObserveOnly, modes.Current);
    }

    [Theory]
    [InlineData(OperatingMode.ObserveOnly, OperationKind.ReadOnly, true)]
    [InlineData(OperatingMode.ObserveOnly, OperationKind.SafeMaintenance, false)]
    [InlineData(OperatingMode.ObserveOnly, OperationKind.Repair, false)]
    [InlineData(OperatingMode.SafeMaintenance, OperationKind.SafeMaintenance, true)]
    [InlineData(OperatingMode.SafeMaintenance, OperationKind.Repair, false)]
    [InlineData(OperatingMode.ControlledRepair, OperationKind.SafeMaintenance, true)]
    [InlineData(OperatingMode.ControlledRepair, OperationKind.Repair, true)]
    public void Gate_allows_only_what_the_mode_permits(OperatingMode mode, OperationKind kind, bool allowed)
    {
        var gate = new OperationGate(new ModeManager(new MemoryModeStore { Mode = mode }));
        Assert.Equal(allowed, gate.Check(kind).Allowed);
    }
}
