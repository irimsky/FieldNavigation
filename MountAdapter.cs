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
public sealed unsafe class MountAdapter(ICondition condition, IObjectTable objectTable, IPluginLog? log = null) : ITravelRecoveryControl
{
    private const uint MountRouletteActionId = 9;
    private const uint DismountActionId = 23;
    private readonly MountRequestGate mountRequests = new();
    private readonly IPluginLog? pluginLog = log;
    private bool? lastObservedMountCast;
    private DateTime nextMountCastDiagnosticAt;
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

    public bool TryMount(Action? onRequest)
    {
        bool accepted = this.mountRequests.TryRequest(
            DateTime.UtcNow, this.State, this.CanAttemptMount, () => this.RequestMount(onRequest));
        if (accepted)
            this.pluginLog?.Debug("[FN_MOUNT] mount request accepted; blockReason={BlockReason}; mounted={Mounted}; nativeMounted={NativeMounted}; transition={Transition}; inFlight={InFlight}; position={Position}",
                this.MountBlockReason, this.IsMounted, this.IsNativeMounted, this.IsMountTransition, this.IsInFlight,
                this.objectTable.LocalPlayer?.Position);
        return accepted;
    }

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
        bool accepted = manager->UseAction(ActionType.GeneralAction, MountRouletteActionId);
        this.pluginLog?.Debug("[FN_MOUNT] UseAction mount result={Result}; owner={Owner}; position={Position}; inCombat={InCombat}; casting={Casting}; occupied={Occupied}",
            accepted, this.mountCastOwner, this.objectTable.LocalPlayer?.Position,
            condition[ConditionFlag.InCombat], condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87],
            condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent]);
        return accepted;
    }

    /// <summary>The host must validate ownership and character before cancelling its mount request.</summary>
    public void CancelMountCast(string reason)
    {
        var player = objectTable.LocalPlayer;
        bool active = this.IsMountCastActive;
        this.pluginLog?.Debug("[FN_MOUNT] cancel requested; reason={Reason}; active={Active}; owner={Owner}; castActionType={CastActionType}; castActionId={CastActionId}; mounted={Mounted}; nativeMounted={NativeMounted}; transition={Transition}; inCombat={InCombat}; casting={Casting}; occupied={Occupied}; position={Position}",
            reason, active, this.mountCastOwner, player?.CastActionType, player?.CastActionId,
            this.IsMounted, this.IsNativeMounted, this.IsMountTransition, condition[ConditionFlag.InCombat],
            condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87] || player?.IsCasting == true,
            condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent],
            player?.Position);
        if (!active)
            return;
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (ui != null)
        {
            ui->Hotbar.CancelCast();
            this.pluginLog?.Debug("[FN_MOUNT] owned mount cast cancelled; reason={Reason}", reason);
        }
    }

    public void ObserveMountCast(string source)
    {
        bool active = this.IsMountCastActive;
        DateTime now = DateTime.UtcNow;
        if (this.lastObservedMountCast == active && (!active || now < this.nextMountCastDiagnosticAt))
            return;
        this.lastObservedMountCast = active;
        this.nextMountCastDiagnosticAt = now.AddSeconds(2);
        var player = objectTable.LocalPlayer;
        this.pluginLog?.Debug("[FN_MOUNT] cast state; source={Source}; active={Active}; owner={Owner}; castActionType={CastActionType}; castActionId={CastActionId}; mountTransition={MountTransition}; mounted={Mounted}; nativeMounted={NativeMounted}; inFlight={InFlight}; inCombat={InCombat}; casting={Casting}; occupied={Occupied}; position={Position}",
            source, active, this.mountCastOwner, player?.CastActionType, player?.CastActionId, this.IsMountTransition,
            this.IsMounted, this.IsNativeMounted, this.IsInFlight, condition[ConditionFlag.InCombat],
            condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87] || player?.IsCasting == true,
            condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent],
            player?.Position);
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
