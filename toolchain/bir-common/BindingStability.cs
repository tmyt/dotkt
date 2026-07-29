// The call-evaluation plan's two VALUE predicates, for the bindings bir2cir itself authors or rewrites.
//
// kotc judges `stable` ONCE per binding it emits, from the Kotlin expression, and records the answer on the binding
// (docs/bir-cir-spec.md §2.7) — bir2cir consumes that answer and never re-derives it. What is left here is the narrow
// case bir2cir owns: a binding whose EXPRESSION bir2cir supplied, which is a cross-module default materialized from a
// `[kotlin.clr.KotlinDefault]` carrier (kotc reserved the binding with a placeholder and could not know what would
// fill it). Deliberately conservative — answering "no" costs one local, answering "yes" wrongly duplicates an
// evaluation.
//
// Kept in bir-common beside TypeNode/FieldLegality because both predicates are pure facts about the shared node
// vocabulary, with no reference-metadata or lowering context.

#nullable enable
using System.Text.Json.Nodes;

namespace DotKt.Bir;

public static class BindingStability
{
    /// <summary>
    /// May this value be READ more than once — is re-reading it free of side effects AND unable to observe a
    /// different value? A literal, `this`, and a read of an already-materialized binding are; anything else is
    /// assumed not to be (a plain local read is excluded: another value's evaluation could write it between the
    /// two reads).
    /// </summary>
    public static bool IsStable(JsonNode? n) =>
        n is JsonObject o && Str(o["k"]) is "const" or "this" or "bindRef";

    /// <summary>
    /// Is EVALUATING this value unobservable — may a binding nothing reads be dropped instead of evaluated for its
    /// side effects? True only for pure loads (a literal, `this`, a local/binding read, a static or instance field
    /// read whose receiver is itself pure, a default value). Everything else — any call, any construction — is
    /// evaluated, because Kotlin evaluates every value a call supplies whether the emitted call shape uses it or not.
    /// </summary>
    public static bool IsTriviallyPure(JsonNode? n) => n is JsonObject o && Str(o["k"]) switch
    {
        "const" or "this" or "local" or "bindRef" or "default" or "classRef" or "staticField" or "enumValue" => true,
        "field" => IsTriviallyPure(o["recv"]),
        _ => false,
    };

    static string? Str(JsonNode? n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
