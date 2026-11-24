using System.Globalization;

namespace Stations.Core;

/// <summary>Binary min-heap of int values with double priorities.</summary>
public sealed class MinHeap
{
    private readonly List<double> _keys = new();
    private readonly List<int> _vals = new();

    /// <summary>Priority of the value most recently returned by <see cref="Pop"/>.</summary>
    public double LastKey { get; private set; }

    public int Count => _keys.Count;

    public void Push(double key, int val)
    {
        int i = _keys.Count;
        _keys.Add(key);
        _vals.Add(val);
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (_keys[p] <= key) break;
            _keys[i] = _keys[p];
            _vals[i] = _vals[p];
            i = p;
        }
        _keys[i] = key;
        _vals[i] = val;
    }

    public int Pop()
    {
        int top = _vals[0];
        LastKey = _keys[0];
        int last = _keys.Count - 1;
        double lastK = _keys[last];
        int lastV = _vals[last];
        _keys.RemoveAt(last);
        _vals.RemoveAt(last);
        int n = _keys.Count;
        if (n > 0)
        {
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= n) break;
                int r = l + 1;
                int c = r < n && _keys[r] < _keys[l] ? r : l;
                if (_keys[c] >= lastK) break;
                _keys[i] = _keys[c];
                _vals[i] = _vals[c];
                i = c;
            }
            _keys[i] = lastK;
            _vals[i] = lastV;
        }
        return top;
    }
}

public static class Util
{
    public static readonly string[] LineColors =
    {
        "#e6332a", "#0a6ebd", "#00963f", "#f39200", "#8e44ad", "#00a3c4",
        "#c2185b", "#6d4c41", "#7cb342", "#283593", "#ef6c00", "#00897b",
    };

    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>Distance from point p to segment a-b and the projection parameter t in [0, 1].</summary>
    public static (double D, double T) DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
        t = Math.Clamp(t, 0, 1);
        return (Hypot(px - (ax + t * dx), py - (ay + t * dy)), t);
    }

    /// <summary>Like JavaScript parseFloat: reads the leading number of a string ("30 mph" → 30).</summary>
    public static double ParseLeadingNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        s = s.Trim();
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.' || (end == 0 && (s[end] == '-' || s[end] == '+')))) end++;
        return end > 0 && double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : double.NaN;
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(double v) => v.ToString("#,##0 €", De);
    public static string Num(double v) => v.ToString("#,##0", De);

    public static string Clock(double t)
    {
        double s = ((t % 86400) + 86400) % 86400;
        return $"{(int)(s / 3600):00}:{(int)(s % 3600 / 60):00}";
    }

    public static string Duration(double sec)
    {
        if (double.IsInfinity(sec) || double.IsNaN(sec)) return "–";
        if (sec < 60) return $"{Math.Round(sec)} s";
        int m = (int)Math.Round(sec / 60);
        return m < 60 ? $"{m} min" : $"{m / 60} h {m % 60} min";
    }

    public static string Dist(double m) =>
        m < 1000 ? $"{Math.Round(m)} m" : (m / 1000).ToString("0.0", De) + " km";
}
