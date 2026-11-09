using System.Numerics;

namespace NadeLab.Core;

/// <summary>
/// Inverse flash search with no target point. Geometry is queried incrementally
/// on the calling thread. Advance runs one simulation step (at most four hull
/// traces), one action check, or one observer visibility callback. The caller's
/// visibility callback must itself use a bounded number of rays.
/// </summary>
public sealed class FlashEffectSearch : IDisposable
{
    private readonly ICollisionWorld world;
    private readonly PhysicsProfile physics;
    private readonly ThrowStart start;
    private readonly FlashObjective objective;
    private readonly Func<Vector3,FlashObserver,PredictionVisibility> visibility;
    private readonly Func<Aim,bool>? canThrow;
    private readonly FlashSearchOptions options;
    private readonly IReadOnlyList<Aim> families;
    private readonly HashSet<Aim> visited=[];
    private readonly HashSet<Aim> reviewed=[];
    private readonly List<FlashSearchCandidate> hits=[],nearest=[],matches=[];
    private readonly Dictionary<int,FlashSearchCandidate> bankSeeds=[];
    private readonly IEnumerator<int> work;
    private FlashSearchCandidate? last;
    private bool finishRequested,disposed;
    private readonly float heading;
    public bool Done { get; private set; }
    public FlashSearchResult? Result { get; private set; }
    private IReadOnlyList<FlashSearchCandidate> publishedCandidates=Array.Empty<FlashSearchCandidate>();
    public IReadOnlyList<FlashSearchCandidate> PublishedCandidates=>publishedCandidates;
    public FlashSearchResult PublishedResult()=>new(physics.Id,Evaluated,false,publishedCandidates,[]);
    public int Evaluated { get; private set; }
    public int StabilityProbes { get; private set; }
    public int MaxCandidates=>options.MaxCandidates;
    public IReadOnlyList<Aim> SeedAims=>options.SeedAims is { } seeds ? Array.AsReadOnly(seeds.ToArray()) : Array.Empty<Aim>();
    public int ReviewedCandidateCount=>matches.Count;
    public string Phase { get; private set; }="flash-seeds";
    public ThrowStart Start=>start;
    public FlashObjective Objective=>objective;

    public FlashEffectSearch(ICollisionWorld world,PhysicsProfile physics,ThrowStart start,FlashObjective objective,
        Func<Vector3,FlashObserver,PredictionVisibility> visibility,FlashSearchOptions? options=null,Func<Aim,bool>? canThrow=null)
    {
        ArgumentNullException.ThrowIfNull(world);ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(objective);ArgumentNullException.ThrowIfNull(visibility);
        physics.Validate();
        if(physics.Kind!=GrenadeKind.Flash || !Geometry.Finite(start.Feet) || !float.IsFinite(start.EyeHeight) || start.EyeHeight is <40 or >80)
            throw new ArgumentException("Flash search requires a flash profile and a valid fixed origin.");
        this.options=options is null ? new() : options with {SeedAims=options.SeedAims?.ToArray()};
        this.options.Validate();
        this.world=world;this.physics=physics;this.start=start;this.objective=objective;this.visibility=visibility;this.canThrow=canThrow;
        heading=this.options.PreferredView?.Yaw ?? Heading(objective.Observers.First(o=>o.Role==FlashRole.MustFlash).Eye);
        families=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},0,heading)
            .Where(a=>a.EffectiveAction is ThrowAction.Standing or ThrowAction.Jump)
            .Where(FlashSearchOptions.FixedAim).ToArray();
        work=Work().GetEnumerator();
    }

    public bool Advance()
    {
        if(Done || disposed) return false;
        if(work.MoveNext()) return true;
        Done=true;return false;
    }
    // Finish the current bounded review, then publish only completed reviews.
    public void RequestFinish()=>finishRequested=true;
    public void Dispose(){if(disposed)return;disposed=true;work.Dispose();Done=true;}
    private bool Budget=>!finishRequested && Evaluated<options.MaxCandidates;
    private int ReviewBatchCount=>SearchResultCapacity.ReviewBatch(options.ResultCount,6);
    private float Heading(Vector3 eye)=>Geometry.NormalizeYaw(MathF.Atan2(eye.Y-start.Feet.Y,eye.X-start.Feet.X)*180/MathF.PI);
    private float ViewChange(Aim aim)
    {
        var view=options.PreferredView ?? new Aim(0,heading,ThrowMode.Left);
        return MathF.Abs(Geometry.NormalizeYaw(aim.Yaw-view.Yaw))+.5f*MathF.Abs(aim.Pitch-view.Pitch);
    }
    private static Aim Normalize(Aim a)=>a with {Yaw=Geometry.NormalizeYaw(a.Yaw)};
    private float Deficit(FlashSearchCandidate c)=>c.Effects.Where(e=>e.Participates).Sum(e=>e.GeometryAccepted ? 0 :
        !e.Estimate.Valid ? 100 : e.Observer.Role==FlashRole.Protect ? 10+(float)e.Estimate.Visibility.Clear/e.Estimate.Visibility.Total :
        10+MathF.Max(0,objective.MinimumFlashSeconds-e.Estimate.Minimum));
    private int Order(FlashSearchCandidate a,FlashSearchCandidate b)
    {
        int compare=Deficit(a).CompareTo(Deficit(b));if(compare!=0)return compare;
        compare=b.Candidate.RobustFraction.CompareTo(a.Candidate.RobustFraction);if(compare!=0)return compare;
        compare=(a.Candidate.Aim.Airborne ? 1 : 0).CompareTo(b.Candidate.Aim.Airborne ? 1 : 0);if(compare!=0)return compare;
        compare=ViewChange(a.Candidate.Aim).CompareTo(ViewChange(b.Candidate.Aim));if(compare!=0)return compare;
        return a.Candidate.Trajectory.WallBounces.CompareTo(b.Candidate.Trajectory.WallBounces);
    }
    private static bool Similar(Aim a,Aim b)=>a.Mode==b.Mode && a.EffectiveAction==b.EffectiveAction &&
        MathF.Abs(a.Pitch-b.Pitch)<2 && MathF.Abs(Geometry.NormalizeYaw(a.Yaw-b.Yaw))<2;
    private void Keep(List<FlashSearchCandidate> list,FlashSearchCandidate candidate,int count)
    {
        int similar=list.FindIndex(c=>Similar(c.Candidate.Aim,candidate.Candidate.Aim));
        if(similar>=0){if(Order(candidate,list[similar])>=0)return;list.RemoveAt(similar);}
        list.Add(candidate);list.Sort(Order);if(list.Count>count)list.RemoveRange(count,list.Count-count);
    }

    private IEnumerable<int> Evaluate(Aim raw,bool nominal,bool recordPoints=false,bool completeOnFinish=false)
    {
        last=null;var aim=Normalize(raw);
        if(!FlashSearchOptions.FixedAim(aim)){yield return 0;yield break;}
        if(nominal && !visited.Add(aim)){yield return 0;yield break;}
        if(nominal)Evaluated++;else StabilityProbes++;
        bool allowed=canThrow?.Invoke(aim) ?? true;
        yield return 0;
        if(!allowed || (finishRequested && !completeOnFinish))yield break;
        var simulation=new SmokeSimulation(world,physics,start,aim,recordPoints);
        yield return 0;
        while(!simulation.Done)
        {
            if(finishRequested && !completeOnFinish)yield break;
            simulation.Step();yield return 0;
        }
        var trajectory=simulation.Result();
        if(trajectory.Status!=SimulationStatus.Detonated)yield break;
        var effects=new List<FlashObserverEvaluation>();
        foreach(var observer in objective.Observers)
        {
            if(finishRequested && !completeOnFinish)yield break;
            var exposed=observer.Role==FlashRole.Ignore ? new PredictionVisibility(0,0,false) : visibility(trajectory.Endpoint,observer);
            effects.Add(objective.Evaluate(trajectory.Endpoint,observer,exposed));
            yield return 0;
        }
        var candidate=new FlashSearchCandidate(new(aim,trajectory,0),effects.AsReadOnly());
        candidate=candidate with {Candidate=candidate.Candidate with {Error=Deficit(candidate)}};
        last=candidate;
        if(!nominal)yield break;
        if(candidate.GeometryAccepted)Keep(hits,candidate,options.ResultCount*8);
        else Keep(nearest,candidate,16);
        if(trajectory.WallBounces>0 && (bankSeeds.Count<24 || bankSeeds.ContainsKey(trajectory.CollisionRoute)))
            if(!bankSeeds.TryGetValue(trajectory.CollisionRoute,out var old) || Order(candidate,old)<0)bankSeeds[trajectory.CollisionRoute]=candidate;
    }

    private IEnumerable<Aim> InitialAims()
    {
        // A hint contributes angles/strength/action only. Rebuild jump release
        // height/velocity from this request's physics, never an older profile.
        foreach(var seed in options.SeedAims ?? [])
        {
            var family=families.First(f=>f.Mode==seed.Mode && f.EffectiveAction==seed.EffectiveAction);
            yield return family with {Pitch=seed.Pitch,Yaw=seed.Yaw};
        }
        var heads=objective.Observers.Where(o=>o.Role==FlashRole.MustFlash).Select(o=>Heading(o.Eye)).Prepend(heading).Distinct().ToArray();
        // These headings seed useful directions, not requested detonation points.
        foreach(float pitch in new[]{-15f,-35,-55,-75,5,25,55,80})
            foreach(var family in families)
                foreach(float yaw in heads)yield return family with {Pitch=pitch,Yaw=yaw};
    }
    private IEnumerable<Aim> GlobalAims()
    {
        // Every heading, strength, and standing/jumping family receives a coarse
        // pass. Banks and throws away from an opponent remain first-class routes.
        var yaws=Enumerable.Range(0,12).Select(i=>Geometry.NormalizeYaw(heading+i*30)).OrderBy(y=>MathF.Abs(Geometry.NormalizeYaw(y-heading)));
        foreach(float pitch in new[]{-85f,-70,-55,-40,-25,-10,5,20,35,50,65,80})
            foreach(float yaw in yaws)
                foreach(var family in families)yield return family with {Pitch=pitch,Yaw=yaw};
    }
    private IEnumerable<Aim> GapAims()
    {
        // Occluded samples tie on effect deficit. Refining only the best few
        // cannot recover an opening between the 30-degree yaw / 15-degree pitch
        // samples. Cover those gaps before spending the remaining budget nearby.
        // Visit cell centres first, then their edges; retain every throw family.
        foreach(var (pitchOffset,yawOffset) in new[]{(7.5f,15f),(0f,15f),(7.5f,0f)})
            for(int pitchIndex=0;pitchIndex<(pitchOffset==0 ? 12 : 11);pitchIndex++)
                foreach(int yawIndex in Enumerable.Range(0,12).OrderBy(i=>MathF.Abs(Geometry.NormalizeYaw(i*30+yawOffset))))
                    foreach(var family in families)
                        yield return family with {Pitch=-85+pitchIndex*15+pitchOffset,Yaw=heading+yawIndex*30+yawOffset};
    }
    private IEnumerable<int> Review(int limit)
    {
        Phase="flash-review";
        foreach(var seed in hits.OrderBy(c=>c,Comparer<FlashSearchCandidate>.Create(Order)).ToArray())
        {
            if(finishRequested && matches.Count>0)yield break;
            if(limit<=0)yield break;
            if(!reviewed.Add(seed.Candidate.Aim))continue;
            limit--;
            foreach(int unit in Evaluate(seed.Candidate.Aim,false,true,true))yield return unit;
            var center=last;
            if(center is null || !center.GeometryAccepted)continue;
            int accepted=1,trials=1;
            foreach(var (pitch,yaw) in new[]{(.75f,0f),(-.75f,0f),(0f,.75f),(0f,-.75f)})
            {
                var perturbed=center.Candidate.Aim with {Pitch=Math.Clamp(center.Candidate.Aim.Pitch+pitch,-89,89),Yaw=center.Candidate.Aim.Yaw+yaw};
                foreach(int unit in Evaluate(perturbed,false,false,true))yield return unit;
                trials++;if(last?.GeometryAccepted==true)accepted++;
            }
            if(center.Candidate.Aim.Airborne)
                foreach(float dt in new[]{-.025f,.025f})
                {
                    float time=JumpLatch.Seconds+dt,takeoff=JumpLatch.TakeoffSpeed(physics);
                    var delayed=center.Candidate.Aim with {ReleaseHeight=takeoff*time-.5f*physics.PlayerGravity*time*time,
                        VerticalSpeed=takeoff-physics.PlayerGravity*time};
                    foreach(int unit in Evaluate(delayed,false,false,true))yield return unit;
                    trials++;if(last?.GeometryAccepted==true)accepted++;
                }
            // These counts measure only stability of the geometric screening,
            // not the probability that CS2 will flash/protect these players.
            foreach(int unit in Evaluate(center.Candidate.Aim,false,true,true))yield return unit;
            if(last is not {GeometryAccepted:true} fresh || !RouteFreshnessCheck.SameFlight(fresh.Candidate.Trajectory,center.Candidate.Trajectory)) continue;
            var reviewedCandidate=fresh with {Candidate=fresh.Candidate with {RobustHits=accepted,RobustTrials=trials}};
            Keep(matches,reviewedCandidate,options.ResultCount);
            if(publishedCandidates.Count<options.ResultCount && !publishedCandidates.Any(c=>c.Candidate.Aim==reviewedCandidate.Candidate.Aim))
                publishedCandidates=Array.AsReadOnly(publishedCandidates.Append(reviewedCandidate).ToArray());
        }
    }

    private IEnumerable<int> Work()
    {
        foreach(var aim in InitialAims())
        {
            if(!Budget)break;
            foreach(int unit in Evaluate(aim,true))yield return unit;
        }
        if(hits.Count>0)foreach(int unit in Review(finishRequested ? ReviewBatchCount : 2))yield return unit;
        Phase="flash-all-directions";
        foreach(var aim in GlobalAims())
        {
            if(!Budget)break;
            foreach(int unit in Evaluate(aim,true))yield return unit;
        }
        if(hits.Count==0)
        {
            Phase="flash-angle-gaps";
            foreach(var aim in GapAims())
            {
                if(!Budget)break;
                foreach(int unit in Evaluate(aim,true))yield return unit;
            }
        }
        // Keep collision branches alongside useful direct samples: an aggregate
        // ranking alone can discard a bank before its nearby angle is refined.
        foreach(float step in new[]{4f,1f,.25f})
        {
            if(!Budget)break;
            Phase="flash-refine";
            var seeds=hits.Take(8).Concat(bankSeeds.Values.OrderBy(c=>c,Comparer<FlashSearchCandidate>.Create(Order)).Take(8))
                .Concat(nearest.Take(8)).DistinctBy(c=>c.Candidate.Aim).ToArray();
            foreach(var seed in seeds)
                foreach(var (dp,dy) in new[]{(-step,-step),(-step,0f),(-step,step),(0f,-step),(0f,step),(step,-step),(step,0f),(step,step)})
                {
                    if(!Budget)break;
                    var a=seed.Candidate.Aim;
                    foreach(int unit in Evaluate(a with {Pitch=Math.Clamp(a.Pitch+dp,-89,89),Yaw=a.Yaw+dy},true))yield return unit;
                }
        }
        foreach(int unit in Review(ReviewBatchCount*3))yield return unit;
        // An early reviewed candidate can be older than the final scan. Recheck
        // exact nominal geometry before publication without using rounded caches.
        Phase="flash-publish";
        foreach(var old in matches.ToArray())
        {
            foreach(int unit in Evaluate(old.Candidate.Aim,false,true,true))yield return unit;
            matches.Remove(old);
            if(last is {GeometryAccepted:true} current && RouteFreshnessCheck.SameFlight(current.Candidate.Trajectory,old.Candidate.Trajectory))
                Keep(matches,current with {Candidate=current.Candidate with
                    {RobustHits=old.Candidate.RobustHits,RobustTrials=old.Candidate.RobustTrials}},options.ResultCount);
        }
        Result=new(physics.Id,Evaluated,finishRequested || Evaluated>=options.MaxCandidates,
            Array.AsReadOnly(matches.ToArray()),Array.AsReadOnly(nearest.Take(6).ToArray()));
        Phase="done";
    }
}
