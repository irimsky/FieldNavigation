using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FieldNavigation;

public readonly record struct MountOption(uint Id, string Name);

/// <summary>Shared mount ownership lookup for host settings pages.</summary>
public static class MountCatalog
{
    public static IReadOnlyList<MountOption> GetOwnedMounts(IDataManager dataManager, IUnlockState unlockState)
    {
        List<MountOption> options = [];
        foreach (Mount mount in dataManager.GetExcelSheet<Mount>())
        {
            if (mount.RowId == 0 || mount.Icon == 0)
                continue;
            string name = mount.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name) || !unlockState.IsMountUnlocked(mount))
                continue;
            options.Add(new MountOption(mount.RowId, name));
        }

        options.Sort(static (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return options;
    }

    public static string GetMountName(IDataManager dataManager, uint mountId)
    {
        if (mountId == 0)
            return "随机坐骑";
        foreach (Mount mount in dataManager.GetExcelSheet<Mount>())
            if (mount.RowId == mountId && !string.IsNullOrWhiteSpace(mount.Singular.ToString()))
                return mount.Singular.ToString();
        return $"坐骑 #{mountId}";
    }
}
