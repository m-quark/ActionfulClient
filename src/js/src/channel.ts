const DONE = Symbol('DONE');

/**
 * Bounded async channel. Push blocks when full; pop blocks when empty.
 * Backpressure flows naturally: a slow consumer prevents new submissions.
 */
export class BoundedChannel<T> implements AsyncIterable<T> {
  private readonly _queue: T[] = [];
  private readonly _pushWaiters: Array<() => void> = [];
  private readonly _popWaiters: Array<(val: T | typeof DONE) => void> = [];
  private _done = false;

  constructor(private readonly _capacity: number) {}

  push(item: T, signal?: AbortSignal): Promise<void> {
    if (this._done) return Promise.reject(new Error('Channel is already complete'));

    // Deliver directly to a waiting consumer
    const popWaiter = this._popWaiters.shift();
    if (popWaiter) {
      popWaiter(item);
      return Promise.resolve();
    }

    // Buffer if space available
    if (this._queue.length < this._capacity) {
      this._queue.push(item);
      return Promise.resolve();
    }

    // Back-pressure: wait for a consumer to free space
    return new Promise<void>((resolve, reject) => {
      const waiter = () => {
        this._queue.push(item);
        resolve();
      };
      this._pushWaiters.push(waiter);
      signal?.addEventListener('abort', () => {
        const idx = this._pushWaiters.indexOf(waiter);
        if (idx >= 0) this._pushWaiters.splice(idx, 1);
        reject(new DOMException('Aborted', 'AbortError'));
      }, { once: true });
    });
  }

  pop(): Promise<T | typeof DONE> {
    if (this._queue.length > 0) {
      const item = this._queue.shift()!;
      // Free space: wake a blocked producer
      this._pushWaiters.shift()?.();
      return Promise.resolve(item);
    }
    if (this._done) return Promise.resolve(DONE);
    return new Promise<T | typeof DONE>(resolve => this._popWaiters.push(resolve));
  }

  complete(): void {
    this._done = true;
    for (const w of this._popWaiters) w(DONE);
    this._popWaiters.length = 0;
  }

  async *[Symbol.asyncIterator](): AsyncGenerator<T> {
    while (true) {
      const item = await this.pop();
      if (item === DONE) return;
      yield item as T;
    }
  }
}
