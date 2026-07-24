using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin/JVM leaves an unchecked `Any? as T` at the erased Object boundary.  CLR generics are reified, so emitting
// the source signature literally as `T Read(...)` moves the cast into the callee (`unbox.any !T`).  That is too early:
//
//   val value = readNullableSlot() as T
//   if (!consume) return
//   use(value)
//
// must not throw for a value-type T when the nullable slot is null and `value` is never consumed.  Represent such a
// method physically as returning object and preserve its source-level T return in [KotlinType].  Calls normally keep
// their logical return hint and therefore narrow immediately.  The important deferred-use form — a Tv local directly
// initialized by the call — keeps both the call result and local as object; existing CIR argument/return/cast coercions
// narrow it at the first real typed consumer.
//
// This is a Kotlin-to-CLR ABI decision, so it belongs in bir2cir.  ilemit merely emits the object signature and the
// explicit CIR types.  Detection is structural (return Tv + returned cast from a nullable/object static source), with
// exact owner/name/substituted-signature matching at calls; no library or declaration-name special cases.
static class UncheckedGenericCastReturnErasure
{
    public sealed class DeclIndex
    {
        internal readonly Dictionary<string, List<Decl>> ByOwner = new(StringComparer.Ordinal);
        internal readonly List<Decl> TopLevel = new();
    }

    internal sealed class Decl
    {
        public string Owner;
        public string Name;
        public TypeNode[] Params;
        public TypeNode.Tv Return;
        public JsonObject Method;
    }

    public static DeclIndex Collect(IEnumerable<JsonNode> roots)
    {
        var index = new DeclIndex();
        foreach (var root in roots.OfType<JsonObject>())
            CollectContainer(root, index, topLevel: true);
        return index;
    }

    static void CollectContainer(JsonObject container, DeclIndex index, bool topLevel)
    {
        if (topLevel && container["methods"] is JsonArray topMethods)
            foreach (var method in topMethods.OfType<JsonObject>())
                AddCandidate(method, owner: null, index);

        if (container["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            var owner = Str(type["name"]);
            if (owner != null && type["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    AddCandidate(method, owner, index);
            CollectContainer(type, index, topLevel: false);
        }
    }

    static void AddCandidate(JsonObject method, string owner, DeclIndex index)
    {
        if (Str(method["name"]) is not string name
            // A virtual/override slot is one ABI shared by the whole hierarchy.  Erasing only the declaration whose
            // body happens to contain the cast changes its CLR slot and bypasses a derived unwrapping override (the
            // DispatchedTask<T>.getSuccessfulResult / CancellableContinuationImpl case).  Hierarchy-wide physical
            // signature coordination is a distinct lowering; this local deferred-carrier rule applies only to final
            // slots, where the declaration and every call resolve to the same method.
            || Bool(method["virtual"]) || Bool(method["override"]) || Bool(method["abstract"])
            || TypeJson.Read(method["ret"]) is not TypeNode.Tv ret
            || !HasReturnedUncheckedCast(method["body"], ret))
            return;

        var ps = method["params"] is JsonArray paramsArray
            ? paramsArray.OfType<JsonObject>().Select(p => TypeJson.Read(p["type"])).ToArray()
            : Array.Empty<TypeNode>();
        if (ps.Any(p => p == null)) return;

        var decl = new Decl { Owner = owner, Name = name, Params = ps, Return = ret, Method = method };
        if (owner == null)
        {
            index.TopLevel.Add(decl);
            return;
        }
        if (!index.ByOwner.TryGetValue(owner, out var declarations))
            index.ByOwner[owner] = declarations = new List<Decl>();
        declarations.Add(decl);
    }

    static bool HasReturnedUncheckedCast(JsonNode node, TypeNode.Tv ret)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "return" && obj["value"] is JsonObject value
                    && Str(value["k"]) == "cast"
                    && TypeJson.Read(value["type"]) is TypeNode.Tv cast && cast == ret
                    && value["e"] is JsonObject source && IsNullableOrObjectSource(source))
                    return true;
                return obj.Any(kv => kv.Value != null && HasReturnedUncheckedCast(kv.Value, ret));
            case JsonArray arr:
                return arr.Any(value => value != null && HasReturnedUncheckedCast(value, ret));
            default:
                return false;
        }
    }

    static bool IsNullableOrObjectSource(JsonObject source)
    {
        var type = TypeJson.Read(source["sty"]) ?? TypeJson.Read(source["type"]) ?? TypeJson.Read(source["ret"]);
        return type switch
        {
            TypeNode.Nullable => true,
            TypeNode.Oblivious => true,
            TypeNode.Fqn f => f.Name is "kotlin.Any" or "object" or "System.Object",
            _ => false,
        };
    }

    public static void Apply(JsonNode root, DeclIndex index)
    {
        if (root is not JsonObject obj) return;

        // Mutate each declaration exactly once.  The fact is the PRE-lowering Kotlin TypeNode; RoundtripMetadata
        // consumes it after type lowering and facadegen restores the source signature for downstream Kotlin.
        foreach (var decl in index.TopLevel.Concat(index.ByOwner.Values.SelectMany(x => x)))
        {
            if (TypeJson.Read(decl.Method["ret"]) is not TypeNode.Tv) continue;
            decl.Method["retKotlinType"] = TypeNode.ToJson(decl.Return);
            decl.Method["ret"] = TypeJson.Fqn("object");
            RetypeReturnedCasts(decl.Method["body"], decl.Return);
        }

        RewriteMethods(obj["methods"], index);
        if (obj["types"] is JsonArray types)
            foreach (var type in types)
                if (type != null) ApplyCallsOnly(type, index);
    }

    // Consumer-side half of the object-return ABI.  facadegen has already restored the Kotlin expression type into
    // BIR's `sty`, while the reference index sees the producer's physical Object return and trusted [KotlinType]
    // declaration carrier.  Turn that exact pair into CIR's explicit `ret`: BirTypeLowering then lowers the concrete
    // Kotlin type and ilemit mechanically emits the required unbox/cast at the call boundary.
    public static void ApplyReferenced(JsonNode root, ReferenceMetadataIndex refs)
    {
        RewriteReferencedCalls(root, refs);
    }

    static void RewriteReferencedCalls(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject call:
            {
                var kind = Str(call["k"]);
                if (kind is "callStatic" or "callInstance"
                    && call["ret"] == null
                    && TypeJson.Read(call["sty"]) is TypeNode expected
                    // A Unit-valued expression statement deliberately ignores the physical Object result.  Turning
                    // Kotlin Unit into CIR `ret:void` would suppress exprStmt's pop while the CLR call still pushes
                    // Object, leaving a value on the enclosing void method's return stack.
                    && !IsObject(expected) && !IsUnit(expected)
                    && Str(call["method"]) is string name
                    && TryReferencedOwner(call, kind == "callStatic", out var ownerName, out var ownerArgs)
                    && refs.TryUncheckedGenericCastReturn(
                        ownerName,
                        name,
                        kind == "callStatic",
                        (call["args"] as JsonArray)?.Count ?? 0,
                        ReadTypes(call["typeArgs"]).Length,
                        out var declaredReturn))
                {
                    var actual = Substitute(declaredReturn, ownerArgs, ReadTypes(call["typeArgs"]));
                    if (actual != null && actual.Equals(expected))
                        call["ret"] = call["sty"].DeepClone();
                }
                foreach (var child in call.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RewriteReferencedCalls(child, refs);
                break;
            }
            case JsonArray array:
                foreach (var child in array.Where(v => v != null).ToList())
                    RewriteReferencedCalls(child, refs);
                break;
        }
    }

    static bool TryReferencedOwner(JsonObject call, bool isStatic, out string ownerName, out TypeNode[] ownerArgs)
    {
        ownerName = null;
        ownerArgs = Array.Empty<TypeNode>();
        var ownerNode = isStatic
            ? TypeJson.Read(call["ownerType"]) ?? TypeJson.Read(call["owner"]) ?? TypeJson.Read(call["calleeOwner"])
            : TypeJson.Read(call["ownerType"]);
        if (ownerNode is not TypeNode.Fqn owner) return false;
        ownerName = owner.Name;
        ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
        return true;
    }

    static bool IsObject(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "System.Object" or "object" or "kotlin.Any";

    static bool IsUnit(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "kotlin.Unit" or "void" or "System.Void";

    static void ApplyCallsOnly(JsonNode node, DeclIndex index)
    {
        if (node is not JsonObject obj) return;
        RewriteMethods(obj["methods"], index);
        if (obj["types"] is JsonArray types)
            foreach (var type in types)
                if (type != null) ApplyCallsOnly(type, index);
    }

    static void RewriteMethods(JsonNode methods, DeclIndex index)
    {
        if (methods is not JsonArray array) return;
        foreach (var method in array.OfType<JsonObject>())
        {
            var deferred = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
            RewriteDeferredLocals(method["body"], index, deferred);
            if (deferred.Count > 0) RenarrowTypedUses(method["body"], deferred);
        }
    }

    static void RewriteDeferredLocals(JsonNode node, DeclIndex index,
        Dictionary<string, TypeNode.Tv> deferred)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "var"
                    && Str(obj["name"]) is string localName
                    && TypeJson.Read(obj["type"]) is TypeNode.Tv localType
                    && obj["init"] is JsonObject call
                    && TryResolve(call, index, out var logicalReturn)
                    && logicalReturn is TypeNode.Tv resolved
                    && resolved == localType)
                {
                    obj["type"] = TypeJson.Fqn("object");
                    call["ret"] = TypeJson.Fqn("object");
                    if (call["dynRet"] != null) call["dynRet"] = TypeJson.Fqn("object");
                    if (call["sty"] != null) call["sty"] = TypeJson.Fqn("object");
                    deferred[localName] = localType;
                }
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RewriteDeferredLocals(child, index, deferred);
                break;
            case JsonArray arr:
                foreach (var child in arr.Where(v => v != null).ToList())
                    RewriteDeferredLocals(child, index, deferred);
                break;
        }
    }

    // A deferred object local must be explicitly narrowed when it enters a Tv-typed call slot.  Although ilemit's
    // ordinary argument coercion handles reflected methods, a same-assembly call re-anchored on a TypeBuilder generic
    // owner cannot always expose its substituted ParameterInfo.  State the conversion in CIR with a cast so emission
    // remains mechanical.  Object/null tests and branches are not call arguments and therefore remain non-narrowing.
    static void RenarrowTypedUses(JsonNode node, IReadOnlyDictionary<string, TypeNode.Tv> deferred)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var ownerArgs = TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn owner
                    ? owner.Args ?? Array.Empty<TypeNode>()
                    : Array.Empty<TypeNode>();
                var methodArgs = ReadTypes(obj["typeArgs"]);
                var sig = ReadTypes(obj["sig"]);
                if (sig.Length == 0) sig = ReadTypes(obj["argTypes"]);
                if (obj["args"] is JsonArray args && sig.Length == args.Count)
                    for (var i = 0; i < args.Count; i++)
                    {
                        var expected = Substitute(sig[i], ownerArgs, methodArgs);
                        if (args[i] is JsonObject arg
                            && Str(arg["k"]) == "local"
                            && Str(arg["name"]) is string name
                            && deferred.TryGetValue(name, out var logical)
                            && expected.Equals(logical))
                            args[i] = new JsonObject
                            {
                                ["k"] = "cast",
                                ["type"] = TypeJson.Write(logical),
                                ["e"] = arg.DeepClone(),
                            };
                    }

                // Generic-receiver calls carry the receiver's required type directly.  Narrow only an exact Tv owner;
                // concrete/object owners deliberately keep the erased local as a reference.
                if (obj["recv"] is JsonObject recv
                    && Str(recv["k"]) == "local"
                    && Str(recv["name"]) is string recvName
                    && deferred.TryGetValue(recvName, out var recvLogical)
                    && TypeJson.Read(obj["ownerType"]) is TypeNode.Tv recvExpected
                    && recvExpected == recvLogical)
                    obj["recv"] = new JsonObject
                    {
                        ["k"] = "cast",
                        ["type"] = TypeJson.Write(recvLogical),
                        ["e"] = recv.DeepClone(),
                    };

                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RenarrowTypedUses(child, deferred);
                break;
            }
            case JsonArray arr:
                foreach (var child in arr.Where(v => v != null).ToList())
                    RenarrowTypedUses(child, deferred);
                break;
        }
    }

    static bool TryResolve(JsonObject call, DeclIndex index, out TypeNode logicalReturn)
    {
        logicalReturn = null;
        var kind = Str(call["k"]);
        if (kind is not ("callInstance" or "callStatic") || Str(call["method"]) is not string name) return false;

        TypeNode[] ownerArgs = Array.Empty<TypeNode>();
        IEnumerable<Decl> declarations;
        if (kind == "callInstance")
        {
            if (TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner
                || !index.ByOwner.TryGetValue(owner.Name, out var owned))
                return false;
            ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
            declarations = owned;
        }
        else
        {
            // A local top-level BIR call is ownerless.  Once an external/file-class owner is attributed it is no
            // longer a declaration from this compilation and must not be matched by bare name.
            if (call["owner"] != null || call["calleeOwner"] != null) return false;
            declarations = index.TopLevel;
        }

        var methodArgs = ReadTypes(call["typeArgs"]);
        var sig = ReadTypes(call["sig"]);
        var argc = (call["args"] as JsonArray)?.Count ?? 0;
        var matches = new List<(Decl Decl, TypeNode Return)>();
        foreach (var decl in declarations.Where(d => d.Name == name && d.Params.Length == argc))
        {
            var substitutedParams = decl.Params.Select(p => Substitute(p, ownerArgs, methodArgs)).ToArray();
            if (substitutedParams.Any(p => p == null)) continue;
            if (sig.Length > 0
                && (sig.Length != substitutedParams.Length || !sig.Zip(substitutedParams).All(x => x.First.Equals(x.Second))))
                continue;
            var ret = Substitute(decl.Return, ownerArgs, methodArgs);
            if (ret != null) matches.Add((decl, ret));
        }
        if (matches.Count != 1) return false;
        logicalReturn = matches[0].Return;
        return true;
    }

    static TypeNode[] ReadTypes(JsonNode node)
    {
        if (node is not JsonArray arr) return Array.Empty<TypeNode>();
        var result = arr.Select(TypeJson.Read).ToArray();
        return result.Any(t => t == null) ? Array.Empty<TypeNode>() : result;
    }

    static TypeNode Substitute(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn { Args: { } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(a => Substitute(a, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Substitute(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Substitute(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(Substitute(a.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(Substitute(b.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Substitute(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => Substitute(p, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : Substitute(fn.Recv, ownerArgs, methodArgs)),
        _ => type,
    };

    static void RetypeReturnedCasts(JsonNode node, TypeNode.Tv ret)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "return" && obj["value"] is JsonObject value
                    && Str(value["k"]) == "cast"
                    && TypeJson.Read(value["type"]) is TypeNode.Tv cast && cast == ret
                    && value["e"] is JsonObject source && IsNullableOrObjectSource(source))
                    value["type"] = TypeJson.Fqn("object");
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RetypeReturnedCasts(child, ret);
                break;
            case JsonArray arr:
                foreach (var child in arr.Where(v => v != null).ToList())
                    RetypeReturnedCasts(child, ret);
                break;
        }
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}
