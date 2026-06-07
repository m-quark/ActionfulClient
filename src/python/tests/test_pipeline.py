import asyncio
import respx

from .conftest import ENDPOINT, JOB_URL, make_client, resp_200, resp_202


async def test_process_streams_results():
    async def input_stream():
        for i in range(3):
            yield {"id": i}

    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=resp_200('{"score": 0.3}'))
        async with make_client() as client:
            results = []
            async for r in client.process(input_stream()):
                results.append(r)

    assert len(results) == 3
    assert all(r.is_success for r in results)


async def test_pipeline_push_and_iterate():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=resp_200('{"score": 0.6}'))
        async with make_client() as client:
            pipeline = client.create_pipeline()

            async def producer() -> None:
                for i in range(5):
                    await pipeline.push({"id": i})
                pipeline.complete()

            asyncio.create_task(producer())

            results = []
            async for r in pipeline:
                results.append(r)

    assert len(results) == 5
    assert all(r.is_success for r in results)
    assert all(r.output == {"score": 0.6} for r in results)


async def test_pipeline_errors_captured_per_item():
    from mquark_actionful import PipelineOptions

    call_count = 0

    import httpx

    def post_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal call_count
        call_count += 1
        if call_count % 2 == 0:
            return httpx.Response(500, text="forced error")
        return resp_202("https://api.test/jobs/ok")

    with respx.mock:
        respx.post(ENDPOINT).mock(side_effect=post_side_effect)
        respx.get("https://api.test/jobs/ok").mock(return_value=resp_200())
        async with make_client() as client:
            pipeline = client.create_pipeline(PipelineOptions(max_concurrency=1))

            async def producer() -> None:
                for i in range(4):
                    await pipeline.push({"id": i})
                pipeline.complete()

            asyncio.create_task(producer())

            results = []
            async for r in pipeline:
                results.append(r)

    assert len(results) == 4
    assert any(r.is_success for r in results)
    assert any(not r.is_success for r in results)


async def test_process_respects_max_concurrency():
    from mquark_actionful import PipelineOptions

    in_flight = 0
    max_seen = 0

    async def post_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal in_flight, max_seen
        in_flight += 1
        max_seen = max(max_seen, in_flight)
        await asyncio.sleep(0.01)
        in_flight -= 1
        return resp_202()

    import httpx

    async def input_stream():
        for i in range(10):
            yield {"id": i}

    with respx.mock:
        respx.post(ENDPOINT).mock(side_effect=post_side_effect)
        respx.get(JOB_URL).mock(return_value=resp_200())
        opts = PipelineOptions(max_concurrency=3)
        async with make_client() as client:
            async for _ in client.process(input_stream(), opts):
                pass

    assert max_seen <= 3
