using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>
    /// Full-screen image with zoom, and saving to the phone's gallery.
    ///
    /// Zoom is done with a CompositeTransform driven by manipulation events rather
    /// than a ScrollViewer: WP8.1 Silverlight has no built-in pinch-zoom viewer, and
    /// a ScrollViewer only pans. Pinch gives the scale, and the same handler pans
    /// when only one finger is down.
    ///
    /// The image is loaded from the media cache by path - it has already been
    /// downloaded by the time this page opens, so there is no network work here.
    /// </summary>
    public partial class ImageViewerPage : PhoneApplicationPage
    {
        private const double MinScale = 1.0;
        private const double MaxScale = 6.0;
        private const double DoubleTapScale = 2.5;

        private string _path;
        private string _name;
        private byte[] _data;

        private double _scale = 1.0;
        private double _scaleAtStart = 1.0;

        public ImageViewerPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (_data != null) return;

            string value;
            if (NavigationContext.QueryString.TryGetValue("path", out value))
                _path = Uri.UnescapeDataString(value);
            if (NavigationContext.QueryString.TryGetValue("name", out value))
                _name = Uri.UnescapeDataString(value);

            if (string.IsNullOrEmpty(_path)) { StatusText.Text = "No image."; return; }

            _data = MediaCache.Read(_path);
            if (_data == null) { StatusText.Text = "Could not read the image."; return; }

            try
            {
                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(_data)) bitmap.SetSource(ms);
                Zoomed.Source = bitmap;
                Reset();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Cannot display: " + ex.GetType().Name;
            }
        }

        private void Reset()
        {
            _scale = 1.0;
            Transform.ScaleX = 1.0;
            Transform.ScaleY = 1.0;
            Transform.TranslateX = 0;
            Transform.TranslateY = 0;
        }

        private void Viewport_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (e.PinchManipulation != null)
            {
                // Scale relative to where the pinch began, so it does not drift.
                double factor = Separation(e.PinchManipulation.Current) /
                                Math.Max(1.0, Separation(e.PinchManipulation.Original));

                _scale = Clamp(_scaleAtStart * factor, MinScale, MaxScale);
                Apply();
            }
            else if (_scale > MinScale)
            {
                // Panning only means something once zoomed in.
                Transform.TranslateX += e.DeltaManipulation.Translation.X;
                Transform.TranslateY += e.DeltaManipulation.Translation.Y;
                ClampTranslation();
            }

            e.Handled = true;
        }

        /// <summary>
        /// Distance between the two fingers. PinchContactPoints gives the points
        /// themselves, not a distance, so the ratio has to be computed here.
        /// </summary>
        private static double Separation(PinchContactPoints points)
        {
            double dx = points.PrimaryContact.X - points.SecondaryContact.X;
            double dy = points.PrimaryContact.Y - points.SecondaryContact.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void Viewport_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            _scaleAtStart = _scale;
            if (_scale <= MinScale) Reset();
        }

        private void Viewport_DoubleTap(object sender, GestureEventArgs e)
        {
            _scale = _scale > MinScale + 0.01 ? MinScale : DoubleTapScale;
            _scaleAtStart = _scale;

            if (_scale <= MinScale) Reset();
            else
            {
                // Zoom towards the point that was tapped, not the corner.
                Point p = e.GetPosition(Viewport);
                Transform.TranslateX = (Viewport.ActualWidth / 2 - p.X) * (_scale - 1);
                Transform.TranslateY = (Viewport.ActualHeight / 2 - p.Y) * (_scale - 1);
                Apply();
            }
            e.Handled = true;
        }

        private void Apply()
        {
            Transform.ScaleX = _scale;
            Transform.ScaleY = _scale;
            ClampTranslation();
        }

        /// <summary>Keeps the image from being dragged off the screen entirely.</summary>
        private void ClampTranslation()
        {
            double slackX = Viewport.ActualWidth * (_scale - 1) / 2;
            double slackY = Viewport.ActualHeight * (_scale - 1) / 2;

            Transform.TranslateX = Clamp(Transform.TranslateX, -slackX, slackX);
            Transform.TranslateY = Clamp(Transform.TranslateY, -slackY, slackY);
        }

        private static double Clamp(double value, double low, double high)
        {
            if (value < low) return low;
            if (value > high) return high;
            return value;
        }

        /// <summary>
        /// Saves to the phone's Pictures library.
        ///
        /// SavePictureToCameraRoll rather than SavePicture: the camera roll is where
        /// people look for something they just kept, and it is the one the Photos
        /// hub surfaces first.
        /// </summary>
        private void Viewport_Hold(object sender, GestureEventArgs e)
        {
            if (_data == null) return;

            if (MessageBox.Show("Save this image to your pictures?", "Save",
                                MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;

            try
            {
                string name = _name;
                if (string.IsNullOrEmpty(name))
                    name = "lumigram-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jpg";

                using (var library = new Microsoft.Xna.Framework.Media.MediaLibrary())
                using (var ms = new MemoryStream(_data))
                {
                    library.SavePictureToCameraRoll(name, ms);
                }

                StatusText.Text = "Saved to your pictures.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not save: " + ex.Message;
            }
        }
    }
}
