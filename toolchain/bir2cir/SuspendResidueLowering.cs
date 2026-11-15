using DotKt.Bir.Lowering;

// bir2cir — the PHYSICAL BODY of a suspend declaration the cold-core lowering deliberately does not lower, and the
// disposal of the Kotlin `suspend` modifier at the CIR boundary.
//
// SuspendColdLowering rewrites every declared `suspend fun` into its cold Continuation shape (SM class + cold entry +
// Task bridge). Two mechanisms in that pass leave the `suspend` modifier on a declaration that still reaches this
// point, and BOTH are stdlib-self-build only:
//
//   * the self-build RETAINS the original declaration beside its cold entry (ApplyNamedSource removes it only when
//     SuspendModuleContext.ContinuationBaseIsLocal is false) —
//     kotc's pre-ignition @RestrictsSuspension builder path (`sequence{}`/`iterator{}`) still calls SequenceScope's
//     yield/yieldAll by name, so the Kotlin-shaped slot must stay declared;
//   * IsSourceColdCandidate excludes an `inline` suspend fun outside an app build —
//     that is how the coroutine PRIMITIVES, whose call sites are reconstructed inline instead, keep their standalone
//     declarations.
//
// Neither category has a state-machine form: the body kotc emitted for it suspends over a continuation that was never
// materialized, so there is no CIL that means it. Its physical body is therefore a call-time throw, and bir2cir STATES
// it here as ordinary CIR — the same `throw new NotSupportedException(msg)` shape SuspendColdLowering's ColdEntryStub
// uses for a concrete-but-not-segmentable member. `bodyTerminates` records that the authored body cannot fall through,
// so no synthetic trailing return follows it.
//
// The REFERENCE build does not run this pass (Program.cs): RefBodySquash replaces EVERY body there with the
// metadata-only `throw NotImplementedException()`, which is already the physical body of a declaration that cannot be
// executed, and the ref build keeps kotlin.* type tokens verbatim — an authored `NotSupportedException(kotlin.String)`
// would have no CLR constructor to bind.
//
// An APP build has neither mechanism — every suspend declaration is lowered, unconditionally, and a non-segmentable
// shape gets a call-time-throw COLD ENTRY rather than a retained original. A survivor there is a cold-lowering MISS, so
// this pass fails loud naming the declaration, at the layer that owns the transform.
//
// Finally, `mods.suspend` itself is Kotlin frontend vocabulary with no CLR meaning. Its consumers all live in bir2cir —
// the cold lowering above, and RoundtripMetadata's [KotlinFunction(Suspend)] stamp — so DropModifier removes it once
// that stamp is written. CIR describes a physical CLR graph; ilemit has no coroutine semantics to apply to the flag,
// and IrSanity refuses one that survives.
static partial class SuspendResidueLowering
{
    internal static void DropModifier(BirFileDeclaration file)
    {
        DropMethods(file.Methods.OrEmpty());
        // Final type placement has already flattened the actual owning vector.
        foreach (var type in file.Types.OrEmpty()) DropMethods(type.Methods.OrEmpty());

        static void DropMethods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
            {
                if (!method.Mods.HasValue) continue;
                var modifiers = method.Mods.Value;
                // The grant owns the metadata obligation. Presence, not the
                // scalar's truth value, preserves false/null and an empty mods
                // object for the later typed metadata completion.
                if (method.RoundtripMetadataPending.OrDefault() && modifiers.Suspend.IsPresent)
                    method.RoundtripSuspend = modifiers.Suspend;
                modifiers.Suspend = default;
            }
        }
    }
}
