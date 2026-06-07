import asyncio
import httpx
import respx

from .conftest import ENDPOINT, JOB_URL, make_client, resp_200, resp_202, resp_error


async def test_invoke_batch_all_succeed():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=resp_200('{"score": 0.5}'))
        async with make_client() as client:
            results = await client.invoke_batch([{"id": 1}, {"id": 2}, {"id": 3}])

    assert len(results) == 3
    assert all(r.is_success for r in results)
    assert all(r.output == {"score": 0.5} for r in results)


async def test_invoke_batch_partial_failure():
    call_count = 0

    def post_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal call_count
        call_count += 1
        if call_count % 2 == 0:
            return resp_error(500, "forced error")
        return resp_202("https://api.test/jobs/ok")

    with respx.mock:
        respx.post(ENDPOINT).mock(side_effect=post_side_effect)
        respx.get("https://api.test/jobs/ok").mock(return_value=resp_200('{"score": 0.5}'))
        async with make_client() as client:
            results = await client.invoke_batch([{"id": i} for i in range(4)])

    assert len(results) == 4
    successes = [r for r in results if r.is_success]
    failures = [r for r in results if not r.is_success]
    assert len(successes) > 0
    assert len(failures) > 0


async def test_stream_batch_yields_as_complete():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=resp_200('{"score": 0.3}'))
        async with make_client() as client:
            received = []
            async for r in client.stream_batch([{"id": 1}, {"id": 2}]):
                received.append(r)

    assert len(received) == 2
    assert all(r.is_success for r in received)


async def test_stream_batch_stop_on_first_failure():
    from mquark_actionful import BatchOptions

    call_count = 0

    def post_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return resp_error(500, "first failure")
        return resp_202()

    with respx.mock:
        respx.post(ENDPOINT).mock(side_effect=post_side_effect)
        respx.get(JOB_URL).mock(return_value=resp_200())
        opts = BatchOptions(max_concurrency=1, stop_on_first_failure=True)
        async with make_client() as client:
            results = []
            async for r in client.stream_batch([{"id": i} for i in range(50)], opts):
                results.append(r)

    assert len(results) < 50


async def test_batch_respects_max_concurrency():
    """No more than MaxConcurrency requests should be in flight at the same time."""
    from mquark_actionful import BatchOptions

    in_flight = 0
    max_seen = 0

    async def post_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal in_flight, max_seen
        in_flight += 1
        max_seen = max(max_seen, in_flight)
        await asyncio.sleep(0.01)
        in_flight -= 1
        return resp_202()

    with respx.mock:
        respx.post(ENDPOINT).mock(side_effect=post_side_effect)
        respx.get(JOB_URL).mock(return_value=resp_200())
        opts = BatchOptions(max_concurrency=3)
        async with make_client() as client:
            await client.invoke_batch([{"id": i} for i in range(10)], opts)

    assert max_seen <= 3
