using Stations.Core.Map;

namespace Stations.Core.Sim;

/// <summary>A polyline with cumulative distances and per-segment speed limits.</summary>
public sealed class RoutePath
{
    public readonly List<double> Xs = new();
    public readonly List<double> Ys = new();
    public readonly List<double> Cum = new();
    public readonly List<double> Spd = new();

    public double Length => Cum.Count > 0 ? Cum[^1] : 0;
    public int Count => Xs.Count;

    public static RoutePath From(RouteResult r)
    {
        var p = new RoutePath();
        p.Append(r);
        return p;
    }

    public void Append(RouteResult r)
    {
        for (int i = 0; i < r.Xs.Count; i++)
        {
            if (i == 0 && Xs.Count > 0) continue; // shared join point
            double x = r.Xs[i], y = r.Ys[i];
            int n = Xs.Count;
            if (n > 0)
            {
                Cum.Add(Cum[n - 1] + Util.Hypot(x - Xs[n - 1], y - Ys[n - 1]));
                Spd.Add(i - 1 < r.Spd.Count ? r.Spd[i - 1] : r.Spd[0]);
            }
            else
            {
                Cum.Add(0);
            }
            Xs.Add(x);
            Ys.Add(y);
        }
    }

    /// <summary>Index i of the segment containing distance s (Cum[i] &lt;= s &lt;= Cum[i+1]).</summary>
    public int SegAt(double s, int hint = 0)
    {
        int last = Cum.Count - 2;
        if (last < 0) return 0;
        int i = Math.Clamp(hint, 0, last);
        if (Cum[i] <= s)
        {
            while (i < last && Cum[i + 1] < s) i++;
            return i;
        }
        int lo = 0, hi = i;
        while (lo < hi)
        {
            int m = (lo + hi + 1) >> 1;
            if (Cum[m] <= s) lo = m;
            else hi = m - 1;
        }
        return lo;
    }

    public (double X, double Y, double Ang) PointAt(double s, int seg)
    {
        int n = Xs.Count;
        if (n == 1) return (Xs[0], Ys[0], 0);
        int i = Math.Min(seg, n - 2);
        double l = Cum[i + 1] - Cum[i];
        double t = l > 0 ? Math.Clamp((s - Cum[i]) / l, 0, 1) : 0;
        double dx = Xs[i + 1] - Xs[i], dy = Ys[i + 1] - Ys[i];
        return (Xs[i] + t * dx, Ys[i] + t * dy, Math.Atan2(dy, dx));
    }

    /// <summary>Estimated driving time between two distances for a vehicle with top speed vmax.</summary>
    public double TravelTime(double s0, double s1, double vmax)
    {
        double t = 0;
        int i = SegAt(s0);
        while (i < Spd.Count && Cum[i] < s1)
        {
            double a = Math.Max(Cum[i], s0), b = Math.Min(Cum[i + 1], s1);
            if (b > a) t += (b - a) / Math.Min(Spd[i], vmax);
            i++;
        }
        return t * 1.1; // acceleration and braking
    }
}
