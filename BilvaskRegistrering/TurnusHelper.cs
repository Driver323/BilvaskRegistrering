using System;

namespace BilvaskRegistrering;

/// <summary>
/// Shift (turnus) helper used by Admin UI.
/// All inputs/outputs are LOCAL time (Europe/Oslo) as seen by operators.
/// </summary>
internal static class TurnusHelper
{
    internal readonly struct ShiftInfo
    {
        public ShiftInfo(int nr, string display, DateTime startLocal, DateTime endLocal)
        {
            Nr = nr;
            Display = display;
            StartLocal = startLocal;
            EndLocal = endLocal;
        }

        public int Nr { get; }
        public string Display { get; }
        public DateTime StartLocal { get; }
        public DateTime EndLocal { get; }
    }

    /// <summary>
    /// Returns current shift number and window. If the current time is in a "gap" between shifts,
    /// we return the most recent shift window to avoid an empty view.
    /// </summary>
    public static (int Nr, DateTime StartLocal, DateTime EndLocal) GetCurrentShiftWindow(DateTime nowLocal)
    {
        var sh = GetShift(nowLocal);
        return (sh.Nr, sh.StartLocal, sh.EndLocal);
    }

    /// <summary>
    /// Computes shift for a local timestamp.
    /// Rules:
    /// - Skift 3 always: 22:00–06:00 (crosses midnight)
    /// - Mon–Fri: Skift 1 07:00–14:30, Skift 2 15:40–23:00 (but 22:00–06:00 belongs to Skift 3)
    /// - Sat/Sun: Skift 1 09:00–17:00, Skift 3 22:00–06:00
    /// - In gaps (e.g., 14:30–15:40), we map to the previous shift window.
    /// </summary>
    public static ShiftInfo GetShift(DateTime tLocal)
    {
        var date = tLocal.Date;
        var time = tLocal.TimeOfDay;
        var dow = tLocal.DayOfWeek;
        var isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;

        // Skift 3 (cross midnight) has priority
        if (time >= TimeSpan.FromHours(22))
        {
            var start = date.AddHours(22);
            var end = date.AddDays(1).AddHours(6);
            return new ShiftInfo(3, "Skift 3", start, end);
        }

        if (time < TimeSpan.FromHours(6))
        {
            var start = date.AddDays(-1).AddHours(22);
            var end = date.AddHours(6);
            return new ShiftInfo(3, "Skift 3", start, end);
        }

        if (!isWeekend)
        {
            // Weekday shift 1
            var s1Start = date.AddHours(7);
            var s1End = date.AddHours(14).AddMinutes(30);
            if (time >= TimeSpan.FromHours(7) && time < TimeSpan.FromHours(14) + TimeSpan.FromMinutes(30))
                return new ShiftInfo(1, "Skift 1", s1Start, s1End);

            // Gap after skift 3 (06:00–07:00): map to the just-finished Skift 3
            if (time >= TimeSpan.FromHours(6) && time < TimeSpan.FromHours(7))
            {
                var start = date.AddDays(-1).AddHours(22);
                var end = date.AddHours(6);
                return new ShiftInfo(3, "Skift 3", start, end);
            }

            // Gap between shift 1 and shift 2 (14:30–15:40): map to Skift 1
            if (time >= TimeSpan.FromHours(14) + TimeSpan.FromMinutes(30) && time < TimeSpan.FromHours(15) + TimeSpan.FromMinutes(40))
                return new ShiftInfo(1, "Skift 1", s1Start, s1End);

            // Weekday shift 2 (15:40–23:00, but 22:00+ already handled as Skift 3)
            var s2Start = date.AddHours(15).AddMinutes(40);
            var s2End = date.AddHours(23);
            if (time >= TimeSpan.FromHours(15) + TimeSpan.FromMinutes(40) && time < TimeSpan.FromHours(22))
                return new ShiftInfo(2, "Skift 2", s2Start, s2End);

            // Default to previous shift in any other weekday gap
            return new ShiftInfo(1, "Skift 1", s1Start, s1End);
        }

        // Weekend: shift 1 09:00–17:00
        var w1Start = date.AddHours(9);
        var w1End = date.AddHours(17);
        if (time >= TimeSpan.FromHours(9) && time < TimeSpan.FromHours(17))
            return new ShiftInfo(1, "Skift 1", w1Start, w1End);

        // Weekend gaps: 06:00–09:00 map to previous Skift 3
        if (time >= TimeSpan.FromHours(6) && time < TimeSpan.FromHours(9))
        {
            var start = date.AddDays(-1).AddHours(22);
            var end = date.AddHours(6);
            return new ShiftInfo(3, "Skift 3", start, end);
        }

        // 17:00–22:00 map to Skift 1
        if (time >= TimeSpan.FromHours(17) && time < TimeSpan.FromHours(22))
            return new ShiftInfo(1, "Skift 1", w1Start, w1End);

        // Fallback
        return new ShiftInfo(0, "Ingen skift", date, date.AddDays(1));
    }
}
