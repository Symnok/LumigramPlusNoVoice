using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Threading.Tasks;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Downloaded attachments, kept in isolated storage.
    ///
    /// Caching is not an optimisation here, it is a requirement. Re-downloading a
    /// photo every time a conversation is opened would be slow on this hardware and
    /// wasteful of a mobile data allowance, and holding them all in memory is not an
    /// option on a 512 MB device.
    ///
    /// Files are named by the media id, which is stable. The file *reference* that
    /// authorises a download expires, but the id does not - so a cached file stays
    /// valid long after the reference that fetched it has died.
    /// </summary>
    internal static class MediaCache
    {
        private const string Folder = "media";

        /// <summary>Keep the cache bounded; a phone has little room to spare.</summary>
        private const long MaxBytes = 20 * 1024 * 1024;

        private static string PathFor(MediaInfo info)
        {
            string extension = info.Kind == MediaKind.Video ? ".mp4"
                             : info.Kind == MediaKind.Photo ? ".jpg"
                             : ".bin";
            return Folder + "/" + info.Id + extension;
        }

        public static bool TryGetPath(MediaInfo info, out string path)
        {
            path = PathFor(info);
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    return store.FileExists(path);
            }
            catch (Exception) { return false; }
        }

        public static byte[] Read(string path)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(path, FileMode.Open, FileAccess.Read))
                {
                    var data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return data;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Returns the cached file, downloading it first if necessary.
        ///
        /// Chunks are written straight to storage rather than accumulated, so a
        /// large file never has to fit in memory.
        /// </summary>
        public static async Task<string> GetAsync(MtprotoClient client, MediaInfo info,
                                                  Action<long, long> progress = null)
        {
            string path;
            if (TryGetPath(info, out path)) return path;

            EnsureFolder();
            Trim();

            string temporary = path + ".part";

            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(temporary, FileMode.Create, FileAccess.Write))
                {
                    await Media.DownloadAsync(client, info,
                        delegate (byte[] chunk) { fs.Write(chunk, 0, chunk.Length); },
                        progress, TelegramService.Info);
                }

                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (store.FileExists(path)) store.DeleteFile(path);
                    store.MoveFile(temporary, path);
                }
                return path;
            }
            catch (Exception)
            {
                // Never leave a half-written file behind: it would be served as if
                // it were complete on the next request.
                try
                {
                    using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                        if (store.FileExists(temporary)) store.DeleteFile(temporary);
                }
                catch (Exception) { }
                throw;
            }
        }

        private static void EnsureFolder()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    if (!store.DirectoryExists(Folder)) store.CreateDirectory(Folder);
            }
            catch (Exception) { }
        }

        /// <summary>Deletes the oldest files once the cache grows past its limit.</summary>
        private static void Trim()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.DirectoryExists(Folder)) return;

                    string[] names = store.GetFileNames(Folder + "/*");
                    long total = 0;
                    var dates = new DateTimeOffset[names.Length];
                    var sizes = new long[names.Length];

                    for (int i = 0; i < names.Length; i++)
                    {
                        string full = Folder + "/" + names[i];
                        dates[i] = store.GetLastWriteTime(full);
                        using (var fs = store.OpenFile(full, FileMode.Open, FileAccess.Read))
                            sizes[i] = fs.Length;
                        total += sizes[i];
                    }

                    if (total <= MaxBytes) return;

                    Array.Sort(dates, names);       // oldest first
                    for (int i = 0; i < names.Length && total > MaxBytes; i++)
                    {
                        string full = Folder + "/" + names[i];
                        try
                        {
                            using (var fs = store.OpenFile(full, FileMode.Open, FileAccess.Read))
                                total -= fs.Length;
                            store.DeleteFile(full);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        public static void Clear()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.DirectoryExists(Folder)) return;
                    foreach (string name in store.GetFileNames(Folder + "/*"))
                    {
                        try { store.DeleteFile(Folder + "/" + name); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
