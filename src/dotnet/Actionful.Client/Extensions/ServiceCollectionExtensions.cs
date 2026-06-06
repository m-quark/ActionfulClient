using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MQuark.Actionful.Client.Extensions;

/// <summary>Extension methods for registering the Actionful client with <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IActionfulClient"/> and binds options from <paramref name="configuration"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="IHttpClientBuilder"/> so callers can chain resilience handlers, logging, etc.
    /// </returns>
    /// <example>
    /// <code>
    /// services.AddActionfulClient(configuration.GetSection("Actionful"))
    ///         .AddStandardResilienceHandler(); // optional
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddActionfulClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ActionfulClientOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return AddCore(services);
    }

    /// <summary>
    /// Registers <see cref="IActionfulClient"/> and configures options via <paramref name="configure"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="IHttpClientBuilder"/> so callers can chain resilience handlers, logging, etc.
    /// </returns>
    /// <example>
    /// <code>
    /// services.AddActionfulClient(o =>
    /// {
    ///     o.EndpointUrl  = "https://...";
    ///     o.AccessToken  = "...";
    ///     o.AccessSecret = "...";
    /// });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddActionfulClient(
        this IServiceCollection services,
        Action<ActionfulClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ActionfulClientOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return AddCore(services);
    }

    private static IHttpClientBuilder AddCore(IServiceCollection services)
    {
        services.AddTransient<AccessTokenHandler>();

        return services
            .AddHttpClient<IActionfulClient, ActionfulClient>()
            .AddHttpMessageHandler<AccessTokenHandler>();
    }
}
