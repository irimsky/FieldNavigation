using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace FieldNavigation;

[Flags]
public enum NoFlyRouteRule
{
    None = 0,
    InsideToInside = 1 << 0,
    InsideToOutside = 1 << 1,
    OutsideToInside = 1 << 2,
}

/// <summary>A map volume where travel must use ground navigation.</summary>
/// <remarks>The optional landing point stores world X/Z with a placeholder Y; landing resolves its floor.</remarks>
public readonly record struct NoFlyZone(
    uint TerritoryId,
    Vector3 Center,
    Vector3 HalfExtents,
    string Name,
    Vector3? LandingPoint = null,
    NoFlyRouteRule SpecialRules = NoFlyRouteRule.None)
{
    public bool Contains(Vector3 position) =>
        MathF.Abs(position.X - this.Center.X) <= this.HalfExtents.X
        && MathF.Abs(position.Y - this.Center.Y) <= this.HalfExtents.Y
        && MathF.Abs(position.Z - this.Center.Z) <= this.HalfExtents.Z;

    public bool IsRouteExempt(bool originInside, bool destinationInside)
    {
        if (!originInside && !destinationInside)
            return false;
        NoFlyRouteRule route = originInside
            ? destinationInside ? NoFlyRouteRule.InsideToInside : NoFlyRouteRule.InsideToOutside
            : NoFlyRouteRule.OutsideToInside;
        return (this.SpecialRules & route) != 0;
    }
}

/// <summary>A rectangular no-fly area defined by two in-game map coordinates (X, Y).</summary>
public readonly record struct NoFlyMapRectangle(
    uint TerritoryId,
    Vector2 FirstMapCoordinate,
    Vector2 SecondMapCoordinate,
    string Name,
    Vector2? LandingMapCoordinate = null,
    NoFlyRouteRule SpecialRules = NoFlyRouteRule.None);

/// <summary>A map rectangle whose configured boundary crossings must pass through a fixed airborne waypoint.</summary>
public readonly record struct FlightExitZone(
    uint TerritoryId,
    Vector2 FirstMapCoordinate,
    Vector2 SecondMapCoordinate,
    Vector2 ExitWaypointMapCoordinate,
    string Name,
    NoFlyRouteRule Routes = NoFlyRouteRule.InsideToOutside);

public sealed class NoFlyZoneCatalog(
    IDataManager dataManager,
    IEnumerable<uint>? placeNameIds = null,
    IEnumerable<NoFlyMapRectangle>? mapRectangles = null,
    IEnumerable<FlightExitZone>? flightExitZones = null)
{
    // Only IDs are configured. Geometry is read from planmap.lgb at runtime.
    private static readonly uint[] defaultPlaceNameIds =
    {
        // PlaceNameId 238：拉诺西亚外地 · 武伽玛罗武装矿山（Territory 180）
        238,
    };
    private static readonly NoFlyMapRectangle[] defaultMapRectangles =
    {
        // 试验区域：南萨纳兰 (27.4, 21.5) 到 (36.2, 17.2)，指定平面落点 (27.4, 20.9)。
        new(146, new Vector2(27.4f, 21.5f), new Vector2(36.2f, 17.2f),
            "试验矩形禁飞区·南萨纳兰", new Vector2(27.4f, 20.9f)),
    };
    private static readonly FlightExitZone[] defaultFlightExitZones =
    {
        // 叹息海进出中转：内到外及外到内均先经过地图坐标 (21.8, 18.5)。
        new(959, new Vector2(4.2f, 17.5f), new Vector2(39.2f, 4.1f),
            new Vector2(21.8f, 18.5f), "飞行进出中转·叹息海",
            NoFlyRouteRule.InsideToOutside | NoFlyRouteRule.OutsideToInside),
    };
    private readonly HashSet<uint> placeNameIds = new(placeNameIds ?? defaultPlaceNameIds);
    private readonly IReadOnlyList<NoFlyMapRectangle> mapRectangles =
        (mapRectangles ?? defaultMapRectangles).ToArray();
    private readonly IReadOnlyList<FlightExitZone> flightExitZones =
        (flightExitZones ?? defaultFlightExitZones).ToArray();
    private readonly Dictionary<uint, IReadOnlyList<NoFlyZone>> cache = [];

    public NoFlyZone? Find(uint territoryId, Vector3 position)
    {
        if (!this.cache.TryGetValue(territoryId, out var zones))
            this.cache[territoryId] = zones = this.Load(territoryId);
        foreach (NoFlyZone zone in zones)
        {
            if (zone.Contains(position))
                return zone;
        }

        return null;
    }

    /// <summary>Returns whether every no-fly zone touched by this route grants a special exemption.</summary>
    public bool IsRouteExempt(uint territoryId, Vector3 origin, Vector3 destination)
    {
        bool matched = false;
        foreach (NoFlyZone zone in this.Load(territoryId))
        {
            bool originInside = zone.Contains(origin);
            bool destinationInside = zone.Contains(destination);
            if (!originInside && !destinationInside)
                continue;
            matched = true;
            if (!zone.IsRouteExempt(originInside, destinationInside))
                return false;
        }

        return matched;
    }

    public Vector3? FindFlightExitWaypoint(uint territoryId, Vector3 origin, Vector3 destination)
    {
        if (!TravelGeometry.IsFinite(origin) || !TravelGeometry.IsFinite(destination)
            || !this.flightExitZones.Any(zone => zone.TerritoryId == territoryId))
            return null;
        var map = this.GetMap(territoryId);
        if (map is not { } mapRow || mapRow.SizeFactor == 0)
            return null;
        foreach (FlightExitZone zone in this.flightExitZones.Where(zone => zone.TerritoryId == territoryId))
        {
            Vector3 first = ConvertMapCoordinate(mapRow, zone.FirstMapCoordinate);
            Vector3 second = ConvertMapCoordinate(mapRow, zone.SecondMapCoordinate);
            Vector3 min = Vector3.Min(first, second);
            Vector3 max = Vector3.Max(first, second);
            bool originInside = origin.X >= min.X && origin.X <= max.X && origin.Z >= min.Z && origin.Z <= max.Z;
            bool destinationInside = destination.X >= min.X && destination.X <= max.X && destination.Z >= min.Z && destination.Z <= max.Z;
            NoFlyRouteRule route = originInside
                ? destinationInside ? NoFlyRouteRule.InsideToInside : NoFlyRouteRule.InsideToOutside
                : destinationInside ? NoFlyRouteRule.OutsideToInside : NoFlyRouteRule.None;
            if (route != NoFlyRouteRule.None && (zone.Routes & route) != 0)
                return ConvertMapCoordinate(mapRow, zone.ExitWaypointMapCoordinate);
        }
        return null;
    }

    public IReadOnlyList<NoFlyZone> Load(uint territoryId)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>()!.GetRowOrDefault(territoryId);
        if (territory is not { } row)
            return [];
        List<NoFlyZone> zones = [];
        string bg = row.Bg.ToString();
        int level = bg.LastIndexOf("/level/", StringComparison.Ordinal);
        if (level >= 0)
        {
            string directory = bg[..(level + "/level/".Length)];
            var file = dataManager.GetFile<LgbFile>($"bg/{directory}planmap.lgb");
            if (file is not null)
            {
                var names = dataManager.GetExcelSheet<PlaceName>()!;
                zones.AddRange(file.Layers.SelectMany(layer => layer.InstanceObjects)
                    .Where(instance => instance.Object is LayerCommon.MapRangeInstanceObject range
                                       && this.placeNameIds.Contains(range.PlaceNameBlock))
                    .Select(instance =>
                    {
                        var range = (LayerCommon.MapRangeInstanceObject)instance.Object!;
                        uint id = range.PlaceNameBlock;
                        return new NoFlyZone(territoryId,
                            new Vector3(instance.Transform.Translation.X, instance.Transform.Translation.Y, instance.Transform.Translation.Z),
                            new Vector3(instance.Transform.Scale.X, instance.Transform.Scale.Y, instance.Transform.Scale.Z),
                            names.GetRowOrDefault(id)?.Name.ToString() ?? $"PlaceName {id}");
                    }));
            }
        }

        var map = this.GetMap(territoryId);
        if (map is { } mapRow && mapRow.SizeFactor != 0)
        {
            foreach (NoFlyMapRectangle rectangle in this.mapRectangles.Where(rectangle => rectangle.TerritoryId == territoryId))
                zones.Add(CreateMapRectangleZone(rectangle, mapRow));
        }

        return zones;
    }

    private Map? GetMap(uint territoryId)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        return territory is { } row ? dataManager.GetExcelSheet<Map>()?.GetRowOrDefault(row.Map.RowId) : null;
    }

    public static NoFlyZone CreateMapRectangleZone(NoFlyMapRectangle rectangle, Map map)
    {
        Vector3 first = ConvertMapCoordinate(map, rectangle.FirstMapCoordinate);
        Vector3 second = ConvertMapCoordinate(map, rectangle.SecondMapCoordinate);
        Vector3 min = Vector3.Min(first, second);
        Vector3 max = Vector3.Max(first, second);
        Vector3? landingPoint = rectangle.LandingMapCoordinate is { } landingCoordinate
            ? ConvertMapCoordinate(map, landingCoordinate)
            : null;
        return new(
            rectangle.TerritoryId,
            new Vector3((min.X + max.X) * 0.5f, 0f, (min.Z + max.Z) * 0.5f),
            new Vector3((max.X - min.X) * 0.5f, float.MaxValue, (max.Z - min.Z) * 0.5f),
            rectangle.Name,
            landingPoint,
            rectangle.SpecialRules);
    }

    public static Vector3 ConvertMapCoordinate(Map map, Vector2 coordinate) => new(
        (coordinate.X - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetX,
        0f,
        (coordinate.Y - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetY);
}
