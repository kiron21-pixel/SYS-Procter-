using Guardian.Core.Reporting;

namespace Guardian.Core.Platform;

/// <summary>A read-only check defined by a function. Exceptions become an honest "could not complete" report.</summary>
public sealed class DelegateCheck(string id, string title, Func<CancellationToken, Task<CheckReport>> run, bool onDemandOnly = false) : ISecurityCheck
{
    public string Id => id;
    public string Title => title;
    public bool OnDemandOnly => onDemandOnly;

    public async Task<CheckReport> RunAsync(CancellationToken ct = default)
    {
        try { return await run(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return CheckReport.Failed(id, title, "The check stopped with an unexpected error. No conclusion was drawn.", new Evidence("Error", ex.GetType().Name + ": " + ex.Message));
        }
    }
}
