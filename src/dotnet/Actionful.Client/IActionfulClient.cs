namespace MQuark.Actionful.Client;

/// <summary>
/// Client for invoking a published mQuark Actionful endpoint.
/// </summary>
/// <remarks>
/// Four layers of API are available — choose the one that fits your use case:
/// <list type="bullet">
///   <item><b>Layer 1 — Raw async:</b> <see cref="SubmitAsync(string,CancellationToken)"/> /
///     <see cref="GetJobAsync(InvocationTicket,CancellationToken)"/> — you own the polling loop.</item>
///   <item><b>Layer 2 — Invoke and wait:</b> <see cref="InvokeAsync(string,CancellationToken)"/> /
///     <see cref="InvokeAsync{TInput,TOutput}"/> — submit, poll, and return the result in one call.</item>
///   <item><b>Layer 3 — Batch:</b> <see cref="InvokeBatchAsync{TInput,TOutput}"/> /
///     <see cref="StreamBatchAsync{TInput,TOutput}"/> — process a collection with bounded concurrency.</item>
///   <item><b>Layer 4 — Pipeline:</b> <see cref="ProcessAsync{TInput,TOutput}"/> /
///     <see cref="CreatePipeline{TInput,TOutput}"/> — continuous streaming with backpressure.</item>
/// </list>
/// Pass a <see cref="CancellationToken"/> with a deadline to any method to impose a timeout.
/// </remarks>
public interface IActionfulClient
{
    // ── Layer 1 · Raw async ───────────────────────────────────────────────

    /// <summary>
    /// Submits a raw JSON payload and returns a ticket immediately.
    /// The endpoint processes the job asynchronously; use <see cref="GetJobAsync(InvocationTicket,CancellationToken)"/>
    /// to poll for completion.
    /// </summary>
    Task<InvocationTicket> SubmitAsync(string jsonPayload, CancellationToken ct = default);

    /// <summary>
    /// Serializes <paramref name="input"/> to JSON, submits it, and returns a ticket immediately.
    /// </summary>
    Task<InvocationTicket> SubmitAsync<TInput>(TInput input, CancellationToken ct = default);

    /// <summary>
    /// Polls the job identified by <paramref name="ticket"/> once and returns its current state.
    /// Call repeatedly until <see cref="InvocationJob.IsTerminal"/> is true.
    /// </summary>
    Task<InvocationJob> GetJobAsync(InvocationTicket ticket, CancellationToken ct = default);

    /// <summary>
    /// Polls the job at <paramref name="pollUrl"/> once and returns its current state.
    /// </summary>
    Task<InvocationJob> GetJobAsync(string pollUrl, CancellationToken ct = default);

    // ── Layer 2 · Invoke and wait ─────────────────────────────────────────

    /// <summary>
    /// Submits a raw JSON payload and waits until the endpoint returns a result.
    /// Returns the raw JSON result string.
    /// </summary>
    Task<string> InvokeAsync(string jsonPayload, CancellationToken ct = default);

    /// <summary>
    /// Serializes <paramref name="input"/> to JSON, submits it, waits for completion,
    /// and deserializes the result to <typeparamref name="TOutput"/>.
    /// </summary>
    Task<TOutput> InvokeAsync<TInput, TOutput>(TInput input, CancellationToken ct = default);

    // ── Layer 3 · Batch ───────────────────────────────────────────────────

    /// <summary>
    /// Invokes the endpoint for each item in <paramref name="inputs"/>, running up to
    /// <see cref="BatchOptions.MaxConcurrency"/> invocations in parallel.
    /// Waits for all items to complete (or fail) before returning.
    /// </summary>
    Task<IReadOnlyList<InvocationResult<TOutput>>> InvokeBatchAsync<TInput, TOutput>(
        IEnumerable<TInput> inputs,
        BatchOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="InvokeBatchAsync{TInput,TOutput}"/> but yields results as each item
    /// completes rather than waiting for the entire batch. Results arrive in completion order
    /// unless <see cref="BatchOptions"/> is configured otherwise.
    /// </summary>
    IAsyncEnumerable<InvocationResult<TOutput>> StreamBatchAsync<TInput, TOutput>(
        IEnumerable<TInput> inputs,
        BatchOptions? options = null,
        CancellationToken ct = default);

    // ── Layer 4 · Pipeline ────────────────────────────────────────────────

    /// <summary>
    /// Processes items from an <see cref="IAsyncEnumerable{T}"/> source, running up to
    /// <see cref="PipelineOptions.MaxConcurrency"/> invocations in parallel.
    /// Yields results as each item completes.
    /// </summary>
    IAsyncEnumerable<InvocationResult<TOutput>> ProcessAsync<TInput, TOutput>(
        IAsyncEnumerable<TInput> inputs,
        PipelineOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a long-running pipeline with a <see cref="System.Threading.Channels.ChannelWriter{T}"/> for input
    /// and a <see cref="System.Threading.Channels.ChannelReader{T}"/> for output.
    /// Suitable for unbounded workloads where producer and consumer run in separate tasks.
    /// Dispose the pipeline to signal completion and drain remaining results.
    /// </summary>
    ActionfulPipeline<TInput, TOutput> CreatePipeline<TInput, TOutput>(PipelineOptions? options = null);
}
