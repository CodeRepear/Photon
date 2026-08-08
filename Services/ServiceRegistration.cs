using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photon.AI;
using Photon.Core;
using Photon.Edit;

namespace Photon.Services;

public static class ServiceRegistration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
        });

        services.AddSingleton(_ => AppSettings.Load());

        // Core services
        services.AddSingleton<MetadataReader>();
        services.AddSingleton<ThumbnailEngine>();
        services.AddSingleton<MediaLibrary>();
        services.AddSingleton<LibraryDatabase>();
        services.AddSingleton<SecureVault>();

        // Edit services
        services.AddSingleton<ConversionPipeline>();
        services.AddSingleton<CompressionTool>();

        // AI
        services.AddSingleton<SubjectDetector>();

        return services.BuildServiceProvider();
    }
}
