/** Thrown when the Actionful endpoint returns a non-success HTTP status. */
export class ActionfulError extends Error {
  /** HTTP status code returned by the endpoint. */
  readonly statusCode: number;
  /** Response body returned by the endpoint, if any. */
  readonly responseBody: string | null;
  /**
   * Server-requested retry delay in milliseconds.
   * Populated only on 429 Too Many Requests responses with a Retry-After header.
   */
  readonly retryAfter: number | null;

  constructor(statusCode: number, responseBody: string | null, retryAfter: number | null = null) {
    const msg = responseBody
      ? `Actionful endpoint returned ${statusCode}: ${responseBody}`
      : `Actionful endpoint returned ${statusCode}`;
    super(msg);
    this.name = 'ActionfulError';
    this.statusCode = statusCode;
    this.responseBody = responseBody;
    this.retryAfter = retryAfter;
  }
}
