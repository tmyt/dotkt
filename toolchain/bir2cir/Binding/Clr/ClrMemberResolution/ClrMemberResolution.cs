using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Native binding consumes the already-physical owner and carried declaration
// descriptors. The owning typed family renderers share these reflection policies
// and author exact MemberRefs; ilemit only links those stated identities.
// Matching uses an open declaration frame, then projects the selected declaring
// owner without manufacturing closed reflection types for local/type-variable
// arguments. Applicability must not replace an explicitly selected descriptor.
static partial class ClrMemberResolution
{
    const string DeclarationIdentityBoundKey = "dotktDeclarationIdentityBound";
    static ReferenceMetadataIndex _refs;
    static IReadOnlyDictionary<string, string> _localCarriers;
    static IReadOnlySet<string> _localTypes = new HashSet<string>();
    static IReadOnlySet<string> _externalCanonicalTypes = new HashSet<string>();
    static IReadOnlySet<string> _localDeclarationIds = new HashSet<string>();

    static ConstructorInfo PickDeclaredConstructor(IEnumerable<ConstructorInfo> constructors,
        IReadOnlyList<TypeNode> declaration, string context)
    {
        // Both `new` and base delegation carry the selected declaration's open shape.
        // Compare that shape, not argument applicability or a nominal-head approximation.
        var exact = constructors.Where(constructor =>
        {
            var parameters = constructor.GetParameters();
            return parameters.Length == declaration.Count && parameters.Select((parameter, index) =>
                DeclaredConstructorSlotMatches(declaration[index],
                    _refs.FunctionSignatureDiscriminator(parameter) ?? parameter.ParameterType)).All(match => match);
        }).ToList();
        return exact.Count == 1 ? exact[0] : throw new InvalidOperationException(
            $"bir2cir: {context} resolves to {exact.Count} constructor declarations, expected exactly one");
    }

    static bool DeclaredConstructorSlotMatches(TypeNode selected, Type parameter)
    {
        selected = BirTypeLowering.CanonicalPhysicalSlotType(selected);
        if (selected is TypeNode.Oblivious oblivious)
            return DeclaredConstructorSlotMatches(oblivious.Of, parameter);
        parameter = AliasResolve(parameter);
        if (parameter.IsGenericParameter)
            return selected is TypeNode.Tv tv && tv.I == parameter.GenericParameterPosition
                && tv.Scope == (parameter.DeclaringMethod == null ? "type" : "method");
        if (parameter.IsByRef)
            return selected is TypeNode.ByRef byref && DeclaredConstructorSlotMatches(byref.Of, parameter.GetElementType());
        if (parameter.IsPointer)
            return selected is TypeNode.Ptr pointer && DeclaredConstructorSlotMatches(pointer.Of, parameter.GetElementType());
        if (parameter.IsArray)
            return selected is TypeNode.Array array && array.Rank == SafeArrayRank(parameter)
                && array.SzArray == parameter.IsSZArray
                && DeclaredConstructorSlotMatches(array.Elem, parameter.GetElementType());
        if (selected is TypeNode.Nullable nullable)
            return parameter.IsGenericType && SafeDef(parameter) == NullableDef()
                && DeclaredConstructorSlotMatches(nullable.Of, parameter.GetGenericArguments()[0]);
        if (selected is TypeNode.Fn function)
        {
            if (!IsDelegateType(parameter) || function.Clr != DelegateFamily(parameter)) return false;
            var invoke = parameter.GetMethod("Invoke");
            var delegateParameters = function.DelegateParams;
            return invoke != null && delegateParameters.Length == invoke.GetParameters().Length
                && delegateParameters.Select((p, i) => DeclaredConstructorSlotMatches(p, invoke.GetParameters()[i].ParameterType)).All(match => match)
                && DeclaredConstructorSlotMatches(function.Ret, invoke.ReturnType);
        }
        if (selected is not TypeNode.Fqn named) return false;
        var arguments = named.Args ?? Array.Empty<TypeNode>();
        var definition = RefDef(named.Name, arguments.Length);
        if (definition == null || SafeDef(definition) != SafeDef(parameter)) return false;
        var parameters = parameter.IsGenericType ? parameter.GetGenericArguments() : Type.EmptyTypes;
        return arguments.Length == parameters.Length
            && arguments.Select((arg, i) => DeclaredConstructorSlotMatches(arg, parameters[i])).All(match => match);
    }

    // Proof demand and final MemberRef emission consume the same frontend-selected open descriptor. A lone
    // same-name/arity MethodDef without that exact descriptor is never a selected declaration.
    static MethodInfo SelectedGenericProofDeclaration(TypeNode.Fqn owner, string name, int arity,
        bool instance, IReadOnlyList<TypeNode> parameters, ReferenceMetadataIndex refs,
        TypeNode[] parameterOwnerArguments = null)
    {
        _refs = refs;
        var declaration = ResolveOwnerType(owner)
            ?? throw new InvalidOperationException("Native generic proof demand lacks its selected declaration owner");
        // This caller runs before the full BIR vocabulary walk. Lower the selected formal parameters in their
        // declaration role, not the call's value arguments, before sharing the physical descriptor matcher.
        var signature = parameters.Select(CanonicalNativeFormalType).ToList();
        var selected = SelectedGenericDeclaration(declaration, name, arity, instance, signature, parameterOwnerArguments)
            ?? throw new InvalidOperationException($"Native generic proof demand '{owner.Name}.{name}' "
                + $"has no exact selected MethodDef for descriptor {string.Join(", ", parameters)}; candidates: "
                + string.Join("; ", declaration.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(method => method.Name == name)
                    .Select(method => method.ToString())));
        return selected;
    }

    static MethodInfo SelectedGenericDeclaration(Type owner, string name, int arity, bool instance,
        List<TypeNode> signature, TypeNode[] parameterOwnerArguments = null)
    {
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        List<MethodInfo> Candidates(Type declaration) => declaration.GetMethods(flags)
            .Where(method => method.Name == name && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == arity
                && method.GetParameters().Length == signature.Count).ToList();
        return TryPickDeclared(Candidates(owner), signature, owner, parameterOwnerArguments)
            ?? (!instance ? null : TryPickDeclared(SafeInterfaces(owner).SelectMany(Candidates)
                .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList(),
                signature, owner, parameterOwnerArguments));
    }

    static TypeNode CanonicalNativeFormalType(TypeNode type) => BirTypeLowering.CanonicalPhysicalSlotType(
        BirTypeLowering.LowerPhysicalType(type, _refs.Aliases, _refs.IsValueType, _refs.PhysicalTypeNames,
            typeArg: false, nullableFrames: _refs.NullableTypeFrames));

    static MethodInfo TryPickDeclared(List<MethodInfo> candidates, List<TypeNode> signature, Type declarationOwner,
        TypeNode[] parameterOwnerArguments = null)
    {
        // The carried owner names the frontend-selected declaration/projection frame. Its own declaration is not
        // an inherited same-parameter member with a different return (Task<T>.GetAwaiter vs Task.GetAwaiter).
        // A projected completion on a class instead describes its constructed public interface surface: retain
        // that reflected frame rather than reopening I<string>.M(string) to the unrelated open I<T>.M(T) vector.
        // Call descriptors are open declaration vectors. Override descriptors instead belong to the implementer's
        // frame; substitute the explicitly selected native owner before exact comparison, never accept either frame.
        var exact = candidates.Where(candidate => parameterOwnerArguments == null
            ? Match(candidate.GetParameters(), signature, null) == MatchKind.Exact
            : (NativeSignatures.Method(candidate)?.Parameters
                ?? candidate.GetParameters().Select(parameter => RefTypeOf(parameter.ParameterType)).ToArray())
                .Select(parameter => CanonicalNativeFormalType(
                    SupertypeGraph.SubstOwnerTvs(parameter, parameterOwnerArguments)))
                .SequenceEqual(signature)).ToList();
        var own = exact.Where(candidate => SameDeclType(candidate.DeclaringType, declarationOwner)).ToList();
        if (own.Count != 0) return own.Count == 1 ? own[0] : null;
        return exact.Count == 1 ? exact[0] : null;
    }

    // `PickUnique`'s first two tiers without its diagnostics: the exact structural match, then the single applicable
    // one. Null where the set is empty or leaves more than one standing — the caller has another set to ask before it
    // may call that a hard error. The third tier (fewest `object` parameters) is deliberately absent: a DECLARED
    // parameter vector either is the member's or is not, and there is nothing to be more-specific about.
    static MethodInfo TryPickUnique(List<MethodInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var applicable = MostDerived(scored.Select(x => x.c).ToList());
        return applicable.Count == 1 ? applicable[0] : null;
    }

    // The same two tiers for a CONSTRUCTOR set. Constructors are never inherited, so there is no most-derived
    // question to ask: a set of them either narrows to one declaration or it does not.
    static ConstructorInfo TryPickUniqueCtor(List<ConstructorInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList();
        if (exact.Count > 0) return exact.Count == 1 ? exact[0] : null;
        return scored.Count == 1 ? scored[0].c : null;
    }

    // ---- shared resolution ---------------------------------------------------------------------

    // Resolve the owner's OPEN definition off the ref.dll — via ResolveRefType, which (unlike ResolveNetType) does NOT
    // skip `kotlin.*`, so a stdlib-owner clr* node (`kotlin.collections.Iterator.next()`, kept by IteratorConsumer-
    // Normalization for the rt-stdlib link) resolves its declared member sig here too. RESPECTS generic arity: a generic
    // owner (args present) binds the arity-suffixed def (`TaskCompletionSource`1`), never a same-named NON-generic sibling.
    internal static Type ResolveOwnerType(TypeNode.Fqn ownerFqn)
        => ResolveOwnerType(ownerFqn, _refs);

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
        return RefDef(ownerFqn.Name, ownerFqn.Args?.Length ?? 0, refs);
    }

    // DefaultArgSplice runs before NetInteropBinding, but it must read defaults from the exact MethodDef this pass will
    // later put in memberRef. Match in the selected declaration/projection frame; a second applicability search here
    // can materialize a sibling's value irreversibly after owner substitution makes their parameters coincide.
    internal static bool TryResolveExternalMethodForDefaults(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn,
        string name, int methodArity, bool isStatic, IReadOnlyList<TypeNode> callSignature,
        out MethodInfo declaration, string propertyKind = null)
    {
        declaration = null;
        if (refs == null || ownerFqn == null || name == null || callSignature == null) return false;
        _refs = refs;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null) return false;
        // Defaults belong to the same accessor MethodDef used by later interop
        // binding, not to the Kotlin operator spelling of an indexed property.
        if (propertyKind is "index-get" or "index-set")
            name = NetInteropBinding.DefaultIndexerAccessor(open, propertyKind == "index-set") ?? name;
        TypeNode Physical(TypeNode type, bool typeArg) => BirTypeLowering.CanonicalPhysicalSlotType(
            BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames, typeArg, nullableFrames: refs.NullableTypeFrames));
        var argNodes = callSignature.Select(type => Physical(type, typeArg: false)).ToList();
        var ownerArgs = ownerFqn.Args?.Select(type => Physical(type, typeArg: true)).ToArray();
        var flags = BindingFlags.Public | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = Candidates(open, name, argNodes, ownerArgs, flags)
            .Where(IsPublicOrProtected)
            .Where(method => method.IsGenericMethodDefinition
                ? method.GetGenericArguments().Length == methodArity
                : methodArity == 0)
            .ToList();
        declaration = TryPickDeclared(candidates, argNodes, open)
            ?? throw new InvalidOperationException(
                $"bir2cir: default argument declaration '{ownerFqn.Name}.{name}' has no unique open signature");
        return true;
    }

    // Pick the UNIQUE member whose declared params match `argNodes`: Tier 1 all-exact; Tier 2 all-applicable (def-level
    // assignability, shallow — mirrors ilemit PickOpenCtor.ParamAccepts); Tier 3 fewest-`object` strict-min-unique.
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    static T PickUnique<T>(List<T> cands, Func<T, ParameterInfo[]> paramsOf, List<TypeNode> argNodes,
                           TypeNode[] ownerArgs, string desc) where T : MethodBase
    {
        var scored = cands.Select(c => (c, m: Match(paramsOf(c), argNodes, ownerArgs))).Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw Malformed(desc, exact);
        var appl = MostDerived(scored.Select(x => x.c).ToList());
        if (appl.Count == 1) return appl[0];
        if (appl.Count == 0) throw NoMatch(desc, cands);
        // Tier 3 — most-specific: fewest `object` params, strict-min-unique (parity with C#'s "better function member"
        // falling out of specificity: a non-`object` param beats `object`). A tie is a HARD malformed error, never a pick.
        var ranked = appl.Select(c => (c, obj: paramsOf(c).Count(p => IsObjectMlc(p.ParameterType)))).ToList();
        var min = ranked.Min(x => x.obj);
        var best = ranked.Where(x => x.obj == min).Select(x => x.c).ToList();
        if (best.Count == 1) return best[0];
        throw Malformed(desc, best);
    }

    // C#'s "most-derived declaring type wins" (§12.8.10.2): discard a candidate whose declaring type is a STRICT BASE of
    // another candidate's — a base CLASS (`Task<T>.GetAwaiter()` on Task`1 beats the inherited `Task.GetAwaiter()` on the
    // base Task) OR a base INTERFACE (`IEnumerable<T>.GetEnumerator()` beats `IEnumerable.GetEnumerator()`). resolvedMemberParams
    // (params) can't distinguish the return-only difference, so this shadowing rule is what makes the winner unique.
    static List<T> MostDerived<T>(List<T> hits) where T : MethodBase
    {
        if (hits.Count > 1) hits = hits.GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList();   // dedupe reflection duplicates
        if (hits.Count <= 1) return hits;
        return hits.Where(h => !hits.Any(o => !ReferenceEquals(o, h) && !SameDeclType(h.DeclaringType, o.DeclaringType)
            && (IsStrictBase(h.DeclaringType, o.DeclaringType) || IfaceImplementedBy(h.DeclaringType, o.DeclaringType)))).ToList();
    }

    static bool SameDeclType(Type a, Type b) => ReferenceEquals(a, b) || (a != null && b != null && SafeDef(a) == SafeDef(b));
    // True iff `baseT` is a STRICT base class of `derived` (walk `derived`'s base-CLASS chain; generic-def aware).
    static bool IsStrictBase(Type baseT, Type derived)
    {
        if (baseT == null || derived == null) return false;
        try { for (var t = derived.BaseType; t != null; t = t.BaseType) if (SafeDef(t) == SafeDef(baseT)) return true; } catch { }
        return false;
    }
    // True iff `baseIface` is a base INTERFACE of `derived` (derived implements/extends it) — the interface twin of IsStrictBase.
    static bool IfaceImplementedBy(Type baseIface, Type derived)
    {
        if (baseIface == null || derived == null || !baseIface.IsInterface) return false;
        try { return derived.GetInterfaces().Any(i => SafeDef(i) == SafeDef(baseIface)); } catch { return false; }
    }

    enum MatchKind { No, Assignable, Exact }

    static MatchKind Match(ParameterInfo[] ps, List<TypeNode> argNodes, TypeNode[] ownerArgs)
    {
        if (ps.Length != argNodes.Count) return MatchKind.No;
        var acc = MatchKind.Exact;
        for (int i = 0; i < ps.Length; i++)
        {
            MatchKind m;
            if (NativeSignatureReader.ContainsPointer(ps[i].ParameterType)
                && NativeSignatures.Method((MethodBase)ps[i].Member) is { } native)
            {
                var actual = BirTypeLowering.LowerPhysicalType(argNodes[i], _refs.Aliases,
                    _ => false, _refs.PhysicalTypeNames, typeArg: false);
                var expected = ownerArgs is { Length: > 0 }
                    ? SupertypeGraph.SubstOwnerTvs(native.Parameters[i], ownerArgs) : native.Parameters[i];
                m = BirTypeLowering.CanonicalPhysicalSlotType(actual) == BirTypeLowering.CanonicalPhysicalSlotType(expected)
                    ? MatchKind.Exact : MatchKind.No;
            }
            else m = Applies(argNodes[i], ps[i].ParameterType, ownerArgs);
            if (m == MatchKind.No) return MatchKind.No;
            if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
        }
        return acc;
    }

    // Applicability of a DECLARED arg TypeNode `a` (from kotc's FIR-resolved `sig`) to a candidate OPEN-def param `p`.
    // `p` is ALIAS-RESOLVED first (a ref.dll member param typed through a stdlib @ClrTypeAlias is compared/emitted as
    // its BCL twin, matching the lowered arg).
    //   CONCRETE param -> the C#-binder LEAF rule: resolve the arg to an MLC Type, exact-identity or IsAssignableFrom
    //     (so `sbyte[]` binds `Sort(System.Array)`); an UNRESOLVABLE arg (a local/synthetic ref such as
    //     `dotkt$CharSequence`, or a function-type) binds only `object` (+ an arity-matching delegate param for a function
    //     arg) — the deterministic form of ilemit's former object-steering, never a first-pick.
    //   OPEN param (a type-var, or a constructed generic mentioning one) -> STRUCTURAL match under positional-tv
    //     equality: a class-var param at position i is satisfied by the DECLARED `tv(type,i)` (a method call carries the
    //     callee-owner's own class var) OR by the arg matching `ownerArgs[i]` (a ctor carries the SUBSTITUTED concrete
    //     arg — `Box<Int>(value: Int)`); a method tv by `tv(method,i)`; a constructed generic
    //     recurses, with a shallow def-derivation assignability (IReadOnlyCollection<E> -> IEnumerable<E>).
    static MatchKind Applies(TypeNode a, Type p, TypeNode[] ownerArgs)
    {
        if (a is TypeNode.Oblivious ob) return Applies(ob.Of, p, ownerArgs);
        p = AliasResolve(p);
        // dll2klib exposes a current Kotlin declaration's source arguments, even
        // inside a native signature. Compare that projection through its explicit
        // frame, then retain the winning CLR declaration's COMPLETE signature.
        // Never reconstruct companion arguments from the source projection.
        TypeNode ProjectFrame(TypeNode type) => type switch {
            TypeNode.Fqn { Args: { } args } named => new TypeNode.Fqn(
                _refs.PhysicalTypeNames.GetValueOrDefault(named.Name) ?? named.Name,
                (_refs.NullableTypeFrames.TryGetValue(named.Name, out var frame)
                    && args.Length == frame.PhysicalArity ? frame.OrdinaryArguments(args) : args)
                    .Select(ProjectFrame).ToArray()),
            TypeNode.ByRef reference => new TypeNode.ByRef(ProjectFrame(reference.Of)),
            TypeNode.Array array => new TypeNode.Array(ProjectFrame(array.Elem), array.Rank, array.SzArray),
            TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectFrame(nullable.Of)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectFrame(oblivious.Of)),
            _ => type,
        };
        var physical = MemberSigOf(p);
        var projected = ProjectFrame(physical);
        if (projected != physical && SignatureValueTypes.Of(a) == SignatureValueTypes.Of(projected))
            return MatchKind.Exact;
        // An unmanaged pointer has its own recursive signature identity. Handle it before the concrete-type fast
        // path: `MapMlc` can materialize ordinary `int*`, but the CIR spelling `void*` deliberately uses the
        // primitive token `void`, which is not a resolvable nominal TypeRef. The semantic KLIB marker and the
        // already-lowered CIR pointer must answer identically at both resolution stages.
        if (p.IsPointer)
        {
            var pointerNode = a switch
            {
                TypeNode.Nullable nullable => nullable.Of,
                _ => a,
            };
            var pointee = pointerNode switch
            {
                TypeNode.Ptr ptr => ptr.Of,
                TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn, Args: { Length: 1 } } marker => marker.Args[0],
                _ => null,
            };
            if (pointee == null) return MatchKind.No;
            var physicalPointee = p.GetElementType();
            if (physicalPointee.FullName == "System.Void"
                && pointee is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null })
                return MatchKind.Exact;
            return Applies(pointee, physicalPointee, ownerArgs);
        }
        if (!p.IsGenericParameter && !p.ContainsGenericParameters)
        {
            var aT = MapMlc(a);
            if (aT != null)
            {
                if (aT == p) return MatchKind.Exact;
                try { if (p.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
                // The DECLARED arg is the erased top type `object` (kotlin.Any) — the runtime value is really a subtype,
                // so a REFERENCE param accepts it via an implicit downcast (emitted as a castclass). Mirrors the old
                // object-arg acceptance (`fun createArray(cls: Any, ...)` binding to `Array.CreateInstance(Type,...)`).
                if (IsObjectMlc(aT) && !p.IsValueType) return MatchKind.Assignable;
                // Kotlin's primitive DUAL-REPRESENTATION: a signed integer and its SAME-WIDTH unsigned twin share the
                // bit pattern (Long==ULong, Int==UInt, …), so a @ClrIntrinsic bit-op binds `Long.countLeadingZeroBits()`
                // to `BitOperations.LeadingZeroCount(UInt64)` — the arg reinterprets. Same-width only (never Int64->UInt32).
                if (SameWidthIntegral(aT, p)) return MatchKind.Assignable;
                return MatchKind.No;
            }
            // Some compiler-owned physical types are intentionally not general-purpose ref-resolver inputs
            // (`dotkt$CharSequence` is the canonical example), but they can still occur verbatim in a referenced
            // declaration signature.  Their unresolved TypeNode is exact when its canonical metadata identity is
            // exactly the reflected parameter identity; this is identity comparison, not assignability inference.
            if (a is TypeNode.Fqn unresolved
                && string.Equals(StripArity(Dotted(unresolved.Name)),
                    StripArity(Dotted(p.FullName ?? p.Name)), StringComparison.Ordinal))
                return MatchKind.Exact;
            if (a is TypeNode.Fn fn) return MatchFnToDelegate(fn, p, ownerArgs);
            // An array with an UNRESOLVABLE element (`Array<T>`, T a type-var) still IS a System.Array/object and
            // implements the non-generic array interfaces — `System.Array` assignable to `p` means `T[]` is too (e.g.
            // the generic `arrayCopy(Array<T>,...)` binding to `Array.Copy(System.Array,...)`).
            if (a is TypeNode.Array)
            {
                var sysArr = SystemArrayMlc();
                if (sysArr != null) { try { if (p.IsAssignableFrom(sysArr)) return MatchKind.Assignable; } catch { } }
            }
            return IsObjectMlc(p) ? MatchKind.Assignable : MatchKind.No;
        }
        // p is OPEN (a generic parameter or a constructed generic mentioning one).
        if (p.IsGenericParameter)
        {
            int i = p.GenericParameterPosition;
            if (p.DeclaringMethod != null)
                return a is TypeNode.Tv { Scope: "method" } mtv && mtv.I == i ? MatchKind.Exact : MatchKind.No;
            // class type-var: (a) DECLARED — the arg is the owner's own class var at position i (a method call); OR
            // (b) SUBSTITUTED — the arg matches the owner's instantiation arg at position i (a ctor's concrete param).
            if (a is TypeNode.Tv { Scope: "type" } ttv && ttv.I == i) return MatchKind.Exact;
            if (ownerArgs != null && i >= 0 && i < ownerArgs.Length && ownerArgs[i] != null) return NodeEq(a, ownerArgs[i]);
            return MatchKind.No;
        }
        if (p.IsByRef) return a is TypeNode.ByRef b ? Applies(b.Of, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsArray) return a is TypeNode.Array ar ? Applies(ar.Elem, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsGenericType && SafeDef(p) == NullableDef())
            return a is TypeNode.Nullable nv ? Applies(nv.Of, p.GetGenericArguments()[0], ownerArgs) : MatchKind.No;
        if (a is TypeNode.Nullable nn) return Applies(nn.Of, p, ownerArgs);   // nullable ref arg = same .NET type
        if (a is TypeNode.Fn fnOpen) return MatchFnToDelegate(fnOpen, p, ownerArgs);     // a lambda arg binding an OPEN delegate param (`ThreadLocal<T>(Func<T>)`)
        if (a is not TypeNode.Fqn f) return MatchKind.No;
        var pdef = SafeDef(p);
        var adef = RefDef(f.Name, f.Args?.Length ?? 0);
        if (adef == null) return MatchKind.No;
        var adefDef = SafeDef(adef);
        if (adefDef == pdef)
        {
            if (f.Args == null) return MatchKind.Exact;
            var pa = p.GetGenericArguments();
            if (pa.Length != f.Args.Length) return MatchKind.No;
            var acc = MatchKind.Exact;
            for (int i = 0; i < pa.Length; i++)
            {
                var m = Applies(f.Args[i], pa[i], ownerArgs);
                if (m == MatchKind.No) return MatchKind.No;
                if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
            }
            return acc;
        }
        try { if (adefDef.GetInterfaces().Any(i => SafeDef(AliasResolve(i)) == pdef)) return MatchKind.Assignable; } catch { }
        return MatchKind.No;
    }

    // Structural equality of an arg TypeNode against the owner's instantiation-arg TypeNode (the ctor SUBSTITUTED-param
    // bridge): both resolve to the same MLC Type (exact), or the arg is assignable to the instantiation arg.
    static MatchKind NodeEq(TypeNode a, TypeNode ownerArg)
    {
        if (a == ownerArg) return MatchKind.Exact;
        if (CollectionViewFaces.IsViewSeam(a, ownerArg)) return MatchKind.Assignable;
        // A constructed owner slot closed over Object accepts every source value through the CLR's ordinary
        // reference conversion / boxing path. `Pair<Any, …>(nullableInt, …)` is the constructor form of the same
        // generic object-erasure seam already accepted for method parameters below.
        if (ownerArg is TypeNode.Fqn { Name: "object" or "System.Object", Args: null })
            return MatchKind.Assignable;
        // A `Nullable<value>` arg (`Int?`) binds the underlying value owner-arg (`T`=Int): the value-nullable GENERIC
        // erasure (#128) — `IComparer<Int>.Compare(x: Int?, y: Int?)` binds the constructed `Compare(Int,Int)`.
        if (a is TypeNode.Nullable an && NodeEq(an.Of, ownerArg) != MatchKind.No) return MatchKind.Assignable;
        var aT = MapMlc(a); var oT = MapMlc(ownerArg);
        if (aT != null && oT != null)
        {
            if (aT == oT) return MatchKind.Exact;
            try { if (oT.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
        }
        // The arg is the erased top type `object` (kotlin.Any) — in a GENERIC erasure it IS the owner-arg boxed/as-is
        // (a reference class, an unresolvable LOCAL class, OR a boxed value type unboxed at emit): `Comparable<Ver>.
        // compareTo(other:object)` binds `CompareTo(Ver)`, `IComparer<Int>.Compare(object,object)` binds `Compare(Int,Int)`.
        if (a is TypeNode.Fqn { Name: "object" or "System.Object", Args: null }) return MatchKind.Assignable;
        return MatchKind.No;
    }

    // Resolve a ref.dll-reflected type through the @ClrTypeAlias index to its BCL-MLC twin, recursively over generic
    // args / element types; a generic parameter and a non-aliased type are returned unchanged.
    static Type AliasResolve(Type t)
    {
        if (t == null || t.IsGenericParameter) return t;
        if (t.IsArray)
        {
            var e = AliasResolve(t.GetElementType());
            if (ReferenceEquals(e, t.GetElementType())) return t;
            return t.IsSZArray ? e.MakeArrayType() : e.MakeArrayType(t.GetArrayRank());
        }
        if (t.IsByRef) { var e = AliasResolve(t.GetElementType()); return ReferenceEquals(e, t.GetElementType()) ? t : e.MakeByRefType(); }
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var rdef = AliasDef(def);
            var args = t.GetGenericArguments().Select(AliasResolve).ToArray();
            if (rdef == null && args.Zip(t.GetGenericArguments(), ReferenceEquals).All(x => x)) return t;
            try { return (rdef ?? def).MakeGenericType(args); } catch { return t; }
        }
        return AliasDef(t) ?? t;
    }

    // The BCL-MLC type an @ClrTypeAlias def maps to, or null when the def is not aliased.
    static Type AliasDef(Type def)
    {
        return _refs.Aliases.TryGetValue(AliasKey(def), out var bcl)
            ? RefDef(bcl, def.IsGenericType ? def.GetGenericArguments().Length : 0)
            : null;
    }

    /// <summary>
    /// The @ClrTypeAlias index key for a reflected type: dotted, with the arity backtick dropped from EVERY
    /// nesting segment.
    /// </summary>
    /// <remarks>
    /// Truncating at the FIRST backtick instead keys `Map`2+Entry`2` as `kotlin.collections.Map`, so a nested
    /// type inherits its OUTER type's alias — `Map.Entry` resolved to `IDictionary`. Nothing caught that while
    /// the only consumer matched on parameters, because the mis-aliased type sat in a return.
    /// </remarks>
    static string AliasKey(Type def) =>
        string.Join('.', (def.FullName ?? def.Name).Split('+').Select(StripArity));

    // Resolve a .NET type by name off the ref.dll, RESPECTING generic arity: probe the arity-suffixed def (`Foo`1`)
    // FIRST when arity>0, so a same-named NON-generic sibling (`TaskCompletionSource`/the `System.Nullable` static class)
    // never shadows the generic def (ResolveRefType/ResolveNetType probe the bare name first).
    static Type RefDef(string owner, int arity, ReferenceMetadataIndex refs = null)
    {
        refs ??= _refs;
        if (refs == null) return null;
        var physical = owner.Contains('`') || owner.Contains('+');
        if (physical)
            return refs.ResolveRefType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner), 0);
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner);
        // A current-format [ClrExternal] classifier may have a collision-free Kotlin source name while naming an
        // arity-bearing or nested CLR TypeDef (for example Foo1<T> -> Foo`1). The producer-authored mapping is the
        // identity; do not reconstruct the physical name from the semantic spelling and argument count.
        if (refs.PhysicalTypeNames.TryGetValue(bare, out var mapped))
            return refs.ResolveRefType(mapped, 0);
        // A flattened nested identity needs each declaring segment's own metadata arity
        // (`Outer`1+Leaf`1`), not one suffix made from the flattened total (`Outer+Leaf`2`).
        if (refs.TryExactPhysicalTypeName(bare, arity, out var exact))
        {
            if (exact == null)
                throw new InvalidOperationException(
                    $"ambiguous CLR metadata identity for nested type '{bare}' with flattened arity {arity}");
            // The exact spelling is authoritative. If local-source precedence rejects it or its declaring reference
            // is unavailable, do not fall back to a different aggregate-arity TypeDef.
            return refs.ResolveRefType(exact, 0);
        }
        if (arity > 0 && !bare.Contains('`') && refs.ResolveRefType(bare + "`" + arity, arity) is { } g) return g;
        return refs.ResolveRefType(bare, arity);
    }

    // A concrete arg TypeNode -> its MLC Type (null when it embeds an open tv / a local-emitted / a delegate / can't construct).
    static Type MapMlc(TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Oblivious o: return MapMlc(o.Of);
            case TypeNode.ByRef b: { var e = MapMlc(b.Of); return e?.MakeByRefType(); }
            case TypeNode.Ptr p: { var e = MapMlc(p.Of); return e?.MakePointerType(); }
            case TypeNode.Array a:
            {
                var e = MapMlc(a.Elem);
                return e == null ? null : a.SzArray ? e.MakeArrayType() : e.MakeArrayType(a.Rank);
            }
            case TypeNode.Nullable n:
            {
                var inner = MapMlc(n.Of);
                if (inner == null) return null;
                if (!inner.IsValueType) return inner;
                try { return NullableDef()?.MakeGenericType(inner); } catch { return null; }
            }
            case TypeNode.Tv: return null;
            case TypeNode.Fn: return null;
            case TypeNode.Fqn f:
            {
                var baseT = RefDef(f.Name, f.Args?.Length ?? 0);
                if (baseT == null) return null;
                if (f.Args == null || f.Args.Length == 0) return baseT;
                if (!baseT.IsGenericTypeDefinition) return baseT;
                var margs = f.Args.Select(MapMlc).ToArray();
                if (margs.Any(x => x == null)) return null;
                try { return baseT.MakeGenericType(margs); } catch { return null; }
            }
        }
        return null;
    }

    static Type _nullableDef;
    static Type NullableDef() => _nullableDef ??= RefDef("System.Nullable", 1);
    static Type _sysArr;
    // This is an internal ABI-resolution probe, not a NetInterop ownership decision. Use ResolveRefType so DotKt
    // declaration ownership filters cannot hide the CLR root array type while matching `T[]` to Array.Copy(Array,...).
    static Type SystemArrayMlc() => _sysArr ??= _refs.ResolveRefType("System.Array");

    // The candidate set for `name`, PREFERRING the owner's OWN declared members over interface members
    // (C#'s "most-derived declaring type wins" — §12.8.10.2): reflection's GetMethods surfaces inherited CLASS
    // members, but not an interface slot implemented by a private explicit MethodImpl body. GetInterfaces is therefore
    // the fallback for class and interface owners alike. Adding those slots unconditionally would make
    // `IEnumerable<T>.GetEnumerator()` ambiguous with the inherited non-generic `IEnumerable.GetEnumerator()`
    // (resolvedMemberParams = [] cannot distinguish return-type-only overloads), so consult them only when no own member is
    // applicable. Static calls never bind an implemented-interface slot.
    static List<MethodInfo> Candidates(Type open, string name, List<TypeNode> argNodes, TypeNode[] ownerArgs, BindingFlags flags)
    {
        var own = new List<MethodInfo>();
        try { own.AddRange(open.GetMethods(flags).Where(m => m.Name == name &&
            m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        if ((flags & BindingFlags.Instance) == 0 ||
            own.Any(m => Match(m.GetParameters(), argNodes, ownerArgs) != MatchKind.No))
            return own;
        var withInterfaces = new List<MethodInfo>(own);
        foreach (var bi in SafeInterfaces(open))
            try { withInterfaces.AddRange(bi.GetMethods(flags).Where(m => m.Name == name &&
                m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        return withInterfaces;
    }

    static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

    // Kotlin's signed<->unsigned SAME-WIDTH integral TWIN (identical bit pattern; a @ClrIntrinsic bit-op reinterprets,
    // `Long.countLeadingZeroBits()` -> `BitOperations.LeadingZeroCount(UInt64)`). `Char` is DELIBERATELY excluded (it is
    // a distinct Kotlin type with explicit `.code`/`.toChar()` conversions — reinterpreting a Short into a `char` param
    // would silently print "A" for 65). Only the exact signed/unsigned pairs qualify.
    static bool SameWidthIntegral(Type a, Type b)
    {
        static string W(Type t) => (t.FullName ?? t.Name) switch
        {
            "System.SByte" or "System.Byte" => "8",
            "System.Int16" or "System.UInt16" => "16",
            "System.Int32" or "System.UInt32" => "32",
            "System.Int64" or "System.UInt64" => "64",
            "System.IntPtr" or "System.UIntPtr" => "n",
            _ => null,
        };
        var wa = W(a); return wa != null && wa == W(b);
    }
    static Type SafeDef(Type t) { try { return t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; } catch { return t; } }
    internal static string DeclaringTypeIdentity(MethodBase member)
    {
        var declaring = member?.DeclaringType
            ?? throw new InvalidOperationException($"bir2cir: resolved member '{member}' has no declaring type");
        declaring = SafeDef(AliasResolve(declaring));
        return declaring.FullName ?? declaring.Name;
    }
    internal static JsonNode DeclaringTypeDescriptor(MethodBase member)
        => TypeJson.Write(new TypeNode.Fqn(DeclaringTypeIdentity(member)));
    static bool IsObjectMlc(Type t) { try { return t.FullName == "System.Object"; } catch { return false; } }
    static bool IsVoidNode(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    // A function-type arg binds an `object` param OR a delegate param (BCL Func/Action or a stdlib delegate, possibly
    // OPEN — `Func<T>`). Match arity + void-ness, and each param/return STRUCTURALLY — but ONLY reject on a genuine
    // mismatch of RESOLVABLE types (`(Int)->Unit` must NOT bind `Action<string>`). An UNRESOLVABLE lambda side (a local/
    // kotlin.* type — `(MatchResult)->CharSequence` binding a dll2klib `MatchEvaluator(System.Text..Match)`) is a
    // WILDCARD (the delegate mapping bridges the Kotlin↔BCL types). A lambda->delegate is a CONVERSION -> Assignable.
    static MatchKind MatchFnToDelegate(TypeNode.Fn fn, Type p, TypeNode[] ownerArgs)
    {
        if (IsObjectMlc(p)) return MatchKind.Assignable;
        if (!IsDelegateType(p)) return MatchKind.No;
        var targetFamily = DelegateFamily(p);
        if (targetFamily != null && fn.Clr != targetFamily) return MatchKind.No;
        MethodInfo invoke; try { invoke = p.GetMethod("Invoke"); } catch { return MatchKind.No; }
        if (invoke == null) return MatchKind.No;
        var ips = invoke.GetParameters();
        var dp = fn.DelegateParams;
        if (ips.Length != dp.Length) return MatchKind.No;
        bool retVoid; try { retVoid = invoke.ReturnType.FullName == "System.Void"; } catch { retVoid = false; }
        if (retVoid != IsVoidNode(fn.Ret)) return MatchKind.No;
        for (int i = 0; i < dp.Length; i++) if (Incompatible(dp[i], ips[i].ParameterType, ownerArgs)) return MatchKind.No;
        if (!retVoid && Incompatible(fn.Ret, invoke.ReturnType, ownerArgs)) return MatchKind.No;
        return MatchKind.Assignable;
    }

    // A RESOLVABLE lambda side that structurally fails against the delegate's Invoke type — a genuine element mismatch.
    // An unresolvable side (MapMlc null) is a wildcard (No verdict), so the dll2klib Kotlin↔BCL delegate bridge passes.
    static bool Incompatible(TypeNode side, Type invokeType, TypeNode[] ownerArgs) =>
        MapMlc(side) != null && Applies(side, invokeType, ownerArgs) == MatchKind.No;
    static bool IsDelegateType(Type t)
    {
        try { for (var c = t; c != null; c = c.BaseType) if (c.FullName == "System.MulticastDelegate") return true; } catch { }
        return false;
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

    // A resolved OPEN-def member's param Type -> its declared-param TypeNode in the CLR-lowered vocabulary (BCL FullName
    // spellings, matching S1's lowered resolvedMemberParams). A class/method generic param -> a positional tv; a delegate keeps its
    // concrete Fqn (unlike TypeNodeOf, which drops delegates) so ilemit can link the exact slot.
    internal static TypeNode MemberSigOf(Type t)
    {
        t = AliasResolve(t);   // a ref.dll @ClrTypeAlias param -> its BCL twin, so ilemit's MapType links the rt-stdlib slot
        if (t.IsByRef) return new TypeNode.ByRef(MemberSigOf(t.GetElementType()));
        if (t.IsPointer) return new TypeNode.Ptr(MemberSigOf(t.GetElementType()));
        if (t.IsArray) return ArrayOf(t, MemberSigOf(t.GetElementType()));
        if (t.IsGenericParameter)
            return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // Kotlin function delegate families stay `fn`, carrying their exact nominal family. Unknown/custom CLR delegates
        // remain FQNs below.
        if (IsShapeDelegate(t)) return DelegateFn(t);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(MemberSigOf).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static bool IsShapeDelegate(Type t) => IsDelegateType(t) && DelegateFamily(t) != null;

    static string DelegateFamily(Type t)
    {
        Type def;
        try { def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; }
        catch { return null; }
        if (def.Namespace == "System")
        {
            if (def.Name == "Action" || def.Name.StartsWith("Action`", StringComparison.Ordinal)) return "System.Action";
            if (def.Name.StartsWith("Func`", StringComparison.Ordinal)) return "System.Func";
        }
        if (def.Namespace == "DotKt.Runtime.CompilerServices")
        {
            if (def.Name.StartsWith("KAction`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KAction";
            if (def.Name.StartsWith("KFunc`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KFunc";
        }
        return null;
    }

    static TypeNode DelegateFn(Type t)
    {
        var invoke = t.GetMethod("Invoke");
        if (invoke == null) return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));   // defensive: not a real delegate
        var ps = invoke.GetParameters().Select(p => MemberSigOf(p.ParameterType)).ToArray();
        var ret = invoke.ReturnType.FullName == "System.Void" ? new TypeNode.Fqn("void") : MemberSigOf(invoke.ReturnType);
        return new TypeNode.Fn(false, ret, ps, null, DelegateFamily(t));
    }

    static string Dotted(string s) => s.Replace('+', '.');
    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    // ---- diagnostics ---------------------------------------------------------------------------

    static string DescArgs(List<TypeNode> a) => string.Join(",", a.Select(x => TypeNode.ToJson(x)));
    static InvalidOperationException NoMatch<T>(string desc, List<T> cands) where T : MethodBase =>
        new($"bir2cir: no .NET member matches the resolved descriptor {desc} (ABI mismatch; {cands.Count} same-name/arity candidate(s): {string.Join("; ", cands.Select(c => c.ToString()))})");
    static InvalidOperationException Malformed<T>(string desc, List<T> hits) where T : MethodBase =>
        new($"bir2cir: resolved descriptor {desc} is AMBIGUOUS — {hits.Count} members match (malformed): {string.Join("; ", hits.Select(c => c.ToString()))}");

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
