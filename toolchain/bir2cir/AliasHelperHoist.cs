using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// RULE-3 HOIST (ALL CLR-bound alias classes). kotc no longer synthesizes the `dotkt$ClrH_<owner>` helper for ANY
// @ClrTypeAlias class whose concrete intrinsic-less members carry real bodies — the alias-only files (kotlin.String's
// subSequence, plus kotlin.Boolean/kotlin.Char operator stubs) AND the MIXED files (StringBuilder/UInt/collections/
// Regex). kotc emits each such alias class as a PLAIN BIR type; this pass reads the ref.dll @ClrTypeAlias index, hoists
// those rule-3 members into the static helper (the dispatch `this` becomes a leading `__self` param), and drops the
// original alias implementation. Only a compiler-generated ownership shell may remain when any physical nested
// child requires a CLR enclosing type; the alias implementation itself must NEVER reach ilemit (its
// equals(Any?)/toString()/length members
// would clash with System.String/System.Object). The rule-3 CALL routing in MemberCallSubstitution already targets
// `dotkt$ClrH_<owner>.<member>(recv, ..)` by name, so emitting the helper here closes the loop. This is the SOLE home
// of rule-3 helper synthesis. Runs only in substitute/app builds (never ref).
static class AliasHelperHoist
{
    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return root;
        RehomeGeneratedMethods(obj, types, refs);
        var rebuilt = new JsonArray();
        var changed = false;
        foreach (var t in types)
        {
            if (t is JsonObject td && IsAliasTypeDef(td, refs, out var fqn))
            {
                changed = true;                                  // alias implementation -> dropped (and possibly hoisted)
                // A physical nested child still needs its declaring TypeDef after the alias implementation is
                // substituted to the BCL type. Retain only a compiler-generated ownership shell plus any companion
                // value field; no ordinary alias member/backing state survives. Without the shell, the child cannot
                // preserve its semantic owner in CLR metadata.
                if (BuildOwnershipHost(td, types) is { } host) rebuilt.Add(host);
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

    // A @ClrTypeAlias declaration has no physical TypeDef on which a source-owned generated helper can live. Consume
    // that explicit semantic-owner fact at the alias representation boundary and retain the frontend's file-facade
    // projection. Delegate edges and implementation classifiers carried inside the helper move as one declaration.
    static void RehomeGeneratedMethods(JsonObject root, JsonArray types, ReferenceMetadataIndex refs)
    {
        if (root["methods"] is not JsonArray methods
            || (root["fileClass"] as JsonValue)?.GetValue<string>() is not string fileClass)
            return;
        var aliases = types.OfType<JsonObject>()
            .Where(type => IsAliasTypeDef(type, refs, out _))
            .Select(type => (type["name"] as JsonValue)?.GetValue<string>())
            .Where(name => name != null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var method in methods.OfType<JsonObject>())
        {
            if ((method["semanticOwner"] as JsonValue)?.GetValue<string>() is not string owner
                || !aliases.Contains(owner)
                || (method["name"] as JsonValue)?.GetValue<string>() is not string name)
                continue;
            // A non-capturing lambda/local adapter physically becomes a generic FILE-FACADE method. kotc states the
            // exact lexical-to-declaration correspondence on its newDelegate.typeArgs edge: an enclosing !i / !!j
            // becomes this helper's !!k. Consume that authored fact while changing the physical owner; leaving the
            // declaration in the source type frame would put an out-of-scope !i in CIR.
            var frame = GeneratedMethodFrame(root, owner, name, method);
            RewriteLexicalTypes(method, type => RemapTypeVariables(type, frame));
            method["semanticOwner"] = fileClass;
            RewriteOwnedDeclaration(method, owner, fileClass);
            RewriteDelegateOwner(root, owner, fileClass, name);
        }
    }

    static Dictionary<(string Scope, int Index), int> GeneratedMethodFrame(
        JsonObject root, string owner, string methodName, JsonObject declaration)
    {
        var arity = (declaration["typeParams"] as JsonArray)?.Count ?? 0;
        if (arity == 0) return new();

        Dictionary<(string Scope, int Index), int> result = null;
        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if ((obj["k"] as JsonValue)?.GetValue<string>() == "newDelegate"
                    && (obj["method"] as JsonValue)?.GetValue<string>() == methodName
                    && TypeJson.OwnerName(obj["calleeOwner"]) == owner
                    && obj["typeArgs"] is JsonArray typeArgs
                    && typeArgs.Count == arity)
                {
                    var candidate = new Dictionary<(string Scope, int Index), int>();
                    for (var i = 0; i < typeArgs.Count; i++)
                    {
                        if (TypeJson.Read(typeArgs[i]) is not TypeNode.Tv tv)
                        {
                            // A cloned/constructed use of the same target is an application, not the declaration-frame
                            // witness. The original lexical edge remains in the declaring body and is the one consumed.
                            candidate.Clear();
                            break;
                        }
                        candidate[(tv.Scope, tv.I)] = i;
                    }
                    if (candidate.Count > 0)
                    {
                        if (candidate.Count != arity)
                            throw new InvalidOperationException(
                                $"generated alias helper '{owner}.{methodName}' does not carry a one-to-one lexical generic frame");
                        if (result != null && !result.OrderBy(x => x.Key).SequenceEqual(candidate.OrderBy(x => x.Key)))
                            throw new InvalidOperationException(
                                $"generated alias helper '{owner}.{methodName}' carries inconsistent lexical generic frames");
                        result = candidate;
                    }
                }
                foreach (var value in obj.Select(kv => kv.Value).ToList())
                    if (value != null) Visit(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array.ToList())
                    if (value != null) Visit(value);
        }
        Visit(root);
        return result ?? throw new InvalidOperationException(
            $"generated alias helper '{owner}.{methodName}' has no newDelegate edge defining its generic frame");
    }

    static TypeNode RemapTypeVariables(TypeNode type, IReadOnlyDictionary<(string Scope, int Index), int> frame) =>
        RewriteType(type, tv => frame.TryGetValue((tv.Scope, tv.I), out var index)
            ? new TypeNode.Tv("method", index)
            : tv);

    static TypeNode RewriteType(TypeNode type, Func<TypeNode.Tv, TypeNode> rewrite) => type switch
    {
        TypeNode.Tv tv => rewrite(tv),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(a => RewriteType(a, rewrite)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, RewriteType(p.Of, rewrite)),
        TypeNode.Nullable n => new TypeNode.Nullable(RewriteType(n.Of, rewrite)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(RewriteType(o.Of, rewrite)),
        TypeNode.Array a => new TypeNode.Array(RewriteType(a.Elem, rewrite)),
        TypeNode.ByRef b => new TypeNode.ByRef(RewriteType(b.Of, rewrite)),
        TypeNode.Ptr p => new TypeNode.Ptr(RewriteType(p.Of, rewrite)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, RewriteType(m.M, rewrite), RewriteType(m.Of, rewrite)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, RewriteType(fn.Ret, rewrite),
            fn.Params.Select(p => RewriteType(p, rewrite)).ToArray(),
            fn.Recv == null ? null : RewriteType(fn.Recv, rewrite), fn.Clr,
            fn.Ctx?.Select(p => RewriteType(p, rewrite)).ToArray()),
        _ => type,
    };

    // Rewrite types owned by this lexical declaration. Descriptor vectors (`sig`, resolvedMemberParams, …) describe another
    // declaration in that declaration's own frame and therefore stay untouched; typeArgs/owner applications and
    // ordinary value types are use-site facts and do move with this declaration.
    static void RewriteLexicalTypes(JsonNode node, Func<TypeNode, TypeNode> rewrite)
    {
        if (node is JsonObject obj)
        {
            var kind = (obj["k"] as JsonValue)?.GetValue<string>();
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var value = obj[key];
                if (value == null) continue;
                if (key is "sig" or "resolvedMemberParams" or "shapeTypes" or "paramSig"
                    or "delegationSig" || (key == "argTypes" && kind != "new"))
                    continue;
                if (TypeJson.IsType(value)) obj[key] = TypeJson.Write(rewrite(TypeJson.Read(value)));
                else RewriteLexicalTypes(value, rewrite);
            }
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
            {
                var value = array[i];
                if (value == null) continue;
                if (TypeJson.IsType(value)) array[i] = TypeJson.Write(rewrite(TypeJson.Read(value)));
                else RewriteLexicalTypes(value, rewrite);
            }
    }

    static void RewriteOwnedDeclaration(JsonNode node, string oldOwner, string newOwner)
    {
        if (node is JsonObject obj)
        {
            if (obj["synthClass"] is JsonObject synth
                && (synth["semanticOwner"] as JsonValue)?.GetValue<string>() == oldOwner)
                synth["semanticOwner"] = newOwner;
            foreach (var value in obj.Select(kv => kv.Value).ToList())
                if (value != null) RewriteOwnedDeclaration(value, oldOwner, newOwner);
        }
        else if (node is JsonArray array)
            foreach (var value in array.ToList())
                if (value != null) RewriteOwnedDeclaration(value, oldOwner, newOwner);
    }

    static void RewriteDelegateOwner(JsonNode node, string oldOwner, string newOwner, string method)
    {
        if (node is JsonObject obj)
        {
            if ((obj["method"] as JsonValue)?.GetValue<string>() == method
                && TypeJson.OwnerName(obj["calleeOwner"]) == oldOwner)
                obj["calleeOwner"] = TypeJson.Write(new TypeNode.Fqn(newOwner));
            foreach (var value in obj.Select(kv => kv.Value).ToList())
                if (value != null) RewriteDelegateOwner(value, oldOwner, newOwner, method);
        }
        else if (node is JsonArray array)
            foreach (var value in array.ToList())
                if (value != null) RewriteDelegateOwner(value, oldOwner, newOwner, method);
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

    static JsonObject BuildOwnershipHost(JsonObject owner, JsonArray types)
    {
        var ownerName = (owner["name"] as JsonValue)?.GetValue<string>();
        if (ownerName == null) return null;
        // The alias implementation itself disappears, but a semantic child still needs a real TypeDef on which CIR
        // can place its nested definition. Companion lowering may already have authored `nestedIn`; ordinary Kotlin
        // nested/local ownership is still the BIR `semanticOwner` fact at this point.
        var ownedTypes = types.OfType<JsonObject>()
            .Where(t => (t["nestedIn"] as JsonValue)?.GetValue<string>() == ownerName ||
                (t["semanticOwner"] as JsonValue)?.GetValue<string>() == ownerName)
            .ToArray();
        if (ownedTypes.Length == 0) return null;
        var carrierNames = ownedTypes
            .Where(t => t["companionCarrier"] is JsonObject)
            .Select(t => (t["name"] as JsonValue)?.GetValue<string>())
            .Where(n => n != null)
            .ToHashSet(StringComparer.Ordinal);

        var fields = new JsonArray();
        foreach (var field in owner["fields"] as JsonArray ?? [])
            if (field is JsonObject f && (f["static"] as JsonValue)?.GetValue<bool>() == true &&
                TypeJson.Read(f["type"]) is TypeNode.Fqn ft && carrierNames.Contains(ft.Name))
                fields.Add(f.DeepClone());

        var host = new JsonObject
        {
            ["name"] = ownerName,
            ["kind"] = "class",
            ["generated"] = true,
            ["abstract"] = false,
            ["vis"] = owner["vis"]?.DeepClone() ?? "public",
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = fields,
            ["ctors"] = new JsonArray(),
            ["methods"] = new JsonArray(),
            ["properties"] = new JsonArray(),
        };
        foreach (var key in new[] { "typeParams", "capturedTypeParams", "nestedIn", "semanticOwner" })
            if (owner[key] is JsonNode value) host[key] = value.DeepClone();
        return host;
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
            // A property accessor normally becomes clrPropGet/clrPropSet on the BCL type, not a hoisted helper.
            // Exception: a rule-3 accessor whose body binds to a BCL method (for example Regex.pattern -> ToString()).
            // Hoist it only when the body reads no backing field: an alias accessor reading a Kotlin backing field has
            // no corresponding field on the BCL type and remains on the property path.
            if (KotlinPropertyAccessors.TryIdentity(mo, out _, out _)
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
        // declared on the method via MergeTypeParams below, so `gp:E` is in the helper's method scope.
        // The class type params are declared on the static helper as its OWN (method-scope) params AHEAD of the
        // method's own (MergeTypeParams), so `__self`'s generic args are METHOD-scope tv by flattened position.
        TypeNode selfType = classTps is { Count: > 0 }
            ? new TypeNode.Fqn(aliasToken, Enumerable.Range(0, classTps.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray())
            : new TypeNode.Fqn(aliasToken);
        var classArity = classTps?.Count ?? 0;
        TypeNode Rebind(TypeNode type) => RewriteType(type, tv => tv.Scope switch
        {
            "type" => new TypeNode.Tv("method", tv.I),
            "method" => new TypeNode.Tv("method", classArity + tv.I),
            _ => tv,
        });
        var rewritten = m.DeepClone() as JsonObject;
        RewriteLexicalTypes(rewritten, Rebind);
        var rewrittenClassTps = classTps?.DeepClone() as JsonArray;
        if (rewrittenClassTps != null) RewriteLexicalTypes(rewrittenClassTps, Rebind);

        var ps = new JsonArray { new JsonObject { ["name"] = "__self", ["type"] = TypeJson.Write(selfType) } };
        foreach (var p in rewritten["params"] as JsonArray ?? new JsonArray()) ps.Add(p?.DeepClone());
        var outM = new JsonObject
        {
            ["name"] = (rewritten["name"] as JsonValue)!.DeepClone(),
            ["static"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
        };
        var tps = MergeTypeParams(rewrittenClassTps, rewritten["typeParams"] as JsonArray);
        if (tps != null) outM["typeParams"] = tps;
        outM["params"] = ps;
        outM["ret"] = rewritten["ret"]?.DeepClone();
        outM["body"] = RewriteThis(rewritten["body"]);
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
