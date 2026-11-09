using System.Numerics;

namespace NadeLab.Core;

public sealed partial class LineupSearch : IDisposable
{
    private ICollisionWorld world;
    private readonly PhysicsProfile physics;
    private readonly ThrowStart start;
    private readonly Target target;
    private SearchOptions options;
    private readonly List<Candidate> nearest = [], directSeeds = [], bankSeeds = [], hits = [], bankHits = [], directSamples = [];
    private readonly HashSet<Aim> visited = [];
    // Endpoint-only snapshots, valid only within this world generation. A
    // duplicate still yields to the frame budget, but refiners get its result.
    private readonly Dictionary<Aim,Candidate?> nominalCache=[];
    private int duplicateProposals,cachedResults,revalidatedAngles;
    private IReadOnlyList<Aim> families;
    private readonly Dictionary<Aim,Candidate> familySeeds=[];
    private readonly Dictionary<ThrowAction,Candidate> actionHits=[];
    private Func<Aim,bool>? canRunUp;
    private Func<Aim,Vector3?>? runUpPosition;
    private Func<Aim,bool>? rejectedByEvidence;
    private Func<Aim,IEnumerable<Vector3?>>? runUpSteps;
    private IEnumerator<int> work;
    private bool disposed;
    private int actionRejected,runUpRejected,blockedFlights,windowReviews;
    private readonly float heading;
    private readonly Vector3 horizontal;
    private readonly List<Candidate> matches=[];
    private readonly HashSet<Aim> reviewed=[];
    private readonly Dictionary<(ThrowAction,ThrowMode),int[]> coverage=[];
    private int resumePasses;
    private int stabilityProbes;
    private int stageRepairs;
    private readonly HashSet<Aim> stabilitySeeds=[];
    private Candidate? lastCandidate;
    private int[] Counts(Aim aim)
    {
        var key=(aim.EffectiveAction,aim.Mode);
        if(!coverage.TryGetValue(key,out var counts)) coverage[key]=counts=new int[4];
        return counts;
    }
    private bool Recommended(Candidate c) => SearchQuality.Recommended(c);
    private bool HasReliable => target.IsArea ? SimpleAreaHit : hits.Count>0;
    private bool finishRequested;
    private bool finishingReviewStarted;
    private bool seedSatisfied;
    public PhysicsProfile Profile => physics;
    public int InitialHintCount { get; }
    public int Evaluated { get; private set; }
    public string Phase { get; private set; } = "direct";
    public bool Done { get; private set; }
    public SearchResult? Result { get; private set; }
    // Fully reviewed so far; final publication still checks current geometry.
    public int ReviewedRecommendedCount => matches.Count(Recommended);
    // A low-tolerance hit is still a useful result for the player. It has the
    // same completed nominal collision check and perturbation review; only its
    // measured tolerance score differs. Final publication rechecks it again.
    public int ReviewedCandidateCount => matches.Count;

    public LineupSearch(ICollisionWorld world, PhysicsProfile physics, ThrowStart start, Target target, SearchOptions? options = null, Func<Aim,bool>? canRunUp = null, Func<Aim,Vector3?>? runUpPosition = null,Func<Aim,bool>? rejectedByEvidence=null,Func<Aim,IEnumerable<Vector3?>>? runUpSteps=null,
        Func<ThrowStart,Aim,IEnumerable<bool?>>? executionSpace=null)
    {
        this.world=world; this.physics=physics; this.start=start; this.target=target.Snapshot();
        this.options=options is null ? new() : options with {SeedAims=options.SeedAims?.ToArray()};
        InitialHintCount=this.options.SeedAims?.Count ?? 0;
        this.canRunUp=canRunUp;
        this.runUpPosition=runUpPosition;
        this.rejectedByEvidence=rejectedByEvidence;
        this.runUpSteps=runUpSteps;
        this.executionSpace=executionSpace;
        if(!ThrowExecution.Valid(start)) throw new ArgumentException("Invalid throwing origin.");
        physics.Validate(); this.options.Validate();
        target.Validate();
        heading=MathF.Atan2(target.Position.Y-start.Feet.Y,target.Position.X-start.Feet.X)*180/MathF.PI;
        horizontal=Geometry.Direction(0,heading);
        families=AvailableFamilies(new Vector2(target.Position.X-start.Feet.X,target.Position.Y-start.Feet.Y).Length());
        work=Work().GetEnumerator();
    }
    // One physics step, at most four collision queries, per advancement.
    public bool Advance()
    {
        if (Done) return false;
        if (work.MoveNext()) return true;
        Done=true; return false;
    }
    // A time-limited search still publishes complete, rechecked candidates.
    public void RequestFinish()
    {
        finishRequested=true;
        // A currently running review still completes; don't start a fresh
        // batch after the deadline when evidence has already been reviewed.
        if(matches.Count>0) finishingReviewStarted=true;
    }
    public void Dispose() { work.Dispose();foreach(var scan in jumpScans.Values) scan.Dispose();jumpScans.Clear();Done=true;disposed=true; }
    public bool CanContinue => Done && !disposed && Evaluated<43000;
    public int Continuations { get; private set; }
    public void ContinueExpanded(ICollisionWorld currentWorld,Func<Aim,bool>? actionFilter=null,
        Func<Aim,IEnumerable<Vector3?>>? runUpFilter=null,Func<Aim,bool>? evidenceFilter=null,
        Func<ThrowStart,Aim,IEnumerable<bool?>>? executionSpace=null)
    {
        if(!CanContinue) throw new InvalidOperationException("Search cannot be continued.");
        world=currentWorld;canRunUp=actionFilter;runUpSteps=runUpFilter;runUpPosition=null;rejectedByEvidence=evidenceFilter;
        this.executionSpace=executionSpace;
        nominalCache.Clear();
        publishedCandidates=Array.Empty<Candidate>();
        qualityContinuationLimit=null;
        options=options with {Exhaustive=true,IncludeActions=true,YawSpan=180,YawStep=15,MaxCandidates=Evaluated+7000,SeedAims=null};
        families=AvailableFamilies(Vector3.Dot(target.Position-start.Feet,horizontal));
        // Keep angles already explored and useful nominal seeds. Prior scores
        // are not evidence after a pause: fully recheck the retained finalists.
        foreach(var c in matches) Keep(hits,c,options.ResultCount*6,NominalOrder);
        matches.Clear();reviewed.Clear();stabilitySeeds.Clear();stageRepairs=0;windowReviews=0;
        finishRequested=false;finishingReviewStarted=false;seedSatisfied=false;Done=false;Result=null;Continuations++;
        work.Dispose();work=Work().GetEnumerator();Phase="continue";
    }
    private bool Budget => !finishRequested && !seedSatisfied && Evaluated<Math.Min(options.MaxCandidates,qualityContinuationLimit ?? int.MaxValue);
    // A missing body-path validator makes this action unavailable, so it must
    // not consume route-lane proposal slots before being rejected downstream.
    private Aim[] AvailableFamilies(float distance)=>ThrowCatalog.Families(physics,options,distance,heading)
        .Where(a=>executionSpace is not null || a.EffectiveAction!=ThrowAction.AirForwardJump).ToArray();
    private bool SimpleAreaHit => target.IsArea && SearchQuality.EnoughChoices(matches,target,options.ResultCount);

    private static IEnumerable<float> Offsets(float radius,float step) =>
        Enumerable.Range(0,(int)MathF.Floor(2*radius/step)+1).Select(i=>-radius+i*step)
            .Append(0).Append(radius).Distinct().OrderBy(MathF.Abs);

    private IEnumerable<Aim> DirectAims()
    {
        float distance=Vector3.Dot(target.Position-start.Feet,horizontal);
        if(options.IncludeActions && distance>1500)
        {
            // A roof/window route occupies a narrow heading cone. Sample the
            // viable long-range launches before spending the budget on banks
            // and on short underhand throws that cannot cross this distance.
            // Window openings can be narrower than the old 1.5-degree yaw / 
            // 4-degree pitch grid. Cover the central cone finely before banks.
            foreach(float yaw in Offsets(12,.5f))
                foreach(float pitch in Enumerable.Range(0,25).Select(i=>-15f-i*1.5f).OrderBy(p=>MathF.Abs(p+33)))
                    foreach(var family in families.Where(a=>a.Mode==ThrowMode.Left && a.Airborne))
                        yield return family with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(heading+yaw)};
        }
        foreach (var mode in Enum.GetValues<ThrowMode>())
        {
            var aim=new Aim(0,heading,mode);
            float speed=physics.ThrowSpeed*(.3f+.7f*aim.Strength);
            float dz=target.Position.Z-(start.Feet.Z+start.EyeHeight+aim.Strength*12-12);
            float square=speed*speed;
            float disc=square*square-physics.Gravity*(physics.Gravity*distance*distance+2*dz*square);
            if (distance<1 || disc<0) continue;
            foreach (float sign in new[]{-1f,1f})
            {
                float physicalPitch=-MathF.Atan((square+sign*MathF.Sqrt(disc))/(physics.Gravity*distance))*180/MathF.PI;
                float pitch=(physicalPitch+10)*(physicalPitch<=-10 ? 9f/8 : 9f/10);
                foreach (float offset in Offsets(3,1))
                    if (pitch+offset is >=-89 and <=89) yield return new(pitch+offset,heading,mode);
            }
        }
        // Landing/rolling shifts a ballistic guess. Cover low and high arcs along
        // the exact target heading before exploring bank shots.
        for (float pitch=-85;pitch<=75;pitch+=options.IncludeActions ? 8 : 4)
            foreach (var family in families) yield return family with {Pitch=pitch};
    }
    private IEnumerable<int> ExploreHighArcs()
    {
        // Sample both pitch bands and off-heading roof routes before the dense
        // long-range cone can consume the time budget. Keep the recorded feet.
        if(!options.IncludeActions || Vector3.Dot(target.Position-start.Feet,horizontal)<=1500) yield break;
        var highSeeds=new List<Candidate>();
        // Share the previous sampling allowance between controlled jump recipes.
        int highAllowance=Offsets(MathF.Min(options.YawSpan,60),6).Count()*8;
        foreach(var proposal in HighProposals(target.Position).Take(highAllowance))
            {
                if(!Budget) yield break;
                foreach(int unit in Evaluate(proposal)) yield return unit;
                if(lastCandidate is { } c) Keep(highSeeds,c,4,(a,b)=>Objective(a).CompareTo(Objective(b)));
            }
        // A coarse roof opening rarely lands on its center. Refine the best
        // collision-tested seeds while this lane still has an explicit budget.
        int limit=Math.Min(options.MaxCandidates,Evaluated+160);
        foreach(float step in new[]{1.5f,.5f})
            foreach(var seed in highSeeds.ToArray())
                foreach(float dp in Offsets(step==1.5f ? 3 : 1.5f,step))
                    foreach(float dy in Offsets(step==1.5f ? 3 : 1.5f,step))
                    {
                        if(!Budget || Evaluated>=limit) break;
                        foreach(int unit in Evaluate(seed.Aim with {Pitch=seed.Aim.Pitch+dp,Yaw=Geometry.NormalizeYaw(seed.Aim.Yaw+dy)})) yield return unit;
                        if(lastCandidate is { } c) Keep(highSeeds,c,4,(a,b)=>Objective(a).CompareTo(Objective(b)));
                    }
        foreach(int unit in StageCheckpoint(false)) yield return unit;
    }
    private IEnumerable<Aim> Coarse(float span,float yawStep,float pitchStep)
    {
        var selected=options.IncludeActions ? familySeeds.Values.OrderBy(Objective).Select(c=>Family(c.Aim)).Take(6).ToArray() : families.ToArray();
        if(selected.Length==0) selected=families.Take(6).ToArray();
        var pitches=Enumerable.Range(0,(int)MathF.Floor(160/pitchStep)+1).Select(i=>-85+i*pitchStep).OrderBy(p=>MathF.Abs(p+20)).ToArray();
        foreach (float dy in Offsets(span,yawStep))
            foreach(float pitch in pitches)
                foreach (var family in selected)
                    yield return family with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(heading+dy)};
    }
    private static Aim Family(Aim a) => a with {Pitch=0,Yaw=0,Action=a.EffectiveAction};
    private static Aim AimKey(Aim a) => a with {Pitch=MathF.Round(a.Pitch*100)/100,
        Yaw=Geometry.NormalizeYaw(MathF.Round(Geometry.NormalizeYaw(a.Yaw)*100)/100),Action=a.EffectiveAction};
    private static bool Similar(Candidate a,Candidate b) => Family(a.Aim)==Family(b.Aim) &&
        a.Trajectory.CollisionRoute==b.Trajectory.CollisionRoute && Math.Min(3,a.Trajectory.WallBounces)==Math.Min(3,b.Trajectory.WallBounces) &&
        MathF.Abs(a.Aim.Pitch-b.Aim.Pitch)<1 && MathF.Abs(Geometry.NormalizeYaw(a.Aim.Yaw-b.Aim.Yaw))<1;
    private float Objective(Candidate c) => target.IsArea ? target.MissDistance(c.Trajectory.Endpoint)+MathF.Max(0,.3f-target.InteriorMargin(c.Trajectory.Endpoint))*8+c.Error*.001f : c.Error;
    private int NominalOrder(Candidate a,Candidate b)=>CompareCandidates(a,b,false);
    private int RouteOrder(Candidate a,Candidate b)=>CompareCandidates(a,b,true);
    private int CompareCandidates(Candidate a,Candidate b,bool reviewedOrder)
    {
        // Before review there is no reliability evidence. Preserve the coarse
        // sampling order; independent route lanes protect bank alternatives.
        // After review, contacts must not outrank measured reproducibility.
        int compare=0;
        if(!reviewedOrder)
        {
            compare=a.Trajectory.WallBounces.CompareTo(b.Trajectory.WallBounces);
            if(compare!=0) return compare;
        }
        compare=b.Reliability.CompareTo(a.Reliability);
        if (compare!=0) return compare;
        if(target.IsArea)
        {
            // Prefer adequate interior clearance before convenience or view turn.
            compare=(target.InteriorMargin(b.Trajectory.Endpoint)>=.3f).CompareTo(target.InteriorMargin(a.Trajectory.Endpoint)>=.3f);
            if(compare!=0) return compare;
            compare=SceneAdvisor.ActionCost(a.Aim).CompareTo(SceneAdvisor.ActionCost(b.Aim));
            if(compare!=0) return compare;
            if(options.PreferredView is { } view)
            {
                compare=SceneAdvisor.ViewTurn(a.Aim,view).CompareTo(SceneAdvisor.ViewTurn(b.Aim,view));
                if(compare!=0) return compare;
            }
        }
        compare=a.Trajectory.WallBounces.CompareTo(b.Trajectory.WallBounces);
        if(compare!=0) return compare;
        // Accuracy first within the same reliability/convenience class.
        compare=a.Error.CompareTo(b.Error);
        if (compare!=0) return compare;
        compare=a.Aim.ForwardSpeed.CompareTo(b.Aim.ForwardSpeed);
        return compare!=0 ? compare : a.Trajectory.Time.CompareTo(b.Trajectory.Time);
    }
    private void Keep(List<Candidate> list,Candidate candidate,int count,Comparison<Candidate> order)
    {
        var nearby=list.FirstOrDefault(c=>Similar(c,candidate));
        if (nearby is not null)
        {
            if (order(nearby,candidate)<=0) return;
            list.Remove(nearby);
        }
        list.Add(candidate); list.Sort(order);
        if (list.Count>count) list.RemoveAt(list.Count-1);
    }
    private IEnumerable<int> Evaluate(Aim aim,bool refreshMissing=false)
    {
        lastCandidate=null;
        var key=AimKey(aim);
        if(!Budget) yield break;
        if(!visited.Add(key))
        {
            duplicateProposals++;
            if(nominalCache.TryGetValue(key,out var known))
            {lastCandidate=known;cachedResults++;yield return 0;yield break;}
            if(!refreshMissing) {yield return 0;yield break;}
            revalidatedAngles++;
        }
        nominalCache[key]=null;
        if(activeLane is { } lane) {lane.Attempted++;aimLanes[key]=lane;}
        var counts=Counts(aim); counts[0]++;
        yield return 0;
        if (canRunUp is not null && !canRunUp(aim)) {actionRejected++;yield break;}
        yield return 0;
        Vector3? runUp=null;
        if(aim.NeedsRunUp && start.Origin==ThrowOrigin.Preparation) runUp=start.Feet;
        else if(aim.NeedsRunUp && runUpSteps is not null)
        {
            foreach(var point in runUpSteps(aim)) {runUp=point; yield return 0;}
            if(runUp is null || !Geometry.Finite(runUp.Value)) {runUpRejected++;yield break;}
        }
        else if(aim.NeedsRunUp && runUpPosition is not null)
        {
            runUp=runUpPosition(aim);
            yield return 0;
            if(runUp is null || !Geometry.Finite(runUp.Value)) {runUpRejected++;yield break;}
        }
        foreach(int unit in SimulateChecked(start,aim,recordPoints:false)) yield return unit;
        if(sampleSimulation is not { } sim) {runUpRejected++;yield break;}
        Evaluated++; counts[1]++;
        if(activeLane is not null) activeLane.Evaluated++;
        var trajectory=sim.Result();
        if (!physics.Successful(trajectory.Status)) {if(trajectory.Status==SimulationStatus.Blocked) blockedFlights++;yield break;}
        var candidate=new Candidate(aim,trajectory,Vector3.Distance(trajectory.Endpoint,target.Position),RunUpFeet:runUp);
        var family=Family(aim);
        if(!familySeeds.TryGetValue(family,out var previous) || Objective(candidate)<Objective(previous)) familySeeds[family]=candidate;
        lastCandidate=candidate;
        nominalCache[key]=candidate;
        jumpBranches.Observe(candidate,Objective);
        if(activeLane is not null) Keep(activeLane.Seeds,candidate,4,(a,b)=>Objective(a).CompareTo(Objective(b)));
        Keep(nearest,candidate,options.SeedCount,(a,b)=>Objective(a).CompareTo(Objective(b)));
        if (trajectory.WallBounces==0) Keep(directSeeds,candidate,options.SeedCount,(a,b)=>Objective(a).CompareTo(Objective(b)));
        else Keep(bankSeeds,candidate,options.SeedCount,(a,b)=>Objective(a).CompareTo(Objective(b)));
        if (target.Accepts(world,trajectory.Endpoint) && rejectedByEvidence?.Invoke(aim)!=true)
        {
            counts[2]++;
            jumpBranchHits.Observe(candidate,Objective);
            if(activeLane is not null) {activeLane.NominalHits++;Keep(activeLane.Hits,candidate,8,NominalOrder);}
            if(!actionHits.TryGetValue(aim.EffectiveAction,out var actionHit) || NominalOrder(candidate,actionHit)<0) actionHits[aim.EffectiveAction]=candidate;
            Keep(hits,candidate,options.ResultCount*3,NominalOrder);
            if (trajectory.WallBounces>0) Keep(bankHits,candidate,options.ResultCount*2,NominalOrder);
        }
    }
    private float SignedRange(Candidate c) => Vector3.Dot(c.Trajectory.Endpoint-target.Position,Geometry.Direction(0,c.Aim.Yaw));
    private IEnumerable<int> DirectRoots()
    {
        // Bracketed range refinement avoids a dense yaw/pitch grid; all midpoint
        // trajectories still use real collision queries.
        foreach (var group in directSamples.GroupBy(c=>(Family:Family(c.Aim),Yaw:AimKey(c.Aim).Yaw,c.Trajectory.CollisionRoute,c.Trajectory.WallBounces)).ToArray())
        {
            var samples=group.DistinctBy(c=>AimKey(c.Aim)).OrderBy(c=>c.Aim.Pitch).ToArray();
            int brackets=0;
            for (int i=1;i<samples.Length && Budget;i++)
            {
                var a=samples[i-1]; var b=samples[i];
                if(Continuations>0)
                {
                    foreach(int unit in Evaluate(a.Aim,true)) yield return unit;
                    if(lastCandidate is not { } currentA) continue;
                    foreach(int unit in Evaluate(b.Aim,true)) yield return unit;
                    if(lastCandidate is not { } currentB) continue;
                    a=currentA;b=currentB;
                }
                if (a.Trajectory.CollisionRoute!=b.Trajectory.CollisionRoute || a.Trajectory.WallBounces!=b.Trajectory.WallBounces || SignedRange(a)*SignedRange(b)>0 || b.Aim.Pitch-a.Aim.Pitch>(options.IncludeActions ? 10 : 6)) continue;
                if (++brackets>8) break;
                for (int n=0;n<12 && Budget && b.Aim.Pitch-a.Aim.Pitch>.03f;n++)
                {
                    var aim=group.Key.Family with {Pitch=(a.Aim.Pitch+b.Aim.Pitch)/2,Yaw=group.Key.Yaw};
                    foreach (int unit in Evaluate(aim,true)) yield return unit;
                    if (lastCandidate is not { } c || c.Trajectory.WallBounces!=a.Trajectory.WallBounces || c.Trajectory.CollisionRoute!=a.Trajectory.CollisionRoute) break;
                    if (target.IsArea ? target.Accepts(world,c.Trajectory.Endpoint) : c.Error<=MathF.Max(1,target.Radius*.15f)) break;
                    if (SignedRange(a)*SignedRange(c)<=0) b=c; else a=c;
                }
            }
        }
    }
    private IEnumerable<int> Refine(float pitchRadius,float yawRadius,int allocation)
    {
        var seeds=Enumerable.Range(0,options.SeedCount)
            .SelectMany(i=>new[]{nearest.ElementAtOrDefault(i),directSeeds.ElementAtOrDefault(i),bankSeeds.ElementAtOrDefault(i)})
            .Where(c=>c is not null).Select(c=>c!)
            .Concat(options.IncludeActions ? familySeeds.Values.OrderBy(Objective).Take(options.SeedCount) : []).DistinctBy(c=>AimKey(c.Aim)).ToArray();
        foreach(int unit in AdaptiveRefine(seeds.Select(c=>((RouteLane?)null,c)),Math.Clamp(pitchRadius,.5f,6),Math.Clamp(yawRadius,.5f,9),allocation)) yield return unit;
    }
    private IEnumerable<int> HeadingRefine()
    {
        int limit=Math.Min(options.MaxCandidates,Evaluated+180);
        var seeds=directSamples.GroupBy(c=>Family(c.Aim))
            .SelectMany(g=>g.OrderBy(Objective).Take(2)).OrderBy(Objective).ToArray();
        foreach(var seed in seeds)
            foreach(float dp in Offsets(2,.5f))
                foreach(float dy in Offsets(1,.5f))
                {
                    if(!Budget || Evaluated>=limit) yield break;
                    float pitch=seed.Aim.Pitch+dp;
                    if(pitch is <-89 or >89) continue;
                    foreach(int unit in Evaluate(seed.Aim with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(seed.Aim.Yaw+dy)})) yield return unit;
                }
    }
    private IEnumerable<int> PolishNearMisses()
    {
        int limit=Math.Min(options.MaxCandidates,Evaluated+100);
        var seeds=nearest.Where(c=>(target.IsArea ? target.MissDistance(c.Trajectory.Endpoint) : c.Error)<=target.Radius*2).OrderBy(Objective).Take(3).ToArray();
        (float p,float y)[] offsets=[(1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1)];
        foreach(var seed in seeds)
        {
            foreach(int unit in Evaluate(seed.Aim,true)) yield return unit;
            if(lastCandidate is not { } best) continue;
            foreach(float step in new[]{1.5f,.5f,.15f,.05f})
            {
                var center=best;
                foreach(var (p,y) in offsets)
                {
                    if(!Budget || Evaluated>=limit) yield break;
                    var aim=center.Aim with {Pitch=Math.Clamp(center.Aim.Pitch+p*step,-89,89),Yaw=Geometry.NormalizeYaw(center.Aim.Yaw+y*step)};
                    foreach(int unit in Evaluate(aim,true)) yield return unit;
                    if(lastCandidate is { } c && Objective(c)<Objective(best)) best=c;
                }
                if(target.IsArea ? target.Accepts(world,best.Trajectory.Endpoint) && target.InteriorMargin(best.Trajectory.Endpoint)>=.3f : best.Error<=MathF.Max(3,target.Radius*.2f)) break;
            }
        }
    }
    private IEnumerable<int> CompareMovingRoutes()
    {
        // Run before the long-range scan. Each action/strength has its own coarse
        // AND refinement allowance, including strengths without an existing hit.
        foreach(var family in families.Where(a=>a.EffectiveAction is ThrowAction.Run or ThrowAction.Walk))
        {
            int attempts=0;
            foreach(float pitch in Enumerable.Range(0,16).Select(i=>-55f+i*5).OrderBy(p=>MathF.Abs(p+20)))
            {
                foreach(float offset in new[]{0f,-2f,2f})
                {
                    if(!Budget || attempts>=30) break;
                    attempts++;
                    foreach(int unit in Evaluate(family with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(heading+offset)})) yield return unit;
                }
                if(!Budget || attempts>=30) break;
            }
            if(!familySeeds.TryGetValue(Family(family),out var seed)) continue;
            attempts=0;
            foreach(float dp in Offsets(3,.5f))
            {
                foreach(float dy in Offsets(1,.5f))
                {
                    if(!Budget || attempts>=20) break;
                    attempts++;
                    foreach(int unit in Evaluate(seed.Aim with {Pitch=Math.Clamp(seed.Aim.Pitch+dp,-89,89),Yaw=Geometry.NormalizeYaw(seed.Aim.Yaw+dy)})) yield return unit;
                }
                if(!Budget || attempts>=20) break;
            }
        }
    }
    private IEnumerable<int> ReviewCandidates(int limit,IEnumerable<Candidate>? selected=null)
    {
        Phase="robustness";
        var finalists=(selected ?? hits.Concat(actionHits.Values).Concat(bankHits).Concat(lanes.Values.SelectMany(l=>l.Hits)).Concat(jumpBranchHits.Seeds(Objective))).Where(c=>!reviewed.Contains(AimKey(c.Aim))).DistinctBy(c=>AimKey(c.Aim))
            .GroupBy(c=>(Family(c.Aim),c.Trajectory.WallBounces))
            .Select(g=>g.OrderBy(c=>c,Comparer<Candidate>.Create(NominalOrder)).ToArray())
            .SelectMany(g=>g.Select((c,i)=>(c,i))).OrderBy(x=>x.i).ThenBy(x=>x.c,Comparer<Candidate>.Create(NominalOrder))
            .Select(x=>x.c).Take(limit).ToList();
        // Explicit expanded searches keep a bank-shot alternative without letting
        // it evict the preferred direct route.
        if (options.Exhaustive && options.ResultCount>1 && bankHits.Count>0 &&
            finalists.Count>1 && finalists.All(c=>c.Trajectory.WallBounces==0)) finalists[^1]=bankHits[0];
        foreach (var retained in finalists)
        {
            if(finishRequested)
            {
                if(finishingReviewStarted) yield break;
                finishingReviewStarted=true;
            }
            var candidate=retained;
            if(!reviewed.Add(AimKey(candidate.Aim))) continue;
            if(aimLanes.TryGetValue(AimKey(candidate.Aim),out var lane)) lane.Reviewed++;
            Counts(candidate.Aim)[3]++;
            hits.RemoveAll(c=>AimKey(c.Aim)==AimKey(candidate.Aim));
            if(rejectedByEvidence?.Invoke(candidate.Aim)==true) continue;
            if(canRunUp is not null && !canRunUp(candidate.Aim)) {actionRejected++;continue;}
            if(candidate.Aim.NeedsRunUp && start.Origin==ThrowOrigin.Preparation) candidate=candidate with {RunUpFeet=start.Feet};
            else if(candidate.Aim.NeedsRunUp && runUpSteps is not null)
            {
                Vector3? runUp=null;
                foreach(var point in runUpSteps(candidate.Aim)) {runUp=point;yield return 0;}
                if(runUp is null || !Geometry.Finite(runUp.Value)) {runUpRejected++;continue;}
                candidate=candidate with {RunUpFeet=runUp};
            }
            // Allocate polylines only for finalists; recheck the nominal path so
            // changed dynamic geometry cannot publish an obsolete hit.
            foreach(int unit in SimulateChecked(start,candidate.Aim)) yield return unit;
            if(sampleSimulation is not { } nominal) {runUpRejected++;continue;}
            // Sensitivity starts at the captured launch, not the completed endpoint.
            var nominalSource=sampleSource; var nominalVelocity=sampleVelocity;
            var full=nominal.Result();
            if (!physics.Successful(full.Status) || !target.Accepts(world,full.Endpoint)) continue;
            // The release-window grid repeats nominal and manual trials. Reuse
            // their exact inputs only inside this candidate's current review;
            // later candidates/continuations/publication still trace afresh.
            Dictionary<(ThrowStart Start,Aim Aim),bool>? releaseSamples=candidate.Aim.NeedsRunUp && windowReviews<2 ? new() : null;
            if(releaseSamples is not null) releaseSamples[(start,candidate.Aim)]=true;
            int hitCount=1,trials=1,executionHits=0,executionTrials=0,modelHits=0,modelTrials=0,sourceHits=0,sourceTrials=0;
            var checks=new RobustChecks();
            bool Check(string group,SmokeSimulation? sim)
            {
                bool hit=sim is not null && physics.Successful(sim.Status) && target.Accepts(world,sim.Position);
                float miss=sim is not null && physics.Successful(sim.Status) ? target.MissDistance(sim.Position) : target.Radius*4;
                checks.Observe(group,hit,hit ? 0 : MathF.Max(.01f,miss));
                return hit;
            }
            foreach(var (group,perturbedStart,perturbedAim) in ExecutionEnvelope.Cases(start,candidate.Aim))
            {
                foreach(int unit in SimulateChecked(perturbedStart,perturbedAim,recordPoints:false)) yield return unit;
                trials++;
                if (Check(group,sampleSimulation)) hitCount++;
            }
            foreach(var (group,executionStart,executionAim) in ManualExecution.Cases(start,candidate.Aim,physics))
            {
                foreach(int unit in SimulateChecked(executionStart,executionAim,recordPoints:false)) yield return unit;
                trials++; executionTrials++;
                bool hit=Check(group,sampleSimulation);
                if(releaseSamples is not null) releaseSamples[(executionStart,executionAim)]=hit;
                if(hit) {hitCount++; executionHits++;}
            }
            if(candidate.Aim.Airborne)
                foreach(float delta in new[]{-.25f,.25f})
                {
                    // Release timing inside the engine latch window does not
                    // change the jump phase. Test observed latch jitter instead.
                    var aim=candidate.Aim with {ReleaseHeight=candidate.Aim.ReleaseHeight+delta,VerticalSpeed=candidate.Aim.VerticalSpeed+delta};
                    foreach(int unit in SimulateChecked(start,aim,recordPoints:false)) yield return unit;
                    trials++;
                    if(Check("latch",sampleSimulation)) hitCount++;
                }
            if(target.IsArea)
                foreach(var (source,variant) in TrajectorySensitivity.SurfaceCases(nominalSource,physics,full.Bounces))
                {
                    var sim=new SmokeSimulation(world,variant,source,nominalVelocity,recordPoints:false);
                    yield return 0;
                    while(!sim.Done) {sim.Step();yield return 0;}
                    modelTrials++;
                    if(Check("surface",sim)) modelHits++;
                }
            if(target.IsArea)
                foreach(var (source,variant) in TrajectorySensitivity.SourceCases(nominalSource,candidate.Aim,physics))
                {
                    var sim=new SmokeSimulation(world,variant,source,nominalVelocity,recordPoints:false);
                    yield return 0;
                    while(!sim.Done) {sim.Step();yield return 0;}
                    sourceTrials++;
                    if(Check("source",sim)) sourceHits++;
                }
            ReleaseWindow? window=null;
            if(!finishRequested && candidate.Aim.NeedsRunUp && windowReviews<2 && (float)hitCount/trials>=.75f &&
                (executionTrials==0 || (float)executionHits/executionTrials>=.75f) && (modelTrials==0 || (float)modelHits/modelTrials>=.75f))
            {
                windowReviews++;
                bool[] samples=[true,true,true,true,true];
                foreach(var trial in ReleaseWindow.Cases(start,candidate.Aim,physics))
                {
                    if(!samples[trial.Index]) continue;
                    if(releaseSamples is not null && releaseSamples.TryGetValue((trial.Start,trial.Aim),out bool known))
                    {samples[trial.Index]=known;yield return 0;continue;}
                    foreach(int unit in SimulateChecked(trial.Start,trial.Aim,recordPoints:false)) yield return unit;
                    samples[trial.Index]=sampleSimulation is { } sim && physics.Successful(sim.Status) && target.Accepts(world,sim.Position);
                }
                window=ReleaseWindow.FromSamples(samples,candidate.Aim.ForwardSpeed,MathF.Max(15,candidate.Aim.ForwardSpeed*.1f));
            }
            // Review can span many game frames. Publish only after a fresh nominal
            // collision/path check; do not expose an angle that just hit once.
            foreach(int unit in SimulateChecked(start,candidate.Aim)) yield return unit;
            if(sampleSimulation is not { } currentNominal) continue;
            var currentTrajectory=currentNominal.Result();
            if(!physics.Successful(currentTrajectory.Status) || !target.Accepts(world,currentTrajectory.Endpoint) ||
                !RouteFreshnessCheck.SameFlight(currentTrajectory,full)) continue;
            var reviewedCandidate=candidate with {Trajectory=currentTrajectory,Error=Vector3.Distance(currentTrajectory.Endpoint,target.Position),RobustHits=hitCount,RobustTrials=trials,
                ExecutionHits=executionHits,ExecutionTrials=executionTrials,ModelHits=modelHits,ModelTrials=modelTrials,SourceStressHits=sourceHits,SourceStressTrials=sourceTrials,Checks=checks.Results(),ReleaseWindow=window};
            matches.Add(reviewedCandidate);
            PublishReviewed(reviewedCandidate);
        }
    }

    private IEnumerable<int> Work()
    {
        if(options.SeedAims is {Count:>0} priorAims)
        {
            Phase="prior-routes";
            foreach(var aim in priorAims)
            {
                if(!Budget) break;
                // Import only the old angle/action identity. Current request
                // families own launch speed, jump phase and release offset.
                // Wide-only actions cannot leak into quick search via history.
                var family=families.Where(f=>f.Mode==aim.Mode && f.EffectiveAction==aim.EffectiveAction)
                    .OrderBy(f=>MathF.Abs(f.ForwardSpeed-aim.ForwardSpeed)).Select(f=>(Aim?)f).FirstOrDefault();
                if(family is not { } current) continue;
                foreach(int unit in Evaluate(current with {Pitch=aim.Pitch,Yaw=Geometry.NormalizeYaw(aim.Yaw)})) yield return unit;
            }
            foreach(int unit in ReviewCandidates(ReviewBatchCount)) yield return unit;
            // Quick repeat queries may finish once two fully reviewed options
            // survive. Expanded research still explores the rest of the space.
            seedSatisfied=!options.Exhaustive && SimpleAreaHit;
        }
        if(target.IsArea && Budget)
        {
            foreach(int unit in ExploreRouteLanes()) yield return unit;
        }
        if(!target.IsArea && (options.IncludeActions || options.IncludeForward))
        {
            Phase="compare-moving";
            foreach(int unit in CompareMovingRoutes()) yield return unit;
            if(!options.Exhaustive && Budget)
                foreach(int unit in ReviewCandidates(2)) yield return unit;
        }
        if(!target.IsArea && !HasReliable && Budget)
        {
            Phase="high-arcs";
            foreach(int unit in ExploreHighArcs()) yield return unit;
        }
        Phase="direct";
        int nextReview=0;
        int directLimit=options.IncludeActions ? Math.Min(options.MaxCandidates,Evaluated+Math.Max(120,options.MaxCandidates/5)) : options.MaxCandidates;
        int localRepairs=0;
        foreach (var aim in DirectAims())
        {
            if (!Budget || Evaluated>=directLimit) break;
            foreach (int unit in Evaluate(aim)) yield return unit;
            if (lastCandidate is { } c) directSamples.Add(c);
            if(!options.Exhaustive && target.IsArea && hits.Count>0 && Evaluated>=nextReview)
            {
                foreach(int unit in ReviewCandidates(2)) yield return unit;
                if(!SimpleAreaHit && Budget && localRepairs++<2)
                {
                    foreach(int unit in RefineReliability()) yield return unit;
                }
                nextReview=Evaluated+30;
                Phase="direct";
            }
            if(!options.Exhaustive && SimpleAreaHit) break;
        }
        if(!options.Exhaustive && SimpleAreaHit)
        {
            Phase="area-refine";
            foreach(int unit in Refine(2,2,40)) yield return unit;
        }
        Phase="direct-refine";
        foreach (int unit in DirectRoots()) yield return unit;
        if(!HasReliable && Budget)
        {
            Phase="heading-refine";
            foreach(int unit in HeadingRefine()) yield return unit;
        }
        foreach(int unit in StageCheckpoint(false)) yield return unit;
        if (options.Exhaustive || (target.IsArea ? !SimpleAreaHit : !hits.Any(c=>c.Trajectory.WallBounces==0)))
        {
            Phase="coarse";
            // If the heading scan already found a viable route, a quick search
            // spends a small additional budget looking for a cleaner alternative.
            int coarseLimit=!options.Exhaustive && hits.Count>0 ? Math.Min(options.MaxCandidates,Evaluated+160) :
                options.IncludeActions ? Math.Min(options.MaxCandidates,Evaluated+(options.Exhaustive ? 2200 : 650)) : options.MaxCandidates;
            foreach (var aim in Coarse(options.YawSpan,options.YawStep,options.PitchStep))
            {
                if (!Budget || Evaluated>=coarseLimit) break;
                foreach (int unit in Evaluate(aim)) yield return unit;
            }
            Phase="refine";
            foreach (int unit in Refine(options.PitchStep/2,options.YawStep/2,!options.Exhaustive && hits.Count>0 ? 120 : options.IncludeActions ? 400 : 500)) yield return unit;
            if(hits.Count==0 && nearest.Any(c=>c.Error<=target.Radius*2) && Budget)
            {
                Phase="near-refine";
                foreach(int unit in PolishNearMisses()) yield return unit;
            }
            foreach(int unit in StageCheckpoint()) yield return unit;
            if (options.GlobalFallback && options.YawSpan<180 && !HasReliable && Budget)
            {
                Phase="global";
                int globalLimit=options.IncludeActions ? Math.Min(options.MaxCandidates,Evaluated+1000) : options.MaxCandidates;
                foreach (var aim in Coarse(180,18,12))
                {
                    if (!Budget || Evaluated>=globalLimit) break;
                    foreach (int unit in Evaluate(aim)) yield return unit;
                }
                Phase="global-refine";
                foreach (int unit in Refine(6,9,options.IncludeActions ? 400 : 1000)) yield return unit;
                if(hits.Count==0 && nearest.Any(c=>c.Error<=target.Radius*2) && Budget)
                {
                    Phase="near-refine";
                    foreach(int unit in PolishNearMisses()) yield return unit;
                }
                foreach(int unit in StageCheckpoint()) yield return unit;
            }
        }
        if(hits.Count>0 && Budget && !target.IsArea)
        {
            Phase="precision";
            int limit=Math.Min(options.MaxCandidates,Evaluated+80);
            var seeds=hits.GroupBy(c=>(Family(c.Aim),c.Trajectory.WallBounces))
                .Select(g=>g.OrderBy(c=>c.Error).First()).Where(c=>c.Error>MathF.Max(3,target.Radius*.2f))
                .OrderBy(c=>c.Trajectory.WallBounces).ThenBy(c=>c.Error).Take(2).ToArray();
            foreach(var seed in seeds)
                foreach(float dp in Offsets(.4f,.1f))
                    foreach(float dy in Offsets(.2f,.1f))
                    {
                        if(!Budget || Evaluated>=limit) break;
                        float pitch=seed.Aim.Pitch+dp;
                        if(pitch is <-89 or >89) continue;
                        foreach(int unit in Evaluate(seed.Aim with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(seed.Aim.Yaw+dy)})) yield return unit;
                    }
        }
        foreach(int unit in ReviewCandidates(target.IsArea ? ReviewBatchCount*2 : ReviewBatchCount)) yield return unit;
        // A nominal hit is not a completed search. Resume exploration when the
        // rechecked pool is empty or contains too few usable alternatives.
        if(target.IsArea && !options.Exhaustive && Budget && !SimpleAreaHit)
        {
            using var remaining=DirectAims().Concat(Coarse(options.GlobalFallback ? 180 : options.YawSpan,12,8)).GetEnumerator();
            bool more=true;
            while(Budget && more && !SimpleAreaHit)
            {
                resumePasses++; Phase="resume-search";
                int attempts=0;
                while(Budget && attempts++<180 && (more=remaining.MoveNext()))
                    foreach(int unit in Evaluate(remaining.Current)) yield return unit;
                foreach(int unit in ReviewCandidates(ReviewBatchCount*2)) yield return unit;
                foreach(int unit in StageCheckpoint()) yield return unit;
            }
        }
        matches.Sort(RouteOrder);
        var experimental=matches.Where(c=>!Recommended(c)).OrderByDescending(c=>c.ExecutionReliability)
            .ThenByDescending(c=>c.Reliability).Take(options.ResultCount).ToArray();
        // Preserve the raw recommendation/evidence distinction. The player
        // presentation appends the separately labelled low-tolerance choices;
        // their complete collision and final geometry checks are identical.
        var ordered=SceneAdvisor.Order(target.IsArea ? matches.Where(Recommended).ToArray() : matches,options.PreferredView,target.IsArea,target);
        var published=ordered.Take(options.ResultCount).ToList();
        if(options.Exhaustive && published.Count>1 && published.All(c=>c.Trajectory.WallBounces==0) &&
            ordered.FirstOrDefault(c=>c.Trajectory.WallBounces>0) is { } bank) published[^1]=bank;
        Phase="publish-review";
        var fresh=new List<Candidate>();
        var freshExperiments=new List<Candidate>();
        foreach(var candidate in published.Concat(ordered).Concat(experimental).Distinct())
        {
            bool isExperiment=target.IsArea && !Recommended(candidate);
            if(fresh.Count>=options.ResultCount && (!isExperiment || freshExperiments.Count>=options.ResultCount)) continue;
            yield return 0;
            foreach(int unit in SimulateChecked(start,candidate.Aim)) yield return unit;
            if(sampleSimulation is not { } nominal) continue;
            var trajectory=nominal.Result();
            // Earlier checks may be old after resumed exploration. Changed
            // geometry invalidates their robustness evidence, even within area.
            if(physics.Successful(trajectory.Status) && target.Accepts(world,trajectory.Endpoint) &&
                RouteFreshnessCheck.SameFlight(trajectory,candidate.Trajectory))
            {
                var checkedCandidate=candidate with {Trajectory=trajectory};
                if(isExperiment && freshExperiments.Count<options.ResultCount) freshExperiments.Add(checkedCandidate);
                if(!isExperiment && fresh.Count<options.ResultCount) fresh.Add(checkedCandidate);
            }
        }
        published=fresh;
        experimental=freshExperiments.ToArray();
        Result=new(physics.Id,physics.Calibrated,Evaluated,finishRequested || Evaluated>=Math.Min(options.MaxCandidates,qualityContinuationLimit ?? int.MaxValue),published,nearest.ToArray(),coverage.Select(k=>new FamilyCoverage(k.Key.Item1,k.Key.Item2,k.Value[0],k.Value[1],k.Value[2],k.Value[3])).ToArray(),resumePasses,experimental,stabilityProbes,new(actionRejected,runUpRejected,blockedFlights),lanes.Values.Select(l=>new RouteCoverage(l.Name,l.Attempted,l.Evaluated,l.NominalHits,l.Reviewed)).ToArray(),new(duplicateProposals,cachedResults,refinementProposals,refinementMoves,revalidatedAngles));
        Phase="done";
    }
}

