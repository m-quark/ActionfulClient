# Actionful Client SDK

Client libraries for invoking published [mQuark Actionful](https://mquark.com) endpoints.

Build and publish your endpoint at [app.mquark.com](https://app.mquark.com), then use this SDK to call it from your application — passing in a payload and getting the result back, without writing HTTP boilerplate.

## Language Support

| Language | Package | Status |
|---|---|---|
| .NET / C# | [`MQuark.Actionful.Client`](https://www.nuget.org/packages/MQuark.Actionful.Client) (NuGet) | ✅ Available |
| JavaScript / TypeScript | [`@mquark/actionful-client`](https://www.npmjs.com/package/@mquark/actionful-client) (npm) | ✅ Available |
| Python | [`mquark-actionful-client`](https://pypi.org/project/mquark-actionful-client) (PyPI) | ✅ Available |
| Go | [`github.com/m-quark/ActionfulClient/src/go/v2`](https://pkg.go.dev/github.com/m-quark/ActionfulClient/src/go/v2) | ✅ Available |

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

#### Polling

`InvokeAsync` returns in a single round trip when the flow has already completed. Anything longer
comes back as a job the client polls for you, backing off between attempts.

```csharp
services.Configure<ActionfulClientOptions>(o =>
{
    o.InitialPollInterval = TimeSpan.FromMilliseconds(250);
    o.MaxPollInterval     = TimeSpan.FromSeconds(5);
});
```

The equivalent options are `initialPollInterval` / `maxPollInterval` (JS, milliseconds),
`initial_poll_interval` / `max_poll_interval` (Python, seconds), and `InitialPollInterval` /
`MaxPollInterval` (Go, `time.Duration`).

A `Retry-After` from the server always wins over the ceiling — the server knows what the endpoint
costs, so it can widen the gap but the client never narrows it. Waits carry ±10% jitter so a batch
submitted together does not come back in lockstep.

The server never holds a connection waiting on a flow, and there is no way for a caller to ask it
to: waiting is the client's side of the contract.

---

## JavaScript / TypeScript

### Installation

```sh
npm install @mquark/actionful-client
```

Requires Node.js 18+ or any modern browser with `fetch` support.

### Configuration

```ts
import { ActionfulClient } from '@mquark/actionful-client';

const client = new ActionfulClient({
  endpointUrl: 'https://...',
  accessToken: '...',
  accessSecret: '...',
});
```

### Usage

#### Invoke and wait (most common)

```ts
const result = await client.invoke<Order, RiskScore>(order);
```

#### Raw async — submit now, poll later

```ts
const ticket = await client.submit(order);
// ticket.jobId, ticket.pollUrl

const job = await client.getJob(ticket);
// job.status, job.resultJson, job.isTerminal
```

#### Batch

```ts
// Collect all results
const results = await client.invokeBatch<Order, RiskScore>(orders);

// Or stream results as each completes
for await (const r of client.streamBatch<Order, RiskScore>(orders)) {
  if (r.isSuccess) process(r.output!);
  else             log(r.error);
}
```

#### Continuous pipeline

```ts
// AsyncIterable source
for await (const r of client.process<Order, RiskScore>(orderStream)) {
  await sink.write(r);
}

// Channel-based — push/complete/iterate independently
const pipeline = client.createPipeline<Order, RiskScore>();

// producer
await pipeline.push(order);
pipeline.complete();

// consumer
for await (const r of pipeline) {
  process(r);
}
```

#### Timeouts (AbortSignal)

```ts
const signal = AbortSignal.timeout(120_000); // 2 minutes
const result = await client.invoke<Order, RiskScore>(order, signal);
```

---

## Python

### Installation

```sh
pip install mquark-actionful-client
```

Requires Python 3.10+ and `httpx`.

### Usage

```python
from mquark_actionful import ActionfulClient, ActionfulClientOptions

async with ActionfulClient(ActionfulClientOptions(
    endpoint_url="https://...",
    access_token="...",
    access_secret="...",
)) as client:
    ...
```

#### Invoke and wait (most common)

```python
result = await client.invoke({"orderId": 42})
# result is a dict / list / primitive (json.loads output)
```

#### Raw async — submit now, poll later

```python
ticket = await client.submit({"orderId": 42})
# ticket.job_id, ticket.poll_url

job = await client.get_job(ticket)
# job.status, job.result_json, job.is_terminal
```

#### Batch

```python
# Collect all results
results = await client.invoke_batch([item1, item2, item3])

# Or stream results as each completes
async for r in client.stream_batch(items):
    if r.is_success: process(r.output)
    else:            log(r.error)
```

#### Continuous pipeline

```python
# AsyncIterator source
async for r in client.process(async_item_stream()):
    await sink.write(r)

# Push/complete API
pipeline = client.create_pipeline()

async def producer():
    for item in items:
        await pipeline.push(item)
    pipeline.complete()

asyncio.create_task(producer())
async for r in pipeline:
    process(r)
```

#### Timeouts

```python
import asyncio

async with asyncio.timeout(120):  # 2 minutes
    result = await client.invoke({"orderId": 42})
```

---

## Go

### Installation

```sh
go get github.com/m-quark/ActionfulClient/src/go/v2
```

Requires Go 1.22+.

> Go has no package registry — the import path *is* the source location — so the path names this
> repository and the subdirectory the client lives in. Releases are tagged `src/go/vX.Y.Z`, which
> is how Go versions a module that is not at its repository root. The trailing `/v2` is required for
> any Go module at major version 2 or above; it belongs to the module path, not to the directory.

### Usage

```go
import actionful "github.com/m-quark/ActionfulClient/src/go/v2"

client, err := actionful.New(actionful.Options{
    EndpointURL:  "https://...",
    AccessToken:  "...",
    AccessSecret: "...",
})
```

#### Invoke and wait (most common)

```go
score, err := actionful.Invoke[Order, RiskScore](ctx, client, order)
```

#### Raw async — submit now, poll later

```go
ticket, err := client.Submit(ctx, `{"orderId":42}`)
// ticket.JobID, ticket.PollURL

job, err := client.GetJob(ctx, ticket)
// job.Status, job.ResultJSON, job.IsTerminal
```

#### Batch

```go
// Collect all results
results, err := actionful.InvokeBatch[Order, RiskScore](ctx, client, orders, nil)

// Or stream results as each completes
for r := range actionful.StreamBatch[Order, RiskScore](ctx, client, orders, nil) {
    if r.IsSuccess { process(r.Output) } else { log(r.Error) }
}
```

#### Continuous pipeline

```go
// Channel-based source
inputCh := make(chan Order, 100)
for r := range actionful.Process[Order, RiskScore](ctx, client, inputCh, nil) {
    sink.Write(r)
}

// Push/complete API
pipeline := actionful.NewPipeline[Order, RiskScore](ctx, client, nil)
go func() {
    pipeline.Push(ctx, order)
    pipeline.Complete()
}()
for r := range pipeline.Results() {
    process(r)
}
```

#### Timeouts

```go
ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
defer cancel()
score, err := actionful.Invoke[Order, RiskScore](ctx, client, order)
```

---

## Releasing

Maintainers: see [docs/design/release-process.md](docs/design/release-process.md)
before cutting a release. Four SDKs ship from this repo on four independent tags, and several of
the failure modes are silent.

---

## License

MIT
