using System.Net;
using Actionful.Client.Tests.Helpers;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class BatchTests
{
    private static void SetupEndpoint(MockHttpMessageHandler mock, string result, int delayMs = 0)
    {
        var endpointUrl = MockClientFactory.DefaultOptions.EndpointUrl;
        var counter = 0;

        mock.When(HttpMethod.Post, endpointUrl)
            .Respond(_ =>
            {
                var id = Interlocked.Increment(ref counter);
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri($"{endpointUrl}/job-{id}");
                return r;
            });

        mock.When(HttpMethod.Get, $"{endpointUrl}/*")
            .Respond(async _ =>
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(result)
                };
            });
    }

    [Fact]
    public async Task InvokeBatchAsync_Returns_All_Results()
    {
        var (client, mock) = MockClientFactory.Create();
        SetupEndpoint(mock, """"{"done":true}"""");

        var inputs = Enumerable.Range(1, 5).Select(i => new { Id = i });
        var results = await client.InvokeBatchAsync<object, Dictionary<string, bool>>(inputs);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.All(results, r => Assert.True(r.Output!["done"]));
    }

    [Fact]
    public async Task StreamBatchAsync_Yields_Results_As_They_Complete()
    {
        var (client, mock) = MockClientFactory.Create();
        SetupEndpoint(mock, "42");

        var inputs = Enumerable.Range(1, 3).Select(i => i.ToString());
        var received = new List<InvocationResult<int>>();

        await foreach (var r in client.StreamBatchAsync<string, int>(inputs))
            received.Add(r);

        Assert.Equal(3, received.Count);
        Assert.All(received, r => Assert.Equal(42, r.Output));
    }

    [Fact]
    public async Task InvokeBatchAsync_Captures_Failures_Without_Throwing()
    {
        var (client, mock) = MockClientFactory.Create();
        var endpointUrl = MockClientFactory.DefaultOptions.EndpointUrl;
        var call = 0;

        mock.When(HttpMethod.Post, endpointUrl)
            .Respond(_ =>
            {
                var id = Interlocked.Increment(ref call);
                // Even-numbered calls fail
                if (id % 2 == 0)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("bad")
                    };
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri($"{endpointUrl}/job-{id}");
                return r;
            });

        mock.When(HttpMethod.Get, $"{endpointUrl}/*")
            .Respond(HttpStatusCode.OK, "text/plain", "1");

        var inputs = Enumerable.Range(1, 4).Select(i => i);
        var results = await client.InvokeBatchAsync<int, int>(inputs);

        Assert.Equal(4, results.Count);
        var successes = results.Count(r => r.IsSuccess);
        var failures = results.Count(r => !r.IsSuccess);
        Assert.Equal(2, successes);
        Assert.Equal(2, failures);
    }

    [Fact]
    public async Task InvokeBatchAsync_Respects_MaxConcurrency()
    {
        var (client, mock) = MockClientFactory.Create();
        var endpointUrl = MockClientFactory.DefaultOptions.EndpointUrl;
        var inFlight = 0;
        var maxObserved = 0;

        mock.When(HttpMethod.Post, endpointUrl)
            .Respond(async _ =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, current));
                await Task.Delay(50);
                Interlocked.Decrement(ref inFlight);
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri($"{endpointUrl}/job-x");
                return r;
            });

        mock.When(HttpMethod.Get, $"{endpointUrl}/*")
            .Respond(HttpStatusCode.OK, "text/plain", "ok");

        var opts = new BatchOptions { MaxConcurrency = 3 };
        var inputs = Enumerable.Range(1, 10).Select(i => i);
        await client.InvokeBatchAsync<int, string>(inputs, opts);

        Assert.True(maxObserved <= 3);
    }

    [Fact]
    public async Task StreamBatchAsync_StopOnFirstFailure_Stops_Submitting()
    {
        var (client, mock) = MockClientFactory.Create();
        var endpointUrl = MockClientFactory.DefaultOptions.EndpointUrl;
        var submitted = 0;

        mock.When(HttpMethod.Post, endpointUrl)
            .Respond(_ =>
            {
                Interlocked.Increment(ref submitted);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("fail")
                };
            });

        var opts = new BatchOptions { MaxConcurrency = 1, StopOnFirstFailure = true };
        var inputs = Enumerable.Range(1, 10).Select(i => i);
        var results = new List<InvocationResult<int>>();

        await foreach (var r in client.StreamBatchAsync<int, int>(inputs, opts))
            results.Add(r);

        Assert.True(submitted < 10, $"Expected fewer than 10 submissions but got {submitted}");
        Assert.True(results.Any(r => !r.IsSuccess));
    }
}
