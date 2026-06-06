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
    /// Minimum interval between poll requests when waiting for a job to complete.
    /// The actual wait is <c>max(PollInterval, Retry-After)</c> — the server hint wins when it is longer.
    /// Default: 2 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
