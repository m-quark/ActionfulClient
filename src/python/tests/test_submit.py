import httpx
import pytest
import respx

from mquark_actionful import ActionfulError, InvocationStatus

from .conftest import ENDPOINT, JOB_URL, make_client, resp_200, resp_202, resp_error


async def test_submit_returns_ticket():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        async with make_client() as client:
            ticket = await client.submit({"id": 1})

    assert ticket.job_id == "abc123"
    assert ticket.poll_url == JOB_URL


async def test_submit_serialises_dict():
    with respx.mock:
        route = respx.post(ENDPOINT).mock(return_value=resp_202())
        async with make_client() as client:
            await client.submit({"order": 42})

    assert route.called
    import json
    assert json.loads(route.calls[0].request.content) == {"order": 42}


async def test_submit_passes_raw_json_string():
    with respx.mock:
        route = respx.post(ENDPOINT).mock(return_value=resp_202())
        async with make_client() as client:
            await client.submit('{"raw": true}')

    body = route.calls[0].request.content
    assert b'"raw": true' in body


async def test_get_job_running_on_202():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=httpx.Response(202, headers={"Retry-After": "0"}))
        async with make_client() as client:
            ticket = await client.submit({"id": 1})
            job = await client.get_job(ticket)

    assert job.status == InvocationStatus.RUNNING
    assert not job.is_terminal


async def test_get_job_succeeded_on_200():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_202())
        respx.get(JOB_URL).mock(return_value=resp_200('{"score": 0.7}'))
        async with make_client() as client:
            ticket = await client.submit({"id": 1})
            job = await client.get_job(ticket)

    assert job.status == InvocationStatus.SUCCEEDED
    assert job.result_json == '{"score": 0.7}'
    assert job.is_terminal


async def test_get_job_accepts_poll_url_string():
    with respx.mock:
        respx.get(JOB_URL).mock(return_value=resp_200())
        async with make_client() as client:
            job = await client.get_job(JOB_URL)

    assert job.status == InvocationStatus.SUCCEEDED


async def test_submit_raises_on_4xx():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=resp_error(400, "bad input"))
        async with make_client() as client:
            with pytest.raises(ActionfulError) as exc_info:
                await client.submit({"id": 1})

    assert exc_info.value.status_code == 400
    assert exc_info.value.body == "bad input"


async def test_submit_raises_with_retry_after_on_429():
    with respx.mock:
        respx.post(ENDPOINT).mock(return_value=httpx.Response(
            429, text="rate limited", headers={"Retry-After": "30"}
        ))
        async with make_client() as client:
            with pytest.raises(ActionfulError) as exc_info:
                await client.submit({"id": 1})

    assert exc_info.value.status_code == 429
    assert exc_info.value.retry_after == 30


async def test_missing_endpoint_url_raises():
    with pytest.raises(ValueError, match="endpoint_url"):
        from mquark_actionful import ActionfulClientOptions
        ActionfulClientOptions(endpoint_url="", access_token="t", access_secret="s")


async def test_submit_negotiates_no_server_side_wait():
    """The server holds no connection and reads no wait preference; asking for one advertises a
    contract that does not exist. See docs/design/actionful-client-sdk.md."""
    with respx.mock:
        route = respx.post(ENDPOINT).mock(return_value=resp_202())

        async with make_client() as client:
            await client.submit({"a": 1})

        headers = route.calls[0].request.headers
        assert "mq-timeout-seconds" not in headers
        assert "prefer" not in headers
