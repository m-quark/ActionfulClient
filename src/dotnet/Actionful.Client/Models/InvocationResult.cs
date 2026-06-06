namespace MQuark.Actionful.Client;

/// <summary>
/// Typed result of a single invocation, as returned by batch and pipeline operations.
/// <para>
/// <b>Ticket</b> — identifies the underlying job.<br/>
/// <b>Output</b> — deserialized result; populated when <see cref="IsSuccess"/> is true.<br/>
/// <b>Error</b> — error message; populated when the invocation failed.
/// </para>
/// </summary>
public sealed record InvocationResult<TOutput>(
    InvocationTicket Ticket,
    TOutput? Output,
    string? Error)
{
    /// <summary>True when the invocation completed successfully and <see cref="Output"/> is populated.</summary>
    public bool IsSuccess => Error is null;
}
