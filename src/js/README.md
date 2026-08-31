# @mquark/actionful-client

Official JavaScript/TypeScript client for invoking [mQuark Actionful](https://mquark.com) endpoints.

See the full documentation and examples at [github.com/m-quark/ActionfulClient](https://github.com/m-quark/ActionfulClient).

## Installation

```sh
npm install @mquark/actionful-client
```

Requires Node.js 18+ or any modern browser with `fetch` support. Ships both ESM and CommonJS
builds with type definitions for each.

## Quick start

```ts
import { ActionfulClient } from '@mquark/actionful-client';

const client = new ActionfulClient({
  endpointUrl: 'https://...',
  accessToken: '...',
  accessSecret: '...',
});

// Single invocation
const result = await client.invoke<Order, RiskScore>(order);

// Batch
const results = await client.invokeBatch<Order, RiskScore>(orders);

// Stream results as each completes
for await (const r of client.streamBatch<Order, RiskScore>(orders)) {
  if (r.isSuccess) process(r.output!);
}

// Continuous pipeline
for await (const r of client.process<Order, RiskScore>(orderStream)) {
  await sink.write(r);
}
```

## Polling

An invocation returns in a single round trip when the flow has already completed; anything longer
comes back as a job the client polls for you. Waits start at `initialPollInterval` (250 ms) and
double up to `maxPollInterval` (5 s), with jitter. A `Retry-After` from the server always wins
over the ceiling.

```ts
const client = new ActionfulClient({
  endpointUrl: 'https://...',
  accessToken: '...',
  accessSecret: '...',
  initialPollInterval: 250,
  maxPollInterval: 5000,
});
```

Pass an `AbortSignal` to bound the total wait — the library imposes no deadline of its own.

## License

MIT
