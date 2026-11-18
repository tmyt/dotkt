using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A clrBaseImpls entry is an ECMA-335 MethodImpl declaration operand. Local declarations can be linked directly to
// their MethodBuilder, but an external declaration must cross CIR as the same complete member identity as a call.
// KotlinOverrideSlotBridge authors the semantic wiring and its constructed slot descriptor; this final physical pass
// resolves that descriptor against the compile-reference universe and replaces selection with one scalar reference.
static partial class ClrMemberResolution
{
    static void ResolveExternalBaseMethodImpls(ReferenceMetadataIndex bindingReferences, BirGraphValue node, IReadOnlySet<string> localTypes)
    {
        if (node == null || node is IBirExecutablePayload) return;
        if (node is BirFunctionDeclaration method)
            foreach (var descriptor in method.ClrBaseImpls.OrEmpty())
                ResolveExternalBaseMethodImpl(bindingReferences, descriptor, localTypes);
        node.VisitChildren(child => ResolveExternalBaseMethodImpls(bindingReferences, child, localTypes));
    }

    static void ResolveExternalBaseMethodImpl(ReferenceMetadataIndex bindingReferences, BirMethodImpl descriptor, IReadOnlySet<string> localTypes)
    {
        if (descriptor.MemberRef.HasValue) return;
        if (descriptor.Owner.OrDefault() is not TypeNode.Fqn ownerSpec
            || localTypes.Contains(ownerSpec.Name)) return;
        var name = descriptor.Member.OrDefault();
        // A metadata-only Kotlin declaration can retain its source MethodDef
        // name while the runtime declaration adopts its selected CLR override
        // allocation. The slot pass carries both identities explicitly.
        var referenceName = descriptor.ReferenceMember.OrDefault() ?? name;
        var arity = descriptor.Arity.HasValue ? descriptor.Arity.Value : -1;
        var rawReturn = descriptor.Ret.OrDefault();
        var ret = rawReturn == null ? null : BirTypeLowering.CanonicalPhysicalSlotType(rawReturn);
        if (name == null || arity < 0 || ret == null || !descriptor.Params.HasValue)
            throw new InvalidOperationException(
                $"bir2cir: external base MethodImpl for '{ownerSpec.Name}' is missing its exact slot descriptor");
        var parameters = descriptor.Params.Value.Select((type, index) =>
                BirTypeLowering.CanonicalPhysicalSlotType(type
                    ?? throw new InvalidOperationException(
                        $"bir2cir: external base MethodImpl '{ownerSpec.Name}.{name}' parameter #{index} is malformed")))
            .ToList();
        var open = ResolveOwnerType(ownerSpec, refs: bindingReferences)
            ?? throw new InvalidOperationException(
                $"bir2cir: external base MethodImpl owner '{ownerSpec.Name}' does not resolve in the compile-reference universe");
        // The bridge descriptor names the constructed supertype through which Kotlin reached the slot.  The actual
        // CLR declaration may live on any of that type's base classes.  Reflection's inherited method set preserves
        // that distinction, and MemberRefOf projects the winner's actual declaring type through `open`; restricting
        // this search to DeclaredOnly would reject a perfectly exact descriptor solely because it arrived through an
        // intermediate base.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var candidates = open.GetMethods(flags)
            .Where(method => method.Name == referenceName && method.IsVirtual
                && method.GetGenericArguments().Length == arity
                && method.GetParameters().Length == parameters.Count
                && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
            .ToList();
        // GetMethods returns declarations in their OPEN declaring-type frames.  Specialize each through the
        // constructed owner edge before comparing it with the already-constructed descriptor: a Base<T,U> that
        // declares M(T) and M(U) must become distinguishable when reached through Base<Int32,String>.
        var matches = ClrDeclarationMatching.MostDerived(candidates.Where(candidate =>
            ExternalMethodImplSlotMatches(localTypes, bindingReferences, candidate, open, ownerSpec.Args, parameters, ret)).ToList());
        var description = $"external base MethodImpl={ownerSpec.Name}.{name}({DescArgs(parameters)}):{ret}";
        if (matches.Count == 0) throw ClrDeclarationMatching.NoMatch(description, candidates);
        if (matches.Count > 1) throw ClrDeclarationMatching.Malformed(description, matches);
        var winner = matches[0];
        var reference = ClrMemberReferences.MemberRefOf(bindingReferences, localTypes, winner, MemberRefNode.Kinds.Method, open,
            ownerSpec.Args ?? Array.Empty<TypeNode>());
        descriptor.MemberRef = BirMemberReference.FromPhysical(reference with { Name = name });
        descriptor.ReferenceMember = default;
    }

    static bool ExternalMethodImplSlotMatches(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, MethodInfo candidate, Type open, TypeNode[] ownerArgs,
        IReadOnlyList<TypeNode> parameters, TypeNode ret)
    {
        // Reflection may express an inherited member in the receiver's frame.
        // Substitute the declaration's own signature through the declaring edge once.
        candidate = (MethodInfo)ClrMemberReferences.OpenDeclarationOf(candidate);
        var declarer = ClrMemberReferences.DeclaringTypeRef(bindingReferences, bindingLocalTypes, candidate, open, ownerArgs ?? Array.Empty<TypeNode>()) as TypeNode.Fqn;
        var declaringArgs = declarer?.Args ?? Array.Empty<TypeNode>();
        var candidateParameters = ClrMemberReferences.RefParamsOf(bindingReferences, bindingLocalTypes, candidate)
            .Select(type => MethodImplComparisonType(
                SupertypeGraph.SubstOwnerTvs(type, declaringArgs))).ToArray();
        var candidateReturn = MethodImplComparisonType(
            SupertypeGraph.SubstOwnerTvs(ClrMemberReferences.RefReturnOf(bindingReferences, bindingLocalTypes, candidate), declaringArgs));
        var descriptorParameters = parameters.Select(MethodImplComparisonType).ToArray();
        var descriptorReturn = MethodImplComparisonType(ret);
        return candidateParameters.Length == parameters.Count
            && candidateParameters.Where((type, index) => type != descriptorParameters[index]).Any() == false
            && candidateReturn == descriptorReturn;
    }

    // A memberRef keeps the target's exact metadata spelling (`List`1`, nested `+`) but the MethodImpl descriptor is
    // ordinary CIR type vocabulary (arity-free, dotted).  Selection compares those two vocabularies structurally;
    // MemberRefOf retains the exact reflected name. Comparing two physical hierarchy edges instead
    // resolves their classifiers through the reference index, since flattening can merge distinct TypeDefs.
    static TypeNode MethodImplComparisonType(TypeNode type) => MethodImplComparisonType(type, null);

    internal static TypeNode MethodImplComparisonType(TypeNode type, ReferenceMetadataIndex ownerIndex)
    {
        TypeNode Compare(TypeNode child) => MethodImplComparisonType(child, ownerIndex);
        type = BirTypeLowering.CanonicalPhysicalSlotType(type);
        return type switch
        {
            TypeNode.Fqn f => new TypeNode.Fqn(ownerIndex?.HierarchyComparisonName(f.Name, f.Args?.Length ?? 0)
                ?? ReferenceMetadataIndex.BareOwnerFqn(f.Name).Replace('+', '.'), f.Args?.Select(Compare).ToArray()),
            TypeNode.Array array => new TypeNode.Array(
                Compare(array.Elem), array.Rank, array.SzArray),
            // Compare the document's physical nullable wrapper with metadata's constructed Nullable`1<V>.
            TypeNode.Nullable nullable => new TypeNode.Fqn("System.Nullable",
                new[] { Compare(nullable.Of) }),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Compare(oblivious.Of)),
            TypeNode.ByRef byRef => new TypeNode.ByRef(Compare(byRef.Of)),
            TypeNode.Ptr pointer => new TypeNode.Ptr(Compare(pointer.Of)),
            TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
                Compare(modifier.M), Compare(modifier.Of)),
            TypeNode.FnPtr pointer => pointer with {
                Ret = Compare(pointer.Ret),
                Params = pointer.Params.Select(Compare).ToArray(),
            },
            TypeNode.Fn { Clr: not null } fn => Compare(BirTypeLowering.DelegateFqnOf(fn)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
                Compare(fn.Ret), fn.Params.Select(Compare).ToArray(),
                fn.Recv == null ? null : Compare(fn.Recv), fn.Clr,
                fn.Ctx?.Select(Compare).ToArray()),
            _ => type,
        };
    }
}
