using System.Threading.Channels;

namespace MQuark.Actionful.Client;

/// <summary>
/// A long-running processing pipeline created by <see cref="IActionfulClient.CreatePipeline{TInput,TOutput}"/>.
/// Write inputs to <see cref="Writer"/> and consume results from <see cref="Reader"/>.
/// Call <see cref="DisposeAsync"/> (or use <c>await using</c>) to signal that no more inputs will
/// arrive and to wait for in-flight jobs to finish.
/// </summary>
public sealed class ActionfulPipeline<TInput, TOutput> : IAsyncDisposable
{
    private readonly Channel<TInput> _input;
    private readonly Channel<InvocationResult<TOutput>> _output;
    private readonly Task _worker;

    internal ActionfulPipeline(
        Channel<TInput> input,
        Channel<InvocationResult<TOutput>> output,
        Task worker)
    {
        _input = input;
        _output = output;
        _worker = worker;
    }

    /// <summary>Write inputs here. Call <see cref="ChannelWriter{T}.Complete"/> when done producing.</summary>
    public ChannelWriter<TInput> Writer => _input.Writer;

    /// <summary>Read results from here. Completes automatically once all in-flight jobs finish.</summary>
    public ChannelReader<InvocationResult<TOutput>> Reader => _output.Reader;

    /// <summary>
    /// Signals the input as complete (if not already done) and waits for all in-flight jobs to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
