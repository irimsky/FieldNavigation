using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace FieldNavigation;

/// <summary>A map volume where travel must use ground navigation.</summary>
public readonly record struct NoFlyZone(
    uint TerritoryId,
    Vector3 Center,
    Vector3 HalfExtents,
    string Name,
    Vector3? LandingPoint = null)
{
    public bool Contains(Vector3 position) =>
        MathF.Abs(position.X - this.Center.X) <= this.HalfExtents.X
        && MathF.Abs(position.Y - this.Center.Y) <= this.HalfExtents.Y
        && MathF.Abs(position.Z - this.Center.Z) <= this.HalfExtents.Z;
}

/// <summary>A rectangular no-fly area defined by two in-game map coordinates (X, Y).</summary>
public readonly record struct NoFlyMapRectangle(
    uint TerritoryId,
    Vector2 FirstMapCoordinate,
    Vector2 SecondMapCoordinate,
    string Name,
    Vector2? LandingMapCoordinate = null,
    float LandingHeight = 0f);

public sealed class NoFlyZoneCatalog(
    IDataManager dataManager,
    IEnumerable<uint>? placeNameIds = null,
    IEnumerable<NoFlyMapRectangle>? mapRectangles = null)
{
    // Only IDs are configured. Geometry is read from planmap.lgb at runtime.
    private static readonly uint[] defaultPlaceNameIds =
    {
        // PlaceNameId 238：拉诺西亚外地 · 武伽玛罗武装矿山（Territory 180）
        238,
    };
    private static readonly NoFlyMapRectangle[] defaultMapRectangles =
    {
        // 试验区域：南萨纳兰 (29.2, 21.3) 到 (36.2, 17.2)，指定落点 (29.0, 20.3)，高度 0.3。
        new(146, new Vector2(29.2f, 21.3f), new Vector2(36.2f, 17.2f),
            "试验矩形禁飞区·南萨纳兰", new Vector2(29.0f, 20.3f), 0.3f),
    };
    private readonly HashSet<uint> placeNameIds = new(placeNameIds ?? defaultPlaceNameIds);
    private readonly IReadOnlyList<NoFlyMapRectangle> mapRectangles =
        (mapRectangles ?? defaultMapRectangles).ToArray();
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

        var map = dataManager.GetExcelSheet<Map>()?.GetRowOrDefault(row.Map.RowId);
        if (map is { } mapRow && mapRow.SizeFactor != 0)
        {
            foreach (NoFlyMapRectangle rectangle in this.mapRectangles.Where(rectangle => rectangle.TerritoryId == territoryId))
                zones.Add(CreateMapRectangleZone(rectangle, mapRow));
        }

        return zones;
    }

    public static NoFlyZone CreateMapRectangleZone(NoFlyMapRectangle rectangle, Map map)
    {
        Vector3 first = ConvertMapCoordinate(map, rectangle.FirstMapCoordinate);
        Vector3 second = ConvertMapCoordinate(map, rectangle.SecondMapCoordinate);
        Vector3 min = Vector3.Min(first, second);
        Vector3 max = Vector3.Max(first, second);
        Vector3? landingPoint = rectangle.LandingMapCoordinate is { } landingCoordinate
            ? ToLandingPoint(ConvertMapCoordinate(map, landingCoordinate), rectangle.LandingHeight)
            : null;
        return new(
            rectangle.TerritoryId,
            new Vector3((min.X + max.X) * 0.5f, 0f, (min.Z + max.Z) * 0.5f),
            new Vector3((max.X - min.X) * 0.5f, float.MaxValue, (max.Z - min.Z) * 0.5f),
            rectangle.Name,
            landingPoint);
    }

    public static Vector3 ConvertMapCoordinate(Map map, Vector2 coordinate) => new(
        (coordinate.X - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetX,
        0f,
        (coordinate.Y - 1f - 2048f / map.SizeFactor) / 0.02f - map.OffsetY);

    private static Vector3 ToLandingPoint(Vector3 mapPosition, float height) =>
        new(mapPosition.X, height, mapPosition.Z);
}
