using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Photon.Core;

namespace Photon.Views;

public class SecureItemVm : INotifyPropertyChanged
{
    public VaultEntry Entry { get; }
    public string OriginalName => Entry.OriginalName;
    public double AspectRatio { get; set; } = 1.0;
    
    private BitmapImage? _displayImage;
    public BitmapImage? DisplayImage 
    {
        get => _displayImage;
        set 
        { 
            _displayImage = value; 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayImage))); 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility))); 
        }
    }
    
    public Visibility IconVisibility => DisplayImage == null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public SecureItemVm(VaultEntry entry) => Entry = entry;
}

public sealed partial class SecureFolderPage : Page
{
    private SecureVault? _vault;
    private ObservableCollection<SecureItemVm> _files = new();
    private MenuFlyout? _contextMenu;
    private double _containerWidth = 900;
    private DispatcherTimer? _resizeTimer;

    public SecureFolderPage()
    {
        this.InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _vault = App.GetService<SecureVault>();
        if (_vault is null) return;

        AppPaths.EnsureLocalFolders();

        if (!_vault.IsPasswordSet)
        {
            LockSubtitle.Text = "Create a password for your secure folder";
            AuthButton.Content = "Create Password";
            ForgotLink.Visibility = Visibility.Collapsed;
        }
        else
        {
            LockSubtitle.Text = "Enter your password to unlock";
            AuthButton.Content = "Unlock";
            ForgotLink.Visibility = Visibility.Visible;
        }

        try
        {
            var availability = await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)
                WindowsHelloBtn.Visibility = Visibility.Visible;
        } catch { }

        if (_vault.IsUnlocked) App.MainWindow.SetScreenCaptureProtection(true);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        App.MainWindow.SetScreenCaptureProtection(false);
    }

    // --- Authentication ---

    private async void OnAuthClick(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password)) { AuthError.Text = "Please enter a password"; return; }
        if (password.Length < 4) { AuthError.Text = "Password must be at least 4 characters"; return; }

        if (!_vault!.IsPasswordSet)
        {
            if (_vault.SetPassword(password)) ShowUnlocked();
            else AuthError.Text = "Failed to create vault";
        }
        else
        {
            if (_vault.Unlock(password)) ShowUnlocked();
            else AuthError.Text = "Incorrect password";
        }
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnAuthClick(sender, e);
    }

    private async void OnWindowsHelloClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync("Unlock Secure Folder");
            if (result == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified)
            {
                var locker = new Windows.Security.Credentials.PasswordVault();
                var creds = locker.Retrieve("PhotonVault", "hello_key");
                creds.RetrievePassword();
                if (_vault!.Unlock(creds.Password)) ShowUnlocked();
                else AuthError.Text = "Windows Hello unlock failed";
            }
        }
        catch { AuthError.Text = "Windows Hello is not available"; }
    }

    private void ShowUnlocked()
    {
        App.MainWindow.SetScreenCaptureProtection(true);
        LockScreen.Visibility = Visibility.Collapsed;
        UnlockedContent.Visibility = Visibility.Visible;
        RefreshFileList();

        var pwd = PasswordBox.Password;
        if (!string.IsNullOrEmpty(pwd))
        {
            try {
                var locker = new Windows.Security.Credentials.PasswordVault();
                locker.Add(new Windows.Security.Credentials.PasswordCredential("PhotonVault", "hello_key", pwd));
            } catch { }
        }
    }

    private void OnLockTap(object sender, TappedRoutedEventArgs e)
    {
        App.MainWindow.SetScreenCaptureProtection(false);
        _vault!.Lock();
        LockScreen.Visibility = Visibility.Visible;
        UnlockedContent.Visibility = Visibility.Collapsed;
        PasswordBox.Password = string.Empty;
        AuthError.Text = string.Empty;

        if (_vault.IsPasswordSet)
        {
            LockSubtitle.Text = "Enter your password to unlock";
            AuthButton.Content = "Unlock";
        }
    }

    private async void OnResetVaultClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot, Title = "Reset Secure Folder",
            Content = "This will permanently delete all encrypted files and the password. This cannot be undone.\n\nAre you sure?",
            PrimaryButtonText = "Delete Everything", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _vault!.Lock();
            try {
                foreach (var f in Directory.GetFiles(AppPaths.SecureVaultDir)) File.Delete(f);
                if (File.Exists(AppPaths.SecureVaultDbPath)) File.Delete(AppPaths.SecureVaultDbPath);
                var locker = new Windows.Security.Credentials.PasswordVault();
                locker.Remove(locker.Retrieve("PhotonVault", "hello_key"));
            } catch { }
            OnPageLoaded(sender, e);
        }
    }

    // --- Dynamic River Flow Layout Engine ---

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double newWidth = e.NewSize.Width - 32;
        if (Math.Abs(newWidth - _containerWidth) > 20 && newWidth > 100)
        {
            _containerWidth = newWidth;
            TriggerRelayout();
        }
    }

    private void TriggerRelayout()
    {
        if (_resizeTimer == null)
        {
            _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _resizeTimer.Tick += (s, e) => {
                _resizeTimer.Stop();
                BuildVisualTree();
            };
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void BuildVisualTree()
    {
        SecureRoot.Children.Clear();
        if (_files.Count == 0) return;

        var items = _files.ToList();
        int start = 0;
        const double targetHeight = 160;
        const double spacing = 6;

        while (start < items.Count)
        {
            double totalAr = 0;
            int end = start;

            while (end < items.Count)
            {
                double ar = items[end].AspectRatio;
                double testWidth = (totalAr + ar) * targetHeight + (end - start) * spacing;
                if (testWidth > _containerWidth && end > start) break;
                totalAr += ar;
                end++;
            }

            int count = end - start;
            double finalHeight;
            if (count == 1) {
                double w = Math.Min(items[start].AspectRatio * targetHeight, _containerWidth);
                finalHeight = Math.Max(80, Math.Min(targetHeight * 1.8, w / items[start].AspectRatio));
            } else {
                double availWidth = _containerWidth - (count - 1) * spacing;
                finalHeight = Math.Max(targetHeight * 0.5, Math.Min(targetHeight * 1.5, availWidth / totalAr));
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Margin = new Thickness(0, 0, 0, spacing) };
            for (int i = start; i < end; i++) {
                var item = items[i];
                double w = item.AspectRatio * finalHeight;
                row.Children.Add(CreatePhotoCard(item, w, finalHeight));
            }
            SecureRoot.Children.Add(row);
            start = end;
        }
    }

    private Grid CreatePhotoCard(SecureItemVm vm, double width, double height)
    {
        var card = new Grid
        {
            Width = width, Height = height,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            DataContext = vm, 
        };

        var img = new Image {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        img.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Source = vm, Path = new PropertyPath("DisplayImage"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        card.Children.Add(img);

        var iconStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        iconStack.SetBinding(StackPanel.VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding { Source = vm, Path = new PropertyPath("IconVisibility"), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        iconStack.Children.Add(new FontIcon { Glyph = "\uEB9F", FontSize = 32, FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.5 } });
        card.Children.Add(iconStack);

        var labelBorder = new Border {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 },
            Padding = new Thickness(4)
        };
        labelBorder.Child = new TextBlock { Text = vm.OriginalName, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        card.Children.Add(labelBorder);

        card.Tapped += OnSecureItemTapped;
        card.RightTapped += OnFileRightTapped;

        return card;
    }

    // --- Decryption & Processing ---

    private void RefreshFileList()
    {
        _files.Clear();
        if (_vault is null || !_vault.IsUnlocked) return;
        foreach (var entry in _vault.ListFiles())
        {
            var vm = new SecureItemVm(entry);
            _files.Add(vm);
            _ = LoadSecureImageAsync(vm, true);
        }
        VaultInfoLabel.Text = $"{_files.Count} item{(_files.Count == 1 ? "" : "s")} in vault";
        TriggerRelayout(); 
    }

    private async Task<MemoryStream?> DecryptToMemoryStreamAsync(VaultEntry entry)
    {
        try {
            var key = _vault!.GetCachedKey();
            if (key == null) return null;

            using var fs = File.OpenRead(entry.VaultPath);
            var iv = new byte[16];
            await fs.ReadAsync(iv, 0, 16);
            var lenBytes = new byte[4];
            await fs.ReadAsync(lenBytes, 0, 4);
            int nameLen = BitConverter.ToInt32(lenBytes, 0);
            await fs.ReadAsync(new byte[nameLen], 0, nameLen);

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key; aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            using var cryptoStream = new System.Security.Cryptography.CryptoStream(fs, decryptor, System.Security.Cryptography.CryptoStreamMode.Read);
            
            var ms = new MemoryStream();
            await cryptoStream.CopyToAsync(ms);
            return ms;
        } catch { return null; }
    }

    private async Task LoadSecureImageAsync(SecureItemVm vm, bool isThumbnail)
    {
        try {
            using var rawMs = await DecryptToMemoryStreamAsync(vm.Entry);
            if (rawMs == null) return;
            
            var ext = Photon.Core.FormatRegistry.GetExtension(vm.OriginalName);
            byte[] imageBytes;

            if (Photon.Core.FormatRegistry.MagickDecodedExtensions.Contains(ext))
            {
                imageBytes = await Task.Run(() => {
                    using var magick = new ImageMagick.MagickImage(rawMs.ToArray());
                    if (isThumbnail) {
                        var ratio = 250.0 / magick.Width;
                        if (ratio < 1.0) magick.Resize(250, (uint)(magick.Height * ratio));
                    }
                    return magick.ToByteArray(ImageMagick.MagickFormat.Jpeg);
                });
            }
            else
            {
                imageBytes = rawMs.ToArray();
            }

            DispatcherQueue.TryEnqueue(async () => 
            {
                try {
                    // Do not dispose the stream, let WinUI manage its lifecycle internally
                    var ras = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(imageBytes);
                        await writer.StoreAsync();
                    }
                    ras.Seek(0);

                    var bmp = new BitmapImage();
                    bmp.ImageOpened += (s, e) => {
                        if (bmp.PixelHeight > 0 && bmp.PixelWidth > 0) {
                            var ar = Math.Max(0.3, Math.Min(3.0, (double)bmp.PixelWidth / bmp.PixelHeight));
                            if (Math.Abs(vm.AspectRatio - ar) > 0.01) {
                                vm.AspectRatio = ar;
                                TriggerRelayout(); 
                            }
                        }
                    };
                    await bmp.SetSourceAsync(ras);
                    vm.DisplayImage = bmp;
                } catch { }
            });
        } catch { }
    }

    // --- Overlay & Context Menus ---

    private async void OnSecureItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SecureItemVm vm)
        {
            SecureViewerOverlay.Visibility = Visibility.Visible;
            try {
                using var rawMs = await DecryptToMemoryStreamAsync(vm.Entry);
                if (rawMs == null) return;

                var ext = Photon.Core.FormatRegistry.GetExtension(vm.OriginalName);
                byte[] imageBytes;

                if (Photon.Core.FormatRegistry.MagickDecodedExtensions.Contains(ext))
                {
                    imageBytes = await Task.Run(() => {
                        using var magick = new ImageMagick.MagickImage(rawMs.ToArray());
                        return magick.ToByteArray(ImageMagick.MagickFormat.Jpeg);
                    });
                }
                else
                {
                    imageBytes = rawMs.ToArray();
                }
                
                var ras = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(imageBytes);
                    await writer.StoreAsync();
                }
                ras.Seek(0);

                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ras);
                SecureFullImage.Source = bmp;
            } catch { }
        }
    }

    private void OnCloseSecureViewer(object sender, RoutedEventArgs e)
    {
        SecureViewerOverlay.Visibility = Visibility.Collapsed;
        SecureFullImage.Source = null;
    }

    private void OnFileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SecureItemVm vm) return;
        var entry = vm.Entry; 
        
        _contextMenu = new MenuFlyout();
        var export = new MenuFlyoutItem { Text = "Export to..." };
        export.Click += async (s, args) => await ExportFileAsync(entry);
        var remove = new MenuFlyoutItem { Text = "Remove from vault" };
        remove.Click += (s, args) =>
        {
            _vault!.RemoveFile(entry.VaultPath);
            RefreshFileList();
        };
        _contextMenu.Items.Add(export);
        _contextMenu.Items.Add(remove);
        _contextMenu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
    }

    private async void OnImportTap(object sender, TappedRoutedEventArgs e)
    {
        if (_vault is null || !_vault.IsUnlocked) return;
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        int imported = 0;
        foreach (var file in files) if (await _vault.ImportFileAsync(file.Path)) imported++;
        RefreshFileList();
        if (imported > 0) VaultInfoLabel.Text = $"Imported {imported} file{(imported == 1 ? "" : "s")}";
    }

    private async Task ExportFileAsync(VaultEntry entry)
    {
        if (_vault is null) return;
        var picker = new FileSavePicker { SuggestedFileName = entry.OriginalName };
        var ext = Path.GetExtension(entry.OriginalName);
        if (!string.IsNullOrEmpty(ext)) picker.FileTypeChoices.Add(ext.ToUpperInvariant().TrimStart('.'), new[] { ext });
        else picker.FileTypeChoices.Add("All", new[] { ".*" });

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (await _vault.ExportFileAsync(entry.VaultPath, file.Path)) VaultInfoLabel.Text = $"Exported: {entry.OriginalName}";
        else VaultInfoLabel.Text = "Export failed";
    }

    private async void OnChangePasswordTap(object sender, TappedRoutedEventArgs e)
    {
        if (_vault is null || !_vault.IsUnlocked) return;
        var oldBox = new PasswordBox { PlaceholderText = "Current password" };
        var newBox = new PasswordBox { PlaceholderText = "New password (min 4 chars)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Current password" }); panel.Children.Add(oldBox);
        panel.Children.Add(new TextBlock { Text = "New password" }); panel.Children.Add(newBox);

        var dialog = new ContentDialog {
            XamlRoot = this.XamlRoot, Title = "Change Password", Content = panel,
            PrimaryButtonText = "Change", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (_vault.ChangePassword(oldBox.Password, newBox.Password)) VaultInfoLabel.Text = "Password changed successfully";
            else VaultInfoLabel.Text = "Failed to change password (wrong current password?)";
        }
    }

    private void OnToolBtnEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(38, 255, 255, 255));
    }
    
    private void OnToolBtnExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}