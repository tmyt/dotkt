// Fixture source for the cross-target (target RID != host RID) reference-asset selection scenario in
// tests/msbuild/run.sh.  ONE source builds the four probe assemblies the gate packs into its throwaway feed:
//
//   ProbeFamily  ProbeFull   assembly                     role
//   -----------  ----------  ---------------------------  --------------------------------------------------
//   false        true        DotKt.Tests.Rid.Exact        ref/ compile surface AND the runtimes/<rid>/lib asset
//   false        false       DotKt.Tests.Rid.Exact        the RID-NEUTRAL lib/ placeholder (no *OnlyMarker)
//   true         true        DotKt.Tests.Rid.Family       ref/ compile surface AND the runtimes/<family>/lib asset
//   true         false       DotKt.Tests.Rid.Family       the RID-NEUTRAL lib/ placeholder (no *OnlyMarker)
//
// The RID-neutral placeholder deliberately OMITS the *OnlyMarker method that the compile (ref/) surface
// declares.  That asymmetry is the whole point: the Kotlin consumer calls the marker, so the emit only links
// if ilemit loaded the runtimes/<rid>/lib asset.  Picking the placeholder is an ilemit hard error, not a
// silently-different program — the target output cannot be executed on the build host, so the assertion has
// to be a build/emit outcome rather than a runtime value.
namespace DotKt.Tests.Rid;

#if FAMILY
public static class FamilyRidProbe
{
    public static string Which() => "family";
#if FULL
    public static string FamilyOnlyMarker() => "family-rid-asset";
#endif
}
#else
public static class ExactRidProbe
{
    public static string Which() => "exact";
#if FULL
    public static string TargetOnlyMarker() => "exact-rid-asset";
#endif
}
#endif
