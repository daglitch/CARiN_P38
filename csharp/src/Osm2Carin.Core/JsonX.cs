using System.Globalization;
using System.Text.Json.Nodes;

namespace Osm2Carin.Core;

/// <summary>Numbers out of JsonNodes whatever CLR type they were stored as.</summary>
public static class JsonX
{
    public static double D(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);
    public static long L(JsonNode? n) => (long)D(n);
}
