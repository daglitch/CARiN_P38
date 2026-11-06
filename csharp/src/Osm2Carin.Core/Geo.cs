namespace Osm2Carin.Core;

/// <summary>CARiN coordinates (tools/osm2carin/geo.py). 1 unit = 1.8e-7 degree, K = 2e9/360 units per
/// degree; lat = Y/K, lon = X/K - 30 wrapped to [-180, 180); X in [0, 2e9).
/// Arithmetic mirrors the Python (floor modulo, round half to even) so results are identical.</summary>
public static class Geo
{
    public const double K = 2e9 / 360;
    public const double UnitDeg = 1.8e-7;
    public const double EarthR = 6371008.8;

    /// <summary>Python's float % (result has the sign of the divisor).</summary>
    public static double PyMod(double a, double b)
    {
        double r = a % b;
        if (r != 0 && (r < 0) != (b < 0)) r += b;
        return r;
    }

    public static long PyModL(long a, long b) { long r = a % b; return r != 0 && (r < 0) != (b < 0) ? r + b : r; }
    public static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }

    public static (double lon, double lat) ToLonLat(double x, double y) =>
        (PyMod(x * UnitDeg - 30.0 + 180.0, 360.0) - 180.0, y * UnitDeg);

    public static (int x, int y) FromLonLat(double lon, double lat)
    {
        long full = (long)Math.Round(360 * K);
        long x = (long)Math.Round(PyMod(lon + 30.0, 360.0) * K);
        return ((int)PyModL(x, full), (int)Math.Round(lat * K));
    }

    // numpy's deg2rad / rad2deg: x * (pi / 180), x * (180 / pi)
    public static double NpRadians(double d) => d * (Math.PI / 180.0);
    public static double NpDegrees(double r) => r * (180.0 / Math.PI);
    // Python's math.radians / math.degrees (CPython: x * degToRad, x / degToRad)
    const double DegToRad = Math.PI / 180.0;
    public static double MathDegrees(double r) => r / DegToRad;

    /// <summary>numpy-style haversine (model.py _haversine, tiles.py poly_len_m).</summary>
    public static double Haversine(double lon1, double lat1, double lon2, double lat2)
    {
        double p1 = NpRadians(lat1), p2 = NpRadians(lat2);
        double dp = p2 - p1, dl = NpRadians(lon2 - lon1);
        double sdp = Math.Sin(dp / 2), sdl = Math.Sin(dl / 2);
        double h = sdp * sdp + Math.Cos(p1) * Math.Cos(p2) * (sdl * sdl);
        return 2 * EarthR * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>model.py _bearing256 (numpy): round(degrees(atan2) % 360 * 256 / 360) &amp; 0xFF.</summary>
    public static byte Bearing256Np(double lon1, double lat1, double lon2, double lat2)
    {
        double p1 = NpRadians(lat1), p2 = NpRadians(lat2), dl = NpRadians(lon2 - lon1);
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (byte)((long)Math.Round(PyMod(NpDegrees(Math.Atan2(y, x)), 360) * 256 / 360) & 0xFF);
    }

    /// <summary>geo.py bearing256 (math module): the same formula with math.radians / math.degrees.</summary>
    public static byte Bearing256(double lon1, double lat1, double lon2, double lat2)
    {
        double p1 = lat1 * DegToRad, p2 = lat2 * DegToRad, dl = (lon2 - lon1) * DegToRad;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (byte)((long)Math.Round(PyMod(MathDegrees(Math.Atan2(y, x)), 360) * 256 / 360) & 0xFF);
    }

    public static bool PointInPoly(double x, double y, IReadOnlyList<(double x, double y)> poly)
    {
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            j = i;
        }
        return inside;
    }
}
