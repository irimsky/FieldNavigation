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
    public void Reset() { this.readySince = null; this.requested = false; }
    public bool TryTakeoff(DateTime now, bool allowed)
    {
        if (this.requested)
            return false;
        ActionManager* actions = ActionManager.Instance();
        bool ready = allowed && actions != null && Control.CanFly
            && objects.LocalPlayer is { IsDead: false, IsCasting: false }
            && condition[ConditionFlag.Mounted] && !condition[ConditionFlag.InFlight]
            && !condition.Any(ConditionFlag.InCombat, ConditionFlag.Mounting, ConditionFlag.Mounting71,
                ConditionFlag.MountOrOrnamentTransition, ConditionFlag.Jumping, ConditionFlag.Jumping61,
                ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut)
            && actions->GetActionStatus(ActionType.GeneralAction, 2) == 0;
        if (!ready) { this.readySince = null; return false; }
        this.readySince ??= now;
        if (now - this.readySince.Value < TimeSpan.FromSeconds(1)) return false;
        this.requested = true;
        _ = actions->UseAction(ActionType.GeneralAction, 2);
        return true;
    }
}
