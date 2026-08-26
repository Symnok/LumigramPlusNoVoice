using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Profile and chat pictures for the chat list.
    ///
    /// Kept on disk and keyed by the photo id the server gave it, so a picture is
    /// fetched once and survives restarts - and a changed picture is a different id,
    /// which means there is nothing to invalidate.
    ///
    /// Everything here fails quietly. An avatar is decoration: a chat list that
    /// loads without pictures is a great deal better than one that does not load.
    /// </summary>
    internal static class AvatarCache
    {
        private const string Folder = "avatars";

        /// <summary>
        /// Avatars already decoded, so scrolling does not re-read the disk.
        ///
        /// Small and bounded by how many chats the list holds; the images are the
        /// small size, a few kilobytes each.
        /// </summary>
        private static readonly Dictionary<long, BitmapImage> Loaded =
            new Dictionary<long, BitmapImage>();

        private static readonly HashSet<long> Missing = new HashSet<long>();

        /// <summary>The picture if it is already to hand, or null.</summary>
        public static BitmapImage Get(long photoId)
        {
            if (photoId == 0) return null;

            BitmapImage image;
            lock (Loaded) if (Loaded.TryGetValue(photoId, out image)) return image;

            byte[] bytes = ReadFile(photoId);
            if (bytes == null) return null;

            image = Decode(bytes);
            if (image == null) return null;

            lock (Loaded) Loaded[photoId] = image;
            return image;
        }

        /// <summary>
        /// Fetches a picture if it is not already here. Null when there is nothing
        /// to show - no photo set, or the download did not work.
        /// </summary>
        public static async Task<BitmapImage> FetchAsync(MtprotoClient client, PeerInfo peer,
                                                        string kind)
        {
            if (peer == null || peer.PhotoId == 0) return null;

            BitmapImage existing = Get(peer.PhotoId);
            if (existing != null) return existing;

            // Remembered so a peer whose picture cannot be fetched is not retried on
            // every refresh of the list.
            lock (Missing) if (Missing.Contains(peer.PhotoId)) return null;

            try
            {
                byte[] location = Peers.PhotoLocation(peer, kind);
                if (location == null) return null;

                byte[] bytes = await Media.DownloadLocationAsync(client, location,
                                                                 TelegramService.Info);
                if (bytes == null || bytes.Length == 0)
                {
                    lock (Missing) Missing.Add(peer.PhotoId);
                    return null;
                }

                WriteFile(peer.PhotoId, bytes);

                BitmapImage image = Decode(bytes);
                if (image == null) return null;

                lock (Loaded) Loaded[peer.PhotoId] = image;
                return image;
            }
            catch (Exception)
            {
                lock (Missing) Missing.Add(peer.PhotoId);
                return null;
            }
        }

        private static BitmapImage Decode(byte[] bytes)
        {
            try
            {
                var image = new BitmapImage();
                using (var stream = new MemoryStream(bytes)) image.SetSource(stream);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string PathFor(long photoId)
        {
            return Folder + "/" + photoId.ToString("x16") + ".jpg";
        }

        private static byte[] ReadFile(long photoId)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    string path = PathFor(photoId);
                    if (!store.FileExists(path)) return null;

                    using (var fs = store.OpenFile(path, FileMode.Open, FileAccess.Read))
                    {
                        var bytes = new byte[fs.Length];
                        fs.Read(bytes, 0, bytes.Length);
                        return bytes;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteFile(long photoId, byte[] bytes)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.DirectoryExists(Folder)) store.CreateDirectory(Folder);

                    using (var fs = store.OpenFile(PathFor(photoId), FileMode.Create,
                                                   FileAccess.Write))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
