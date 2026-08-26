using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    public partial class App : Application
    {
        public static PhoneApplicationFrame RootFrame { get; private set; }

        public App()
        {
            UnhandledException += Application_UnhandledException;
            InitializeComponent();
            InitializePhoneApplication();

            // Deactivated is not raised for an app with continuous background
            // execution - it keeps running, and RunningInBackground is raised in its
            // place. Without this the foreground flag stayed true the whole time the
            // app was off screen, so the chat that happened to be open went on
            // suppressing its own notifications.
            //
            // Subscribed here rather than in Launching because Launching only fires
            // on a cold start; the constructor runs once per process, which covers
            // being activated from tombstoning as well.
            PhoneApplicationService.Current.RunningInBackground += Application_RunningInBackground;

            StartXnaPump();

            if (Debugger.IsAttached)
            {
                Application.Current.Host.Settings.EnableFrameRateCounter = false;
            }
        }

        private void Application_Launching(object sender, LaunchingEventArgs e)
        {
            AppState.IsForeground = true;
            ApplyBackgroundLater();
        }

        /// <summary>
        /// Keeps XNA's audio alive.
        ///
        /// SoundEffect is XNA, and XNA in a Silverlight app has no game loop to run
        /// its internal message pump, so nothing plays until FrameworkDispatcher is
        /// pumped by hand. It fails silently, with no exception and no sound, which
        /// makes it an expensive thing to discover later.
        /// </summary>
        private void StartXnaPump()
        {
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(33);
            timer.Tick += delegate
            {
                try { Microsoft.Xna.Framework.FrameworkDispatcher.Update(); }
                catch (Exception) { }
            };
            timer.Start();
        }

        /// <summary>
        /// The app has left the screen but is still running, courtesy of the
        /// location subscription. Same bookkeeping as Deactivated - only one of the
        /// two is ever raised.
        /// </summary>
        private void Application_RunningInBackground(object sender, RunningInBackgroundEventArgs e)
        {
            AppState.IsForeground = false;
            AppState.OpenPeerId = 0;
        }

        private void Application_Activated(object sender, ActivatedEventArgs e)
        {
            AppState.IsForeground = true;
            ApplyBackgroundLater();
        }

        /// <summary>
        /// Registers background work after the app is on screen, never during
        /// launch or activation.
        ///
        /// ScheduledActionService and the location subscription are slow enough to
        /// matter, and anything that stalls here stalls the splash screen: the
        /// phone shows "Loading..." or "Resuming..." for as long as it takes.
        /// </summary>
        private void ApplyBackgroundLater()
        {
            RootFrame.Dispatcher.BeginInvoke(delegate
            {
                try { BackgroundControl.Apply(); }
                catch (Exception) { }
            });
        }

        /// <summary>
        /// The user pressed Home, or something else took the screen.
        ///
        /// The app stays registered for background work; only the foreground flag
        /// changes, so notifications start being shown for every chat rather than
        /// being suppressed for the one that was open.
        /// </summary>
        private void Application_Deactivated(object sender, DeactivatedEventArgs e)
        {
            AppState.IsForeground = false;
            AppState.OpenPeerId = 0;
        }

        /// <summary>
        /// The user pressed Back off the first page - a real exit.
        ///
        /// Everything stops, including background work. Leaving an agent registered
        /// after an explicit exit would keep waking the phone for an app the user
        /// has closed.
        /// </summary>
        private void Application_Closing(object sender, ClosingEventArgs e)
        {
            AppState.IsForeground = false;
            try { BackgroundControl.StopAll(); } catch (Exception) { }
            try { TelegramService.Disconnect(); } catch (Exception) { }
        }

        private void Application_UnhandledException(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            // Nothing swallows exceptions silently in this app. A white screen with
            // no explanation is the single most expensive failure mode to debug on a
            // phone, so an unhandled exception breaks into the debugger when one is
            // attached and is left to terminate loudly when one is not.
            if (Debugger.IsAttached) Debugger.Break();
        }

        private bool _phoneApplicationInitialized;

        private void InitializePhoneApplication()
        {
            if (_phoneApplicationInitialized) return;

            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInitializePhoneApplication;
            RootFrame.NavigationFailed += RootFrame_NavigationFailed;

            _phoneApplicationInitialized = true;
        }

        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInitializePhoneApplication;
        }

        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            if (Debugger.IsAttached) Debugger.Break();
        }
    }
}
