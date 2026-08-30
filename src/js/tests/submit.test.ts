import { afterEach, describe, expect, it, vi } from 'vitest';
import { ActionfulError } from '../src/errors.js';
import { ENDPOINT, makeClient, mockFetch, response200, response202, response4xx } from './helpers.js';

afterEach(() => vi.unstubAllGlobals());

describe('submit', () => {
  it('returns ticket with jobId and pollUrl on 202', async () => {
    mockFetch(() => response202('job-123'));
    const client = makeClient();
    const ticket = await client.submit('{"x":1}');
    expect(ticket.jobId).toBe('job-123');
    expect(ticket.pollUrl).toBe(`${ENDPOINT}/job-123`);
    expect(ticket.submittedAt).toBeInstanceOf(Date);
  });

  it('serialises object input to JSON', async () => {
    const fetch = mockFetch(() => response202('job-456'));
    await makeClient().submit({ amount: 99 });
    const [, init] = fetch.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ amount: 99 });
  });

  it('negotiates no server-side wait', async () => {
    // The server holds no connection and reads no wait preference; asking for one advertises a
    // contract that does not exist. See docs/design/actionful-client-sdk.md.
    const fetch = mockFetch(() => response202('job-x'));
    await makeClient().submit('{}');
    const [, init] = fetch.mock.calls[0] as [string, RequestInit];
    const headers = init.headers as Record<string, string>;
    expect(headers['Mq-Timeout-Seconds']).toBeUndefined();
    expect(headers['Prefer']).toBeUndefined();
  });

  it('attaches Basic Auth header', async () => {
    const fetch = mockFetch(() => response202('job-x'));
    await makeClient().submit('{}');
    const [, init] = fetch.mock.calls[0] as [string, RequestInit];
    const expected = 'Basic ' + btoa('test-token:test-secret');
    expect((init.headers as Record<string, string>)['Authorization']).toBe(expected);
  });

  it('throws ActionfulError on 400', async () => {
    mockFetch(() => response4xx(400, 'bad payload'));
    const err = await makeClient().submit('{}').catch(e => e);
    expect(err).toBeInstanceOf(ActionfulError);
    expect(err.statusCode).toBe(400);
    expect(err.responseBody).toBe('bad payload');
  });

  it('throws ActionfulError with retryAfter on 429', async () => {
    mockFetch(() => new Response('slow down', {
      status: 429,
      headers: { 'Retry-After': '30' },
    }));
    const err = await makeClient().submit('{}').catch(e => e);
    expect(err).toBeInstanceOf(ActionfulError);
    expect(err.retryAfter).toBe(30_000);
  });
});

describe('getJob', () => {
  it('returns running status on 202', async () => {
    const pollUrl = `${ENDPOINT}/job-789`;
    mockFetch(() => new Response(null, { status: 202 }));
    const job = await makeClient().getJob(pollUrl);
    expect(job.status).toBe('running');
    expect(job.resultJson).toBeNull();
    expect(job.isTerminal).toBe(false);
  });

  it('returns succeeded status with result on 200', async () => {
    const pollUrl = `${ENDPOINT}/job-done`;
    mockFetch(() => response200('{"score":0.9}'));
    const job = await makeClient().getJob(pollUrl);
    expect(job.status).toBe('succeeded');
    expect(job.resultJson).toBe('{"score":0.9}');
    expect(job.isTerminal).toBe(true);
  });

  it('accepts InvocationTicket as first argument', async () => {
    const fetch = mockFetch(() => response200('ok'));
    const ticket = { jobId: 'abc', pollUrl: `${ENDPOINT}/abc`, submittedAt: new Date() };
    await makeClient().getJob(ticket);
    expect((fetch.mock.calls[0] as [string])[0]).toBe(ticket.pollUrl);
  });
});
