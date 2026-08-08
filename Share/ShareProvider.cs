using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Photon.Share;

/// <summary>
/// Windows Share Sheet integration. Surfaces the selected file(s) to the OS
/// share contract so the user can send them to Mail, OneDrive, Teams, etc.
///
/// WinUI 3's DataTransferManager API requires the calling window be initialized
/// as a share source — that's handled by the caller passing in the window's
/// HWND via <see cref="WinRT.Interop.InitializeWithWindow.Initialize"/>.
/// </summary>
public sealed class ShareProvider
{
    /// <summary>Share one or more files via the Windows share sheet.</summary>
    public void ShareFiles(string title, IEnumerable<string> filePaths)
    {
        var manager = DataTransferManager.GetForCurrentView();
        manager.DataRequested += OnDataRequested;

        DataTransferManager.ShowShareUI(new ShareUIOptions
        {
            Theme = ShareUITheme.Default,
        });

        void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            // One-shot — unsubscribe so the next share starts fresh.
            manager.DataRequested -= OnDataRequested;

            var deferral = args.Request.GetDeferral();
            try
            {
                var dp = new DataPackage { Properties = { Title = title } };
                var storageItems = new List<IStorageItem>();
                foreach (var path in filePaths)
                {
                    var file = StorageFile.GetFileFromPathAsync(path).GetAwaiter().GetResult();
                    storageItems.Add(file);
                }
                dp.SetStorageItems(storageItems);

                // Offer a bitmap thumbnail for the first file so the share
                // sheet can preview the content.
                if (storageItems.Count > 0 && storageItems[0] is StorageFile firstFile)
                {
                    try
                    {
                        var thumb = firstFile.GetThumbnailAsync(
                            Windows.Storage.FileProperties.ThumbnailMode.PicturesView, 256).GetAwaiter().GetResult();
                        if (thumb is not null)
                        {
                            dp.Properties.Thumbnail = RandomAccessStreamReference.CreateFromStream(thumb);
                            dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(firstFile));
                        }
                    }
                    catch { /* thumbnail optional */ }
                }

                args.Request.Data = dp;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    /// <summary>Copy a single image file's bitmap + path + storage item to the clipboard.</summary>
    public async Task CopyImageToClipboardAsync(string filePath)
    {
        var package = new DataPackage();
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        // Also include the file path as text so paste-into-text-fields works.
        package.SetText(filePath);
        // And the StorageFile itself so paste-into-Explorer works.
        package.SetStorageItems(new[] { file });
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>Copy just the text path to the clipboard.</summary>
    public void CopyPathToClipboard(string filePath)
    {
        var package = new DataPackage();
        package.SetText(filePath);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
