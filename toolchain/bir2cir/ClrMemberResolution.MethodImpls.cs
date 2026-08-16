using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A clrBaseImpls entry is an ECMA-335 MethodImpl declaration operand. Local declarations can be linked directly to
// their MethodBuilder, but an external declaration must cross CIR as the same complete member identity as a call.
// KotlinOverrideSlotBridge authors the semantic wiring and its constructed slot descriptor; this final physical pass
// resolves that descriptor against the compile-reference universe and replaces selection with one scalar reference.
static partial class ClrMemberResolution
{
    static void ResolveExternalBaseMethodImpls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["clrBaseImpls"] is JsonArray implementations)
                foreach (var item in implementations.OfType<JsonObject>()) ResolveExternalBaseMethodImpl(item);
            foreach (var value in obj.Select(pair => pair.Value).ToList())
                if (value != null) ResolveExternalBaseMethodImpls(value);
        }
        else if (node is JsonArray array)
            foreach (var item in array.ToList()) if (item != null) ResolveExternalBaseMethodImpls(item);
    }

    static void ResolveExternalBaseMethodImpl(JsonObject descriptor)
    {
        if (descriptor["memberRef"] is JsonObject) return;
        if (TypeJson.Read(descriptor["owner"]) is not TypeNode.Fqn ownerSpec
            || _localTypes.Contains(ownerSpec.Name)) return;
        var name = (descriptor["member"] as JsonValue)?.TryGetValue<string>(out var member) == true
            ? member : null;
        var arity = (descriptor["arity"] as JsonValue)?.TryGetValue<int>(out var genericArity) == true
            ? genericArity : -1;
        var rawReturn = TypeJson.Read(descriptor["ret"]);
        var ret = rawReturn == null ? null : BirTypeLowering.CanonicalPhysicalSlotType(rawReturn);
        if (name == null || arity < 0 || ret == null || descriptor["params"] is not JsonArray parameterNodes)
            throw new InvalidOperationException(
                $"bir2cir: external base MethodImpl for '{ownerSpec.Name}' is missing its exact slot descriptor");
        var parameters = parameterNodes.Select((node, index) =>
                BirTypeLowering.CanonicalPhysicalSlotType(TypeJson.Read(node)
                    ?? throw new InvalidOperationException(
                        $"bir2cir: external base MethodImpl '{ownerSpec.Name}.{name}' parameter #{index} is malformed")))
            .ToList();
        var open = ResolveOwnerType(ownerSpec)
            ?? throw new InvalidOperationException(
                $"bir2cir: external base MethodImpl owner '{ownerSpec.Name}' does not resolve in the compile-reference universe");
        // The bridge descriptor names the constructed supertype through which Kotlin reached the slot.  The actual
        // CLR declaration may live on any of that type's base classes.  Reflection's inherited method set preserves
        // that distinction, and MemberRefJson projects the winner's actual declaring type through `open`; restricting
        // this search to DeclaredOnly would reject a perfectly exact descriptor solely because it arrived through an
        // intermediate base.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var candidates = open.GetMethods(flags)
            .Where(method => method.Name == name && method.IsVirtual
                && method.GetGenericArguments().Length == arity
                && method.GetParameters().Length == parameters.Count
                && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
            .ToList();
        // GetMethods returns declarations in their OPEN declaring-type frames.  Specialize each through the
        // constructed owner edge before comparing it with the already-constructed descriptor: a Base<T,U> that
        // declares M(T) and M(U) must become distinguishable when reached through Base<Int32,String>.
        var matches = MostDerived(candidates.Where(candidate =>
            ExternalMethodImplSlotMatches(candidate, open, ownerSpec.Args, parameters, ret)).ToList());
        var description = $"external base MethodImpl={ownerSpec.Name}.{name}({DescArgs(parameters)}):{ret}";
        if (matches.Count == 0) throw NoMatch(description, candidates);
        if (matches.Count > 1) throw Malformed(description, matches);
        var winner = matches[0];
        descriptor["memberRef"] = MemberRefJson(
            winner, MemberRefNode.Kinds.Method, open, ownerSpec.Args ?? Array.Empty<TypeNode>());
    }

    static bool ExternalMethodImplSlotMatches(MethodInfo candidate, Type open, TypeNode[] ownerArgs,
        IReadOnlyList<TypeNode> parameters, TypeNode ret)
    {
        var declarer = DeclaringTypeRef(candidate, open, ownerArgs ?? Array.Empty<TypeNode>()) as TypeNode.Fqn;
        var declaringArgs = declarer?.Args ?? Array.Empty<TypeNode>();
        var candidateParameters = RefParamsOf(candidate)
            .Select(type => MethodImplComparisonType(
                SupertypeGraph.SubstOwnerTvs(type, declaringArgs))).ToArray();
        var candidateReturn = MethodImplComparisonType(
            SupertypeGraph.SubstOwnerTvs(RefReturnOf(candidate), declaringArgs));
        var descriptorParameters = parameters.Select(MethodImplComparisonType).ToArray();
        var descriptorReturn = MethodImplComparisonType(ret);
        return candidateParameters.Length == parameters.Count
            && candidateParameters.Where((type, index) => type != descriptorParameters[index]).Any() == false
            && candidateReturn == descriptorReturn;
    }

    // A memberRef keeps the target's exact metadata spelling (`List`1`, nested `+`) but the MethodImpl descriptor is
    // ordinary CIR type vocabulary (arity-free, dotted).  Selection compares those two vocabularies structurally;
    // only the comparison drops metadata punctuation.  MemberRefJson below still serializes the exact reflected name.
    static TypeNode MethodImplComparisonType(TypeNode type)
    {
        type = BirTypeLowering.CanonicalPhysicalSlotType(type);
        return type switch
        {
            TypeNode.Fqn f => new TypeNode.Fqn(
                ReferenceMetadataIndex.BareOwnerFqn(f.Name).Replace('+', '.'),
                f.Args?.Select(MethodImplComparisonType).ToArray()),
            TypeNode.Array array => new TypeNode.Array(
                MethodImplComparisonType(array.Elem), array.Rank, array.SzArray),
            TypeNode.Nullable nullable => new TypeNode.Nullable(MethodImplComparisonType(nullable.Of)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(MethodImplComparisonType(oblivious.Of)),
            TypeNode.ByRef byRef => new TypeNode.ByRef(MethodImplComparisonType(byRef.Of)),
            TypeNode.Ptr pointer => new TypeNode.Ptr(MethodImplComparisonType(pointer.Of)),
            TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
                MethodImplComparisonType(modifier.M), MethodImplComparisonType(modifier.Of)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
                MethodImplComparisonType(fn.Ret), fn.Params.Select(MethodImplComparisonType).ToArray(),
                fn.Recv == null ? null : MethodImplComparisonType(fn.Recv), fn.Clr,
                fn.Ctx?.Select(MethodImplComparisonType).ToArray()),
            _ => type,
        };
    }
}
