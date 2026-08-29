import { afterEach, describe, expect, it, vi } from 'vitest';
import { ActionfulError } from '../src/errors.js';
import { ENDPOINT, makeClient, mockFetch, response200, response202, response4xx } from './helpers.js';

afterEach(() => vi.unstubAllGlobals());

describe('invokeRaw', () => {
  it('returns body directly on 200 fast path', async () => {
    mockFetch(() => response200('{"score":0.2}'));
    const result = await makeClient().invokeRaw('{"id":1}');
    expect(result).toBe('{"score":0.2}');
  });

  it('polls and returns result on 202 path', async () => {
    let pollCount = 0;
    mockFetch(url => {
      if (!url.includes('/job-')) return response202('job-abc');
      pollCount++;
      return pollCount < 3
        ? new Response(null, { status: 202 })
        : response200('{"score":0.8}');
    });
    const result = await makeClient().invokeRaw('{}');
    expect(pollCount).toBe(3);
    expect(result).toBe('{"score":0.8}');
  });

  it('respects Retry-After on poll — waits longer than the poll interval', async () => {
    const timestamps: number[] = [];
    mockFetch(url => {
      if (!url.includes('/job-')) return response202('job-delay');
      timestamps.push(Date.now());
      if (timestamps.length === 1)
        return new Response(null, { status: 202, headers: { 'Retry-After': '1' } });
      return response200('done');
    });
    await makeClient().invokeRaw('{}');
    expect(timestamps.length).toBeGreaterThanOrEqual(2);
    expect(timestamps[1] - timestamps[0]).toBeGreaterThanOrEqual(900);
  });

  it('throws on 4xx', async () => {
    mockFetch(() => response4xx(400, 'bad input'));
    const err = await makeClient().invokeRaw('{}').catch(e => e);
    expect(err).toBeInstanceOf(ActionfulError);
    expect(err.statusCode).toBe(400);
  });

  it('aborts polling when signal fires', async () => {
    mockFetch(url => url.includes('/job-')
      ? new Response(null, { status: 202 })
      : response202('job-cancel'));

    const controller = new AbortController();
    setTimeout(() => controller.abort(), 100);
    const err = await makeClient().invokeRaw('{}', controller.signal).catch(e => e);
    expect(err?.name).toBe('AbortError');
  });
});

describe('invoke (typed)', () => {
  it('deserialises JSON output to typed object', async () => {
    mockFetch(() => response200('{"score":0.55,"label":"medium"}'));
    const result = await makeClient().invoke<{ id: number }, { score: number; label: string }>(
      { id: 1 },
    );
    expect(result.score).toBe(0.55);
    expect(result.label).toBe('medium');
  });

  it('returns raw string when output is plain text', async () => {
    mockFetch(() => response200('plain text result'));
    const result = await makeClient().invoke<string, string>('{}');
    expect(result).toBe('plain text result');
  });
});
