using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using Photon.Core;
using Photon.Services;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Photon;

/// <summary>
/// Application entry point. Wires up the DI container, ensures local-data
/// folders exist, and shows <see cref="MainWindow"/> on launch.
/// </summary>
public partial class App : Application
{
    private static IServiceProvider? _services;
    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not initialized");

    /// <summary>
    /// Strongly-typed main window so XAML code-behind can call into its
    /// content frame without a cast.
    /// </summary>
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        this.InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureLocalFolders();
        _services = ServiceRegistration.Build();
        MainWindow = new MainWindow();

        string? launchFilePath = null;

        // 1. Check packaged file activation
        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activatedArgs.Kind == ExtendedActivationKind.File && activatedArgs.Data is IFileActivatedEventArgs fileArgs)
        {
            if (fileArgs.Files.Count > 0)
                launchFilePath = fileArgs.Files[0].Path;
        }
        else
        {
            // 2. Fallback for unpackaged .exe launch
            string[] cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Length > 1 && System.IO.File.Exists(cmdArgs[1]))
                launchFilePath = cmdArgs[1];
        }

        MainWindow.Activate();

        if (launchFilePath != null)
        {
            MainWindow.OpenDirectFile(launchFilePath);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var logger = _services?.GetService<ILogger<App>>();
        logger?.LogError(e.Exception, "Unhandled exception: {Message}", e.Message);
        // Prevent the app from crashing on handled-by-us exceptions.
        e.Handled = true;
    }

    /// <summary>Convenience accessor for resolving services from XAML code-behind.</summary>
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();
}
