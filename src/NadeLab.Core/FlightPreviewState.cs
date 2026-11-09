namespace NadeLab.Core;

// Visibility is separate from flight accounting: hiding a native camera must
// never forget grenades which can still compete for that camera's subject.
public sealed class FlightPreviewState
{
    private sealed class Flight(double at)
    {
        public double At=at;
        public uint? Handle;
    }
    private readonly Dictionary<long,Flight> flights=[];
    private long generation;
    private long? camera;
    private double? preparedAt,preparedUntil,lastThrowAt;
    private bool holding,eligible=true;
    public bool Paused {get;private set;}
    public int InFlightCount=>flights.Count;
    public IEnumerable<uint> Projectiles=>flights.Values.Where(f=>f.Handle.HasValue).Select(f=>f.Handle!.Value);
    public uint? Projectile=>camera is {} token && flights.TryGetValue(token,out var flight) ? flight.Handle : null;

    // CS2 initializes PiP before grenade_thrown. Each committed animation may
    // prime once; repeated ticks must not reopen a completed or suppressed throw.
    public bool Prepare(double now,double throwAt)
    {
        if(!double.IsFinite(now) || !double.IsFinite(throwAt) || throwAt<=now || throwAt-now>1 || lastThrowAt==throwAt) return false;
        lastThrowAt=throwAt;preparedAt=now;preparedUntil=throwAt+.25;
        if(flights.Count>0 || !eligible) Suppress();
        return true;
    }
    public void ObserveHolding(bool value)
    {
        holding=value;
        if(holding && flights.Count>0) Suppress();
        ResumeWhenEmpty();
    }
    public bool Visible(double now)=>eligible && !Paused && !holding && double.IsFinite(now) &&
        (Prepared(now) || (camera is {} token && flights.TryGetValue(token,out var flight) && Live(flight,now)));
    private bool Prepared(double now)=>preparedAt is {} start && preparedUntil is {} end && now>=start && now<end;
    // Bound flights are reaped by the plugin's validity/effect checks, never by
    // age alone. The watchdog is only for an unresolved entity association.
    private static bool Live(Flight flight,double now)=>double.IsFinite(now) && now>=flight.At && (flight.Handle.HasValue || now-flight.At<30);
    public long Begin(double now)
    {
        long token=++generation;
        if(!double.IsFinite(now)) return token;
        bool prime=Prepared(now) && eligible && !Paused;
        if(flights.Count>0) Suppress();
        flights.Add(token,new(now));
        camera=prime && !Paused ? token : null;
        preparedAt=preparedUntil=null;holding=false;
        return token;
    }
    public bool Bind(long token,uint handle)
    {
        if(!flights.TryGetValue(token,out var flight) || handle==0 || handle==uint.MaxValue ||
            (flight.Handle.HasValue && flight.Handle!=handle) || flights.Any(p=>p.Key!=token && p.Value.Handle==handle)) return false;
        flight.Handle=handle;return true;
    }
    public bool Finish(uint handle)
    {
        var match=flights.FirstOrDefault(p=>p.Value.Handle==handle);
        if(match.Value is null) return false;
        flights.Remove(match.Key);if(camera==match.Key) camera=null;
        ResumeWhenEmpty();return true;
    }
    // Failed association is unknown, not finished. Quarantine it until the
    // bounded watchdog expires; never guess which entity the native PiP chose.
    public bool Cancel(long token)
    {if(!flights.ContainsKey(token)) return false;Suppress();return true;}
    public bool Active(double now)=>flights.Values.Any(f=>Live(f,now));
    public bool Refresh(double now,bool enabled)
    {
        eligible=enabled;
        if(!enabled) {Suppress();preparedAt=preparedUntil=null;}
        if(!double.IsFinite(now) || flights.Values.Any(f=>now<f.At) || (preparedAt is {} start && now<start))
        {
            // A clock reset is not evidence that an entity ended. Preserve
            // handles and animation deduplication; only owner/map reset clears.
            Suppress();preparedAt=preparedUntil=null;
            if(double.IsFinite(now))
                foreach(var flight in flights.Values) if(flight.At>now) flight.At=now;
            return false;
        }
        foreach(var token in flights.Where(p=>!Live(p.Value,now)).Select(p=>p.Key).ToArray())
        {flights.Remove(token);if(camera==token) camera=null;}
        if(preparedUntil is {} end && now>=end) preparedAt=preparedUntil=null;
        ResumeWhenEmpty();return Visible(now);
    }
    private void Suppress() {Paused=true;camera=null;}
    private void ResumeWhenEmpty()
    {
        // A held grenade is not another flight. Once earlier grenades ended,
        // the next release can prime normally even if its pin was pulled early.
        // An already committed release still belongs to the suppressed batch.
        if(flights.Count==0 && preparedUntil is null) Paused=false;
    }
    public void Clear()
    {generation++;flights.Clear();camera=null;preparedAt=preparedUntil=lastThrowAt=null;holding=false;Paused=false;}
}
