using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace FieldNavigation;

/// <summary>Existing host services; callers do not implement recovery or backend adapters.</summary>
public sealed record FieldNavigationHostServices
{
    public required IDalamudPluginInterface PluginInterface { get; init; }
    public required IFramework Framework { get; init; }
    public required IClientState ClientState { get; init; }
    public required IPlayerState PlayerState { get; init; }
    public required IObjectTable Objects { get; init; }
    public required ICondition Condition { get; init; }
    public required IDataManager DataManager { get; init; }
    public required IGameInteropProvider Interop { get; init; }
    public required IPluginLog Log { get; init; }
    // Existing concrete FN instances may be shared during incremental host migration.
    public VnavmeshIpc? SharedNavigation { get; init; }
    public MountAdapter? SharedMount { get; init; }
    public LandingAdapter? SharedLanding { get; init; }
}

internal readonly record struct RuntimeFrame(TravelObservation Travel, bool Valid, bool Loading, bool Dead, bool Casting,
    bool Occupied, bool InCombat, float Rotation, uint Job);

internal sealed unsafe class NavigationRuntime(FieldNavigationHostServices services, MountAdapter mount, NoFlyZoneCatalog zones)
{
    public RuntimeFrame Read()
    {
        var c = services.Condition;
        var player = services.Objects.LocalPlayer;
        bool loading = c.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);
        bool valid = services.ClientState.IsLoggedIn && services.PlayerState.IsLoaded && player is not null
            && !c[ConditionFlag.LoggingOut];
        bool casting = player?.IsCasting == true || c.Any(ConditionFlag.Casting, ConditionFlag.Casting87);
        bool occupied = c.Any(ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
            ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.Crafting, ConditionFlag.Gathering);
        bool dead = player?.IsDead == true || c[ConditionFlag.Unconscious];
        Vector3 position = player?.Position ?? Vector3.Zero;
        var zone = valid && !loading ? zones.Find(services.ClientState.TerritoryType, position, NoFlyZone.BoundaryMargin) : null;
        var native = valid && !loading ? PlayerState.Instance() : null;
        bool combat = c[ConditionFlag.InCombat];
        bool mapAllowsMount = services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(services.ClientState.TerritoryType) is { Mount: true };
        var observation = new TravelObservation(DateTime.UtcNow, position,
            new(mount.IsInFlight, mount.IsJumping, mount.IsMountTransition), valid && !loading && mount.IsReadyForNavigation, valid && !loading && mount.IsDismounted,
            native != null && native->CanFly && mapAllowsMount, zone is not null, zone?.LandingPoint,
            valid && !loading && !dead && !casting && !occupied && !combat,
            valid, Character: services.PlayerState.ContentId, Territory: services.ClientState.TerritoryType, InCombat: combat);
        return new(observation, valid, loading, dead, casting, occupied, combat, player?.Rotation ?? 0,
            services.PlayerState.ClassJob.RowId);
    }
}
