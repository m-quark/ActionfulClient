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

    /// <summary>
    /// Capacity of the bounded output buffer used by
    /// <see cref="IActionfulClient.StreamBatchAsync{TInput,TOutput}"/>.
    /// When full, workers block until the consumer reads — this is the backpressure mechanism.
    /// Default: 1000.
    /// </summary>
    public int OutputBufferCapacity { get; init; } = 1000;
}
