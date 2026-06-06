namespace MQuark.Actionful.Client;

/// <summary>
/// Snapshot of a job's state returned by a single poll call.
/// <para>
/// <b>JobId</b> — unique job identifier.<br/>
/// <b>Status</b> — current status.<br/>
/// <b>ResultJson</b> — raw JSON result; populated only when <see cref="Status"/> is <see cref="InvocationStatus.Succeeded"/>.<br/>
/// <b>Error</b> — error message; populated only when <see cref="Status"/> is <see cref="InvocationStatus.Failed"/>.
/// </para>
/// </summary>
public sealed record InvocationJob(
    string JobId,
    InvocationStatus Status,
    string? ResultJson,
    string? Error)
{
    /// <summary>True when the job has reached a terminal state (succeeded, failed, or cancelled).</summary>
    public bool IsTerminal => Status is InvocationStatus.Succeeded or InvocationStatus.Failed or InvocationStatus.Cancelled;
}
