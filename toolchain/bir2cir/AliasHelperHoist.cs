using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// RULE-3 HOIST (ALL CLR-bound alias classes). kotc no longer synthesizes the `dotkt$ClrH_<owner>` helper for ANY
// @ClrTypeAlias class whose concrete intrinsic-less members carry real bodies — the alias-only files (kotlin.String's
// subSequence, plus kotlin.Boolean/kotlin.Char operator stubs) AND the MIXED files (StringBuilder/UInt/collections/
// Regex). kotc emits each such alias class as a PLAIN BIR type; this pass reads the ref.dll @ClrTypeAlias index, hoists
// those rule-3 members into the static helper (the dispatch `this` becomes a leading `__self` param), and DROPS the
// original alias type def — it must NEVER reach ilemit as a real CLR type (its equals(Any?)/toString()/length members
// would clash with System.String/System.Object). The rule-3 CALL routing in MemberCallSubstitution already targets
// `dotkt$ClrH_<owner>.<member>(recv, ..)` by name, so emitting the helper here closes the loop. This is the SOLE home
// of rule-3 helper synthesis. Runs only in substitute/app builds (never ref).
static class AliasHelperHoist
{
    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return root;
        var rebuilt = new JsonArray();
        var changed = false;
        foreach (var t in types)
        {
            if (t is JsonObject td && IsAliasTypeDef(td, refs, out var fqn))
            {
                changed = true;                                  // alias type def -> dropped (and possibly hoisted)
                var helper = BuildHelper(td, fqn, refs);
                if (helper != null) rebuilt.Add(helper);         // null = no rule-3 members (e.g. kotlin.Any) -> just dropped
            }
            else rebuilt.Add(t?.DeepClone());
        }
        if (!changed) return root;
        var outObj = new JsonObject();
        foreach (var kv in obj) outObj[kv.Key] = kv.Key == "types" ? rebuilt : kv.Value?.DeepClone();
        return outObj;
    }

    // A top-level type def whose FQN is a @ClrTypeAlias owner in the ref.dll (the same index the type-token lowering and
    // member-call substitution use). Only such a def is dropped/hoisted, so a non-alias plain type can never be lost.
    static bool IsAliasTypeDef(JsonObject td, ReferenceMetadataIndex refs, out string fqn)
    {
        fqn = null;
        if ((td["name"] as JsonValue)?.GetValue<string>() is not string name) return false;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(name);
        if (!refs.Aliases.ContainsKey(bare)) return false;
        fqn = bare;
        return true;
    }

    static JsonObject BuildHelper(JsonObject td, string fqn, ReferenceMetadataIndex refs)
    {
        // ONLY a CLASS alias gets a rule-3 helper. kotc now emits @ClrTypeAlias INTERFACES (Comparable/Iterable/
        // Collection/List/…) too (it no longer strips them); those are dropped here with NO helper — an interface's
        // members are abstract in source, and a ref.dll default-interface-method would otherwise false-positive as a
        // rule-3 member and produce a bogus interface "helper". A non-class kind => return null => the alias is just
        // dropped (its use-site references are lowered to the BCL type by BirTypeLowering).
        if ((td["kind"] as JsonValue)?.GetValue<string>() != "class") return null;
        var classTps = td["typeParams"] as JsonArray;
        var aliasToken = (td["name"] as JsonValue)!.GetValue<string>();   // kotlin FQN; lowered to its BCL form downstream
        // An @JvmInline value-class alias (UInt/UByte/ULong/UShort -> System.UInt32/Byte/...) erases to its backing
        // primitive; its Object-method overrides (Equals/GetHashCode/ToString) operate on the boxed Kotlin value and
        // read the now-erased `.data` field, so hoisting them produces a `<self>.data` access on the value-type
        // shorthand (`ubyte`) that ilemit cannot resolve. They must NOT be hoisted — a call `u.toString()` defers to
        // the BCL primitive's ToString via member-call substitution. (A non-value alias like Boolean DOES hoist its
        // Equals/GetHashCode/ToString — those carry real Kotlin bodies and no erased field.)
        var isInlineValue = refs.IsInlineValueClass(fqn);
        var methods = new JsonArray();
        foreach (var m in td["methods"] as JsonArray ?? new JsonArray())
        {
            if (m is not JsonObject mo) continue;
            if ((mo["name"] as JsonValue)?.GetValue<string>() is not string mn) continue;
            if ((mo["static"] as JsonValue)?.GetValue<bool>() == true) continue;   // a top-level/companion static, not a member
            if (mo["body"] is not JsonArray mbody) continue;                        // abstract / no body
            // A property accessor (`get_`/`set_`) is normally a `clrPropGet`/`clrPropSet` on the BCL type, NOT a hoisted
            // helper — so blanket-skip it. EXCEPTION: a rule-3 accessor whose body binds to a BCL *method* (e.g. Regex's
            // `val pattern get() = toString()` — the BCL Regex has no `Pattern` property, only `ToString()`). Such an
            // accessor MUST be hoisted so `re.pattern` routes to `dotkt$ClrH_Regex.get_pattern(recv)`. But hoist it ONLY
            // when the body reads NO backing field: a rule-3 accessor that reads `{"k":"field"}` (another alias's real
            // backing field) would NRE ilemit's ResolveField (no such field on the BCL type) — those stay clrPropGet/Set.
            if ((mn.StartsWith("get_", StringComparison.Ordinal) || mn.StartsWith("set_", StringComparison.Ordinal))
                && BodyReadsBackingField(mbody)) continue;
            if (isInlineValue && (mo["objectOverride"] as JsonValue)?.GetValue<bool>() == true) continue;  // see note above
            if (!refs.IsRule3Member(fqn, mn)) continue;   // ref.dll: concrete + intrinsic-less (matches the rule-3 call routing)
            methods.Add(HoistMethod(mo, aliasToken, classTps));
        }
        if (methods.Count == 0) return null;
        return new JsonObject
        {
            ["name"] = ReferenceMetadataIndex.HelperTypeName(fqn),
            ["kind"] = "class",
            // #68: the rule-3 static helper is compiler-generated — flag it so ilemit stamps [CompilerGenerated].
            ["generated"] = true,
            ["abstract"] = false,
            ["vis"] = "public",
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["methods"] = methods,
        };
    }

    // An instance member -> a static helper method: prepend a `__self` param typed as the alias owner, rewrite the
    // dispatch `this` to that `__self`, and declare the class type params ahead of the method's own (a generic alias's
    // helper needs them for `__self`). Produces the helper shape ilemit expects (a static method with a `__self` first param).
    static JsonObject HoistMethod(JsonObject m, string aliasToken, JsonArray classTps)
    {
        // A GENERIC alias owner (ArrayList<E>, HashMap<K,V>) must type `__self` as the CONSTRUCTED generic
        // `kotlin.collections.ArrayList[gp:E]` — BirTypeLowering then lowers it to `clrg:System...List[gp:E]` (with
        // arity). A bare `kotlin.collections.ArrayList` token would lower to a non-generic `clr:System...List` that
        // ilemit cannot resolve. The class type params (bare-string entries like "E") become the `gp:` args; they are
        // declared on the method via MergeTypeParams below, so `gp:E` is in scope. (Mirrors kotc's old birType(__self).)
        // The class type params are declared on the static helper as its OWN (method-scope) params AHEAD of the
        // method's own (MergeTypeParams), so `__self`'s generic args are METHOD-scope tv by flattened position.
        TypeNode selfType = classTps is { Count: > 0 }
            ? new TypeNode.Fqn(aliasToken, Enumerable.Range(0, classTps.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray())
            : new TypeNode.Fqn(aliasToken);
        var ps = new JsonArray { new JsonObject { ["name"] = "__self", ["type"] = TypeJson.Write(selfType) } };
        foreach (var p in m["params"] as JsonArray ?? new JsonArray()) ps.Add(p?.DeepClone());
        var outM = new JsonObject
        {
            ["name"] = (m["name"] as JsonValue)!.DeepClone(),
            ["static"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
        };
        var tps = MergeTypeParams(classTps, m["typeParams"] as JsonArray);
        if (tps != null) outM["typeParams"] = tps;
        outM["params"] = ps;
        outM["ret"] = m["ret"]?.DeepClone();
        outM["body"] = RewriteThis(m["body"]);
        return outM;
    }

    // True if the accessor body reads (or writes) a raw backing field — a `{"k":"field"}` / `{"k":"setFieldExpr"}` node.
    // Such an accessor cannot be hoisted onto the BCL alias type (ilemit's ResolveField NREs — the BCL type has no such
    // field), so it stays a clrPropGet/Set. A rule-3 accessor with NO field node (e.g. `get() = toString()`) is safe.
    static bool BodyReadsBackingField(JsonNode n)
    {
        if (n is JsonObject o)
        {
            if ((o["k"] as JsonValue)?.GetValue<string>() is string k
                && (k == "field" || k == "setFieldExpr" || k == "staticField" || k == "staticFieldSet")) return true;
            foreach (var kv in o) if (kv.Value != null && BodyReadsBackingField(kv.Value)) return true;
            return false;
        }
        if (n is JsonArray a)
        {
            foreach (var i in a) if (i != null && BodyReadsBackingField(i)) return true;
            return false;
        }
        return false;
    }

    static JsonArray MergeTypeParams(JsonArray a, JsonArray b)
    {
        if ((a == null || a.Count == 0) && (b == null || b.Count == 0)) return null;
        var r = new JsonArray();
        if (a != null) foreach (var x in a) r.Add(x?.DeepClone());
        if (b != null) foreach (var x in b) r.Add(x?.DeepClone());
        return r;
    }

    // Rewrite every dispatch-receiver node {"k":"this"} to the hoisted static's leading `__self` local. kotc lifts all
    // lambdas/local funs to separate methods, so within a single member body every {"k":"this"} is THIS receiver.
    static JsonNode RewriteThis(JsonNode n)
    {
        if (n is JsonObject o)
        {
            if ((o["k"] as JsonValue)?.GetValue<string>() == "this")
                return new JsonObject { ["k"] = "local", ["name"] = "__self" };
            var c = new JsonObject();
            foreach (var kv in o) c[kv.Key] = kv.Value == null ? null : RewriteThis(kv.Value);
            return c;
        }
        if (n is JsonArray a)
        {
            var c = new JsonArray();
            foreach (var i in a) c.Add(i == null ? null : RewriteThis(i));
            return c;
        }
        return n?.DeepClone();
    }
}

