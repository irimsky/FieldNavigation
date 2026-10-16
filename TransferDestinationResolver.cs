using System.Numerics;
using Lumina.Excel.Sheets;
using Dalamud.Plugin.Services;

namespace FieldNavigation;

internal static class TransferDestinationResolver
{
    // MapMarker coordinates supply X/Z only: arrival checks are horizontal unless explicitly configured.
    public static Vector3? Find(IDataManager data, TransferKind kind, uint id, uint territory)
    {
        var aetherytes = data.GetExcelSheet<Aetheryte>();
        var ids = kind == TransferKind.Aetheryte ? new HashSet<uint> { id }
            : aetherytes.Where(a => a.AethernetName.RowId == id && a.Territory.RowId == territory).Select(a => a.RowId).ToHashSet();
        var names = aetherytes.Where(a => ids.Contains(a.RowId)).Select(a => a.AethernetName.RowId).ToHashSet();
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var map in data.GetExcelSheet<Map>().Where(m => m.TerritoryType.RowId == territory && m.MapMarkerRange != 0 && m.SizeFactor > 0))
        {
            if (!markers.TryGetRow(map.MapMarkerRange, out var group)) continue;
            foreach (var marker in group)
            {
                if (!(marker.DataType == 3 && ids.Contains(marker.DataKey.RowId)
                    || marker.DataType == 4 && names.Contains(marker.DataKey.RowId))) continue;
                float scale = map.SizeFactor / 100f;
                return new((marker.X - 1024f) / scale - map.OffsetX, 0, (marker.Y - 1024f) / scale - map.OffsetY);
            }
        }
        return null;
    }
}
