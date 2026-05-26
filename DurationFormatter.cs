namespace Activity_Tracker
{
    public static class DurationFormatter
    {
        public static string Format(int totalSeconds)
        {
            switch (totalSeconds)
            {
                case < 60:
                    return $"{totalSeconds}s";

                case < 3600:
                    var minutes = totalSeconds / 60;
                    var seconds = totalSeconds % 60;
                    return seconds == 0 ? $"{minutes}m" : $"{minutes}m {seconds}s";

                default:
                    var hours = totalSeconds / 3600;
                    var remainingSeconds = totalSeconds % 3600;
                    var mins = remainingSeconds / 60;
                    var secs = remainingSeconds % 60;

                    if (mins == 0 && secs == 0) return $"{hours}h";
                    if (secs == 0) return $"{hours}h {mins}m";
                    return $"{hours}h {mins}m {secs}s";
            }
        }

        public static string Format(TimeSpan duration)
        {
            return Format((int)duration.TotalSeconds);
        }

        public static string FormatCompact(int totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                : string.Format("{0}:{1:00}", ts.Minutes, ts.Seconds);
        }

        public static string FormatForTelegram(int totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);

            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            else if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            else
                return $"{ts.Seconds}s";
        }
    }
}