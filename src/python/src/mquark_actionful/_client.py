from __future__ import annotations

import asyncio
import dataclasses
import json
from datetime import datetime, timezone
from typing import Any, AsyncGenerator, AsyncIterator, Iterable
from urllib.parse import urljoin, urlparse

import httpx

from ._types import (
    ActionfulClientOptions,
    ActionfulError,
    BatchOptions,
    InvocationJob,
    InvocationResult,
    InvocationStatus,
    InvocationTicket,
    PipelineOptions,
)

# Sent on submit() to force an immediate 202 (internal optimisation, not part of the public contract).
_TIMEOUT_HEADER = "Mq-Timeout-Seconds"

_SENTINEL = object()


class ActionfulClient:
    """Client for invoking a single published mQuark Actionful endpoint."""

    def __init__(
        self,
        options: ActionfulClientOptions,
        *,
        http_client: httpx.AsyncClient | None = None,
    ) -> None:
        self._endpoint_url = options.endpoint_url
        self._poll_interval = options.poll_interval
        self._http = http_client or httpx.AsyncClient(
            auth=(options.access_token, options.access_secret)
        )

    async def aclose(self) -> None:
        await self._http.aclose()

    async def __aenter__(self) -> ActionfulClient:
        return self

    async def __aexit__(self, *_: Any) -> None:
        await self.aclose()

    # ── Layer 1 · Raw async ──────────────────────────────────────────────────

    async def submit(self, input: Any) -> InvocationTicket:
        """Submits a payload and returns a ticket immediately (always 202)."""
        response = await self._http.post(
            self._endpoint_url,
            content=_serialise(input),
            headers={"Content-Type": "application/json", _TIMEOUT_HEADER: "0"},
        )
        _ensure_success(response)
        return _parse_ticket(response)

    async def get_job(self, ticket_or_url: InvocationTicket | str) -> InvocationJob:
        """Polls once and returns the job's current state."""
        poll_url = ticket_or_url if isinstance(ticket_or_url, str) else ticket_or_url.poll_url
        response = await self._http.get(poll_url)
        _ensure_success(response)

        job_id = _extract_job_id(poll_url)
        if response.status_code == 200:
            return InvocationJob(
                job_id=job_id,
                status=InvocationStatus.SUCCEEDED,
                result_json=response.text,
            )
        return InvocationJob(job_id=job_id, status=InvocationStatus.RUNNING)

    # ── Layer 2 · Invoke and wait ─────────────────────────────────────────

    async def invoke_raw(self, payload: str) -> str:
        """Submits payload, polls until completion, returns the raw result string."""
        response = await self._http.post(
            self._endpoint_url,
            content=payload,
            headers={"Content-Type": "application/json"},
        )
        _ensure_success(response)

        if response.status_code == 200:
            return response.text

        ticket = _parse_ticket(response)
        return await self._poll_until_complete(ticket.poll_url)

    async def invoke(self, input: Any) -> Any:
        """Serialises input, invokes the endpoint, returns the deserialised result (dict/list/primitive)."""
        raw = await self.invoke_raw(_serialise(input))
        return _deserialise(raw)

    # ── Layer 3 · Batch ───────────────────────────────────────────────────

    async def invoke_batch(
        self,
        inputs: Iterable[Any],
        options: BatchOptions | None = None,
    ) -> list[InvocationResult[Any]]:
        """Processes all inputs and returns results when everything completes."""
        results: list[InvocationResult[Any]] = []
        async for r in self.stream_batch(inputs, options):
            results.append(r)
        return results

    async def stream_batch(
        self,
        inputs: Iterable[Any],
        options: BatchOptions | None = None,
    ) -> AsyncGenerator[InvocationResult[Any], None]:
        """Processes inputs concurrently, yielding results as each completes."""
        opts = options or BatchOptions()
        out: asyncio.Queue[Any] = asyncio.Queue(maxsize=opts.output_buffer_capacity)
        sem = asyncio.Semaphore(opts.max_concurrency)
        stop = asyncio.Event()

        async def worker(item: Any) -> None:
            result = await self._invoke_one(item)
            if not result.is_success and opts.stop_on_first_failure:
                stop.set()
            await out.put(result)
            sem.release()

        async def producer() -> None:
            tasks: list[asyncio.Task[None]] = []
            for item in inputs:
                if stop.is_set():
                    break
                await sem.acquire()
                tasks.append(asyncio.create_task(worker(item)))
            await asyncio.gather(*tasks, return_exceptions=True)
            await out.put(_SENTINEL)

        producer_task = asyncio.create_task(producer())

        while True:
            item = await out.get()
            if item is _SENTINEL:
                break
            yield item

        await producer_task

    # ── Layer 4 · Pipeline ────────────────────────────────────────────────

    async def process(
        self,
        inputs: AsyncIterator[Any],
        options: PipelineOptions | None = None,
    ) -> AsyncGenerator[InvocationResult[Any], None]:
        """Processes an async-iterable stream, yielding results as each completes."""
        opts = options or PipelineOptions()
        out: asyncio.Queue[Any] = asyncio.Queue(maxsize=opts.output_buffer_capacity)
        sem = asyncio.Semaphore(opts.max_concurrency)

        async def worker(item: Any) -> None:
            result = await self._invoke_one(item)
            await out.put(result)
            sem.release()

        async def producer() -> None:
            tasks: list[asyncio.Task[None]] = []
            async for item in inputs:
                await sem.acquire()
                tasks.append(asyncio.create_task(worker(item)))
            await asyncio.gather(*tasks, return_exceptions=True)
            await out.put(_SENTINEL)

        producer_task = asyncio.create_task(producer())

        while True:
            item = await out.get()
            if item is _SENTINEL:
                break
            yield item

        await producer_task

    def create_pipeline(self, options: PipelineOptions | None = None) -> Pipeline:
        """Creates a long-running pipeline with explicit push/complete/iterate control."""
        return Pipeline(self._invoke_one, options)

    # ── Internal ─────────────────────────────────────────────────────────────

    async def _poll_until_complete(self, poll_url: str) -> str:
        while True:
            response = await self._http.get(poll_url)
            _ensure_success(response)

            if response.status_code == 200:
                return response.text

            await asyncio.sleep(_poll_wait(response, self._poll_interval))

    async def _invoke_one(self, item: Any) -> InvocationResult[Any]:
        ticket = InvocationTicket(job_id="unknown", poll_url="", submitted_at=datetime.now(timezone.utc))
        try:
            ticket = await self.submit(item)
            result_json = await self._poll_until_complete(ticket.poll_url)
            return InvocationResult(ticket=ticket, output=_deserialise(result_json), error=None)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            return InvocationResult(ticket=ticket, output=None, error=str(exc))


class Pipeline:
    """Long-running pipeline with explicit push/complete/iterate control.

    Create via ``client.create_pipeline()`` inside an async context. Push items
    with ``await push()``, signal end with ``complete()``, and consume results
    with ``async for``.
    """

    def __init__(
        self,
        invoke_fn: Any,
        options: PipelineOptions | None = None,
    ) -> None:
        self._invoke_fn = invoke_fn
        self._opts = options or PipelineOptions()
        self._input: asyncio.Queue[Any] | None = None
        self._output: asyncio.Queue[Any] | None = None
        self._task: asyncio.Task[None] | None = None

    def _ensure_started(self) -> None:
        if self._task is None:
            self._input = asyncio.Queue(maxsize=self._opts.input_buffer_capacity)
            self._output = asyncio.Queue(maxsize=self._opts.output_buffer_capacity)
            self._task = asyncio.create_task(self._run())

    async def push(self, item: Any) -> None:
        """Sends an item to the pipeline. Blocks when the input buffer is full."""
        self._ensure_started()
        assert self._input is not None
        await self._input.put(item)

    def complete(self) -> None:
        """Signals that no more items will be pushed. Must be called exactly once."""
        self._ensure_started()
        assert self._input is not None
        self._input.put_nowait(_SENTINEL)

    def __aiter__(self) -> Pipeline:
        return self

    async def __anext__(self) -> InvocationResult[Any]:
        self._ensure_started()
        assert self._output is not None
        result = await self._output.get()
        if result is _SENTINEL:
            raise StopAsyncIteration
        return result

    async def _run(self) -> None:
        assert self._input is not None
        assert self._output is not None
        sem = asyncio.Semaphore(self._opts.max_concurrency)
        tasks: list[asyncio.Task[None]] = []

        while True:
            item = await self._input.get()
            if item is _SENTINEL:
                break
            await sem.acquire()

            async def worker(i: Any, s: asyncio.Semaphore = sem) -> None:
                result = await self._invoke_fn(i)
                await self._output.put(result)  # type: ignore[union-attr]
                s.release()

            tasks.append(asyncio.create_task(worker(item)))

        await asyncio.gather(*tasks, return_exceptions=True)
        await self._output.put(_SENTINEL)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _serialise(input: Any) -> str:
    if isinstance(input, str):
        return input
    if dataclasses.is_dataclass(input) and not isinstance(input, type):
        return json.dumps(dataclasses.asdict(input))
    return json.dumps(input)


def _deserialise(json_str: str) -> Any:
    try:
        return json.loads(json_str)
    except json.JSONDecodeError:
        return json_str


def _parse_ticket(response: httpx.Response) -> InvocationTicket:
    location = response.headers.get("location") or response.headers.get("Location")
    if not location:
        raise ActionfulError(response.status_code, "202 response missing Location header")
    if not location.startswith("http"):
        location = urljoin(str(response.url), location)
    return InvocationTicket(
        job_id=_extract_job_id(location),
        poll_url=location,
        submitted_at=datetime.now(timezone.utc),
    )


def _extract_job_id(url: str) -> str:
    path = urlparse(url).path.rstrip("/")
    return path.rsplit("/", 1)[-1]


def _ensure_success(response: httpx.Response) -> None:
    if response.status_code in (200, 202):
        return
    retry_after: int | None = None
    if response.status_code == 429:
        ra = response.headers.get("retry-after", "")
        if ra.isdigit():
            retry_after = int(ra)
    raise ActionfulError(
        status_code=response.status_code,
        body=response.text.strip(),
        retry_after=retry_after,
    )


def _poll_wait(response: httpx.Response, floor: float) -> float:
    ra = response.headers.get("retry-after", "")
    if ra.isdigit():
        return max(floor, float(ra))
    return floor
