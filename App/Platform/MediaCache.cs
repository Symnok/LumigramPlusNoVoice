using System;
using System.Threading.Tasks;
using Windows.Storage;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Attachments, kept on disk once fetched.
    ///
    /// Keyed by the id Telegram gave the file, so the same picture is downloaded
    /// once however many times it is looked at, and survives restarts. Nothing is
    /// ever evicted yet - worth adding before this is used in anger, but a cache
    /// that grows is a better problem than a photo that downloads on every scroll.
    ///
    /// Files are handed on as ms-appdata paths rather than decoded here, so XAML
    /// loads them itself, off the UI thread.
    /// </summary>
    internal static class MediaCache
    {
        private const string Folder = "media";

        /// <summary>
        /// Fetches an attachment if it is not already here, and returns the URI to
        /// show it from. Null when it could not be fetched.
        /// </summary>
        public static async Task<Uri> GetAsync(MtprotoClient client, MediaInfo info,
                                               Action<long, long> progress = null)
        {
            if (info == null || info.Id == 0) return null;

            string name = Name(info);

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(Folder, CreationCollisionOption.OpenIfExists);

                try
                {
                    await folder.GetFileAsync(name);
                    return Uri(name);
                }
                catch (Exception)
                {
                    // Not cached; fetch it below.
                }

                byte[] bytes = await Media.DownloadToMemoryAsync(
                    client, info, progress, TelegramService.Info);

                if (bytes == null || bytes.Length == 0) return null;

                StorageFile file = await folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, bytes);
                return Uri(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The cached file if there is one, without fetching anything.</summary>
        public static async Task<StorageFile> FindAsync(MediaInfo info)
        {
            if (info == null || info.Id == 0) return null;

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync(Folder);

                return await folder.GetFileAsync(Name(info));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The extension matters: the picture control and the video player both
        /// decide what to do partly by the file name, and a video saved as .jpg is
        /// refused by both.
        /// </summary>
        private static string Name(MediaInfo info)
        {
            string extension = info.Kind == MediaKind.Photo ? ".jpg"
                             : info.Kind == MediaKind.Video ? ".mp4"
                             : ".bin";

            return info.Id.ToString("x16") + extension;
        }

        private static Uri Uri(string name)
        {
            return new Uri("ms-appdata:///local/" + Folder + "/" + name);
        }
    }
}
