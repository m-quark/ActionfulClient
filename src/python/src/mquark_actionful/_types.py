from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Generic, TypeVar

T = TypeVar("T")


class InvocationStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"

    @property
    def is_terminal(self) -> bool:
        return self in (self.SUCCEEDED, self.FAILED, self.CANCELLED)


@dataclass(frozen=True)
class InvocationTicket:
    job_id: str
    poll_url: str
    submitted_at: datetime


@dataclass(frozen=True)
class InvocationJob:
    job_id: str
    status: InvocationStatus
    result_json: str | None = None
    error: str | None = None

    @property
    def is_terminal(self) -> bool:
        return self.status.is_terminal


@dataclass(frozen=True)
class InvocationResult(Generic[T]):
    ticket: InvocationTicket
    output: T | None
    error: str | None

    @property
    def is_success(self) -> bool:
        return self.error is None


class ActionfulError(Exception):
    """Raised for non-2xx HTTP responses."""

    def __init__(self, status_code: int, body: str, retry_after: int | None = None) -> None:
        super().__init__(f"HTTP {status_code}: {body}")
        self.status_code = status_code
        self.body = body
        self.retry_after = retry_after  # seconds; populated on 429


@dataclass
class ActionfulClientOptions:
    """Configuration for ActionfulClient."""

    endpoint_url: str
    access_token: str
    access_secret: str
    # Wait before the first poll of a job that is still running, in seconds. Subsequent waits double
    # up to max_poll_interval, with jitter.
    initial_poll_interval: float = 0.25
    # Ceiling on the wait between polls. A Retry-After from the server outranks it.
    max_poll_interval: float = 5.0

    def __post_init__(self) -> None:
        if not self.endpoint_url:
            raise ValueError("endpoint_url is required")
        if not self.access_token:
            raise ValueError("access_token is required")
        if not self.access_secret:
            raise ValueError("access_secret is required")


@dataclass
class BatchOptions:
    max_concurrency: int = 10
    stop_on_first_failure: bool = False
    output_buffer_capacity: int = 1000


@dataclass
class PipelineOptions:
    max_concurrency: int = 10
    input_buffer_capacity: int = 1000
    output_buffer_capacity: int = 1000
