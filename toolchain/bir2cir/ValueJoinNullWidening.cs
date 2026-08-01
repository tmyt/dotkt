using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A VALUE-POSITION JOIN THAT ONE BRANCH LEAVES NULL (#86 §3).
//
// `try { … } catch { null }` and `if (c) x else null` are the same shape: a value-position join the frontend resolved
// to a NON-nullable type while one branch's result is a literal `null`. A substituted generic or a spliced stdlib
// inline body (`takeIf`, `firstOrNull`) produces it — the `?` lives on the enclosing return, not on the join.
//
// The physical slot then cannot be that type: `null` into a `Nullable<V>` materializes as `HasValue=false`, but into
// a bare `int32` it is a reference stored over a value — the raw-Nullable/InvalidProgram miscompile class. So a VALUE
// join widens to `Nullable<V>`. A REFERENCE join does not: a reference holds null already.
//
// THE FACT IS THE FRONTEND'S AND THE DECISION IS THIS LAYER'S, and the split is not cosmetic — it is what makes the
// rule addressable at all. kotc stamps `joinNullBranch` on the declaration it MINTS for the join (the `try`'s temp,
// each `cond` of a `when` chain), which is a statement about that join and nothing else. An earlier version of this
// pass instead recognized the join by its emitted SHAPE — a `var` next to a `try` inside a `valueBlock` — and that
// shape is not unique to a join: an ordinary `var x: Int = 5` written before a `try` in an expression-position block
// matched it, and the widening retyped a USER local whose initializer was still an `int32` (an
// AccessViolationException at best, a swallow-and-null `try` silently answering `1` instead of the length at worst).
// Nothing recoverable was left in the BIR to tell the two apart: the temp's name is not stable (InlineSplice renames
// it), and the `try`'s own type is `Unit` in both. The producer knows; the consumer cannot.
//
// The value-ness question is this layer's for the same reason every other erasure decision is: it asks the
// struct-ness ORACLE, so a `value class` or a BCL struct join is covered by the rule that covers `Int`, where the
// former hardcoded primitive/unsigned list in kotc covered neither.
static class ValueJoinNullWidening
{
    const string Fact = "joinNullBranch";

    public static void Apply(JsonNode root, Func<string, bool> isValue) => Walk(root, isValue);

    static void Walk(JsonNode node, Func<string, bool> isValue)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var e in a.ToList()) Walk(e, isValue);
                return;
            case JsonObject o:
            {
                // Snapshot before mutating: consuming the fact and rewriting `type` both edit this object.
                var children = o.Select(kv => kv.Value).ToList();
                if (Bool(o[Fact]))
                {
                    o.Remove(Fact);   // a producer->consumer hint, never emitted as CIR
                    if (TypeJson.Read(o["type"]) is TypeNode.Fqn { Args: null } t && isValue(t.Name) && !IsVoidish(t.Name))
                        o["type"] = TypeJson.Write(new TypeNode.Nullable(t));
                }
                foreach (var c in children) Walk(c, isValue);
                return;
            }
        }
    }

    // A join that produces no value has nothing to widen, and `Nullable<void>` is not a type. `Nothing` is the
    // bottom: a join typed by it never completes normally, so no slot holds its result either.
    static bool IsVoidish(string name) =>
        name is "kotlin.Unit" or "void" or "System.Void" or "kotlin.Nothing";

    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
