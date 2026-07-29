using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// VALUE-TYPE PLATFORM SLOT WRITE COERCION (#11): the WRITE twin of #8. A reference-KLIB-projected value-type platform
// property/field — e.g. `System.Threading.ThreadLocal<Int>.Value`, whose setter slot is a BARE `int32` (T reified to a
// value type, §9a-bis / clr-all-type-args-reified) — can, under platform-type laxity, be assigned a NULLABLE or `null`
// source (`ti.Value = someIntQ`, `ti.Value = null`). kotc is .NET-agnostic and emits the plain nullable source; the
// slot vs source type mismatch (`Nullable<Int32>` value flowing into a bare `int32` setter) is a Kotlin<->CLR relation
// fact, so the coercion belongs HERE, not in kotc/ilemit:
//   - a `Nullable<V>` source (a genuine Kotlin `Int?`) into a bare value slot -> unwrap it via the existing
//     `nullableValue` node (ilemit emits `Nullable<V>.get_Value()` — which throws InvalidOperationException at runtime
//     if the source is dynamically `null`, the faithful "no value to store" outcome for a null-less value slot).
//   - a literal `null` source into a bare value slot -> a LOUD emit-time error. A CLR value type has no null
//     representation, so this is a user-code bug; a clear diagnostic beats a silent `default(V)` (0).
// Only a BARE value slot triggers coercion: a genuine `Nullable<V>` .NET property (a real `int?` slot) or a
// `ThreadLocal<Int?>` (owner-arg `Int?`) keeps the source verbatim, and a reference slot is untouched (a `String!`
// platform reference has real null). Runs right AFTER NetInteropBinding (so the `clrPropSet` nodes exist) and BEFORE
// BirTypeLowering (owner args + the `nullableValue` elem are still pure-Kotlin `kotlin.*`, lowered downstream exactly
// as PrimitiveOperatorLowering's `nullableValue` is). BirScope-tracking walk (mirrors PrimitiveOperatorLowering) so a
// `{local q}` source resolves its declared static type. Non-ref build only (clrPropSet is a NetInteropBinding product).
static class ValueSlotNullableWrite
{
    static ReferenceMetadataIndex _refs;

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs;
        switch (root)
        {
            case JsonObject o: WalkObject(o, BirScope.Empty); break;
            case JsonArray a: WalkArray(a, BirScope.Empty); break;
        }
    }

    static void WalkArray(JsonArray arr, BirScope scope)
    {
        // A statement sequence: a `var` enters scope for the SUBSEQUENT siblings only (lexical block scoping), so a
        // `var q` preceding the `clrPropSet` exprStmt is in scope when the setter is visited.
        var cur = scope;
        for (var i = 0; i < arr.Count; i++)
        {
            switch (arr[i])
            {
                case JsonObject co: WalkObject(co, cur); break;
                case JsonArray ca: WalkArray(ca, cur); break;
            }
            if (arr[i] is JsonObject vo && (vo["k"] as JsonValue)?.GetValue<string>() == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
    }

    static void WalkObject(JsonObject obj, BirScope scope)
    {
        var child = scope.Extend(obj);
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co, child); break;
                case JsonArray ca: WalkArray(ca, child); break;
            }
        if ((obj["k"] as JsonValue)?.GetValue<string>() == "clrPropSet") Coerce(obj, child);
    }

    static void Coerce(JsonObject node, BirScope scope)
    {
        var value = node["value"];
        if (value == null) return;
        // Resolve the owner .NET type + the setter slot's instantiated shape. Only a BARE value slot needs coercion.
        // Peel a Nullable/Oblivious/ByRef wrapper off the owner (a safe-call assignment `tl?.Value = q` spells the owner
        // `nullable(ThreadLocal<Int>)`) to reach the underlying Fqn — its `Args` drive the generic-param slot mapping.
        var ownerFqn = UnwrapOwnerFqn(node["type"]);
        if (ownerFqn == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqn.Name), ownerFqn.Args?.Length ?? 0);
        if (netType == null) return;
        var name = (node["name"] as JsonValue)?.GetValue<string>();
        if (name == null) return;
        var slotType = MemberType(netType, name);
        if (!SlotIsBareValue(slotType, ownerFqn.Args)) return;

        // A literal `null` into a null-less value slot -> loud emit-time error (no valid IL; a silent default(V) would
        // mask a user bug). Recognized by the source's `nullable(kotlin.Nothing)` static type or a raw null const.
        var src = StaticType.Surface(value, scope);
        if (IsNullSource(value, src))
            throw new InvalidOperationException(
                $"bir2cir (#11): cannot assign `null` to the value-type platform slot `{ownerFqn.Name}.{name}` — a CLR "
                + "value type has no null representation. Use an explicit Kotlin `Int?`-typed property for nullable value storage.");

        // A genuine `Nullable<V>` source (Kotlin `Int?`) -> unwrap to the bare `V` the slot expects.
        if (src is TypeNode.Nullable ns && ns.Of is TypeNode.Fqn vf && _refs.IsValueTypeFqn(vf.Name))
            node["value"] = new JsonObject
            {
                ["k"] = "nullableValue",
                ["elem"] = TypeNode.Write(ns.Of),
                ["e"] = value.DeepClone(),
            };
    }

    // Peel Nullable/Oblivious/ByRef wrappers off the clrPropSet owner slot to reach the underlying .NET Fqn (name +
    // type-args preserved) — mirrors NetInteropBinding.UnwrapFqn. null when there is no Fqn underneath.
    static TypeNode.Fqn UnwrapOwnerFqn(JsonNode ownerJson)
    {
        if (ownerJson == null) return null;
        if (ownerJson is JsonValue sv && sv.TryGetValue<string>(out var s) && s != null) return new TypeNode.Fqn(s);
        var t = TypeJson.Read(ownerJson);
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable nu: t = nu.Of; break;
                case TypeNode.Oblivious ob: t = ob.Of; break;
                case TypeNode.ByRef br: t = br.Of; break;
                default: return null;
            }
    }

    // True iff the setter slot, after generic instantiation with the owner's type-args, is a BARE (non-Nullable) value
    // type — the only case that needs a nullable/null source coerced. A generic-parameter slot maps to the owner arg at
    // its position (`ThreadLocal<T>.Value` param `T` -> owner arg 0); a `Nullable<...>` owner arg (`ThreadLocal<Int?>`)
    // or a concrete `Nullable<>` member type is a genuine nullable-value slot (keep the source verbatim); a concrete
    // struct is bare-value; a reference member is untouched.
    static bool SlotIsBareValue(Type slotType, TypeNode[] ownerArgs)
    {
        if (slotType == null) return false;
        if (slotType.IsGenericParameter)
        {
            // GenericParameterPosition indexes the DECLARING type's params, mapped against THIS owner's args. Exact for
            // a member declared on the owner itself (`ThreadLocal<T>.Value`); an inherited member whose param comes from
            // a base with a different arity would map to the wrong arg — but that only ever mis-decides toward a no-op /
            // a loud ilemit mismatch, never a silent miscompile (the `nullableValue` guard fails loud on a bad source).
            var pos = slotType.GenericParameterPosition;
            if (ownerArgs == null || pos < 0 || pos >= ownerArgs.Length) return false;
            return ownerArgs[pos] switch
            {
                TypeNode.Nullable => false,                              // ThreadLocal<Int?> -> Nullable<Int32> slot
                TypeNode.Fqn af => _refs.IsValueTypeFqn(af.Name),        // ThreadLocal<Int> -> bare int32 slot
                _ => false,
            };
        }
        if (slotType.IsGenericType && slotType.GetGenericTypeDefinition().FullName == "System.Nullable`1") return false;
        return slotType.IsValueType;
    }

    // A literal-null source: the frontend types `= null` as `nullable(kotlin.Nothing)`, and the value node is a `const`
    // carrying a JSON-null `value`. Either signal identifies it (the type is the robust primary; the const is a backup).
    static bool IsNullSource(JsonNode value, TypeNode src)
    {
        if (src is TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Nothing" } }) return true;
        return value is JsonObject o
            && (o["k"] as JsonValue)?.GetValue<string>() == "const"
            && o.ContainsKey("value") && o["value"] == null;
    }

    // The .NET member type (PropertyType / FieldType) of a non-indexed property OR field named `name`, walking the type
    // + its bases + interfaces — the two member kinds NetInteropBinding routes to clrPropSet. null when absent.
    static Type MemberType(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return p.PropertyType;
                foreach (var fi in cur.GetFields(Flags))
                    if (fi.Name == name) return fi.FieldType;
            }
            catch { /* metadata-load edge on a malformed member table — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return null;
    }
}
