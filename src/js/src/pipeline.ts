import { BoundedChannel } from './channel.js';
import { Semaphore } from './semaphore.js';
import type { InvocationResult, InvocationTicket, PipelineOptions } from './types.js';

export type PipelineWorker<TInput, TOutput> = (
  input: TInput,
  signal?: AbortSignal,
) => Promise<InvocationResult<TOutput>>;

const DEFAULT: Required<PipelineOptions> = {
  maxConcurrency: 10,
  inputBufferCapacity: 1000,
  outputBufferCapacity: 1000,
};

/**
 * Long-running processing pipeline. Write inputs via {@link push} / {@link complete},
 * consume results via async iteration.
 *
 * @example
 * ```ts
 * const pipeline = client.createPipeline<Order, RiskScore>();
 * // producer
 * await pipeline.push(order);
 * pipeline.complete();
 * // consumer
 * for await (const result of pipeline) { ... }
 * ```
 */
export class ActionfulPipeline<TInput, TOutput> implements AsyncIterable<InvocationResult<TOutput>> {
  private readonly _input: BoundedChannel<TInput>;
  private readonly _output: BoundedChannel<InvocationResult<TOutput>>;
  private readonly _opts: Required<PipelineOptions>;
  private readonly _running: Promise<void>;
  private _inputDone = false;

  constructor(worker: PipelineWorker<TInput, TOutput>, options?: PipelineOptions) {
    this._opts = { ...DEFAULT, ...options };
    this._input = new BoundedChannel<TInput>(this._opts.inputBufferCapacity);
    this._output = new BoundedChannel<InvocationResult<TOutput>>(this._opts.outputBufferCapacity);
    this._running = this._run(worker);
  }

  /** Write an input item. Awaits if the input buffer is full (backpressure). */
  push(input: TInput, signal?: AbortSignal): Promise<void> {
    return this._input.push(input, signal);
  }

  /** Signal that no more inputs will be written. */
  complete(): void {
    if (!this._inputDone) {
      this._inputDone = true;
      this._input.complete();
    }
  }

  async *[Symbol.asyncIterator](): AsyncGenerator<InvocationResult<TOutput>> {
    for await (const result of this._output) yield result;
    await this._running;
  }

  private async _run(worker: PipelineWorker<TInput, TOutput>): Promise<void> {
    const semaphore = new Semaphore(this._opts.maxConcurrency);
    const tasks: Promise<void>[] = [];
    try {
      for await (const input of this._input) {
        await semaphore.acquire();
        tasks.push(this._processOne(input, worker, semaphore));
      }
      await Promise.all(tasks);
    } finally {
      this._output.complete();
    }
  }

  private async _processOne(
    input: TInput,
    worker: PipelineWorker<TInput, TOutput>,
    semaphore: Semaphore,
  ): Promise<void> {
    try {
      const result = await worker(input);
      await this._output.push(result);
    } finally {
      semaphore.release();
    }
  }
}

// ── Helpers used by ActionfulClient ──────────────────────────────────────────

export function makeResult<TOutput>(
  ticket: InvocationTicket,
  output: TOutput,
): InvocationResult<TOutput> {
  return { ticket, output, error: null, isSuccess: true };
}

export function makeErrorResult<TOutput>(
  ticket: InvocationTicket,
  error: string,
): InvocationResult<TOutput> {
  return { ticket, output: null, error, isSuccess: false };
}
