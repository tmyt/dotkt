using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

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
// exact frontend declaration identity and owner matching at calls; no library or declaration-name special cases.
static partial class UncheckedGenericCastReturnErasure
{
    public sealed class DeclIndex
    {
        internal readonly Dictionary<string, Decl> ByIdentity = new(StringComparer.Ordinal);
    }

    internal sealed class Decl
    {
        public string Owner;
        public bool TopLevel;
        public bool Static;
        public TypeNode.Tv Return;
        public JsonObject Method;
        internal BirFunctionDeclaration SourceMethod;
    }

    static bool IsNullableOrObjectSource(TypeNode type) => type switch
        {
            TypeNode.Nullable => true,
            TypeNode.Oblivious => true,
            TypeNode.Fqn f => f.Name is "kotlin.Any" or "object" or "System.Object",
            _ => false,
        };

    static bool IsUncheckedReturnCast(string kind, bool reifiedOperand, TypeNode castType,
        TypeNode.Tv expected, TypeNode sourceType) => kind == "cast"
        && !KotlinTypeWitness.NeedsWitness(kind, reifiedOperand)
        && castType is TypeNode.Tv variable && variable == expected
        && IsNullableOrObjectSource(sourceType);

    // An inherited call initially names its receiver rather than the declaration owner. Only after owner binding
    // can the selected object-returning declaration be closed in its exact frame. Preserve its Kotlin result as an
    // explicit use projection before BirTypeLowering consumes sty; PhysicalValueCoercion materializes the cast.
    public static void ApplyAfterInheritedOwnerBinding(JsonNode root, DeclIndex index)
    {
        ApplyCallsOnly(root, index);
    }

    static bool IsObject(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "System.Object" or "object" or "kotlin.Any";

    static bool IsUnit(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "kotlin.Unit" or "void" or "System.Void";

    static void ApplyCallsOnly(JsonNode node, DeclIndex index)
    {
        if (node is not JsonObject obj) return;
        RewriteMethods(obj["methods"], index);
        RewriteMethods(obj["ctors"], index);
        if (obj["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
            {
                var deferred = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
                RewriteDeferredLocals(field["init"], index, deferred);
                if (deferred.Count > 0) RenarrowTypedUses(field["init"], deferred);
            }
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
            var regions = new[] { method["preStmts"], method["thisArgs"] ?? method["baseArgs"], method["body"] };
            foreach (var region in regions) RewriteDeferredLocals(region, index, deferred);
            if (deferred.Count > 0)
                foreach (var region in regions) RenarrowTypedUses(region, deferred);
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
                if (obj["ret"] == null && TypeJson.Read(obj["sty"]) is TypeNode expected
                    && !IsObject(expected) && !IsUnit(expected)
                    && TryResolve(obj, index, out var projectedReturn) && projectedReturn.Equals(expected))
                    obj["ret"] = obj["sty"].DeepClone();
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
        => TryResolve(Str(call["k"]), Str(call[DeclarationIdentityBinding.Key]), TypeJson.Read(call["ownerType"]),
            new[] { "owner", "calleeOwner", "ownerType" }.Where(key => call[key] != null)
                .Select(key => TypeJson.Read(call[key])).ToArray(), ReadTypes(call["typeArgs"]), index, out logicalReturn);

    static bool TryResolve(string kind, string identity, TypeNode instanceOwner, TypeNode[] staticOwners,
        TypeNode[] methodArguments, DeclIndex index, out TypeNode logicalReturn)
    {
        logicalReturn = null;
        if (kind is not ("callInstance" or "callStatic")
            || identity == null
            || !index.ByIdentity.TryGetValue(identity, out var decl)
            || decl.Static != (kind == "callStatic"))
            return false;

        TypeNode[] ownerArgs = Array.Empty<TypeNode>();
        if (kind == "callInstance")
        {
            if (instanceOwner is not TypeNode.Fqn owner
                || owner.Name != decl.Owner)
                return false;
            ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
        }
        else
        {
            // Source top-level calls carry their file facade in calleeOwner. Calls that remain ownerless after
            // earlier lowerings still select a LOCAL declaration by frontend identity, never by a bare method name
            // or substituted signature. An attributed owner is an exact fact, not evidence of an external call.
            var hasOwner = false;
            foreach (var selectedOwner in staticOwners)
                {
                    hasOwner = true;
                    if (selectedOwner is not TypeNode.Fqn owner || owner.Name != decl.Owner)
                        return false;
                    ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
                }
            if (!hasOwner && !decl.TopLevel) return false;
        }

        logicalReturn = Substitute(decl.Return, ownerArgs, methodArguments);
        return logicalReturn != null;
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

    static T ReturnedValue<T>(string statementKind, T value, Func<T, string> kind, Func<T, TypeNode> type,
        Func<T, T> castOperand, Func<T, T> blockResult) where T : class
    {
        if (statementKind != "return") return null;
        // Return-slot coercion can already have boxed the value. Preserve that
        // edge and its guards while locating the cast supplying the return.
        while (value != null)
        {
            if (kind(value) == "cast" && IsObject(type(value))) value = castOperand(value);
            else if (kind(value) == "valueBlock") value = blockResult(value);
            else break;
        }
        return value;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

}
