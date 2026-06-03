using System.Globalization;

namespace Option.Data.Ui.Services;

/// <summary>
/// Календарное окно агрегации Trade «до квартальной экспирации включительно».
/// Квартальная = последняя пятница ближайшего месяца из {Mar, Jun, Sep, Dec}
/// (опционы Deribit истекают 08:00 UTC). Расчёт чисто календарный — не зависит от
/// состава снимка, не ломается со временем. Дальние кварталы в окно не входят.
/// </summary>
public static class QuarterlyAggregation
{
    private const int DeribitExpiryHourUtc = 8;
    private static readonly int[] QuarterMonths = [3, 6, 9, 12];

    /// <summary>Последняя пятница месяца (год, месяц).</summary>
    public static DateTime LastFridayOfMonth(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        int diff = ((int)last.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        return last.AddDays(-diff);
    }

    /// <summary>
    /// Дата ближайшей квартальной экспирации (последняя пятница ближайшего месяца
    /// {3,6,9,12}), чьё истечение 08:00 UTC ≥ <paramref name="asOf"/>.
    /// </summary>
    public static DateTime NearestQuarterlyExpiry(DateTimeOffset asOf)
    {
        DateTime cursor = asOf.UtcDateTime;
        for (int i = 0; i < 24; i++)
        {
            DateTime m = cursor.AddMonths(i);
            if (Array.IndexOf(QuarterMonths, m.Month) < 0)
                continue;

            DateTime lastFri = LastFridayOfMonth(m.Year, m.Month);
            var expiryUtc = new DateTimeOffset(lastFri.Year, lastFri.Month, lastFri.Day,
                DeribitExpiryHourUtc, 0, 0, TimeSpan.Zero);
            if (expiryUtc >= asOf)
                return lastFri;
        }

        // Фолбэк (не достигается на нормальных данных): год вперёд.
        return LastFridayOfMonth(cursor.Year + 1, 3);
    }

    /// <summary>Код ближайшей квартальной для подписи, формат "dMMMyy" в верхнем регистре ("26JUN26").</summary>
    public static string NearestQuarterlyCode(DateTimeOffset asOf)
        => NearestQuarterlyExpiry(asOf).ToString("dMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>
    /// Подмножество кодов экспираций (уже неистёкших, с OI) с датой ≤ ближайшей квартальной,
    /// сортировка по дате ↑. Если пусто — ближайшая одиночная (минимальная по дате), чтобы
    /// расчёт не падал.
    /// </summary>
    public static List<string> WindowExpirations(IEnumerable<string> expirations, DateTimeOffset asOf)
    {
        DateTime quarterly = NearestQuarterlyExpiry(asOf).Date;

        List<(string Code, DateTime Date)> parsed = expirations
            .Select(e => (Code: e, Date: DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture).Date))
            .OrderBy(x => x.Date)
            .ToList();

        List<string> window = parsed
            .Where(x => x.Date <= quarterly)
            .Select(x => x.Code)
            .ToList();

        if (window.Count == 0 && parsed.Count > 0)
            window.Add(parsed[0].Code);

        return window;
    }
}
