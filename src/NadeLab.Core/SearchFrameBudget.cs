namespace NadeLab.Core;

/// <summary>A bounded slice on the game thread, with prompt backoff under load.</summary>
public sealed class SearchFrameBudget
{
    public const double ConservativeMs=.5, NormalMs=1;
    private double holdUntil;
    public void Observe(double nowSeconds,double frameIntervalMs,double pluginWorkMs)
    {
        if(!double.IsFinite(nowSeconds) || !double.IsFinite(frameIntervalMs) || !double.IsFinite(pluginWorkMs))
        {holdUntil=double.PositiveInfinity;return;}
        // At 64 Hz a normal interval is 15.625ms. A delayed frame or an
        // expensive UI/native action keeps the old slice for one second.
        if(frameIntervalMs>24 || pluginWorkMs>3) holdUntil=nowSeconds+1;
    }
    public double Milliseconds(double nowSeconds)=>nowSeconds<holdUntil ? ConservativeMs : NormalMs;
}
