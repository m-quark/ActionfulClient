/** Limits the number of concurrent async operations. */
export class Semaphore {
  private _permits: number;
  private readonly _waiting: Array<() => void> = [];

  constructor(permits: number) {
    this._permits = permits;
  }

  acquire(signal?: AbortSignal): Promise<void> {
    if (this._permits > 0) {
      this._permits--;
      return Promise.resolve();
    }
    return new Promise<void>((resolve, reject) => {
      const waiter = () => resolve();
      this._waiting.push(waiter);
      signal?.addEventListener('abort', () => {
        const idx = this._waiting.indexOf(waiter);
        if (idx >= 0) this._waiting.splice(idx, 1);
        reject(new DOMException('Aborted', 'AbortError'));
      }, { once: true });
    });
  }

  release(): void {
    const next = this._waiting.shift();
    if (next) {
      next();
    } else {
      this._permits++;
    }
  }
}
