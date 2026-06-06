using System.Net;
using System.Threading.Channels;
using Actionful.Client.Tests.Helpers;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class PipelineTests
{
    private static void SetupEndpoint(MockHttpMessageHandler mock, Func<int, string> resultFactory)
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
            .Respond(req =>
            {
                var id = int.Parse(req.RequestUri!.Segments.Last().Replace("job-", ""));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resultFactory(id))
                };
            });
    }

    [Fact]
    public async Task ProcessAsync_Processes_All_Items_From_AsyncEnumerable()
    {
        var (client, mock) = MockClientFactory.Create();
        SetupEndpoint(mock, id => id.ToString());

        async IAsyncEnumerable<int> Source()
        {
            foreach (var i in Enumerable.Range(1, 5))
            {
                yield return i;
                await Task.Yield();
            }
        }

        var results = new List<InvocationResult<int>>();
        await foreach (var r in client.ProcessAsync<int, int>(Source()))
            results.Add(r);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public async Task CreatePipeline_Processes_Items_And_Exposes_Results_Via_Reader()
    {
        var (client, mock) = MockClientFactory.Create();
        SetupEndpoint(mock, _ => "42");

        await using var pipeline = client.CreatePipeline<int, int>();

        // Producer
        var producer = Task.Run(async () =>
        {
            foreach (var i in Enumerable.Range(1, 4))
                await pipeline.Writer.WriteAsync(i);
            pipeline.Writer.Complete();
        });

        // Consumer
        var results = new List<InvocationResult<int>>();
        await foreach (var r in pipeline.Reader.ReadAllAsync())
            results.Add(r);

        await producer;

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(42, r.Output));
    }

    [Fact]
    public async Task CreatePipeline_Dispose_Drains_InFlight_Jobs()
    {
        var (client, mock) = MockClientFactory.Create();
        var endpointUrl = MockClientFactory.DefaultOptions.EndpointUrl;

        mock.When(HttpMethod.Post, endpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri($"{endpointUrl}/job-drain");
                return r;
            });

        mock.When(HttpMethod.Get, $"{endpointUrl}/job-drain")
            .Respond(async _ =>
            {
                await Task.Delay(50);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("done")
                };
            });

        var results = new List<InvocationResult<string>>();

        await using (var pipeline = client.CreatePipeline<int, string>())
        {
            await pipeline.Writer.WriteAsync(1);
            pipeline.Writer.Complete();

            await foreach (var r in pipeline.Reader.ReadAllAsync())
                results.Add(r);
        }

        Assert.Single(results);
        Assert.Equal("done", results[0].Output);
    }

    [Fact]
    public async Task ProcessAsync_Respects_MaxConcurrency()
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
                await Task.Delay(40);
                Interlocked.Decrement(ref inFlight);
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri($"{endpointUrl}/job-x");
                return r;
            });

        mock.When(HttpMethod.Get, $"{endpointUrl}/*")
            .Respond(HttpStatusCode.OK, "text/plain", "ok");

        async IAsyncEnumerable<int> Source()
        {
            foreach (var i in Enumerable.Range(1, 12)) yield return i;
        }

        var opts = new PipelineOptions { MaxConcurrency = 4 };
        await foreach (var _ in client.ProcessAsync<int, string>(Source(), opts)) { }

        Assert.True(maxObserved <= 4, $"MaxConcurrency exceeded: {maxObserved}");
    }
}
