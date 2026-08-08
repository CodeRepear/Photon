using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Photon.Views;
using Microsoft.UI.Xaml.Media;

namespace Photon;
public sealed partial class MainWindow : Window
{
    private bool _isViewerActive = false;
    public Frame GetContentFrame() => ContentFrame;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Photon";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        this.Resize(1280, 800);
    }

    public void SetWindowTitle(string title)
    {
        AppTitleText.Text = string.IsNullOrEmpty(title) ? "Photon" : $"Photon - {title}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    public void OpenDirectFile(string filePath)
    {
        var item = new Models.MediaItem(
            Path: filePath,
            FileName: System.IO.Path.GetFileName(filePath),
            Type: Core.FormatRegistry.Classify(filePath) ?? Models.MediaType.Image,
            FileSize: (ulong)new System.IO.FileInfo(filePath).Length,
            DateCreated: System.IO.File.GetCreationTimeUtc(filePath),
            DateModified: System.IO.File.GetLastWriteTimeUtc(filePath),
            Width: 0, Height: 0,
            Format: Core.FormatRegistry.GetFormatLabel(filePath)
        );
        
        // Pass the single item as both current and sibling list
        ContentFrame.Navigate(typeof(Views.ViewerPage), new Views.ViewerNavigationPayload(item, new[] { item }));
        NavView.SelectedItem = null;
    }

    public void SetScreenCaptureProtection(bool enable)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // 0x00000011 (WDA_EXCLUDEFROMCAPTURE) makes the window appear black in screenshots
        uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        uint WDA_NONE = 0x00000000;
        SetWindowDisplayAffinity(hwnd, enable ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    public void ConfigureTitleBar(bool isViewer, UIElement? center = null)
    {
        _isViewerActive = isViewer;

        // Hide "Photon" text and swap hamburger -> back glyph when in the viewer
        AppTitleText.Visibility = isViewer ? Visibility.Collapsed : Visibility.Visible;
        HamburgerButton.Content = isViewer ? "\uE72B" : "\uE700"; // back arrow vs. hamburger

        // Inject the search bar or filename
        TitleCenterContainer.Content = center;
    }

    // Custom hamburger button: toggles the pane normally, acts as Back inside the viewer
    private void OnHamburgerClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        // Only select the default gallery if a direct file hasn't already been opened
        if (ContentFrame.SourcePageType == null)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Type? page = tag switch
            {
                "Gallery"       => typeof(GalleryView),
                "Favorites"     => typeof(FavoritesPage),
                "Videos"        => typeof(VideosPage),
                "Albums"        => typeof(AlbumsPage),
                "SecureFolder"  => typeof(SecureFolderPage),
                _               => null,
            };
            if (page is not null)
                ContentFrame.Navigate(page, tag, args.RecommendedNavigationTransitionInfo);
        }
    }
}

internal static class WindowExtensions
{
    public static void Resize(this Window window, int width, int height)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
    }
}