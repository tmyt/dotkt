using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    enum Phase { DeclarationMoves, SuspendValueBridges, PhysicalBridges }

    // The ordinary erasure halves run at different points because their inputs are valid at different points.
    // Suspend Unit result adapters have an additional pre-cold phase: the generic slot's value obligation must
    // survive Task projection, while the ordinary late half still owns each original declaration's cold slots.
    //
    // The DECLARATION MOVE (a nested position no cast reaches) must land before the erasure sweep and the use-side
    // realign: those are what retype the body that reads the moved parameter, and a slot moved after them leaves the
    // body reading it at the type it used to have — verifiably wrong IL.
    //
    // The BRIDGE must land after the star-projection erasure, because that pass ADDS supertypes: a `G<*>` anywhere in
    // the compilation gives `G` a synthesized existential view whose slots an implementer must fill too, and a
    // bridge built before it exists would carry a MethodImpl for the constructed interface and leave the existential
    // one unimplemented. Nothing between the two points can introduce a new nested move — an existential view's slots
    // are copies of the erased ones — so the halves do not race.
    //
    // Adapt differing suspend results before they become invariant Task<T> signatures. This includes Unit in a
    // value slot and a covariant result supplied by an inherited implementation. Cold lowering drives a Task of the
    // slot's result from the selected implementation's cold call; it must not cast the public Task result.
    public static void PrepareSuspendValueBridges(IEnumerable<JsonNode> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeNames, bool refBuild, GenericRepresentationPolicy representations,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots,
        IReadOnlyDictionary<JsonObject, TypeNode[]> sourceParameters) =>
        ApplyAll(roots, isValue, refs, representations, Phase.SuspendValueBridges, localTypeNames, covariantBridgedSlots, refBuild, sourceParameters);

    internal static IReadOnlyDictionary<JsonObject, TypeNode[]> CaptureSourceParameters(IEnumerable<JsonNode> roots)
    {
        var facts = new Dictionary<JsonObject, TypeNode[]>(ReferenceEqualityComparer.Instance);
        foreach (var definition in SupertypeGraph.Collect(roots).Values)
            foreach (var method in definition.Methods.OfType<JsonObject>())
                facts[method] = ReadParameterTypes(method);
        return facts;
    }

    internal static TypeNode[] ReadParameterTypes(JsonObject method) =>
        (method["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(parameter => TypeJson.Read(parameter["type"])).ToArray() ?? Array.Empty<TypeNode>();

    // The bridge half.
    public static void ApplyAll(IEnumerable<JsonNode> roots, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypeNames, bool refBuild, GenericRepresentationPolicy representations,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots = null,
        IReadOnlyDictionary<string, string> localCarriers = null) =>
        ApplyAll(roots, isValue, refs, representations, Phase.PhysicalBridges, localTypeNames, covariantBridgedSlots,
            refBuild, localCarriers: localCarriers);

    static void ApplyAll(IEnumerable<JsonNode> roots, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        GenericRepresentationPolicy representations, Phase phase, IReadOnlySet<string> localTypeNames,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots = null, bool refBuild = false,
        IReadOnlyDictionary<JsonObject, TypeNode[]> sourceParameters = null,
        IReadOnlyDictionary<string, string> localCarriers = null)
    {
        var rootList = roots.ToArray();
        var defs = SupertypeGraph.Collect(rootList);
        IReadOnlyDictionary<string, JsonObject> assignabilityDefinitions = null;
        bool CanForwardPhysicalValue(TypeNode source, TypeNode target) => FBoundStarProjectionErasure.IsClrAssignable(
            source, target, assignabilityDefinitions ??= defs.ToDictionary(pair => pair.Key, pair => pair.Value.Node,
                StringComparer.Ordinal), refs, physicalTypes: true);
        bool CanForwardReturn(TypeNode source, TypeNode target) => FBoundStarProjectionErasure.IsClrAssignable(
            source, target, assignabilityDefinitions ??= defs.ToDictionary(pair => pair.Key, pair => pair.Value.Node,
                StringComparer.Ordinal), refs, physicalTypes: true, allowBoxing: true);
        bool CarrierParameterSeam(TypeNode left, TypeNode right)
        {
            TypeNode Core(TypeNode type) => type switch {
                TypeNode.Nullable nullable => Core(nullable.Of),
                TypeNode.Oblivious oblivious => Core(oblivious.Of),
                _ => type,
            };
            bool Matches(TypeNode exact, TypeNode view) => Core(exact) is TypeNode.Fqn { Args: { Length: > 0 } } construction
                && Core(view) is TypeNode.Fqn carrier
                && (localCarriers?.TryGetValue(construction.Name, out var local) == true && local == carrier.Name
                    || refs.TryExistentialPhysicalOwner(construction.Name, out var referenced) && referenced == carrier.Name);
            return phase == Phase.PhysicalBridges && (Matches(left, right) || Matches(right, left));
        }
        var frames = refs.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var def in defs.Values)
            if (KotlinSupertypesRecord.ReadNullableFrame(def.Node) is { } frame) frames[def.Name] = frame;
        // Override markers retain Kotlin applications even after the live graph uses declaration order.
        var sourceApplications = new InnerApplicationFrames(rootList, refs)
            .Project(frames, retainPhysicalOrder: true);
        NullableRepresentationTypes SourceMapping(Def def) => new(
            KotlinSupertypesRecord.ReadNullableFrame(def.Node), null, sourceApplications, isValue, policy: representations);
        var annotationLocalTypes = localTypeNames ?? defs.Keys.ToHashSet(StringComparer.Ordinal);
        TypeNode[] AnnotationArguments(TypeNode.Fqn type) =>
            BirTypeLowering.AnnotationArguments(type, refs.Aliases, isValue, refs.PhysicalTypeNames,
                annotationLocalTypes, refBuild, refs.NullableTypeFrames);
        // The source accessor relation is needed only between the two halves of this one pass. Keep it in memory by
        // JsonObject identity rather than minting another BIR/CIR identifier or parsing the bridge's metadata
        // association. The relation cannot escape this ApplyAll invocation.
        var exactBridgeSources = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        // An earlier physical-slot owner (notably CovariantInterfaceReturnBridge) can already have authored an exact
        // interface property bridge. Its carrier explicitly preserves the selected source association; seed this
        // pass-local hand-off from that fact so inherited-DIM collision repair can consume the bridge without asking
        // this pass to synthesize a duplicate or reconstructing the property relation from CLR names.
        foreach (var method in defs.Values.Where(def => def.Kind == "interface")
                     .SelectMany(def => def.Methods.OfType<JsonObject>()))
            if (Bool(method[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey])
                && Str((method[KotlinPropertyAccessors.MetadataCarrierKey] as JsonObject)?["sourceAssociation"])
                    is string sourceAssociation)
                exactBridgeSources[method] = sourceAssociation;
        foreach (var cls in defs.Values.Where(d => d.Kind is "class" or "interface").ToList())
            ApplyClass(cls, defs, isValue, refs, phase, refBuild, exactBridgeSources, localTypeNames,
                covariantBridgedSlots, AnnotationArguments, SourceMapping(cls), sourceParameters, representations,
                CarrierParameterSeam, CanForwardPhysicalValue, CanForwardReturn);
        // A class-level inherited-DIM bridge consumes the exact MethodImpl descriptor synthesized on its interface.
        // Declarations may appear in either order and in different input files, so first finish every interface/class's
        // own slot allocation above, then inspect classes. Reading the live method arrays during the first loop would
        // make correctness depend on source/file order.
        if (phase == Phase.PhysicalBridges)
            foreach (var cls in defs.Values.Where(d => d.Kind == "class").ToList())
            {
                if (cls.Node["methods"] is not JsonArray methods) continue;
                var inheritedBridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                var ordinal = 0;
                AddInheritedDefaultPropertyBridges(cls, defs, methods, ClassOwnArgs(cls), refs, isValue,
                    exactBridgeSources, inheritedBridges, ref ordinal, localTypeNames);
                AddInheritedDefaultMethodBridges(cls, defs, methods, ClassOwnArgs(cls), refs, isValue,
                    exactBridgeSources, inheritedBridges, ref ordinal, localTypeNames);
            }
        if (phase == Phase.PhysicalBridges)
            foreach (var method in defs.Values.SelectMany(def => def.Methods.OfType<JsonObject>()))
                method.Remove(KotlinPropertyAccessors.SuspendTaskOnlyBridgeKey);
    }

    static TypeNode DeclaredSlot(TypeNode type, ValueTypeOracle isValue, bool physical) =>
        physical ? type : NullableGenericErasure.EraseNullableTv(type, isValue);

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, Phase phase, bool refBuild, IDictionary<JsonObject, string> exactBridgeSources,
        IReadOnlySet<string> localTypeNames,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots,
        Func<TypeNode.Fqn, TypeNode[]> annotationArguments, NullableRepresentationTypes sourceMapping,
        IReadOnlyDictionary<JsonObject, TypeNode[]> sourceParameters, GenericRepresentationPolicy representations,
        Func<TypeNode, TypeNode, bool> CarrierParameterSeam, Func<TypeNode, TypeNode, bool> CanForwardPhysicalValue,
        Func<TypeNode, TypeNode, bool> CanForwardReturn)
    {
        if (cls.Node["methods"] is not JsonArray methods) return;
        var ownArgs = ClassOwnArgs(cls);
        var bridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var claimedClassSlots = new Dictionary<(JsonObject Body, string Shape), List<JsonObject>>();
        var ordinal = 0;
        var candidates = methods.OfType<JsonObject>().ToList();
        var inheritedOwners = new Dictionary<JsonObject, TypeNode.Fqn>(ReferenceEqualityComparer.Instance);
        var inheritedSignatures = new Dictionary<JsonObject, TypeNode[]>(ReferenceEqualityComparer.Instance);
        var enumerationSlots = !refBuild && phase == Phase.PhysicalBridges
            ? ReverseEnumeratorBridgeSynthesis.ReserveSlots(cls, defs, refs) : null;
        TypeNode PhysicalSlotType(TypeNode type) => BirTypeLowering.LowerPhysicalType(
            type, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false,
            localTypeNames, nullableFrames: refs.NullableTypeFrames);
        bool CanPassAsObject(TypeNode type, JsonObject method)
        {
            type = SignatureValueTypes.Of(PhysicalSlotType(type));
            if (type is TypeNode.Nullable nullable) return CanPassAsObject(nullable.Of, method);
            if (type is TypeNode.Oblivious oblivious) return CanPassAsObject(oblivious.Of, method);
            if (type is TypeNode.Fqn named) return !refs.IsByRefLikeFqn(named);
            if (type is TypeNode.Tv variable)
            {
                var parameters = variable.Scope == "type" ? TypeParameterFrame.Declarations(cls.Node).ToArray()
                    : (method["typeParams"] as JsonArray ?? new JsonArray()).ToArray();
                return variable.I >= 0 && variable.I < parameters.Length
                    && !((parameters[variable.I] as JsonObject)?["specialConstraints"] as JsonArray ?? new JsonArray())
                        .Select(Str).Contains("allowsRefStruct");
            }
            return type is TypeNode.Array or TypeNode.Fn;
        }
        bool CanForwardClassReturn(TypeNode source, TypeNode target, JsonObject implementation)
        {
            if (LowersToObject(source, refs)
                && Str(implementation["retKotlinType"]) is string encodedReturn
                && TypeNode.Parse(encodedReturn) is TypeNode.Tv semanticReturn)
            {
                var frame = semanticReturn.Scope == "method"
                    ? NullableRepresentationTypes.DeclarationMethodFrame(implementation)
                    : sourceMapping.OwnerFrame;
                source = frame?.Variable(semanticReturn, NullableRepresentationFrame.Role.Ordinary)
                    ?? semanticReturn;
            }
            var seen = new HashSet<TypeNode.Tv>();
            bool Prove(TypeNode value)
            {
                if (!CanPassAsObject(value, implementation)) return false;
                if (CanForwardReturn(value, target)) return true;
                if (value is not TypeNode.Tv variable || !seen.Add(variable)) return false;
                var parameters = variable.Scope == "type" ? TypeParameterFrame.Declarations(cls.Node).ToArray()
                    : (implementation["typeParams"] as JsonArray ?? new JsonArray()).ToArray();
                return variable.I >= 0 && variable.I < parameters.Length
                    && ((parameters[variable.I] as JsonObject)?["constraints"] as JsonArray ?? new JsonArray())
                        .Select(TypeJson.Read).Where(bound => bound != null).Any(bound => Prove(PhysicalSlotType(bound)));
            }
            return Prove(source);
        }
        CollectInheritedClassMethods(cls, defs, refs, isValue, candidates, inheritedOwners, inheritedSignatures,
            phase == Phase.SuspendValueBridges);

        // EVERY EARLY EXIT BELOW MEANS "THIS SUPERTYPE OR SLOT IS NOT ONE THIS ERASURE DIVERGED", never "give up on a
        // slot that needs filling". A supertype absent from `defs` is declared elsewhere and goes to the referenced
        // arm rather than being dropped; an arity that does not match its spec, or a slot whose types do not read, is
        // a declaration this pass has no opinion about and leaves exactly as the other passes found it. The one
        // judgement call is `Implementer` returning null on an ambiguous overload set, and it is documented there.
        //
        // The shared tail of both arms: given a SLOT (its constructed owner, its name, its erased parameter vector and
        // return) and the class declaration that claims to fill it, decide per position whether the declaration is
        // already the slot, is one `object` seam away from it (bridge), or has to adopt it (rewrite).
        void Fill(TypeNode.Fqn semanticSpec, TypeNode.Fqn descriptorSpec,
            bool supIsInterface, bool referencedSlot, bool interfaceSlotHasDefault,
            string identityName, string descriptorMember, string propertyAccessor,
            TypeNode[] slotParams, TypeNode slotRet, JsonObject impl, JsonArray slotTypeParams = null,
            bool unitValueReturn = false, TypeNode[] slotSignature = null, string referenceMember = null,
            TypeNode semanticSlotReturn = null, JsonObject selectedLocalSlot = null,
            string selectedReferencedDeclarationId = null)
        {
            if (supIsInterface && enumerationSlots?.Owns(descriptorSpec, descriptorMember,
                    (impl["typeParams"] as JsonArray)?.Count ?? 0, slotParams, slotRet, PhysicalSlotType, refs) == true)
                return;
            inheritedOwners.TryGetValue(impl, out var inheritedOwner);
            if (inheritedOwner != null && !supIsInterface) return;

            // An abstract interface slot reached only through a base class already has that base's mapping. A derived
            // declaration does not acquire a fresh MethodImpl unless the source class re-lists the interface; this is
            // what keeps an inherited CLR explicit implementation intact. A DEFAULT interface slot is different:
            // Kotlin lets a grandchild override the DIM without re-listing the interface, and CLR needs an exact
            // per-type MethodImpl for that override. `semanticSpec` deliberately remains in Kotlin vocabulary:
            // `descriptorSpec` may already be the physical owner of @ClrTypeAlias.
            var reimplementsInterface = supIsInterface && cls.Kind == "class"
                && (ReachesFromDeclaredInterface(cls, semanticSpec, defs, refs)
                    || referencedSlot && ReachesFromDeclaredPhysicalInterface(cls, descriptorSpec, defs, refs,
                        isValue, localTypeNames));
            var overridesInheritedDefault = supIsInterface && cls.Kind == "class"
                && !reimplementsInterface && interfaceSlotHasDefault;
            if (supIsInterface && cls.Kind == "class"
                && !reimplementsInterface && !overridesInheritedDefault)
                return;

            var methodView = new OverrideMethod(impl);
            var assessment = AssessFit(methodView, ownArgs, slotParams, slotRet, unitValueReturn, slotSignature,
                supIsInterface, referencedSlot, descriptorSpec, propertyAccessor, phase, inheritedOwner != null,
                isValue, refs, localTypeNames, sourceMapping, PhysicalSlotType, CarrierParameterSeam,
                CanForwardPhysicalValue, type => CanPassAsObject(type, impl),
                (source, target) => CanForwardClassReturn(source, target, impl),
                () => HasExistingSlotBridge(methods, supIsInterface ? "clrInterfaceImpls" : "clrBaseImpls",
                    descriptorSpec, descriptorMember, (impl["typeParams"] as JsonArray)?.Count ?? 0,
                    slotParams, slotRet, SubstituteOwnerTypeParameterConstraints(slotTypeParams,
                        semanticSpec.Args ?? Array.Empty<TypeNode>()), refs, isValue),
                () => covariantBridgedSlots?.Contains(CovariantInterfaceReturnBridge.BridgedSlotKey(
                    impl, descriptorSpec, descriptorMember, (impl["typeParams"] as JsonArray)?.Count ?? 0,
                    slotParams, slotRet, refs, isValue)) == true);
            if (assessment == null) return;
            var fit = assessment.Parameters;
            var retFit = assessment.Return;
            var declParams = impl["params"] as JsonArray;
            var declRet = TypeJson.Read(impl["ret"]);

            ApplyDeclarationEdits(methodView, ownArgs, slotParams, slotRet, assessment, phase, isValue, annotationArguments);

            // A Kotlin accessor keeps its dedicated physical name even when it implements a property imported from a
            // CLR interface whose slot uses the ordinary get_/set_ convention. With an otherwise-identical signature,
            // the accessor itself is the MethodImpl body: the exact descriptor names the external slot, while direct
            // Kotlin calls and the class's Property row continue to name the dedicated accessor. Ordinary functions are
            // unaffected and may independently fill a same-named slot on another interface.
            var needsSignatureBridge = fit.Contains(Fit.Bridge) || retFit == Fit.Bridge;
            var needsExplicitPropertySlot = supIsInterface && propertyAccessor != null
                && descriptorMember != Str(impl["name"])
                && (cls.Kind != "class" || reimplementsInterface || overridesInheritedDefault);
            // A concrete method declared on a derived CLR interface is a fresh NewSlot even when its name and
            // signature equal the base declaration. The frontend override marker says which Kotlin declaration it
            // overrides; realize that decision here as an exact private/final MethodImpl bridge. Leaving it to ilemit
            // would force the emitter to rediscover override meaning from names, bodies, and hierarchy order.
            var needsExactInterfaceSlot = supIsInterface &&
                (inheritedOwner != null
                    || declParams.OfType<JsonObject>().Any(parameter => parameter[FunctionSignatureIdentity.Key] != null)
                    || cls.Kind == "interface" && !Bool(impl["abstract"])
                    || cls.Kind == "class" && reimplementsInterface
                        && descriptorMember != Str(impl["name"])
                    || cls.Kind == "class" && overridesInheritedDefault);
            // A local base can have adopted a CLR name while its selected source override kept its own name.
            // Equal parameter/return types do not bind those differently named MethodDefs without a MethodImpl.
            var needsExactLocalBaseSlot = !supIsInterface && !referencedSlot
                && descriptorMember != Str(impl["name"]);
            var needsExplicitSlot = needsExplicitPropertySlot || needsExactInterfaceSlot || needsExactLocalBaseSlot;
            var constructedSlotTypeParams = SubstituteOwnerTypeParameterConstraints(
                slotTypeParams, semanticSpec.Args ?? Array.Empty<TypeNode>());
            if (phase == Phase.PhysicalBridges && !needsSignatureBridge && cls.Kind == "class"
                && inheritedOwner == null && IsNativeSlotOwner(descriptorSpec.Name, defs, refs.HasDotKtOwner))
                DeclarationSignatureIdentity.RecordSelectedPhysicalSlot(impl,
                    ImplDescriptor(descriptorSpec, descriptorMember, (impl["typeParams"] as JsonArray)?.Count ?? 0,
                        slotSignature ?? slotParams, slotRet, constructedSlotTypeParams, unitValueReturn), supIsInterface);

            if (!needsSignatureBridge && !needsExplicitSlot)
                return;
            if (!supIsInterface)
            {
                var classShape = descriptorMember + "`" + ((impl["typeParams"] as JsonArray)?.Count ?? 0)
                    + "(" + string.Join(",", (slotSignature ?? slotParams).Select(type =>
                        SupertypeGraph.TypeKey(BirTypeLowering.CanonicalPhysicalSlotType(PhysicalSlotType(type))))) + ")->"
                    + SupertypeGraph.TypeKey(BirTypeLowering.CanonicalPhysicalSlotType(PhysicalSlotType(slotRet)))
                    + MethodTypeParameterShapeKey(constructedSlotTypeParams, ownArgs,
                        type => BirTypeLowering.CanonicalPhysicalSlotType(PhysicalSlotType(type)))
                    + (unitValueReturn ? "[value-return]" : "");
                var claim = (impl, classShape);
                var selectedClassDeclarationId = selectedReferencedDeclarationId
                    ?? Str(selectedLocalSlot?[DeclarationIdentityBinding.Key]);
                // A nearer class declaration already reuses its overridden ancestor's physical slot. Two distinct
                // MethodImpl bodies for those two names would override the same CLR virtual slot twice. Require
                // both the exact physical signature and the frontend's override chain; interfaces remain distinct.
                if (claimedClassSlots.TryGetValue(claim, out var declarations) && selectedClassDeclarationId != null
                    && declarations.Any(declaration => OverridesInto(declaration, defs, semanticSpec.Name,
                        identityName, propertyAccessor switch { "get" => "getter", "set" => "setter", _ => "method" },
                        selectedClassDeclarationId)))
                    return;
                if (selectedLocalSlot != null)
                {
                    if (declarations == null) claimedClassSlots[claim] = declarations = new List<JsonObject>();
                    declarations.Add(selectedLocalSlot);
                }
            }
            // An inherited candidate is a signature fact, not a MethodDef on this class. It needs a real forwarding
            // body even for an exact-signature MethodImpl; attaching the descriptor to the fact would discard it.
            if (!needsSignatureBridge && cls.Kind != "interface" && inheritedOwner == null)
            {
                impl["virtual"] = true;
                var descriptor = ImplDescriptor(descriptorSpec, descriptorMember,
                    (impl["typeParams"] as JsonArray)?.Count ?? 0, slotSignature ?? slotParams, slotRet,
                    constructedSlotTypeParams, unitValueReturn);
                for (var i = 0; i < declParams.Count; i++)
                    if (slotSignature == null && declParams[i]?[FunctionSignatureIdentity.Key] is JsonNode discriminator)
                        descriptor["params"][i] = TypeJson.Write(new TypeNode.Mod(false,
                            SupertypeGraph.SubstOwnerTvs(
                                FunctionSignatureIdentity.DeclarationType(TypeJson.Read(discriminator)), ownArgs), slotParams[i]));
                AddImplDescriptor(impl, supIsInterface ? "clrInterfaceImpls" : "clrBaseImpls", descriptor);
                // Late declaration-signature allocation can add modifiers to this authored body after an
                // exact local slot was attached. Preserve the selected declaration ID so that pass can
                // retarget the relation to an exact forwarding bridge, including compiler-generated slots.
                if (selectedLocalSlot != null)
                    DeclarationSignatureIdentity.RecordSelectedImplementationSlot(impl, selectedLocalSlot);
                return;
            }
            // A CLR interface can implement a base-interface slot only through a FINAL MethodImpl body. Keep the
            // authored DIM public/overridable and synthesize the explicit-implementation shape. An abstract accessor
            // has no body to forward to; its eventual implementing class receives the descriptor instead.
            if (cls.Kind == "interface" && Bool(impl["abstract"]))
                return;

            // The typed body is what the bridge dispatches to, so it must own a virtual slot of its own. Kotlin
            // does not require `open` to satisfy an interface, and the exact-signature normalization that would
            // otherwise mark it no longer matches — the signatures deliberately differ now.
            impl["virtual"] = true;

            // ARITY IS PART OF THE IDENTITY (ECMA-335 I.8.6.1.6), so it is part of the key. `put(T?)` and
            // `<U> put(T?)` erase to one parameter vector and are two CLR slots; one bridge shared between them is
            // wired to both, and the CLR rejects the pair ("Signature of the body and declaration in a method
            // implementation do not match"). The DESCRIPTOR carries it too, so the emitter matches on it rather than
            // taking the arity from whichever slot it is currently looking at.
            var arity = (impl["typeParams"] as JsonArray)?.Count ?? 0;
            // THE BODY IS PART OF THE IDENTITY TOO. Two slots with the same ERASED signature can be filled by two
            // DIFFERENT declarations — `class Two : A1<Int>, B1<String>` has two `accept` overloads that both erase
            // to `accept(object)` — and one bridge shared between them forwards both slots to whichever body it was
            // built for, casting a `string` into a `Nullable<int32>` at run time. Keyed by the declaration it
            // forwards to as well, the two slots get one bridge each, while the sharing that IS correct — one body
            // reached through several supertypes of the same shape — still collapses to one.
            var body = string.Join(",", (impl["params"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().Select(pn => TypeJson.Read(pn["type"]) is TypeNode t ? SupertypeGraph.TypeKey(t) : "?"));
            string PhysicalSlotKey(TypeNode type) => SupertypeGraph.TypeKey(BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false,
                localTypeNames, nullableFrames: refs.NullableTypeFrames));
            // The same CLR obligation may be reached through both a source-metadata edge and a projected
            // reference edge. Bridge identity uses its physical slot, not the spelling of either path.
            var key = identityName + "`" + arity + "(" + string.Join(",", (slotSignature ?? slotParams).Select(PhysicalSlotKey)) + ")->" + PhysicalSlotKey(slotRet)
                      + "{" + Str(impl["name"]) + "<"
                      + MethodTypeParameterShapeKey(impl["typeParams"] as JsonArray, ownArgs)
                      + ">(" + body + ")}";
            if (inheritedOwner != null) key += "[body-owner:" + SupertypeGraph.TypeKey(inheritedOwner) + "]";
            // A private-final MethodDef used as an interface MethodImpl body belongs to one exact declaration slot.
            // Sharing that final body across an inherited chain (I::m and I.Base::m) makes the derived interface's
            // MethodImpl graph invalid on CoreCLR even though both descriptors forward to the same Kotlin declaration.
            // Classes may validly map one body to several independent interface slots, so isolate only this explicit-
            // interface-implementation shape by the complete declaration owner/member identity.
            if (cls.Kind == "interface")
                key += "[slot:" + SupertypeGraph.TypeKey(descriptorSpec) + "::" + descriptorMember + "]";
            if (unitValueReturn) key += "[value-return]";
            if (!bridges.TryGetValue(key, out var bridge))
            {
                var bridgeOrdinal = ordinal++;
                bridge = BuildBridge(cls, impl, slotParams, slotRet,
                    $"dotkt$ovslot${SafeName(identityName)}${bridgeOrdinal}", isValue, refs,
                    callOwner: inheritedOwner,
                    callMember: inheritedOwner == null ? null : Str(impl[DeclarationIdentityBinding.ExplicitNameKey]),
                    unitValueReturn: unitValueReturn, physicalImplementation: phase == Phase.PhysicalBridges);
                if (slotSignature != null && bridge["params"] is JsonArray bridgeParameters)
                    for (var i = 0; i < slotSignature.Length; i++)
                        if (slotSignature[i] is TypeNode.Mod { Req: false } signature
                            && bridgeParameters[i] is JsonObject parameter)
                            parameter[FunctionSignatureIdentity.Key] = TypeJson.Write(signature.M);
                // Cold suspend entries share an object result, but a parameter conversion still
                // needs its own MethodImpl there as well as on the public Task entry.
                if (phase == Phase.SuspendValueBridges && !fit.Contains(Fit.Bridge))
                    bridge[KotlinPropertyAccessors.SuspendTaskOnlyBridgeKey] = true;
                if (propertyAccessor == null)
                {
                    RoundtripMetadata.AddSourceMethodIdentity(bridge, identityName,
                        Str(impl[DeclarationIdentityBinding.Key]));
                    if (cls.Kind == "interface" && exactBridgeSources != null
                        && Str(impl[DeclarationIdentityBinding.Key]) is string sourceIdentity)
                        exactBridgeSources[bridge] = sourceIdentity;
                }
                bridges[key] = bridge;
                methods.Add(bridge);
                if (propertyAccessor != null)
                {
                    var sourceAssociation = Str(impl[KotlinPropertyAccessors.AssociationKey]);
                    if (!needsSignatureBridge && cls.Kind == "interface")
                    {
                        KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(bridge, identityName, propertyAccessor,
                            sourceAssociation);
                    }
                    else
                        KotlinPropertyAccessors.AssociateBridgeProperty(cls.Node, bridge, identityName, propertyAccessor,
                            sourceAssociation, slotParams, slotRet);
                    // This in-memory relation is independent of the bridge's physical signature. A signature-changing
                    // bridge is still the exact MethodImpl body selected for the source accessor's default, and the
                    // later class-level DIM collision pass must be able to consume it without comparing those unlike
                    // signatures or recovering the relation from either generated association spelling.
                    if (cls.Kind == "interface" && exactBridgeSources != null)
                        exactBridgeSources[bridge] = sourceAssociation;
                }
            }
            // Which METADATA wiring fills the slot: an interface slot is a MethodImpl against the constructed
            // interface, a base-class slot a MethodImpl against the constructed base. ilemit consumes the
            // resolved descriptor and resolves nothing itself.
            var bridgeDescriptor = ImplDescriptor(descriptorSpec, descriptorMember, arity, slotSignature ?? slotParams, slotRet,
                constructedSlotTypeParams, unitValueReturn);
            if (referenceMember != null && referenceMember != descriptorMember)
                bridgeDescriptor["referenceMember"] = referenceMember;
            AddImplDescriptor(bridge, supIsInterface ? "clrInterfaceImpls" : "clrBaseImpls", bridgeDescriptor);
            // The bridge, not its typed forwarding target, owns this exact class slot.
            bool PendingOwnerSelectsSlot(TypeNode pending) => semanticSlotReturn == null
                ? ReferenceMetadataIndex.SourceDeclarationDescribesCall(pending, descriptorSpec)
                : pending is TypeNode.Fqn pendingSpec
                    && SupertypeGraph.Reachable(new Def { Base = pendingSpec }, defs, refs)
                        .Any(edge => !edge.isInterface
                            && ReferenceMetadataIndex.SourceDeclarationDescribesCall(edge.spec, semanticSpec));
            if (!supIsInterface
                && TypeJson.Read(impl["pendingOverrideOwner"]) is TypeNode pendingOwner
                && PendingOwnerSelectsSlot(pendingOwner)
                && (Str(impl["pendingOverrideMember"]) ?? Str(impl["name"])) == descriptorMember
                && TypeJson.Read(impl["pendingOverrideReturn"]) is TypeNode pendingReturn
                && ReferenceMetadataIndex.SourceDeclarationDescribesCall(pendingReturn, semanticSlotReturn ?? slotRet))
            {
                impl.Remove("pendingOverrideOwner");
                impl.Remove("pendingOverrideMember");
                impl.Remove("pendingOverrideReturn");
            }
        }

        var declarationViews = new OverrideViews();
        SelectSlots(declarationViews.Owner(cls), defs, declarationViews, candidates.Select(declarationViews.Method), ownArgs,
            isValue, refs, phase, refBuild, localTypeNames, sourceMapping, representations, CarrierParameterSeam,
            slot => Fill(slot.SemanticSpec, slot.DescriptorSpec, slot.IsInterface, slot.Referenced, slot.HasDefault,
                slot.IdentityName, slot.DescriptorMember, slot.PropertyAccessor, slot.Parameters, slot.Return,
                slot.Implementation.Dom, ParametersDom(slot.TypeParameters), slot.UnitValueReturn, slot.Signature,
                slot.ReferenceMember, slot.SemanticReturn, slot.SelectedLocal?.Dom, slot.SelectedReferencedDeclarationId),
            inheritedSignatures.ToDictionary(pair => declarationViews.Method(pair.Key), pair => pair.Value),
            sourceParameters?.ToDictionary(pair => declarationViews.Method(pair.Key), pair => pair.Value));

    }

    static void CollectInheritedClassMethods(Def cls, IReadOnlyDictionary<string, Def> defs,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, List<JsonObject> candidates,
        Dictionary<JsonObject, TypeNode.Fqn> inheritedOwners,
        Dictionary<JsonObject, TypeNode[]> inheritedSignatures, bool suspendValues,
        Func<JsonObject, bool> includeFact = null, IEnumerable<JsonObject> suppliedFacts = null,
        bool interfaceImplementations = false, Dictionary<JsonObject, JsonObject> selectedFacts = null,
        bool collectionQueries = false)
    {
        var facts = suppliedFacts ?? (cls.Node["inheritedClassMethods"] as JsonArray)?.OfType<JsonObject>();
        if (facts == null) return;
        var bases = SupertypeGraph.Reachable(cls, defs, refs).Where(edge => edge.isInterface == interfaceImplementations).ToList();
        foreach (var fact in facts)
        {
            if (includeFact != null && !includeFact(fact)) continue;
            if (fact[KotlinPropertyAccessors.InheritedImplementationKey] is not JsonObject implementation
                || TypeJson.OwnerName(implementation["owner"]) is not string ownerName
                || Str(implementation["member"]) is not string member
                || fact["params"] is not JsonArray parameters
                || TypeJson.Read(fact["ret"]) is not TypeNode factRet) continue;
            var factParams = parameters.OfType<JsonObject>().Select(p => TypeJson.Read(p["type"])).ToArray();
            if (factParams.Any(p => p == null)) continue;
            var arity = Int(implementation["arity"]);
            var propertyAccessor = Str(fact[KotlinPropertyAccessors.KindKey]);
            foreach (var (spec, _) in bases.Where(edge => edge.spec.Name == ownerName)
                         .GroupBy(edge => SupertypeGraph.TypeKey(edge.spec)).Select(group => group.First()))
            {
                var args = spec.Args ?? Array.Empty<TypeNode>();
                TypeNode[] sourceParams = null;
                TypeNode sourceRet = null;
                bool returnsValue = false;
                string physicalMember = null;
                JsonArray physicalTypeParams = null;
                NullableRepresentationFrame physicalMethodFrame = null;
                byte[][] physicalParameterNullableFlags = null;
                TypeNode[] queryBounds = null;
                JsonArray declarationSignature = null;
                string referencedDeclarationId = null;
                TypeNode logicalSuspendResult = null;
                JsonObject projectedSource = null;
                var callOwner = spec;
                if (defs.TryGetValue(spec.Name, out var sourceOwner))
                {
                    var sources = sourceOwner.Methods.OfType<JsonObject>().Where(source =>
                        !Bool(source["static"]) && !KotlinPropertyAccessors.IsPhysicalSlotBridge(source)
                        && (propertyAccessor == null
                            ? !KotlinPropertyAccessors.TryIdentity(source, out _, out _)
                                && (Str(source[DeclarationRename.SourceMemberKey]) ?? Str(source["name"])) == member
                            : Str(source[KotlinPropertyAccessors.SourceNameKey]) == member
                                && Str(source[KotlinPropertyAccessors.KindKey]) == propertyAccessor)
                        && ((source["typeParams"] as JsonArray)?.Count ?? 0) == arity
                        && SameMethodTypeParameterShape(SemanticMethodTypeParameters(source),
                            SemanticMethodTypeParameters(implementation), args, args)
                        && ((fact["overrides"] as JsonArray)?.OfType<JsonObject>().Any(edge =>
                                Str(edge[DeclarationIdentityBinding.Key]) is string selected
                                && selected == SourceDeclarationId(Str(source[DeclarationIdentityBinding.Key]))) == true
                            || SignatureMatches(source, factParams, factRet, args, refs, isValue))).ToList();
                    if (sources.Count == 0 || sources.Count > 1 && !SameSelectedSuspendDeclaration(sources)) continue;
                    // One selected Kotlin suspend declaration has multiple explicit CLR projections.
                    // Retain each exact MethodDef; these are not ambiguous source overloads.
                    foreach (var source in sources)
                    {
                        declarationSignature = FunctionSignatureIdentity.Signature((JsonArray)source["params"]);
                        physicalTypeParams = source["typeParams"] as JsonArray;
                        physicalMethodFrame = NullableRepresentationTypes.DeclarationMethodFrame(source);
                        queryBounds = !collectionQueries ? null : ((JsonArray)source["params"]).OfType<JsonObject>().Select((parameter, index) =>
                            !KotlinCollectionSlotSynthesis.IsErasedQueryParameter(fact, index)
                                || !KotlinCollectionSlotSynthesis.UsesErasedQueryCarrier(TypeJson.Read(parameter["type"])) ? null
                                : KotlinCollectionSlotSynthesis.GenericQueryBound(Str(parameter["kotlinType"] ?? parameter["nullableGeneric"]) is string semantic
                                    ? TypeNode.Parse(semantic) : null,
                                source, sourceOwner.Node, type => type is TypeNode.Fqn named
                                    && (defs.TryGetValue(named.Name, out var definition) ? definition.Kind == "interface"
                                        : refs?.IsInterfaceType(named) == true), refs?.NullableTypeFrames, isValue)).ToArray();
                        sourceParams = ((JsonArray)source["params"]).OfType<JsonObject>()
                            .Select(p => SupertypeGraph.SubstOwnerTvs(TypeJson.Read(p["type"]), args)).ToArray();
                        var openRet = TypeJson.Read(source["ret"]);
                        returnsValue = !IsVoid(openRet) || Bool(source[BirTypeLowering.ValueReturnKey]);
                        sourceRet = SupertypeGraph.SubstOwnerTvs(openRet, args);
                        physicalMember = Str(source[DeclarationIdentityBinding.ExplicitNameKey]) ?? Str(source["name"]);
                        referencedDeclarationId = Str(source[DeclarationIdentityBinding.Key]);
                        projectedSource = source[KotlinPropertyAccessors.SuspendSourceParamsKey] is JsonArray ? source : null;
                        if (suspendValues && IsSuspendMethod(source))
                            logicalSuspendResult = SupertypeGraph.SubstOwnerTvs(TypeNode.Parse(
                                Str(source["suspendResult"]) ?? throw new InvalidOperationException(
                                    "inherited suspend source has no logical result")), args);
                        PublishCandidate();
                    }
                    continue;
                }
                else
                {
                    if (refs == null) continue;
                    ReferencedMethodDeclaration source;
                    if (suspendValues && IsSuspendMethod(fact))
                    {
                        if (!refs.TrySelectedOverrideDeclaration(spec.Name, member, propertyAccessor, arity,
                                factParams, args, implementation["typeParams"] as JsonArray, args,
                                selectedSuspend: true, out source)) continue;
                        logicalSuspendResult = SupertypeGraph.SubstOwnerTvs(source.Return, args);
                    }
                    else
                    {
                        // Referenced declaration selection needs the preserved source signature; local and
                        // physical slot matching still consume the lowered factParams in their own frame.
                        var declarationParams = parameters.OfType<JsonObject>().Select((parameter, index) =>
                            Str(parameter["kotlinType"]) is string sourceType
                                ? TypeNode.Parse(sourceType) : factParams[index]).ToArray();
                        var ownArgs = ClassOwnArgs(cls);
                        var selectedOwnerArgs = KotlinSupertypesRecord.ReadNullableFrame(cls.Node)?.OrdinaryArguments(ownArgs)
                            ?? ownArgs;
                        // A selected property's return can already be a value carrier. Its exact
                        // owner, role and parameters identify the declaration without guessing that return.
                        var found = propertyAccessor != null
                            ? refs.TrySelectedOverrideDeclaration(spec.Name, member, propertyAccessor,
                                (fact["typeParams"] as JsonArray)?.Count ?? 0, declarationParams, args,
                                SemanticMethodTypeParameters(fact), selectedOwnerArgs, selectedSuspend: false,
                                out source, semanticConstraints: true)
                            : refs.TrySelectedMethodDeclaration(spec.Name, member,
                                (fact["typeParams"] as JsonArray)?.Count ?? 0,
                                declarationParams, factRet, args, SemanticMethodTypeParameters(fact),
                                selectedOwnerArgs, out source);
                        if (!found) continue;
                    }
                    sourceParams = source.PhysicalParameters.Select(p => SupertypeGraph.SubstOwnerTvs(p, args)).ToArray();
                    declarationSignature = new JsonArray(source.SignatureParameters.Select(TypeJson.Write).ToArray());
                    referencedDeclarationId = source.DeclarationId;
                    physicalTypeParams = source.TypeParams;
                    physicalMethodFrame = source.NullableFrame;
                    physicalParameterNullableFlags = source.ParameterNullableFlags;
                    queryBounds = !collectionQueries ? null : Enumerable.Range(0, source.PhysicalParameters.Length)
                        .Select(index => KotlinCollectionSlotSynthesis.IsErasedQueryParameter(fact, index)
                            ? source.QueryParameterBound?.Invoke(index) : null).ToArray();
                    sourceRet = SupertypeGraph.SubstOwnerTvs(
                        logicalSuspendResult != null ? source.Return : source.PhysicalReturn, args);
                    returnsValue = logicalSuspendResult != null ? !IsVoid(source.Return) : source.ReturnsValue;
                    physicalMember = source.PhysicalMember;
                    callOwner = new TypeNode.Fqn(refs.ExactReflectedOwner(spec.Name, args.Length), spec.Args);
                }
                PublishCandidate();

                void PublishCandidate()
                {
                    var candidate = (JsonObject)fact.DeepClone();
                    candidate["name"] = projectedSource == null ? Str(fact["member"]) : Str(projectedSource["name"]);
                    if (projectedSource != null)
                    {
                        candidate[DeclarationRename.SourceMemberKey] = fact["member"].DeepClone();
                        candidate["params"] = projectedSource["params"].DeepClone();
                    }
                    candidate["typeParams"] = SubstituteOwnerTypeParameterConstraints(physicalTypeParams, args);
                    if (physicalMethodFrame is { RequiresMetadata: true })
                        candidate[NullableRepresentationTypes.MethodFrameKey] = physicalMethodFrame.ToJson().ToJsonString();
                    // The candidate now owns physical parameters; carry the selected implementation's source
                    // bounds too, so local interface matching does not mistake companion constraints for Kotlin bounds.
                    if (Str(implementation[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string encodedBounds)
                    {
                        var sourceBounds = JsonNode.Parse(encodedBounds).AsObject();
                        var ownerFrame = sourceOwner != null ? KotlinSupertypesRecord.ReadNullableFrame(sourceOwner.Node)
                            : refs.NullableTypeFrames.GetValueOrDefault(spec.Name);
                        var sourceOwnerArgs = ownerFrame?.OrdinaryArguments(args) ?? args;
                        foreach (var constraints in sourceBounds["bounds"].AsObject().Select(pair => pair.Value).OfType<JsonArray>())
                            for (var index = 0; index < constraints.Count; index++)
                                constraints[index] = TypeJson.Write(SupertypeGraph.SubstOwnerTvs(TypeJson.Read(constraints[index]), sourceOwnerArgs));
                        candidate[NullableGenericErasure.MethodTypeParameterBoundsPre] = sourceBounds.ToJsonString();
                    }
                    candidate[DeclarationIdentityBinding.ExplicitNameKey] = physicalMember;
                    candidate["ret"] = TypeJson.Write(sourceRet);
                    candidate["inheritedDeclarationSignature"] = declarationSignature;
                    if (referencedDeclarationId != null)
                        candidate["inheritedDeclarationId"] = referencedDeclarationId;
                    if (logicalSuspendResult != null)
                    {
                        candidate["suspendResult"] = TypeNode.ToJson(logicalSuspendResult);
                        candidate["suspendRet"] = TypeJson.Write(sourceRet);
                    }
                    else if (candidate["mods"] is JsonObject candidateMods)
                        candidateMods.Remove("suspend");
                    for (var i = 0; i < sourceParams.Length; i++)
                    {
                        var parameter = (JsonObject)((JsonArray)candidate["params"])[i];
                        parameter["type"] = TypeJson.Write(sourceParams[i]);
                        if (queryBounds?[i] is { } queryBound)
                            parameter[KotlinCollectionSlotSynthesis.QueryBoundKey] = TypeJson.Write(
                                SupertypeGraph.SubstOwnerTvs(queryBound, args));
                        if (physicalParameterNullableFlags?[i] is { } flags)
                            parameter["nullableFlags"] = new JsonArray(flags.Select(flag =>
                                (JsonNode)JsonValue.Create((int)flag)).ToArray());
                    }
                    if (returnsValue && IsUnitValueSlot(sourceRet)) candidate[BirTypeLowering.ValueReturnKey] = true;
                    candidates.Add(candidate);
                    inheritedOwners.Add(candidate, callOwner);
                    selectedFacts?.Add(candidate, fact);
                    // Selection consumes the frontend's source signature; the forwarding body separately consumes
                    // sourceParams, the selected implementation's physical declaration slots.
                    // Added physical inputs (e.g. the cold continuation) retain their
                    // selected MethodDef slots; source inputs retain the frontend proof.
                    inheritedSignatures.Add(candidate, sourceParams.Select((type, index) =>
                        index < factParams.Length ? factParams[index] : type).ToArray());
                }
            }
        }
    }

    internal static Dictionary<string, List<(TypeNode.Fqn Owner, JsonObject Method)>> SelectedInheritedCollectionMethods(
        IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var defs = SupertypeGraph.Collect(roots);
        var selected = new Dictionary<string, List<(TypeNode.Fqn, JsonObject)>>(StringComparer.Ordinal);
        foreach (var cls in defs.Values.Where(def => def.Kind == "class"))
        {
            var methods = new List<JsonObject>();
            var owners = new Dictionary<JsonObject, TypeNode.Fqn>();
            var signatures = new Dictionary<JsonObject, TypeNode[]>();
            CollectInheritedClassMethods(cls, defs, refs, isValue, methods, owners, signatures,
                suspendValues: false, includeFact: fact => fact["overrides"] is JsonArray overrides
                    && overrides.OfType<JsonObject>().Any(KotlinCollectionSlotSynthesis.OwnsSupplementalOverride),
                collectionQueries: true);
            if (methods.Count != 0)
                selected[cls.Name] = methods.Select(method => (owners[method], method)).ToList();
        }
        return selected;
    }

    internal static IReadOnlyList<(TypeNode.Fqn Owner, JsonObject Method, JsonObject Fact, bool IsDefault)>
        SelectedInheritedImplementations(Def cls, IReadOnlyDictionary<string, Def> defs,
            ReferenceMetadataIndex refs, ValueTypeOracle isValue, Func<JsonObject, bool, bool> includeFact)
    {
        var result = new List<(TypeNode.Fqn, JsonObject, JsonObject, bool)>();
        void Collect(bool isDefault, IEnumerable<JsonObject> facts = null)
        {
            var methods = new List<JsonObject>();
            var owners = new Dictionary<JsonObject, TypeNode.Fqn>();
            var signatures = new Dictionary<JsonObject, TypeNode[]>();
            var selections = new Dictionary<JsonObject, JsonObject>();
            CollectInheritedClassMethods(cls, defs, refs, isValue, methods, owners, signatures,
                suspendValues: false, includeFact: fact => includeFact(fact, isDefault), suppliedFacts: facts,
                interfaceImplementations: isDefault, selectedFacts: selections);
            result.AddRange(methods.Select(method => (owners[method], method, selections[method], isDefault)));
        }
        Collect(false);
        var defaults = new[] { KotlinPropertyAccessors.InheritedDefaultAccessorsKey,
                KotlinPropertyAccessors.InheritedDefaultMethodsKey }
            .SelectMany(key => (cls.Node[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Select(fact => {
                var selected = (JsonObject)fact.DeepClone();
                selected["member"] ??= selected[KotlinPropertyAccessors.SourceNameKey]?.DeepClone();
                selected["params"] = new JsonArray((fact["params"] as JsonArray ?? new JsonArray())
                    .Select((type, index) => (JsonNode)new JsonObject { ["name"] = "p" + index, ["type"] = type?.DeepClone() }).ToArray());
                selected[KotlinPropertyAccessors.InheritedImplementationKey] = selected["implementation"]?.DeepClone();
                return selected;
            }).ToArray();
        Collect(true, defaults);
        return result;
    }

    static bool SameSelectedSuspendDeclaration(IReadOnlyList<JsonObject> projections)
    {
        var identity = projections.Count == 0 ? null
            : SourceDeclarationId(Str(projections[0][DeclarationIdentityBinding.Key]));
        return identity != null && projections.All(projection =>
            SourceDeclarationId(Str(projection[DeclarationIdentityBinding.Key])) == identity
            && projection[KotlinPropertyAccessors.SuspendSourceParamsKey] is JsonArray
            && TypeJson.Read(projection[KotlinPropertyAccessors.SuspendSourceRetKey]) != null);
    }

    // Ordinary-function twin of the property rule below. This matters when a DIM has a CLR physical name that differs
    // from its Kotlin name and an unrelated class/base method already occupies that physical signature.
    static void AddInheritedDefaultMethodBridges(Def cls, IReadOnlyDictionary<string, Def> defs,
        JsonArray methods, TypeNode[] ownArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        IReadOnlyDictionary<JsonObject, string> exactBridgeSources,
        Dictionary<string, JsonObject> bridges, ref int ordinal, IReadOnlySet<string> localTypeNames)
    {
        if (cls.Node[KotlinPropertyAccessors.InheritedDefaultMethodsKey] is not JsonArray inheritedFacts)
            return;
        var reachable = SupertypeGraph.Reachable(cls, defs, refs)
            .Where(item => item.isInterface).Select(item => item.spec).ToList();
        foreach (var fact in inheritedFacts.OfType<JsonObject>())
        {
            if (Str(fact["member"]) is not string memberName
                || fact["params"] is not JsonArray factParamNodes
                || TypeJson.Read(fact["ret"]) is not TypeNode factRet
                || fact["implementation"] is not JsonObject implementation
                || TypeJson.OwnerName(implementation["owner"]) is not string implementationOwner
                || Str(implementation["member"]) is not string implementationMember
                || Str(implementation["kind"]) != "method")
                continue;
            var methodArity = Int(implementation["arity"]);
            var implementationTypeParams = implementation["typeParams"] as JsonArray;
            var factParams = factParamNodes.Select(TypeJson.Read).ToArray();
            if (methodArity < 0 || (implementationTypeParams?.Count ?? 0) != methodArity
                || factParams.Any(type => type == null)) continue;
            var selectedIdentities = (fact["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(edge => TypeJson.OwnerName(edge["owner"]) == implementationOwner
                    && Str(edge["member"]) == implementationMember)
                .Select(edge => Str(edge[DeclarationIdentityBinding.Key])).Where(id => id != null).Distinct().ToArray();
            var selectedIdentity = selectedIdentities.Length == 1 ? selectedIdentities[0] : null;

            foreach (var spec in reachable.Where(candidate => candidate.Name == implementationOwner)
                         .GroupBy(SupertypeGraph.TypeKey).Select(group => group.First()))
            {
                if (defs.TryGetValue(spec.Name, out var sup) && sup.Kind == "interface")
                {
                    var supArgs = SupertypeGraph.EffectiveArgs(spec, sup.Arity);
                    if (supArgs == null) continue;
                    var sources = sup.Methods.OfType<JsonObject>().Where(candidate =>
                        !KotlinPropertyAccessors.IsPhysicalSlotBridge(candidate)
                        && !KotlinPropertyAccessors.TryIdentity(candidate, out _, out _)
                        && (Str(candidate[DeclarationRename.SourceMemberKey]) ?? Str(candidate["name"])) == implementationMember
                        && ((candidate["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                        && SameMethodTypeParameterShape(candidate["typeParams"] as JsonArray,
                            implementationTypeParams, supArgs, supArgs)
                        && (selectedIdentity != null
                            ? SourceDeclarationId(Str(candidate[DeclarationIdentityBinding.Key]))
                                == SourceDeclarationId(selectedIdentity)
                            : SignatureMatches(candidate, factParams, factRet, supArgs, refs, isValue))).ToList();
                    if (sources.Count == 0 || sources.Count > 1 && sources.Any(candidate =>
                            candidate[KotlinPropertyAccessors.SuspendSourceParamsKey] is not JsonArray
                            || TypeJson.Read(candidate[KotlinPropertyAccessors.SuspendSourceRetKey]) is null))
                        continue;
                    // A normal Kotlin declaration has one physical method. Suspend lowering explicitly projects one
                    // selected frontend declaration into the Task and continuation methods and stamps the same source
                    // descriptor on both; allocate each resulting CLR obligation independently.
                    foreach (var source in sources)
                    {
                        var slotParams = ((JsonArray)source["params"]).OfType<JsonObject>()
                            .Select(parameter => SupertypeGraph.SubstOwnerTvs(
                                TypeJson.Read(parameter["type"]), supArgs))
                            .ToArray();
                        var slotRet = SupertypeGraph.SubstOwnerTvs(TypeJson.Read(source["ret"]), supArgs);
                        var sourcePhysicalMember = Str(source[DeclarationIdentityBinding.ExplicitNameKey])
                            ?? Str(source["name"]);
                        var descriptors = new JsonArray(ImplDescriptor(spec, sourcePhysicalMember, methodArity,
                            slotParams, slotRet, SubstituteOwnerTypeParameterConstraints(
                                source["typeParams"] as JsonArray, supArgs)));
                        AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                            memberName, null, spec, source, null, descriptors,
                            sourceOwnerArgs: supArgs, descriptorOwnerArgs: Array.Empty<TypeNode>(), virtualCall: false,
                            localTypeNames: localTypeNames);
                        // This selected default can itself fill inherited CLR slots. Erasure can make an unrelated
                        // class method capture those slots too, even when its own interface MethodDef is protected.
                        // Reuse the exact source-to-bridge relation established while allocating those MethodImpls.
                        if (Str(source[DeclarationIdentityBinding.Key]) is string sourceIdentity
                            && exactBridgeSources != null)
                            foreach (var exactBridge in sup.Methods.OfType<JsonObject>().Where(candidate =>
                                         exactBridgeSources.TryGetValue(candidate, out var selected)
                                         && selected == sourceIdentity && candidate["clrInterfaceImpls"] is JsonArray))
                                AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                                    memberName, null, spec, source, null, (JsonArray)exactBridge["clrInterfaceImpls"],
                                    sourceOwnerArgs: supArgs, descriptorOwnerArgs: supArgs, virtualCall: true,
                                    localTypeNames: localTypeNames);
                    }
                    continue;
                }

                if (refs == null) continue;
                var selectedArgs = spec.Args ?? Array.Empty<TypeNode>();
                var semanticArgs = refs.NullableTypeFrames.GetValueOrDefault(spec.Name)?.OrdinaryArguments(selectedArgs)
                    ?? selectedArgs;
                ReferencedMethodDeclaration referencedDeclaration;
                var found = selectedIdentity != null && refs.HasDotKtOwner(spec.Name)
                    ? refs.TrySelectedOverrideDeclaration(spec.Name, implementationMember, null, methodArity,
                        factParams, selectedArgs, SemanticMethodTypeParameters(implementation), semanticArgs,
                        IsSuspendMethod(fact), out referencedDeclaration, semanticConstraints: true,
                        selectedDeclarationId: selectedIdentity)
                    : refs.TrySelectedMethodDeclaration(spec.Name, implementationMember, methodArity,
                        factParams, factRet, selectedArgs, SemanticMethodTypeParameters(implementation), semanticArgs,
                        out referencedDeclaration);
                if (!found
                    || referencedDeclaration.Return == null
                    || referencedDeclaration.Parameters == null
                    || referencedDeclaration.Parameters.Any(type => type == null))
                    continue;
                var specArgs = spec.Args ?? Array.Empty<TypeNode>();
                var referencedSlotParams = referencedDeclaration.PhysicalParameters.Select(type =>
                    SupertypeGraph.SubstOwnerTvs(type, specArgs)).ToArray();
                var referencedSlotRet = SupertypeGraph.SubstOwnerTvs(
                    referencedDeclaration.PhysicalReturn, specArgs);
                var physicalMember = referencedDeclaration.PhysicalMember;
                var physicalOwner = refs.ExactReflectedOwner(spec.Name, spec.Args?.Length ?? 0);
                var descriptorOwner = new TypeNode.Fqn(physicalOwner, spec.Args);
                var referencedSource = MethodShape(
                    physicalMember, referencedSlotParams, referencedSlotRet, methodArity,
                    referencedDeclaration.TypeParams);
                var referencedDescriptors = new JsonArray(ImplDescriptor(
                    descriptorOwner, physicalMember, methodArity, referencedSlotParams, referencedSlotRet,
                    referencedDeclaration.TypeParams));
                AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                    memberName, null, descriptorOwner, referencedSource, null, referencedDescriptors,
                    sourceOwnerArgs: Array.Empty<TypeNode>(), descriptorOwnerArgs: Array.Empty<TypeNode>(),
                    virtualCall: false, localTypeNames: localTypeNames);
                foreach (var inheritedSlot in refs.ReferencedMethodImpls(spec.Name, referencedDeclaration.DeclarationId))
                {
                    var descriptors = new JsonArray(ImplDescriptor(inheritedSlot.DeclarationOwner,
                        inheritedSlot.DeclarationMember, inheritedSlot.MethodArity,
                        inheritedSlot.Parameters, inheritedSlot.Return, inheritedSlot.TypeParams));
                    AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                        memberName, null, descriptorOwner, referencedSource, null, descriptors,
                        sourceOwnerArgs: Array.Empty<TypeNode>(), descriptorOwnerArgs: specArgs,
                        virtualCall: true, localTypeNames: localTypeNames);
                }
            }
        }
    }

    // A class can inherit a frontend-selected default property implementation while also exposing an unrelated
    // ordinary function whose physical name is the external CLR accessor name. CoreCLR then prefers the class method
    // over the DIM unless the property slot receives an exact class-level MethodImpl. The selected implementation is
    // an explicit BIR fact; only the physical collision and MethodImpl allocation are decided here.
    static void AddInheritedDefaultPropertyBridges(Def cls, IReadOnlyDictionary<string, Def> defs,
        JsonArray methods, TypeNode[] ownArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        IReadOnlyDictionary<JsonObject, string> exactBridgeSources,
        Dictionary<string, JsonObject> bridges, ref int ordinal, IReadOnlySet<string> localTypeNames)
    {
        if (cls.Node[KotlinPropertyAccessors.InheritedDefaultAccessorsKey] is not JsonArray inheritedFacts)
            return;
        var reachable = SupertypeGraph.Reachable(cls, defs, refs)
            .Where(item => item.isInterface)
            .Select(item => item.spec)
            .ToList();
        foreach (var fact in inheritedFacts.OfType<JsonObject>())
        {
            if (Str(fact[KotlinPropertyAccessors.SourceNameKey]) is not string propertyName
                || Str(fact[KotlinPropertyAccessors.KindKey]) is not string accessorKind
                || accessorKind is not ("get" or "set")
                || fact["params"] is not JsonArray factParamNodes
                || TypeJson.Read(fact["ret"]) is not TypeNode factRet
                || fact["implementation"] is not JsonObject implementation
                || TypeJson.OwnerName(implementation["owner"]) is not string implementationOwner
                || Str(implementation["member"]) is not string implementationMember)
                continue;
            var implementationKind = Str(implementation["kind"]) switch
            {
                "getter" => "get",
                "setter" => "set",
                _ => null,
            };
            if (implementationKind == null) continue;
            var methodArity = Int(implementation["arity"]);
            var implementationTypeParams = implementation["typeParams"] as JsonArray;
            if (methodArity < 0 || (implementationTypeParams?.Count ?? 0) != methodArity) continue;
            var factParams = factParamNodes.Select(TypeJson.Read).ToArray();
            if (factParams.Any(type => type == null)) continue;

            foreach (var spec in reachable.Where(candidate => candidate.Name == implementationOwner)
                         .GroupBy(SupertypeGraph.TypeKey).Select(group => group.First()))
            {
                if (defs.TryGetValue(spec.Name, out var sup) && sup.Kind == "interface")
                {
                    var supArgs = SupertypeGraph.EffectiveArgs(spec, sup.Arity);
                    if (supArgs == null) continue;
                    var sources = sup.Methods.OfType<JsonObject>().Where(candidate =>
                        !KotlinPropertyAccessors.IsPhysicalSlotBridge(candidate)
                        && KotlinPropertyAccessors.TryIdentity(candidate, out var candidateProperty,
                            out var candidateKind)
                        && candidateProperty == implementationMember && candidateKind == implementationKind
                        && ((candidate["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                        && SameMethodTypeParameterShape(candidate["typeParams"] as JsonArray,
                            implementationTypeParams, supArgs, supArgs)
                        && SignatureMatches(candidate, factParams, factRet, supArgs, refs, isValue)).ToList();
                    if (sources.Count != 1) continue;
                    if (exactBridgeSources == null) continue;
                    var source = sources[0];
                    var sourceAssociation = Str(source[KotlinPropertyAccessors.AssociationKey]);
                    var exactBridges = sup.Methods.OfType<JsonObject>().Where(candidate =>
                        Bool(candidate[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey])
                        && candidate["clrInterfaceImpls"] is JsonArray
                        && KotlinPropertyAccessors.TryIdentity(candidate, out var bridgeProperty,
                            out var bridgeKind)
                        && bridgeProperty == implementationMember && bridgeKind == implementationKind
                        && exactBridgeSources.TryGetValue(candidate, out var association)
                        && association == sourceAssociation).ToList();
                    // No exact bridge means this is a pure local Kotlin property: its dedicated physical name cannot be
                    // captured by an ordinary get_/set_ method, so the class needs no additional MethodImpl. More than
                    // one bridge is meaningful when one selected accessor fills several external property slots; carry
                    // every already-resolved descriptor instead of silently dropping all of them as "ambiguous".
                    foreach (var exactBridge in exactBridges)
                        AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                            propertyName, accessorKind, spec, source, sourceAssociation,
                            (JsonArray)exactBridge["clrInterfaceImpls"],
                            sourceOwnerArgs: supArgs, descriptorOwnerArgs: supArgs, virtualCall: true,
                            localTypeNames: localTypeNames);
                    continue;
                }

                if (refs == null) continue;
                var referencedImplementations = refs.ReferencedPropertyMethodImpls(
                    spec.Name, implementationMember, implementationKind,
                    factParams.Length, methodArity, factParams,
                    spec.Args ?? Array.Empty<TypeNode>());
                if (referencedImplementations.Count != 0)
                {
                    foreach (var referencedImplementation in referencedImplementations)
                    {
                        var source = MethodShape(
                            referencedImplementation.SourceMember, factParams, factRet, methodArity,
                            implementationTypeParams);
                        var referencedArgs = spec.Args ?? Array.Empty<TypeNode>();
                        var referencedSlotParams = referencedImplementation.Parameters.Select(type =>
                            SupertypeGraph.SubstOwnerTvs(
                                NullableGenericErasure.EraseNullableTv(type, isValue), referencedArgs)).ToArray();
                        var referencedSlotRet = SupertypeGraph.SubstOwnerTvs(
                            NullableGenericErasure.EraseNullableTv(referencedImplementation.Return, isValue), referencedArgs);
                        var referencedDescriptorOwner = SupertypeGraph.SubstOwnerTvs(
                            referencedImplementation.DeclarationOwner, referencedArgs) as TypeNode.Fqn;
                        if (referencedDescriptorOwner == null) continue;
                        var referencedDescriptors = new JsonArray(ImplDescriptor(
                            referencedDescriptorOwner, referencedImplementation.DeclarationMember,
                            referencedImplementation.MethodArity, referencedSlotParams, referencedSlotRet,
                            referencedImplementation.TypeParams));
                        AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                            propertyName, accessorKind, spec, source,
                            "inherited-default:" + propertyName + ":" + accessorKind,
                            referencedDescriptors, sourceOwnerArgs: Array.Empty<TypeNode>(),
                            descriptorOwnerArgs: Array.Empty<TypeNode>(), virtualCall: true,
                            localTypeNames: localTypeNames);
                    }
                    continue;
                }

                if (!refs.TryExternalPropertyAccessor(spec.Name, implementationMember, implementationKind,
                        factParams.Length, methodArity, factParams, spec.Args ?? Array.Empty<TypeNode>(),
                        out var physicalOwner, out _, out var physicalMember))
                    continue;
                var provisionalCollision = HasOrdinaryPhysicalCollisionInHierarchy(cls, defs, methods,
                    physicalMember, methodArity, factParams, factRet, ownArgs, refs, isValue,
                    includeNonVirtual: false);
                if (!refs.TryNullableGenericPropertySlot(spec.Name, implementationMember, implementationKind,
                        isStatic: false, factParams.Length, methodArity, factParams,
                        spec.Args ?? Array.Empty<TypeNode>(), out var slotRet0, out var slotParams0,
                        out var refused, includeUnchanged: true)
                    || slotParams0 == null || slotParams0.Any(type => type == null)
                    || refused?.Any(value => value) == true)
                {
                    if (provisionalCollision)
                        throw UnrepresentableInheritedDefault(
                            cls.Name, implementationOwner, implementationMember, implementationKind,
                            "its exact CLR slot signature could not be resolved");
                    continue;
                }
                var specArgs = spec.Args ?? Array.Empty<TypeNode>();
                var slotParams = slotParams0
                    .Select(type => SupertypeGraph.SubstOwnerTvs(
                        NullableGenericErasure.EraseNullableTv(type, isValue), specArgs)).ToArray();
                var slotRet = slotRet0 == null
                    ? factRet
                    : SupertypeGraph.SubstOwnerTvs(
                        NullableGenericErasure.EraseNullableTv(slotRet0, isValue), specArgs);
                var descriptorOwner = new TypeNode.Fqn(physicalOwner, spec.Args);
                var callableExternalBody = refs.IsPublicConcreteInstanceMethod(
                    physicalOwner, physicalMember, methodArity, slotParams, slotRet);
                var collision = HasOrdinaryPhysicalCollisionInHierarchy(cls, defs, methods,
                    physicalMember, methodArity, slotParams, slotRet, ownArgs, refs, isValue,
                    includeNonVirtual: callableExternalBody);
                if (!collision) continue;
                if (!callableExternalBody)
                    throw UnrepresentableInheritedDefault(
                        cls.Name, implementationOwner, implementationMember, implementationKind,
                        $"the selected external body is not a public concrete MethodDef ({physicalOwner}.{physicalMember})");
                var externalSource = MethodShape(physicalMember, slotParams, slotRet, methodArity);
                var descriptors = new JsonArray(ImplDescriptor(
                    descriptorOwner, physicalMember, methodArity, slotParams, slotRet));
                AddInheritedDefaultBridge(cls, defs, methods, ownArgs, refs, isValue, bridges, ref ordinal,
                    propertyName, accessorKind, descriptorOwner, externalSource,
                    "inherited-default:" + propertyName + ":" + accessorKind,
                    descriptors, sourceOwnerArgs: Array.Empty<TypeNode>(),
                    descriptorOwnerArgs: Array.Empty<TypeNode>(), virtualCall: false,
                    localTypeNames: localTypeNames);
            }
        }
    }

    static InvalidOperationException UnrepresentableInheritedDefault(string owner, string implementationOwner,
        string member, string accessorKind, string reason) => new(
        $"bir2cir: inherited default property {implementationOwner}.{member} ({accessorKind}) cannot be represented "
        + $"on {owner}: an ordinary CLR method captures the selected property slot, but {reason}; "
        + "a callable producer-side trampoline is required");

    static bool SignatureMatches(JsonObject method, TypeNode[] expectedParams, TypeNode expectedRet,
        TypeNode[] ownerArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var sourceParameters = method[KotlinPropertyAccessors.SuspendSourceParamsKey] as JsonArray;
        var parameters = sourceParameters ?? method["params"] as JsonArray;
        var declaredRet = TypeJson.Read(sourceParameters == null
            ? method["ret"] : method[KotlinPropertyAccessors.SuspendSourceRetKey]);
        if (parameters == null || parameters.Count != expectedParams.Length || declaredRet == null) return false;
        for (var i = 0; i < expectedParams.Length; i++)
        {
            var declared = TypeJson.Read(sourceParameters == null
                ? (parameters[i] as JsonObject)?[FunctionSignatureIdentity.Key]
                    ?? (parameters[i] as JsonObject)?["type"] : parameters[i]);
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

    static JsonObject MethodShape(string name, TypeNode[] parameters, TypeNode ret, int methodArity,
        JsonArray typeParams = null)
    {
        var result = new JsonObject
        {
            ["name"] = name,
            ["params"] = new JsonArray(parameters.Select((type, index) => (JsonNode)new JsonObject
            {
                ["name"] = "p" + index,
                ["type"] = TypeJson.Write(type),
            }).ToArray()),
            ["ret"] = TypeJson.Write(ret),
        };
        if (typeParams != null)
            result["typeParams"] = typeParams.DeepClone();
        else if (methodArity > 0)
            result["typeParams"] = new JsonArray(Enumerable.Range(0, methodArity)
                .Select(index => (JsonNode)new JsonObject { ["name"] = "T" + index }).ToArray());
        return result;
    }

    static void AddInheritedDefaultBridge(Def cls, IReadOnlyDictionary<string, Def> defs,
        JsonArray methods, TypeNode[] ownArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        Dictionary<string, JsonObject> bridges, ref int ordinal, string propertyName, string accessorKind,
        TypeNode.Fqn callOwner, JsonObject source, string sourceAssociation, JsonArray descriptors,
        TypeNode[] sourceOwnerArgs, TypeNode[] descriptorOwnerArgs, bool virtualCall,
        IReadOnlySet<string> localTypeNames)
    {
        if (source["params"] is not JsonArray sourceParamNodes
            || TypeJson.Read(source["ret"]) is not TypeNode sourceRet0)
            return;
        var sourceParams = sourceParamNodes.OfType<JsonObject>()
            .Select(parameter => TypeJson.Read(parameter["type"]))
            .Select(type => type == null ? null : SupertypeGraph.SubstOwnerTvs(type, sourceOwnerArgs))
            .ToArray();
        if (sourceParams.Length != sourceParamNodes.Count || sourceParams.Any(type => type == null)) return;
        var sourceRet = SupertypeGraph.SubstOwnerTvs(sourceRet0, sourceOwnerArgs);
        var sourceTypeParams = SubstituteOwnerTypeParameterConstraints(
            source["typeParams"] as JsonArray, sourceOwnerArgs);
        var callMember = Str(source[DeclarationIdentityBinding.ExplicitNameKey]) ?? Str(source["name"]);
        if (defs.TryGetValue(callOwner.Name, out var callDef)
            && callDef.Node["methods"] is JsonArray callMethods)
        {
            if (!callMethods.OfType<JsonObject>().Any(method => ReferenceEquals(method, source)))
                throw new InvalidOperationException(
                    $"bir2cir: inherited-default call target {callOwner.Name}.{callMember} is absent from its declaring type");
        }
        foreach (var descriptor in descriptors.OfType<JsonObject>())
        {
            if (TypeJson.Read(descriptor["owner"]) is not TypeNode.Fqn descriptorOwner0
                || Str(descriptor["member"]) is not string descriptorMember
                || descriptor["params"] is not JsonArray descriptorParamNodes
                || TypeJson.Read(descriptor["ret"]) is not TypeNode descriptorRet0)
                continue;
            var descriptorArity = Int(descriptor["arity"]);
            if (descriptorArity < 0) continue;
            var descriptorTypeParams = SubstituteOwnerTypeParameterConstraints(
                descriptor["typeParams"] as JsonArray, descriptorOwnerArgs);
            var slotParams = descriptorParamNodes.Select(TypeJson.Read)
                .Select(type => type == null ? null : SupertypeGraph.SubstOwnerTvs(type, descriptorOwnerArgs))
                .ToArray();
            if (slotParams.Any(type => type == null)) continue;
            var slotRet = SupertypeGraph.SubstOwnerTvs(descriptorRet0, descriptorOwnerArgs);
            if (!HasOrdinaryPhysicalCollisionInHierarchy(cls, defs, methods, descriptorMember,
                    descriptorArity, slotParams, slotRet, ownArgs, refs, isValue,
                    includeNonVirtual: true))
                continue;
            var descriptorOwner = SupertypeGraph.SubstOwnerTvs(descriptorOwner0, descriptorOwnerArgs)
                as TypeNode.Fqn;
            if (descriptorOwner == null) continue;
            var propertyBridge = accessorKind is "get" or "set";
            var key = (propertyBridge ? "dim-property:" : "dim-method:") + descriptorOwner.Name + ":"
                      + propertyName + ":" + accessorKind + "#"
                      + MethodTypeParameterShapeKey(descriptorTypeParams, Array.Empty<TypeNode>()) + ":"
                      + descriptorArity + "(" + string.Join(",", slotParams.Select(SupertypeGraph.TypeKey))
                      + ")->" + SupertypeGraph.TypeKey(slotRet);
            if (!bridges.TryGetValue(key, out var bridge))
            {
                var substitutedSource = (JsonObject)source.DeepClone();
                var substitutedParams = substitutedSource["params"] as JsonArray;
                for (var i = 0; i < sourceParams.Length; i++)
                    if (substitutedParams?[i] is JsonObject parameter)
                        parameter["type"] = TypeJson.Write(sourceParams[i]);
                substitutedSource["ret"] = TypeJson.Write(sourceRet);
                if (sourceTypeParams != null)
                    substitutedSource["typeParams"] = sourceTypeParams.DeepClone();
                bridge = BuildBridge(cls, substitutedSource, slotParams, slotRet,
                    $"dotkt${(propertyBridge ? "$dimprop" : "$dimmethod")}${SafeName(propertyName)}${ordinal++}",
                    isValue, refs, callOwner, virtualCall, callMember, physicalImplementation: true);
                if (!propertyBridge)
                    RoundtripMetadata.AddSourceMethodIdentity(bridge, propertyName);
                if (propertyBridge)
                    KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(
                        bridge, propertyName, accessorKind, sourceAssociation);
                bridges[key] = bridge;
                methods.Add(bridge);
            }
            AddImplDescriptor(bridge, "clrInterfaceImpls",
                ImplDescriptor(descriptorOwner, descriptorMember, descriptorArity, slotParams, slotRet,
                    descriptorTypeParams));
        }
    }

    // Re-anchor only OWNER-scoped references inside a method type-parameter declaration. Method-scoped references
    // remain in the bridge's own !!i frame. This is declaration transport, not constraint inference: the frontend's
    // exact selected declaration is copied into the constructed owner frame used by the MethodImpl descriptor/body.
    internal static JsonArray SubstituteOwnerTypeParameterConstraints(JsonArray typeParams, TypeNode[] ownerArgs)
    {
        if (typeParams == null) return null;
        var result = typeParams.DeepClone() as JsonArray;
        if (result == null || ownerArgs is not { Length: > 0 }) return result;
        foreach (var parameter in result.OfType<JsonObject>())
            if (parameter["constraints"] is JsonArray constraints)
                for (var i = 0; i < constraints.Count; i++)
                    if (TypeJson.Read(constraints[i]) is TypeNode constraint)
                        constraints[i] = TypeJson.Write(SupertypeGraph.SubstOwnerTvs(constraint, ownerArgs));
        return result;
    }

    static bool HasOrdinaryPhysicalCollisionInHierarchy(Def cls, IReadOnlyDictionary<string, Def> defs,
        JsonArray methods, string physicalName, int methodArity, TypeNode[] slotParams, TypeNode slotRet,
        TypeNode[] ownArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        bool includeNonVirtual)
    {
        if (HasOrdinaryPhysicalCollision(methods, physicalName, methodArity,
                slotParams, slotRet, ownArgs, refs, isValue, includeNonVirtual))
            return true;

        // CLR implicit interface implementation considers public instance methods inherited from base classes too.
        // Walk the constructed class base chain across local and referenced declarations. Property accessors remain
        // excluded explicitly because this question is only whether an unrelated ordinary function whose already-
        // physical name captures the DIM slot is inherited. Exact accessor MethodImpls are allocated independently.
        var current = cls.Base;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            if (defs.TryGetValue(current.Name, out var localBase) && localBase.Kind == "class")
            {
                var args = SupertypeGraph.EffectiveArgs(current, localBase.Arity);
                if (args == null) return false;
                if (HasOrdinaryPhysicalCollision(localBase.Methods, physicalName, methodArity,
                        slotParams, slotRet, args, refs, isValue, includeNonVirtual))
                    return true;
                current = localBase.Base == null
                    ? null
                    : SupertypeGraph.SubstOwnerTvs(localBase.Base, args) as TypeNode.Fqn;
                continue;
            }

            if (refs == null) return false;
            foreach (var candidate in refs.AccessibleDeclaredInstanceMethods(current, physicalName, methodArity))
                if ((includeNonVirtual || candidate.IsVirtual)
                    && SamePhysicalSignature(candidate.Parameters, candidate.Return,
                        slotParams, slotRet, refs, isValue))
                    return true;
            var currentArgs = current.Args ?? Array.Empty<TypeNode>();
            current = refs.ReferencedSupertypes(current)
                .Where(parent => !parent.isInterface)
                .Select(parent => SupertypeGraph.SubstOwnerTvs(parent.spec, currentArgs) as TypeNode.Fqn)
                .FirstOrDefault(parent => parent != null);
        }
        return false;
    }

    static bool HasOrdinaryPhysicalCollision(JsonArray methods, string physicalName, int methodArity,
        TypeNode[] slotParams, TypeNode slotRet, TypeNode[] ownArgs, ReferenceMetadataIndex refs,
        ValueTypeOracle isValue, bool includeNonVirtual)
    {
        foreach (var method in methods.OfType<JsonObject>())
        {
            if (Bool(method["static"]) || Str(method["name"]) != physicalName
                || KotlinPropertyAccessors.TryIdentity(method, out _, out _)
                || Str(method["vis"]) is not (null or "public")
                || (!includeNonVirtual && !OccupiesClrVirtualSlot(method))
                || ((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity
                || method["params"] is not JsonArray parameters || parameters.Count != slotParams.Length
                || TypeJson.Read(method["ret"]) is not TypeNode declaredRet)
                continue;
            var declaredParams = parameters.OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"]))
                .ToArray();
            if (declaredParams.Length == parameters.Count && declaredParams.All(type => type != null)
                && SamePhysicalSignature(
                    declaredParams.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownArgs)).ToArray(),
                    SupertypeGraph.SubstOwnerTvs(declaredRet, ownArgs),
                    slotParams, slotRet, refs, isValue))
                return true;
        }
        return false;
    }

    // A non-virtual class method is harmless when a private-final explicit DIM already owns the slot, but the CLR
    // rejects that same method spelling beside a public DIM body unless DotKt emits a separate virtual MethodImpl
    // bridge. Callers therefore include non-virtual candidates only when the selected body is callable. Keep the
    // narrower predicate aligned with ilemit's one-to-one MethodAttributes.Virtual projection.
    static bool OccupiesClrVirtualSlot(JsonObject method) =>
        Bool(method["override"]) || Bool(method["virtual"]) || Bool(method["abstract"])
        || Bool(method["objectOverride"]) || method["pendingOverrideOwner"] != null;

    static bool SamePhysicalSignature(TypeNode[] candidateParams, TypeNode candidateRet,
        TypeNode[] slotParams, TypeNode slotRet, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        if (candidateParams == null || candidateRet == null || candidateParams.Length != slotParams.Length)
            return false;
        for (var i = 0; i < candidateParams.Length; i++)
            if (Classify(slotParams[i], candidateParams[i], refs, isValue, returnPosition: false) != Fit.Same)
                return false;
        return Classify(slotRet, candidateRet, refs, isValue, returnPosition: true) == Fit.Same;
    }

    // THE REFERENCED ARM, walked implementer -> slot. A referenced supertype hands over no slot list, so the class's
    // own `overrides` markers are what name the members that must fill one: each says the OWNER the author wrote and
    // the MEMBER on it. For every own method claiming this supertype, the D1 reader answers that member's pre-erasure
    // declaration off the producing assembly, and the slot is then derived by the identical
    // `Subst(Erase(declared), typeArgs)` formula the local arm uses.
    //
    // Every `return`/`continue` here means "this pass has no opinion about that slot", never "give up on one that
    // needs filling": no reference index (a build with no references), a supertype whose arity the spec does not
    // match, a member the reader refuses (an ambiguous same-shape overload set, which it will not guess at), or a
    // parameter vector that does not read. A REFUSED position is respected exactly as it is on the argument axis —
    // the reader saw the declaration and declined to state it, and inventing a slot from the physical signature is
    // the derivation the refusal exists to prevent.


    enum Fit { Same, Bridge, Rewrite, Foreign }

    static bool HasExistingSlotBridge(JsonArray methods, string key, TypeNode.Fqn owner, string member, int arity,
        TypeNode[] parameters, TypeNode result, JsonArray typeParams, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        foreach (var bridge in methods.OfType<JsonObject>())
        {
            if (!KotlinPropertyAccessors.IsPhysicalSlotBridge(bridge)
                || bridge[key] is not JsonArray descriptors) continue;
            foreach (var descriptor in descriptors.OfType<JsonObject>())
            {
                if (TypeJson.Read(descriptor["owner"]) is not TypeNode descriptorOwner
                    || !BirTypeLowering.SamePhysicalSlotType(descriptorOwner, owner,
                        refs.Aliases, isValue, refs.PhysicalTypeNames, returnPosition: false,
                        nullableFrames: refs.NullableTypeFrames)
                    || Str(descriptor["member"]) != member
                    || Int(descriptor["arity"]) != arity || descriptor["params"] is not JsonArray ps
                    || TypeJson.Read(descriptor["ret"]) is not TypeNode ret) continue;
                if (SameMethodTypeParameterShape(descriptor["typeParams"] as JsonArray, typeParams,
                        Array.Empty<TypeNode>(), Array.Empty<TypeNode>())
                    && SamePhysicalSignature(ps.Select(TypeJson.Read).ToArray(), ret,
                        parameters, result, refs, isValue)) return true;
            }
        }
        return false;
    }

    static void AddImplDescriptor(JsonObject method, string key, JsonObject descriptor)
    {
        if (method[key] is not JsonArray descriptors)
        {
            descriptors = new JsonArray();
            method[key] = descriptors;
        }
        var encoded = descriptor.ToJsonString();
        if (!descriptors.Any(existing => existing?.ToJsonString() == encoded))
            descriptors.Add(descriptor);
    }

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
                refs?.PhysicalTypeNames, returnPosition, nullableFrames: refs?.NullableTypeFrames)) return Fit.Same;
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
            NullableRepresentationFrame.Role.Nullable or NullableRepresentationFrame.Role.NullableStorage;
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

    internal static void SelfTest()
    {
        SourceDeclarationSelfTest();
        ExactRenamedSlotSelfTest();
        var queryCarrier = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        if (Classify(queryCarrier, new TypeNode.Fqn("kotlin.String"), null, _ => false,
                returnPosition: false) != Fit.Bridge
            || !ErasureAligned(queryCarrier, new TypeNode.Fqn("kotlin.String")))
            throw new InvalidOperationException("Nullable object query slots lost their concrete implementation bridge");
        var localSlotOwners = SupertypeGraph.Collect(new[] { JsonNode.Parse("""
            {"types":[{"name":"LocalSlot","kind":"interface","methods":[]}]}
            """) });
        if (IsNativeSlotOwner("LocalSlot", localSlotOwners, _ => false)
            || IsNativeSlotOwner("ReferencedSlot", localSlotOwners, name => name == "ReferencedSlot")
            || !IsNativeSlotOwner("NativeSlot", localSlotOwners, _ => false))
            throw new InvalidOperationException("Physical-only slot capture admitted a selected Kotlin declaration");
        var constructedOverride = JsonNode.Parse("""
            {"name":"accept","vis":"public","params":[{"name":"value","type":{"t":"fqn","name":"ElementBox","args":[{"t":"tv","scope":"type","i":0}]}}],
             "overrides":[{"owner":{"t":"fqn","name":"Base"},"member":"accept","kind":"method","declarationId":"selected-base"}]}
            """).AsObject();
        var constructedOwner = new Def { Name = "Derived", Kind = "class", Node = new JsonObject() };
        var constructedView = new TypeNode.Fqn("ElementBox$star");
        var constructedViews = new OverrideViews();
        JsonObject SelectedConstructedOverride(IEnumerable<JsonObject> candidates, string identity = "selected-base") =>
            FindImplementer(constructedViews.Owner(constructedOwner), new Dictionary<string, Def>(), candidates.Select(constructedViews.Method), "Base", "NativeAccept", "accept",
                identity, null, null, 0, new TypeNode[] { constructedView }, null, Array.Empty<TypeNode>(),
                new TypeNode[] { new TypeNode.Tv("type", 0) }, carrierSeam: (slot, body) =>
                    slot == constructedView && body is TypeNode.Fqn { Name: "ElementBox", Args.Length: 1 })?.Dom;
        if (SelectedConstructedOverride(new[] { constructedOverride }) != constructedOverride
            || SelectedConstructedOverride(new[] { constructedOverride }, "other-base") != null
            || SelectedConstructedOverride(new[] { constructedOverride, (JsonObject)constructedOverride.DeepClone() }) != null)
            throw new InvalidOperationException("Renamed local slot lost its exact unchanged-source constructed override");
        var suspendSource = new JsonObject {
            [DeclarationIdentityBinding.Key] = "selected-suspend",
            [KotlinPropertyAccessors.SuspendSourceParamsKey] = new JsonArray(),
            [KotlinPropertyAccessors.SuspendSourceRetKey] = TypeJson.Fqn("kotlin.Unit"),
        };
        var coldSource = (JsonObject)suspendSource.DeepClone();
        coldSource[DeclarationIdentityBinding.Key] = "selected-suspend|cold";
        var otherSource = (JsonObject)suspendSource.DeepClone();
        otherSource[DeclarationIdentityBinding.Key] = "other-suspend";
        var missingIdentity = (JsonObject)suspendSource.DeepClone();
        missingIdentity.Remove(DeclarationIdentityBinding.Key);
        var missingSignature = (JsonObject)coldSource.DeepClone();
        missingSignature.Remove(KotlinPropertyAccessors.SuspendSourceParamsKey);
        if (!SameSelectedSuspendDeclaration(new[] { suspendSource, coldSource })
            || !SameSelectedSuspendDeclaration(new[] { coldSource, suspendSource })
            || SameSelectedSuspendDeclaration(new[] { suspendSource, otherSource })
            || SameSelectedSuspendDeclaration(new[] { suspendSource, missingIdentity })
            || SameSelectedSuspendDeclaration(new[] { suspendSource, missingSignature }))
            throw new InvalidOperationException("Inherited suspend projections lost their exact source identity proof");
        var frame = new NullableRepresentationFrame(2, new[] { 0 },
            physicalOrder: new[] { 3, 0, 4, 1, 2, 5 }, storageIndices: new[] { 0, 1 },
            nullableStorageIndices: new[] { 0 });
        var source = new TypeNode.Tv("type", 0);
        var ordinary = frame.Variable(source, NullableRepresentationFrame.Role.Ordinary);
        var storage = frame.Variable(source, NullableRepresentationFrame.Role.Storage);
        var other = frame.Variable(new TypeNode.Tv("type", 1), NullableRepresentationFrame.Role.Storage);
        if (!SameSourceRepresentationVariables(storage, ordinary, frame)
            || SameSourceRepresentationVariables(other, ordinary, frame)
            || SameSourceRepresentationVariables(new TypeNode.Tv("method", storage.I), ordinary, frame)
            || SameSourceRepresentationVariables(new TypeNode.Array(storage), new TypeNode.Array(ordinary), frame)
            || SameSourceRepresentationVariables(frame.Variable(source, NullableRepresentationFrame.Role.Nullable), ordinary, frame)
            || SameSourceRepresentationVariables(storage, ordinary, null))
            throw new InvalidOperationException("Override representation seam lost declaration-owned source identity");
        var permutedMethod = new JsonObject {
            ["typeParams"] = new JsonArray(Enumerable.Range(0, frame.PhysicalArity)
                .Select(index => (JsonNode)new TypeParameterDeclaration("P" + index).ToDom()).ToArray()),
            [NullableRepresentationTypes.MethodFrameKey] = frame.ToJson().ToJsonString(),
            [NullableGenericErasure.MethodTypeParameterBoundsPre] =
                "{\"bounds\":{\"0\":[{\"t\":\"fqn\",\"name\":\"FirstBound\"}],\"1\":[{\"t\":\"fqn\",\"name\":\"SecondBound\"}]}}",
        };
        var authoredParameters = AuthoredMethodTypeParameters(permutedMethod);
        var semanticParameters = SemanticMethodTypeParameters(permutedMethod);
        if (authoredParameters.Count != frame.SourceArity
            || TypeJson.Read(authoredParameters[0]["constraints"][0]) != new TypeNode.Fqn("FirstBound")
            || TypeJson.Read(authoredParameters[1]["constraints"][0]) != new TypeNode.Fqn("SecondBound")
            || !JsonNode.DeepEquals(semanticParameters[frame.SourcePosition(0)], authoredParameters[0])
            || !JsonNode.DeepEquals(semanticParameters[frame.SourcePosition(1)], authoredParameters[1]))
            throw new InvalidOperationException("Source constraint comparison consumed companion rows or ignored the recorded physical order");
        var functionSlot = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), new[] { ordinary });
        var functionBody = functionSlot with { Params = new[] { storage } };
        if (!SameSourceFunctionRepresentation(functionSlot, functionBody, frame)
            || SameSourceFunctionRepresentation(functionSlot, functionSlot with { Params = new[] { other } }, frame)
            || SameSourceFunctionRepresentation(functionSlot with { Clr = "System.Action" },
                functionBody with { Clr = "System.Action" }, frame)
            || SameSourceFunctionRepresentation(new TypeNode.Array(functionSlot), new TypeNode.Array(functionBody), frame))
            throw new InvalidOperationException("Function override carrier seam lost source identity or admitted a nominal conversion");
        var valueSignature = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"),
            new TypeNode[] { new TypeNode.Fqn("kotlin.Unit") }, Clr: "System.Func");
        var voidSignature = valueSignature with { Ret = new TypeNode.Fqn("void"), Clr = "System.Action" };
        if (ClrMemberResolution.SameInterfaceSlotType(valueSignature, voidSignature)
            || SameSourceFunctionRepresentation(valueSignature, voidSignature, null)
            || ClrMemberResolution.SameInterfaceSlotType(BirTypeLowering.DelegateFqnOf(valueSignature),
                BirTypeLowering.DelegateFqnOf(voidSignature)))
            throw new InvalidOperationException("Function signature bridge confused source identity with delegate equivalence");
        var carrier = new TypeNode.Fn(false, new TypeNode.Fqn("object"),
            new TypeNode[] { new TypeNode.Fqn("object") }, Clr: "System.Func");
        var integer = new TypeNode.Fqn("System.Int32");
        if (IsNullableValueSlot(carrier, new TypeNode.Nullable(carrier), null, _ => false, false)
            || !IsNullableValueSlot(integer, new TypeNode.Nullable(integer), null,
                type => type.Name == "System.Int32", false))
            throw new InvalidOperationException("Nullable bridge confused reference annotations with CLR nullable values");
        var slotMethod = JsonNode.Parse("""
          {"typeParams":[{"name":"R","constraints":[{"t":"tv","scope":"type","i":0}]},{"name":"P"}]}
          """).AsObject();
        var implementation = JsonNode.Parse("""
          {"typeParams":[{"name":"R","constraints":[{"t":"fqn","name":"Animal"}]},
            {"name":"P","constraints":[{"t":"fqn","name":"Animal"}]}]}
          """).AsObject();
        var storageFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        slotMethod[NullableRepresentationTypes.MethodFrameKey] = storageFrame.ToJson().ToJsonString();
        implementation[NullableRepresentationTypes.MethodFrameKey] = storageFrame.ToJson().ToJsonString();
        var ownerArguments = new TypeNode[] { new TypeNode.Fqn("Animal") };
        if (!SameMethodTypeParameterShape(SourceMethodTypeParameters(slotMethod), SourceMethodTypeParameters(implementation),
                ownerArguments, Array.Empty<TypeNode>())
            || SameMethodTypeParameterShape(slotMethod["typeParams"].AsArray(), implementation["typeParams"].AsArray(),
                ownerArguments, Array.Empty<TypeNode>())
            || implementation["typeParams"][1]["constraints"].AsArray().Count != 1)
            throw new InvalidOperationException("Override source selection consumed or mutated a physical companion constraint");
        OwnerConstrainedMethodLowering.RecordOverride(implementation, slotMethod["typeParams"].AsArray(),
            ownerArguments, new JsonArray(new TypeParameterDeclaration("T").ToDom()));
        var overrideBounds = JsonNode.Parse(implementation[OwnerConstrainedMethodLowering.OverrideBoundsKey].GetValue<string>()).AsArray();
        if (overrideBounds.Count != 2
            || !overrideBounds.OfType<JsonObject>().Select(row => row["index"].GetValue<int>()).SequenceEqual(new[] { 0, 1 }))
            throw new InvalidOperationException("Selected override failed to transfer its bound plan to the matching storage companion");
        Console.WriteLine("[override representation seam] self-test OK (permuted frame, distinct source, scope, nested rejection)");
    }

    static void ExactRenamedSlotSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        foreach (var kind in new[] { "interface", "class" })
        {
            var root = JsonNode.Parse("""
                {"types":[
                  {"name":"Element","kind":"interface","methods":[]},
                  {"name":"Base","kind":"class","abstract":true,"methods":[
                    {"name":"NativeAccept","kotlinSourceMember":"accept","declarationId":"selected-base",
                     "virtual":true,"abstract":true,"vis":"public","params":[{"name":"value","type":{"t":"fqn","name":"System.Object"}}],
                     "ret":{"t":"fqn","name":"kotlin.Boolean"}}]},
                  {"name":"Derived","kind":"class","methods":[
                    {"name":"accept","declarationId":"selected-body","override":true,"virtual":true,"vis":"public",
                     "params":[{"name":"value","type":{"t":"fqn","name":"System.Object"}}],"ret":{"t":"fqn","name":"kotlin.Boolean"},
                     "body":[{"k":"return","value":{"k":"const","type":{"t":"fqn","name":"kotlin.Boolean"},"value":false}}],
                     "overrides":[{"owner":{"t":"fqn","name":"Base"},"member":"accept","kind":"method","declarationId":"selected-base"}]}]}]}
                """).AsObject();
            root["types"][1]["kind"] = kind;
            if (kind == "interface") root["types"][2]["interfaces"] = new JsonArray(TypeJson.Fqn("Base"));
            else root["types"][2]["base"] = TypeJson.Fqn("Base");
            ApplyAll(new[] { root }, _ => false, refs, new HashSet<string> { "Element", "Base", "Derived" },
                refBuild: false, new GenericRepresentationPolicy(new Dictionary<string, string>()));
            var descriptors = root["types"][2]["methods"][0][kind == "interface" ? "clrInterfaceImpls" : "clrBaseImpls"] as JsonArray;
            if (descriptors is not { Count: 1 } || Str(descriptors[0]["member"]) != "NativeAccept"
                || TypeJson.Read(descriptors[0]["params"][0]) != new TypeNode.Fqn("System.Object"))
                throw new InvalidOperationException("Exact renamed " + kind + " slot lost its selected MethodImpl");
            var implementation = root["types"][2]["methods"][0].AsObject();
            var modified = TypeJson.Write(new TypeNode.Mod(false, new TypeNode.Fqn("SignatureMarker"),
                new TypeNode.Fqn("System.Object")));
            implementation["params"][0]["type"] = modified.DeepClone();
            DeclarationSignatureIdentity.BindSelectedOverrides(new[] { root },
                new Dictionary<string, JsonArray> { ["selected-body"] = new JsonArray(modified) }, refs, _ => false);
            var relationKey = kind == "interface" ? "clrInterfaceImpls" : "clrBaseImpls";
            var members = root["types"][2]["methods"].AsArray();
            if (members.Count != 2 || implementation[relationKey]?.AsArray().Count != 0
                || TypeJson.Read(members[1]["params"][0]["type"]) != new TypeNode.Fqn("System.Object")
                || members[1][relationKey]?.AsArray().Count != 1
                || Str(members[1][relationKey][0]["member"]) != "NativeAccept")
                throw new InvalidOperationException("Late signature allocation retained a modified body on an exact " + kind + " slot");
        }
    }

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

    static bool IsSuspendMethod(JsonObject method) =>
        method["mods"] is JsonObject mods && Bool(mods["suspend"]);

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


    internal static bool SameMethodTypeParameterShape(JsonArray slotTypeParams, JsonArray implementationTypeParams,
        TypeNode[] slotOwnerArgs, TypeNode[] implementationOwnerArgs, Func<TypeNode, TypeNode> constraintType = null)
        => MethodTypeParameterShapeKey(slotTypeParams, slotOwnerArgs, constraintType)
            == MethodTypeParameterShapeKey(implementationTypeParams, implementationOwnerArgs, constraintType);

    internal static bool SameSourceMethodTypeParameterShape(BirTypeParameter[] slotTypeParams,
        BirTypeParameter[] implementationTypeParams, TypeNode[] slotOwnerArgs, TypeNode[] implementationOwnerArgs)
        => SourceMethodTypeParameterShapeKey(slotTypeParams, slotOwnerArgs)
            == SourceMethodTypeParameterShapeKey(implementationTypeParams, implementationOwnerArgs);

    static JsonArray SourceMethodTypeParameters(JsonObject method) => ParametersDom(SourceParameters(new OverrideMethod(method)));
    static JsonArray AuthoredMethodTypeParameters(JsonObject method) => ParametersDom(AuthoredParameters(new OverrideMethod(method)));
    internal static JsonArray SemanticMethodTypeParameters(JsonObject method) => ParametersDom(SemanticParameters(new OverrideMethod(method)));

    internal static string MethodTypeParameterShapeKey(JsonArray typeParams, TypeNode[] ownerArgs,
        Func<TypeNode, TypeNode> constraintType = null)
        => ConstraintShapeKey(typeParams?.Select(parameter => (
            Constraints: ((parameter as JsonObject)?["constraints"] as JsonArray)?.Select(TypeJson.Read)
                .Where(constraint => constraint != null) ?? Enumerable.Empty<TypeNode>(),
            Specials: ((parameter as JsonObject)?["specialConstraints"] as JsonArray)?.Select(Str)
                .Where(value => value != null) ?? Enumerable.Empty<string>())), ownerArgs, constraintType);

    internal static string SourceMethodTypeParameterShapeKey(BirTypeParameter[] typeParams, TypeNode[] ownerArgs,
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
    static bool OverridesInto(JsonObject method, IReadOnlyDictionary<string, Def> defs, string supName, string member,
        string memberKind, string slotDeclarationId) =>
        OverridesInto(new OverrideMethod(method), defs, supName, member, memberKind, slotDeclarationId);

    static string SourceDeclarationId(string declarationId)
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

    static bool ReachesFromDeclaredInterface(Def cls, TypeNode.Fqn slotOwner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs)
    {
        foreach (var direct in cls.Interfaces)
            if (SupertypeGraph.Reaches(direct, slotOwner, defs, refs))
                return true;
        return false;
    }

    static bool ReachesFromDeclaredPhysicalInterface(Def cls, TypeNode.Fqn slotOwner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        IReadOnlySet<string> localTypeNames)
    {
        // A referenced graph is already in CLR vocabulary, while the selected owner may still carry Kotlin type
        // arguments. Compare both physical projections, retaining exact construction and excluding the base chain.
        TypeNode Physical(TypeNode type) => ClrMemberResolution.MethodImplComparisonType(
            BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false, localTypeNames,
                nullableFrames: refs.NullableTypeFrames));
        var physicalSlot = Physical(slotOwner);
        return SupertypeGraph.Reachable(new Def { Interfaces = cls.Interfaces }, defs, refs)
            .Any(edge => edge.isInterface && Equals(Physical(edge.spec), physicalSlot));
    }


    // The bridge: the slot's exact signature. Local Kotlin defaults forward virtually so a further-derived override
    // still runs; a referenced CLR DIM is called non-virtually to avoid redispatch to the colliding class method.
    // Each argument is narrowed out of the slot's `object` into the declared type
    // (`unbox.any` for a value, `castclass` for a reference) and the result widened back.
    // Only the final physical phase owns a frozen implementation descriptor; earlier suspend/value adapters still
    // carry semantic signatures that the ordinary representation pass must project.
    internal static JsonObject BuildBridge(Def cls, JsonObject impl, TypeNode[] slotParams, TypeNode slotRet, string bridgeName,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, TypeNode.Fqn callOwner = null,
        bool virtualCall = true, string callMember = null, bool unitValueReturn = false,
        bool physicalImplementation = false)
    {
        var declParams = impl["params"] as JsonArray ?? new JsonArray();
        var bridgeParams = new JsonArray();
        var callArgs = new JsonArray();
        var callSig = new JsonArray();
        for (var i = 0; i < slotParams.Length; i++)
        {
            var p = declParams[i] as JsonObject;
            var pn = Str(p?["name"]) ?? "p" + i;
            var bp = new JsonObject { ["name"] = pn, ["type"] = TypeJson.Write(slotParams[i]) };
            var declT = TypeJson.Read(p?["type"]);
            if (declT != null && !declT.Equals(slotParams[i]))
            {
                CarryKotlinType(bp, "nullableGeneric", "nullableFlags", declT, slotParams[i], isValue);
                bridgeParams.Add(bp);
                callArgs.Add(IsNullableValueSlot(slotParams[i], declT, refs, isValue,
                        returnPosition: false) && declT is TypeNode.Nullable nullable
                    ? new JsonObject
                    {
                        ["k"] = "nullableWrap",
                        [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = physicalImplementation,
                        ["elem"] = TypeJson.Write(nullable.Of),
                        ["e"] = new JsonObject { ["k"] = "local", ["name"] = pn },
                    }
                    : new JsonObject
                    {
                        ["k"] = "cast",
                        [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = physicalImplementation,
                        ["type"] = TypeJson.Write(declT),
                        ["e"] = new JsonObject { ["k"] = "local", ["name"] = pn },
                    });
            }
            else
            {
                bridgeParams.Add(bp);
                callArgs.Add(new JsonObject { ["k"] = "local", ["name"] = pn });
            }
            callSig.Add(p?["type"]?.DeepClone());
        }

        var implRet = TypeJson.Read(impl["ret"]);
        var ownArgs = ClassOwnArgs(cls);
        var owner = callOwner ?? new TypeNode.Fqn(cls.Name, ownArgs.Length == 0 ? null : ownArgs);
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(owner),
            ["virtual"] = virtualCall,
            // Synthesized by bir2cir with its exact CLR declaration owner: the later inherited-owner pass must not
            // reinterpret it as an ordinary Kotlin receiver call and rebind it to the slot this bridge implements.
            ["clrOwnerResolved"] = true,
            [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = physicalImplementation,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = callMember ?? Str(impl["name"]),
            ["sig"] = callSig,
            ["calleeParams"] = impl["inheritedDeclarationSignature"]?.DeepClone()
                ?? FunctionSignatureIdentity.Signature(declParams),
            // The implementation has already been instantiated in this bridge's owner frame, including inherited
            // and default targets. Preserve that call-site result rather than substituting the target owner twice.
            ["sty"] = TypeJson.Write(implRet),
            ["dynRet"] = TypeJson.Write(implRet),
            ["ret"] = TypeJson.Write(implRet),
            ["args"] = callArgs,
        };
        if (impl["inheritedDeclarationId"] is JsonNode declarationId)
            call[DeclarationIdentityBinding.Key] = declarationId.DeepClone();
        else if (impl[DeclarationIdentityBinding.Key] is JsonNode implementationId)
            call[DeclarationIdentityBinding.Key] = implementationId.DeepClone();
        if (IsSuspendMethod(impl)) call["suspendCall"] = true;
        if (impl["typeParams"] is JsonArray methodTps && methodTps.Count > 0)
        {
            var typeArgs = new JsonArray();
            for (var i = 0; i < methodTps.Count; i++) typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));
            call["typeArgs"] = typeArgs;
        }
        var body = new JsonArray();
        var retCarried = false;
        if (IsVoid(slotRet) && !unitValueReturn)
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = call });
        else
        {
            JsonNode value = call;
            if (implRet != null && !implRet.Equals(slotRet))
            {
                value = IsNullableValueSlot(slotRet, implRet, refs, isValue, returnPosition: true)
                        && implRet is TypeNode.Nullable nullable
                    ? new JsonObject
                    {
                        ["k"] = "nullableValue",
                        [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = physicalImplementation,
                        ["elem"] = TypeJson.Write(nullable.Of),
                        ["e"] = value,
                    }
                    : new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(slotRet), ["e"] = value,
                        [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = physicalImplementation };
                retCarried = true;
            }
            body.Add(new JsonObject { ["k"] = "return", ["value"] = value });
        }

        var bridge = new JsonObject
        {
            ["name"] = bridgeName,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            // Keep the implementation private; the generated fact below separately excludes this physical adapter
            // from Kotlin source projection, including when dll2klib processes its MethodImpl rows.
            ["vis"] = "private",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(slotRet),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
            ["generated"] = true,
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
            ["clrInterfaceImpls"] = new JsonArray(),
            ["clrBaseImpls"] = new JsonArray(),
        };
        if (cls.Kind == "interface")
            bridge[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true;
        if (unitValueReturn) bridge[BirTypeLowering.ValueReturnKey] = true;
        if (IsSuspendMethod(impl))
        {
            bridge["mods"] = new JsonObject { ["suspend"] = true };
            bridge["suspendRet"] = TypeJson.Write(slotRet);
            bridge["suspendResult"] = impl["suspendResult"]?.DeepClone()
                ?? throw new InvalidOperationException("suspend slot bridge source has no logical result");
        }
        if (retCarried && implRet != null)
            CarryKotlinType(bridge, "nullableGenericRet", "retNullableFlags", implRet, slotRet, isValue);
        if (impl["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return MaterializedExecutable.Normalize(bridge);
    }

    internal static JsonObject BuildSignatureBridge(Def owner, JsonObject implementation, TypeNode[] signature,
        TypeNode result, string name, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
    {
        // Reuse the ordinary override conversion/forwarding boundary. Identity modifiers belong only to the
        // emitted method signature; arguments and conversions keep the underlying CLR value representation.
        var valueImplementation = implementation.DeepClone().AsObject();
        valueImplementation["inheritedDeclarationSignature"] = FunctionSignatureIdentity.Signature(
            (JsonArray)implementation["params"]);
        foreach (var parameter in ((JsonArray)valueImplementation["params"]).OfType<JsonObject>())
            parameter["type"] = TypeJson.Write(SignatureValueTypes.Of(TypeJson.Read(parameter["type"])));
        var bridge = BuildBridge(owner, valueImplementation, signature.Select(SignatureValueTypes.Of).ToArray(),
            result, name, isValue, refs);
        for (var index = 0; index < signature.Length; index++)
            bridge["params"][index]["type"] = TypeJson.Write(signature[index]);
        return bridge;
    }

    // Preserve the Kotlin value type represented by a physical bridge slot through both metadata channels:
    // `[KotlinNullableGeneric]` carries its type tree and the NRT byte carries its outer nullability. Reference-side
    // representation consumers need both facts even though the generated MethodDef is not a new Kotlin declaration.
    static void CarryKotlinType(JsonObject decl, string factKey, string flagsKey, TypeNode kotlin, TypeNode slot,
        ValueTypeOracle isValue)
    {
        decl[factKey] ??= TypeNode.ToJson(kotlin);
        // The byte walk describes the PHYSICAL slot, which is the bare `object` this bridge states, carrying the
        // Kotlin type's outer nullability onto it. Walking the Kotlin type instead would stamp nothing for an `Int?`
        // — a value type's `?` is structural `Nullable<V>` and contributes no NRT byte — losing the nullable value fact.
        var shape = kotlin is TypeNode.Nullable ? new TypeNode.Nullable(slot) : slot;
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(shape, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    static JsonObject ImplDescriptor(TypeNode.Fqn spec, string member, int arity, TypeNode[] slotParams,
        TypeNode slotRet, JsonArray typeParams = null, bool unitValueReturn = false)
    {
        var ps = new JsonArray();
        foreach (var p in slotParams) ps.Add(TypeJson.Write(p));
        var descriptor = new JsonObject
        {
            ["owner"] = TypeJson.Write(spec),
            ["member"] = member,
            // The slot's METHOD GENERIC ARITY. Without it the emitter takes the arity from whichever slot it is
            // matching, so a directive for the arity-0 member also answers for the arity-1 one.
            ["arity"] = arity,
            ["params"] = ps,
            ["ret"] = TypeJson.Write(slotRet),
        };
        if (typeParams != null) descriptor["typeParams"] = typeParams.DeepClone();
        if (unitValueReturn) descriptor[BirTypeLowering.ValueReturnKey] = true;
        return descriptor;
    }

    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static int Int(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
