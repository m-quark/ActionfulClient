namespace MQuark.Actionful.Client;

/// <summary>Controls how <see cref="IActionfulClient.ProcessAsync"/> and
/// <see cref="IActionfulClient.CreatePipeline"/> process a stream.</summary>
public sealed class PipelineOptions
{
    /// <summary>Maximum number of endpoint invocations in flight simultaneously. Default: 10.</summary>
    public int MaxConcurrency { get; init; } = 10;

    /// <summary>
    /// Capacity of the bounded input channel used by
    /// <see cref="IActionfulClient.CreatePipeline{TInput,TOutput}"/>.
    /// When full, <see cref="ActionfulPipeline{TInput,TOutput}.Writer"/> blocks until the
    /// pipeline consumes an item — this is the producer-side backpressure mechanism. Default: 1000.
    /// </summary>
    public int InputBufferCapacity { get; init; } = 1000;

    /// <summary>
    /// Capacity of the bounded output buffer used by
    /// <see cref="IActionfulClient.ProcessAsync{TInput,TOutput}"/> and
    /// <see cref="IActionfulClient.CreatePipeline{TInput,TOutput}"/>.
    /// When full, workers block until the consumer reads — this is the consumer-side backpressure mechanism.
    /// Default: 1000.
    /// </summary>
    public int OutputBufferCapacity { get; init; } = 1000;

    /// <summary>
    /// When false (default), results are yielded in completion order — fastest jobs come out first.
    /// When true, results are yielded in the same order as the input sequence.
    /// </summary>
    public bool PreserveOrder { get; init; } = false;
}
