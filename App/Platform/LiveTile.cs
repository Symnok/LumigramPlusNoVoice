using System;
using System.Collections.Generic;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// The start screen tile.
    ///
    /// Shows how many chats are waiting and who they are from - chats, not
    /// messages. Ten messages in one conversation is one thing to go and read, and a
    /// tile reading "10" for it says something the user has to go and disprove.
    ///
    /// Muted chats are left out, so the tile agrees with the notifications: a chat
    /// muted precisely so it stops asking for attention should not then ask for it
    /// from the start screen.
    ///
    /// Honest limitation: without a background task nothing updates this while the
    /// app is closed, so the tile is a snapshot from the last time the app was open
    /// rather than a live view. It is written anyway - it costs one call on a poll
    /// that already happened, and it is the half of the feature that survives if
    /// background execution ever arrives.
    /// </summary>
    internal static class LiveTile
    {
        /// <summary>How many chats to name. Four is what the wide tile fits.</summary>
        private const int Names = 4;

        /// <summary>
        /// Writes the tile from a chat list.
        ///
        /// Both tile sizes are written in one notification: the user chooses the
        /// size on the start screen, and a notification that only fills the one we
        /// guessed leaves the other blank.
        /// </summary>
        public static void Update(List<DialogEntry> dialogs, int nowUtc)
        {
            if (dialogs == null) return;

            var waiting = new List<string>();
            int chats = 0;

            foreach (DialogEntry d in dialogs)
            {
                if (d.UnreadCount <= 0) continue;
                if (d.IsMuted(nowUtc)) continue;

                chats++;

                if (waiting.Count < Names)
                {
                    waiting.Add(string.IsNullOrEmpty(d.Title) ? "chat" : d.Title);
                }
            }

            if (chats == 0)
            {
                Clear();
                return;
            }

            Write(chats, waiting);
        }

        /// <summary>Puts the tile back to its plain state.</summary>
        public static void Clear()
        {
            try
            {
                TileUpdateManager.CreateTileUpdaterForApplication().Clear();
                BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
            }
            catch (Exception)
            {
                // A tile that will not clear is not worth failing over.
            }
        }

        private static void Write(int chats, List<string> names)
        {
            try
            {
                string headline = chats == 1 ? "1 chat waiting"
                                             : chats + " chats waiting";

                TileUpdateManager.CreateTileUpdaterForApplication()
                    .Update(new TileNotification(Xml(headline, names)));

                // The badge is the part that reads at a glance, and it is the only
                // part the small tile has room for.
                XmlDocument badge = BadgeUpdateManager.GetTemplateContent(
                    BadgeTemplateType.BadgeNumber);

                ((XmlElement)badge.SelectSingleNode("/badge"))
                    .SetAttribute("value", chats.ToString());

                BadgeUpdateManager.CreateBadgeUpdaterForApplication()
                    .Update(new BadgeNotification(badge));
            }
            catch (Exception)
            {
                // Same: the tile is the last thing that should take a refresh down.
            }
        }

        /// <summary>
        /// Builds the tile payload by hand.
        ///
        /// The template helpers return one binding at a time and merging two
        /// documents is more code than writing the four lines they would have
        /// produced. branding="name" keeps the app name on the tile once a
        /// notification replaces the default one - without it the name the manifest
        /// asks for disappears the moment the tile goes live.
        /// </summary>
        private static XmlDocument Xml(string headline, List<string> names)
        {
            var text = new StringBuilder();
            text.Append("<tile><visual version='2' branding='name'>");

            Binding(text, "TileSquare150x150Text01", "TileSquareText01", headline, names, 3);
            Binding(text, "TileWide310x150Text01", "TileWideText01", headline, names, 4);

            text.Append("</visual></tile>");

            var xml = new XmlDocument();
            xml.LoadXml(text.ToString());
            return xml;
        }

        private static void Binding(StringBuilder text, string template, string fallback,
                                    string headline, List<string> names, int lines)
        {
            text.Append("<binding template='").Append(template)
                .Append("' fallback='").Append(fallback).Append("'>");

            text.Append("<text id='1'>").Append(Escape(headline)).Append("</text>");

            for (int i = 0; i < lines && i < names.Count; i++)
            {
                text.Append("<text id='").Append(i + 2).Append("'>")
                    .Append(Escape(names[i])).Append("</text>");
            }

            text.Append("</binding>");
        }

        /// <summary>
        /// Chat titles are arbitrary text and go straight into markup, so an
        /// ampersand in someone's name would otherwise fail the whole document.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("'", "&apos;")
                        .Replace("\"", "&quot;");
        }
    }
}
