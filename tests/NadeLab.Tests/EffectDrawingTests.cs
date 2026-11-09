using System.Numerics;
using NadeLab.Core;

public static class EffectDrawingTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("Air-target allowed region has complete lower upper and vertical bounds within drawing cap",()=>
        {
            var target=new Target(new(50,60,100),48,new Vector3(48,32,24),Airspace:true);
            var edges=TargetRangeGeometry.Edges(target,true);
            assert(edges.Count==20 && edges.Count<=26,"range boundary exceeds renderer cap");
            foreach(float z in new[]{76f,124f})
            {
                var ring=edges.Where(e=>e.A.Z==z && e.B.Z==z).ToArray();
                assert(ring.Length==8 && Vector3.Distance(ring[0].A,ring[^1].B)<.001f,"upper/lower ring is incomplete");
                assert(ring.All(e=>MathF.Abs((e.A.X-50)*(e.A.X-50)/(48*48)+(e.A.Y-60)*(e.A.Y-60)/(32*32)-1)<.001f),"visible boundary disagrees with acceptance ellipse");
            }
            assert(edges.Count(e=>e.A.Z!=e.B.Z)==4,"vertical extent hidden");
            var ground=TargetRangeGeometry.Edges(target,false);
            assert(ground.Count+4*2<=26 && ground.All(e=>e.A.Z==102 && e.B.Z==102) && Vector3.Distance(ground[0].A,ground[^1].B)<.001f,"multi-region ground outline would be truncated");
        });
        test("HE reference describes one 3D extent without invented concentric damage thresholds",()=>
        {
            var center=new Vector3(120,300,80);float radius=EffectReference.Radius(GrenadeKind.HighExplosive);
            var edges=EffectRangeGeometry.Sphere(center,radius).ToArray();
            assert(edges.Length==36 && edges.All(e=>MathF.Abs(Vector3.Distance(e.A,center)-radius)<.01f && MathF.Abs(Vector3.Distance(e.B,center)-radius)<.01f),"nested damage radii or out-of-bounds endpoint");
            assert(edges.Any(e=>e.A.Z>center.Z+radius*.9f) && edges.Any(e=>e.A.Z<center.Z-radius*.9f),"vertical explosion extent missing");
            var point=TargetRangeGeometry.Edges(new(center,48),true);
            assert(point.Count==24 && point.All(e=>MathF.Abs(Vector3.Distance(e.A,center)-48)<.01f),"precise point rendered as a cylinder instead of spherical tolerance");
        });
        test("Four moving effect scenes complete work without restarting and converge after movement", () =>
        {
            var queues = Enumerable.Range(0, 4).Select(_ => new EffectBuildQueue<int>()).ToArray();
            var progress = new int[4];var completed = new int[4];var published = new int[4];
            var markerWanted = new int[4];var markerApplied = new int[4];int request = 0;
            for (int tick = 0; tick < 320; tick++)
            {
                // 64 tick/s and a 0.333 s producer: this reproduces the four-player
                // starvation case while retaining the production six-step budget.
                if (tick < 220 && tick % 22 == 0)
                {
                    request++;
                    for (int i = 0; i < 4; i++) {queues[i].Enqueue(request);markerWanted[i] = request;}
                }
                // Marker work is independent of the 37-step ring build. With
                // three native updates per cross and four per tick, one owner's
                // complete marker is serviced first in each rotating frame.
                int first = tick % 4;markerApplied[first] = markerWanted[first];
                int budget = 6;
                for (int n = 0; n < 4 && budget > 0; n++)
                {
                    int i = (first + n) % 4;
                    while (queues[i].Active is not null && budget > 0)
                    {
                        budget--;
                        if (++progress[i] < 37) continue;
                        progress[i] = 0;var done = queues[i].Complete();completed[i]++;
                        if (done.Version == queues[i].LatestVersion) published[i] = done.Value;
                    }
                }
                if (tick < 220 && tick % 22 >= 3)
                    assert(markerApplied.All(v => v == request), "marker waited for a range build");
            }
            assert(completed.All(n => n >= 5), "moving producer starved an active build");
            assert(published.All(v => v == request), "latest stopped position never published");
            assert(queues.All(q => q.Active is null && q.Pending is null), "work remained after convergence");
        });
        test("Moving effect requests coalesce to one latest replacement", () =>
        {
            var queue = new EffectBuildQueue<int>();queue.Enqueue(1);
            for (int i = 2; i <= 100; i++) queue.Enqueue(i);
            assert(queue.Active!.Value.Value == 1, "active work was replaced mid-build");
            assert(queue.Pending!.Value.Value == 100, "replacement is not the latest request");
            var first = queue.Complete();
            assert(first.Value == 1 && first.Version != queue.LatestVersion, "stale completion appears current");
            var last = queue.Complete();
            assert(last.Value == 100 && last.Version == queue.LatestVersion && queue.Active is null, "superseded requests remained queued");
        });
        test("Partially uploaded effect generations cannot mix old and new visible segments", () =>
        {
            var queue = new EffectBuildQueue<Vector3>();
            queue.Enqueue(Vector3.Zero);var old = queue.Complete();
            var applied = Enumerable.Repeat(old.Version, 36).ToArray();
            queue.Enqueue(new Vector3(400, 0, 0));var next = queue.Complete();
            for (int start = 0; start < applied.Length; start += 4)
            {
                for (int i = start; i < Math.Min(start + 4, applied.Length); i++) applied[i] = next.Version;
                var visible = applied.Where(v => EffectDrawRules.Current(v, next.Version)).ToArray();
                assert(visible.Length == Math.Min(start + 4, applied.Length), "stale lines were exposed during a partial upload");
                assert(visible.All(v => v != old.Version), "mixed published positions");
            }
            queue.Clear();assert(!EffectDrawRules.Current(next.Version, queue.LatestVersion), "clear retained an obsolete generation");
            assert(!EffectDrawRules.Current(0, 0), "uninitialized beam considered published");
        });
        test("Effect visibility rejects near-end thin walls and malformed native traces", () =>
        {
            var start = new Vector3(0, 0, 64);var eye = new Vector3(600, 0, 64);
            var thinWall = new AnalyticWorld(-100, new Box(new(598, -100, 0), new(599, 100, 128)));
            var hit = thinWall.Sweep(start, eye, 0);
            assert(hit.Hit && hit.Fraction > .995f, "fixture does not reproduce the old percentage tolerance");
            assert(!EffectDrawRules.RayClear(hit.StartSolid, hit.Hit, hit.Fraction), "thin wall admitted as clear");
            assert(EffectDrawRules.RayClear(false, false, 1), "unobstructed ray rejected");
            assert(!EffectDrawRules.RayClear(false, false, .999999f), "truncated ray accepted");
            assert(!EffectDrawRules.RayClear(true, false, 1) && !EffectDrawRules.RayClear(false, true, 1), "solid/hit flag ignored");
            assert(!EffectDrawRules.RayClear(false, false, float.NaN), "NaN ray accepted");
        });
    }
}
