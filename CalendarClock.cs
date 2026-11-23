using System;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private readonly Func<DateTime> localNow;
        private DateTime calendarDay;
        private DateTime Today { get { return localNow().Date; } }

        private static DateTime MonthStart(DateTime date)
        {
            return new DateTime(date.Year, date.Month, 1);
        }

        private void ReturnToCurrentMonth()
        {
            month = MonthStart(Today);
            RefreshMonth();
        }

        private void WireCalendarClock()
        {
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher);
            bool closed = false;
            Action synchronize = delegate
            {
                if (closed) return;
                timer.Stop();
                try { Safely(RefreshCalendarDay); }
                finally
                {
                    DateTime now = localNow();
                    double remaining = (now.Date.AddDays(1) - now).TotalMilliseconds;
                    timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(remaining, 60000)));
                    timer.Start();
                }
            };
            EventHandler clockChanged = delegate { window.Dispatcher.BeginInvoke(synchronize); };
            PowerModeChangedEventHandler wake = delegate(object sender, PowerModeChangedEventArgs args)
            {
                if (args.Mode == PowerModes.Resume) window.Dispatcher.BeginInvoke(synchronize);
            };
            timer.Tick += delegate { synchronize(); };
            window.Loaded += delegate { synchronize(); };
            window.Activated += delegate { synchronize(); };
            window.SourceInitialized += delegate
            {
                SystemEvents.TimeChanged += clockChanged;
                SystemEvents.PowerModeChanged += wake;
            };
            window.Closed += delegate
            {
                closed = true;
                SystemEvents.TimeChanged -= clockChanged;
                SystemEvents.PowerModeChanged -= wake;
                timer.Stop();
            };
        }

        private void RefreshCalendarDay()
        {
            DateTime current = Today;
            if (current == calendarDay) return;
            DateTime currentMonth = MonthStart(current);
            DateTime previousMonth = MonthStart(calendarDay);
            if (currentMonth != previousMonth)
            {
                if (month == previousMonth) month = currentMonth;
                foreach (string page in pageMonths.Where(pair => pair.Value == previousMonth).Select(pair => pair.Key).ToArray())
                    pageMonths[page] = currentMonth;
                UpdateClosures();
                if (currentPage == "Closures") LoadClosurePage();
            }
            RefreshMonth();
            RefreshChat();
            calendarDay = current;
        }
    }
}
