using System;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>
    /// The launch page, which only decides where to go: the chat list if there is a
    /// signed-in session stored, otherwise sign-in.
    ///
    /// It does no work of its own and removes itself from the back stack, so Back
    /// from the chat list leaves the app rather than landing on a blank router.
    /// </summary>
    public partial class MainPage : PhoneApplicationPage
    {
        private bool _routed;

        public MainPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_routed) return;
            _routed = true;

            PhoneSession session = PhoneSession.Load();
            bool signedIn = session != null && session.SignedIn;

            Dispatcher.BeginInvoke(delegate
            {
                NavigationService.Navigate(new Uri(
                    signedIn ? "/ChatsPage.xaml" : "/LoginPage.xaml", UriKind.Relative));
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (NavigationService.CanGoBack) NavigationService.RemoveBackEntry();
        }
    }
}
