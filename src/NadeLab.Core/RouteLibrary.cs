using System.Numerics;

namespace NadeLab.Core;

public sealed record RouteTrial(string ThrowId,bool Matched,bool LandingHit,string CoverageVerdict="unknown",bool GuidedMatched=false,bool? ObjectiveViewMatched=null);
public sealed record SavedRoute(ThrowStart Start,Target Target,Aim Aim,string World,string Physics,bool SimulationPassed,IReadOnlyList<RouteTrial> Trials,
    bool NominalValidated=false);

public sealed class RouteLibrary
{
    private readonly List<SavedRoute> entries=[];
    public IReadOnlyList<SavedRoute> Snapshot()=>entries.Select(e=>e with {Target=e.Target.Snapshot(),Trials=e.Trials.ToArray()}).ToArray();
    public int Count=>entries.Count;
    public void Import(IEnumerable<SavedRoute> routes)
    {
        foreach(var r in routes.TakeLast(192))
        {
            if(r is null) continue;
            try
            {
                r.Target.Validate();new SearchOptions {SeedAims=[r.Aim]}.Validate();
                if(!ThrowExecution.Valid(r.Start) ||
                    string.IsNullOrWhiteSpace(r.World) || string.IsNullOrWhiteSpace(r.Physics) || r.Trials is null) continue;
                Store(r with {Trials=r.Trials.Where(t=>t is not null && !string.IsNullOrWhiteSpace(t.ThrowId) && t.CoverageVerdict is "unknown" or "sealed" or "leaking")
                    .GroupBy(t=>t.ThrowId).Select(g=>g.Last()).TakeLast(32).ToArray()});
            }
            catch(ArgumentException) { }
        }
    }
    private static bool Same(SavedRoute r,ThrowStart start,Target target,Aim aim,string world,string physics)=>
        r.Start==start && r.Aim==aim && r.World==world && r.Physics==physics && r.Target.SameDefinition(target);
    private void Store(SavedRoute route)
    {
        entries.RemoveAll(r=>Same(r,route.Start,route.Target,route.Aim,route.World,route.Physics));entries.Add(route with {Target=route.Target.Snapshot()});
        if(entries.Count>192) entries.RemoveRange(0,entries.Count-192);
    }
    public SavedRoute? Find(ThrowStart start,Target target,Aim aim,string world,string physics)=>entries.LastOrDefault(r=>Same(r,start,target,aim,world,physics));
    public void Remember(ThrowStart start,Target target,Aim aim,string world,string physics,bool simulationPassed,RouteTrial? trial=null,bool nominalValidated=false)
    {
        var old=Find(start,target,aim,world,physics);
        var trials=old?.Trials.ToList() ?? [];
        if(trial is not null) {trials.RemoveAll(t=>t.ThrowId==trial.ThrowId);trials.Add(trial);}
        Store(new(start,target,aim,world,physics,simulationPassed || old?.SimulationPassed==true,trials.TakeLast(32).ToArray(),
            nominalValidated || old?.NominalValidated==true));
    }
    private static bool HasMatchedLanding(SavedRoute route)=>route.Trials.Any(t=>(t.Matched || t.GuidedMatched) && t.LandingHit);
    private static bool NearbyTarget(Target saved,Target target)
    {
        if(saved.IsArea!=target.IsArea || saved.Airspace!=target.Airspace) return false;
        float distance=Vector3.Distance(saved.Position,target.Position);
        if(distance<=8) return true;
        // Moving the centre of a broad area a little should not discard a
        // useful direction hint. Require overlap and a bounded neighbourhood.
        // This never transfers a result, score, target or launch position.
        return target.IsArea && distance<=96 && (target.Contains(saved.Position) || saved.Contains(target.Position));
    }
    // A nominally validated low-tolerance route is only a proposal. Return angles,
    // never saved endpoints/scores, so the current solver must repeat all checks.
    public Aim[] Hints(ThrowStart start,Target target,Aim? view,string world,string physics,bool includeNominalValidated=true)=>entries.Where(r=>r.World==world && r.Physics==physics &&
        r.Start.Origin==start.Origin && (start.Origin==ThrowOrigin.Release || r.Start.RunUpDistance==start.RunUpDistance) &&
        NearbyTarget(r.Target,target) && Vector3.Distance(r.Start.Feet,start.Feet)<=64 && MathF.Abs(r.Start.EyeHeight-start.EyeHeight)<=4 &&
        (r.SimulationPassed || HasMatchedLanding(r) || includeNominalValidated && r.NominalValidated))
        .OrderBy(r=>HasMatchedLanding(r) ? 0 : r.SimulationPassed ? 1 : 2)
        .ThenBy(r=>Vector3.DistanceSquared(r.Start.Feet,start.Feet)+Vector3.DistanceSquared(r.Target.Position,target.Position)).ThenBy(r=>SceneAdvisor.ActionCost(r.Aim))
        .ThenBy(r=>view is { } v ? SceneAdvisor.ViewTurn(r.Aim,v) : 0).Select(r=>r.Aim).Distinct().Take(12).ToArray();
}

