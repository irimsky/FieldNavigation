using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace FieldNavigation;

/// <summary>Which route categories a no-fly definition handles specially.</summary>
[Flags]
public enum NoFlyRouteRule
{
    None = 0,
    InsideToInside = 1 << 0,
    InsideToOutside = 1 << 1,
    OutsideToInside = 1 << 2,
    OutsideToOutside = 1 << 3,
}

/// <summary>A rectangular area defined by two in-game map coordinates (X, Y).</summary>
public readonly record struct NoFlyMapRectangle(Vector2 FirstMapCoordinate, Vector2 SecondMapCoordinate);

/// <summary>
/// One no-fly configuration. The area is the union of its <see cref="Rectangles"/> and the volumes
/// resolved from <see cref="PlaceNameIds"/>: being inside any of them means being inside the zone.
/// </summary>
/// <param name="RouteRule">Route categories that do not force a landing. Other categories use ground navigation.</param>
/// <param name="TransferPoint">
/// World X/Z with a placeholder Y. Exempt routes that cross the boundary fly through this point first;
/// routes that are not exempt land on it before continuing on the ground. Null lets the navigator choose.
/// </param>
public sealed record NoFlyZoneDefinition(
    uint TerritoryId,
    string Name,
    IReadOnlyList<NoFlyMapRectangle> Rectangles,
    IReadOnlyList<uint> PlaceNameIds,
    NoFlyRouteRule RouteRule = NoFlyRouteRule.None,
    Vector3? TransferPoint = null);

/// <summary>A resolved no-fly volume in world space.</summary>
public readonly record struct NoFlyZone(
    uint TerritoryId,
    Vector3 Center,
    Vector3 HalfExtents,
    string Name,
    Vector3? TransferPoint = null,
    NoFlyRouteRule SpecialRules = NoFlyRouteRule.None)
{
    /// <summary>Boundary allowance so a character stopped just outside a no-fly border still counts as inside.</summary>
    public const float BoundaryMargin = 2f;

    public bool Contains(Vector3 position) => this.Contains(position, 0f);

    public bool Contains(Vector3 position, float margin) =>
        MathF.Abs(position.X - this.Center.X) <= this.HalfExtents.X + margin
        && MathF.Abs(position.Y - this.Center.Y) <= this.HalfExtents.Y + margin
        && MathF.Abs(position.Z - this.Center.Z) <= this.HalfExtents.Z + margin;

    /// <summary>True when the padded volume touches the straight segment, even with both endpoints outside.</summary>
    public bool IntersectsRoute(Vector3 origin, Vector3 destination, float margin = 0f)
    {
        if (!TravelGeometry.IsFinite(origin) || !TravelGeometry.IsFinite(destination))
            return false;
        if (this.Contains(origin, margin) || this.Contains(destination, margin))
            return true;
        Vector3 min = this.Center - this.HalfExtents - new Vector3(margin);
        Vector3 max = this.Center + this.HalfExtents + new Vector3(margin);
        Vector3 direction = destination - origin;
        float enter = 0f, exit = 1f;
        return ClipAxis(origin.X, direction.X, min.X, max.X, ref enter, ref exit)
            && ClipAxis(origin.Y, direction.Y, min.Y, max.Y, ref enter, ref exit)
            && ClipAxis(origin.Z, direction.Z, min.Z, max.Z, ref enter, ref exit);

        static bool ClipAxis(float start, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (MathF.Abs(delta) < 1e-6f) return start >= min && start <= max;
            float first = (min - start) / delta, second = (max - start) / delta;
            if (first > second) (first, second) = (second, first);
            enter = MathF.Max(enter, first); exit = MathF.Min(exit, second);
            return enter <= exit;
        }
    }

    /// <summary>Route category of a straight segment, based on where its endpoints sit relative to this volume.</summary>
    public static NoFlyRouteRule Route(bool originInside, bool destinationInside) =>
        originInside
            ? destinationInside ? NoFlyRouteRule.InsideToInside : NoFlyRouteRule.InsideToOutside
            : destinationInside ? NoFlyRouteRule.OutsideToInside : NoFlyRouteRule.OutsideToOutside;

    public bool IsRouteExempt(bool originInside, bool destinationInside) =>
        (this.SpecialRules & Route(originInside, destinationInside)) != 0;
}

public sealed class NoFlyZoneCatalog(
    IDataManager dataManager,
    IEnumerable<NoFlyZoneDefinition>? definitions = null)
{
    // Rectangles are authored in map coordinates; transfer points are world coordinates.
    private static readonly NoFlyZoneDefinition[] defaultDefinitions =
    [
        // PlaceNameId 238：拉诺西亚外地 · 武伽玛罗武装矿山（Territory 180），几何运行时从 planmap.lgb 读取。
        new(180, "武伽玛罗武装矿山", [], [238]),
        // 试验区域：地图坐标 (27.4, 21.5) 到 (36.2, 17.2)；落点地图坐标 (27.4, 20.9) = 世界 (296, 0, -29)。
        new(146, "试验矩形禁飞区·南萨纳兰",
            [new(new Vector2(27.4f, 21.5f), new Vector2(36.2f, 17.2f))],
            [], NoFlyRouteRule.None, new Vector3(296f, 0f, -29f)),
        // 叹息海进出中转：跨边界先经过地图坐标 (21.8, 18.5) = 世界 (16, 0, -149)，区内飞行不强制落地。
        new(959, "飞行进出中转·叹息海",
            [new(new Vector2(4.2f, 17.5f), new Vector2(39.2f, 4.1f))],
            [], NoFlyRouteRule.InsideToInside | NoFlyRouteRule.InsideToOutside
                | NoFlyRouteRule.OutsideToInside | NoFlyRouteRule.OutsideToOutside,
            new Vector3(16f, 0f, -149f)),
    ];
    private readonly IReadOnlyList<NoFlyZoneDefinition> definitions =
        (definitions ?? defaultDefinitions).ToArray();
    private readonly Dictionary<uint, IReadOnlyList<NoFlyZone>> cache = [];

    public NoFlyZone? Find(uint territoryId, Vector3 position) => this.Find(territoryId, position, 0f);

    public NoFlyZone? Find(uint territoryId, Vector3 position, float margin)
    {
        foreach (NoFlyZone zone in this.Load(territoryId))
        {
            if (zone.Contains(position, margin))
                return zone;
        }

        return null;
    }

    /// <summary>Returns a no-fly zone touched by the padded straight route unless that route is exempt.</summary>
    public NoFlyZone? FindRouteBlocking(uint territoryId, Vector3 origin, Vector3 destination, float margin = 0f)
    {
        foreach (NoFlyZone zone in this.Load(territoryId))
        {
            if (!zone.IntersectsRoute(origin, destination, margin)
                || zone.IsRouteExempt(zone.Contains(origin, margin), zone.Contains(destination, margin)))
                continue;
            return zone;
        }

        return null;
    }

    /// <summary>Returns whether every no-fly zone touched by this route grants a special exemption.</summary>
    public bool IsRouteExempt(uint territoryId, Vector3 origin, Vector3 destination)
    {
        foreach (NoFlyZone zone in this.Load(territoryId))
        {
            if (!zone.IntersectsRoute(origin, destination))
                continue;
            if (!zone.IsRouteExempt(zone.Contains(origin), zone.Contains(destination)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the transfer point of the boundary this flight route is about to cross,
    /// or null when the route needs no airborne transfer.
    /// </summary>
    public Vector3? FindFlightExitWaypoint(uint territoryId, Vector3 origin, Vector3 destination)
    {
        if (!TravelGeometry.IsFinite(origin) || !TravelGeometry.IsFinite(destination))
            return null;
        foreach (NoFlyZone zone in this.Load(territoryId))
        {
            if (zone.TransferPoint is not { } waypoint || zone.SpecialRules == NoFlyRouteRule.None)
                continue;
            if (!zone.IntersectsRoute(origin, destination))
                continue;
            NoFlyRouteRule route = NoFlyZone.Route(zone.Contains(origin), zone.Contains(destination));
            // Moving within a zone or passing by it is not a boundary crossing, so it needs no transfer.
            if (route is NoFlyRouteRule.InsideToInside or NoFlyRouteRule.OutsideToOutside)
                continue;
            if ((zone.SpecialRules & route) != 0)
                return waypoint;
        }

        return null;
    }

    public IReadOnlyList<NoFlyZone> Load(uint territoryId)
    {
        if (this.cache.TryGetValue(territoryId, out IReadOnlyList<NoFlyZone>? cached))
            return cached;
        List<NoFlyZone> zones = [];
        Map? map = this.GetMap(territoryId);
        foreach (NoFlyZoneDefinition definition in this.definitions)
        {
            if (definition.TerritoryId != territoryId)
                continue;
            if (definition.PlaceNameIds.Count > 0)
                zones.AddRange(this.LoadPlaceNameZones(definition));
            if (definition.Rectangles.Count == 0 || map is not { } mapRow || mapRow.SizeFactor == 0)
                continue;
            foreach (NoFlyMapRectangle rectangle in definition.Rectangles)
                zones.Add(CreateMapRectangleZone(definition, rectangle, mapRow));
        }

        this.cache[territoryId] = zones;
        return zones;
    }

    private IEnumerable<NoFlyZone> LoadPlaceNameZones(NoFlyZoneDefinition definition)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>()!.GetRowOrDefault(definition.TerritoryId);
        if (territory is not { } row)
            yield break;
        string bg = row.Bg.ToString();
        int level = bg.LastIndexOf("/level/", StringComparison.Ordinal);
        if (level < 0)
            yield break;
        string directory = bg[..(level + "/level/".Length)];
        var file = dataManager.GetFile<LgbFile>($"bg/{directory}planmap.lgb");
        if (file is null)
            yield break;
        var names = dataManager.GetExcelSheet<PlaceName>()!;
        foreach (var instance in file.Layers.SelectMany(layer => layer.InstanceObjects))
        {
            if (instance.Object is not LayerCommon.MapRangeInstanceObject range
                || !definition.PlaceNameIds.Contains(range.PlaceNameBlock))
                continue;
            yield return new NoFlyZone(definition.TerritoryId,
                new Vector3(instance.Transform.Translation.X, instance.Transform.Translation.Y, instance.Transform.Translation.Z),
                new Vector3(instance.Transform.Scale.X, instance.Transform.Scale.Y, instance.Transform.Scale.Z),
                string.IsNullOrEmpty(definition.Name)
                    ? names.GetRowOrDefault(range.PlaceNameBlock)?.Name.ToString() ?? $"PlaceName {range.PlaceNameBlock}"
                    : definition.Name,
                definition.TransferPoint,
                definition.RouteRule);
        }
    }

    private Map? GetMap(uint territoryId)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        return territory is { } row ? dataManager.GetExcelSheet<Map>()?.GetRowOrDefault(row.Map.RowId) : null;
    }

    public static NoFlyZone CreateMapRectangleZone(
        NoFlyZoneDefinition definition,
        NoFlyMapRectangle rectangle,
        Map map)
    {
        Vector3 first = ConvertMapCoordinate(map, rectangle.FirstMapCoordinate);
        Vector3 second = ConvertMapCoordinate(map, rectangle.SecondMapCoordinate);
        Vector3 min = Vector3.Min(first, second);
        Vector3 max = Vector3.Max(first, second);
        return new(
            definition.TerritoryId,
            new Vector3((min.X + max.X) * 0.5f, 0f, (min.Z + max.Z) * 0.5f),
            new Vector3((max.X - min.X) * 0.5f, float.MaxValue, (max.Z - min.Z) * 0.5f),
            definition.Name,
            definition.TransferPoint,
            definition.RouteRule);
    }

    public static Vector3 ConvertMapCoordinate(Map map, Vector2 coordinate) => new(
        (coordinate.X - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetX,
        0f,
        (coordinate.Y - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetY);
}