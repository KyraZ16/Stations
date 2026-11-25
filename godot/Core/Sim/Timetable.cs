namespace Stations.Core.Sim;

/// <summary>
/// A line's timetable: service hours plus a target headway per daily period.
/// Hours are decimal (5.5 = 05:30); End may exceed 24 for service past midnight.
/// </summary>
public sealed class Timetable
{
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Headway in seconds per period; 0 = no service in that period.</summary>
    public double[] Headways { get; set; } = Array.Empty<double>();

    public static readonly (string Name, double To)[] Periods =
    {
        ("Frühverkehr", 6),
        ("HVZ morgens", 9),
        ("Tagesverkehr", 15),
        ("HVZ nachmittags", 19),
        ("Abendverkehr", double.PositiveInfinity),
    };

    /// <summary>Selectable headways in seconds.</summary>
    public static readonly double[] HeadwayOptions = { 300, 360, 450, 600, 720, 900, 1200, 1800, 3600 };

    public static Timetable Default() => Default(ModeId.Bus);

    /// <summary>
    /// Standard timetable per mode (early, morning peak, day, afternoon peak, evening):
    /// bus/tram 20/10/15/10/20 min, U-Bahn 10/5/7.5/5/10 min, S-Bahn 20/10/10/10/20 min, regional trains hourly with half-hourly peaks.
    /// </summary>
    public static Timetable Default(ModeId mode) => mode switch
    {
        ModeId.Metro => new() { Start = 4.5, End = 24.5, Headways = new double[] { 600, 300, 450, 300, 600 } },
        ModeId.SBahn => new() { Start = 4.5, End = 24.5, Headways = new double[] { 1200, 600, 600, 600, 1200 } },
        ModeId.Train => new() { Start = 5, End = 23.5, Headways = new double[] { 3600, 1800, 3600, 1800, 3600 } },
        _ => new() { Start = 5, End = 24, Headways = new double[] { 1200, 600, 900, 600, 1200 } },
    };

    public Timetable Clone() => new() { Start = Start, End = End, Headways = (double[])Headways.Clone() };

    /// <summary>[from, to) in hours for period i, or null if the period is empty.</summary>
    public (double From, double To)? PeriodBounds(int i)
    {
        double from = i == 0 ? Start : Math.Max(Start, Periods[i - 1].To);
        double to = i == Periods.Length - 1 ? End : Math.Min(End, Periods[i].To);
        return to > from ? (from, to) : null;
    }

    /// <summary>Index of the period active at game time t (seconds), or -1 outside service hours.</summary>
    public int PeriodAt(double t)
    {
        double h = (((t % 86400) + 86400) % 86400) / 3600;
        if (h < Start && h + 24 < End) h += 24;
        if (h < Start || h >= End) return -1;
        for (int i = 0; i < Periods.Length; i++)
        {
            var b = PeriodBounds(i);
            if (b != null && h >= b.Value.From && h < b.Value.To) return i;
        }
        return -1;
    }

    public double TargetHeadway(double t)
    {
        int i = PeriodAt(t);
        if (i < 0) return double.PositiveInfinity;
        double hw = Headways[i];
        return hw > 0 ? hw : double.PositiveInfinity;
    }

    public static int VehiclesFor(double cycleTime, double headway) =>
        double.IsFinite(headway) ? (int)Math.Ceiling(cycleTime / headway - 0.05) : 0;

    /// <summary>Most vehicles needed at any time of the day.</summary>
    public int PeakVehicles(double cycleTime)
    {
        int n = 0;
        for (int i = 0; i < Periods.Length; i++)
            if (PeriodBounds(i) != null && Headways[i] > 0) n = Math.Max(n, VehiclesFor(cycleTime, Headways[i]));
        return n;
    }

    /// <summary>Vehicle hours per day (for estimating driver needs).</summary>
    public double VehicleHours(double cycleTime)
    {
        double sum = 0;
        for (int i = 0; i < Periods.Length; i++)
        {
            var b = PeriodBounds(i);
            if (b != null && Headways[i] > 0) sum += (b.Value.To - b.Value.From) * VehiclesFor(cycleTime, Headways[i]);
        }
        return sum;
    }

    public static string FmtHour(double h)
    {
        int hh = (int)Math.Floor(h) % 24;
        int mm = (int)Math.Round(h % 1 * 60);
        return $"{hh:00}:{mm:00}";
    }
}
