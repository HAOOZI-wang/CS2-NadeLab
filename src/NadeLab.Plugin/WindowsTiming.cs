using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

// CS2 changes its power policy during startup, overwriting launcher settings.
// Hold one paired process-local timer request and preserve its other policy bits.
internal sealed class WindowsTiming : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerState { public uint Version,ControlMask,StateMask; }
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetProcessInformation(nint process,int kind,ref PowerState state,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetProcessInformation(nint process,int kind,ref PowerState state,uint size);
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
    private readonly bool timerRequested;
    private readonly ILogger logger;
    private readonly OwnedPowerPolicy policy=new();
    private bool warned,disposed;
    public WindowsTiming(ILogger logger)
    {
        this.logger=logger;
        timerRequested=timeBeginPeriod(1)==0;
        EnsurePolicy();
    }
    public void EnsurePolicy()
    {
        if(disposed) return;
        var state=new PowerState {Version=1};
        if(!GetProcessInformation((nint)(-1),4,ref state,12))
        {
            if(!warned) {warned=true;logger.LogWarning("Windows background timer policy could not be read: {Error}",Marshal.GetLastPInvokeError());}
            return;
        }
        policy.Capture(state.ControlMask,state.StateMask);
        if((state.ControlMask&4)!=0 && (state.StateMask&4)==0) return;
        state.ControlMask|=4; state.StateMask&=~4u;
        if (SetProcessInformation((nint)(-1),4,ref state,12))
        {
            policy.Applied();
            logger.LogInformation("Windows background timer policy restored for this training process.");
        }
        else if (!warned)
        { warned=true; logger.LogWarning("Windows background timer policy unavailable: {Error}",Marshal.GetLastPInvokeError()); }
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true;
        if(timerRequested) timeEndPeriod(1);
        var current=new PowerState {Version=1};
        if(GetProcessInformation((nint)(-1),4,ref current,12) &&
            policy.TryRestore(current.ControlMask,current.StateMask,out uint control,out uint state))
        {
            current.ControlMask=control;current.StateMask=state;
            if(!SetProcessInformation((nint)(-1),4,ref current,12))
                logger.LogWarning("Windows training timer policy could not be released: {Error}",Marshal.GetLastPInvokeError());
        }
    }
}
