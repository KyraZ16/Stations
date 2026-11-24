namespace Stations.Core;

/// <summary>A square map area: center and half side length in meters.</summary>
public sealed record Area(string Name, double Lat, double Lon, double Radius);

/// <summary>
/// Local equirectangular projection around a center point.
/// World units are meters, x grows east, y grows south (screen orientation).
/// </summary>
public sealed class Projection
{
    private const double EarthR = 6378137;
    private const double Deg = Math.PI / 180;

    public double Lat0 { get; }
    public double Lon0 { get; }
    public double Kx { get; }
    public double Ky { get; }

    public Projection(double lat0, double lon0)
    {
        Lat0 = lat0;
        Lon0 = lon0;
        Kx = Math.Cos(lat0 * Deg) * EarthR * Deg;
        Ky = EarthR * Deg;
    }

    public double X(double lon) => (lon - Lon0) * Kx;
    public double Y(double lat) => -(lat - Lat0) * Ky;
    public (double Lat, double Lon) ToLatLon(double x, double y) => (Lat0 - y / Ky, Lon0 + x / Kx);

    /// <summary>Returns (south, west, north, east).</summary>
    public static (double S, double W, double N, double E) BBox(Area a)
    {
        double dLat = a.Radius / (EarthR * Deg);
        double dLon = a.Radius / (Math.Cos(a.Lat * Deg) * EarthR * Deg);
        return (a.Lat - dLat, a.Lon - dLon, a.Lat + dLat, a.Lon + dLon);
    }
}
