# Actionful Client SDK

Client libraries for invoking published [mQuark Actionful](https://mquark.com) endpoints.

Build and publish your endpoint at [app.mquark.com](https://app.mquark.com), then use this SDK to call it from your application — passing in a payload and getting the result back, without writing HTTP boilerplate.

## Language Support

| Language | Package | Status |
|---|---|---|
| .NET / C# | [`MQuark.Actionful.Client`](https://www.nuget.org/packages/MQuark.Actionful.Client) (NuGet) | ✅ Available |
| JavaScript / TypeScript | `@mquark/actionful-client` (npm) | 🚧 Coming soon |
| Python | `mquark-actionful-client` (PyPI) | Planned |
| Go | `github.com/mquark/actionful-client-go` | Planned |

---

## .NET / C#

### Installation

```sh
dotnet add package MQuark.Actionful.Client
```

Requires .NET 10+.

### Configuration

Credentials are shown in the Actionful Web UI when you publish an endpoint.

```json
// appsettings.json
{
  "Actionful": {
    "EndpointUrl": "https://...",
    "AccessToken": "...",
    "AccessSecret": "..."
  }
}
```

### DI Registration

```csharp
services.AddActionfulClient(configuration.GetSection("Actionful"));
```

Returns `IHttpClientBuilder` — chain resilience policies if needed:

```csharp
services.AddActionfulClient(configuration.GetSection("Actionful"))
        .AddStandardResilienceHandler();
```

### Usage

#### Invoke and wait (most common)

```csharp
public class OrderProcessor(IActionfulClient client)
{
    public async Task<RiskScore> AssessAsync(Order order, CancellationToken ct)
        => await client.InvokeAsync<Order, RiskScore>(order, ct);
}
```

#### Raw async — submit now, poll later

```csharp
InvocationTicket ticket = await client.SubmitAsync(order, ct);
// ... store ticket.JobId or ticket.PollUrl ...

InvocationJob job = await client.GetJobAsync(ticket, ct);
// job.Status, job.ResultJson, job.IsTerminal
```

#### Batch

```csharp
// Collect all results
IReadOnlyList<InvocationResult<RiskScore>> results =
    await client.InvokeBatchAsync<Order, RiskScore>(orders, ct: ct);

// Or stream results as each completes
await foreach (var r in client.StreamBatchAsync<Order, RiskScore>(orders, ct: ct))
{
    if (r.IsSuccess) Process(r.Output!);
    else             Log(r.Error);
}
```

#### Continuous pipeline

```csharp
// IAsyncEnumerable source
await foreach (var r in client.ProcessAsync<Order, RiskScore>(orderStream, ct: ct))
    await sink.WriteAsync(r, ct);

// Channel-based — separate producer and consumer tasks
await using var pipeline = client.CreatePipeline<Order, RiskScore>();

var producer = Task.Run(async () => {
    await pipeline.Writer.WriteAsync(order, ct);
    pipeline.Writer.Complete();
});

await foreach (var r in pipeline.Reader.ReadAllAsync(ct))
    Process(r);

await producer;
```

#### Standalone (no DI host)

```csharp
var client = ActionfulClient.Create(new ActionfulClientOptions
{
    EndpointUrl  = "https://...",
    AccessToken  = "...",
    AccessSecret = "...",
});
```

#### Timeouts

There is no built-in timeout. Pass a `CancellationToken` with a deadline:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var result = await client.InvokeAsync<Order, RiskScore>(order, cts.Token);
```

---

## JavaScript / TypeScript

> Coming soon — `@mquark/actionful-client` (npm).

The JS/TS client mirrors the .NET API with idiomatic JavaScript conventions:
`AbortSignal` for cancellation, `AsyncIterable` for streaming, `fetch`-based with no runtime dependencies.

---

## License

MIT
