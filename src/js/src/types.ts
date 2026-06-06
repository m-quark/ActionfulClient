/** Handle returned by {@link IActionfulClient.submit} representing an in-flight job. */
export interface InvocationTicket {
  /** Unique job identifier extracted from the server Location URL. */
  readonly jobId: string;
  /** Absolute URL to poll for job status. */
  readonly pollUrl: string;
  /** UTC timestamp when the job was submitted. */
  readonly submittedAt: Date;
}

/** Snapshot of a job's state returned by a single poll call. */
export interface InvocationJob {
  readonly jobId: string;
  readonly status: InvocationStatus;
  /** Raw result string. Populated when status is `succeeded`. */
  readonly resultJson: string | null;
  /** Error message. Populated when status is `failed`. */
  readonly error: string | null;
  /** True when the job has reached a terminal state. */
  readonly isTerminal: boolean;
}

export type InvocationStatus = 'pending' | 'running' | 'succeeded' | 'failed' | 'cancelled';

/** Typed result of a single invocation, as returned by batch and pipeline operations. */
export interface InvocationResult<TOutput> {
  readonly ticket: InvocationTicket;
  /** Deserialized output. Populated when {@link isSuccess} is true. */
  readonly output: TOutput | null;
  /** Error message. Populated when the invocation failed. */
  readonly error: string | null;
  readonly isSuccess: boolean;
}

/** Controls batch invocation behaviour. */
export interface BatchOptions {
  /** Max in-flight invocations. Default: 10. */
  maxConcurrency?: number;
  /**
   * When true, no further items are submitted after the first failure.
   * Already-running invocations are allowed to complete. Default: false.
   */
  stopOnFirstFailure?: boolean;
  /**
   * Capacity of the bounded output buffer. When full, workers block until
   * the consumer reads — this is the backpressure mechanism. Default: 1000.
   */
  outputBufferCapacity?: number;
}

/** Controls pipeline behaviour. */
export interface PipelineOptions {
  /** Max in-flight invocations. Default: 10. */
  maxConcurrency?: number;
  /**
   * Capacity of the bounded input channel used by {@link ActionfulPipeline}.
   * When full, `push()` blocks until the pipeline consumes an item. Default: 1000.
   */
  inputBufferCapacity?: number;
  /**
   * Capacity of the bounded output buffer. When full, workers block until
   * the consumer reads. Default: 1000.
   */
  outputBufferCapacity?: number;
}

/** Configuration for {@link ActionfulClient}. */
export interface ActionfulClientOptions {
  /** Full URL of the published Actionful endpoint (from the Web UI). */
  endpointUrl: string;
  /** Access token shown in the Actionful Web UI. */
  accessToken: string;
  /** Access secret shown in the Actionful Web UI. */
  accessSecret: string;
  /**
   * Minimum interval between poll requests in milliseconds.
   * Actual wait = max(pollInterval, Retry-After). Default: 2000.
   */
  pollInterval?: number;
}
