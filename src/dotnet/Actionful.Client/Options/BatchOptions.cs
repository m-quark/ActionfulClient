namespace MQuark.Actionful.Client;

/// <summary>Controls how <see cref="IActionfulClient.InvokeBatchAsync"/> processes a collection.</summary>
public sealed class BatchOptions
{
    /// <summary>Maximum number of endpoint invocations in flight simultaneously. Default: 10.</summary>
    public int MaxConcurrency { get; init; } = 10;

    /// <summary>
    /// When true, no further items are submitted after the first failure.
    /// Already-running invocations are allowed to complete. Default: false.
    /// </summary>
    public bool StopOnFirstFailure { get; init; } = false;
}
