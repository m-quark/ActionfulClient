import { afterEach, describe, expect, it, vi } from 'vitest';
import { ENDPOINT, makeClient, mockFetch, response200, response202, response4xx } from './helpers.js';

afterEach(() => vi.unstubAllGlobals());

function setupEndpoint(resultBody: string) {
  let counter = 0;
  mockFetch(url => {
    if (!url.includes('/job-')) {
      const id = ++counter;
      return response202(`job-${id}`);
    }
    return response200(resultBody);
  });
}

describe('invokeBatch', () => {
  it('returns all results', async () => {
    setupEndpoint('42');
    const results = await makeClient().invokeBatch<number, number>([1, 2, 3]);
    expect(results).toHaveLength(3);
    expect(results.every(r => r.isSuccess)).toBe(true);
    expect(results.every(r => r.output === 42)).toBe(true);
  });

  it('captures failures without throwing', async () => {
    let call = 0;
    mockFetch(url => {
      if (!url.includes('/job-')) {
        call++;
        return call % 2 === 0 ? response4xx(400, 'bad') : response202(`job-${call}`);
      }
      return response200('1');
    });

    const results = await makeClient().invokeBatch<number, number>([1, 2, 3, 4]);
    expect(results).toHaveLength(4);
    expect(results.filter(r => r.isSuccess)).toHaveLength(2);
    expect(results.filter(r => !r.isSuccess)).toHaveLength(2);
  });

  it('respects maxConcurrency', async () => {
    let inFlight = 0;
    let maxObserved = 0;
    mockFetch(async url => {
      if (!url.includes('/job-')) {
        inFlight++;
        maxObserved = Math.max(maxObserved, inFlight);
        await new Promise(r => setTimeout(r, 30));
        inFlight--;
        return response202('job-x');
      }
      return response200('ok');
    });

    await makeClient().invokeBatch<number, string>(
      Array.from({ length: 10 }, (_, i) => i),
      { maxConcurrency: 3 },
    );
    expect(maxObserved).toBeLessThanOrEqual(3);
  });
});

describe('streamBatch', () => {
  it('yields results as they complete', async () => {
    setupEndpoint('"done"');
    const received = [];
    for await (const r of makeClient().streamBatch<number, string>([1, 2, 3])) {
      received.push(r);
    }
    expect(received).toHaveLength(3);
    expect(received.every(r => r.output === 'done')).toBe(true);
  });

  it('stops submitting on first failure when stopOnFirstFailure is true', async () => {
    let submitted = 0;
    mockFetch(url => {
      if (!url.includes('/job-')) {
        submitted++;
        return response4xx(400, 'fail');
      }
      return response200('ok');
    });

    const results = [];
    for await (const r of makeClient().streamBatch<number, number>(
      Array.from({ length: 10 }, (_, i) => i),
      { maxConcurrency: 1, stopOnFirstFailure: true },
    )) {
      results.push(r);
    }
    expect(submitted).toBeLessThan(10);
    expect(results.some(r => !r.isSuccess)).toBe(true);
  });
});
