using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>Where the handful of choices live.</summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// Set while the switch is being put into its stored position.
        ///
        /// Assigning IsOn raises Toggled, so without this the act of showing the
        /// current setting would write it straight back - harmless here, and the
        /// kind of loop that is not harmless once a setting does something on
        /// change.
        /// </summary>
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            AutoLoadSwitch.IsOn = AppSettings.AutoLoadPhotos;
            _loading = false;
        }

        private void AutoLoad_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoLoadPhotos = AutoLoadSwitch.IsOn;
        }
    }
}
