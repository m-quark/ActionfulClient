using System.Net;

namespace MQuark.Actionful.Client;

/// <summary>
/// Thrown when the Actionful endpoint returns a non-success HTTP status code.
/// </summary>
public sealed class ActionfulException : Exception
{
    /// <summary>HTTP status code returned by the endpoint.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Response body returned by the endpoint, if any.</summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Server-requested retry delay. Populated only on <c>429 Too Many Requests</c> responses
    /// that include a <c>Retry-After</c> header.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    internal ActionfulException(HttpStatusCode statusCode, string? responseBody, TimeSpan? retryAfter = null)
        : base(BuildMessage(statusCode, responseBody))
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    private static string BuildMessage(HttpStatusCode statusCode, string? body)
    {
        var msg = $"Actionful endpoint returned {(int)statusCode} {statusCode}";
        return string.IsNullOrWhiteSpace(body) ? msg : $"{msg}: {body}";
    }
}
