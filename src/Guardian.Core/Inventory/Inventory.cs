using Guardian.Core.Reporting;

namespace Guardian.Core.Inventory;

public enum ItemScope { CurrentUser, AllUsers, System }

public sealed record StartupItem(string Name, string Command, string Location, ItemScope Scope, string Mechanism);

public sealed record InstalledApplication(string Name, string? Version, string? Publisher, string Source);

/// <summary>Items plus the honest report of how they were collected.</summary>
public sealed record InventoryResult<T>(IReadOnlyList<T> Items, IReadOnlyList<CheckReport> Reports);

/// <summary>Lists startup items. Read-only: Guardian never disables or removes them.</summary>
public interface IStartupInventory
{
    Task<InventoryResult<StartupItem>> ListAsync(CancellationToken ct = default);
}

/// <summary>Lists installed applications. Read-only: Guardian never uninstalls or modifies them.</summary>
public interface IApplicationInventory
{
    Task<InventoryResult<InstalledApplication>> ListAsync(CancellationToken ct = default);
}
