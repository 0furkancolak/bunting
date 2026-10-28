using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Hosting;

namespace Bunting.Sdk;

public static class OpenFeatureBuilderExtensions
{
    /// <summary>Registers <see cref="BuntingProvider"/> as the default OpenFeature provider.</summary>
    public static OpenFeatureBuilder AddBuntingProvider(this OpenFeatureBuilder builder, Action<BuntingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.AddProvider(services => Create(services, configure));
    }

    /// <summary>Registers <see cref="BuntingProvider"/> for a named domain.</summary>
    public static OpenFeatureBuilder AddBuntingProvider(this OpenFeatureBuilder builder, string domain, Action<BuntingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.AddProvider(domain, (services, _) => Create(services, configure));
    }

    private static BuntingProvider Create(IServiceProvider services, Action<BuntingOptions> configure)
    {
        var options = new BuntingOptions();
        configure(options);
        return new BuntingProvider(options, services.GetService<ILogger<BuntingProvider>>());
    }
}
