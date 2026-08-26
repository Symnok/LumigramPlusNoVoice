using System;
using Microsoft.Phone.Scheduler;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Turns the chosen background mode on and off.
    ///
    /// Windows Phone 8.1 offers an ordinary app two ways to do anything while it is
    /// not on screen, and they are very different:
    ///
    ///   Periodic  - a scheduled agent, woken roughly every 30 minutes for about
    ///               25 seconds. Cheap, but messages arrive in batches long after
    ///               they were sent, and the OS may unschedule the agent at will.
    ///   AlwaysOn  - continuous background execution, granted to apps that hold a
    ///               location subscription. The connection stays up and messages
    ///               arrive as they are sent, at a real cost in battery, and the
    ///               phone shows its location indicator while it is on.
    ///
    /// There is no option here that is both live and free; that is the platform,
    /// not a design choice.
    /// </summary>
    internal static class BackgroundControl
    {
        private const string TaskName = "LumigramAgent";
        private const string TaskDescription = "Checks for new Telegram messages.";

        private static BackgroundMode _applied = (BackgroundMode)(-1);
        private static bool _idleDetectionDisabled;

        /// <summary>
        /// Applies the stored setting, doing nothing if it is already in effect.
        ///
        /// Idempotence matters more than it looks. Under LocationTracking the app
        /// keeps running while backgrounded, so tapping the tile reactivates a live
        /// instance and this runs again - and the previous version tore the
        /// location subscription down and rebuilt it every time, while toggling
        /// idle detection off and on. ApplicationIdleDetectionMode may only be
        /// changed once per session and throws afterwards, so that churn turned
        /// every resume into an exception on the activation path.
        /// </summary>
        public static void Apply()
        {
            BackgroundMode mode = AppSettings.Current.Background;
            if (mode == _applied) return;

            if (_applied == BackgroundMode.Periodic && mode != BackgroundMode.Periodic)
                StopPeriodic();
            if (_applied == BackgroundMode.AlwaysOn && mode != BackgroundMode.AlwaysOn)
                StopAlwaysOn();

            if (mode == BackgroundMode.Periodic) StartPeriodic();
            else if (mode == BackgroundMode.AlwaysOn) StartAlwaysOn();

            _applied = mode;
        }

        /// <summary>Stops everything - used when the user exits with Back.</summary>
        public static void StopAll()
        {
            StopPeriodic();
            StopAlwaysOn();
            _applied = BackgroundMode.Disabled;
        }

        /// <summary>
        /// Registers the periodic agent.
        ///
        /// An existing registration must be removed first: re-adding a live task
        /// throws rather than replacing it. The OS also disables agents that crash
        /// or overrun twice, so a failure here is worth reporting rather than
        /// swallowing entirely.
        /// </summary>
        public static string StartPeriodic()
        {
            try
            {
                var existing = ScheduledActionService.Find(TaskName) as PeriodicTask;
                if (existing != null) ScheduledActionService.Remove(TaskName);

                var task = new PeriodicTask(TaskName);
                task.Description = TaskDescription;

                ScheduledActionService.Add(task);

                // Only has an effect on a developer-unlocked phone, and shortens the
                // interval to about 30 seconds so the agent can actually be tested.
#if DEBUG
                ScheduledActionService.LaunchForTest(TaskName, TimeSpan.FromSeconds(30));
#endif
                return null;
            }
            catch (InvalidOperationException ex)
            {
                // Usually: the user has disabled background tasks for this app, or
                // the phone already has its maximum number of agents.
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static void StopPeriodic()
        {
            try
            {
                if (ScheduledActionService.Find(TaskName) != null)
                    ScheduledActionService.Remove(TaskName);
            }
            catch (Exception) { }
        }

        private static Windows.Devices.Geolocation.Geolocator _geolocator;

        /// <summary>
        /// What the geolocator last said about itself, and how many position
        /// reports have arrived.
        ///
        /// Worth surfacing because the failure is silent: if the report count never
        /// moves, the system is not treating this as active tracking and will
        /// suspend the app the moment it leaves the foreground, however the
        /// manifest is written.
        /// </summary>
        public static string Status { get; private set; }

        public static int Reports { get; private set; }

        /// <summary>Compact form for the diagnostics line.</summary>
        public static string TrackingStatus
        {
            get { return (string.IsNullOrEmpty(Status) ? "off" : Status) + "/" + Reports; }
        }

        /// <summary>
        /// Starts continuous background execution.
        ///
        /// The position is never read, stored or sent. Holding a live Geolocator
        /// subscription is simply the mechanism by which Windows Phone keeps an app
        /// running once it leaves the foreground, and it is what keeps the MTProto
        /// connection alive so messages arrive as they are sent.
        ///
        /// Two things have to line up or this silently does nothing: the manifest
        /// must declare BackgroundExecution/LocationTracking *inside DefaultTask*,
        /// and ID_CAP_LOCATION must be granted. Declared as a child of App instead,
        /// the manifest fails validation outright.
        ///
        /// Idle detection is disabled as well, so the connection also survives the
        /// screen locking.
        /// </summary>
        public static void StartAlwaysOn()
        {
            // Only ever once per session: Windows Phone throws on any later change.
            if (!_idleDetectionDisabled)
            {
                try
                {
                    Microsoft.Phone.Shell.PhoneApplicationService.Current.ApplicationIdleDetectionMode =
                        Microsoft.Phone.Shell.IdleDetectionMode.Disabled;
                    _idleDetectionDisabled = true;
                }
                catch (Exception)
                {
                    // Already disabled, or not permitted. Either way, do not retry.
                    _idleDetectionDisabled = true;
                }
            }

            try
            {
                if (_geolocator != null) return;

                _geolocator = new Windows.Devices.Geolocation.Geolocator();
                _geolocator.DesiredAccuracy =
                    Windows.Devices.Geolocation.PositionAccuracy.Default;

                // Frequent enough to count as active tracking, which is the whole
                // point: the system grants continuous execution only while the app
                // is genuinely tracking. Coarse settings look like the thrifty
                // choice and are in fact the broken one - with a long interval and
                // a wide movement threshold the OS stops considering this tracking
                // at all and suspends the app on HOME, taking the connection and
                // every notification with it.
                _geolocator.MovementThreshold = 0;
                _geolocator.ReportInterval = 5000;          // milliseconds

                _geolocator.PositionChanged += OnPositionChanged;
                _geolocator.StatusChanged += OnStatusChanged;
                Status = "starting";
            }
            catch (Exception ex)
            {
                // Location refused - the app still runs, just not in the background.
                Status = "unavailable: " + ex.Message;
                _geolocator = null;
            }
        }

        public static void StopAlwaysOn()
        {
            try
            {
                if (_geolocator != null)
                {
                    _geolocator.PositionChanged -= OnPositionChanged;
                    _geolocator.StatusChanged -= OnStatusChanged;
                    _geolocator = null;
                    Status = "stopped";
                }
            }
            catch (Exception) { }

            // Idle detection is deliberately not re-enabled. Windows Phone allows
            // ApplicationIdleDetectionMode to be set once per session and throws on
            // any later change; it resets by itself when the app next starts.
        }

        private static void OnPositionChanged(
            Windows.Devices.Geolocation.Geolocator sender,
            Windows.Devices.Geolocation.PositionChangedEventArgs args)
        {
            // The position is of no interest - an unsubscribed Geolocator does not
            // keep the app alive, so the handler exists in order to be a subscriber.
            // Only the count is kept, as evidence that tracking is really running.
            Reports++;
            Status = "tracking";
        }

        private static void OnStatusChanged(
            Windows.Devices.Geolocation.Geolocator sender,
            Windows.Devices.Geolocation.StatusChangedEventArgs args)
        {
            Status = args.Status.ToString();
        }

        public static string Describe(BackgroundMode mode)
        {
            switch (mode)
            {
                case BackgroundMode.Periodic:
                    return "Wakes about every 30 minutes. Messages arrive in batches.";
                case BackgroundMode.AlwaysOn:
                    return "Stays connected in the background using location access. " +
                           "Messages arrive as they are sent. Uses more battery, and the " +
                           "phone shows the location indicator.";
                default:
                    return "Nothing runs once Lumigram is closed.";
            }
        }
    }
}
