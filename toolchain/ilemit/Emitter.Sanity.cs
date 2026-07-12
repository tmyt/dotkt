// AUTO-SPLIT companion to Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Text.Json;
using DotKt.Bir;

// #84 Phase 4 — the IR SANITY gate. #112 Phase 4 MOVED the invariant LOGIC to the shared bir-common IrSanity so
// BOTH bir2cir (on the CIR it produces) and ilemit (here, at the head of EmitAssembly) run the SAME checks; this
// file is now the thin ilemit ADAPTER that routes an IrSanityException into Phase 1's `ilemit: <Decl>: sanity: …`
// diagnostic. The invariant set, its calibration rationale, and the scope-lifetime notes live in
// toolchain/bir-common/IrSanity.cs.

// A sanity violation, adapted from the shared IrSanityException. Derives from CirEmitException so IlEmit.Main's
// existing catch prints `ilemit: <Decl>: sanity: <message>` with no new plumbing (the `sanity: ` prefix is baked in).
sealed class CirSanityException : CirEmitException
{
    public CirSanityException(string decl, string message) : base(decl, "sanity: " + message, null) { }
}

sealed partial class Emitter
{
    // Run the shared sanity invariants over every method/ctor/static-field-initializer in the CIR, before any emit.
    // Every check is intra-declaration and needs no Reflection.Emit state, so fail-before-any-work is the cleanest
    // contract. Re-throw the layer-agnostic IrSanityException as ilemit's CirSanityException for the Main catch.
    void CheckCir(List<JsonElement> files)
    {
        try { IrSanity.Check(files); }
        catch (IrSanityException ex) { throw new CirSanityException(ex.Decl, ex.Message); }
    }
}
