using System.Net;
using Actionful.Client.Tests.Helpers;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class PollBackoffTests
{
    private const string PollUrl = "https://edge.mquark.test/api/workflows/org/space/ep/job-1";

    private static void RespondWithAccepted(MockHttpMessageHandler mock, string url) =>
        mock.When(HttpMethod.Post, url).Respond(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Location = new Uri(PollUrl);
            return r;
        });

    [Fact]
    public async Task Poll_Waits_Grow_Between_Attempts()
    {
        var options = new ActionfulClientOptions
        {
            EndpointUrl = MockClientFactory.DefaultOptions.EndpointUrl,
            AccessToken = "t",
            AccessSecret = "s",
            InitialPollInterval = TimeSpan.FromMilliseconds(50),
            MaxPollInterval = TimeSpan.FromSeconds(5),
        };
        var (client, mock) = MockClientFactory.Create(options);
        RespondWithAccepted(mock, options.EndpointUrl);

        var timestamps = new List<DateTimeOffset>();
        mock.When(HttpMethod.Get, PollUrl).Respond(_ =>
        {
            timestamps.Add(DateTimeOffset.UtcNow);
            return timestamps.Count < 4
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        });

        await client.InvokeAsync("{}");

        // 50ms → 100ms → 200ms, jittered by ±10%; assert growth rather than exact values.
        var first = timestamps[1] - timestamps[0];
        var third = timestamps[3] - timestamps[2];
        Assert.True(third > first, $"expected backoff growth, got {first.TotalMilliseconds}ms then {third.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Poll_Starts_Well_Below_The_Old_Fixed_Interval()
    {
        var options = new ActionfulClientOptions
        {
            EndpointUrl = MockClientFactory.DefaultOptions.EndpointUrl,
            AccessToken = "t",
            AccessSecret = "s",
        };
        var (client, mock) = MockClientFactory.Create(options);
        RespondWithAccepted(mock, options.EndpointUrl);

        var started = DateTimeOffset.UtcNow;
        var polls = 0;
        mock.When(HttpMethod.Get, PollUrl).Respond(_ =>
            ++polls < 2
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") });

        await client.InvokeAsync("{}");

        // The pre-1.1 client waited a flat 2s before its second poll.
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RetryAfter_Outranks_MaxPollInterval()
    {
        var options = new ActionfulClientOptions
        {
            EndpointUrl = MockClientFactory.DefaultOptions.EndpointUrl,
            AccessToken = "t",
            AccessSecret = "s",
            InitialPollInterval = TimeSpan.FromMilliseconds(10),
            MaxPollInterval = TimeSpan.FromMilliseconds(20),
        };
        var (client, mock) = MockClientFactory.Create(options);
        RespondWithAccepted(mock, options.EndpointUrl);

        var timestamps = new List<DateTimeOffset>();
        mock.When(HttpMethod.Get, PollUrl).Respond(_ =>
        {
            timestamps.Add(DateTimeOffset.UtcNow);
            if (timestamps.Count >= 2)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };

            var r = new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Add("Retry-After", "1");
            return r;
        });

        await client.InvokeAsync("{}");

        // A server asking for a 1s pause must not be overridden by a 20ms client ceiling.
        Assert.True((timestamps[1] - timestamps[0]).TotalMilliseconds >= 900);
    }

    [Fact]
    public async Task InvokeAsync_Sends_PreferredWait_As_Prefer_Header()
    {
        var options = new ActionfulClientOptions
        {
            EndpointUrl = MockClientFactory.DefaultOptions.EndpointUrl,
            AccessToken = "t",
            AccessSecret = "s",
            PreferredWait = TimeSpan.FromSeconds(30),
        };
        var (client, mock) = MockClientFactory.Create(options);

        string? prefer = null;
        mock.When(HttpMethod.Post, options.EndpointUrl).Respond(req =>
        {
            prefer = req.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        });

        await client.InvokeAsync("{}");

        Assert.Equal("wait=30", prefer);
    }

    [Fact]
    public async Task InvokeAsync_Omits_Prefer_When_No_Preference_Is_Configured()
    {
        var (client, mock) = MockClientFactory.Create();

        var sentPrefer = true;
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl).Respond(req =>
        {
            sentPrefer = req.Headers.Contains("Prefer");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        });

        await client.InvokeAsync("{}");

        Assert.False(sentPrefer);
    }

    [Fact]
    public async Task SubmitAsync_Asks_For_An_Immediate_202()
    {
        var (client, mock) = MockClientFactory.Create();

        string? prefer = null;
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl).Respond(req =>
        {
            prefer = req.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null;
            var r = new HttpResponseMessage(HttpStatusCode.Accepted);
            r.Headers.Location = new Uri(PollUrl);
            return r;
        });

        await client.SubmitAsync("{}");

        Assert.Equal("wait=0", prefer);
    }
}
