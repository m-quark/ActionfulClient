import { vi } from 'vitest';
import { ActionfulClient } from '../src/client.js';

export const ENDPOINT = 'https://edge.mquark.test/api/workflows/org/space/ep';

export const DEFAULT_OPTIONS = {
  endpointUrl: ENDPOINT,
  accessToken: 'test-token',
  accessSecret: 'test-secret',
  initialPollInterval: 10,
  maxPollInterval: 50,
};

export function makeClient() {
  return new ActionfulClient(DEFAULT_OPTIONS);
}

export function mockFetch(...handlers: ((url: string, init?: RequestInit) => Response | null)[]) {
  const fn = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    for (const h of handlers) {
      const r = h(url, init);
      if (r !== null) return Promise.resolve(r);
    }
    return Promise.resolve(new Response('Not mocked', { status: 500 }));
  });
  vi.stubGlobal('fetch', fn);
  return fn;
}

export function response202(pollPath: string): Response {
  return new Response(null, {
    status: 202,
    headers: { Location: `${ENDPOINT}/${pollPath}`, 'Retry-After': '0' },
  });
}

export function response200(body: string): Response {
  return new Response(body, { status: 200 });
}

export function response4xx(status: number, body: string): Response {
  return new Response(body, { status });
}
