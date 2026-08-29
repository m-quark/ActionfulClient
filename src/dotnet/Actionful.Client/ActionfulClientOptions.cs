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
    /// Wait before the first poll of a job that is still running. Default: 250ms.
    /// </summary>
    /// <remarks>
    /// Subsequent waits double up to <see cref="MaxPollInterval"/>, with jitter, so a fast job is
    /// noticed quickly while a slow one settles into an unobtrusive cadence.
    /// </remarks>
    public TimeSpan InitialPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on the wait between polls. Default: 5 seconds.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(5);
}
