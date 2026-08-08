using System;
using System.IO;

namespace Photon.Core;

/// <summary>
/// Centralized path constants for runtime data. Everything lives under
/// <c>%LocalAppData%\Photon\</c> so the app can write without admin rights
/// and so users can easily reset state by deleting the folder.
/// </summary>
public static class AppPaths
{
    public static string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string AppRoot { get; } = Path.Combine(LocalAppData, "Photon");
    public static string ThumbCacheDir { get; set; } = Path.Combine(AppRoot, "ThumbCache");
    public static string LibraryDbPath { get; } = Path.Combine(AppRoot, "library.db");
    public static string LogDir { get; } = Path.Combine(AppRoot, "logs");
    public static string SecureVaultDir { get; } = Path.Combine(AppRoot, "SecureVault");
    public static string SecureVaultDbPath { get; } = Path.Combine(AppRoot, "vault.db");

    /// <summary>Create all required directories. Safe to call repeatedly.</summary>
    public static void EnsureLocalFolders()
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(ThumbCacheDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(SecureVaultDir);
    }
}