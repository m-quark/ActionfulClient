# MQuark.Actionful.Client

Official .NET client library for the [mQuark Actionful](https://mquark.com) REST API.

> **Status:** Planning / early development.

## Installation

```sh
dotnet add package MQuark.Actionful.Client
```

## Quick Start

```csharp
using MQuark.Actionful.Client;

var client = new ActionfulClient(new ActionfulClientOptions
{
    BaseUrl = "https://api.mquark.com",
    BearerToken = "<your-jwt-token>",
});

// List orgs
var orgs = await client.Orgs.ListAsync();

// Deploy an endpoint and wait for completion
var jobId = await client.Endpoints.DeployAsync("my-org", "my-space", "my-endpoint");
var job = await client.Jobs.WaitForJobAsync(jobId);
```

## Language Support

| Language | Package | Status |
|---|---|---|
| .NET/C# | `MQuark.Actionful.Client` (NuGet) | In development |
| JavaScript/TypeScript | `@mquark/actionful-client` (npm) | Planned |
| Python | `mquark-actionful-client` (PyPI) | Planned |
| Go | `github.com/mquark/actionful-client-go` | Planned |

## License

MIT
