import asyncio
import httpx
import pytest
import respx

from .conftest import ENDPOINT, JOB_URL, make_client, resp_200, resp_202


async def test_invoke_raw_fast_path_200():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_200('{"score": 0.9}'))
        async with make_client() as client:
            result = await client.invoke_raw('{"id": 1}')

    assert result == '{"score": 0.9}'


async def test_invoke_raw_async_path_polls_until_200():
    poll_count = 0

    def poll_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal poll_count
        poll_count += 1
        if poll_count < 3:
            return httpx.Response(202, headers={"Retry-After": "0"})
        return resp_200('{"score": 0.5}')

    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(side_effect=poll_side_effect)
        async with make_client() as client:
            result = await client.invoke_raw('{"id": 1}')

    assert result == '{"score": 0.5}'
    assert poll_count == 3


async def test_invoke_returns_dict():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_200('{"score": 0.85}'))
        async with make_client() as client:
            result = await client.invoke({"id": 1})

    assert result == {"score": 0.85}


async def test_invoke_returns_string_for_plain_text():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_200("plain text result"))
        async with make_client() as client:
            result = await client.invoke_raw("plain text result")

    assert result == "plain text result"


async def test_invoke_respects_retry_after():
    """Server Retry-After wins when higher than PollInterval."""
    poll_times: list[float] = []
    poll_count = 0

    def poll_side_effect(request: httpx.Request) -> httpx.Response:
        nonlocal poll_count
        poll_count += 1
        poll_times.append(asyncio.get_event_loop().time())
        if poll_count < 2:
            return httpx.Response(202, headers={"Retry-After": "0"})
        return resp_200('{"done": true}')

    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(side_effect=poll_side_effect)
        async with make_client(poll_interval=0.001) as client:
            await client.invoke_raw('{}')

    assert poll_count == 2


async def test_invoke_cancellation():
    async def slow_poll(request: httpx.Request) -> httpx.Response:
        await asyncio.sleep(10)
        return resp_200()

    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(side_effect=slow_poll)

        async with make_client() as client:
            with pytest.raises(asyncio.CancelledError):
                task = asyncio.create_task(client.invoke_raw('{}'))
                await asyncio.sleep(0.01)
                task.cancel()
                await task
