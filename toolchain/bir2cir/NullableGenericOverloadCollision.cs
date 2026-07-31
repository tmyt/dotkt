using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE OVERLOAD COLLISION THE ERASURE CREATES (#86 §5.3).
//
// `Nullable(Tv)` erases to `System.Object`, so two Kotlin declarations that differ ONLY in a position the erasure
// flattens become one CLR signature. On a generic owner:
//
//     class C<T> { fun f(x: T?) ; fun f(x: Any?) }        // both emit `f(object)`
//
// The frontend accepts that pair — `T?` and `Any?` are different Kotlin types — and Kotlin's own resolution picks
// `f(T?)` for `c.f(3)` at `C<Int>`. Emitted, both members occupy one slot: whichever the emitter binds wins every
// call, and the other is unreachable. That is a SILENT WRONG BINDING, not a diagnostic — measured, `c.f(3)` and
// `c.f("s")` both ran the `Any?` body.
//
// A program with no valid CIL lowering owes its author an actionable message rather than a silently different
// meaning, so this refuses and names BOTH source signatures. It is the same discipline as the other refusals: it
// cannot fire on a program the erasure does not collapse, because the key it compares IS the emitted signature.
//
// GENERIC ARITY IS PART OF THE KEY. `fun <T> f(x: T?)` and `fun f(x: Any?)` do NOT collide — a method's generic
// parameter count is part of the CLI signature (ECMA-335 I.8.6.1.6) and ilemit's own overload key already carries it.
// Only a same-name, same-generic-arity, same-erased-parameter-vector pair is one slot.
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
                CheckContainer(to, Str(to["name"]) ?? "<type>", file);
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
                throw new InvalidOperationException(
                    $"bir2cir: {file}: {ownerName}: two declarations of '{name}' erase to one CLR signature. "
                    + $"A nullable generic 'T?' is emitted as System.Object (#86), so '{Render(prior, name, ownerTps)}' "
                    + $"and '{Render(mo, name, ownerTps)}' both become '{name}({ErasedVector(mo)})' and only one can be "
                    + "called. Give one of them a different name or a different parameter count.");
            }
            seen[key] = mo;
        }
    }

    // The PHYSICAL parameter vector, as the emitted signature. Rendered through the alias normalization because one
    // CLR type reaches this point under more than one spelling — `T?` erases to `object` while `Any?` arrives as
    // `System.Object` — and a signature key that told those apart would miss exactly the collision it is looking for.
    static string ErasedVector(JsonObject mo)
        => string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Read(p["type"]) is TypeNode t ? Normalize(Render(t)) : "?") ?? Enumerable.Empty<string>());

    static string Normalize(string rendered) => rendered
        .Replace("System.Object", "object", StringComparison.Ordinal);

    // The pre-erasure parameter vector as a name-free STRUCTURAL key: the differential the refusal turns on.
    static string SourceKey(JsonObject mo)
        => string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => SourceType(p, (new List<string>(), new List<string>()))) ?? Enumerable.Empty<string>());

    // The parameter vector as WRITTEN, recovered from the pre-erasure record wherever the erasure kept one.
    static string SourceVector(JsonObject mo, List<string> ownerTps)
    {
        var tps = Scopes(ownerTps, TypeParamNames(mo));
        return string.Join(", ", (mo["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => SourceType(p, tps)) ?? Enumerable.Empty<string>());
    }

    static string SourceType(JsonObject p, (List<string> type, List<string> method) tps)
    {
        var pre = (p["nullableGeneric"] as JsonValue)?.GetValue<string>();
        var t = pre != null ? TypeJson.Read(JsonNode.Parse(pre)) : TypeJson.Read(p["type"]);
        return t is TypeNode tn ? Render(tn, tps) : "?";
    }

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

    static string Render(TypeNode t) => Render(t, (new List<string>(), new List<string>()));

    // Renders a type as the AUTHOR would recognize it, so the refusal names declarations rather than node shapes: a
    // type variable resolves to its declared name from the owner's or the method's list.
    static string Render(TypeNode t, (List<string> type, List<string> method) tps) => t switch
    {
        TypeNode.Nullable n => Render(n.Of, tps) + "?",
        TypeNode.Oblivious o => Render(o.Of, tps) + "!",
        TypeNode.Array a => "Array<" + Render(a.Elem, tps) + ">",
        TypeNode.ByRef b => "ref " + Render(b.Of, tps),
        TypeNode.Tv tv => TvName(tv, tps),
        TypeNode.Fqn { Args: { } args } f => f.Name + "<" + string.Join(", ", args.Select(a => Render(a, tps))) + ">",
        TypeNode.Fqn f => f.Name,
        TypeNode.Fn fn => "(" + string.Join(", ", fn.Params.Select(pp => Render(pp, tps))) + ") -> " + Render(fn.Ret, tps),
        _ => t.ToString(),
    };

    static string TvName(TypeNode.Tv tv, (List<string> type, List<string> method) tps)
    {
        var names = tv.Scope == "method" ? tps.method : tps.type;
        return tv.I >= 0 && tv.I < names.Count ? names[tv.I] : tv.Scope + "#" + tv.I;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
