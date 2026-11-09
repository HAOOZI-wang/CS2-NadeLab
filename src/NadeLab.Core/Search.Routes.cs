using System.Numerics;

namespace NadeLab.Core;

public sealed record RouteCoverage(string Lane,int Attempted,int Evaluated,int Hits,int Reviewed);

public sealed partial class LineupSearch
{
    private sealed class RouteLane(string name)
    {
        public string Name=name;
        public readonly List<Candidate> Seeds=[],Hits=[];
        public int Attempted,Evaluated,NominalHits,Reviewed;
    }
    private readonly Dictionary<string,RouteLane> lanes=[];
    private RouteLane? activeLane;
    private readonly Dictionary<Aim,RouteLane> aimLanes=[];
    private readonly CollisionBranches jumpBranches=new();
    private readonly CollisionBranches jumpBranchHits=new();
    private readonly Dictionary<Vector3,IEnumerator<Aim>> jumpScans=[];
    private int jumpReviewTurn;
    private RouteLane Lane(string name)
    {if(!lanes.TryGetValue(name,out var lane)) lanes[name]=lane=new(name);return lane;}

    // Ballistic roots are only proposal angles. Inherited player motion and the
    // game's pitch correction are included; every proposal then uses collisions.
    private IEnumerable<Aim> Toward(Vector3 point,IReadOnlyList<Aim> actions,bool highOnly=false)
    {
        var delta=point-start.Feet;float distance=new Vector2(delta.X,delta.Y).Length();
        if(distance<8) yield break;
        float yaw=MathF.Atan2(delta.Y,delta.X)*180/MathF.PI;
        foreach(var action in actions)
        {
            if(physics.Kind.Timed())
            {
                // Aim at the required displacement at the fixed effect time.
                // This is a seed only: strength/range and collisions are still
                // checked by the complete simulator and robustness review.
                float t=physics.FuseSeconds;
                var source=ThrowExecution.Resolve(start,action with {Yaw=yaw},physics).ReleaseFeet+new Vector3(0,0,action.EyeHeight(start)+action.Strength*12-12);
                var required=point-source-(action with {Yaw=yaw}).PlayerVelocity*physics.PlayerVelocityScale*t+Vector3.UnitZ*(.5f*physics.Gravity*t*t);
                float physical=-MathF.Atan2(required.Z,new Vector2(required.X,required.Y).Length())*180/MathF.PI;
                float aimPitch=(physical+10)*(physical<=-10 ? 9f/8 : 9f/10);
                if(!highOnly || aimPitch<-45)
                    foreach(float offset in new[]{0f,-3f,3f,-6f,6f})
                        yield return action with {Pitch=Math.Clamp(aimPitch+offset,-89,89),Yaw=yaw};
                continue;
            }
            float Residual(float pitch)
            {
                float corrected=pitch-(90-MathF.Abs(pitch))*10/90;
                var direction=Geometry.Direction(corrected,yaw);
                float speed=physics.ThrowSpeed*(.3f+.7f*action.Strength);
                float vx=direction.X*MathF.Cos(yaw*MathF.PI/180)+direction.Y*MathF.Sin(yaw*MathF.PI/180);
                vx=vx*speed+action.ForwardSpeed*physics.PlayerVelocityScale;
                if(vx<=1) return float.NaN;
                var release=ThrowExecution.Resolve(start,action with {Yaw=yaw},physics).ReleaseFeet;
                float offset=Vector3.Dot(release-start.Feet,Geometry.Direction(0,yaw));
                float t=MathF.Max(0,distance-offset-16)/vx;
                return release.Z+action.EyeHeight(start)+action.Strength*12-12+
                    (direction.Z*speed+action.VerticalSpeed*physics.PlayerVelocityScale)*t-.5f*physics.Gravity*t*t-point.Z;
            }
            float previous=-88,old=Residual(previous);
            for(float pitch=-86;pitch<=80;pitch+=2)
            {
                float value=Residual(pitch);
                if(float.IsFinite(value) && float.IsFinite(old) && value*old<=0)
                {
                    float low=previous,high=pitch;
                    for(int n=0;n<9;n++) {float mid=(low+high)/2;if(Residual(low)*Residual(mid)<=0)high=mid;else low=mid;}
                    float root=(low+high)/2;
                    if(!highOnly || root<-45) yield return action with {Pitch=root,Yaw=yaw};
                }
                previous=pitch;old=value;
            }
        }
    }
    private IEnumerable<Aim> Interleave(IEnumerable<IEnumerable<Aim>> groups)
    {
        var iterators=groups.Select(g=>g.GetEnumerator()).ToList();
        try
        {
            while(iterators.Count>0)
                for(int i=0;i<iterators.Count;)
                    if(iterators[i].MoveNext()) {yield return iterators[i].Current;i++;}
                    else {iterators[i].Dispose();iterators.RemoveAt(i);}
        }
        finally {foreach(var iterator in iterators) iterator.Dispose();}
    }
    private IEnumerable<Aim> HighProposals(Vector3 anchor)
    {
        float yaw=MathF.Atan2(anchor.Y-start.Feet.Y,anchor.X-start.Feet.X)*180/MathF.PI;
        var jumps=options.IncludeActions ? ControlledJumps() : [new Aim(0,yaw,ThrowMode.Left)];
        foreach(float offset in Offsets(MathF.Min(options.YawSpan,60),6))
            foreach(float pitch in new[]{-63f,-57f,-69f,-54f,-60f,-66f,-72f,-75f})
                foreach(var jump in jumps)
                yield return jump with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(yaw+offset)};
    }
    private Aim[] ControlledJumps()=>families.Where(a=>a.Mode==ThrowMode.Left &&
        a.EffectiveAction is ThrowAction.Jump or ThrowAction.AirForwardJump).ToArray();
    private IEnumerable<Aim> DirectProposals(Vector3 anchor)
    {
        float yaw=MathF.Atan2(anchor.Y-start.Feet.Y,anchor.X-start.Feet.X)*180/MathF.PI;
        // Include even strengths whose no-collision ballistic estimate cannot
        // reach: geometry can make a shorter/rolling route useful.
        foreach(var action in families) yield return action with {Pitch=-25,Yaw=yaw};
        foreach(var aim in Toward(anchor,families)) yield return aim;
    }
    private IEnumerable<Aim> JumpBankProposals(Vector3 anchor)
    {
        float yaw=MathF.Atan2(anchor.Y-start.Feet.Y,anchor.X-start.Feet.X)*180/MathF.PI;
        var jumps=ControlledJumps();
        // Moderate pitches can enter a window after several contacts even when
        // the high-arc lane and the single-plane mirror estimate cannot.
        // Retain the enumerator across continuations: don't repeat its prefix.
        IEnumerable<Aim> Local(float step)
        {
            foreach(float dy in Offsets(24,step==3 ? 2 : 1))
                foreach(float pitch in Enumerable.Range(0,(int)(60/step)+1).Select(i=>-6-i*step).OrderBy(p=>MathF.Abs(p+27)))
                    foreach(var jump in jumps)
                    yield return jump with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(yaw+dy)};
        }
        IEnumerable<Aim> Wide()
        {
            foreach(float dy in Offsets(180,12))
                foreach(float pitch in new[]{-24f,-36f,-12f,-48f,-60f,-72f,0f,12f})
                    foreach(var jump in jumps)
                    yield return jump with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(yaw+dy)};
        }
        IEnumerable<Aim> Downward()
        {
            // A close, low timed burst may require shedding range on the
            // ground and a wall. The upward-only local scan cannot seed that
            // contact sequence; the single-wall inverse cannot model it either.
            foreach(float dy in Offsets(180,15))
                foreach(float pitch in new[]{12f,24f,36f,48f,60f,72f})
                    foreach(var jump in jumps)
                    yield return jump with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(yaw+dy)};
        }
        // Allocate one of three existing jump-bank slots, without increasing
        // the lane or search budget. Preserve the old order for smoke and for
        // distant/high targets whose useful routes need their forward range.
        bool closeLow=physics.Kind.Timed() &&
            new Vector2(anchor.X-start.Feet.X,anchor.Y-start.Feet.Y).Length()<physics.ThrowSpeed*physics.FuseSeconds*.6f &&
            anchor.Z<jumps[0].ReleaseFeet(start).Z+jumps[0].EyeHeight(start);
        foreach(var aim in Interleave(closeLow ? [Local(3),Wide(),Downward()] : [Local(3),Wide()])) yield return aim;
        foreach(var aim in Local(1)) yield return aim;
    }
    private IEnumerable<Aim> ContinueJumpScan(Vector3 anchor)
    {
        if(!jumpScans.TryGetValue(anchor,out var scan)) jumpScans[anchor]=scan=JumpBankProposals(anchor).GetEnumerator();
        while(scan.MoveNext()) yield return scan.Current;
    }
    private Candidate[] LaneSeeds(RouteLane lane,int count)=>lane.Name=="jump-bank" ?
        jumpBranches.Seeds(Objective,count).Concat(lane.Seeds).DistinctBy(c=>AimKey(c.Aim)).Take(count).ToArray() : lane.Seeds.Take(count).ToArray();
    private IEnumerable<Aim> RestingBankProposals(Vector3 mirror,IReadOnlyList<Aim> actions)
    {
        float yaw=MathF.Atan2(mirror.Y-start.Feet.Y,mirror.X-start.Feet.X)*180/MathF.PI;
        // A ballistic root to an ideal reflected target ignores energy lost at
        // contact and rolling. Include shallow/downward stationary releases,
        // without requiring a no-contact ballistic root to exist first.
        foreach(float pitch in new[]{-24f,-12f,-36f,0f,12f,24f,36f,48f,60f,-48f})
            foreach(var action in actions.Where(a=>a.EffectiveAction is ThrowAction.Standing or ThrowAction.Crouch))
                yield return action with {Pitch=pitch,Yaw=Geometry.NormalizeYaw(yaw)};
    }
    private IEnumerable<Aim> MissingBankProposals(IEnumerable<Aim> proposals)
    {
        // Do not replace a productive bank branch with a broad pitch scan.
        // Let its existing contact-aware refinement keep its original budget.
        foreach(var aim in proposals)
        {
            if(Lane("bank").NominalHits>0) yield break;
            yield return aim;
        }
    }
    private IEnumerable<int> ExploreRouteLanes()
    {
        Phase="route-geometry";
        var anchors=target.IsArea ? target.AllowedRegions().Select(r=>r.Center).ToArray() : [target.Position];
        var portals=new List<Vector3>();var mirrors=new List<Vector3>();var distantMirrors=new List<Vector3>();var timedWalls=new List<(Vector3 Point,Vector3 Normal)>();
        if(target.Objective is { } face)
        {
            portals.Add(face.Center);
            portals.Add(face.Center+face.Right*face.HalfWidth*.5f);portals.Add(face.Center-face.Right*face.HalfWidth*.5f);
            portals.Add(face.Center+Vector3.UnitZ*face.HalfHeight*.5f);
        }
        // The old 256u smoke survey could not propose a wall farther along a
        // route. Keep local opening hints and separate distant wall proposals.
        float surveyReach=physics.Kind.Timed() ? Math.Clamp((physics.ThrowSpeed+families.Max(a=>a.ForwardSpeed)*physics.PlayerVelocityScale)*physics.FuseSeconds,256,1024) :
            Math.Clamp(Vector3.Distance(start.Feet,target.Position),256,1024);
        // Free approaches are hints, not inferred windows. Wall planes supply
        // proposal headings, never accepted solutions.
        foreach(var center in anchors.Prepend(start.Feet))
            for(int i=0;i<8 && Budget;i++)
            {
                var origin=center+Vector3.UnitZ*64;var direction=Geometry.Direction(0,i*45);
                var hit=world.Sweep(origin,origin+direction*surveyReach,physics.HullHalfSize);yield return 0;
                // Extending the ray must not erase the old local opening hint
                // or put a distant wall ahead of an existing mirror seed.
                // Beyond 256u, a wall is only an additional timed-bank hint.
                bool localBlocked=hit.Hit && (hit.StartSolid || Vector3.Distance(origin,hit.Position)<=256.001f);
                if(hit.Hit && !hit.StartSolid && MathF.Abs(hit.Normal.Z)<.35f)
                {
                    foreach(var anchor in anchors)
                    {
                        // Start and landing must lie on the same side for a
                        // single wall reflection to be a meaningful proposal.
                        float a=Vector3.Dot(start.Feet+Vector3.UnitZ*64-hit.Position,hit.Normal),b=Vector3.Dot(anchor-hit.Position,hit.Normal);
                        if(a*b>0)
                        {
                            if(localBlocked) mirrors.Add(anchor-2*b*hit.Normal);
                            else if(!physics.Kind.Timed()) distantMirrors.Add(anchor-2*b*hit.Normal);
                            if(physics.Kind.Timed() && MathF.Abs(hit.Normal.Z)<.05f) timedWalls.Add((hit.Position,hit.Normal));
                        }
                    }
                }
                if(!localBlocked && center!=start.Feet && target.Objective is null && i%2==0)
                    portals.Add(origin+direction*96);
            }
        var actions=families.ToArray();
        var mirrorProposals=Interleave(mirrors.Distinct().Take(12).Select(a=>Toward(a,actions)));
        // Timed impacts need the time remaining after a lossy reflection;
        // ideal mirror geometry alone points at the wrong three-dimensional
        // detonation locus. Retain mirrors as a fallback in the same lane.
        var bankProposals=physics.Kind.Timed() ? Interleave([
            Interleave(timedWalls.DistinctBy(w=>(MathF.Round(w.Normal.X*32),MathF.Round(w.Normal.Y*32),MathF.Round(Vector3.Dot(w.Point,w.Normal))))
                .Take(12).SelectMany(w=>anchors.Select(a=>TimedBankProposals(a,w.Point,w.Normal,actions)))),mirrorProposals]) :
            mirrorProposals.Concat(MissingBankProposals(Interleave([
                Interleave(mirrors.Concat(distantMirrors).Distinct().Take(12).Select(a=>RestingBankProposals(a,actions))),
                Interleave(distantMirrors.Distinct().Take(8).Select(a=>Toward(a,actions)))])));
        var sources=new List<(RouteLane Lane,IEnumerator<Aim> Aims)>
        {
            (Lane("direct"),Interleave(anchors.Select(DirectProposals)).GetEnumerator()),
            (Lane("high"),Interleave(anchors.Select(HighProposals)).GetEnumerator()),
            (Lane("opening"),Interleave(portals.Distinct().Take(8).Select(a=>Toward(a,actions))).GetEnumerator()),
            (Lane("bank"),bankProposals.GetEnumerator())
        };
        if(options.IncludeActions) sources.Add((Lane("jump-bank"),Interleave(anchors.Select(ContinueJumpScan)).GetEnumerator()));
        try
        {
            // Equal early opportunities. A finite lane never monopolizes a long
            // range scan. Each lane also keeps its own candidates for review.
            for(int round=0;round<168 && Budget;round++)
            {
                foreach(var (lane,aims) in sources)
                {
                    if(!Budget) break;
                    if(!aims.MoveNext()) continue;
                    activeLane=lane;Phase="route-"+lane.Name;
                    foreach(int unit in Evaluate(aims.Current)) yield return unit;
                    activeLane=null;
                }
                if(round==(options.IncludeActions ? 23 : 11) || round==47 || round==111)
                {
                    foreach(int unit in ReviewLanes()) yield return unit;
                    if(!options.Exhaustive && SimpleAreaHit) {seedSatisfied=true;yield break;}
                    if(round!=111)
                    {
                        foreach(int unit in RefineLanesEarly()) yield return unit;
                        foreach(int unit in ReviewLanes()) yield return unit;
                        if(!options.Exhaustive && SimpleAreaHit) {seedSatisfied=true;yield break;}
                    }
                }
            }
            var proposals=lanes.Values.SelectMany(l=>LaneSeeds(l,l.Name=="jump-bank" ? 6 : 3).Select(c=>((RouteLane?)l,c))).ToArray();
            foreach(int unit in AdaptiveRefine(proposals,3,3,480)) yield return unit;
            foreach(int unit in ReviewLanes()) yield return unit;
            if(!options.Exhaustive && SimpleAreaHit) seedSatisfied=true;
        }
        finally {activeLane=null;foreach(var (_,aims) in sources) aims.Dispose();}
    }
    private IEnumerable<int> ReviewLanes()
    {
        foreach(var lane in lanes.Values)
        {
            IEnumerable<Candidate> selected=lane.Hits;
            if(lane.Name=="jump-bank")
            {
                var groups=lane.Hits.Concat(jumpBranchHits.Seeds(Objective)).Where(c=>!reviewed.Contains(AimKey(c.Aim)))
                    .GroupBy(c=>(Contacts:Math.Min(3,c.Trajectory.WallBounces),Action:c.Aim.EffectiveAction)).OrderBy(g=>g.Key).ToArray();
                // Low-contact nominal hits must not take every review slot.
                selected=groups.Length==0 ? [] : groups[jumpReviewTurn++%groups.Length];
            }
            foreach(int unit in ReviewCandidates(1,selected)) yield return unit;
        }
    }
    private IEnumerable<int> RefineLanesEarly()
    {
        // Spend a small local allowance before broad sampling can consume the
        // deadline. In particular a roof seed often needs only a 1-degree turn.
        var selected=lanes.Values.SelectMany(l=>LaneSeeds(l,l.Name=="jump-bank" ? 3 : 1).Select(c=>(Lane:l,Seed:c))).ToArray();
        (float Pitch,float Yaw)[] offsets=[(0,1.5f),(0,-1.5f),(1.5f,0),(-1.5f,0),(.5f,.5f),(-.5f,-.5f),(1.5f,1.5f),(-1.5f,-1.5f)];
        foreach(var offset in offsets)
            foreach(var (lane,seed) in selected)
            {
                if(!Budget) yield break;
                activeLane=lane;Phase="route-"+lane.Name;
                foreach(int unit in Evaluate(seed.Aim with {Pitch=Math.Clamp(seed.Aim.Pitch+offset.Pitch,-89,89),Yaw=Geometry.NormalizeYaw(seed.Aim.Yaw+offset.Yaw)})) yield return unit;
                activeLane=null;
            }
    }
}
