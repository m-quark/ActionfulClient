using System.Net;
using Actionful.Client.Tests.Helpers;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class NoLegacyHeadersTests
{
    // The server holds no connection and reads no wait preference; a client that asks for one is
    // advertising a contract that does not exist. See docs/design/actionful-client-sdk.md.
    [Theory]
    [InlineData("Mq-Timeout-Seconds")]
    [InlineData("Prefer")]
    public async Task SubmitAsync_Sends_No_Wait_Negotiation_Header(string header)
    {
        var (client, mock) = MockClientFactory.Create();

        var sent = true;
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl).Respond(req =>
        {
            sent = req.Headers.Contains(header);
            var r = new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Location = new Uri("https://edge.mquark.test/api/workflows/org/space/ep/job-1");
            return r;
        });

        await client.SubmitAsync("{}");

        Assert.False(sent, $"client must not send {header}");
    }

    [Theory]
    [InlineData("Mq-Timeout-Seconds")]
    [InlineData("Prefer")]
    public async Task InvokeAsync_Sends_No_Wait_Negotiation_Header(string header)
    {
        var (client, mock) = MockClientFactory.Create();

        var sent = true;
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl).Respond(req =>
        {
            sent = req.Headers.Contains(header);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        });

        await client.InvokeAsync("{}");

        Assert.False(sent, $"client must not send {header}");
    }
}
