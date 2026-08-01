using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE DELEGATE-TARGET HALF OF THE CARRIER-ARGUMENT ERASURE (#86).
//
// A delegate's parameters and return are ARGUMENTS of a reified construction (`Func`2<…>`), so
// NullableGenericErasure gives them the same `object` a type argument gets: `(Int?) -> String` is
// `Func<object, string>` and `(Int) -> Int?` is `Func<int32, object>`. The value BOUND into that delegate is a
// lifted method — a `newDelegate`'s named static, or a `newClosure`'s class `invoke` — whose own slots the erasure
// did NOT touch, because they are ordinary declaration slots and a direct `Int?` slot is a `Nullable<int32>`.
//
// The two must agree or there is no delegate at all. ECMA-335 II.14.6 makes a `ldftn` target compatible with a
// delegate only when each of its parameters is assignable FROM the delegate's and its return assignable TO the
// delegate's: `object` and `Nullable<int32>` are assignable in neither direction, so a `Func<object, string>` built
// over an `__lambda0(Nullable<int32>)` is rejected outright (ilverify DelegateCtor "Unrecognized arguments"; at run
// time an InvalidProgramException before the first instruction executes).
//
// A target slot FOLLOWS the delegate slot it fills wherever the funcType component is the bare `object` the erasure
// produced.
//
// A `::fn` reference reaches this pass too, and moves with the same rule — it names a declaration the author wrote,
// so this is the one place a declared signature is decided by a USE. That is inherited behaviour, not a new choice:
// the pass this one replaces did exactly the same on the RETURN, and the alternative is worse, because a
// `Func<…, object>` over a target returning `Nullable<int32>` reads the struct's bits as a reference at run time.
// Closing it properly means synthesizing a forwarder at the reference instead of moving the referenced declaration,
// which is the same missing piece the delegate PARAMETER is scoped out for (see NullableGenericErasure's header);
// the two land together. Which target slots that reaches differs by position, because delegate compatibility is not
// symmetric: a PARAMETER is contravariant and only `object` is assignable from `object`, so every non-`object`
// parameter follows; a RETURN is covariant and a reference already reaches `object`, so only a value / `Nullable<V>`
// / type-variable return follows. Rewriting a reference RETURN is what broke the concrete-delegate ctor in #189
// (`object` is not assignable TO a `Func<string>` slot). See `Align`.
//
// Runs BETWEEN the declaration axis and the use axis, which is what makes the body side free: once the lifted
// method's parameter is `object`, NullableTvErasureCallRealign types every read of it as `object` and narrows at the
// consumer that needs a value, and once its return is `object` the `return` statement's value is boxed by the same
// write axis. Both are ordinary erasure-boundary uses; this pass states the declaration and nothing else.
static class DelegateTargetSlotAlignment
{
    static Func<string, bool> _isValue = _ => false;

    // Reports whether any target slot MOVED. The use axis has to see the moved declaration — a now-`object`
    // parameter is read as `object` and narrowed at its first typed consumer, a now-`object` return boxes its
    // `return` value — and it has already run by the time this pass can act, so the caller re-runs it exactly when
    // there is something new to type. Nothing moved is the common case and costs nothing.
    public static bool Apply(JsonNode root, Func<string, bool> isValue)
    {
        _isValue = isValue ?? (_ => false);
        if (root is not JsonObject o) return false;
        // Target identity -> which of its slots the delegate states as `object`. A `newDelegate` names a lifted
        // static by `method`; a `newClosure` names its synthetic class by `closureType` and its body is always that
        // class's `invoke`.
        var statics = new Dictionary<string, Demand>(StringComparer.Ordinal);
        var closures = new Dictionary<string, Demand>(StringComparer.Ordinal);
        Collect(o, statics, closures);
        if (statics.Count == 0 && closures.Count == 0) return false;
        _moved = false;
        if (statics.Count > 0) AlignMethods(o["methods"], statics);
        AlignClosureTypes(o, closures);
        return _moved;
    }

    static bool _moved;

    // The positions one delegate construction requires of its target: the parameter indices stated as `object`
    // (indexed over the funcType's DELEGATE parameters, receiver first, which is the order the lifted method declares
    // them in) and whether the return is stated as `object`.
    sealed class Demand
    {
        public readonly HashSet<int> Params = new();
        public bool Ret;
    }

    static void Collect(JsonNode node, Dictionary<string, Demand> statics, Dictionary<string, Demand> closures)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var k = Str(obj["k"]);
                // A SUSPEND fn is not a delegate at all — its value is a Continuation state machine erased to
                // `object` — so there is no funcType component for a target slot to follow.
                if (k is "newDelegate" or "newClosure"
                    && TypeJson.Read(obj["funcType"]) is TypeNode.Fn { Suspend: false } fn)
                {
                    var key = k == "newDelegate"
                        ? Str(obj["method"])
                        : (TypeJson.Read(obj["closureType"]) as TypeNode.Fqn)?.Name;
                    if (key != null) Demanded(k == "newDelegate" ? statics : closures, key, fn);
                }
                foreach (var kv in obj) if (kv.Value != null) Collect(kv.Value, statics, closures);
                break;
            }
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Collect(it, statics, closures);
                break;
        }
    }

    // Accumulate across every construction that binds the same target: one lifted method may be bound into two
    // delegate slots, and a slot stated as `object` anywhere is `object` for the declaration.
    static void Demanded(Dictionary<string, Demand> into, string key, TypeNode.Fn fn)
    {
        if (!into.TryGetValue(key, out var d)) into[key] = d = new Demand();
        var ps = fn.DelegateParams;   // the receiver of a `T.() -> R` is the delegate's first parameter (#145)
        for (var i = 0; i < ps.Length; i++) if (IsBareObject(ps[i])) d.Params.Add(i);
        if (IsBareObject(fn.Ret)) d.Ret = true;
    }

    static void AlignClosureTypes(JsonObject owner, Dictionary<string, Demand> closures)
    {
        if (owner["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject to)
            {
                if (closures.Count > 0 && Str(to["name"]) is string tn && closures.TryGetValue(tn, out var d)
                    && to["methods"] is JsonArray tms)
                    foreach (var tm in tms)
                        if (tm is JsonObject tmo && Str(tmo["name"]) == "invoke") Align(tmo, d);
                AlignClosureTypes(to, closures);
            }
    }

    static void AlignMethods(JsonNode methods, Dictionary<string, Demand> byName)
    {
        if (methods is not JsonArray a) return;
        foreach (var m in a)
            if (m is JsonObject mo && Str(mo["name"]) is string n && byName.TryGetValue(n, out var d))
                Align(mo, d);
    }

    // THE TWO POSITIONS TAKE OPPOSITE RULES, because delegate compatibility is not symmetric (ECMA-335 II.14.6):
    //
    //   * A PARAMETER is CONTRAVARIANT — the target's parameter must be assignable FROM the delegate's. The only type
    //     assignable from `object` is `object`, so EVERY non-`object` target parameter follows, reference included:
    //     an `invokeNullable<String>` whose physical parameter is `Func<object, string>` cannot be filled by a lifted
    //     `__lambda0(string)`, and the erasure makes that slot `object` T-independently.
    //   * A RETURN is COVARIANT — the target's return must be assignable TO the delegate's. A reference return
    //     already is, so it stays exactly as declared (#189: rewriting a `(…) -> String?` lambda's return to `object`
    //     is what broke the concrete-delegate ctor). Only a value / `Nullable<V>` / type-variable return, which needs
    //     a `box` to reach `object` at all, follows.
    //
    // The body side of both is ordinary erasure-boundary flow: the use axis narrows a now-`object` parameter at its
    // first typed consumer and boxes a now-`object` return at the `return`.
    static void Align(JsonObject mo, Demand d)
    {
        if (mo["params"] is JsonArray ps)
            for (var i = 0; i < ps.Count; i++)
                if (d.Params.Contains(i) && ps[i] is JsonObject po
                    && TypeJson.Read(po["type"]) is TypeNode pt && !IsBareObject(pt))
                {
                    po["type"] = TypeJson.Write(ObjFqn);
                    _moved = true;
                }
        if (d.Ret && TypeJson.Read(mo["ret"]) is TypeNode ret && NeedsObjectSeam(ret))
        {
            mo["ret"] = TypeJson.Write(ObjFqn);
            _moved = true;
        }
    }

    static readonly TypeNode ObjFqn = new TypeNode.Fqn("object");

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object", Args: null };

    // Whether this target slot is one the `object` seam does NOT already cover. A value type, a structural
    // `Nullable<V>` and a type variable each need the slot itself rewritten (there is no assignability between them
    // and `object` without a `box`/`unbox.any`); a reference is assignable in both directions a delegate needs and
    // stays exactly as declared — that is the #189 rule, and rewriting it would break the concrete-delegate ctor.
    // A CONSTRUCTED struct counts, since the oracle strips generic arity to say so.
    static bool NeedsObjectSeam(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of),
        TypeNode.Fqn f => _isValue(f.Name),
        _ => false,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
