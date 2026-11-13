namespace GwsBusinessSuite.Web;

// Key-less 3D terrain for Overwatch (13-11-26): the open Terrain Tiles on AWS (Terrarium encoding,
// Mapzen/AWS open data, no key), Web Mercator z/x/y. Zoom 14 is enough for a globe.
public static class OverwatchTerrain
{
    public const int MaxZoom = 14;

    public static bool IsTile(int z, int x, int y) =>
        z is >= 0 and <= MaxZoom && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    public static string TileUrl(int z, int x, int y) =>
        $"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";
}
