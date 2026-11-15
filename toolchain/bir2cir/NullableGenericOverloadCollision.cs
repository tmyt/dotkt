using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// THE OVERLOAD COLLISION THE ERASURE CREATES (#86 §5.3).
//
// A possibly-value `X?` is `System.Object` wherever it is a reified ARGUMENT, and `Nullable(Tv)` is `System.Object`
// everywhere, so two Kotlin declarations that differ ONLY in a position the erasure flattens become one CLR
// signature. On a generic owner, and on a plain one:
//
//     class C<T> { fun f(x: T?) ; fun f(x: Any?) }             // both emit `f(object)`
//     fun f(xs: List<Int?>) ; fun f(xs: List<Boolean?>)        // both emit `f(IReadOnlyList<object>)`
//     class D(a: List<Int?>) { constructor(b: List<Long?>) }   // both emit `.ctor(IReadOnlyList<object>)`
//
// SUPERTYPE EDGES AND GENERIC CONSTRAINTS ARE NOT CHECKED, because the collision cannot arise there. Two edges can
// only collapse onto one if they are two instantiations of the SAME head — different heads erase to different
// heads — and Kotlin's own frontend rejects that outright ("type parameter 'T' … has inconsistent values",
// "a supertype appears twice"; pinned in tests/compile-fail/NullableGenericInterfaceCollision.kt). A backend check
// there would be unreachable code pretending to be a safety net.
//
// The frontend accepts each pair — the two Kotlin types are different — and Kotlin's own resolution picks between
// them. Emitted, both occupy one slot: whichever the emitter binds wins every call, and the other is unreachable.
// That is a SILENT WRONG BINDING, not a diagnostic — measured, `c.f(3)` and `c.f("s")` both ran the `Any?` body.
//
// A program with no valid CIL lowering owes its author an actionable message rather than a silently different
// meaning, so this refuses and names BOTH source declarations. It is the same discipline as the other refusals: it
// cannot fire on a program the erasure does not collapse, because the key it compares IS the emitted signature and
// the refusal is DIFFERENTIAL — distinct before, identical after.
//
// GENERIC ARITY IS PART OF THE KEY. `fun <T> f(x: T?)` and `fun f(x: Any?)` do NOT collide — a method's generic
// parameter count is part of the CLI signature (ECMA-335 I.8.6.1.6) and ilemit's own overload key already carries it.
// Only a same-name, same-generic-arity, same-erased-parameter-vector pair is one slot. A constructor has no generic
// arity of its own, so its key is the erased vector alone.
static partial class NullableGenericOverloadCollision
{
    static string Normalize(string rendered) => rendered
        .Replace("System.Object", "object", StringComparison.Ordinal);
    static (List<string> type, List<string> method) Scopes(List<string> owner, List<string> method) => (owner, method);
    static readonly (List<string> type, List<string> method) NoTps = (new List<string>(), new List<string>());

    // Renders a type as the AUTHOR would recognize it, so the refusal names declarations rather than node shapes: a
    // type variable resolves to its declared name from the owner's or the method's list.
    //
    // `source` picks which of the two vocabularies the message needs. The two SOURCE signatures are Kotlin, so a
    // reference slot that arrived stripped is printed back as `Any` rather than as the `System.Object` it lowered to;
    // the one signature they COLLIDE on is the emitted CLR one, and printing that as `Any` would name a type the
    // assembly does not contain.
    static string Render(TypeNode t, (List<string> type, List<string> method) tps, bool source) => t switch
    {
        TypeNode.Nullable n => Render(n.Of, tps, source) + "?",
        TypeNode.Oblivious o => Render(o.Of, tps, source) + "!",
        TypeNode.Array a => "Array<" + Render(a.Elem, tps, source) + ">",
        TypeNode.ByRef b => "ref " + Render(b.Of, tps, source),
        TypeNode.Tv tv => TvName(tv, tps),
        TypeNode.Fqn { Args: { } args } f => f.Name + "<" + string.Join(", ", args.Select(a => Render(a, tps, source))) + ">",
        TypeNode.Fqn { Name: "object" or "System.Object" } when source => "Any",
        TypeNode.Fqn f => f.Name,
        TypeNode.Fn fn => "(" + string.Join(", ", fn.Params.Select(pp => Render(pp, tps, source))) + ") -> "
                          + Render(fn.Ret, tps, source),
        _ => t.ToString(),
    };

    static string TvName(TypeNode.Tv tv, (List<string> type, List<string> method) tps)
    {
        var names = tv.Scope == "method" ? tps.method : tps.type;
        return tv.I >= 0 && tv.I < names.Count ? names[tv.I] : tv.Scope + "#" + tv.I;
    }
}
