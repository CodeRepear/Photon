using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Photon.Core;
using Windows.Storage.Pickers;

namespace Photon.Views;

public class AlbumVm : INotifyPropertyChanged
{
    public AlbumRecord Record { get; set; }
    
    public string Name => Record.Name;
    public string Description => string.IsNullOrEmpty(Record.Description) ? "No description" : Record.Description;
    public string CountText => $"{Record.ItemCount} item{(Record.ItemCount == 1 ? "" : "s")}";
    public Visibility NoCoverVisibility => CoverImage == null ? Visibility.Visible : Visibility.Collapsed;

    private BitmapImage? _coverImage;
    public BitmapImage? CoverImage 
    {
        get => _coverImage;
        set 
        { 
            _coverImage = value; 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoCoverVisibility)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AlbumVm(AlbumRecord record) => Record = record;
}

public sealed partial class AlbumsPage : Page
{
    private LibraryDatabase? _db;
    private ObservableCollection<AlbumVm> _albums = new();
    private MenuFlyout? _contextMenu;

    public AlbumsPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _db ??= App.GetService<LibraryDatabase>();
        RebuildAlbums();
    }

    private async void OnCreateAlbum(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        var inputBox = new TextBox { PlaceholderText = "Album name", Width = 300 };
        var dlg = new ContentDialog {
            XamlRoot = this.XamlRoot, Title = "New Album", Content = inputBox,
            PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(inputBox.Text))
        {
            _db.CreateAlbum(inputBox.Text.Trim());
            RebuildAlbums();
        }
    }

    private void RebuildAlbums()
    {
        if (_db is null) return;
        _albums.Clear();
        var records = _db.ListAlbums();
        
        foreach (var record in records)
        {
            var vm = new AlbumVm(record);
            if (!string.IsNullOrEmpty(record.CoverPath) && File.Exists(record.CoverPath))
            {
                vm.CoverImage = new BitmapImage(new Uri(record.CoverPath)) { DecodePixelWidth = 300 };
            }
            _albums.Add(vm);
        }

        AlbumsGrid.ItemsSource = _albums;
        EmptyState.Visibility = _albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumsGrid.Visibility = _albums.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAlbumRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlbumVm vm) return;

        _contextMenu = new MenuFlyout();
        
        var editDetails = new MenuFlyoutItem { Text = "Edit details" };
        editDetails.Click += async (s, args) => await EditAlbumDetails(vm);
        
        var setCover = new MenuFlyoutItem { Text = "Set cover photo..." };
        setCover.Click += async (s, args) => await SetCoverPhoto(vm);
        
        var delete = new MenuFlyoutItem { Text = "Delete album" };
        delete.Click += async (s, args) => 
        {
            _db?.DeleteAlbum(vm.Record.Id);
            RebuildAlbums();
        };

        _contextMenu.Items.Add(editDetails);
        _contextMenu.Items.Add(setCover);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(delete);
        _contextMenu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
    }

    private async System.Threading.Tasks.Task EditAlbumDetails(AlbumVm vm)
    {
        var nameBox = new TextBox { Text = vm.Record.Name, Header = "Name", Width = 300 };
        var descBox = new TextBox { Text = vm.Record.Description, Header = "Description", Width = 300, AcceptsReturn = true, Height = 100 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(descBox);

        var dlg = new ContentDialog {
            XamlRoot = this.XamlRoot, Title = "Edit Album", Content = panel,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel"
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
        {
            _db?.UpdateAlbum(vm.Record.Id, nameBox.Text.Trim(), descBox.Text.Trim(), vm.Record.CoverPath);
            RebuildAlbums();
        }
    }

    private void OnAlbumTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumVm vm)
        {
            App.MainWindow.GetContentFrame().Navigate(typeof(AlbumContentPage), vm.Record);
        }
    }

    private async System.Threading.Tasks.Task SetCoverPhoto(AlbumVm vm)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _db?.UpdateAlbum(vm.Record.Id, vm.Record.Name, vm.Record.Description, file.Path);
            RebuildAlbums();
        }
    }
}