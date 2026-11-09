namespace NadeLab.Core;

public sealed class MenuHold
{
    private float? since;
    private bool latched;
    public bool Observe(float now,bool chord,bool allowed)
    {
        if(!chord) {Reset();return false;}
        if(!allowed || !float.IsFinite(now)) {since=null;latched=true;return false;}
        if(latched) return false;
        if(since is null || now<since) {since=now;return false;}
        if(now-since<.65f) return false;
        latched=true;return true;
    }
    public void Reset() {since=null;latched=false;}
}
