using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

// ERASURE PROPAGATES FROM THE OVERRIDDEN SLOT, NOT FROM SYNTAX (#86 D3).
//
// `interface Sink<T> { fun accept(x: T?): String }` has its parameter object-erased like every other `Nullable(Tv)`
// slot, so the CLR slot an implementor must fill is `accept(object)` — at EVERY instantiation, because the erasure is
// a property of the DECLARATION and not of the type argument. `class IntSink : Sink<Int>` writes
// `override fun accept(x: Int?)`, which holds a CONCRETE type: there is no `Nullable(Tv)` anywhere in it, so no
// syntactic sweep can reach it, and left alone it emits `accept(Nullable<int32>)` — a NEW OVERLOAD. The interface
// method stays unimplemented and the type fails to LOAD.
//
// WHICH DECLARATION MOVES IS THE WHOLE DESIGN. Rewriting the override's own signature to the base slot makes the class
// load and dispatch, but it puts the type's physical shape and its Kotlin surface into permanent disagreement: the
// declaration says `accept(x: Int?)` and the assembly says `accept(object)`, so a separately compiled consumer that
// type-checks against the re-imported surface resolves a member that does not exist ("no referenced method matches the
// resolved descriptor IntSink.accept(nullable:System.Int32)"), and a C# consumer sees a parameter the Kotlin
// declaration never named. So THE OVERRIDE KEEPS ITS OWN PHYSICAL TYPE — `physical(s) = Erase(declared(s))` holds for
// it exactly as for every other declaration — and the base slot is filled by a synthesized PRIVATE bridge with the
// slot's exact signature, forwarding to the typed body across the `object` seam. That is the CLR's own
// explicit-implementation shape and the JVM's bridge-method idiom; `CovariantInterfaceReturnBridge` is the same
// construction for the covariant-return case, and this file is deliberately shaped like it.
//
// The generated bridge is physical implementation machinery, not a Kotlin declaration. dll2klib excludes its
// CompilerGenerated MethodDef and MethodImpl body; the authored or inherited source member owns the Kotlin API.
//
// ONE BRIDGE, ONE METHODIMPL PER SLOT IT FILLS. A slot is reached through the whole supertype graph — the constructed
// interface, its own base interfaces (including a synthesized existential view), and the base-class
// chain — and every one of those declares its own CLR slot even when the signatures coincide. The bridges are keyed by
// signature so the several slots share one body, and each contributes its own resolved MethodImpl descriptor. The walk
// itself is `SupertypeGraph`, shared with the crossing refusal that asks the same graph a different question.
//
// NESTED POSITIONS CANNOT BE BRIDGED, and are the one case where the declaration still moves. A base `Box<T?>` erases
// to `Box<object>`, and `Box<object>` and `Box<Nullable<int32>>` are unrelated invariant reified generics that no cast
// converts — there is no forwarding body to write. Such a position is rewritten in the override's declaration to the
// base's shape, with the override's own pre-erasure Kotlin type recorded on the two round-trip channels every erased
// slot uses (the `[KotlinNullableGeneric]` carrier and the slot's NRT byte), so the surface survives even though the
// physical type had to move. The split is exactly the CLR's: a bare `object` seam is one instruction in each
// direction, and a difference under a constructed generic is not expressible at all.
//
// A BASE DECLARED IN A REFERENCED ASSEMBLY IS READ, NOT SKIPPED. The supertype graph of the current compilation
// answers a same-module base; a base declared elsewhere — a referenced DotKt library, and the STDLIB, which is where
// `kotlin.Comparable` lives — is answered by the same D1 carrier reader every other referenced-declaration
// derivation uses (`ReferenceMetadataIndex.TryNullableGenericSlot`: the producing assembly's
// `[KotlinNullableGeneric]` carrier where the erasure recorded one, its physical signature otherwise).
//
// The two arms ask the question from opposite ends, because that is what each side can answer. A LOCAL supertype
// hands over its slot list, so the walk goes slot -> implementer. A REFERENCED supertype has no slot list here, but
// every override that must fill one names its owner and member in its own `overrides` marker — so the walk goes
// implementer -> slot, and asks the reader for exactly the member the author said they were overriding. Neither arm
// guesses: a slot with no `overrides` claim and a claim with no readable declaration are both left alone.
//
// The DESCRIPTOR names the CLR slot, not the Kotlin member. A referenced interface may be `@ClrTypeAlias`'d onto a
// BCL one whose member has a different name (`kotlin.Comparable.compareTo` fills `System.IComparable.CompareTo`), so
// the descriptor's member is resolved through the same `@ClrIntrinsic` binding `DeclarationRename` reads for the
// declaration itself. A MethodImpl naming a member the interface does not have fails type LOAD exactly as an
// unfilled slot does.
static partial class KotlinOverrideSlotBridge
{
    // The owning phases preserve three clocks: declaration moves before erasure/body realignment,
    // suspend value obligations before Cold/Task projection, and final MethodImpl allocation after
    // existential interfaces and both physical suspend declarations exist.
    enum Phase { DeclarationMoves, SuspendValueBridges, PhysicalBridges }

    internal static void SelfTest()
    {
        SourceDeclarationSelfTest();
        RepresentationSourceSelfTest();
    }

    static TypeNode DeclaredSlot(TypeNode type, ValueTypeOracle isValue, bool physical) =>
        physical ? type : NullableGenericErasure.EraseNullableTv(type, isValue);



    static InvalidOperationException UnrepresentableInheritedDefault(string owner, string implementationOwner,
        string member, string accessorKind, string reason) => new(
        $"bir2cir: inherited default property {implementationOwner}.{member} ({accessorKind}) cannot be represented "
        + $"on {owner}: an ordinary CLR method captures the selected property slot, but {reason}; "
        + "a callable producer-side trampoline is required");


    static bool SignatureMatches(OverrideMethod method, TypeNode[] expectedParams, TypeNode expectedRet,
        TypeNode[] ownerArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var sourceParameters = method.SuspendSourceParameters;
        var parameters = method.Parameters;
        var declaredRet = sourceParameters == null ? method.Return : method.SuspendSourceReturn;
        if (sourceParameters == null && !method.ParamsPresent
            || (sourceParameters?.Length ?? parameters.Length) != expectedParams.Length || declaredRet == null) return false;
        for (var i = 0; i < expectedParams.Length; i++)
        {
            var declared = sourceParameters == null ? parameters[i].Signature ?? parameters[i].Type : sourceParameters[i];
            if (declared == null
                || !InheritedDefaultSignatureTypeMatches(
                    SupertypeGraph.SubstOwnerTvs(declared, ownerArgs), expectedParams[i], refs, isValue,
                    returnPosition: false))
                return false;
        }
        return InheritedDefaultSignatureTypeMatches(
            SupertypeGraph.SubstOwnerTvs(declaredRet, ownerArgs), expectedRet, refs, isValue,
            returnPosition: true);
    }

    // The frontend fact is expressed in the inheriting class frame (`I<Int>.f(Int?)`), while the source declaration
    // has already passed nullable-generic erasure (`I<T>.f(object)`) when the CLR bridge half runs. Compare the two in
    // the physical frame selected by those same general lowering rules. `ErasureAligned` is deliberately directional:
    // only an object position already present on the erased source declaration may absorb a concrete class-frame type.
    static bool InheritedDefaultSignatureTypeMatches(TypeNode erasedDeclaration, TypeNode frontendFact,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, bool returnPosition) =>
        erasedDeclaration.Equals(frontendFact)
        || BirTypeLowering.SamePhysicalSlotType(erasedDeclaration, frontendFact,
            refs?.Aliases, isValue, refs?.PhysicalTypeNames, returnPosition, nullableFrames: refs?.NullableTypeFrames)
        || ErasureAligned(erasedDeclaration, frontendFact);



    static bool SamePhysicalSignature(TypeNode[] candidateParams, TypeNode candidateRet,
        TypeNode[] slotParams, TypeNode slotRet, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        Func<TypeNode, bool> hasClassifier = null)
    {
        if (candidateParams == null || candidateRet == null || candidateParams.Length != slotParams.Length)
            return false;
        for (var i = 0; i < candidateParams.Length; i++)
            if (Classify(slotParams[i], candidateParams[i], refs, isValue, returnPosition: false, hasClassifier: hasClassifier) != Fit.Same)
                return false;
        return Classify(slotRet, candidateRet, refs, isValue, returnPosition: true, hasClassifier: hasClassifier) == Fit.Same;
    }



    enum Fit { Same, Bridge, Rewrite, Foreign }


    // How the override's own physical type meets the slot the supertype requires.
    //   Same    — it already IS the slot (or lowers to it): nothing to do.
    //   Bridge  — the slot is a bare `object` and the declaration is not: the two interconvert in ONE instruction
    //             (`unbox.any` inward, `box`/`castclass` outward), so a forwarding body exists and the declaration
    //             keeps its own, truthful type.
    //   Rewrite — the difference is the SAME erasure, one level down: under a constructed generic or an array, where
    //             no conversion exists in either direction, so the declaration has to adopt the slot's shape.
    //   Foreign — a difference this erasure did not create (a covariantly narrowed return, a `@ClrTypeAlias` reshape).
    //             Not this pass's to reconcile, and moving it would state a type the author never wrote.
    static Fit Classify(TypeNode slot, TypeNode declared, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        bool returnPosition, bool referencedSlot = false, Func<TypeNode, bool> hasClassifier = null)
    {
        if (declared.Equals(slot)
            || BirTypeLowering.SamePhysicalSlotType(slot, declared, refs?.Aliases, isValue,
                refs?.PhysicalTypeNames, returnPosition, nullableFrames: refs?.NullableTypeFrames, slotOwners: refs)) return Fit.Same;
        // A plain CLR generic declaration's nullable annotation does not change its metadata signature: C# `T?`
        // on `I<T>.Put(T?)`, constructed as `I<int>`, is the BARE `int` slot. Its Kotlin projection is still the
        // truthful `Int?`, whose declaration lowers to `Nullable<int>`. This is a bridgeable outer seam just like
        // the object-erasure seam below, but its conversion is Nullable<T> construction/extraction rather than a
        // box/unbox cast. Keep it in this override-edge-driven table: selecting the slot later from name/arity loses
        // the frontend declaration identity and misbinds same-name overloads (#355).
        if (IsNullableValueSlot(slot, declared, refs, isValue, returnPosition)) return Fit.Bridge;
        if (slot is TypeNode.Array && declared is TypeNode.Fqn { Name: "System.Array" }
            || declared is TypeNode.Array && slot is TypeNode.Fqn { Name: "System.Array" })
            return Fit.Bridge;
        // Kotlin collection declarations can return a value where their aliased CLR interface slot is void
        // (`MutableCollection.add(): Boolean` -> `ICollection<T>.Add(T): void`). The bridge body already models this
        // as an expression statement, so keep the decision and exact MethodImpl in the same table instead of asking
        // ilemit to discover the mismatch and synthesize a MethodDef of its own.
        if (returnPosition && IsVoid(slot)) return Fit.Bridge;
        // A referenced MethodDef's source carrier can name Any/Any? for its existing object slot.
        // Local Kotlin declarations still belong to source covariance, not CLR object-slot erasure.
        if (IsBareObject(slot)
            || (hasClassifier?.Invoke(slot) ?? KotlinRuntimeTypeWitness.ValueRepresentations?.HasClassifier(slot) == true) && LowersToObject(slot, refs)
            || referencedSlot && LowersToObject(slot, refs))
            // `Any?`/`Any` reach the same bare `object` the slot is, one lowering later — a bridge for them would
            // declare a second member with the identical CLR signature.
            return LowersToObject(declared, refs) ? Fit.Same : Fit.Bridge;
        // Substitution into an interface's bare T can select a nullable companion variable, whereas the
        // overriding body's scalar T? is object-erased. The selected override relation licenses an outer
        // box/unbox bridge; it does not license rewriting invariant constructions containing either type.
        if (IsCompanionScalarSeam(slot, declared)) return Fit.Bridge;
        return ErasureAligned(slot, declared) ? Fit.Rewrite : Fit.Foreign;
    }

    static bool IsCompanionScalarSeam(TypeNode slot, TypeNode declared) =>
        slot is TypeNode.Tv && IsBareObject(declared);

    internal static bool SameSourceRepresentationVariables(TypeNode slot, TypeNode declared,
        NullableRepresentationFrame ownerFrame)
    {
        if (ownerFrame == null || slot is not TypeNode.Tv { Scope: "type" } left
            || declared is not TypeNode.Tv { Scope: "type" } right
            || left.I < 0 || right.I < 0
            || left.I >= ownerFrame.PhysicalArity || right.I >= ownerFrame.PhysicalArity) return false;
        var leftSlot = ownerFrame.PhysicalSlot(left.I);
        var rightSlot = ownerFrame.PhysicalSlot(right.I);
        static bool Nullable(NullableRepresentationFrame.Role role) => role is
            NullableRepresentationFrame.Role.Nullable or NullableRepresentationFrame.Role.NullableStorage or NullableRepresentationFrame.Role.NullableElement;
        return leftSlot.SourceIndex == rightSlot.SourceIndex
            && Nullable(leftSlot.Representation) == Nullable(rightSlot.Representation);
    }

    // Ordinary Kotlin functions have one runtime carrier. Their signature
    // discriminator can nevertheless mention different representations of the
    // same source variable. A selected override needs a separate signature row
    // in that case, not an invariant CLR generic conversion. Nominal delegates
    // and constructed non-function types do not share this rule.
    internal static bool SameSourceFunctionRepresentation(TypeNode slot, TypeNode declared,
        NullableRepresentationFrame ownerFrame)
    {
        bool Same(TypeNode left, TypeNode right) => Equals(left, right)
            || SameSourceRepresentationVariables(left, right, ownerFrame)
            || SameSourceFunctionRepresentation(left, right, ownerFrame);
        return slot is TypeNode.Fn { Clr: null } left && declared is TypeNode.Fn { Clr: null } right
            && left.Suspend == right.Suspend && left.Params.Length == right.Params.Length
            && Same(left.Ret, right.Ret) && left.Params.Zip(right.Params).All(pair => Same(pair.First, pair.Second))
            && (left.Recv == null && right.Recv == null
                || left.Recv != null && right.Recv != null && Same(left.Recv, right.Recv))
            && (left.Ctx?.Length ?? 0) == (right.Ctx?.Length ?? 0)
            && (left.Ctx ?? Array.Empty<TypeNode>()).Zip(right.Ctx ?? Array.Empty<TypeNode>())
                .All(pair => Same(pair.First, pair.Second));
    }

    // Reference metadata owns only referenced Kotlin declarations. A current-compilation owner is a Kotlin
    // signature-plan participant too, even though it cannot appear in that reference-only index.
    static bool IsNativeSlotOwner(string name, IReadOnlyDictionary<string, Def> definitions,
        Func<string, bool> isReferencedKotlinOwner) =>
        !definitions.ContainsKey(name) && !isReferencedKotlinOwner(name);



    static bool IsNullableValueSlot(TypeNode slot, TypeNode declared, ReferenceMetadataIndex refs,
        ValueTypeOracle isValue, bool returnPosition) =>
        declared is TypeNode.Nullable nullable
        // A nullable reference annotation (including a function carrier) is not System.Nullable<T>.
        // Ask the shared representation rule before authoring its construction or extraction.
        && BirTypeLowering.LowerPhysicalType(declared, refs?.Aliases, isValue,
            refs?.PhysicalTypeNames, typeArg: false, nullableFrames: refs?.NullableTypeFrames) is TypeNode.Nullable
        && BirTypeLowering.SamePhysicalSlotType(slot, nullable.Of, refs?.Aliases, isValue,
            refs?.PhysicalTypeNames, returnPosition, nullableFrames: refs?.NullableTypeFrames);

    static bool IsVoid(TypeNode type) =>
        type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null };

    static bool IsUnit(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.Unit", Args: null };

    // Nullable and oblivious Unit still occupy the same CLR reference-valued slot. A void Kotlin body
    // implementing either slot must supply the Unit value just as it does for a closed generic Unit slot.
    // Keep this distinct from IsUnit: a source Unit? declaration must never be classified as a void body.
    static bool IsUnitValueSlot(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => IsUnitValueSlot(nullable.Of),
        TypeNode.Oblivious oblivious => IsUnitValueSlot(oblivious.Of),
        _ => IsUnit(type),
    };

    // WHICH PASS OWNS A DIVERGENT SLOT. `CovariantInterfaceReturnBridge` bridges a return the override narrowed, and
    // it runs first; this erasure narrows returns too, so without a boundary both fire on one slot and emit two
    // private bridges with the SAME signature and the SAME MethodImpl descriptor — and the emitter, taking the first
    // match, silently picks between them. The boundary is the divergence itself: a difference this erasure created
    // belongs to this pass, whose bridge forwards VIRTUALLY (so a further-derived override is what runs) where the
    // covariant one deliberately does not.
    public static bool IsErasureDivergence(TypeNode slot, TypeNode declared) =>
        slot != null && declared != null && !slot.Equals(declared)
        && (ErasureAligned(slot, declared) || IsCompanionScalarSeam(slot, declared));

    // True iff `slot` is `declared` with a bare `object` at some positions and nothing else changed — i.e. the two
    // differ ONLY where the base declaration was object-erased. Every other divergence, at any depth, says the slot
    // and the declaration part company for a reason that is not this erasure.
    static bool ErasureAligned(TypeNode slot, TypeNode declared)
    {
        if (IsBareObject(slot) || slot.Equals(declared)) return true;
        switch (slot, declared)
        {
            case (TypeNode.Fqn { Args: { } sa } sf, TypeNode.Fqn { Args: { } da } df)
                when sf.Name == df.Name && sa.Length == da.Length:
                return !sa.Where((s, i) => !ErasureAligned(s, da[i])).Any();
            case (TypeNode.Array s, TypeNode.Array d): return ErasureAligned(s.Elem, d.Elem);
            case (TypeNode.Nullable s, TypeNode.Nullable d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.Oblivious s, TypeNode.Oblivious d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.ByRef s, TypeNode.ByRef d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.Fn s, TypeNode.Fn d)
                when s.Suspend == d.Suspend && s.Params.Length == d.Params.Length && (s.Recv == null) == (d.Recv == null):
                return ErasureAligned(s.Ret, d.Ret)
                       && !s.Params.Where((p, i) => !ErasureAligned(p, d.Params[i])).Any()
                       && (s.Recv == null || ErasureAligned(s.Recv, d.Recv));
            default: return false;
        }
    }

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object" or "System.Object", Args: null }
        || t is TypeNode.Nullable { Of: TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any", Args: null } };

    // True iff BirTypeLowering will make this type the bare `object` the erased slot already is. A reference `T?` is
    // stripped to its bare inner, so the question recurses through the wrapper; a VALUE `T?` stays `Nullable<V>`, and
    // a type variable stays `!T`.
    static bool LowersToObject(TypeNode t, ReferenceMetadataIndex refs = null)
    {
        return t switch {
        TypeNode.Oblivious o => LowersToObject(o.Of, refs),
        TypeNode.Nullable n => LowersToObject(n.Of, refs),
        TypeNode.Fn f => f.Suspend,
        TypeNode.Fqn { Args: null } f => f.Name is "object" or "System.Object" or "kotlin.Any"
            || refs?.Aliases.TryGetValue(f.Name, out var alias) == true && alias is "object" or "System.Object",
        _ => false,
        };
    }

    // The class's own declaration that fills this slot. Two independent proofs are accepted, and both are needed:
    // the Kotlin `overrides` fact names the supertype the author wrote (`Sink`), which is what makes the match safe;
    // and a slot may be reached through a supertype the author never named (`Sink`'s own base interfaces, including
    // a synthesized existential view of `Sink`), so an ANCESTOR of an overridden owner counts too. An


    internal static bool SameMethodTypeParameterShape(IReadOnlyList<BirTypeParameter> slotTypeParams, IReadOnlyList<BirTypeParameter> implementationTypeParams,
        TypeNode[] slotOwnerArgs, TypeNode[] implementationOwnerArgs, Func<TypeNode, TypeNode> constraintType = null)
        => MethodTypeParameterShapeKey(slotTypeParams, slotOwnerArgs, constraintType)
            == MethodTypeParameterShapeKey(implementationTypeParams, implementationOwnerArgs, constraintType);

    internal static bool SameSourceMethodTypeParameterShape(BirTypeParameter[] slotTypeParams,
        BirTypeParameter[] implementationTypeParams, TypeNode[] slotOwnerArgs, TypeNode[] implementationOwnerArgs)
        => MethodTypeParameterShapeKey(slotTypeParams, slotOwnerArgs)
            == MethodTypeParameterShapeKey(implementationTypeParams, implementationOwnerArgs);


    internal static string MethodTypeParameterShapeKey(IReadOnlyList<BirTypeParameter> typeParams, TypeNode[] ownerArgs,
        Func<TypeNode, TypeNode> constraintType = null)
        => ConstraintShapeKey(typeParams?.Select(parameter => (
            Constraints: (IEnumerable<TypeNode>)parameter.Constraints.OrEmpty(),
            Specials: (IEnumerable<string>)parameter.SpecialConstraints.OrEmpty())), ownerArgs, constraintType);

    static string ConstraintShapeKey(IEnumerable<(IEnumerable<TypeNode> Constraints, IEnumerable<string> Specials)> parameters,
        TypeNode[] ownerArgs, Func<TypeNode, TypeNode> constraintType = null)
    {
        if (parameters == null) return "0";
        var shapes = new List<string>();
        foreach (var parameter in parameters)
        {
            var constraints = parameter.Constraints
                .Select(constraint => SupertypeGraph.SubstOwnerTvs(constraint, ownerArgs ?? Array.Empty<TypeNode>()))
                .Select(constraint => SupertypeGraph.TypeKey(constraintType?.Invoke(constraint) ?? constraint))
                .OrderBy(key => key, StringComparer.Ordinal);
            var specials = parameter.Specials.OrderBy(value => value, StringComparer.Ordinal);
            shapes.Add("{" + string.Join(",", constraints) + "}|{" + string.Join(",", specials) + "}");
        }
        return shapes.Count == 0 ? "0" : shapes.Count + ":" + string.Join(";", shapes);
    }

    // True iff this method declares itself an override of `supName`'s member — directly, or of some owner that
    // `supName` is a supertype of (a redeclared slot on a base interface is a DISTINCT CLR slot with the same shape).

    internal static string SourceDeclarationId(string declarationId)
    {
        if (declarationId == null) return null;
        var physicalOnly = declarationId.IndexOf(
            DeclarationIdentityBinding.PhysicalOnlySuffix, StringComparison.Ordinal);
        if (physicalOnly >= 0) return declarationId[..physicalOnly];
        const string coldSuffix = "|cold";
        return declarationId.EndsWith(coldSuffix, StringComparison.Ordinal)
            ? declarationId[..^coldSuffix.Length]
            : declarationId;
    }

    static bool IsAncestor(IReadOnlyDictionary<string, Def> defs, string ancestor, string of)
    {
        var queue = new Queue<string>();
        queue.Enqueue(of);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (!seen.Add(n)) continue;
            if (n == ancestor) return true;
            if (!defs.TryGetValue(n, out var d)) continue;
            foreach (var i in d.Interfaces) queue.Enqueue(i.Name);
            if (d.Base != null) queue.Enqueue(d.Base.Name);
        }
        return false;
    }

    internal static Def DeclaredFaces(Def owner, string carrier) => new() {
        Interfaces = carrier == null ? owner.Interfaces : owner.Interfaces.Where(face => face.Name != carrier).ToArray()
    };

    static bool ReachesFromDeclaredInterface(Def cls, TypeNode.Fqn slotOwner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs)
    {
        foreach (var direct in cls.Interfaces)
            if (SupertypeGraph.Reaches(direct, slotOwner, defs, refs))
                return true;
        return false;
    }

    internal static bool ReachesFromDeclaredPhysicalInterface(Def cls, TypeNode.Fqn slotOwner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        IReadOnlySet<string> localTypeNames)
    {
        // A referenced graph is already in CLR vocabulary, while the selected owner may still carry Kotlin type
        // arguments. Compare both physical projections, retaining exact construction and excluding the base chain.
        TypeNode Physical(TypeNode type) => ClrMemberResolution.MethodImplComparisonType(
            BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false, localTypeNames,
                nullableFrames: refs.NullableTypeFrames), refs);
        var physicalSlot = Physical(slotOwner);
        return SupertypeGraph.Reachable(new Def { Interfaces = cls.Interfaces }, defs, refs)
            .Any(edge => edge.isInterface && Equals(Physical(edge.spec), physicalSlot));
    }


    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    internal static bool HasNativeBaseMapping(Def owner, IReadOnlyDictionary<string, Def> defs,
        ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> physical, TypeNode.Fqn slot, string member,
        int arity, TypeNode[] parameters, TypeNode result, System.Reflection.MethodInfo selected = null)
    {
        if (owner.Base == null || physical(slot) is not TypeNode.Fqn face) return false;
        var signature = parameters.Select(physical).ToArray();
        var ret = physical(result);
        return SupertypeGraph.ReachableFromClass(owner.Base, defs, refs).Any(edge => !edge.isInterface
            && !defs.ContainsKey(edge.spec.Name) && physical(edge.spec) is TypeNode.Fqn nativeBase
            && (refs.DeclaresExplicitInterfaceSlot(nativeBase, face, member, arity, signature, ret)
                || selected != null && refs.MapsPublicInterfaceMethod(nativeBase, face, selected)));
    }

    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
