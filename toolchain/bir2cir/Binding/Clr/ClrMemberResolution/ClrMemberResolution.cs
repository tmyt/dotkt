using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DotKt.Bir;

// Native binding consumes the already-physical owner and carried declaration
// descriptors. ClrDeclarationMatching owns scalar declaration/type matching;
// this family applies the chosen bindings and authors exact MemberRefs.
// ilemit only links those stated identities. Applicability must not replace an
// explicitly selected descriptor.
// Physical binding borrows the caller's local-name set; reference-only source
// queries use the index's name view without a physical-local override.
static partial class ClrMemberResolution
{
    const string DeclarationIdentityBoundKey = "dotktDeclarationIdentityBound";

    // Proof demand and final MemberRef emission consume the same frontend-selected open descriptor. A lone
    // same-name/arity MethodDef without that exact descriptor is never a selected declaration.
    static MethodInfo SelectedGenericProofDeclaration(IReadOnlySet<string> bindingLocalTypes, TypeNode.Fqn owner, string name, int arity,
        bool instance, IReadOnlyList<TypeNode> parameters, ReferenceMetadataIndex refs,
        TypeNode[] parameterOwnerArguments = null)
    {
        var declaration = ResolveOwnerType(owner, refs: refs)
            ?? throw new InvalidOperationException("Native generic proof demand lacks its selected declaration owner");
        // This caller runs before the full BIR vocabulary walk. Lower the selected formal parameters in their
        // declaration role, not the call's value arguments, before sharing the physical descriptor matcher.
        var signature = parameters.Select(type => ClrDeclarationMatching.CanonicalNativeFormalType(refs, type)).ToList();
        var selected = ClrDeclarationMatching.SelectedGenericDeclaration(refs, bindingLocalTypes, declaration, name, arity, instance, signature, parameterOwnerArguments)
            ?? throw new InvalidOperationException($"Native generic proof demand '{owner.Name}.{name}' "
                + $"has no exact selected MethodDef for descriptor {string.Join(", ", parameters)}; candidates: "
                + string.Join("; ", declaration.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(method => IsPublicOrProtected(method) && method.Name == name)
                    .Select(method => method.ToString())));
        return selected;
    }

    // Resolve the exact open owner in the caller's compile-reference universe.
    internal static Type ResolveOwnerType(TypeNode.Fqn ownerFqn, ReferenceMetadataIndex refs)
    {
        if (refs == null) return null;
        // A generated nested companion's CIR token deliberately omits CLR generic-arity punctuation, including the
        // outer owner's backtick. Its validated [KotlinCompanion] association is the authority for the exact reflected
        // TypeDef spelling; use that association before the ordinary flat-name arity probe.
        if (refs.TryCompanionMetadataCarrier(ownerFqn.Name, out _))
            return refs.ResolveCompanionMetadataCarrier(ownerFqn.Name, ownerFqn.Args?.Length ?? 0);
        // A NESTED-generic reflection name already carries backtick arity + `+` separators (`Outer`1+Nested`, the
        // ConfigureAwait awaiter) — resolve it VERBATIM; BareOwnerFqn/StripGenericArity would truncate at the first
        // backtick and lose the nested type. (Its `args` instantiate the OUTER; a member whose sig has no outer type-var
        // — OnCompleted(Action) — matches on the open nested def regardless, so no MakeGenericType is needed.)
        if (ownerFqn.Name.Contains('`')) return refs.ResolveRefType(ownerFqn.Name, 0);
        return ClrDeclarationMatching.RefDef(refs, ownerFqn.Name, ownerFqn.Args?.Length ?? 0);
    }

    // DefaultArgSplice runs before NetInteropBinding, but it must read defaults from the exact MethodDef this pass will
    // later put in memberRef. Match in the selected declaration/projection frame; a second applicability search here
    // can materialize a sibling's value irreversibly after owner substitution makes their parameters coincide.
    internal static bool TryResolveExternalMethodForDefaults(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn,
        string name, int methodArity, bool isStatic, IReadOnlyList<TypeNode> callSignature,
        out MethodInfo declaration, string propertyKind = null)
    {
        declaration = null;
        if (refs == null || ownerFqn == null || name == null || callSignature == null) return false;
        var open = ResolveOwnerType(ownerFqn, refs: refs);
        if (open == null) return false;
        // Defaults belong to the same accessor MethodDef used by later interop
        // binding, not to the Kotlin operator spelling of an indexed property.
        if (propertyKind is "index-get" or "index-set")
            name = NetInteropBinding.DefaultIndexerAccessor(open, propertyKind == "index-set") ?? name;
        TypeNode Physical(TypeNode type, bool typeArg) => BirTypeLowering.CanonicalPhysicalSlotType(
            BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames, typeArg, nullableFrames: refs.NullableTypeFrames));
        var argNodes = NetInteropBinding.NormalizeSignature(callSignature, open)
            .Select(type => Physical(type, typeArg: false)).ToList();
        var ownerArgs = ownerFqn.Args?.Select(type => Physical(type, typeArg: true)).ToArray();
        var flags = BindingFlags.Public | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = Candidates(refs, open, name, argNodes, ownerArgs, flags)
            .Where(IsPublicOrProtected)
            .Where(method => method.IsGenericMethodDefinition
                ? method.GetGenericArguments().Length == methodArity
                : methodArity == 0)
            .ToList();
        declaration = ClrDeclarationMatching.TryPickDeclared(refs, bindingLocalTypes, candidates, argNodes, open)
            ?? throw new InvalidOperationException(
                $"bir2cir: default argument declaration '{ownerFqn.Name}.{name}' has no unique open signature");
        return true;
    }

    // The candidate set for `name`, PREFERRING the owner's OWN declared members over interface members
    // (C#'s "most-derived declaring type wins" — §12.8.10.2): reflection's GetMethods surfaces inherited CLASS
    // members, but not an interface slot implemented by a private explicit MethodImpl body. GetInterfaces is therefore
    // the fallback for class and interface owners alike. Adding those slots unconditionally would make
    // `IEnumerable<T>.GetEnumerator()` ambiguous with the inherited non-generic `IEnumerable.GetEnumerator()`
    // (resolvedMemberParams = [] cannot distinguish return-type-only overloads), so consult them only when no own member is
    // applicable. Static calls never bind an implemented-interface slot.
    static List<MethodInfo> Candidates(ReferenceMetadataIndex bindingReferences, Type open, string name, List<TypeNode> argNodes, TypeNode[] ownerArgs, BindingFlags flags)
    {
        var own = new List<MethodInfo>();
        try { own.AddRange(open.GetMethods(flags).Where(m => m.Name == name &&
            m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        if ((flags & BindingFlags.Instance) == 0 ||
            own.Any(m => ClrDeclarationMatching.Match(bindingReferences, m.GetParameters(), argNodes, ownerArgs) != ClrDeclarationMatching.MatchKind.No))
            return own;
        var withInterfaces = new List<MethodInfo>(own);
        foreach (var bi in ClrDeclarationMatching.SafeInterfaces(open))
            try { withInterfaces.AddRange(bi.GetMethods(flags).Where(m => m.Name == name &&
                m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        return withInterfaces;
    }
    internal static string DeclaringTypeIdentity(ReferenceMetadataIndex bindingReferences, MethodBase member)
    {
        var declaring = member?.DeclaringType
            ?? throw new InvalidOperationException($"bir2cir: resolved member '{member}' has no declaring type");
        declaring = ClrDeclarationMatching.SafeDef(ClrDeclarationMatching.AliasResolve(bindingReferences, declaring));
        return declaring.FullName ?? declaring.Name;
    }

    // dispatch (clrInstance): mirrors ilemit EmitInstanceCall (Bodies.cs). All facts are MLC-readable off the resolved
    // MethodInfo + the owner value-type-ness. constraintType is redundant (== the owner `type` slot), so NOT carried.
    // INVARIANT: IsVirtual/IsFinal are read off the REF.dll member while ilemit links the RT member (same-source builds
    // keep them identical); an rt-side `sealed`/virtuality change therefore requires a matching ref rebuild.
    //   super (non-virtual base slot, ref receiver) -> call ; non-virtual -> call ; virtual ref receiver -> callvirt ;
    //   virtual FINAL on a value type -> call ; virtual non-final inherited/overridden by a value type ->
    //   constrained.callvirt through the method's base-definition slot.
    static string Dispatch(MethodInfo mi, Type owner, bool superCall)
    {
        bool valueOwner; try { valueOwner = owner.IsValueType; } catch { valueOwner = false; }
        bool isVirtual; try { isVirtual = mi.IsVirtual; } catch { isVirtual = false; }
        bool isFinal; try { isFinal = mi.IsFinal; } catch { isFinal = false; }
        if (superCall && !valueOwner) return "call";
        if (!isVirtual) return "call";
        if (!valueOwner) return "callvirt";
        if (isFinal) return "call";
        return "constrained";
    }

    // MetadataLoadContext does not reliably connect an override in a reference assembly to GetBaseDefinition().
    // Recover the root virtual slot from the value type's base chain using the CLR method-signature identity. A
    // constrained callvirt must name that slot (Object.ToString, for example), while the constraint selects the
    // concrete value-type implementation without boxing.
    static MethodInfo ConstrainedSlot(MethodInfo method, Type owner)
    {
        MethodInfo result = method;
        try
        {
            var reflected = method.GetBaseDefinition();
            if (reflected != null) result = reflected;
        }
        catch { }
        try
        {
            var wanted = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var genericArity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
            for (var type = owner.BaseType; type != null; type = type.BaseType)
            {
                const BindingFlags declaredInstance = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var matches = type.GetMethods(declaredInstance).Where(candidate =>
                    candidate.Name == method.Name && candidate.IsVirtual &&
                    (candidate.IsGenericMethod ? candidate.GetGenericArguments().Length : 0) == genericArity &&
                    candidate.GetParameters().Select(p => p.ParameterType).SequenceEqual(wanted)).ToList();
                if (matches.Count == 1) result = matches[0];
            }
        }
        catch { }
        return result;
    }

    // ---- resolvedMemberParams (winning member params -> lowered TypeNode array) ---------------------------


    // THE FOREIGN DECLARED RETURN, stamped beside `resolvedMemberParams` for the crossing refusal to read (#86).
    //
    // A node's own `ret` is the CALLER's Kotlin view of the result and is erased as a Kotlin slot — correctly, since
    // it is what the value's Kotlin type is. What no key stated is what the MEMBER declares, so a C# `List<int?>
    // Make()` was seen as returning Kotlin's `List<object>`, was not refused, and left a `List<Nullable<int32>>` on a
    // stack typed as the unrelated Kotlin form. `resolvedMemberParams` is that channel for parameters; this is its return twin.
    //
    // A pass-to-pass fact, NOT a CIR key: ForeignNullableGenericCrossing reads it and strips it, so nothing reaches
    // the emitter that the emitter does not consume.
    internal const string ResolvedMemberReturnKey = "resolvedMemberReturn";

    // ---- diagnostics ---------------------------------------------------------------------------

    static string DescArgs(List<TypeNode> a) => string.Join(",", a.Select(x => TypeNode.ToJson(x)));

    // THE CHOKEPOINT: a node this pass resolved against a .NET member CARRIES that member's declared return.
    //
    // Two omissions of exactly this shape shipped before it existed — a generic method, which never entered the
    // switch, and a genuine public field, which returned early — and each one silently removed a whole family from
    // the crossing refusal. Review caught them; the build did not. It does now.
    //
    // WHAT IT ASSERTS, precisely, so nobody reads more into it than it holds: every node carrying a resolved
    // parameter vector (`resolvedMemberParams`), and every node whose KIND only this pass produces, also carries `resolvedMemberReturn`.
    // It cannot speak for a `field`/`setField` node, whose kind Kotlin uses too and whose owner may be local — those
    // stamp in their own branches, and the assertion below covers them only once they carry a `resolvedMemberParams`.
    static readonly string[] ResolvedOnlyKinds =
    {
        "newClr", "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance", "newBoundClrDelegate",
        "newClrStaticDelegate",
        "clrPropGet", "clrPropSet",
    };

    static void RequireForeignDeclarationStamp(string kind, bool parameterStamp, bool memberReference, bool returnStamp, string file)
    {
        // An emitted local event is not a foreign declaration. Only its
        // referenced form, carrying the exact memberRef, requires this stamp.
        var resolvedEvent = kind is "clrEventAdd" or "clrEventRemove" && memberReference;
        var resolved = parameterStamp || resolvedEvent || (kind != null && Array.IndexOf(ResolvedOnlyKinds, kind) >= 0);
        if (resolved && !returnStamp)
            throw new InvalidOperationException(
                $"bir2cir: {file}: a '{kind ?? "?"}' node resolved against a .NET member carries no declared "
                + "return (resolvedMemberReturn). Every site that establishes a foreign declaration must stamp one — "
                + "the crossing refusal reads it, and an unstamped node silently leaves its family unchecked.");
    }
}
