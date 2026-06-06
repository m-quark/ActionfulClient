using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace MQuark.Actionful.Client;

/// <summary>
/// Attaches HTTP Basic Auth credentials derived from <see cref="ActionfulClientOptions"/>
/// to every outbound request.
/// </summary>
internal sealed class AccessTokenHandler : DelegatingHandler
{
    private readonly AuthenticationHeaderValue _authHeader;

    public AccessTokenHandler(IOptions<ActionfulClientOptions> options)
    {
        var opts = options.Value;
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{opts.AccessToken}:{opts.AccessSecret}"));
        _authHeader = new AuthenticationHeaderValue("Basic", credentials);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = _authHeader;
        return base.SendAsync(request, cancellationToken);
    }
}
