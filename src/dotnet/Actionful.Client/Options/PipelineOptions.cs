namespace MQuark.Actionful.Client;

/// <summary>Controls how <see cref="IActionfulClient.ProcessAsync"/> and
/// <see cref="IActionfulClient.CreatePipeline"/> process a stream.</summary>
public sealed class PipelineOptions
{
    /// <summary>Maximum number of endpoint invocations in flight simultaneously. Default: 10.</summary>
    public int MaxConcurrency { get; init; } = 10;

    /// <summary>
    /// Capacity of the bounded output channel used by <see cref="IActionfulClient.CreatePipeline"/>.
    /// When the buffer is full, the pipeline pauses accepting new inputs until the consumer catches up. Default: 100.
    /// </summary>
    public int OutputBufferCapacity { get; init; } = 100;

    /// <summary>
    /// When false (default), results are yielded in completion order — fastest jobs come out first.
    /// When true, results are yielded in the same order as the input sequence.
    /// </summary>
    public bool PreserveOrder { get; init; } = false;
}
