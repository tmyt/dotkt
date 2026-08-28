using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
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
// Being private, the bridge is not part of the Kotlin surface: dll2klib projects public and protected members only, so
// the re-imported type carries the one `accept(x: Int?)` the author wrote, with a physical member behind it. A public
// bridge would instead re-import as a second `accept(x: Any?)` overload and make `IntSink().accept("s")` compile.
// Private is necessary and not sufficient — see CarryKotlinType for the one path that re-surfaces a private body.
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
static class KotlinOverrideSlotBridge
{
    // THE TWO HALVES RUN AT DIFFERENT POINTS, because their inputs are valid at different points.
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
    // The declaration half, over every file at once (a base may be declared in another file of this compilation).
    public static void PropagateErasedSlots(IEnumerable<JsonNode> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs) => ApplyAll(roots, isValue, refs, emitBridges: false, localTypeNames: null);

    // The bridge half.
    public static void ApplyAll(IEnumerable<JsonNode> roots, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypeNames,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots = null) =>
        ApplyAll(roots, isValue, refs, emitBridges: true, localTypeNames, covariantBridgedSlots);

    static void ApplyAll(IEnumerable<JsonNode> roots, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        bool emitBridges, IReadOnlySet<string> localTypeNames,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots = null)
    {
        var defs = SupertypeGraph.Collect(roots);
        // The source accessor relation is needed only between the two halves of this one pass. Keep it in memory by
        // JsonObject identity rather than minting another BIR/CIR identifier or parsing the bridge's metadata
        // association. The relation cannot escape this ApplyAll invocation.
        var exactBridgeSources = emitBridges
            ? new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance)
            : null;
        // An earlier physical-slot owner (notably CovariantInterfaceReturnBridge) can already have authored an exact
        // interface property bridge. Its carrier explicitly preserves the selected source association; seed this
        // pass-local hand-off from that fact so inherited-DIM collision repair can consume the bridge without asking
        // this pass to synthesize a duplicate or reconstructing the property relation from CLR names.
        if (exactBridgeSources != null)
            foreach (var method in defs.Values.Where(def => def.Kind == "interface")
                         .SelectMany(def => def.Methods.OfType<JsonObject>()))
                if (Bool(method[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey])
                    && Str((method[KotlinPropertyAccessors.MetadataCarrierKey] as JsonObject)?["sourceAssociation"])
                        is string sourceAssociation)
                    exactBridgeSources[method] = sourceAssociation;
        foreach (var cls in defs.Values.Where(d => d.Kind is "class" or "interface").ToList())
            ApplyClass(cls, defs, isValue, refs, emitBridges, exactBridgeSources, localTypeNames,
                covariantBridgedSlots);
        // A class-level inherited-DIM bridge consumes the exact MethodImpl descriptor synthesized on its interface.
        // Declarations may appear in either order and in different input files, so first finish every interface/class's
        // own slot allocation above, then inspect classes. Reading the live method arrays during the first loop would
        // make correctness depend on source/file order.
        if (emitBridges)
            foreach (var cls in defs.Values.Where(d => d.Kind == "class").ToList())
            {
                if (cls.Node["methods"] is not JsonArray methods) continue;
                var inheritedBridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                var ordinal = 0;
                AddInheritedDefaultPropertyBridges(cls, defs, methods, ClassOwnArgs(cls), refs, isValue,
                    exactBridgeSources, inheritedBridges, ref ordinal, localTypeNames);
                AddInheritedDefaultMethodBridges(cls, defs, methods, ClassOwnArgs(cls), refs, isValue,
                    inheritedBridges, ref ordinal, localTypeNames);
            }
    }

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, bool emitBridges, IDictionary<JsonObject, string> exactBridgeSources,
        IReadOnlySet<string> localTypeNames,
        IReadOnlySet<CovariantInterfaceReturnBridge.BridgedSlot> covariantBridgedSlots)
    {
        if (cls.Node["methods"] is not JsonArray methods) return;
        var ownArgs = ClassOwnArgs(cls);
        var bridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var ordinal = 0;

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
            TypeNode[] slotParams, TypeNode slotRet, JsonObject impl, JsonArray slotTypeParams = null)
        {
            // An abstract interface slot reached only through a base class already has that base's mapping. A derived
            // declaration does not acquire a fresh MethodImpl unless the source class re-lists the interface; this is
            // what keeps an inherited CLR explicit implementation intact. A DEFAULT interface slot is different:
            // Kotlin lets a grandchild override the DIM without re-listing the interface, and CLR needs an exact
            // per-type MethodImpl for that override. `semanticSpec` deliberately remains in Kotlin vocabulary:
            // `descriptorSpec` may already be the physical owner of @ClrTypeAlias.
            var reimplementsInterface = supIsInterface && cls.Kind == "class"
                && ReachesFromDeclaredInterface(cls, semanticSpec, defs, refs);
            var overridesInheritedDefault = supIsInterface && cls.Kind == "class"
                && !reimplementsInterface && interfaceSlotHasDefault;
            if (supIsInterface && cls.Kind == "class"
                && !reimplementsInterface && !overridesInheritedDefault)
                return;

            var declParams = impl["params"] as JsonArray;
            var declRet = TypeJson.Read(impl["ret"]);
            if (declParams == null || declRet == null || declParams.Count != slotParams.Length) return;

            var fit = new Fit[slotParams.Length];
            for (var i = 0; i < slotParams.Length; i++)
            {
                var declT = TypeJson.Read((declParams[i] as JsonObject)?["type"]);
                if (declT == null) { fit = null; break; }
                fit[i] = Classify(slotParams[i], SupertypeGraph.SubstOwnerTvs(declT, ownArgs), refs, isValue,
                    returnPosition: false);
            }
            // A parameter difference this erasure did not create belongs to whatever pass did create it.
            if (fit == null || fit.Contains(Fit.Foreign)) return;
            var retFit = Classify(slotRet, SupertypeGraph.SubstOwnerTvs(declRet, ownArgs), refs, isValue,
                returnPosition: true);
            if (retFit == Fit.Foreign)
            {
                // The covariant pass now resolves referenced Kotlin declarations too. Its explicit hand-off says
                // exactly which slot obligation already owns that foreign return divergence; do not allocate the same
                // MethodImpl a second time here. Other foreign property/Nothing shapes retain this pass's
                // existing referenced-slot handling (not every physical-vocabulary difference is Kotlin covariance).
                if (covariantBridgedSlots?.Contains(CovariantInterfaceReturnBridge.BridgedSlotKey(
                        impl, descriptorSpec, descriptorMember,
                        (impl["typeParams"] as JsonArray)?.Count ?? 0,
                        slotParams, slotRet, refs, isValue)) == true)
                    return;
                if (referencedSlot && NodeType.IsNothing(declRet)) retFit = Fit.Bridge;
                else
                {
                    if (!fit.Any(f => f is Fit.Bridge or Fit.Rewrite)
                        && !(referencedSlot && supIsInterface && propertyAccessor == "get")) return;
                    retFit = Fit.Bridge;
                }
            }

            // At the logical suspend signature, `object <- Int?` is a bridgeable bare seam. Its public CLR
            // projection is `Task<object> <- Task<Nullable<Int>>`, however, and invariant Task<T> cannot be bridged
            // or cast. State the selected slot result before suspend lowering so it builds Task<object>, TCS<object>,
            // and the boxing body consistently. The cold entry remains object-returning independently.
            if (!emitBridges && IsSuspendMethod(impl) && retFit == Fit.Bridge
                && ErasureAligned(slotRet, SupertypeGraph.SubstOwnerTvs(declRet, ownArgs)))
                impl[KotlinPropertyAccessors.SuspendTaskResultKey] = TypeJson.Write(slotRet);

            // A position no conversion reaches moves the DECLARATION onto the slot's shape — the only option the
            // CLR leaves under a constructed generic — carrying its Kotlin surface on the round-trip channels.
            for (var i = 0; i < slotParams.Length; i++)
                if (fit[i] == Fit.Rewrite && declParams[i] is JsonObject po)
                    Rewrite(po, "type", "nullableGeneric", "nullableFlags", slotParams[i], isValue);
            if (retFit == Fit.Rewrite) Rewrite(impl, "ret", "nullableGenericRet", "retNullableFlags", slotRet, isValue);

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
                (cls.Kind == "interface" && !Bool(impl["abstract"])
                    || cls.Kind == "class" && reimplementsInterface
                        && descriptorMember != Str(impl["name"])
                    || cls.Kind == "class" && overridesInheritedDefault);
            var needsExplicitSlot = needsExplicitPropertySlot || needsExactInterfaceSlot;
            var constructedSlotTypeParams = SubstituteOwnerTypeParameterConstraints(
                slotTypeParams, semanticSpec.Args ?? Array.Empty<TypeNode>());
            // The declaration-move half still sees the original Kotlin supertype graph. Star-projection erasure may
            // add a synthesized existential view while retaining the class's direct interface edge before the bridge
            // half runs. Both are real CLR obligations and therefore need independently resolved MethodImpl facts.
            // An exact-signature class accessor needs no forwarding body, so retain its fully-resolved MethodImpl now;
            // waiting would discard the only direct statement of the external property slot. Signature bridges and
            // interface default bodies remain structural work for the late half.
            if (!emitBridges)
            {
                if (!needsSignatureBridge && needsExplicitPropertySlot && cls.Kind != "interface")
                {
                    impl["virtual"] = true;
                    var descriptor = ImplDescriptor(descriptorSpec, descriptorMember,
                        (impl["typeParams"] as JsonArray)?.Count ?? 0, slotParams, slotRet,
                        constructedSlotTypeParams);
                    AddImplDescriptor(impl, "clrInterfaceImpls", descriptor);
                }
                return;
            }
            if (!needsSignatureBridge && !needsExplicitSlot)
                return;
            if (!needsSignatureBridge && cls.Kind != "interface")
            {
                impl["virtual"] = true;
                var descriptor = ImplDescriptor(descriptorSpec, descriptorMember,
                    (impl["typeParams"] as JsonArray)?.Count ?? 0, slotParams, slotRet,
                    constructedSlotTypeParams);
                AddImplDescriptor(impl, "clrInterfaceImpls", descriptor);
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
            var key = identityName + "`" + arity + "(" + string.Join(",", slotParams.Select(SupertypeGraph.TypeKey)) + ")->" + SupertypeGraph.TypeKey(slotRet)
                      + "{" + Str(impl["name"]) + "<"
                      + MethodTypeParameterShapeKey(impl["typeParams"] as JsonArray, ownArgs)
                      + ">(" + body + ")}";
            // A private-final MethodDef used as an interface MethodImpl body belongs to one exact declaration slot.
            // Sharing that final body across an inherited chain (I::m and I.Base::m) makes the derived interface's
            // MethodImpl graph invalid on CoreCLR even though both descriptors forward to the same Kotlin declaration.
            // Classes may validly map one body to several independent interface slots, so isolate only this explicit-
            // interface-implementation shape by the complete declaration owner/member identity.
            if (cls.Kind == "interface")
                key += "[slot:" + SupertypeGraph.TypeKey(descriptorSpec) + "::" + descriptorMember + "]";
            if (!bridges.TryGetValue(key, out var bridge))
            {
                var bridgeOrdinal = ordinal++;
                bridge = BuildBridge(cls, impl, slotParams, slotRet,
                    $"dotkt$ovslot${SafeName(identityName)}${bridgeOrdinal}", isValue, refs);
                if (propertyAccessor == null)
                    RoundtripMetadata.AddSourceMethodIdentity(bridge, identityName);
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
            var bridgeDescriptor = ImplDescriptor(descriptorSpec, descriptorMember, arity, slotParams, slotRet,
                constructedSlotTypeParams);
            AddImplDescriptor(bridge, supIsInterface ? "clrInterfaceImpls" : "clrBaseImpls", bridgeDescriptor);
        }

        foreach (var (spec, supIsInterface) in SupertypeGraph.Reachable(cls, defs, refs))
        {
            if (!defs.TryGetValue(spec.Name, out var sup))
            {
                // A referenced BASE CLASS reaches the same arm; only its wiring differs (a MethodImpl against the
                // constructed base rather than the interface), and the emitter resolves that base externally.
                FillFromReference(cls, defs, spec, supIsInterface, methods, ownArgs, isValue, refs,
                    (owner, isInterface, referenced, identity, member, accessor, parameters, ret, implementation,
                            slotTypeParams, slotHasDefault) =>
                        Fill(spec, owner, isInterface, referenced, slotHasDefault, identity, member, accessor,
                            parameters, ret, implementation, slotTypeParams));
                continue;
            }
            var supArgs = SupertypeGraph.EffectiveArgs(spec, sup.Arity);
            if (supArgs == null) continue;

            foreach (var slot in sup.Methods.OfType<JsonObject>().ToList())
            {
                // Physical MethodImpl bodies are not Kotlin declarations and therefore cannot introduce another
                // source override slot in a derived type. Re-consuming one here can make a derived interface target
                // the private/final bridge of its base interface, which is not a valid declaration MethodDef.
                if (Bool(slot["static"]) || KotlinPropertyAccessors.IsPhysicalSlotBridge(slot)
                    || Str(slot["name"]) is not string name
                    || slot["params"] is not JsonArray slotParamNodes) continue;
                var methodArity = (slot["typeParams"] as JsonArray)?.Count ?? 0;
                // `Subst(Erase(declared), typeArgs)` and never `Erase(Subst(...))` — a substituted `Nullable(kotlin.Int)`
                // has no type variable left to erase and would state the wrong slot. The erasure is idempotent, so
                // this reads the same slot before the sweep has run and after.
                var rawSlotParams = slotParamNodes.OfType<JsonObject>()
                    .Select(p => TypeJson.Read(p["type"])).ToArray();
                var semanticSlotParams = rawSlotParams
                    .Select(t => t == null ? null : SupertypeGraph.SubstOwnerTvs(t, supArgs)).ToArray();
                var slotParams = rawSlotParams
                    .Select(t => t == null ? null : SupertypeGraph.SubstOwnerTvs(
                        NullableGenericErasure.EraseNullableTv(t, isValue), supArgs)).ToArray();
                var slotRet0 = TypeJson.Read(slot["ret"]);
                if (slotParams.Any(p => p == null) || slotRet0 == null) continue;
                var slotRet = SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(slotRet0, isValue), supArgs);

                KotlinPropertyAccessors.TryIdentity(slot, out var propertyName, out var accessorKind);
                var semanticName = propertyName ?? Str(slot[DeclarationRename.SourceMemberKey])
                    ?? Str(slot[FBoundStarProjectionErasure.SourceMemberKey]) ?? name;
                if (Implementer(cls, defs, methods, spec.Name, name, semanticName,
                    Str(slot[DeclarationIdentityBinding.Key]), propertyName, accessorKind,
                    methodArity, slotParams, slot["typeParams"] as JsonArray, supArgs, ownArgs) is not JsonObject impl)
                    continue;
                // A locally-emitted Kotlin interface may itself be @ClrTypeAlias-bound to a referenced CLR
                // interface. Its Kotlin accessor keeps the dedicated property name, while the MethodImpl descriptor
                // must name the exact external Property/MethodSemantics accessor (Collection.size -> get_Count).
                // Resolve that allocation from metadata here; never infer it from either accessor spelling.
                // Explicit naming of a concrete default-interface declaration is allocated later, after physical
                // type lowering. Its identity makes that MethodDef independently allocatable, while this exact
                // descriptor lets an overriding declaration keep its own Kotlin/CLR name. Consume the stated target
                // here; recovering it after allocation from the rewritten MethodDef spelling would be too late.
                // Suspend cold entries already carry their final role-suffixed name and no explicitClrName field.
                var explicitDescriptorMember = Str(slot[DeclarationIdentityBinding.ExplicitNameKey]);
                var descriptorMember = explicitDescriptorMember ?? name;
                var descriptorOwner = spec;
                var loweredDescriptorOwner = refs == null ? null : BirTypeLowering.LowerPhysicalType(
                    spec, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false, localTypeNames) as TypeNode.Fqn;
                string aliasedPhysicalMember = null;
                if (propertyName == null && refs != null)
                {
                    if (!refs.TryExactMemberIntrinsic(spec.Name, semanticName, methodArity,
                            semanticSlotParams, out aliasedPhysicalMember)
                        && slot["overrides"] is JsonArray slotOverrides)
                        aliasedPhysicalMember = DeclarationRename.ResolveSlot(slot, slotOverrides, refs);
                    if (aliasedPhysicalMember == null)
                    {
                        var inheritedPhysicalMembers = SupertypeGraph.Reachable(sup, defs, refs)
                            .Where(edge => edge.isInterface)
                            .Select(edge => refs.TryExactMemberIntrinsic(edge.spec.Name, semanticName, methodArity,
                                semanticSlotParams, out var inheritedMember) ? inheritedMember : null)
                            .Where(inheritedMember => inheritedMember != null)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        if (inheritedPhysicalMembers.Length > 1)
                            throw new InvalidOperationException(
                                $"bir2cir: aliased interface slot '{spec.Name}.{semanticName}`{methodArity}' "
                                + "inherits conflicting physical member bindings: "
                                + string.Join(", ", inheritedPhysicalMembers));
                        if (inheritedPhysicalMembers.Length == 1)
                            aliasedPhysicalMember = inheritedPhysicalMembers[0];
                    }
                }
                if (propertyName == null && refs != null && loweredDescriptorOwner != null
                    && loweredDescriptorOwner.Name != spec.Name
                    && refs.ResolveNetType(loweredDescriptorOwner.Name,
                        loweredDescriptorOwner.Args?.Length ?? 0) is { IsInterface: true }
                    && aliasedPhysicalMember != null)
                {
                    var comparableParams = slotParams.Select(parameter => BirTypeLowering.LowerPhysicalType(
                        parameter, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false,
                        localTypeNames)).ToArray();
                    if (!ClrMemberResolution.TryResolveAliasedInterfaceSlot(
                            refs, loweredDescriptorOwner, aliasedPhysicalMember, methodArity, comparableParams,
                            slot["typeParams"] as JsonArray, supArgs,
                            out var declarationOwner, out var declarationMember,
                            out var physicalParams, out var physicalRet))
                        throw new InvalidOperationException(
                            $"bir2cir: aliased interface slot '{spec.Name}.{semanticName}`{methodArity}' "
                            + $"does not resolve to '{loweredDescriptorOwner.Name}.{aliasedPhysicalMember}'");
                    // Reflection reports the actual declaring interface. A derived semantic alias may repeat a
                    // source declaration while its CLR face only inherits the base slot; that base semantic edge is
                    // visited separately and owns the one valid MethodImpl.
                    if (ReferenceMetadataIndex.BareOwnerFqn(declarationOwner.Name)
                        != ReferenceMetadataIndex.BareOwnerFqn(loweredDescriptorOwner.Name))
                        continue;
                    descriptorOwner = declarationOwner;
                    descriptorMember = declarationMember;
                    slotParams = physicalParams;
                    slotRet = physicalRet;
                }
                if (propertyName != null && refs != null
                    && refs.TryExternalPropertyAccessor(spec.Name, propertyName, accessorKind,
                        slotParams.Length, methodArity, slotParams, spec.Args ?? Array.Empty<TypeNode>(),
                        out var physicalOwner, out _, out var externalAccessor))
                {
                    var currentPhysicalOwner = refs.ExactReflectedOwner(spec.Name, spec.Args?.Length ?? 0);
                    // Reflection reports the MethodSemantics method's DECLARING interface. A Kotlin interface may
                    // redeclare a property while its CLR alias merely inherits that property (List.size over
                    // IReadOnlyList<T> -> IReadOnlyCollection<T>.Count). Such a spec owns no CLR slot; its reachable
                    // declaring supertype is visited separately and receives the one valid descriptor.
                    if (physicalOwner != currentPhysicalOwner) continue;
                    descriptorMember = externalAccessor;
                    descriptorOwner = new TypeNode.Fqn(physicalOwner, spec.Args);
                }
                // A generated existential slot carries its source-property association only as metadata; its
                // compiler-assigned `$star$...` name is already the physical declaration and bypasses this ordinary
                // source-property allocation.
                else if (propertyName != null && explicitDescriptorMember == null
                    && slot[KotlinPropertyAccessors.MetadataCarrierKey] == null)
                {
                    // The local declaration is allocated later, but its accessor role is still an explicit semantic
                    // fact here. Project the descriptor through the same one-way name allocator now; waiting until
                    // final CIR would leave only the physical spelling and force a forbidden reverse parse.
                    descriptorMember = KotlinPropertyAccessors.PhysicalName(propertyName, accessorKind);
                }
                var slotHasDefault = supIsInterface && !Bool(slot["abstract"])
                    && slot["body"] is JsonArray;
                Fill(spec, descriptorOwner, supIsInterface, false, slotHasDefault,
                    semanticName, descriptorMember, accessorKind,
                    slotParams, slotRet, impl, slot["typeParams"] as JsonArray);
            }
        }

    }

    // Ordinary-function twin of the property rule below. This matters when a DIM has a CLR physical name that differs
    // from its Kotlin name and an unrelated class/base method already occupies that physical signature.
    static void AddInheritedDefaultMethodBridges(Def cls, IReadOnlyDictionary<string, Def> defs,
        JsonArray methods, TypeNode[] ownArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
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
                        && SignatureMatches(candidate, factParams, factRet, supArgs, refs, isValue)).ToList();
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
                    }
                    continue;
                }

                if (refs == null
                    || !refs.TrySelectedMethodDeclaration(spec.Name, implementationMember, methodArity,
                        factParams, factRet, spec.Args ?? Array.Empty<TypeNode>(), implementationTypeParams,
                        out var referencedDeclaration)
                    || referencedDeclaration.Return == null
                    || referencedDeclaration.Parameters == null
                    || referencedDeclaration.Parameters.Any(type => type == null))
                    continue;
                var specArgs = spec.Args ?? Array.Empty<TypeNode>();
                var referencedSlotParams = referencedDeclaration.Parameters.Select(type => SupertypeGraph.SubstOwnerTvs(
                    NullableGenericErasure.EraseNullableTv(type, isValue), specArgs)).ToArray();
                var referencedSlotRet = SupertypeGraph.SubstOwnerTvs(
                    NullableGenericErasure.EraseNullableTv(referencedDeclaration.Return, isValue), specArgs);
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
                ? (parameters[i] as JsonObject)?["type"] : parameters[i]);
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
            refs?.Aliases, isValue, refs?.PhysicalTypeNames, returnPosition)
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
                    isValue, refs, callOwner, virtualCall, callMember);
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
    static void FillFromReference(Def cls, IReadOnlyDictionary<string, Def> defs, TypeNode.Fqn spec,
        bool supIsInterface, JsonArray methods, TypeNode[] ownArgs, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs,
        Action<TypeNode.Fqn, bool, bool, string, string, string, TypeNode[], TypeNode, JsonObject, JsonArray, bool> fill)
    {
        if (refs == null) return;
        var supArgs = spec.Args ?? Array.Empty<TypeNode>();
        foreach (var impl in methods.OfType<JsonObject>().ToList())
        {
            if (Bool(impl["static"]) || Str(impl["name"]) is not string ownName) continue;
            if (Str(impl["vis"]) is not (null or "public" or "protected")) continue;
            if (impl["params"] is not JsonArray ps) continue;
            if (impl["overrides"] is not JsonArray overrides) continue;
            var methodArity = (impl["typeParams"] as JsonArray)?.Count ?? 0;
            foreach (var o in overrides.OfType<JsonObject>())
            {
                // THE MARKER SAYS WHAT WAS OVERRIDDEN; THE SPEC SAYS WHERE THE SLOT IS REACHED. Those differ the
                // moment a referenced supertype inherits the member — `class C : Derived<Int>` where `accept` is
                // declared on `Sink` — so requiring the marker's owner to EQUAL the spec skipped the slot entirely.
                // The question is asked of the SPEC instead, and the reader walks the referenced supertype graph
                // itself, mapping the declaration through each supertype's arguments into the spec's own frame.
                //
                // But the two must still be RELATED, or a marker for one supertype answers against an unrelated one
                // that merely exposes the same erased shape — `class C : A<Int>, B<Int>` with `accept(T?)` on each
                // wires both slots to whichever body the first marker named. "Not declared here" establishes only
                // externality; reachability from the spec is what makes this marker THIS spec's business.
                if (TypeJson.Read(o["owner"]) is not TypeNode.Fqn owner || defs.ContainsKey(owner.Name)) continue;
                if (!SupertypeGraph.ReachesDeclaration(spec, owner, defs, refs)) continue;
                if (Str(o["member"]) is not string member) continue;
                // A PROPERTY marker names the Kotlin property and getter/setter role. Reference metadata supplies the
                // exact physical CLR slot independently of the implementation MethodDef name.
                var overrideKind = Str(o["kind"]);
                var accessorKind = overrideKind switch { "getter" => "get", "setter" => "set", _ => null };
                var implementationSignature = ps.OfType<JsonObject>()
                    .Select(parameter => TypeJson.Read(parameter["type"]))
                    .ToArray();
                if (implementationSignature.Length != ps.Count || implementationSignature.Any(type => type == null))
                    continue;
                string selectedPhysicalMember = null;
                JsonArray selectedSlotTypeParams = null;
                var foundSlot = accessorKind != null
                    ? refs.TryNullableGenericPropertySlot(spec.Name, member, accessorKind, isStatic: false,
                        ps.Count, methodArity, implementationSignature, spec.Args ?? Array.Empty<TypeNode>(),
                        out var slotRet0, out var slotParams0, out var refused, includeUnchanged: true)
                    : refs.TrySelectedNullableGenericSlot(spec.Name, member, isStatic: false, ps.Count, methodArity,
                        implementationSignature, TypeJson.Read(impl["ret"]),
                        spec.Args ?? Array.Empty<TypeNode>(), impl["typeParams"] as JsonArray, ownArgs,
                        out slotRet0, out slotParams0, out refused,
                        out selectedPhysicalMember, out selectedSlotTypeParams);
                if (!foundSlot)
                    continue;
                if (slotParams0 == null || slotParams0.Length != ps.Count) continue;
                if (refused != null && refused.Any(r => r)) continue;
                // A null PARAMETER fact is a slot this reader cannot state, and inventing one from the physical
                // signature is the derivation its silence exists to prevent — so the member is left alone.
                if (slotParams0.Any(t => t == null)) continue;
                var slotParams = slotParams0
                    .Select(t => SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(t, isValue), supArgs))
                    .ToArray();
                // A null RETURN fact is the opposite: the reader states a return only while it still says something a
                // call site completes (a carrier, or a physical type still holding a type variable), so `null` means
                // the slot's return is not one the erasure moved and the override's own return already IS it.
                // `compareTo(T): Int` is exactly that — the parameter is the whole divergence.
                var slotRet = slotRet0 == null
                    ? SupertypeGraph.SubstOwnerTvs(TypeJson.Read(impl["ret"]), ownArgs)
                    : SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(slotRet0, isValue), supArgs);
                if (slotRet == null) continue;
                // The CLR slot's own NAME. A referenced Kotlin interface that is `@ClrTypeAlias`'d onto a BCL one
                // fills a differently-named member (`compareTo` -> `CompareTo`), and the MethodImpl has to name the
                // member the interface actually declares.
                var descriptorOwner = spec;
                var descriptorMember = selectedPhysicalMember ?? ownName;
                if (accessorKind != null)
                {
                    if (refs.TryExternalPropertyAccessor(spec.Name, member, accessorKind,
                            ps.Count, methodArity, implementationSignature, spec.Args ?? Array.Empty<TypeNode>(),
                            out var physicalOwner, out _, out var externalAccessor))
                    {
                        var currentPhysicalOwner = refs.ExactReflectedOwner(spec.Name, spec.Args?.Length ?? 0);
                        if (physicalOwner != currentPhysicalOwner) continue;
                        descriptorOwner = new TypeNode.Fqn(physicalOwner, spec.Args);
                        descriptorMember = externalAccessor;
                    }
                }
                else
                {
                    // The override marker names the exact semantic declaration. A derived @ClrTypeAlias interface
                    // may redeclare that Kotlin member even though its physical CLR face only INHERITS the base slot
                    // (MutableList.add on IList<T>, whose actual declaration is ICollection<T>.Add(T): void). In that
                    // case the reference-surface MethodDef name is not a physical slot name. Consume the marker's
                    // exact @ClrIntrinsic binding when it has one, lower that declaration owner, and resolve the
                    // actual CLR declaration before authoring the table row. A marker whose physical face declares no
                    // such member is not this row; the transitive override closure supplies the base marker next.
                    var selectedDescriptorOwner = spec;
                    if (refs.TryExactMemberClrBinding(owner.Name, member, methodArity,
                            implementationSignature, owner.Args ?? Array.Empty<TypeNode>(), out var markerBinding)
                        && markerBinding.Intrinsic != null)
                    {
                        // `owner` remains the pristine Kotlin declaration identity so the exact lookup above can
                        // distinguish nullable constructed overloads. The MethodImpl descriptor is physical: erase
                        // each reified owner argument before lowering its alias. In particular,
                        // Comparable<Int?> selects compareTo(Int?) semantically but implements the collapsed
                        // non-generic System.IComparable.CompareTo(object) face physically.
                        selectedDescriptorOwner = new TypeNode.Fqn(owner.Name, owner.Args?.Select(argument =>
                            NullableGenericErasure.EraseArgument(argument, isValue)).ToArray());
                        descriptorMember = markerBinding.Intrinsic;
                    }
                    else if (refs.TryExactMemberIntrinsic(spec.Name, member, methodArity,
                            implementationSignature, spec.Args ?? Array.Empty<TypeNode>(), out var clrName))
                        descriptorMember = clrName;

                    var loweredOwner = BirTypeLowering.LowerPhysicalType(
                        selectedDescriptorOwner, refs.Aliases, isValue, refs.PhysicalTypeNames,
                        typeArg: false, localTypeNames: null) as TypeNode.Fqn;
                    if (loweredOwner != null
                        && refs.ResolveNetType(loweredOwner.Name, loweredOwner.Args?.Length ?? 0)
                            is { IsInterface: true })
                    {
                        var comparableParams = slotParams.Select(parameter => BirTypeLowering.LowerPhysicalType(
                            parameter, refs.Aliases, isValue, refs.PhysicalTypeNames,
                            typeArg: false, localTypeNames: null)).ToArray();
                        if (!ClrMemberResolution.TryResolveAliasedInterfaceSlot(
                                refs, loweredOwner, descriptorMember, methodArity, comparableParams,
                                selectedSlotTypeParams, supArgs,
                                out var declarationOwner, out var declarationMember,
                                out var physicalParams, out var physicalRet))
                            continue;
                        descriptorOwner = declarationOwner;
                        descriptorMember = declarationMember;
                        slotParams = physicalParams;
                        slotRet = physicalRet;
                    }
                }
                // Property overrides carry their Kotlin identity in the override marker. An ordinary declaration may
                // already have adopted the referenced CLR slot name in DeclarationRename; keep using the explicit
                // pre-rename identity it handed off instead of reflecting meaning back out of that physical spelling.
                // This identity also becomes the round-trip carrier on an exact interface MethodImpl bridge.
                var sourceIdentity = Str(impl[DeclarationRename.SourceMemberKey]) ?? ownName;
                var slotHasDefault = supIsInterface && refs.IsPublicConcreteInstanceMethod(
                    descriptorOwner.Name, descriptorMember, methodArity, slotParams, slotRet);
                fill(descriptorOwner, supIsInterface, true, accessorKind != null ? member : sourceIdentity,
                    descriptorMember, accessorKind, slotParams, slotRet, impl, selectedSlotTypeParams,
                    slotHasDefault);
                break;
            }
        }
    }

    enum Fit { Same, Bridge, Rewrite, Foreign }

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
        bool returnPosition)
    {
        if (declared.Equals(slot)
            || BirTypeLowering.SamePhysicalSlotType(slot, declared, refs?.Aliases, isValue,
                refs?.PhysicalTypeNames, returnPosition)) return Fit.Same;
        // A plain CLR generic declaration's nullable annotation does not change its metadata signature: C# `T?`
        // on `I<T>.Put(T?)`, constructed as `I<int>`, is the BARE `int` slot. Its Kotlin projection is still the
        // truthful `Int?`, whose declaration lowers to `Nullable<int>`. This is a bridgeable outer seam just like
        // the object-erasure seam below, but its conversion is Nullable<T> construction/extraction rather than a
        // box/unbox cast. Keep it in this override-edge-driven table: selecting the slot later from name/arity loses
        // the frontend declaration identity and misbinds same-name overloads (#355).
        if (IsNullableValueSlot(slot, declared, refs, isValue, returnPosition)) return Fit.Bridge;
        // Kotlin collection declarations can return a value where their aliased CLR interface slot is void
        // (`MutableCollection.add(): Boolean` -> `ICollection<T>.Add(T): void`). The bridge body already models this
        // as an expression statement, so keep the decision and exact MethodImpl in the same table instead of asking
        // ilemit to discover the mismatch and synthesize a MethodDef of its own.
        if (returnPosition && IsVoid(slot)) return Fit.Bridge;
        if (IsBareObject(slot))
            // `Any?`/`Any` reach the same bare `object` the slot is, one lowering later — a bridge for them would
            // declare a second member with the identical CLR signature.
            return LowersToObject(declared) ? Fit.Same : Fit.Bridge;
        return ErasureAligned(slot, declared) ? Fit.Rewrite : Fit.Foreign;
    }

    static bool IsNullableValueSlot(TypeNode slot, TypeNode declared, ReferenceMetadataIndex refs,
        ValueTypeOracle isValue, bool returnPosition) =>
        declared is TypeNode.Nullable nullable
        && BirTypeLowering.SamePhysicalSlotType(slot, nullable.Of, refs?.Aliases, isValue,
            refs?.PhysicalTypeNames, returnPosition);

    static bool IsVoid(TypeNode type) =>
        type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null };

    // WHICH PASS OWNS A DIVERGENT SLOT. `CovariantInterfaceReturnBridge` bridges a return the override narrowed, and
    // it runs first; this erasure narrows returns too, so without a boundary both fire on one slot and emit two
    // private bridges with the SAME signature and the SAME MethodImpl descriptor — and the emitter, taking the first
    // match, silently picks between them. The boundary is the divergence itself: a difference this erasure created
    // belongs to this pass, whose bridge forwards VIRTUALLY (so a further-derived override is what runs) where the
    // covariant one deliberately does not.
    public static bool IsErasureDivergence(TypeNode slot, TypeNode declared) =>
        slot != null && declared != null && !slot.Equals(declared) && ErasureAligned(slot, declared);

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

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object" or "System.Object", Args: null };

    static bool IsSuspendMethod(JsonObject method) =>
        method["mods"] is JsonObject mods && Bool(mods["suspend"]);

    // True iff BirTypeLowering will make this type the bare `object` the erased slot already is. A reference `T?` is
    // stripped to its bare inner, so the question recurses through the wrapper; a VALUE `T?` stays `Nullable<V>`, and
    // a type variable stays `!T`.
    static bool LowersToObject(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => LowersToObject(o.Of),
        TypeNode.Nullable n => LowersToObject(n.Of),
        TypeNode.Fn f => f.Suspend,
        TypeNode.Fqn { Args: null } f => f.Name is "object" or "System.Object" or "kotlin.Any",
        _ => false,
    };

    // The class's own declaration that fills this slot. Two independent proofs are accepted, and both are needed:
    // the Kotlin `overrides` fact names the supertype the author wrote (`Sink`), which is what makes the match safe;
    // and a slot may be reached through a supertype the author never named (`Sink`'s own base interfaces, including
    // a synthesized existential view of `Sink`), so an ANCESTOR of an overridden owner counts too. An
    // unrelated same-name overload proves neither and is left alone — mis-wiring a MethodImpl fails type LOAD.
    static JsonObject Implementer(Def cls, IReadOnlyDictionary<string, Def> defs, JsonArray methods, string supName,
        string physicalName, string semanticName, string slotDeclarationId,
        string propertyName, string accessorKind, int methodArity,
        TypeNode[] slotParams, JsonArray slotTypeParams, TypeNode[] slotOwnerArgs, TypeNode[] ownArgs)
    {
        JsonObject found = null;
        foreach (var m in methods.OfType<JsonObject>())
        {
            if (Bool(m["static"]) || KotlinPropertyAccessors.IsPhysicalSlotBridge(m)) continue;
            if (propertyName != null)
            {
                if (!KotlinPropertyAccessors.TryIdentity(m, out var candidateProperty, out var candidateKind)
                    || candidateProperty != propertyName || candidateKind != accessorKind) continue;
            }
            // Suspend lowering has already split one declaration into hot/cold MethodDefs. An explicitly named DIM
            // gives the base projection its final explicit spelling, while the overriding projection keeps its own
            // physical name; `kotlinSourceMember` is the carried frontend identity that relates those two projections.
            // The override closure below remains the independent proof that this candidate actually fills this slot.
            else if ((Str(m["name"]) != physicalName
                      && (slotDeclarationId == null
                          || Str(m[DeclarationRename.SourceMemberKey]) != semanticName))
                     || KotlinPropertyAccessors.TryIdentity(m, out _, out _)) continue;
            if (((m["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) continue;
            // Generic constraints are part of a CLR MethodDef's implementation contract even when name, arity,
            // parameters, and return are otherwise identical. Kotlin's frontend has already selected the override;
            // compare that selected declaration's constraints in the two constructed owner frames so the A-bounded
            // slot cannot be attached to the B-bounded body (which makes the containing type unloadable).
            if (!SameMethodTypeParameterShape(slotTypeParams, m["typeParams"] as JsonArray,
                    slotOwnerArgs, ownArgs)) continue;
            if (m["params"] is not JsonArray ps || ps.Count != slotParams.Length) continue;
            if (Str(m["vis"]) is not (null or "public" or "protected")) continue;
            if (!OverridesInto(m, defs, supName, propertyName ?? semanticName,
                accessorKind switch { "get" => "getter", "set" => "setter", _ => "method" },
                slotDeclarationId)) continue;
            // Every position must either already BE the slot or differ from it exactly where the slot was erased —
            // the only positions an override is free to narrow, and so the only ones whose types legitimately differ.
            var ok = true;
            for (var i = 0; i < ps.Count && ok; i++)
            {
                var t = TypeJson.Read((ps[i] as JsonObject)?["type"]);
                ok = t != null && ErasureAligned(slotParams[i], SupertypeGraph.SubstOwnerTvs(t, ownArgs));
            }
            if (!ok) continue;
            if (found != null) return null;   // ambiguous overload set: never guess which declaration owns the slot
            found = m;
        }
        return found;
    }

    internal static bool SameMethodTypeParameterShape(JsonArray slotTypeParams, JsonArray implementationTypeParams,
        TypeNode[] slotOwnerArgs, TypeNode[] implementationOwnerArgs)
        => MethodTypeParameterShapeKey(slotTypeParams, slotOwnerArgs)
            == MethodTypeParameterShapeKey(implementationTypeParams, implementationOwnerArgs);

    internal static string MethodTypeParameterShapeKey(JsonArray typeParams, TypeNode[] ownerArgs)
    {
        if (typeParams is not { Count: > 0 }) return "0";
        var shapes = new List<string>();
        foreach (var parameter in typeParams)
        {
            var declaration = parameter as JsonObject;
            var constraints = (declaration?["constraints"] as JsonArray)?.Select(TypeJson.Read)
                .Where(constraint => constraint != null)
                .Select(constraint => SupertypeGraph.TypeKey(
                    SupertypeGraph.SubstOwnerTvs(constraint, ownerArgs ?? Array.Empty<TypeNode>())))
                .OrderBy(key => key, StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            var specials = (declaration?["specialConstraints"] as JsonArray)?.Select(Str)
                .Where(value => value != null).OrderBy(value => value, StringComparer.Ordinal)
                ?? Enumerable.Empty<string>();
            shapes.Add("{" + string.Join(",", constraints) + "}|{" + string.Join(",", specials) + "}");
        }
        return typeParams.Count + ":" + string.Join(";", shapes);
    }

    // True iff this method declares itself an override of `supName`'s member — directly, or of some owner that
    // `supName` is a supertype of (a redeclared slot on a base interface is a DISTINCT CLR slot with the same shape).
    static bool OverridesInto(JsonObject method, IReadOnlyDictionary<string, Def> defs, string supName, string member,
        string memberKind, string slotDeclarationId)
    {
        var sourceDeclarationId = SourceDeclarationId(slotDeclarationId);
        if (method["overrides"] is not JsonArray overrides) return false;
        foreach (var o in overrides.OfType<JsonObject>())
        {
            if (TypeJson.OwnerName(o["owner"]) is not string owner) continue;
            if (Str(o["member"]) != member || Str(o["kind"]) != memberKind) continue;
            // A concrete default-interface MethodDef can be named independently. The frontend therefore carries the
            // exact overridden declaration identity on the override edge; owner/member/arity alone cannot distinguish
            // same-name declarations after nullable-generic erasure or suspend projection. Physical-only existential
            // slots and cold entries normalize back to that same source declaration.
            if (sourceDeclarationId != null
                && Str(o[DeclarationIdentityBinding.Key]) != sourceDeclarationId) continue;
            if (owner == supName || IsAncestor(defs, supName, owner)) return true;
        }
        return false;
    }

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

    // Move a slot the CLR cannot bridge onto the supertype's shape, carrying the override's own pre-erasure Kotlin
    // type across on the round-trip channels so the surface survives the move.
    static void Rewrite(JsonObject decl, string typeKey, string factKey, string flagsKey, TypeNode slot,
        ValueTypeOracle isValue)
    {
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t || t.Equals(slot)) return;
        decl[typeKey] = TypeJson.Write(slot);
        decl[factKey] ??= TypeNode.ToJson(t);
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(t, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    // The bridge: the slot's exact signature. Local Kotlin defaults forward virtually so a further-derived override
    // still runs; a referenced CLR DIM is called non-virtually to avoid redispatch to the colliding class method.
    // Each argument is narrowed out of the slot's `object` into the declared type
    // (`unbox.any` for a value, `castclass` for a reference) and the result widened back.
    static JsonObject BuildBridge(Def cls, JsonObject impl, TypeNode[] slotParams, TypeNode slotRet, string bridgeName,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, TypeNode.Fqn callOwner = null,
        bool virtualCall = true, string callMember = null)
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
                        ["elem"] = TypeJson.Write(nullable.Of),
                        ["e"] = new JsonObject { ["k"] = "local", ["name"] = pn },
                    }
                    : new JsonObject
                    {
                        ["k"] = "cast",
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
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = callMember ?? Str(impl["name"]),
            ["sig"] = callSig,
            ["dynRet"] = TypeJson.Write(implRet),
            ["ret"] = TypeJson.Write(implRet),
            ["args"] = callArgs,
        };
        if (impl["typeParams"] is JsonArray methodTps && methodTps.Count > 0)
        {
            var typeArgs = new JsonArray();
            for (var i = 0; i < methodTps.Count; i++) typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));
            call["typeArgs"] = typeArgs;
        }
        var body = new JsonArray();
        var retCarried = false;
        if (IsVoid(slotRet))
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
                        ["elem"] = TypeJson.Write(nullable.Of),
                        ["e"] = value,
                    }
                    : new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(slotRet), ["e"] = value };
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
            // PRIVATE is what keeps the bridge off the Kotlin surface: dll2klib projects public and protected members
            // only, so the re-imported type carries the author's declaration and nothing else.
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
        if (retCarried && implRet != null)
            CarryKotlinType(bridge, "nullableGenericRet", "retNullableFlags", implRet, slotRet, isValue);
        if (impl["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    // The Kotlin type of a slot the bridge states physically, on BOTH round-trip channels every erased slot uses: the
    // `[KotlinNullableGeneric]` carrier for the type node, and the NRT byte for its outer `?`. Neither alone restores
    // `Int?` — the carrier's outer nullability is deliberately the byte's job — and a bridge carrying only the node
    // re-imports as `Int`.
    //
    // It is not cosmetic here, and it is the reason a private bridge is not simply invisible. dll2klib deliberately
    // re-surfaces a PRIVATE MethodImpl body under the interface member's name — a class that satisfies an interface
    // slot only privately would otherwise re-import still carrying the abstract obligation — and it de-duplicates
    // that against the class's public functions by SIGNATURE. Uncarried, the bridge re-surfaces as `accept(x: Any)`:
    // a second overload beside the author's `accept(x: Int?)`, which makes `IntSink().accept("s")` compile and
    // `IntSink().accept(3)` ambiguous. Carried, the two keys coincide, the bridge de-duplicates away, and the
    // re-imported type carries exactly the one declaration that was written.
    static void CarryKotlinType(JsonObject decl, string factKey, string flagsKey, TypeNode kotlin, TypeNode slot,
        ValueTypeOracle isValue)
    {
        decl[factKey] ??= TypeNode.ToJson(kotlin);
        // The byte walk describes the PHYSICAL slot, which is the bare `object` this bridge states, carrying the
        // Kotlin type's outer nullability onto it. Walking the Kotlin type instead would stamp nothing for an `Int?`
        // — a value type's `?` is the structural `Nullable<V>` and contributes no NRT byte — and the slot would
        // re-import as a non-null `Int`, which is a different overload again.
        var shape = kotlin is TypeNode.Nullable ? new TypeNode.Nullable(slot) : slot;
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(shape, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    static JsonObject ImplDescriptor(TypeNode.Fqn spec, string member, int arity, TypeNode[] slotParams,
        TypeNode slotRet, JsonArray typeParams = null)
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
        return descriptor;
    }

    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static int Int(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
