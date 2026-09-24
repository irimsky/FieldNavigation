using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace FieldNavigation;

/// <summary>A map volume where travel must use ground navigation.</summary>
public readonly record struct NoFlyZone(uint TerritoryId, Vector3 Center, Vector3 HalfExtents, string Name)
{
    public bool Contains(Vector3 position) =>
        MathF.Abs(position.X - this.Center.X) <= this.HalfExtents.X
        && MathF.Abs(position.Y - this.Center.Y) <= this.HalfExtents.Y
        && MathF.Abs(position.Z - this.Center.Z) <= this.HalfExtents.Z;
}

public sealed class NoFlyZoneCatalog(IDataManager dataManager, IEnumerable<uint>? placeNameIds = null)
{
    // Only IDs are configured. Geometry is read from planmap.lgb at runtime.
    private static readonly uint[] defaultPlaceNameIds =
    {
        // PlaceNameId 238：拉诺西亚外地 · 武伽玛罗武装矿山（Territory 180）
        238,
    };
    private readonly HashSet<uint> placeNameIds = new(placeNameIds ?? defaultPlaceNameIds);
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
        string bg = row.Bg.ToString();
        int level = bg.LastIndexOf("/level/", StringComparison.Ordinal);
        if (level < 0)
            return [];
        string directory = bg[..(level + "/level/".Length)];
        var file = dataManager.GetFile<LgbFile>($"bg/{directory}planmap.lgb");
        if (file is null)
            return [];
        var names = dataManager.GetExcelSheet<PlaceName>()!;
        return file.Layers.SelectMany(layer => layer.InstanceObjects)
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
            })
            .ToArray();
    }
}
