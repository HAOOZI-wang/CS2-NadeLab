namespace NadeLab.Plugin;

// Own only the timer-resolution bit; unrelated policies and later external
// changes survive disposal. Capturing state does not itself acquire ownership.
internal sealed class OwnedPowerPolicy
{
    private const uint TimerBit=4;
    private (uint Control,uint State)? original;
    private bool applied;
    public void Capture(uint control,uint state)=>original ??= (control&TimerBit,state&TimerBit);
    public void Applied()=>applied=true;
    public bool TryRestore(uint control,uint state,out uint restoredControl,out uint restoredState)
    {
        restoredControl=control;restoredState=state;
        if(!applied || original is not { } prior || (control&TimerBit)==0 || (state&TimerBit)!=0) return false;
        restoredControl=(control&~TimerBit)|prior.Control;
        restoredState=(state&~TimerBit)|prior.State;
        return restoredControl!=control || restoredState!=state;
    }
}
