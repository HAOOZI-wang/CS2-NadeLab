namespace NadeLab.Core;

public enum RouteEvidenceKind
{
    MatchedRealRoute, NativeSimulation, SearchCompleted, SearchFoundRoute,
    SearchPublishedRoute, ClientPresentedRoute, ManualReproduced, ObservedFinalEffect
}
// Findings describe evidence for a failure, never the result of a whole acceptance run.
public enum RouteFinding { Unknown, Supported, NotSupported, Contradictory }
public sealed record RouteEvidence(RouteEvidenceKind Kind,bool Value,string RequestId,string WorldKey);
public sealed record RouteDiagnosisResult(RouteFinding ModelMismatch,RouteFinding SearchCoverage,
    RouteFinding Presentation,RouteFinding ManualReproducibility,RouteFinding FinalEffect,int IgnoredEvidence);
public static class RouteDiagnosis
{
    public static RouteDiagnosisResult Classify(string requestId,string worldKey,IReadOnlyList<RouteEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        // Callers must bind RequestId to the full immutable request (start, target,
        // angle, physics and builds), rather than reuse a case label across runs.
        var valid=evidence.Where(p=>!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(worldKey) &&
            p.RequestId==requestId && p.WorldKey==worldKey && Enum.IsDefined(p.Kind)).ToArray();
        int State(RouteEvidenceKind kind)
        {
            var values=valid.Where(p=>p.Kind==kind).Select(p=>p.Value).Distinct().ToArray();
            return values.Length==0 ? -1 : values.Length>1 ? 2 : values[0] ? 1 : 0;
        }
        int real=State(RouteEvidenceKind.MatchedRealRoute),sim=State(RouteEvidenceKind.NativeSimulation),
            completed=State(RouteEvidenceKind.SearchCompleted),found=State(RouteEvidenceKind.SearchFoundRoute),
            published=State(RouteEvidenceKind.SearchPublishedRoute),client=State(RouteEvidenceKind.ClientPresentedRoute);
        var model=real==2 || sim==2 ? RouteFinding.Contradictory : real!=1 || sim<0 ? RouteFinding.Unknown :
            sim==0 ? RouteFinding.Supported : RouteFinding.NotSupported;
        var coverage=sim==2 || completed==2 || found==2 ? RouteFinding.Contradictory :
            found==1 ? RouteFinding.NotSupported : sim==1 && completed==1 && found==0 ? RouteFinding.Supported : RouteFinding.Unknown;
        var presentation=found==2 || published==2 || client==2 || (found==0 && (published==1 || client==1)) || (published==0 && client==1)
            ? RouteFinding.Contradictory : found!=1 ? RouteFinding.Unknown :
            published==0 || (published==1 && client==0) ? RouteFinding.Supported :
            published==1 && client==1 ? RouteFinding.NotSupported : RouteFinding.Unknown;
        RouteFinding ObservedFailure(RouteEvidenceKind kind)=>State(kind) switch
        {0=>RouteFinding.Supported,1=>RouteFinding.NotSupported,2=>RouteFinding.Contradictory,_=>RouteFinding.Unknown};
        return new(model,coverage,presentation,ObservedFailure(RouteEvidenceKind.ManualReproduced),
            ObservedFailure(RouteEvidenceKind.ObservedFinalEffect),evidence.Count-valid.Length);
    }
}
