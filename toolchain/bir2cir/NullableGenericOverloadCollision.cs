using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
static class NullableGenericOverloadCollision
{
    // Runs AFTER the erasure, on each declaration container (a type or the file class), because the collision is a
    // property of the ERASED vector: comparing declared types would refuse pairs that stay distinct.
    public static void Check(JsonNode root, string file)
    {
        if (root is not JsonObject o) return;
        CheckContainer(o, Str(o["fileClass"]) ?? "<file>", file);
        CheckTypes(o["types"], file);
    }

    static void CheckTypes(JsonNode types, string file)
    {
        if (types is not JsonArray a) return;
        foreach (var t in a)
            if (t is JsonObject to)
            {
                var name = Str(to["name"]) ?? "<type>";
                CheckContainer(to, name, file);
                CheckCtors(to, name, file);
                CheckTypes(to["types"], file);
            }
    }

    static void CheckContainer(JsonObject owner, string ownerName, string file)
    {
        if (owner["methods"] is not JsonArray methods) return;
        var ownerTps = TypeParamNames(owner);
        var seen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            if (m is not JsonObject mo || Str(mo["name"]) is not string name) continue;
            var key = name + "|" + ((mo["typeParams"] as JsonArray)?.Count ?? 0) + "|" + ErasedVector(mo);
            if (seen.TryGetValue(key, out var prior))
            {
                // THE ERASURE MUST BE WHAT COLLAPSED THEM. Two declarations that were already one CLR signature
                // before this pass ran are not its business — Kotlin admits such pairs (the stdlib's two `contains`
                // overloads differ only in their type-parameter CONSTRAINTS, which the CLR signature never carried),
                // and refusing them would reject programs that emit exactly as they did yesterday. The condition is
                // therefore differential: distinct BEFORE, identical AFTER.
                // Compared STRUCTURALLY — a type variable by scope and index, never by its declared name. Two
                // declarations can name their type parameters differently and still be the same vector, and the
                // stdlib's `contains` pair does exactly that; keying on names would read them as distinct and refuse
                // a pair that has always emitted as one signature.
                if (SourceKey(prior) == SourceKey(mo)) continue;
                // #395 gives distinct frontend declarations an explicit identity. DeclarationIdentityBinding runs
                // module-wide after every file has completed type lowering and allocates two different MethodDef
                // names from these keys, so this pair no longer shares a physical slot. Keep the refusal only for
                // malformed current BIR that omitted the authoritative identity; no structural fallback is invented.
                var priorId = Str(prior[DeclarationIdentityBinding.Key]);
                var currentId = Str(mo[DeclarationIdentityBinding.Key]);
                if (!string.IsNullOrEmpty(priorId) && !string.IsNullOrEmpty(currentId)
                    && !StringComparer.Ordinal.Equals(priorId, currentId))
                {
                    seen[key] = mo;
                    continue;
                }
                throw new InvalidOperationException(
                    $"bir2cir: {file}: {ownerName}: two declarations of '{name}' erase to one CLR signature. "
                    + $"A nullable generic 'T?' is emitted as System.Object, so '{Render(prior, name, ownerTps)}' "
                    + $"and '{Render(mo, name, ownerTps)}' both become '{name}({ErasedVector(mo)})' and only one can be "
                    + "called. Give one of them a different name or a different parameter count.");
            }
            seen[key] = mo;
        }
    }

    // CONSTRUCTORS collide on the erased vector alone. `Cell(xs: List<Int?>)` and `Cell(ys: List<Long?>)` are two
    // Kotlin constructors and one `.ctor(IReadOnlyList<object>)`; the emitter binds one of them for every `Cell(…)`.
    static void CheckCtors(JsonObject to, string ownerName, string file)
    {
        if (to["ctors"] is not JsonArray ctors) return;
        var ownerTps = TypeParamNames(to);
        var seen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var c in ctors)
        {
            if (c is not JsonObject co) continue;
            var key = ErasedVector(co);
            if (seen.TryGetValue(key, out var prior))
            {
                if (SourceKey(prior) == SourceKey(co)) continue;
                throw new InvalidOperationException(
                    $"bir2cir: {file}: {ownerName}: two constructors erase to one CLR signature. "
                    + $"A nullable generic argument is emitted as System.Object, so "
                    + $"'{ownerName}({SourceVector(prior, ownerTps)})' and '{ownerName}({SourceVector(co, ownerTps)})' "
                    + $"both become '.ctor({key})' and only one can be called. "
                    + "Give one of them a different parameter count, or route it through a named factory function.");
            }
            seen[key] = co;
        }
    }

    // The PHYSICAL parameter vector, as the emitted signature. Rendered through the alias normalization because one
    // CLR type reaches this point under more than one spelling — `T?` erases to `object` while `Any?` arrives as
    // `System.Object` — and a signature key that told those apart would miss exactly the collision it is looking for.
    static string ErasedVector(JsonObject mo)
        => string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Read(p["type"]) is TypeNode t ? Normalize(Render(t, NoTps, source: false)) : "?")
            ?? Enumerable.Empty<string>());

    static string Normalize(string rendered) => rendered
        .Replace("System.Object", "object", StringComparison.Ordinal);

    // The pre-erasure parameter vector as a name-free STRUCTURAL key: the differential the refusal turns on.
    static string SourceKey(JsonObject mo)
        => string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => SourceType(p, NoTps, nrt: false)) ?? Enumerable.Empty<string>());

    // The parameter vector as WRITTEN, recovered from the pre-erasure record wherever the erasure kept one.
    static string SourceVector(JsonObject mo, List<string> ownerTps)
    {
        var tps = Scopes(ownerTps, TypeParamNames(mo));
        return string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => SourceType(p, tps, nrt: true)) ?? Enumerable.Empty<string>());
    }

    // One parameter as WRITTEN. Two channels reconstruct it, because the erasure that caused the collision is not the
    // only thing that flattened these types on the way here: a `T?` kept its pre-erasure node on the
    // `[KotlinNullableGeneric]` carrier, while an `Any?` was stripped to a bare reference whose `?` rides the NRT byte
    // alone. Reading only the first would print the second as `System.Object` and name a declaration the author never
    // wrote — in the one message whose whole job is to identify which of two declarations to change.
    //
    // `nrt` is what keeps that reconstruction OUT of the differential. A reference `?` was never a CLR distinction:
    // `fun <T> contentDeepEquals(a: Array<T>, b: Array<T>)` and its `Array<T>?` sibling have emitted as one signature
    // since long before this erasure existed, and the refusal only concerns pairs THIS erasure collapsed. Restoring
    // the byte for the KEY would read them as distinct-before and refuse a pair the stdlib itself declares.
    static string SourceType(JsonObject p, (List<string> type, List<string> method) tps, bool nrt)
    {
        var pre = (p["nullableGeneric"] as JsonValue)?.GetValue<string>();
        if (pre != null) return TypeJson.Read(JsonNode.Parse(pre)) is TypeNode c ? Render(c, tps, source: true) : "?";
        if (TypeJson.Read(p["type"]) is not TypeNode t) return "?";
        if (nrt && t is not TypeNode.Nullable && NrtNullable(p["nullableFlags"])) t = new TypeNode.Nullable(t);
        return Render(t, tps, source: true);
    }

    // The declaration's own NRT byte at the OUTER position: 2 is `?`, 1 is not-null, 0 oblivious.
    static bool NrtNullable(JsonNode flags) =>
        flags is JsonArray a && a.Count > 0 && a[0] is JsonValue v && v.TryGetValue<int>(out var b) && b == 2;

    static (List<string> type, List<string> method) Scopes(List<string> owner, List<string> method) => (owner, method);

    // A `typeParams` entry is either a bare name or a `{name, bound}` object, depending on how far down the pipeline
    // the declaration is — both spellings reach this point, so both are read.
    static List<string> TypeParamNames(JsonObject decl)
        => (decl["typeParams"] as JsonArray)?.Select(t => Str(t) ?? Str((t as JsonObject)?["name"]) ?? "?").ToList()
           ?? new List<string>();

    // The SOURCE signature, recovered from the pre-erasure record where one was kept — the whole point of the
    // message is to name the two declarations as the author wrote them, not as they collided.
    static string Render(JsonObject mo, string name, List<string> ownerTps)
    {
        var own = TypeParamNames(mo);
        return name + (own.Count > 0 ? "<" + string.Join(", ", own) + ">" : "")
               + "(" + SourceVector(mo, ownerTps) + ")";
    }

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

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
