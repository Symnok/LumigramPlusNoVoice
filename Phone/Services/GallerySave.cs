using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>Raised when there is nowhere acceptable to put a file.</summary>
    public sealed class NoStorageException : Exception
    {
        public NoStorageException(string message) : base(message) { }
    }

    /// <summary>Where a saved file ended up, so the user can be told something useful.</summary>
    public sealed class SavedFile
    {
        public StorageFile File;
        public string Location;
    }

    /// <summary>
    /// Saving attachments to the SD card.
    ///
    /// Windows Phone 8.1 does not let a third-party app write into the camera roll
    /// or the video library. KnownFolders.CameraRoll is read-only, and the one
    /// sanctioned write path - MediaLibrary.SavePictureToCameraRoll - takes
    /// pictures only. Attempting it for video fails with "Access is denied": the
    /// platform refusing, not a bug.
    ///
    /// That leaves the SD card as the only place a saved video is genuinely useful:
    /// visible in File Explorer, playable by the system player, survives the app
    /// being uninstalled, and can be copied off the phone.
    ///
    /// There is deliberately no fallback to the app's own storage. A video hidden
    /// inside app storage is reachable by nothing except this app, which cannot
    /// play it - so "saving" there would be a promise the file does not keep. With
    /// no card, the save is refused and the user is told why.
    ///
    /// Files are streamed to disk: a video is easily larger than the memory this
    /// app is allowed on a 512 MB device.
    /// </summary>
    internal static class GallerySave
    {
        private const string FolderName = "Lumigram";

        /// <summary>
        /// Why the last card lookup failed, for reporting to the user.
        ///
        /// Kept because "no SD card" and "the card is there but this app may not
        /// touch it" are completely different problems with completely different
        /// answers, and collapsing them into one message hides which one it is.
        /// </summary>
        public static string LastCardError = "";

        /// <summary>The SD card's Lumigram folder, or null if it cannot be used.</summary>
        public static async Task<StorageFolder> TryGetCardFolderAsync()
        {
            LastCardError = "";

            System.Collections.Generic.IReadOnlyList<StorageFolder> cards;
            try
            {
                cards = await KnownFolders.RemovableDevices.GetFoldersAsync();
            }
            catch (Exception ex)
            {
                LastCardError = "cannot list removable storage: " +
                                ex.GetType().Name + " - " + ex.Message;
                return null;
            }

            if (cards == null || cards.Count == 0)
            {
                // On WP8.1 an app sees the card only through file type associations
                // it has registered; with none, the list comes back empty even when
                // a card is physically present.
                LastCardError = "removable storage listed 0 devices";
                return null;
            }

            try
            {
                return await cards[0].CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
            }
            catch (Exception ex)
            {
                LastCardError = "card found (" + cards[0].Name + ") but not writable: " +
                                ex.GetType().Name + " - " + ex.Message;
                return null;
            }
        }

        public static async Task<bool> HasCardAsync()
        {
            return (await TryGetCardFolderAsync()) != null;
        }

        /// <summary>A short description of the storage situation, for the UI.</summary>
        public static async Task<string> DescribeStorageAsync()
        {
            StorageFolder folder = await TryGetCardFolderAsync();
            if (folder != null) return "SD card ready: " + folder.Path;
            return string.IsNullOrEmpty(LastCardError) ? "no SD card" : LastCardError;
        }

        /// <summary>
        /// Downloads an attachment to the SD card.
        ///
        /// GenerateUniqueName rather than ReplaceExisting: saving the same video
        /// twice should not silently overwrite the first copy.
        /// </summary>
        public static async Task<SavedFile> DownloadAsync(MtprotoClient client, MediaInfo info,
                                                          string name,
                                                          Action<long, long> progress = null)
        {
            StorageFolder folder = await TryGetCardFolderAsync();
            if (folder == null)
                throw new NoStorageException(
                    string.IsNullOrEmpty(LastCardError) ? "no SD card" : LastCardError);

            StorageFile file = await folder.CreateFileAsync(
                Sanitise(name), CreationCollisionOption.GenerateUniqueName);

            try
            {
                using (Stream stream = await file.OpenStreamForWriteAsync())
                {
                    await Media.DownloadAsync(client, info,
                        delegate (byte[] chunk) { stream.Write(chunk, 0, chunk.Length); },
                        progress, TelegramService.Info);

                    stream.Flush();
                }

                return new SavedFile
                {
                    File = file,
                    Location = "SD card, " + FolderName + " folder",
                };
            }
            catch (Exception)
            {
                // A partial file would look like a real video and fail to play,
                // which is worse than it not being there.
                try { await file.DeleteAsync(); }
                catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// Hands the file to whatever the phone uses to open it.
        ///
        /// This is what makes a saved video watchable: the app cannot decode video,
        /// but the system player can.
        /// </summary>
        public static async Task<bool> OpenAsync(StorageFile file)
        {
            try { return await Windows.System.Launcher.LaunchFileAsync(file); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Makes a server-supplied name safe to use as a file name.
        ///
        /// The name comes from whoever sent the file, so it can contain anything -
        /// path separators included. Building a path from it unchecked is how an
        /// attachment ends up written somewhere it should not be.
        /// </summary>
        public static string Sanitise(string name, string fallbackExtension = ".mp4")
        {
            if (string.IsNullOrEmpty(name))
                name = "lumigram-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + fallbackExtension;

            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                bool ok = (c >= (char)48 && c <= (char)57)        // 0-9
                       || (c >= (char)65 && c <= (char)90)        // A-Z
                       || (c >= (char)97 && c <= (char)122)       // a-z
                       || c == (char)46 || c == (char)45 || c == (char)95;   // . - _
                sb.Append(ok ? c : (char)95);
            }

            string safe = sb.ToString().Trim((char)46);
            if (safe.Length == 0) safe = "lumigram" + fallbackExtension;
            if (safe.IndexOf((char)46) < 0) safe += fallbackExtension;
            if (safe.Length > 64) safe = safe.Substring(safe.Length - 64);

            return safe;
        }
    }
}
