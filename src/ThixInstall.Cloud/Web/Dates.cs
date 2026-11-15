using System.Globalization;

namespace ThixInstall.Cloud.Web;

/// <summary>
/// Dates as the pages show them: in the time zone of the server (<c>TZ</c>, Europe/Berlin in the image), in German.
/// The database keeps UTC.
/// </summary>
public static class Dates
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Months = ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sep.", "Okt.", "Nov.", "Dez."];
    private static readonly string[] Weekdays = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];

    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.Local;

    public static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateOnly Today => DateOnly.FromDateTime(Local(DateTime.UtcNow));

    /// <summary>"Heute", "Gestern", the weekday within a week, else "3. Nov. 2026": the heading of a group of clips.</summary>
    public static string DayHeading(DateTime utc)
    {
        var day = DateOnly.FromDateTime(Local(utc));
        var today = Today;
        var days = today.DayNumber - day.DayNumber;
        return days switch
        {
            0 => "Heute",
            1 => "Gestern",
            > 1 and < 7 => Weekdays[(int)day.DayOfWeek],
            _ => $"{day.Day}. {Months[day.Month - 1]}{(day.Year == today.Year ? string.Empty : " " + day.Year.ToString(Invariant))}",
        };
    }

    /// <summary>The time of a clip in a list: "14:28" today, "Gestern, 17:56", else "3. Nov., 09:14".</summary>
    public static string Short(DateTime utc)
    {
        var local = Local(utc);
        var day = DateOnly.FromDateTime(local);
        var today = Today;
        var time = local.ToString("HH:mm", Invariant);
        return (today.DayNumber - day.DayNumber) switch
        {
            0 => time,
            1 => "Gestern, " + time,
            _ => $"{day.Day}. {Months[day.Month - 1]}{(day.Year == today.Year ? string.Empty : " " + day.Year.ToString(Invariant))}, {time}",
        };
    }

    /// <summary>A full stamp: "14.11.2026, 13:47".</summary>
    public static string Stamp(DateTime? utc) => utc is null ? "–" : Local(utc.Value).ToString("dd.MM.yyyy, HH:mm", Invariant);

    /// <summary>"vor 3 Minuten", "vor 2 Stunden", "vor 5 Tagen" (for the last contact of a device).</summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (span < TimeSpan.FromMinutes(1))
        {
            return "gerade eben";
        }

        if (span < TimeSpan.FromHours(1))
        {
            var minutes = (int)span.TotalMinutes;
            return minutes == 1 ? "vor 1 Minute" : $"vor {minutes} Minuten";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            return hours == 1 ? "vor 1 Stunde" : $"vor {hours} Stunden";
        }

        var total = (int)span.TotalDays;
        return total == 1 ? "vor 1 Tag" : $"vor {total} Tagen";
    }

    /// <summary>The ISO day of a date in the local zone (the key of a group in a list).</summary>
    public static string DayKey(DateTime utc) => DateOnly.FromDateTime(Local(utc)).ToString("yyyy-MM-dd", Invariant);
}
