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
    private ulong? mountCastOwner;

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
    public bool IsMountCastActive
    {
        get
        {
            var player = objectTable.LocalPlayer;
            return player is { IsCasting: true }
                && player.GameObjectId == mountCastOwner
                && (player.CastActionType == (byte)ActionType.Mount
                    || player.CastActionType == (byte)ActionType.GeneralAction && player.CastActionId == MountRouletteActionId);
        }
    }

    public bool IsJumping => condition[ConditionFlag.Jumping] || condition[ConditionFlag.Jumping61];

    public bool CanAttemptMount =>
        this.State.CanRequestMount
        && !condition[ConditionFlag.InCombat]
        && !condition[ConditionFlag.Unconscious]
        && !condition[ConditionFlag.BetweenAreas]
        && !condition[ConditionFlag.BetweenAreas51]
        && !condition[ConditionFlag.Occupied]
        && !condition[ConditionFlag.OccupiedInEvent]
        && !condition[ConditionFlag.OccupiedInQuestEvent]
        && !condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !condition[ConditionFlag.Casting]
        && !condition[ConditionFlag.Casting87]
        && objectTable.LocalPlayer?.IsCasting != true
        && !this.IsJumping;

    public string MountBlockReason => this.IsMounted ? "游戏条件已显示正在骑乘"
        : this.IsNativeMounted ? "等待原生坐骑标志与游戏条件同步"
        : this.IsMountTransition ? "坐骑切换进行中"
        : condition[ConditionFlag.InCombat] ? "战斗中"
        : condition[ConditionFlag.Unconscious] ? "角色失去意识"
        : condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ? "区域切换中"
        : condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent]
            ? "角色占用中"
        : condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87] || objectTable.LocalPlayer?.IsCasting == true
            ? "施法中"
        : this.IsJumping ? "跳跃中" : string.Empty;

    public bool TryMount() => this.TryMount(null);

    public bool TryMount(Action? onRequest) => this.mountRequests.TryRequest(
        DateTime.UtcNow, this.State, this.CanAttemptMount, () => this.RequestMount(onRequest));

    private bool RequestMount(Action? onRequest)
    {
        ActionManager* manager = ActionManager.Instance();
        if (manager == null || manager->GetActionStatus(ActionType.GeneralAction, MountRouletteActionId) != 0)
            return false;
        TargetSystem* targets = TargetSystem.Instance();
        if (targets != null)
        {
            targets->Target = null;
            targets->SoftTarget = null;
        }

        this.mountCastOwner = objectTable.LocalPlayer?.GameObjectId;
        onRequest?.Invoke();
        return manager->UseAction(ActionType.GeneralAction, MountRouletteActionId);
    }

    /// <summary>The host must validate ownership and character before cancelling its mount request.</summary>
    public void CancelMountCast()
    {
        var player = objectTable.LocalPlayer;
        if (player is not { IsCasting: true } || player.GameObjectId != this.mountCastOwner || !(player.CastActionType == (byte)ActionType.Mount
            || player.CastActionType == (byte)ActionType.GeneralAction && player.CastActionId == 9))
            return;
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (ui != null) ui->Hotbar.CancelCast();
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
