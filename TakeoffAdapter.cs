using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace FieldNavigation;

/// <summary>One native takeoff request after one second of continuous readiness.</summary>
public sealed unsafe class TakeoffAdapter(ICondition condition, IObjectTable objects)
{
    private DateTime? readySince;
    private bool requested;
    public string Status { get; private set; } = "尚未请求";
    public void Reset() { this.readySince = null; this.requested = false; this.Status = "尚未请求"; }
    public bool TryTakeoff(DateTime now, bool allowed)
    {
        if (this.requested)
            return false;
        ActionManager* actions = ActionManager.Instance();
        var flightStatus = Control.GetFlightAllowedStatus();
        uint? jumpStatus = actions != null ? actions->GetActionStatus(ActionType.GeneralAction, 2) : null;
        bool ready = allowed && actions != null && flightStatus == Control.FlightAllowedStatus.CanFly
            && objects.LocalPlayer is { IsDead: false, IsCasting: false }
            && condition[ConditionFlag.Mounted] && !condition[ConditionFlag.InFlight]
            && !condition.Any(ConditionFlag.InCombat, ConditionFlag.Mounting, ConditionFlag.Mounting71,
                ConditionFlag.MountOrOrnamentTransition, ConditionFlag.Jumping, ConditionFlag.Jumping61,
                ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut)
            && jumpStatus == 0;
        if (!ready)
        {
            this.readySince = null;
            this.Status = $"条件未就绪（FlightAllowed={flightStatus}，JumpStatus={jumpStatus}，Mounted={condition[ConditionFlag.Mounted]}）";
            return false;
        }
        this.readySince ??= now;
        if (now - this.readySince.Value < TimeSpan.FromSeconds(1))
        { this.Status = "等待起飞条件连续稳定一秒"; return false; }
        this.requested = true;
        bool accepted = actions->UseAction(ActionType.GeneralAction, 2);
        this.Status = $"已发出跳跃请求（UseAction={accepted}），等待实际升空";
        return true;
    }
}
