using Android.Graphics;
namespace Plugin.Toasts
{
    using Android.App;
    using Android.Content;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Xml.Serialization;

    internal class NotificationBuilder
    {
        public static IDictionary<string, ManualResetEvent> ResetEvent = new Dictionary<string, ManualResetEvent>();
        public static IDictionary<string, NotificationResult> EventResult = new Dictionary<string, NotificationResult>();

        public const string NotificationId = "NOTIFICATION_ID";
        public const string DismissedClickIntent = "android.intent.action.DISMISSED";
        public const string OnClickIntent = "android.intent.action.CLICK";

        private int _count = 0;
        private object _lock = new object();
        private static NotificationReceiver _receiver;
        private IPlatformOptions _androidOptions;

        public void Init(Activity activity, IPlatformOptions androidOptions)
        {
            if (activity != null)
            {
                IntentFilter filter = new IntentFilter();
                filter.AddAction(DismissedClickIntent);
                filter.AddAction(OnClickIntent);

                _receiver = new NotificationReceiver();

                activity.RegisterReceiver(_receiver, filter);

                _androidOptions = androidOptions;
            }
        }

        public IList<INotification> GetDeliveredNotifications(Activity activity)
        {
            IList<INotification> list = new List<INotification>();
            if (activity != null)
            {
                if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.M)
                {
                    return new List<INotification>();
                }

                NotificationManager notificationManager = activity.GetSystemService(Context.NotificationService) as NotificationManager;

                foreach (var notification in notificationManager.GetActiveNotifications())
                {
                    list.Add(new Notification()
                    {
                        Id = notification.Id.ToString(),
                        Title = notification.Notification.Extras.GetString("android.title"),
                        Description = notification.Notification.Extras.GetString("android.text"),
                        Delivered = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(notification.Notification.When)
                    });
                }

            }
            return list;
        }

        private void ScheduleNotification(string id, INotificationOptions options)
        {
            if (!string.IsNullOrEmpty(id) && options != null)
            {
                var intent = new Intent(Application.Context, typeof(AlarmHandler)).SetAction("AlarmHandlerIntent" + id);

                var notification = new ScheduledNotification()
                {
                    AndroidOptions = (AndroidOptions)options.AndroidOptions,
                    ClearFromHistory = options.ClearFromHistory,
                    DelayUntil = options.DelayUntil,
                    Description = options.Description,
                    IsClickable = options.IsClickable,
                    Title = options.Title,
                };

                var serializedNotification = Serialize(notification);
                intent.PutExtra(AlarmHandler.NotificationKey, serializedNotification);
                intent.PutExtra(NotificationId, id);

                var pendingIntent = PendingIntent.GetBroadcast(Application.Context, 0, intent, PendingIntentFlags.CancelCurrent);
                var timeTriggered = ConvertToMilliseconds(options.DelayUntil.Value);
                var alarmManager = Application.Context.GetSystemService(Context.AlarmService) as AlarmManager;

                alarmManager.Set(AlarmType.RtcWakeup, timeTriggered, pendingIntent);
            }
        }

        private long ConvertToMilliseconds(DateTime notifyTime)
        {
            var utcTime = TimeZoneInfo.ConvertTimeToUtc(notifyTime);
            var epochDifference = (new DateTime(1970, 1, 1) - DateTime.MinValue).TotalSeconds;
            return utcTime.AddSeconds(-epochDifference).Ticks / 10000;
        }

        private string Serialize(ScheduledNotification options)
        {
            var xmlSerializer = new XmlSerializer(typeof(ScheduledNotification));
            using (var stringWriter = new StringWriter())
            {
                xmlSerializer.Serialize(stringWriter, options);
                return stringWriter.ToString();
            }
        }

        public INotificationResult Notify(Activity activity, INotificationOptions options)
        {
            INotificationResult notificationResult = null;
            if (activity != null && options != null)
            {
                var notificationId = _count;
                var id = _count.ToString();
                _count++;

                int smallIcon;

                if (options.AndroidOptions.SmallDrawableIcon.HasValue)
                    smallIcon = options.AndroidOptions.SmallDrawableIcon.Value;
                else if (_androidOptions.SmallIconDrawable.HasValue)
                    smallIcon = _androidOptions.SmallIconDrawable.Value;
                else
                    smallIcon = Android.Resource.Drawable.IcDialogInfo; // As last resort

                if (options.DelayUntil.HasValue)
                {
                    options.AndroidOptions.SmallDrawableIcon = smallIcon;
                    ScheduleNotification(id, options);
                    return new NotificationResult() { Action = NotificationAction.NotApplicable };
                }

                // Show Notification Right Now
                Intent dismissIntent = new Intent(DismissedClickIntent);
                dismissIntent.PutExtra(NotificationId, notificationId);

                PendingIntent pendingDismissIntent = PendingIntent.GetBroadcast(activity.ApplicationContext, 123, dismissIntent, 0);

                Intent clickIntent = new Intent(OnClickIntent);
                clickIntent.PutExtra(NotificationId, notificationId);

                // Add custom args
                if (options.CustomArgs != null)
                    foreach (var arg in options.CustomArgs)
                        clickIntent.PutExtra(arg.Key, arg.Value);

                PendingIntent pendingClickIntent = PendingIntent.GetBroadcast(activity.ApplicationContext, 123, clickIntent, 0);

                Android.App.Notification.Builder builder = new Android.App.Notification.Builder(activity)
                    .SetContentTitle(options.Title)
                    .SetContentText(options.Description)
                    .SetSmallIcon(smallIcon) // Must have small icon to display
                    .SetPriority((int)NotificationPriority.High) // Must be set to High to get Heads-up notification
                    .SetDefaults(NotificationDefaults.All) // Must also include vibrate to get Heads-up notification
                    .SetAutoCancel(true) // To allow click event to trigger delete Intent
                    .SetContentIntent(pendingClickIntent) // Must have Intent to accept the click                   
                    .SetDeleteIntent(pendingDismissIntent)
                    .SetColor(Color.ParseColor(options.AndroidOptions.HexColour));

                Android.App.Notification notification = builder.Build();

                NotificationManager notificationManager = activity.GetSystemService(Context.NotificationService) as NotificationManager;

                notificationManager.Notify(notificationId, notification);

                if (options.DelayUntil.HasValue)
                {
                    return new NotificationResult() { Action = NotificationAction.NotApplicable };
                }

                var timer = new Timer(x => TimerFinished(activity, id, options.ClearFromHistory), null, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(100));

                var resetEvent = new ManualResetEvent(false);
                ResetEvent.Add(id, resetEvent);

                resetEvent.WaitOne(); // Wait for a result

                notificationResult = EventResult[id];

                if (!options.IsClickable && notificationResult.Action == NotificationAction.Clicked)
                {
                    notificationResult.Action = NotificationAction.Dismissed;
                }

                if (EventResult.ContainsKey(id))
                {
                    EventResult.Remove(id);
                }
                if (ResetEvent.ContainsKey(id))
                {
                    ResetEvent.Remove(id);
                }

                // Dispose of Intents and Timer
                pendingClickIntent.Cancel();
                pendingDismissIntent.Cancel();
                timer.Dispose();

            }
            return notificationResult;
        }

        public void CancelAll(Activity activity)
        {
            if (activity != null)
            {
                using (NotificationManager notificationManager = activity.GetSystemService(Context.NotificationService) as NotificationManager)
                {
                    notificationManager.CancelAll();
                }
            }
        }

        private void TimerFinished(Activity activity, string id, bool cancel)
        {
            if (activity != null && !string.IsNullOrEmpty(id))
            {
                if (cancel) // Will clear from Notification Center
                {
                    using (NotificationManager notificationManager = activity.GetSystemService(Context.NotificationService) as NotificationManager)
                    {
                        notificationManager.Cancel(Convert.ToInt32(id));
                    }
                }

                if (ResetEvent.ContainsKey(id))
                {
                    if (EventResult != null)
                    {
                        EventResult.Add(id, new NotificationResult() { Action = NotificationAction.Timeout });
                    }
                    if (ResetEvent != null && ResetEvent.ContainsKey(id))
                    {
                        ResetEvent[id].Set();
                    }
                }
            }
        }

    }

    internal class NotificationReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            var notificationId = intent.Extras.GetInt(NotificationBuilder.NotificationId, -1);
            if (notificationId > -1)
            {
                switch (intent.Action)
                {
                    case NotificationBuilder.OnClickIntent:
                        if (NotificationBuilder.EventResult != null)
                        {
                            NotificationBuilder.EventResult.Add(notificationId.ToString(), new NotificationResult() { Action = NotificationAction.Clicked });
                        }
                        break;
                    default:
                    case NotificationBuilder.DismissedClickIntent:
                        if (NotificationBuilder.EventResult != null)
                        {
                            NotificationBuilder.EventResult.Add(notificationId.ToString(), new NotificationResult() { Action = NotificationAction.Dismissed });
                        }
                        break;
                }

                if (NotificationBuilder.ResetEvent != null && NotificationBuilder.ResetEvent.ContainsKey(notificationId.ToString()))
                {
                    NotificationBuilder.ResetEvent[notificationId.ToString()].Set();
                }
            }
        }
    }

}