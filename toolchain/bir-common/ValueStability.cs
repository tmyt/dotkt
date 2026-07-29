// THE VALUE QUESTIONS a lowering asks about a BIR expression — and, for two of them, the answer.
//
// "Purity" and "stability" are not one property in this backend. FIVE distinct questions are asked about an
// expression, each by a different lowering, for a different purpose. They are deliberately kept apart, one
// question = one implementation = one question-shaped name, because a kind may legitimately answer YES to one
// and NO to another. Where each question lives:
//
//   Q1  RE-READABLE   — may this value be READ more than once, with other evaluation in between?
//                       kotc `isStableValue` over Kotlin IR, recorded on every binding kotc emits (`stable`,
//                       docs/bir-cir-spec.md §2.7); `IsReReadable` below for the bindings bir2cir itself fills.
//   Q1ᴬ  stable ADDRESS — is the LOCATION, rather than the value, stable? kotc `isStableLocation`; IR-only.
//   Q2  DROPPABLE     — is EVALUATING it unobservable, so a binding nothing reads may be skipped?
//                       `IsDroppable` below (CallEvalLowering's zero-reader bindings).
//   Q3  RESUME-STABLE — may it be read AFTER a suspension resumes, or must it be spilled before?
//                       SuspendColdLowering (`ImpureKinds`/`IsPureExpr`).
//   Q4  STACK-NEUTRAL — may it stay in its operand slot when a LATER sibling hoists out of the expression?
//                       TryValueOperandHoist (`StackNeutralKinds`/`IsStackNeutral`).
//   Q5  LVALUE FORMER — does it DESIGNATE storage without evaluating anything itself?
//                       CallEvalLowering (`IsLvalueFormer`).
//
// A SIXTH question lives in CallEvalLowering alone and is deliberately NOT here: `StaysInLocation` asks whether a
// node is a link of an addressable LOCATION's own path (so pinning it into a local would copy the storage away)
// rather than a value computed for it. That is about storage identity, not about side effects, and it is asked only
// of the operands of a by-reference argument — see its own doc comment there.
//
// WORKED EXAMPLE, because these look like disagreements and are not. `arrayGet` and `arrayLen`:
//   * Q2: NEITHER is droppable — an element load and a length load both dereference, so both can throw
//     (NullReferenceException, IndexOutOfRangeException). Throwing is observable.
//   * Q3: `arrayGet` is NOT resume-stable (the array is a shared reference the suspend callee can reach and
//     mutate) while `arrayLen` IS (a .NET array's length is fixed for the array's lifetime).
//   * Q4: NEITHER is stack-neutral — both can throw, so their order relative to a hoisted try's side effects
//     is observable and they are spilled rather than left in place.
//   * Q5: `arrayGet` IS an lvalue former (it names an element slot, and `byref(a[i])` takes that slot's
//     address); `arrayLen` is not — it produces a value, and no storage holds it.
// Four different answers for two kinds, all correct, because four different questions were asked.
//
// Q1 AND Q2 LIVE HERE. Both are pure facts about the shared node vocabulary, with no reference-metadata or
// lowering context, so they sit in bir-common beside TypeNode/FieldLegality.
//
// Q1's home is kotc: it judges `stable` ONCE per binding it emits, from the Kotlin expression, and records the
// answer on the binding — bir2cir consumes that answer and never re-derives it. `IsReReadable` covers only the
// narrow case bir2cir owns: a binding whose EXPRESSION bir2cir supplied, which is a cross-module default
// materialized from a `[kotlin.clr.KotlinDefault]` carrier (kotc reserved the binding with a placeholder and
// could not know what would fill it). Deliberately conservative — answering "no" costs one local, answering
// "yes" wrongly duplicates an evaluation.

#nullable enable
using System.Text.Json.Nodes;

namespace DotKt.Bir;

public static class ValueStability
{
    /// <summary>
    /// Q1 — may this value be READ more than once, is re-reading it free of side effects AND unable to observe a
    /// different value? A literal, `this`, and a read of an already-materialized binding are; anything else is
    /// assumed not to be (a plain local read is excluded: another value's evaluation could write it between the
    /// two reads).
    /// </summary>
    /// <remarks>
    /// COARSER THAN kotc's answer, on purpose. kotc asks this over Kotlin IR, where it can see that a local is a
    /// `val` and not a captured ref-cell, and so accepts an immutable local/parameter read. BIR has one `local`
    /// kind for both `val` and `var`, so bir2cir cannot make that distinction and refuses the whole kind. The two
    /// are not in competition: kotc's IR-precise answer is recorded on the binding and consumed as-is, and this
    /// one is only reached where kotc had nothing to judge yet.
    /// </remarks>
    public static bool IsReReadable(JsonNode? n) =>
        n is JsonObject o && Str(o["k"]) is "const" or "this" or "bindRef";

    /// <summary>
    /// Q2 — is EVALUATING this value unobservable, so a binding NOTHING reads may be dropped instead of evaluated
    /// for its side effects? Kotlin evaluates every value a call supplies whether the emitted call shape has a slot
    /// for it or not, so the answer is YES only where the evaluation cannot be detected at all: a literal, `this`, a
    /// local or binding read, a filled default, a class token.
    /// </summary>
    /// <remarks>
    /// NARROW ON PURPOSE, and narrower than "looks like a pure load" (docs/dotkt-semantics.md §7a). A FIELD read
    /// dereferences, so it throws on a null receiver — and a null receiver is reachable, a platform type carrying no
    /// null assertion (§9a). A STATIC FIELD read, and an ENUM VALUE read with it, runs the declaring type's
    /// initializer — which on this backend is where a Kotlin top-level property initializer and an `object`'s body
    /// live, so it can print, throw, or mutate global state. Those are evaluations with consequences, and skipping
    /// one because the emitted shape has no slot for its value would delete a Kotlin side effect. The cost of being
    /// wrong the other way is one local nobody reads.
    /// </remarks>
    public static bool IsDroppable(JsonNode? n) => n is JsonObject o && Str(o["k"]) switch
    {
        "const" or "this" or "local" or "bindRef" or "default" or "classRef" => true,
        _ => false,
    };

    static string? Str(JsonNode? n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
