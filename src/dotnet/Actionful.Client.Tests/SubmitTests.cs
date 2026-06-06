using System.Net;
using Actionful.Client.Tests.Helpers;
using MQuark.Actionful.Client;
using RichardSzalay.MockHttp;

namespace Actionful.Client.Tests;

public class SubmitTests
{
    [Fact]
    public async Task SubmitAsync_Returns_Ticket_On_202()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri("https://edge.mquark.test/api/workflows/org/space/ep/job-123");
                r.Headers.Add("Retry-After", "5");
                return r;
            });

        var ticket = await client.SubmitAsync("""{"value":1}""");

        Assert.Equal("job-123", ticket.JobId);
        Assert.Equal("https://edge.mquark.test/api/workflows/org/space/ep/job-123", ticket.PollUrl);
    }

    [Fact]
    public async Task SubmitAsync_Serialises_TInput()
    {
        var (client, mock) = MockClientFactory.Create();
        string? capturedBody = null;
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Location = new Uri("https://edge.mquark.test/api/workflows/org/space/ep/job-456");
                return r;
            });

        await client.SubmitAsync(new { Amount = 99.5m });

        Assert.Contains("99.5", capturedBody);
    }

    [Fact]
    public async Task SubmitAsync_Throws_ActionfulException_On_4xx()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(HttpStatusCode.BadRequest, "text/plain", "invalid input");

        var ex = await Assert.ThrowsAsync<ActionfulException>(
            () => client.SubmitAsync("""{"bad":true}"""));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("invalid input", ex.ResponseBody);
    }

    [Fact]
    public async Task SubmitAsync_Throws_On_429_And_Populates_RetryAfter()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Post, MockClientFactory.DefaultOptions.EndpointUrl)
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.Add("Retry-After", "30");
                return r;
            });

        var ex = await Assert.ThrowsAsync<ActionfulException>(
            () => client.SubmitAsync("{}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task GetJobAsync_Returns_Running_On_202()
    {
        var (client, mock) = MockClientFactory.Create();
        mock.When(HttpMethod.Get, "https://edge.mquark.test/api/workflows/org/space/ep/job-789")
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Accepted);
                r.Headers.Add("Retry-After", "2");
                return r;
            });

        var ticket = new InvocationTicket(
            "job-789",
            "https://edge.mquark.test/api/workflows/org/space/ep/job-789",
            DateTimeOffset.UtcNow);

        var job = await client.GetJobAsync(ticket);

        Assert.Equal(InvocationStatus.Running, job.Status);
        Assert.Null(job.ResultJson);
    }

    [Fact]
    public async Task GetJobAsync_Returns_Succeeded_With_Result_On_200()
    {
        var (client, mock) = MockClientFactory.Create();
        const string pollUrl = "https://edge.mquark.test/api/workflows/org/space/ep/job-done";
        mock.When(HttpMethod.Get, pollUrl)
            .Respond(HttpStatusCode.OK, "text/plain", """{"score":0.9}""");

        var job = await client.GetJobAsync(pollUrl);

        Assert.Equal(InvocationStatus.Succeeded, job.Status);
        Assert.Equal("""{"score":0.9}""", job.ResultJson);
        Assert.True(job.IsTerminal);
    }
}
