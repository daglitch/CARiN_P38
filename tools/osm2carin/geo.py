"""CARiN coordinates (see carin-disc-findings.md, "Coordinates (exact)").

Global unit: 1 unit = 1.8e-7 degree (K = 2e9 / 360 units per degree).
    lat = Y / K,  lon = X / K - 30, wrapped to [-180, 180)
X is in [0, 360 K) = [0, 2e9), so it always fits a signed 32-bit int. For the US
(lon < -30) X / K = lon + 390, which is where tools/carin.py's "- 390" comes from.
Inside a road parcel, X = x1 + 64 * u, Y = y1 + 64 * v.
"""
import math

K = 2e9 / 360
UNIT_DEG = 1.8e-7
LOCAL_UNIT = 64
EARTH_R = 6371008.8


def to_lonlat(x, y):
    """Works on scalars and numpy arrays."""
    return (x * UNIT_DEG - 30.0 + 180.0) % 360.0 - 180.0, y * UNIT_DEG


def from_lonlat(lon, lat):
    """-> (X, Y) global units, rounded."""
    return round(((lon + 30.0) % 360.0) * K) % round(360 * K), round(lat * K)


def point_in_poly(x, y, poly):
    inside = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def haversine_m(lon1, lat1, lon2, lat2):
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp, dl = p2 - p1, math.radians(lon2 - lon1)
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * EARTH_R * math.asin(min(1.0, math.sqrt(a)))


def bearing256(lon1, lat1, lon2, lat2):
    """Compass bearing from point 1 to point 2 in 1/256 turn (0 = north, clockwise)."""
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dl = math.radians(lon2 - lon1)
    y = math.sin(dl) * math.cos(p2)
    x = math.cos(p1) * math.sin(p2) - math.sin(p1) * math.cos(p2) * math.cos(dl)
    return round(math.degrees(math.atan2(y, x)) % 360 * 256 / 360) & 0xFF
