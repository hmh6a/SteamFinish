using System.Globalization;

namespace SteamFinish.Core.Formatting;

/// <summary>Shared number formatting, so the window and the Telegram messages always agree.</summary>
public static class Humanize
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Sizes use binary steps with the short labels Steam itself shows, so "22.5 GB" here is the
    /// same 22.5 GB the Steam client displays for the same download.
    /// </summary>
    public static string Bytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return string.Create(CultureInfo.CurrentCulture, $"{value.ToString(format, CultureInfo.CurrentCulture)} {ByteUnits[unit]}");
    }

    /// <summary>
    /// Transfer rate in megabits per second, matching Steam's own read-out. <paramref name="zeroText"/>
    /// separates "nothing is moving" from "nothing has been measured yet".
    /// </summary>
    public static string Rate(double bytesPerSecond, string zeroText = "—")
    {
        if (bytesPerSecond <= 0)
        {
            return zeroText;
        }

        var megabits = bytesPerSecond * 8 / 1_000_000;
        var format = megabits >= 100 ? "0" : "0.0";
        return string.Create(CultureInfo.CurrentCulture, $"{megabits.ToString(format, CultureInfo.CurrentCulture)} Mbps");
    }

    /// <summary>Clock-style remaining time, as Steam shows it: <c>03:47:33</c>.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The wall-clock time a transfer is expected to finish at. A day marker is added when it lands
    /// on a later date, so "01:30" cannot be mistaken for later today.
    /// </summary>
    public static string FinishTime(DateTimeOffset when)
    {
        var days = (when.LocalDateTime.Date - DateTime.Now.Date).Days;
        var time = when.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);

        return days switch
        {
            <= 0 => time,
            1 => $"{time} +1d",
            _ => $"{time} +{days}d",
        };
    }

    public static string Percent(double? fraction) =>
        fraction is { } value
            ? string.Create(CultureInfo.CurrentCulture, $"{Math.Clamp(value, 0, 1) * 100:0}%")
            : "—";

    /// <summary>A text progress bar for the Telegram messages.</summary>
    public static string Bar(double? fraction, int width = 10)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction ?? 0, 0, 1) * width);
        return new string('▰', filled) + new string('▱', width - filled);
    }
}
