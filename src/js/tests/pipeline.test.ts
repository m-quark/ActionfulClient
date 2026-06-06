import { afterEach, describe, expect, it, vi } from 'vitest';
import { ENDPOINT, makeClient, mockFetch, response200, response202 } from './helpers.js';

afterEach(() => vi.unstubAllGlobals());

function setupEndpoint(resultFn: (id: number) => string) {
  let counter = 0;
  mockFetch(url => {
    if (!url.includes('/job-')) {
      const id = ++counter;
      return response202(`job-${id}`);
    }
    const id = parseInt(url.split('/job-')[1]);
    return response200(resultFn(id));
  });
}

describe('process', () => {
  it('processes all items from an async iterable', async () => {
    setupEndpoint(id => String(id));

    async function* source() {
      for (const i of [1, 2, 3, 4, 5]) yield i;
    }

    const results = [];
    for await (const r of makeClient().process<number, number>(source())) results.push(r);
    expect(results).toHaveLength(5);
    expect(results.every(r => r.isSuccess)).toBe(true);
  });

  it('respects maxConcurrency', async () => {
    let inFlight = 0;
    let maxObserved = 0;
    mockFetch(async url => {
      if (!url.includes('/job-')) {
        inFlight++;
        maxObserved = Math.max(maxObserved, inFlight);
        await new Promise(r => setTimeout(r, 20));
        inFlight--;
        return response202('job-x');
      }
      return response200('ok');
    });

    async function* source() {
      for (const i of Array.from({ length: 12 }, (_, i) => i)) yield i;
    }

    for await (const _ of makeClient().process<number, string>(source(), { maxConcurrency: 4 })) { }
    expect(maxObserved).toBeLessThanOrEqual(4);
  });
});

describe('createPipeline', () => {
  it('processes inputs written via push and yields results', async () => {
    setupEndpoint(() => '42');
    const client = makeClient();
    const pipeline = client.createPipeline<number, number>();

    const producer = (async () => {
      for (const i of [1, 2, 3]) await pipeline.push(i);
      pipeline.complete();
    })();

    const results = [];
    for await (const r of pipeline) results.push(r);
    await producer;

    expect(results).toHaveLength(3);
    expect(results.every(r => r.output === 42)).toBe(true);
  });

  it('drains in-flight jobs after complete()', async () => {
    mockFetch(async url => {
      if (!url.includes('/job-')) return response202('job-drain');
      await new Promise(r => setTimeout(r, 40));
      return response200('"finished"');
    });

    const pipeline = makeClient().createPipeline<number, string>();
    await pipeline.push(1);
    pipeline.complete();

    const results = [];
    for await (const r of pipeline) results.push(r);

    expect(results).toHaveLength(1);
    expect(results[0].output).toBe('finished');
  });

  it('respects inputBufferCapacity as backpressure', async () => {
    setupEndpoint(() => '1');
    const pipeline = makeClient().createPipeline<number, number>({
      inputBufferCapacity: 2,
      maxConcurrency: 1,
    });

    // Fill the buffer then push one more — should complete without deadlock
    const pushes = [pipeline.push(1), pipeline.push(2), pipeline.push(3)];
    pipeline.complete();

    const results = [];
    for await (const r of pipeline) results.push(r);
    await Promise.all(pushes);

    expect(results).toHaveLength(3);
  });
});
