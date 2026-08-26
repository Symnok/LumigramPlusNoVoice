using System;
using System.Collections.Generic;
using Concentus.Structs;
using Lumigram.Audio;
using Microsoft.Xna.Framework.Audio;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Plays a Telegram voice message.
    ///
    /// Voice notes are Opus in OGG, and Windows Phone 8.1 can play neither: there is
    /// no Opus support in the platform at all. So the file is demuxed here, decoded
    /// to plain 16-bit PCM, and handed to XNA as a sound effect.
    ///
    /// Decoded in full before playing rather than streamed. It costs a moment on a
    /// slow phone and a few hundred kilobytes of memory for a message of ordinary
    /// length, and in exchange there is no real-time deadline anywhere: a decoder
    /// that cannot keep up merely takes longer to start, instead of stuttering. That
    /// is the whole reason voice messages are approachable on this hardware when
    /// calls are not.
    /// </summary>
    internal static class VoicePlayer
    {
        /// <summary>Opus always decodes to 48 kHz, whatever rate it was encoded at.</summary>
        private const int SampleRate = 48000;

        /// <summary>Room for the largest frame Opus can produce: 120 ms at 48 kHz.</summary>
        private const int MaxFrameSamples = 5760;

        private static SoundEffectInstance _playing;

        /// <summary>Stops whatever is playing. Safe when nothing is.</summary>
        public static void Stop()
        {
            SoundEffectInstance instance = _playing;
            _playing = null;
            if (instance == null) return;

            try { instance.Stop(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Decodes an OGG Opus file and plays it, replacing anything already playing.
        ///
        /// Returns null on success, or a message worth showing. The caller is on the
        /// UI thread; decoding is the slow part and belongs off it, so callers should
        /// use <see cref="Decode"/> on a background thread and <see cref="Play"/>
        /// here when it is done.
        /// </summary>
        public static byte[] Decode(byte[] oggFile, out int channels)
        {
            OpusStream stream = OggOpus.Read(oggFile);
            channels = stream.Channels;

            var decoder = new OpusDecoder(SampleRate, stream.Channels);
            var pcm = new short[MaxFrameSamples * stream.Channels];
            var output = new List<byte>(oggFile.Length * 8);

            int skipRemaining = stream.PreSkip;

            foreach (byte[] packet in stream.Packets)
            {
                int samples;
                try
                {
                    samples = decoder.Decode(packet, 0, packet.Length, pcm, 0,
                                             MaxFrameSamples, false);
                }
                catch (Exception)
                {
                    // One bad packet is a moment of noise, not a reason to lose the
                    // rest of the message.
                    continue;
                }

                if (samples <= 0) continue;

                int first = 0;
                if (skipRemaining > 0)
                {
                    int skipped = Math.Min(skipRemaining, samples);
                    skipRemaining -= skipped;
                    first = skipped;
                }

                for (int i = first * stream.Channels; i < samples * stream.Channels; i++)
                {
                    short sample = pcm[i];
                    output.Add((byte)(sample & 0xFF));
                    output.Add((byte)((sample >> 8) & 0xFF));
                }
            }

            return output.ToArray();
        }

        /// <summary>Plays decoded PCM. Must run on the UI thread.</summary>
        public static void Play(byte[] pcm, int channels)
        {
            Stop();

            if (pcm == null || pcm.Length < 4) return;

            var effect = new SoundEffect(pcm, SampleRate,
                                         channels == 2 ? AudioChannels.Stereo
                                                       : AudioChannels.Mono);

            SoundEffectInstance instance = effect.CreateInstance();
            _playing = instance;
            instance.Play();
        }
    }
}
