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

    static bool IsObject(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "System.Object" or "object" or "kotlin.Any";

    static bool IsUnit(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "kotlin.Unit" or "void" or "System.Void";

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
