using System.ComponentModel.DataAnnotations;

namespace MQuark.Actionful.Client;

/// <summary>Configuration for <see cref="IActionfulClient"/>.</summary>
public sealed class ActionfulClientOptions
{
    /// <summary>The configuration section name to bind from appsettings.</summary>
    public static readonly string SectionName = "Actionful";

    /// <summary>The full URL of the published Actionful endpoint.</summary>
    [Required, Url]
    public string EndpointUrl { get; init; } = string.Empty;

    /// <summary>Access token shown in the Actionful Web UI for this endpoint.</summary>
    [Required]
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Access secret shown in the Actionful Web UI for this endpoint.</summary>
    [Required]
    public string AccessSecret { get; init; } = string.Empty;

    /// <summary>
    /// How long to ask the server to hold an invocation open before handing back a job to poll, sent as
    /// RFC 7240 <c>Prefer: wait</c>. A flow that finishes inside the hold costs a single round trip.
    /// Leave null to accept the server's own default; the server clamps anything it will not sustain.
    /// </summary>
    public TimeSpan? PreferredWait { get; init; }

    /// <summary>
    /// Wait before the first poll of a job that is still running. Default: 250ms.
    /// </summary>
    /// <remarks>
    /// Subsequent waits double up to <see cref="MaxPollInterval"/>, with jitter, so a fast job is
    /// noticed quickly while a slow one settles into an unobtrusive cadence.
    /// </remarks>
    public TimeSpan InitialPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on the wait between polls. Default: 5 seconds.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fixed interval between polls, disabling backoff.
    /// </summary>
    [Obsolete("Polling now backs off; set InitialPollInterval and MaxPollInterval instead. " +
              "When this is set it pins a fixed interval, preserving pre-1.1 behaviour.")]
    public TimeSpan? PollInterval { get; init; }
}
