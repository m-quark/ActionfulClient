using System.Net;
using Actionful.Client.Tests.Helpers;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class InvokeTests
{
    private record Order(int Id, decimal Amount);
    private record RiskScore(double Score, string Label);

    [Fact]
    public async Task InvokeAsync_Returns_Result_On_200_Fast_Path()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(HttpStatusCode.OK, "text/plain", """{"score":0.2,"label":"low"}""");

        var result = await client.InvokeAsync("""{"id":1,"amount":10}""");

        Assert.Equal("""{"score":0.2,"label":"low"}""", result);
    }

    [Fact]
    public async Task InvokeAsync_Polls_And_Returns_Result_On_202_Path()
    {
        var (client, mock) = MockClientFactory.Create();
        const string pollUrl = "https://edge.mquark.test/api/workflows/org/space/ep/job-abc";
        var pollCount = 0;

        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(pollUrl);
                return r;
            });

        mock.When(HttpMethod.Get, pollUrl)
            .Respond(_ =>
            {
                pollCount++;
                return pollCount < 3
                    ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"score":0.8,"label":"high"}""")
                    };
            });

        var result = await client.InvokeAsync("""{"id":2,"amount":500}""");

        Assert.Equal(3, pollCount);
        Assert.Contains("high", result);
    }

    [Fact]
    public async Task InvokeAsync_Typed_Deserialises_Output()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(HttpStatusCode.OK, "application/json", """{"score":0.55,"label":"medium"}""");

        var score = await client.InvokeAsync<Order, RiskScore>(new Order(3, 200m));

        Assert.Equal(0.55, score.Score);
        Assert.Equal("medium", score.Label);
    }

    [Fact]
    public async Task InvokeAsync_Respects_Retry_After_On_Poll()
    {
        var (client, mock) = MockClientFactory.Create();
        const string pollUrl = "https://edge.mquark.test/api/workflows/org/space/ep/job-delay";
        var timestamps = new List<DateTimeOffset>();

        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(pollUrl);
                return r;
            });

        mock.When(HttpMethod.Get, pollUrl)
            .Respond(_ =>
            {
                timestamps.Add(DateTimeOffset.UtcNow);
                if (timestamps.Count < 2)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                    r.Headers.Add("Retry-After", "1"); // 1 second — above PollInterval (10ms)
                    return r;
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("done")
                };
            });

        await client.InvokeAsync("{}");

        // Gap between the two poll calls should be at least 1 second (Retry-After)
        Assert.True(timestamps.Count >= 2);
        Assert.True((timestamps[1] - timestamps[0]).TotalMilliseconds >= 900);
    }

    [Fact]
    public async Task InvokeAsync_Cancellation_Stops_Poll_Loop()
    {
        var (client, mock) = MockClientFactory.Create();
        const string pollUrl = "https://edge.mquark.test/api/workflows/org/space/ep/job-cancel";

        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri(pollUrl);
                return r;
            });

        mock.When(HttpMethod.Get, pollUrl)
            .Respond(HttpStatusCode.Accepted); // never completes

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InvokeAsync("{}", cts.Token));
    }

    [Fact]
    public async Task InvokeAsync_Throws_ActionfulException_On_Error()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(HttpStatusCode.BadRequest, "text/plain", "bad payload");

        var ex = await Assert.ThrowsAsync<ActionfulException>(
            () => client.InvokeAsync("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
