import { ActionfulError } from './errors.js';
import { BoundedChannel } from './channel.js';
import { Semaphore } from './semaphore.js';
import { ActionfulPipeline, makeErrorResult, makeResult } from './pipeline.js';
import type {
  ActionfulClientOptions,
  BatchOptions,
  InvocationJob,
  InvocationResult,
  InvocationTicket,
  PipelineOptions,
} from './types.js';

const DEFAULT_INITIAL_POLL_INTERVAL = 250;
const DEFAULT_MAX_POLL_INTERVAL = 5000;
// Fraction by which a poll wait is randomly spread, so a batch submitted together does not come back
// in lockstep.
const JITTER_RATIO = 0.2;
const DEFAULT_BATCH_CONCURRENCY = 10;
const DEFAULT_BATCH_OUTPUT_CAPACITY = 1000;
const DEFAULT_PIPELINE_CONCURRENCY = 10;

export class ActionfulClient {
  private readonly _endpointUrl: string;
  private readonly _authHeader: string;
  private readonly _initialPollInterval: number;
  private readonly _maxPollInterval: number;

  constructor(options: ActionfulClientOptions) {
    this._endpointUrl = options.endpointUrl;
    this._authHeader = buildBasicAuth(options.accessToken, options.accessSecret);
    this._initialPollInterval = options.initialPollInterval ?? DEFAULT_INITIAL_POLL_INTERVAL;
    this._maxPollInterval = options.maxPollInterval ?? DEFAULT_MAX_POLL_INTERVAL;
  }

  // ── Layer 1 · Raw async ─────────────────────────────────────────────────

  /** Submits a payload and returns a ticket immediately. */
  async submit<TInput>(input: TInput, signal?: AbortSignal): Promise<InvocationTicket> {
    const payload = serialise(input);
    const response = await this._fetch(this._endpointUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: payload,
      signal,
    });
    await ensureSuccess(response);
    return parseTicket(response);
  }

  /** Polls once and returns the job's current state. */
  async getJob(ticketOrUrl: InvocationTicket | string, signal?: AbortSignal): Promise<InvocationJob> {
    const pollUrl = typeof ticketOrUrl === 'string' ? ticketOrUrl : ticketOrUrl.pollUrl;
    const response = await this._fetch(pollUrl, { signal });
    await ensureSuccess(response);

    const jobId = extractJobId(pollUrl);
    if (response.status === 200) {
      const resultJson = await response.text();
      return { jobId, status: 'succeeded', resultJson, error: null, isTerminal: true };
    }
    return { jobId, status: 'running', resultJson: null, error: null, isTerminal: false };
  }

  // ── Layer 2 · Invoke and wait ──────────────────────────────────────────

  /** Submits and waits for completion. Returns the raw result string. */
  async invokeRaw(payload: string, signal?: AbortSignal): Promise<string> {
    const response = await this._fetch(this._endpointUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: payload,
      signal,
    });
    await ensureSuccess(response);

    if (response.status === 200) return response.text();

    const ticket = parseTicket(response);
    return this._pollUntilComplete(ticket.pollUrl, signal);
  }

  /** Serializes input, submits, waits for completion, and deserializes output. */
  async invoke<TInput, TOutput>(input: TInput, signal?: AbortSignal): Promise<TOutput> {
    const resultJson = await this.invokeRaw(serialise(input), signal);
    return deserialise<TOutput>(resultJson);
  }

  // ── Layer 3 · Batch ────────────────────────────────────────────────────

  /** Processes all inputs and returns results when everything completes. */
  async invokeBatch<TInput, TOutput>(
    inputs: Iterable<TInput>,
    options?: BatchOptions,
    signal?: AbortSignal,
  ): Promise<InvocationResult<TOutput>[]> {
    const results: InvocationResult<TOutput>[] = [];
    for await (const r of this.streamBatch<TInput, TOutput>(inputs, options, signal)) results.push(r);
    return results;
  }

  /** Streams results as each item completes. */
  async *streamBatch<TInput, TOutput>(
    inputs: Iterable<TInput>,
    options?: BatchOptions,
    signal?: AbortSignal,
  ): AsyncIterable<InvocationResult<TOutput>> {
    const maxConcurrency = options?.maxConcurrency ?? DEFAULT_BATCH_CONCURRENCY;
    const outputCapacity = options?.outputBufferCapacity ?? DEFAULT_BATCH_OUTPUT_CAPACITY;
    const stopOnFirstFailure = options?.stopOnFirstFailure ?? false;

    const output = new BoundedChannel<InvocationResult<TOutput>>(outputCapacity);
    const semaphore = new Semaphore(maxConcurrency);
    let stopSubmitting = false;

    const producer = (async () => {
      const tasks: Promise<void>[] = [];
      try {
        for (const input of inputs) {
          if (stopSubmitting || signal?.aborted) break;
          await semaphore.acquire(signal);
          tasks.push(
            this._runWorker<TInput, TOutput>(input, semaphore, output, signal, () => {
              if (stopOnFirstFailure) stopSubmitting = true;
            }),
          );
        }
        await Promise.all(tasks);
      } finally {
        output.complete();
      }
    })();

    for await (const result of output) yield result;
    await producer;
  }

  // ── Layer 4 · Pipeline ─────────────────────────────────────────────────

  /** Processes an async-iterable stream, yielding results as each completes. */
  async *process<TInput, TOutput>(
    inputs: AsyncIterable<TInput>,
    options?: PipelineOptions,
    signal?: AbortSignal,
  ): AsyncIterable<InvocationResult<TOutput>> {
    const maxConcurrency = options?.maxConcurrency ?? DEFAULT_PIPELINE_CONCURRENCY;
    const outputCapacity = options?.outputBufferCapacity ?? 1000;

    const output = new BoundedChannel<InvocationResult<TOutput>>(outputCapacity);
    const semaphore = new Semaphore(maxConcurrency);

    const producer = (async () => {
      const tasks: Promise<void>[] = [];
      try {
        for await (const input of inputs) {
          if (signal?.aborted) break;
          await semaphore.acquire(signal);
          tasks.push(this._runWorker<TInput, TOutput>(input, semaphore, output, signal));
        }
        await Promise.all(tasks);
      } finally {
        output.complete();
      }
    })();

    for await (const result of output) yield result;
    await producer;
  }

  /** Creates a long-running pipeline with explicit push/complete/iterate control. */
  createPipeline<TInput, TOutput>(options?: PipelineOptions): ActionfulPipeline<TInput, TOutput> {
    return new ActionfulPipeline<TInput, TOutput>(
      async (input, signal) => {
        const ticket = await this.submit(input, signal);
        try {
          const resultJson = await this._pollUntilComplete(ticket.pollUrl, signal);
          return makeResult(ticket, deserialise<TOutput>(resultJson));
        } catch (err) {
          return makeErrorResult(ticket, errorMessage(err));
        }
      },
      options,
    );
  }

  // ── Internal ────────────────────────────────────────────────────────────

  private async _pollUntilComplete(pollUrl: string, signal?: AbortSignal): Promise<string> {
    let backoff = this._initialPollInterval;

    while (true) {
      if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');

      const response = await this._fetch(pollUrl, { signal });
      await ensureSuccess(response);

      if (response.status === 200) return response.text();

      // 202 — still running. Retry-After is the server's instruction, not a suggestion: it knows what
      // this endpoint costs, and it outranks maxPollInterval, which bounds only our own backoff.
      const retryAfter = response.headers.get('Retry-After');
      const serverMs = retryAfter ? parseInt(retryAfter) * 1000 : 0;

      await delay(applyJitter(Math.max(backoff, serverMs)), signal);

      backoff = Math.min(backoff * 2, this._maxPollInterval);
    }
  }

  private async _runWorker<TInput, TOutput>(
    input: TInput,
    semaphore: Semaphore,
    output: BoundedChannel<InvocationResult<TOutput>>,
    signal: AbortSignal | undefined,
    onFailure?: () => void,
  ): Promise<void> {
    let ticket: InvocationTicket | undefined;
    try {
      ticket = await this.submit(input, signal);
      const resultJson = await this._pollUntilComplete(ticket.pollUrl, signal);
      await output.push(makeResult(ticket, deserialise<TOutput>(resultJson)));
    } catch (err) {
      if (isAbort(err)) throw err;
      const t = ticket ?? { jobId: 'unknown', pollUrl: '', submittedAt: new Date() };
      await output.push(makeErrorResult(t, errorMessage(err)));
      onFailure?.();
    } finally {
      semaphore.release();
    }
  }

  private _fetch(url: string, init?: RequestInit): Promise<Response> {
    return fetch(url, {
      ...init,
      headers: { Authorization: this._authHeader, ...init?.headers },
    });
  }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function buildBasicAuth(token: string, secret: string): string {
  const encoded = btoa(`${token}:${secret}`);
  return `Basic ${encoded}`;
}

function serialise(input: unknown): string {
  return typeof input === 'string' ? input : JSON.stringify(input);
}

function deserialise<T>(json: string): T {
  try {
    return JSON.parse(json) as T;
  } catch {
    return json as unknown as T;
  }
}

function parseTicket(response: Response): InvocationTicket {
  const location = response.headers.get('Location');
  if (!location) throw new ActionfulError(response.status, '202 response is missing the Location header');
  const pollUrl = location.startsWith('http') ? location : new URL(location, 'https://placeholder').toString();
  return {
    jobId: extractJobId(pollUrl),
    pollUrl,
    submittedAt: new Date(),
  };
}

function extractJobId(url: string): string {
  const trimmed = url.replace(/\/$/, '');
  return trimmed.substring(trimmed.lastIndexOf('/') + 1);
}

function applyJitter(ms: number): number {
  return ms * (1 + (Math.random() - 0.5) * JITTER_RATIO);
}

async function ensureSuccess(response: Response): Promise<void> {
  if (response.ok || response.status === 202) return;
  const body = await response.text();
  const retryAfter =
    response.status === 429
      ? (parseInt(response.headers.get('Retry-After') ?? '0') * 1000 || null)
      : null;
  throw new ActionfulError(response.status, body || null, retryAfter);
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      reject(new DOMException('Aborted', 'AbortError'));
    }, { once: true });
  });
}

function isAbort(err: unknown): boolean {
  return err instanceof Error && err.name === 'AbortError';
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
