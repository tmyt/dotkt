using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// VALUE-TYPE PLATFORM SLOT ACCESS COERCION (#11/#501): a reference-KLIB-projected property/field
// may expose either a bare CLR value slot or a structural Nullable<V> slot. Kotlin platform flexibility and smart
// casts can feed the opposite representation into it. kotc is .NET-agnostic and emits the source representation; the
// slot vs source mismatch is a Kotlin<->CLR relation fact, so the coercion belongs HERE, not in kotc/ilemit:
//   - a `Nullable<V>` source (a genuine Kotlin `Int?`) into a bare value slot -> unwrap it via the existing
//     `nullableValue` node (ilemit emits `Nullable<V>.get_Value()` — which throws InvalidOperationException at runtime
//     if the source is dynamically `null`, the faithful "no value to store" outcome for a null-less value slot).
//   - a literal `null` source into a bare value slot -> a LOUD emit-time error. A CLR value type has no null
//     representation, so this is a user-code bug; a clear diagnostic beats a silent `default(V)` (0).
//   - a bare V source into a structural Nullable<V> slot -> construct Nullable<V> explicitly.
// On reads, stamp that same reflected physical slot onto `ret`/`sty`; a projected `T!` can physically be Nullable<T>
// when the CLR declaration is `T? where T : struct`, and a bare frontend stamp would otherwise corrupt the receiving
// local before any consumer can unwrap it. Reference slots are untouched (a `String!` platform reference has real
// null). Runs right AFTER NetInteropBinding
// (so the `clrPropSet` nodes exist) and BEFORE
// BirTypeLowering (owner args + the `nullableValue` elem are still pure-Kotlin `kotlin.*`, lowered downstream exactly
// as PrimitiveOperatorLowering's `nullableValue` is). BirScope-tracking walk (mirrors PrimitiveOperatorLowering) so a
// `{local q}` source resolves its declared static type. Non-ref build only (clrPropSet is a NetInteropBinding product).
static class ValueSlotNullableWrite
{
    static ReferenceMetadataIndex _refs;
    static ValueTypeOracle _isValue;

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        _refs = refs;
        _isValue = isValue;
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
        switch ((obj["k"] as JsonValue)?.GetValue<string>())
        {
            case "clrPropSet": CoerceWrite(obj, child); break;
            case "clrPropGet": StampRead(obj, child); break;
        }
    }

    static void CoerceWrite(JsonObject node, BirScope scope)
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
        var src = StaticType.Surface(value, scope);
        var slotType = MemberType(netType, name);
        var target = ConcreteValueSlot(slotType, ownerFqn.Args, src);
        if (target == null) return;

        // A literal `null` into a null-less value slot -> loud emit-time error (no valid IL; a silent default(V) would
        // mask a user bug). Recognized by the source's `nullable(kotlin.Nothing)` static type or a raw null const.
        if (target is TypeNode.Fqn bare && _isValue(bare) && IsNullSource(value, src))
            throw new InvalidOperationException(
                $"bir2cir (#11): cannot assign `null` to the value-type platform slot `{ownerFqn.Name}.{name}` — a CLR "
                + "value type has no null representation. Use an explicit Kotlin `Int?`-typed property for nullable value storage.");
        if (NullableTvErasureCallRealign.CoerceForFixedSlot(value, src, target, _isValue) is JsonNode coerced)
            node["value"] = coerced;
    }

    static void StampRead(JsonObject node, BirScope scope)
    {
        var ownerFqn = UnwrapOwnerFqn(node["type"]);
        if (ownerFqn == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqn.Name), ownerFqn.Args?.Length ?? 0);
        var name = (node["name"] as JsonValue)?.GetValue<string>();
        if (netType == null || name == null) return;
        var surface = StaticType.Surface(node, scope);
        var target = ConcreteValueSlot(MemberType(netType, name), ownerFqn.Args, surface);
        if (target == null) return;
        node["ret"] = TypeJson.Write(target);
        if (node["sty"] != null) node["sty"] = TypeJson.Write(target);
    }

    // Peel Nullable/Oblivious/ByRef wrappers off the clrPropSet owner slot to reach the underlying .NET Fqn (name +
    // type-args preserved) — mirrors NetInteropBinding.UnwrapFqn. null when there is no Fqn underneath.
    static TypeNode.Fqn UnwrapOwnerFqn(JsonNode ownerJson)
    {
        if (ownerJson == null) return null;
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

    // The concrete structural value slot after owner substitution. Reflection supplies the declaration shape, while
    // BIR's owner args supply local/emitted types Reflection cannot load yet. For a concrete reflected Nullable<V>,
    // Kotlin has already type-checked the assignment; the source's exact value Fqn is therefore the authoritative
    // pre-lowering spelling of V (including nested/generic identity).
    static TypeNode ConcreteValueSlot(Type slotType, TypeNode[] ownerArgs, TypeNode src)
    {
        if (slotType == null) return null;
        if (slotType.IsGenericParameter)
        {
            var pos = slotType.GenericParameterPosition;
            if (ownerArgs == null || pos < 0 || pos >= ownerArgs.Length) return null;
            return ownerArgs[pos] switch
            {
                TypeNode.Fqn direct when _isValue(direct) => direct,
                TypeNode.Nullable { Of: TypeNode.Fqn nullableElem } nullable
                    when _isValue(nullableElem) => nullable,
                _ => null,
            };
        }
        if (slotType.IsGenericType && slotType.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var reflectedElem = slotType.GetGenericArguments()[0];
            TypeNode elem = null;
            if (reflectedElem.IsGenericParameter)
            {
                var pos = reflectedElem.GenericParameterPosition;
                if (ownerArgs != null && pos >= 0 && pos < ownerArgs.Length)
                    elem = ownerArgs[pos] is TypeNode.Nullable n ? n.Of : ownerArgs[pos];
            }
            elem ??= UnwrapSurface(src);
            return elem is TypeNode.Fqn concreteElem && _isValue(concreteElem)
                ? new TypeNode.Nullable(concreteElem) : null;
        }
        // For a bare reflected value slot, the source/surface supplies the exact pre-lowering V spelling. Platform
        // (`oblivious`) and nullable wrappers annotate the Kotlin view but do not change this physical bare slot.
        var bareSurface = UnwrapSurface(src);
        if (slotType.IsValueType && bareSurface is TypeNode.Fqn bareElem && _isValue(bareElem)) return bareElem;
        return null;
    }

    static TypeNode UnwrapSurface(TypeNode surface)
    {
        while (true)
            switch (surface)
            {
                case TypeNode.Nullable nullable: surface = nullable.Of; break;
                case TypeNode.Oblivious oblivious: surface = oblivious.Of; break;
                default: return surface;
            }
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
