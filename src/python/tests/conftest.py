import httpx
import pytest

from mquark_actionful import ActionfulClient, ActionfulClientOptions

ENDPOINT = "https://api.test/endpoint"
JOB_URL = "https://api.test/jobs/abc123"


def make_client(
    initial_poll_interval: float = 0.001,
    max_poll_interval: float = 0.01,
) -> ActionfulClient:
    return ActionfulClient(
        ActionfulClientOptions(
            endpoint_url=ENDPOINT,
            access_token="tok",
            access_secret="sec",
            initial_poll_interval=initial_poll_interval,
            max_poll_interval=max_poll_interval,
        )
    )


def resp_202(job_url: str = JOB_URL) -> httpx.Response:
    return httpx.Response(202, headers={"Location": job_url})


def resp_200(body: str = '{"score": 0.9}') -> httpx.Response:
    return httpx.Response(200, text=body)


def resp_error(status: int = 500, body: str = "internal error") -> httpx.Response:
    return httpx.Response(status, text=body)
