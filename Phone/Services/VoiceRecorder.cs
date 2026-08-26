using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Records from the microphone.
    ///
    /// XNA's Microphone is the only capture API a Silverlight app on this platform
    /// has. It hands back 16-bit mono PCM at 16 kHz, which is exactly what a voice
    /// message wants, and it delivers it in buffers of at least 100 ms - a latency
    /// that would rule it out for a call and does not matter in the slightest here.
    ///
    /// Like the rest of XNA's audio it only works while FrameworkDispatcher is being
    /// pumped; App.xaml.cs does that.
    /// </summary>
    internal static class VoiceRecorder
    {
        /// <summary>
        /// Longest recording accepted, in seconds.
        ///
        /// Raw audio is held in memory while recording - a minute costs under two
        /// megabytes at this rate - and the encode that follows is the slow part, so
        /// an accidental recording left running is worth stopping rather than
        /// discovering later.
        /// </summary>
        private const int MaxSeconds = 120;

        private static Microphone _microphone;
        private static MemoryStream _captured;
        private static byte[] _buffer;
        private static DateTime _startedUtc;

        public static bool IsRecording { get { return _microphone != null; } }

        /// <summary>
        /// How loud the last buffer was, from 0 to 1.
        ///
        /// Shown on screen while recording. A running clock only proves a timer is
        /// running; a level that moves when you speak is the only thing that proves
        /// the microphone is actually capturing, which is the question anyone
        /// staring at a record button is really asking.
        /// </summary>
        public static double Level { get; private set; }

        /// <summary>How long the current recording has been running.</summary>
        public static TimeSpan Elapsed
        {
            get { return _microphone == null ? TimeSpan.Zero : DateTime.UtcNow - _startedUtc; }
        }

        public static int SampleRate
        {
            get { return _microphone != null ? _microphone.SampleRate : 16000; }
        }

        /// <summary>Starts recording. Returns null, or why it could not start.</summary>
        public static string Start()
        {
            if (_microphone != null) return null;

            try
            {
                Microphone microphone = Microphone.Default;
                if (microphone == null) return "This phone has no microphone available.";

                // The shortest buffer the platform accepts, so the elapsed time on
                // screen keeps up with reality.
                microphone.BufferDuration = TimeSpan.FromMilliseconds(100);

                _buffer = new byte[microphone.GetSampleSizeInBytes(microphone.BufferDuration)];
                _captured = new MemoryStream();

                microphone.BufferReady += OnBufferReady;
                microphone.Start();

                _microphone = microphone;
                _startedUtc = DateTime.UtcNow;
                return null;
            }
            catch (Exception ex)
            {
                Cleanup();
                return ex.Message;
            }
        }

        /// <summary>
        /// Stops recording and returns what was captured as 16-bit samples.
        /// Returns null when nothing usable was recorded.
        /// </summary>
        public static short[] Stop(out int sampleCount, out int sampleRate)
        {
            sampleCount = 0;
            sampleRate = SampleRate;

            Microphone microphone = _microphone;
            MemoryStream captured = _captured;

            if (microphone == null || captured == null) { Cleanup(); return null; }

            try
            {
                microphone.Stop();

                // Whatever has been captured since the last buffer would otherwise be
                // lost, which clips the last fraction of a second off every message.
                microphone.GetData(_buffer);
                captured.Write(_buffer, 0, _buffer.Length);
            }
            catch (Exception)
            {
                // Nothing worth reporting: what was already captured still stands.
            }

            byte[] bytes = captured.ToArray();
            Cleanup();

            if (bytes.Length < 2) return null;

            sampleCount = bytes.Length / 2;
            var pcm = new short[sampleCount];
            Buffer.BlockCopy(bytes, 0, pcm, 0, sampleCount * 2);
            return pcm;
        }

        /// <summary>Abandons a recording without producing anything.</summary>
        public static void Cancel()
        {
            Microphone microphone = _microphone;
            if (microphone != null)
            {
                try { microphone.Stop(); }
                catch (Exception) { }
            }
            Cleanup();
        }

        /// <summary>True once the recording has run past what is sensible to send.</summary>
        public static bool ReachedLimit
        {
            get { return IsRecording && Elapsed.TotalSeconds >= MaxSeconds; }
        }

        private static void OnBufferReady(object sender, EventArgs e)
        {
            Microphone microphone = _microphone;
            MemoryStream captured = _captured;
            if (microphone == null || captured == null) return;

            try
            {
                int read = microphone.GetData(_buffer);
                captured.Write(_buffer, 0, read);
                MeasureLevel(_buffer, read);
            }
            catch (Exception)
            {
                // A dropped buffer is a gap in the audio, not a reason to stop.
            }
        }

        /// <summary>
        /// Peak level of one buffer, eased downwards.
        ///
        /// Peak rather than average, because speech is mostly quiet and an average
        /// barely leaves the floor. The decay stops the bar flickering to nothing
        /// between syllables, which reads as a fault rather than as speech.
        /// </summary>
        private static void MeasureLevel(byte[] buffer, int count)
        {
            int loudest = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                int sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                if (sample < 0) sample = -sample;
                if (sample > loudest) loudest = sample;
            }

            double level = loudest / 32768.0;
            Level = level > Level ? level : Level * 0.6 + level * 0.4;
        }

        private static void Cleanup()
        {
            Microphone microphone = _microphone;
            if (microphone != null)
            {
                try { microphone.BufferReady -= OnBufferReady; }
                catch (Exception) { }
            }

            _microphone = null;
            _captured = null;
            _buffer = null;
            Level = 0;
        }
    }
}
