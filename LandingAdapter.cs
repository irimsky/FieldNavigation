using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace FieldNavigation;

/// <summary>
/// Temporarily supplies the game's native MOVE_DESCENT input. This is the same logical input as
/// holding the configured descend key while flying; it avoids asking vnavmesh to repeatedly solve
/// another path after the horizontal arrival threshold has already been reached.
/// </summary>
public sealed unsafe class LandingAdapter : IDisposable, IDescentControl
{
    private delegate bool GetInputStatusDelegate(InputManager* manager, InputCode inputCode);

    private readonly Hook<GetInputStatusDelegate> getInputStatusHook;
    private volatile bool descending;
    private bool disposed;

    public LandingAdapter(IGameInteropProvider interop)
    {
        this.getInputStatusHook = interop.HookFromAddress<GetInputStatusDelegate>(
            (nint)InputManager.MemberFunctionPointers.GetInputStatus,
            this.GetInputStatusDetour);
        this.getInputStatusHook.Enable();
    }

    public bool IsDescending => this.descending;

    public void BeginDescending() => this.descending = true;

    public void StopDescending() => this.descending = false;

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.descending = false;
        this.getInputStatusHook.Disable();
        this.getInputStatusHook.Dispose();
    }

    private bool GetInputStatusDetour(InputManager* manager, InputCode inputCode)
    {
        bool original = this.getInputStatusHook.Original(manager, inputCode);
        return original || (this.descending && inputCode == InputCode.MOVE_DESCENT);
    }
}
