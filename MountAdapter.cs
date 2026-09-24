using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace FieldNavigation;

/// <summary>
/// Framework-thread-only mount actions. Conditions authorize riding; native state blocks
/// duplicate requests and premature dismount completion while the two signals synchronize.
/// </summary>
public sealed unsafe class MountAdapter(ICondition condition, IObjectTable objectTable) : ITravelRecoveryControl
{
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;
    private readonly MountRequestGate mountRequests = new();
    private DateTime nextDismountAt;

    public bool IsMounted => condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion];

    public bool IsNativeMounted
    {
        get
        {
            var player = objectTable.LocalPlayer;
            return player is { Address: not 0 }
                && ((NativeBattleChara*)player.Address)->IsMounted();
        }
    }

    public MountState State => new(this.IsMounted, this.IsNativeMounted, this.IsMountTransition);
    public bool IsReadyForNavigation => this.State.CanNavigateMounted;
    public bool IsDismounted => this.State.IsDismounted && !this.IsInFlight && !this.IsJumping;
    public DateTime NextMountRequestAt => this.mountRequests.NextRequestAt;

    public bool IsMountTransition =>
        condition[ConditionFlag.Mounting]
        || condition[ConditionFlag.Mounting71]
        || condition[ConditionFlag.MountOrOrnamentTransition];

    public bool IsInFlight => condition[ConditionFlag.InFlight];

    public bool IsJumping => condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61];

    public bool CanAttemptMount =>
        this.State.CanRequestMount
        && !condition[ConditionFlag.InCombat]
        && !condition[ConditionFlag.Unconscious]
        && !condition[ConditionFlag.BetweenAreas]
        && !condition[ConditionFlag.BetweenAreas51]
        && !condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !condition[ConditionFlag.Casting]
        && !this.IsJumping;

    public string MountBlockReason => this.IsMounted ? "游戏条件已显示正在骑乘"
        : this.IsNativeMounted ? "等待原生坐骑标志与游戏条件同步"
        : this.IsMountTransition ? "坐骑切换进行中"
        : condition[ConditionFlag.InCombat] ? "战斗中"
        : condition[ConditionFlag.Unconscious] ? "角色失去意识"
        : condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ? "区域切换中"
        : condition[ConditionFlag.OccupiedInCutSceneEvent] ? "过场或事件占用中"
        : condition[ConditionFlag.Casting] ? "施法中"
        : this.IsJumping ? "跳跃中" : string.Empty;

    public bool TryMount() => this.mountRequests.TryRequest(DateTime.UtcNow, this.State, this.CanAttemptMount, this.RequestMount);

    private bool RequestMount()
    {
        TargetSystem* targets = TargetSystem.Instance();
        if (targets != null)
        {
            targets->Target = null;
            targets->SoftTarget = null;
        }

        ActionManager* manager = ActionManager.Instance();
        return manager != null
            && manager->UseAction(ActionType.GeneralAction, MountRouletteActionId);
    }

    public bool TryDismount()
    {
        DateTime now = DateTime.UtcNow;
        if (this.IsDismounted || this.IsInFlight || this.IsJumping || this.IsMountTransition || now < this.nextDismountAt)
            return false;
        this.nextDismountAt = now.AddMilliseconds(250);
        ActionManager* manager = ActionManager.Instance();
        return manager != null
            && manager->UseAction(ActionType.GeneralAction, DismountActionId);
    }

    public bool TryJump()
    {
        if (this.IsMountTransition || this.IsInFlight || this.IsJumping
            || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        ActionManager* manager = ActionManager.Instance();
        return manager != null
            && manager->UseAction(ActionType.GeneralAction, 2);
    }
}
