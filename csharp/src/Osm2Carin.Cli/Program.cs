using Osm2Carin.Core.Osm;
var path = args[0];
int nb = 0, nw = 0, nr = 0, nn = 0;
try
{
    foreach (var b in Pbf.Read(path, new PbfFilter { Ways = true, Relations = true, Nodes = true, Keys = null, NodeKeys = new() { "amenity" } }, 1))
    { nb++; nw += b.Ways.Count; nr += b.Relations.Count; nn += b.Nodes.Count; }
}
catch (Exception e) { Console.WriteLine($"block {nb}: {e}"); }
Console.WriteLine($"{nb} blocks, {nn} nodes, {nw} ways, {nr} relations");
