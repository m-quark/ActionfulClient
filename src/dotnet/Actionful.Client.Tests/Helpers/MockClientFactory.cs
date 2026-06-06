using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests.Helpers;

internal static class MockClientFactory
{
    internal static readonly ActionfulClientOptions DefaultOptions = new()
    {
        EndpointUrl = "https://edge.mquark.test/api/workflows/org/space/ep",
        AccessToken = "test-token",
        AccessSecret = "test-secret",
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    internal static (IActionfulClient client, MockHttpMessageHandler mock) Create(
        ActionfulClientOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var mock = new MockHttpMessageHandler();
        var http = mock.ToHttpClient();

        var client = new ActionfulClient(
            http,
            Options.Create(opts),
            NullLogger<ActionfulClient>.Instance);

        return (client, mock);
    }
}
