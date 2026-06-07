# mquark-actionful-client

Official Python client for invoking [mQuark Actionful](https://mquark.com) endpoints.

See the full documentation and examples at [github.com/m-quark/ActionfulClient](https://github.com/m-quark/ActionfulClient).

## Installation

```sh
pip install mquark-actionful-client
```

Requires Python 3.10+ and `httpx`.

## Quick start

```python
from mquark_actionful import ActionfulClient, ActionfulClientOptions

client = ActionfulClient(ActionfulClientOptions(
    endpoint_url="https://...",
    access_token="...",
    access_secret="...",
))

# Single invocation
result = await client.invoke({"orderId": 42})

# Batch
results = await client.invoke_batch([item1, item2, item3])

# Stream
async for r in client.stream_batch(items):
    if r.is_success:
        print(r.output)

# Async pipeline
async for result in client.process(async_item_stream()):
    await sink.write(result)

# Close when done
await client.aclose()
# or use as context manager: async with ActionfulClient(...) as client: ...
```

## License

MIT
