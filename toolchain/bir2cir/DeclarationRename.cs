using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// CLR OVERRIDE ALLOCATION. kotc tags each emitted declaration/call with a pure-Kotlin `overrides` marker (the
// transitive override closure). This pass resolves the external slot from reference metadata. Ordinary functions may
// adopt that physical name; Kotlin property accessors retain their dedicated physical name and record the external
// base slot separately for MethodImpl wiring. The `overrides` marker is stripped later by BirTypeLowering.
static partial class DeclarationRename
{
    // bir2cir-internal hand-off from semantic declaration identity to physical allocation. Ordinary methods may
    // adopt an external CLR name in this pass, but later representation passes still have to consume frontend facts
    // expressed in Kotlin identity. Preserve that identity explicitly until BirTypeLowering strips the carrier;
    // never recover it from the physical spelling or an override hierarchy.
    internal const string SourceMemberKey = "kotlinSourceMember";

    static bool HasOverridableAccessor(Type owner, string propertyName, string overrideKind)
    {
        try
        {
            var p = owner.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var accessor = overrideKind == "setter" ? p?.GetSetMethod(true) : p?.GetGetMethod(true);
            return accessor?.IsVirtual == true && !accessor.IsFinal;
        }
        catch { return false; }
    }

    // The first override entry whose complete declaration identity resolves to a CLR slot. Property accessors use the
    // referenced Property/MethodSemantics association; ordinary methods use source name + method generic arity + the
    // complete parameter vector in the override owner's constructed frame.
    // null = no CLR-bound member in the closure (leave the kotc name).
    internal static string ResolveSlot(JsonObject declaration, JsonArray ovs, ReferenceMetadataIndex refs,
        NullableRepresentationFrame ownerFrame = null)
    {
        var overrides = ovs.OfType<JsonObject>().Select(marker =>
            (Owner: TypeJson.Read(marker["owner"]) as TypeNode.Fqn,
                Member: (marker["member"] as JsonValue)?.GetValue<string>(),
                Kind: (marker["kind"] as JsonValue)?.GetValue<string>(),
                Arity: (marker["arity"] as JsonValue)?.GetValue<int>() ?? 0))
            .Where(marker => marker.Owner != null && marker.Member != null)
            .Select(marker => new SlotOverride(marker.Owner, marker.Member, marker.Kind, marker.Arity)).ToArray();
        if (!TryCallableSignature(declaration, out var signature, out var methodArity)) signature = null;
        TypeNode[] binding = null;
        if (signature != null && declaration["params"] is JsonArray parameters)
        {
            var methodFrame = NullableRepresentationTypes.DeclarationMethodFrame(declaration);
            binding = signature.Select((type, index) => NullableRepresentationTypes.BindingScalar(type,
                parameters[index]?["nullableGeneric"], ownerFrame, methodFrame)).ToArray();
        }
        return ResolveSlot(overrides, refs, new CallableSlot(signature, binding, methodArity,
            declaration["params"] is JsonArray, (declaration["k"] as JsonValue)?.GetValue<string>() == "callInstance",
            (declaration[DeclarationIdentityBinding.Key] as JsonValue)?.GetValue<string>()));
    }

    // ResolveSlot serves both a declaration and its already-selected call nodes. They carry the same declaration
    // identity in different structural slots: declarations use params/typeParams, calls use sig/typeArgs. Keep that
    // vocabulary distinction explicit rather than recovering either vector from the override marker's coarse arity.
    static bool TryCallableSignature(JsonObject node, out TypeNode[] signature, out int methodArity)
    {
        signature = null;
        methodArity = 0;
        if (node["params"] is JsonArray parameters)
        {
            signature = parameters.OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
            methodArity = (node["typeParams"] as JsonArray)?.Count ?? 0;
            return signature.Length == parameters.Count && signature.All(type => type != null);
        }
        if (node["sig"] is JsonArray callSignature)
        {
            signature = callSignature.Select(TypeJson.Read).ToArray();
            methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
            return signature.All(type => type != null);
        }
        return false;
    }

    static bool TryExactPropertySlot(TypeNode[] signature, int methodArity, ReferenceMetadataIndex refs,
        TypeNode.Fqn owner, string member, string overrideKind,
        out string physicalOwner, out string physicalProperty, out string physicalMethod, out TypeNode slotReturn)
    {
        physicalOwner = physicalProperty = physicalMethod = null;
        slotReturn = null;
        if (overrideKind is not ("getter" or "setter")) return false;
        if (!refs.TryExternalPropertyAccessor(owner.Name, member,
            overrideKind == "setter" ? "set" : "get", signature.Length, methodArity, signature,
            owner.Args ?? Array.Empty<TypeNode>(), out physicalOwner, out physicalProperty, out physicalMethod)
            || !refs.TryNullableGenericPropertySlot(owner.Name, member,
                overrideKind == "setter" ? "set" : "get", isStatic: false, signature.Length, methodArity,
                signature, owner.Args ?? Array.Empty<TypeNode>(), out var declaredReturn, out _, out var refused,
                includeUnchanged: true)
            || declaredReturn == null || refused?.Any(value => value) == true)
            return false;
        slotReturn = SupertypeGraph.SubstOwnerTvs(declaredReturn, owner.Args ?? Array.Empty<TypeNode>());
        return true;
    }
}
