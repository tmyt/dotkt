using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Toolchain;

// Declaration matching consumes explicit reference/local-type facts, never a mutable source graph.
static class ClrDeclarationMatching
{
    internal enum MatchKind { No, Assignable, Exact }

    // The same two tiers for a CONSTRUCTOR set. Constructors are never inherited, so there is no most-derived
    // question to ask: a set of them either narrows to one declaration or it does not.
    internal static ConstructorInfo TryPickUniqueCtor(ReferenceMetadataIndex references, List<ConstructorInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(references, c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList();
        if (exact.Count > 0) return exact.Count == 1 ? exact[0] : null;
        return scored.Count == 1 ? scored[0].c : null;
    }

    internal static ConstructorInfo PickDeclaredConstructor(ReferenceMetadataIndex references, IEnumerable<ConstructorInfo> constructors,
        IReadOnlyList<TypeNode> declaration, string context, IEqualityComparer<TypeNode> metadataSlots = null)
    {
        // Both `new` and base delegation carry the selected declaration's open shape.
        // Compare that shape, not argument applicability or a nominal-head approximation.
        var exact = constructors.Where(constructor =>
        {
            if (metadataSlots != null)
                return references.MetadataMethodSignature(constructor).Parameters.SequenceEqual(declaration, metadataSlots);
            var parameters = constructor.GetParameters();
            return parameters.Length == declaration.Count && parameters.Select((parameter, index) =>
                DeclaredSlotMatches(references, declaration[index],
                    references.FunctionSignatureDiscriminator(parameter) ?? parameter.ParameterType)).All(match => match);
        }).ToList();
        return exact.Count == 1 ? exact[0] : throw new InvalidOperationException(
            $"bir2cir: {context} resolves to {exact.Count} constructor declarations, expected exactly one");
    }

    internal static bool DeclaredSlotMatches(ReferenceMetadataIndex references, TypeNode selected, Type parameter)
    {
        selected = BirTypeLowering.CanonicalPhysicalSlotType(selected);
        if (selected is TypeNode.Oblivious oblivious)
            return DeclaredSlotMatches(references, oblivious.Of, parameter);
        parameter = AliasResolve(references, parameter);
        if (parameter.IsGenericParameter)
            return selected is TypeNode.Tv tv && tv.I == parameter.GenericParameterPosition
                && tv.Scope == (parameter.DeclaringMethod == null ? "type" : "method");
        if (parameter.IsByRef)
            return selected is TypeNode.ByRef byref && DeclaredSlotMatches(references, byref.Of, parameter.GetElementType());
        if (parameter.IsPointer)
            return selected is TypeNode.Ptr pointer && DeclaredSlotMatches(references, pointer.Of, parameter.GetElementType());
        if (parameter.IsArray)
            return selected is TypeNode.Array array && array.Rank == SafeArrayRank(parameter)
                && array.SzArray == parameter.IsSZArray
                && DeclaredSlotMatches(references, array.Elem, parameter.GetElementType());
        if (selected is TypeNode.Nullable nullable)
            return parameter.IsGenericType && SafeDef(parameter) == NullableDef(references)
                && DeclaredSlotMatches(references, nullable.Of, parameter.GetGenericArguments()[0]);
        if (selected is TypeNode.Fn function)
        {
            if (!IsDelegateType(parameter) || function.Clr != DelegateFamily(parameter)) return false;
            var invoke = parameter.GetMethod("Invoke");
            var delegateParameters = function.DelegateParams;
            return invoke != null && delegateParameters.Length == invoke.GetParameters().Length
                && delegateParameters.Select((p, i) => DeclaredSlotMatches(references, p, invoke.GetParameters()[i].ParameterType)).All(match => match)
                && DeclaredSlotMatches(references, function.Ret, invoke.ReturnType);
        }
        if (selected is not TypeNode.Fqn named) return false;
        var arguments = named.Args ?? Array.Empty<TypeNode>();
        var definition = RefDef(references, named.Name, arguments.Length);
        if (definition == null || SafeDef(definition) != SafeDef(parameter)) return false;
        var parameters = parameter.IsGenericType ? parameter.GetGenericArguments() : Type.EmptyTypes;
        return arguments.Length == parameters.Length
            && arguments.Select((arg, i) => DeclaredSlotMatches(references, arg, parameters[i])).All(match => match);
    }

    internal static MethodInfo MethodForNativeKey(ReferenceMetadataIndex refs, Type owner, string name, int arity,
        bool instance, string key)
    {
        var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
            (instance ? BindingFlags.Instance : BindingFlags.Static);
        MethodInfo Find(Type scope) => scope.GetMethods(flags).SingleOrDefault(m => m.Name == name &&
            ClrMemberResolution.IsPublicOrProtected(m) && m.GetGenericArguments().Length == arity &&
            refs.MatchesNativeMethod(m, key));
        for (var scope = owner; scope != null; scope = scope.BaseType)
            if (Find(scope) is { } member) return member;
        return (instance ? SafeInterfaces(owner).Select(Find).Where(m => m != null).Distinct().SingleOrDefault() : null)
            ?? throw new InvalidOperationException($"bir2cir: selected native method '{owner.FullName}.{name}' is missing");
    }

    internal static MethodInfo SelectedGenericDeclaration(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, Type owner, string name, int arity, bool instance,
        List<TypeNode> signature, TypeNode[] parameterOwnerArguments = null, string nativeMethodKey = null)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (instance ? BindingFlags.Instance : BindingFlags.Static);
        List<MethodInfo> Candidates(Type declaration) => declaration.GetMethods(flags)
            .Where(method => ClrMemberResolution.IsPublicOrProtected(method) && method.Name == name && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == arity
                && method.GetParameters().Length == signature.Count).ToList();
        if (nativeMethodKey != null)
            return MethodForNativeKey(references, owner, name, arity, instance, nativeMethodKey);
        return TryPickDeclared(references, localTypes, Candidates(owner), signature, owner, parameterOwnerArguments)
            ?? (!instance ? null : TryPickDeclared(references, localTypes, SafeInterfaces(owner).SelectMany(Candidates)
                .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList(),
                signature, owner, parameterOwnerArguments));
    }

    internal static TypeNode CanonicalNativeFormalType(ReferenceMetadataIndex references, TypeNode type) => BirTypeLowering.CanonicalPhysicalSlotType(
        BirTypeLowering.LowerPhysicalType(type, references.Aliases, references.IsValueType, references.PhysicalTypeNames,
            typeArg: false, nullableFrames: references.NullableTypeFrames));

    internal static MethodInfo TryPickDeclared(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, List<MethodInfo> candidates, List<TypeNode> signature, Type declarationOwner,
        TypeNode[] parameterOwnerArguments = null)
    {
        // The carried owner names the frontend-selected declaration/projection frame. Its own declaration is not
        // an inherited same-parameter member with a different return (Task<T>.GetAwaiter vs Task.GetAwaiter).
        // A projected completion on a class instead describes its constructed public interface surface: retain
        // that reflected frame rather than reopening I<string>.M(string) to the unrelated open I<T>.M(T) vector.
        // Call descriptors are open declaration vectors. Override descriptors instead belong to the implementer's
        // frame; substitute the explicitly selected native owner before exact comparison, never accept either frame.
        var exact = candidates.Where(candidate => parameterOwnerArguments == null
            ? Match(references, candidate.GetParameters(), signature, null, selectedDeclaration: true) == MatchKind.Exact
            : (references.NativeMethodSignature(candidate)?.Parameters
                ?? candidate.GetParameters().Select(parameter => RefTypeOf(references, localTypes, parameter.ParameterType)).ToArray())
                .Select(parameter => CanonicalNativeFormalType(references,
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
    internal static MethodInfo TryPickUnique(ReferenceMetadataIndex references, List<MethodInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(references, c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var applicable = MostDerived(scored.Select(x => x.c).ToList());
        return applicable.Count == 1 ? applicable[0] : null;
    }

    // Pick the UNIQUE member whose declared params match `argNodes`: Tier 1 all-exact; Tier 2 all-applicable (def-level
    // assignability, shallow — mirrors ilemit PickOpenCtor.ParamAccepts); Tier 3 fewest-`object` strict-min-unique.
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    internal static T PickUnique<T>(ReferenceMetadataIndex references, List<T> cands, Func<T, ParameterInfo[]> paramsOf, List<TypeNode> argNodes,
                           TypeNode[] ownerArgs, string desc) where T : MethodBase
    {
        var scored = cands.Select(c => (c, m: Match(references, paramsOf(c), argNodes, ownerArgs))).Where(x => x.m != MatchKind.No).ToList();
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
    internal static List<T> MostDerived<T>(List<T> hits) where T : MethodBase
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

    internal static MatchKind Match(ReferenceMetadataIndex references, ParameterInfo[] ps, List<TypeNode> argNodes, TypeNode[] ownerArgs,
        bool selectedDeclaration = false)
    {
        if (ps.Length != argNodes.Count) return MatchKind.No;
        var acc = MatchKind.Exact;
        for (int i = 0; i < ps.Length; i++)
        {
            MatchKind m;
            if (selectedDeclaration && argNodes[i] is TypeNode.Fn { Clr: not null } function)
                m = DeclaredSlotMatches(references, function, ps[i].ParameterType) ? MatchKind.Exact : MatchKind.No;
            else if (NativeSignatureReader.ContainsPointer(ps[i].ParameterType)
                && references.NativeMethodSignature((MethodBase)ps[i].Member) is { } native)
            {
                var actual = BirTypeLowering.LowerPhysicalType(argNodes[i], references.Aliases,
                    _ => false, references.PhysicalTypeNames, typeArg: false);
                var expected = ownerArgs is { Length: > 0 }
                    ? SupertypeGraph.SubstOwnerTvs(native.Parameters[i], ownerArgs) : native.Parameters[i];
                m = BirTypeLowering.CanonicalPhysicalSlotType(actual) == BirTypeLowering.CanonicalPhysicalSlotType(expected)
                    ? MatchKind.Exact : MatchKind.No;
            }
            else m = Applies(references, argNodes[i], ps[i].ParameterType, ownerArgs);
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
    static MatchKind Applies(ReferenceMetadataIndex references, TypeNode a, Type p, TypeNode[] ownerArgs)
    {
        if (a is TypeNode.Oblivious ob) return Applies(references, ob.Of, p, ownerArgs);
        p = AliasResolve(references, p);
        // dll2klib exposes a current Kotlin declaration's source arguments, even
        // inside a native signature. Compare that projection through its explicit
        // frame, then retain the winning CLR declaration's COMPLETE signature.
        // Never reconstruct companion arguments from the source projection.
        TypeNode ProjectFrame(TypeNode type) => type switch {
            TypeNode.Fqn { Args: { } args } named => new TypeNode.Fqn(
                references.PhysicalTypeNames.GetValueOrDefault(named.Name) ?? named.Name,
                (references.NullableTypeFrames.TryGetValue(named.Name, out var frame)
                    && args.Length == frame.PhysicalArity ? frame.OrdinaryArguments(args) : args)
                    .Select(ProjectFrame).ToArray()),
            TypeNode.ByRef reference => new TypeNode.ByRef(ProjectFrame(reference.Of)),
            TypeNode.Array array => new TypeNode.Array(ProjectFrame(array.Elem), array.Rank, array.SzArray),
            TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectFrame(nullable.Of)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectFrame(oblivious.Of)),
            _ => type,
        };
        var physical = MemberSigOf(references, p);
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
            return Applies(references, pointee, physicalPointee, ownerArgs);
        }
        if (!p.IsGenericParameter && !p.ContainsGenericParameters)
        {
            var aT = MapMlc(references, a);
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
            {
                // An unresolved argument can leave the entire constructed type
                // unmappable. Its nominal head alone does not identify a slot.
                var args = unresolved.Args ?? Array.Empty<TypeNode>();
                var formals = p.IsGenericType ? p.GetGenericArguments() : Type.EmptyTypes;
                if (args.Length == formals.Length && args.Select((arg, i) =>
                        Applies(references, arg, formals[i], ownerArgs)).All(match => match == MatchKind.Exact))
                    return MatchKind.Exact;
            }
            if (a is TypeNode.Fn fn) return MatchFnToDelegate(references, fn, p, ownerArgs);
            // An array with an UNRESOLVABLE element (`Array<T>`, T a type-var) still IS a System.Array/object and
            // implements the non-generic array interfaces — `System.Array` assignable to `p` means `T[]` is too (e.g.
            // the generic `arrayCopy(Array<T>,...)` binding to `Array.Copy(System.Array,...)`).
            if (a is TypeNode.Array)
            {
                var sysArr = SystemArrayMlc(references);
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
            if (ownerArgs != null && i >= 0 && i < ownerArgs.Length && ownerArgs[i] != null) return NodeEq(references, a, ownerArgs[i]);
            return MatchKind.No;
        }
        if (p.IsByRef) return a is TypeNode.ByRef b ? Applies(references, b.Of, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsArray) return a is TypeNode.Array ar ? Applies(references, ar.Elem, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (a is TypeNode.Nullable nv && p.IsGenericType && SafeDef(p) == NullableDef(references))
            return Applies(references, nv.Of, p.GetGenericArguments()[0], ownerArgs);
        if (a is TypeNode.Nullable nn) return Applies(references, nn.Of, p, ownerArgs);   // nullable ref arg = same .NET type
        if (a is TypeNode.Fn fnOpen) return MatchFnToDelegate(references, fnOpen, p, ownerArgs);     // a lambda arg binding an OPEN delegate param (`ThreadLocal<T>(Func<T>)`)
        if (a is not TypeNode.Fqn f) return MatchKind.No;
        var pdef = SafeDef(p);
        var adef = RefDef(references, f.Name, f.Args?.Length ?? 0);
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
                var m = Applies(references, f.Args[i], pa[i], ownerArgs);
                if (m == MatchKind.No) return MatchKind.No;
                if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
            }
            return acc;
        }
        try { if (adefDef.GetInterfaces().Any(i => SafeDef(AliasResolve(references, i)) == pdef)) return MatchKind.Assignable; } catch { }
        return MatchKind.No;
    }

    // Structural equality of an arg TypeNode against the owner's instantiation-arg TypeNode (the ctor SUBSTITUTED-param
    // bridge): both resolve to the same MLC Type (exact), or the arg is assignable to the instantiation arg.
    static MatchKind NodeEq(ReferenceMetadataIndex references, TypeNode a, TypeNode ownerArg)
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
        if (a is TypeNode.Nullable an && NodeEq(references, an.Of, ownerArg) != MatchKind.No) return MatchKind.Assignable;
        var aT = MapMlc(references, a); var oT = MapMlc(references, ownerArg);
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
    internal static Type AliasResolve(ReferenceMetadataIndex references, Type t)
    {
        if (t == null || t.IsGenericParameter) return t;
        if (t.IsArray)
        {
            var e = AliasResolve(references, t.GetElementType());
            if (ReferenceEquals(e, t.GetElementType())) return t;
            return t.IsSZArray ? e.MakeArrayType() : e.MakeArrayType(t.GetArrayRank());
        }
        if (t.IsByRef) { var e = AliasResolve(references, t.GetElementType()); return ReferenceEquals(e, t.GetElementType()) ? t : e.MakeByRefType(); }
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var rdef = AliasDef(references, def);
            var args = t.GetGenericArguments().Select(type => AliasResolve(references, type)).ToArray();
            if (rdef == null && args.Zip(t.GetGenericArguments(), ReferenceEquals).All(x => x)) return t;
            try { return (rdef ?? def).MakeGenericType(args); } catch { return t; }
        }
        return AliasDef(references, t) ?? t;
    }

    // The BCL-MLC type an @ClrTypeAlias def maps to, or null when the def is not aliased.
    static Type AliasDef(ReferenceMetadataIndex references, Type def)
    {
        return references.Aliases.TryGetValue(AliasKey(def), out var bcl)
            ? RefDef(references, bcl, def.IsGenericType ? def.GetGenericArguments().Length : 0)
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
    internal static Type RefDef(ReferenceMetadataIndex references, string owner, int arity)
    {
        if (references == null) return null;
        var physical = owner.Contains('`') || owner.Contains('+');
        if (physical)
            return references.ResolveRefType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner), 0);
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner);
        // A current-format [ClrExternal] classifier may have a collision-free Kotlin source name while naming an
        // arity-bearing or nested CLR TypeDef (for example Foo1<T> -> Foo`1). The producer-authored mapping is the
        // identity; do not reconstruct the physical name from the semantic spelling and argument count.
        if (references.PhysicalTypeNames.TryGetValue(bare, out var mapped))
            return references.ResolveRefType(mapped, 0);
        // A flattened nested identity needs each declaring segment's own metadata arity
        // (`Outer`1+Leaf`1`), not one suffix made from the flattened total (`Outer+Leaf`2`).
        if (references.TryExactPhysicalTypeName(bare, arity, out var exact))
        {
            if (exact == null)
                throw new InvalidOperationException(
                    $"ambiguous CLR metadata identity for nested type '{bare}' with flattened arity {arity}");
            // The exact spelling is authoritative. If local-source precedence rejects it or its declaring reference
            // is unavailable, do not fall back to a different aggregate-arity TypeDef.
            return references.ResolveRefType(exact, 0);
        }
        if (arity > 0 && !bare.Contains('`') && references.ResolveRefType(bare + "`" + arity, arity) is { } g) return g;
        return references.ResolveRefType(bare, arity);
    }

    // A concrete arg TypeNode -> its MLC Type (null when it embeds an open tv / a local-emitted / a delegate / can't construct).
    internal static Type MapMlc(ReferenceMetadataIndex references, TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Oblivious o: return MapMlc(references, o.Of);
            case TypeNode.ByRef b: { var e = MapMlc(references, b.Of); return e?.MakeByRefType(); }
            case TypeNode.Ptr p: { var e = MapMlc(references, p.Of); return e?.MakePointerType(); }
            case TypeNode.Array a:
            {
                var e = MapMlc(references, a.Elem);
                return e == null ? null : a.SzArray ? e.MakeArrayType() : e.MakeArrayType(a.Rank);
            }
            case TypeNode.Nullable n:
            {
                var inner = MapMlc(references, n.Of);
                if (inner == null) return null;
                if (!inner.IsValueType) return inner;
                try { return NullableDef(references)?.MakeGenericType(inner); } catch { return null; }
            }
            case TypeNode.Tv: return null;
            case TypeNode.Fn: return null;
            case TypeNode.Fqn f:
            {
                var baseT = RefDef(references, f.Name, f.Args?.Length ?? 0);
                if (baseT == null) return null;
                if (f.Args == null || f.Args.Length == 0) return baseT;
                if (!baseT.IsGenericTypeDefinition) return baseT;
                var margs = f.Args.Select(type => MapMlc(references, type)).ToArray();
                if (margs.Any(x => x == null)) return null;
                try { return baseT.MakeGenericType(margs); } catch { return null; }
            }
        }
        return null;
    }

    internal static Type NullableDef(ReferenceMetadataIndex references) => RefDef(references, "System.Nullable", 1);
    // This is an internal ABI-resolution probe, not a NetInterop ownership decision. Use ResolveRefType so DotKt
    // declaration ownership filters cannot hide the CLR root array type while matching `T[]` to Array.Copy(Array,...).
    internal static Type SystemArrayMlc(ReferenceMetadataIndex references) => references.ResolveRefType("System.Array");

    internal static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

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
    internal static Type SafeDef(Type t) { try { return t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; } catch { return t; } }
    static bool IsObjectMlc(Type t) { try { return t.FullName == "System.Object"; } catch { return false; } }
    internal static bool IsVoidNode(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    // A function-type arg binds an `object` param OR a delegate param (BCL Func/Action or a stdlib delegate, possibly
    // OPEN — `Func<T>`). Match arity + void-ness, and each param/return STRUCTURALLY — but ONLY reject on a genuine
    // mismatch of RESOLVABLE types (`(Int)->Unit` must NOT bind `Action<string>`). An UNRESOLVABLE lambda side (a local/
    // kotlin.* type — `(MatchResult)->CharSequence` binding a dll2klib `MatchEvaluator(System.Text..Match)`) is a
    // WILDCARD (the delegate mapping bridges the Kotlin↔BCL types). A lambda->delegate is a CONVERSION -> Assignable.
    static MatchKind MatchFnToDelegate(ReferenceMetadataIndex references, TypeNode.Fn fn, Type p, TypeNode[] ownerArgs)
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
        for (int i = 0; i < dp.Length; i++) if (Incompatible(references, dp[i], ips[i].ParameterType, ownerArgs)) return MatchKind.No;
        if (!retVoid && Incompatible(references, fn.Ret, invoke.ReturnType, ownerArgs)) return MatchKind.No;
        return MatchKind.Assignable;
    }

    // A RESOLVABLE lambda side that structurally fails against the delegate's Invoke type — a genuine element mismatch.
    // An unresolvable side (MapMlc null) is a wildcard (No verdict), so the dll2klib Kotlin↔BCL delegate bridge passes.
    static bool Incompatible(ReferenceMetadataIndex references, TypeNode side, Type invokeType, TypeNode[] ownerArgs) =>
        MapMlc(references, side) != null && Applies(references, side, invokeType, ownerArgs) == MatchKind.No;
    static bool IsDelegateType(Type t)
    {
        try { for (var c = t; c != null; c = c.BaseType) if (c.FullName == "System.MulticastDelegate") return true; } catch { }
        return false;
    }

    // A resolved OPEN-def member's param Type -> its declared-param TypeNode in the CLR-lowered vocabulary (BCL FullName
    // spellings, matching S1's lowered resolvedMemberParams). A class/method generic param -> a positional tv; a delegate keeps its
    // concrete Fqn (unlike TypeNodeOf, which drops delegates) so ilemit can link the exact slot.
    internal static TypeNode MemberSigOf(ReferenceMetadataIndex references, Type t)
    {
        t = AliasResolve(references, t);   // a ref.dll @ClrTypeAlias param -> its BCL twin, so ilemit's MapType links the rt-stdlib slot
        if (t.IsByRef) return new TypeNode.ByRef(MemberSigOf(references, t.GetElementType()));
        if (t.IsPointer) return new TypeNode.Ptr(MemberSigOf(references, t.GetElementType()));
        if (t.IsArray) return ArrayOf(t, MemberSigOf(references, t.GetElementType()));
        if (t.IsGenericParameter)
            return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // Kotlin function delegate families stay `fn`, carrying their exact nominal family. Unknown/custom CLR delegates
        // remain FQNs below.
        if (IsShapeDelegate(t)) return DelegateFn(references, t);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(type => MemberSigOf(references, type)).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static bool IsShapeDelegate(Type t) => IsDelegateType(t) && DelegateFamily(t) != null;

    internal static string DelegateFamily(Type t)
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

    static TypeNode DelegateFn(ReferenceMetadataIndex references, Type t)
    {
        var invoke = t.GetMethod("Invoke");
        if (invoke == null) return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));   // defensive: not a real delegate
        var ps = invoke.GetParameters().Select(p => MemberSigOf(references, p.ParameterType)).ToArray();
        var ret = invoke.ReturnType.FullName == "System.Void" ? new TypeNode.Fqn("void") : MemberSigOf(references, invoke.ReturnType);
        return new TypeNode.Fn(false, ret, ps, null, DelegateFamily(t));
    }

    internal static string Dotted(string s) => s.Replace('+', '.');
    internal static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }
    internal static InvalidOperationException NoMatch<T>(string desc, List<T> cands) where T : MethodBase =>
        new($"bir2cir: no .NET member matches the resolved descriptor {desc} (ABI mismatch; {cands.Count} same-name/arity candidate(s): {string.Join("; ", cands.Select(c => c.ToString()))})");
    internal static InvalidOperationException Malformed<T>(string desc, List<T> hits) where T : MethodBase =>
        new($"bir2cir: resolved descriptor {desc} is AMBIGUOUS — {hits.Count} members match (malformed): {string.Join("; ", hits.Select(c => c.ToString()))}");

    // A resolved member's declared type, spelled with the target's exact metadata identity. No arity stripping,
    // no `+`-to-`.` flattening, no delegate rewritten as a Kotlin function type, no `System.Nullable`1`
    // collapsed to a nullability wrapper: each of those is a document convention for talking about a Kotlin
    // program, and each one merges types this reference has to keep apart.
    // The twin the member is RESOLVED in is not the twin it is CALLED in. bir2cir resolves against the stdlib's
    // reference twin, which declares the Kotlin surface (`kotlin.collections.List<kotlin.collections.List<T>>`);
    // the member this reference names lives in the runtime twin, which declares the physical shape
    // (`IReadOnlyList<IList<T>>`). Those are two vocabularies for one member, and the map between them is
    // BirTypeLowering's — POSITION-DEPENDENT, so it cannot be applied by walking the signature with one alias
    // step. Doing that produced `IReadOnlyList<IReadOnlyList<T>>`: a member neither twin declares.
    internal static TypeNode RefTypeOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, Type t) => PhysicalTypeOf(references, localTypes, t, null, typeArg: false);

    /// <summary>
    /// A reflected type, spelled as the target declares it. `ownerArgs`, when given, substitutes the declaring
    /// type's own parameters positionally — projecting a declaration edge instead of naming it over itself.
    /// </summary>
    /// <remarks>
    /// Projection and plain naming were two functions until each was found applying the alias uniformly; they
    /// are one function now because being two is what let one be fixed and the other not.
    /// </remarks>
    internal static TypeNode PhysicalTypeOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, Type t, TypeNode[] ownerArgs, bool typeArg)
    {
        if (t.IsGenericParameter)
            return ownerArgs != null && t.GenericParameterPosition < ownerArgs.Length
                ? ownerArgs[t.GenericParameterPosition]
                : new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // An element, a pointee and a byref target are VALUE positions — `typeArg: false` — exactly as
        // BirTypeLowering lowers them. The collapse below applies to storage slots, not to what a slot holds.
        if (t.IsByRef) return new TypeNode.ByRef(PhysicalTypeOf(references, localTypes, t.GetElementType(), ownerArgs, typeArg: false));
        if (t.IsPointer) return new TypeNode.Ptr(PhysicalTypeOf(references, localTypes, t.GetElementType(), ownerArgs, typeArg: false));
        if (t.IsArray) return ArrayOf(t, PhysicalTypeOf(references, localTypes, t.GetElementType(), ownerArgs, typeArg: false));
        var def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t;
        var kotlinName = AliasKey(def);
        TypeNode[] loweredArgs = null;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var argsAreSlots = !ArgumentsAreMethodSlots(t);
            loweredArgs = t.GetGenericArguments()
                .Select(a => PhysicalTypeOf(references, localTypes, a, ownerArgs, typeArg: argsAreSlots)).ToArray();
        }
        // ONE implementation of "which CLR type does this Kotlin type become", shared with the pass that lowered
        // the declaration being named. Reproducing it here instead reproduced part of it: the arg-position
        // collapse but not the generic-classifier erasure, and not the contravariant `Comparable<Any?>` collapse.
        var head = BirTypeLowering.PhysicalHead(kotlinName,
            references.Aliases.TryGetValue(kotlinName, out var bcl) ? bcl : null, loweredArgs,
            references.NullableTypeFrames, references.PhysicalTypeNames, localTypes);
        return MetadataSpelling(references, head, def, kotlinName);
    }

    // The vector and the general array are different types even at rank 1 — `T[]` and `T[*]` — and reflection
    // says which is which through IsSZArray. Collapsing them would let one reference name either.
    static TypeNode ArrayOf(Type t, TypeNode elem)
    {
        bool sz; try { sz = t.IsSZArray; } catch { sz = true; }
        return sz ? new TypeNode.Array(elem) : TypeNode.Array.General(elem, SafeArrayRank(t));
    }

    /// <summary>
    /// The head decision, respelled as the TARGET's metadata spells it.
    /// </summary>
    /// <remarks>
    /// The lowering speaks document vocabulary, where a generic name carries no arity backtick and nesting is
    /// dotted; a reference is a lookup key and must carry both. When the decision left the name alone, the
    /// reflected type's own FullName IS that spelling. When it substituted a BCL type, the substitute is named
    /// in the same universe, so its definition supplies the spelling rather than a backtick pasted on by hand.
    /// </remarks>
    static TypeNode MetadataSpelling(ReferenceMetadataIndex references, TypeNode head, Type def, string kotlinName)
    {
        if (head is not TypeNode.Fqn f) return head;
        if (f.Name == kotlinName) return new TypeNode.Fqn(PhysicalTypeName(def), f.Args);
        var substitute = RefDef(references, f.Name, f.Args?.Length ?? 0);
        return new TypeNode.Fqn(substitute != null ? PhysicalTypeName(substitute) : f.Name, f.Args);
    }

    /// <summary>
    /// True when this constructed generic's arguments reach the target as method slots rather than storage —
    /// so Root-V does NOT apply to them.
    /// </summary>
    internal static bool ArgumentsAreMethodSlots(Type t)
    {
        var def = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
        // The KProperty carriers, for the reason BirTypeLowering gives: each argument is substituted into an
        // interface method slot, not stored in one.
        return BirTypeLowering.IsMethodSlotCarrier(AliasKey(def));
    }

    static int SafeArrayRank(Type t) { try { return t.GetArrayRank(); } catch { return 1; } }

    // A type's own metadata name, verbatim. Every name inside a reference is a lookup key in the target
    // universe rather than a document type spelling, so neither the arity backtick nor `+` nesting is
    // normalized away — normalizing either is what merged `Outer`1+Inner` into `Outer`.
    internal static string PhysicalTypeName(Type t) => t.FullName ?? t.Name;
}
