using System;
using System.Windows;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>
    /// Closes the app and unloads it from memory.
    ///
    /// Windows Phone normally has no exit: pressing Back off the first page
    /// *tombstones* the app, which leaves it resident and resumable. That is fine
    /// for most apps and not for this one - a resident session holds an open socket
    /// and an authorisation key in memory, and on a 512 MB device it is also worth
    /// giving the memory back.
    ///
    /// Application.Current.Terminate() genuinely ends the process. It arrived in
    /// 8.1, so the call is guarded: on anything older the fallback is to close the
    /// connection and let the platform tombstone as usual, which is the best that
    /// can be done there.
    /// </summary>
    internal static class AppExit
    {
        public static void Quit()
        {
            // Background work first. The location subscription is what keeps this
            // app alive off screen, so leaving it running through an explicit exit
            // left the location indicator on for an app the user had closed.
            try { BackgroundControl.StopAll(); } catch (Exception) { }

            // Then the connection either way: an abandoned socket is worse than a
            // slow exit.
            try { TelegramService.Disconnect(); } catch (Exception) { }

            try
            {
                Application.Current.Terminate();
            }
            catch (Exception)
            {
                // Not available: the app stays resident but at least holds nothing.
            }
        }
    }
}
