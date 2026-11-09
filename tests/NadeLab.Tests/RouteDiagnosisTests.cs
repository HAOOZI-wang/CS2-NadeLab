using System.Numerics;
using NadeLab.Core;

internal static class RouteDiagnosisTests
{
    internal static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        RouteEvidence Proof(RouteEvidenceKind kind,bool value,string request="request",string world="world")=>new(kind,value,request,world);
        RouteDiagnosisResult Diagnose(params RouteEvidence[] evidence)=>RouteDiagnosis.Classify("request","world",evidence);
        test("Native prediction alone cannot establish a real route or manual effectiveness",()=>
        {
            var result=Diagnose(Proof(RouteEvidenceKind.NativeSimulation,true));
            assert(result.ModelMismatch==RouteFinding.Unknown && result.SearchCoverage==RouteFinding.Unknown &&
                result.Presentation==RouteFinding.Unknown && result.ManualReproducibility==RouteFinding.Unknown && result.FinalEffect==RouteFinding.Unknown,"simulation fabricated observed evidence");
        });
        test("Witnessed matched real route and native miss identify model mismatch",()=>
        {
            assert(Diagnose(Proof(RouteEvidenceKind.MatchedRealRoute,true),Proof(RouteEvidenceKind.NativeSimulation,false)).ModelMismatch==RouteFinding.Supported,"model mismatch lost");
            assert(Diagnose(Proof(RouteEvidenceKind.MatchedRealRoute,true),Proof(RouteEvidenceKind.NativeSimulation,true)).ModelMismatch==RouteFinding.NotSupported,"agreement labelled mismatch");
            assert(Diagnose(Proof(RouteEvidenceKind.MatchedRealRoute,false),Proof(RouteEvidenceKind.NativeSimulation,true)).ModelMismatch==RouteFinding.Unknown,"unsuccessful unproven throw treated as a controlled mismatch");
        });
        test("Completed search missing a feasible simulated route identifies coverage independently",()=>
        {
            var evidence=new[]{Proof(RouteEvidenceKind.NativeSimulation,true),Proof(RouteEvidenceKind.SearchCompleted,true),Proof(RouteEvidenceKind.SearchFoundRoute,false)};
            assert(Diagnose(evidence).SearchCoverage==RouteFinding.Supported,"coverage miss lost");
            assert(Diagnose(evidence.Where(p=>p.Kind!=RouteEvidenceKind.SearchCompleted).ToArray()).SearchCoverage==RouteFinding.Unknown,"interrupted search called a miss");
            assert(Diagnose(Proof(RouteEvidenceKind.NativeSimulation,false),Proof(RouteEvidenceKind.SearchCompleted,true),Proof(RouteEvidenceKind.SearchFoundRoute,false)).SearchCoverage==RouteFinding.Unknown,"unproven feasible route counted");
        });
        test("Found routes distinguish publication and witnessed presentation failures",()=>
        {
            assert(Diagnose(Proof(RouteEvidenceKind.SearchFoundRoute,true),Proof(RouteEvidenceKind.SearchPublishedRoute,false)).Presentation==RouteFinding.Supported,"publication miss lost");
            assert(Diagnose(Proof(RouteEvidenceKind.SearchFoundRoute,true),Proof(RouteEvidenceKind.SearchPublishedRoute,true)).Presentation==RouteFinding.Unknown,"server publication proved client pixels");
            assert(Diagnose(Proof(RouteEvidenceKind.SearchFoundRoute,true),Proof(RouteEvidenceKind.SearchPublishedRoute,true),Proof(RouteEvidenceKind.ClientPresentedRoute,false)).Presentation==RouteFinding.Supported,"client presentation miss lost");
            assert(Diagnose(Proof(RouteEvidenceKind.SearchFoundRoute,true),Proof(RouteEvidenceKind.SearchPublishedRoute,true),Proof(RouteEvidenceKind.ClientPresentedRoute,true)).Presentation==RouteFinding.NotSupported,"witnessed presentation lost");
        });
        test("Manual reproducibility and final effect require their separate observations",()=>
        {
            var result=Diagnose(Proof(RouteEvidenceKind.ManualReproduced,false),Proof(RouteEvidenceKind.ObservedFinalEffect,true));
            assert(result.ManualReproducibility==RouteFinding.Supported && result.FinalEffect==RouteFinding.NotSupported,"independent observations collapsed");
            result=Diagnose(Proof(RouteEvidenceKind.ManualReproduced,true),Proof(RouteEvidenceKind.ObservedFinalEffect,false));
            assert(result.ManualReproducibility==RouteFinding.NotSupported && result.FinalEffect==RouteFinding.Supported,"effect failure inferred from execution");
        });
        test("Stale and contradictory proofs stay unknown with explicit evidence gaps",()=>
        {
            var stale=Diagnose(Proof(RouteEvidenceKind.ManualReproduced,true,world:"old"),Proof(RouteEvidenceKind.ObservedFinalEffect,true,request:"other"));
            assert(stale.ManualReproducibility==RouteFinding.Unknown && stale.FinalEffect==RouteFinding.Unknown && stale.IgnoredEvidence==2,"stale evidence accepted");
            var conflict=Diagnose(Proof(RouteEvidenceKind.NativeSimulation,true),Proof(RouteEvidenceKind.NativeSimulation,false),Proof(RouteEvidenceKind.MatchedRealRoute,true));
            assert(conflict.ModelMismatch==RouteFinding.Contradictory,"contradiction became success/failure");
            conflict=Diagnose(Proof(RouteEvidenceKind.SearchFoundRoute,false),Proof(RouteEvidenceKind.SearchPublishedRoute,true));
            assert(conflict.Presentation==RouteFinding.Contradictory,"impossible publication chain accepted");
            assert(RouteDiagnosis.Classify("request","",new[]{Proof(RouteEvidenceKind.ManualReproduced,true,world:"")}).ManualReproducibility==RouteFinding.Unknown,"missing identity accepted");
        });
        test("Known route evaluation yields before tracing the remaining trajectory and cancellation stops work",()=>
        {
            var world=new RouteCountingWorld();
            var run=new KnownRouteEvaluation(world,new(),new(Vector3.Zero),new(-20,0,ThrowMode.Left),new(new(0,0,0)));
            int before=world.Traces;run.Advance(2,100);
            assert(!run.Done && world.Traces-before==2,"simulation did unbounded work");
            run.Cancel();before=world.Traces;run.Advance(8,100);
            assert(run.Cancelled && run.Result is null && world.Traces==before,"cancelled evaluation traced or published success");
        });
        test("Known route completion preserves requested target and termination acceptance",()=>
        {
            var profile=Grenades.Bootstrap(GrenadeKind.Flash,new());var start=new ThrowStart(Vector3.Zero);var aim=new Aim(-20,0,ThrowMode.Left);
            var world=new RouteCountingWorld();var target=new Target(new(0,0,5000),32,Airspace:true);
            var run=new KnownRouteEvaluation(world,profile,start,aim,target);
            for(int slice=0;!run.Done && slice<1000;slice++)run.Advance(8,100);
            assert(run.Result is {Status:SimulationStatus.Detonated} && run.TargetHit==false && run.Target==target && run.Start==start && run.Aim==aim,"diagnosis moved or enlarged input");
            var hit=new KnownRouteEvaluation(new RouteCountingWorld(),profile,start,aim,new(run.Result!.Endpoint));
            for(int slice=0;!hit.Done && slice<1000;slice++)hit.Advance(8,100);
            assert(hit.TargetHit==true,"successful endpoint rejected");
        });
    }
    private sealed class RouteCountingWorld : ICollisionWorld
    {
        internal int Traces;
        public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize) {Traces++;return SweepHit.Miss(end);}
    }
}
