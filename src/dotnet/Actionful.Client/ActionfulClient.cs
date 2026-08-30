using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MQuark.Actionful.Client;

/// <inheritdoc cref="IActionfulClient"/>
internal sealed class ActionfulClient(
    HttpClient http,
    IOptions<ActionfulClientOptions> options,
    ILogger<ActionfulClient> logger) : IActionfulClient
{
    private readonly ActionfulClientOptions _options = options.Value;

    // Fraction by which a poll wait is randomly spread, so a batch submitted together does not come back
    // in lockstep.
    private const double JitterRatio = 0.2;

    // Case-insensitive to tolerate varying JSON conventions from endpoint authors.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ── Factory for standalone (no DI) usage ─────────────────────────────

    /// <summary>
    /// Creates a standalone client without a DI container.
    /// Use <c>AddActionfulClient</c> when running inside a hosted application.
    /// </summary>
    public static IActionfulClient Create(ActionfulClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new AccessTokenHandler(Options.Create(options))
        {
            InnerHandler = new HttpClientHandler()
        };
        var http = new HttpClient(handler);
        return new ActionfulClient(
            http,
            Options.Create(options),
            NullLogger<ActionfulClient>.Instance);
    }

    // ── Layer 1 · Raw async ───────────────────────────────────────────────

    public async Task<InvocationTicket> SubmitAsync(string jsonPayload, CancellationToken ct = default)
    {
        using var request = BuildPostRequest(_options.EndpointUrl, jsonPayload);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        // The server never holds the connection, so a just-scheduled workflow is still running and the
        // response is 202. If it completed inside that window, surface a clear error rather than
        // silently dropping the result — there is no Location to build a ticket from.
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new ActionfulException(response.StatusCode,
                "Expected 202 Accepted from SubmitAsync but received a different status code.");

        return ParseTicket(response);
    }

    public Task<InvocationTicket> SubmitAsync<TInput>(TInput input, CancellationToken ct = default) =>
        SubmitAsync(JsonSerializer.Serialize(input), ct);

    public Task<InvocationJob> GetJobAsync(InvocationTicket ticket, CancellationToken ct = default) =>
        GetJobAsync(ticket.PollUrl, ct);

    public async Task<InvocationJob> GetJobAsync(string pollUrl, CancellationToken ct = default)
    {
        var response = await http.GetAsync(pollUrl, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new InvocationJob(ExtractJobId(pollUrl), InvocationStatus.Succeeded, result, null);
        }

        // 202 — still running
        return new InvocationJob(ExtractJobId(pollUrl), InvocationStatus.Running, null, null);
    }

    // ── Layer 2 · Invoke and wait ─────────────────────────────────────────

    public async Task<string> InvokeAsync(string jsonPayload, CancellationToken ct = default)
    {
        using var request = BuildPostRequest(_options.EndpointUrl, jsonPayload);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 202 — poll until done
        var ticket = ParseTicket(response);
        return await PollUntilCompleteAsync(ticket.PollUrl, ct).ConfigureAwait(false);
    }

    public async Task<TOutput> InvokeAsync<TInput, TOutput>(TInput input, CancellationToken ct = default) =>
        Deserialize<TOutput>(await InvokeAsync(JsonSerializer.Serialize(input), ct).ConfigureAwait(false));

    // ── Layer 3 · Batch ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<InvocationResult<TOutput>>> InvokeBatchAsync<TInput, TOutput>(
        IEnumerable<TInput> inputs,
        BatchOptions? options = null,
        CancellationToken ct = default)
    {
        var results = new List<InvocationResult<TOutput>>();
        await foreach (var r in StreamBatchAsync<TInput, TOutput>(inputs, options, ct).ConfigureAwait(false))
            results.Add(r);
        return results;
    }

    public async IAsyncEnumerable<InvocationResult<TOutput>> StreamBatchAsync<TInput, TOutput>(
        IEnumerable<TInput> inputs,
        BatchOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = options ?? new BatchOptions();
        var output = Channel.CreateBounded<InvocationResult<TOutput>>(
            new BoundedChannelOptions(opts.OutputBufferCapacity) { SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        using var semaphore = new SemaphoreSlim(opts.MaxConcurrency);
        var stopSubmitting = 0; // 1 when StopOnFirstFailure triggers

        var producer = Task.Run(async () =>
        {
            var tasks = new List<Task>();
            try
            {
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref stopSubmitting) == 1) break;
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(RunWorkerAsync(
                        input, semaphore, output.Writer,
                        onFailure: opts.StopOnFirstFailure
                            ? () => Volatile.Write(ref stopSubmitting, 1)
                            : null,
                        ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                output.Writer.TryComplete();
            }
        }, ct);

        await foreach (var result in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        await producer.ConfigureAwait(false);
    }

    // ── Layer 4 · Pipeline ────────────────────────────────────────────────

    public async IAsyncEnumerable<InvocationResult<TOutput>> ProcessAsync<TInput, TOutput>(
        IAsyncEnumerable<TInput> inputs,
        PipelineOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = options ?? new PipelineOptions();
        var output = Channel.CreateBounded<InvocationResult<TOutput>>(
            new BoundedChannelOptions(opts.OutputBufferCapacity) { SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        using var semaphore = new SemaphoreSlim(opts.MaxConcurrency);

        var producer = Task.Run(async () =>
        {
            var tasks = new List<Task>();
            try
            {
                await foreach (var input in inputs.WithCancellation(ct).ConfigureAwait(false))
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(RunWorkerAsync(input, semaphore, output.Writer, onFailure: null, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                output.Writer.TryComplete();
            }
        }, ct);

        await foreach (var result in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        await producer.ConfigureAwait(false);
    }

    public ActionfulPipeline<TInput, TOutput> CreatePipeline<TInput, TOutput>(PipelineOptions? options = null)
    {
        var opts = options ?? new PipelineOptions();
        var input = Channel.CreateBounded<TInput>(
            new BoundedChannelOptions(opts.InputBufferCapacity) { SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var output = Channel.CreateBounded<InvocationResult<TOutput>>(
            new BoundedChannelOptions(opts.OutputBufferCapacity) { SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait });

        var worker = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(opts.MaxConcurrency);
            var tasks = new List<Task>();
            try
            {
                await foreach (var item in input.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    tasks.Add(RunWorkerAsync<TInput, TOutput>(
                        item, semaphore, output.Writer, onFailure: null, CancellationToken.None));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                output.Writer.TryComplete();
            }
        });

        return new ActionfulPipeline<TInput, TOutput>(input, output, worker);
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private async Task RunWorkerAsync<TInput, TOutput>(
        TInput input,
        SemaphoreSlim semaphore,
        ChannelWriter<InvocationResult<TOutput>> writer,
        Action? onFailure,
        CancellationToken ct)
    {
        InvocationTicket? ticket = null;
        try
        {
            ticket = await SubmitAsync(JsonSerializer.Serialize(input), ct).ConfigureAwait(false);
            var resultJson = await PollUntilCompleteAsync(ticket.PollUrl, ct).ConfigureAwait(false);
            var output = Deserialize<TOutput>(resultJson);
            await writer.WriteAsync(new InvocationResult<TOutput>(ticket, output, null), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invocation failed for job {JobId}", ticket?.JobId ?? "unknown");
            var errorTicket = ticket ?? new InvocationTicket("unknown", string.Empty, DateTimeOffset.UtcNow);
            await writer.WriteAsync(
                new InvocationResult<TOutput>(errorTicket, default, ex.Message), ct).ConfigureAwait(false);
            onFailure?.Invoke();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<string> PollUntilCompleteAsync(string pollUrl, CancellationToken ct)
    {
        var backoff = _options.InitialPollInterval;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var response = await http.GetAsync(pollUrl, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 202 — still running. Retry-After is the server's instruction, not a suggestion: it knows what
            // this endpoint costs, and it outranks MaxPollInterval, which bounds only our own backoff.
            var interval = response.Headers.RetryAfter?.Delta is { } delta && delta > backoff ? delta : backoff;

            var wait = ApplyJitter(interval);
            logger.LogDebug("Job at {PollUrl} still running, waiting {WaitMs}ms", pollUrl, wait.TotalMilliseconds);
            await Task.Delay(wait, ct).ConfigureAwait(false);

            if (backoff < _options.MaxPollInterval)
                backoff = backoff + backoff > _options.MaxPollInterval ? _options.MaxPollInterval : backoff + backoff;
        }
    }

    private static TimeSpan ApplyJitter(TimeSpan interval) =>
        TimeSpan.FromMilliseconds(
            interval.TotalMilliseconds * (1 + ((Random.Shared.NextDouble() - 0.5) * JitterRatio)));

    private static HttpRequestMessage BuildPostRequest(string url, string jsonPayload) => 
        new(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

    private static InvocationTicket ParseTicket(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new ActionfulException(response.StatusCode, "202 Accepted response is missing the Location header.");

        var pollUrl = location.IsAbsoluteUri
            ? location.ToString()
            : new Uri(new Uri("https://placeholder"), location).ToString();

        return new InvocationTicket(ExtractJobId(pollUrl), pollUrl, DateTimeOffset.UtcNow);
    }

    private static string ExtractJobId(string url)
    {
        var span = url.AsSpan().TrimEnd('/');
        var slash = span.LastIndexOf('/');
        return slash >= 0 ? span[(slash + 1)..].ToString() : url;
    }

    private static TOutput Deserialize<TOutput>(string json)
    {
        // When the caller asked for a raw string, return the response body as-is without JSON parsing.
        if (typeof(TOutput) == typeof(string))
            return (TOutput)(object)json;

        return JsonSerializer.Deserialize<TOutput>(json, JsonOptions)
            ?? throw new ActionfulException(HttpStatusCode.OK, "Endpoint returned a null result.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
            ? response.Headers.RetryAfter?.Delta
            : null;

        throw new ActionfulException(response.StatusCode, body, retryAfter);
    }
}
