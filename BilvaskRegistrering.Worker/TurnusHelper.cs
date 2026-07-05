using System;

namespace BilvaskRegistrering.Worker;

/// <summary>
/// Shift (turnus) helper used by Worker UI.
/// All inputs/outputs are LOCAL time (Europe/Oslo).
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

    public static (int Nr, DateTime StartLocal, DateTime EndLocal) GetCurrentShiftWindow(DateTime nowLocal)
    {
        var sh = GetShift(nowLocal);
        return (sh.Nr, sh.StartLocal, sh.EndLocal);
    }

    public static ShiftInfo GetShift(DateTime tLocal)
    {
        var date = tLocal.Date;
        var time = tLocal.TimeOfDay;
        var dow = tLocal.DayOfWeek;
        var isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;

        // Skift 3 (22:00–06:00) priority
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
            var s1Start = date.AddHours(7);
            var s1End = date.AddHours(14).AddMinutes(30);
            if (time >= TimeSpan.FromHours(7) && time < TimeSpan.FromHours(14) + TimeSpan.FromMinutes(30))
                return new ShiftInfo(1, "Skift 1", s1Start, s1End);

            if (time >= TimeSpan.FromHours(6) && time < TimeSpan.FromHours(7))
            {
                var start = date.AddDays(-1).AddHours(22);
                var end = date.AddHours(6);
                return new ShiftInfo(3, "Skift 3", start, end);
            }

            if (time >= TimeSpan.FromHours(14) + TimeSpan.FromMinutes(30) && time < TimeSpan.FromHours(15) + TimeSpan.FromMinutes(40))
                return new ShiftInfo(1, "Skift 1", s1Start, s1End);

            var s2Start = date.AddHours(15).AddMinutes(40);
            var s2End = date.AddHours(23);
            if (time >= TimeSpan.FromHours(15) + TimeSpan.FromMinutes(40) && time < TimeSpan.FromHours(22))
                return new ShiftInfo(2, "Skift 2", s2Start, s2End);

            return new ShiftInfo(1, "Skift 1", s1Start, s1End);
        }

        var w1Start = date.AddHours(9);
        var w1End = date.AddHours(17);
        if (time >= TimeSpan.FromHours(9) && time < TimeSpan.FromHours(17))
            return new ShiftInfo(1, "Skift 1", w1Start, w1End);

        if (time >= TimeSpan.FromHours(6) && time < TimeSpan.FromHours(9))
        {
            var start = date.AddDays(-1).AddHours(22);
            var end = date.AddHours(6);
            return new ShiftInfo(3, "Skift 3", start, end);
        }

        if (time >= TimeSpan.FromHours(17) && time < TimeSpan.FromHours(22))
            return new ShiftInfo(1, "Skift 1", w1Start, w1End);

        return new ShiftInfo(0, "Ingen skift", date, date.AddDays(1));
    }
}
