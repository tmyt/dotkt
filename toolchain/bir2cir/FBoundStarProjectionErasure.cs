using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// CLR generics are reified and invariant, so a Kotlin star projection cannot be represented by substituting Any
// (`G<*>` -> `G<object>`): a `G<Concrete>` value is not a `G<object>`. Give each used local generic a
// deterministic non-generic existential view instead. Every closed `G<X>` implements that view, preserving identity
// and allowing a `G<X>` value to flow through a `G<*>` slot without a fictitious variance conversion. The same view
// is used for Kotlin's erased generic `is`/`as` checks (`x is G<T>`): JVM semantics test only the raw classifier.
//
// BIR faithfully carries `{t:"star"}`. This pass synthesizes the CLR-facing interface, attaches it to the generic
// declaration, and uses it for ordinary generic reference values and erased runtime classifier tests/casts. Reference and
// runtime builds both run it, so downstream compilations recognize the view from trusted DotKt metadata. BIR from an
// older toolchain is unsupported: `G<Any>` is always the concrete Kotlin type and is never guessed back into `G<*>`.
static partial class FBoundStarProjectionErasure
{
    const string CarrierMark = "$star";
    internal const int InnerFactoryBottomTypeArgument = -1;
    internal const string ErasedInnerConstraintKey = "erasedInnerConstraints";
    internal const string SourceMemberKey = "existentialSourceMember";
    internal const string InnerConstructorFactoryKey = "existentialInnerConstructorFactory";
    const string InnerFactoryArgumentsKey = "_innerFactoryArguments";
    internal const string ExactBridgeOwnerCallKey = "_exactExistentialBridgeOwnerCall";
    internal const string ExactOuterKey = "outer";
    const string DelegationOuterSlotKey = "delegationOuterSlot";
    static Dictionary<string, List<JsonObject>> _localMethods = new(StringComparer.Ordinal);
    static Dictionary<(string Owner, string Identity), JsonObject> _localDeclarations = new();
    static IReadOnlyDictionary<string, SupertypeGraph.Def> _sourceSupertypes = new Dictionary<string, SupertypeGraph.Def>();
    static HashSet<string> _usedCarrierNames = new(StringComparer.Ordinal);
    static GenericRepresentationPolicy _valueSlots;

    internal sealed partial class Owner
    {
        public string Name;
        public string ErasedName;
        JsonObject physicalDefinition;
        JsonObject physicalRoot;
        public JsonObject Def { get => DefinitionBinding == null ? physicalDefinition : DefinitionBinding.Physical; set => physicalDefinition = value; }
        public JsonObject Root { get => RootBinding == null ? physicalRoot : RootBinding.Physical; set => physicalRoot = value; }
        public int Arity;
        public bool Needed;
    }

    sealed class NormalizedReturnBindings
    {
        public readonly Dictionary<string, TypeNode> ByDeclaration = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TypeNode> ByPhysicalMethod = new(StringComparer.Ordinal);
    }

    static string PhysicalMethodKey(string owner, string method) => owner + "\0" + method;

    // Some structural passes must run after the main existential rewrite (notably the final override-slot bridge,
    // which waits for suspend lowering). Those passes can copy a frontend-resolved `G<*>` slot into a fresh physical
    // declaration. Re-apply only the type projection to those late declarations; do not repeat member binding or
    // synthesize carriers. The allocation handed off by source realization is the sole local authority, while referenced
    // carriers remain metadata-driven through ReferenceMetadataIndex.
    public static void RewriteLateTypes(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> knownClrAliases = null)
    {
        var rootList = roots.ToList();
        var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in rootList.OfType<JsonObject>())
            CollectDefinitions(root, defs);
        _nativeProofs?.RegisterPhysicalLocalDefinitions(defs);
        var owners = localExistentialOwners.ToDictionary(
            pair => pair.Key,
            pair => new Owner
            {
                Name = pair.Key,
                ErasedName = pair.Value,
                Needed = true,
                Def = defs.GetValueOrDefault(pair.Key),
            },
            StringComparer.Ordinal);
        var localClrAliases = new Dictionary<string, string>(
            knownClrAliases ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var alias in CollectLocalClrAliases(defs)) localClrAliases.TryAdd(alias.Key, alias.Value);
        _valueSlots.RegisterLocalDefinitions(defs, localClrAliases);
        foreach (var root in rootList)
        {
            NormalizeRedundantProjections(root, defs, refs);
            RewriteTypesOnly(root, owners, defs, refs, localClrAliases);
        }
    }

    // Repeating a declaration's variance does not introduce an existential capture. Normalize that spelling
    // before choosing value carriers, including below a constructed CLR owner, so equivalent Kotlin types
    // cannot acquire different invariant CLR generic arguments merely because one spells the projection.
    static void NormalizeRedundantProjections(JsonNode node,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array.ToList()) NormalizeRedundantProjections(item, defs, refs);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.ToList()) NormalizeRedundantProjections(child.Value, defs, refs);
        if (TypeJson.Read(obj) is not TypeNode.Fqn { Args: { Length: > 0 } } type
            || obj["args"] is not JsonArray arguments) return;
        var parameters = defs.TryGetValue(type.Name, out var local)
            ? local["typeParams"] as JsonArray : refs.OwnerTypeParamDeclarations(type.Name);
        if (parameters?.Count != arguments.Count) return;
        for (var index = 0; index < arguments.Count; index++)
            if (arguments[index] is JsonObject argument
                && TypeJson.Read(argument) is TypeNode.Projection projection
                && parameters[index] is JsonObject parameter
                && !ReferenceEquals(NormalizeProjectionArgument(projection, Str(parameter["variance"])), projection))
                arguments[index] = argument["of"]!.DeepClone();
    }

    public static void RemoveTransientFacts(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) RemoveTransientMarkers(root);
    }

    static void CollectDefinitions(JsonObject container, Dictionary<string, JsonObject> defs)
    {
        if (container["types"] is not JsonArray types) return;
        foreach (var definition in types.OfType<JsonObject>())
        {
            if (Str(definition["name"]) is string name) defs.TryAdd(name, definition);
            CollectDefinitions(definition, defs);
        }
    }

    static IReadOnlyDictionary<string, string> CollectLocalClrAliases(
        IReadOnlyDictionary<string, JsonObject> defs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, definition) in defs)
        {
            if (definition["attrs"] is not JsonArray attributes) continue;
            foreach (var attribute in attributes.OfType<JsonObject>())
            {
                if (TypeJson.OwnerName(attribute["attr"]) != "kotlin.clr.ClrTypeAlias") continue;
                var target = attribute["args"] is JsonArray { Count: 1 } arguments
                    && arguments[0] is JsonObject constant
                    && Str(constant["k"]) == "const"
                    ? Str(constant["value"])
                    : null;
                if (string.IsNullOrEmpty(target))
                    throw new InvalidOperationException($"bir2cir: malformed @ClrTypeAlias on '{name}'");
                result[name] = target;
            }
        }
        return result;
    }

    static void RewriteTypesOnly(JsonNode node, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases, bool boundDeclaration = false,
        bool preserveArrayShape = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name" || key == InnerConstructorFactoryKey) continue;
                    var childBoundDeclaration = boundDeclaration
                        || IsBoundDeclarationType(obj, key, refs, localClrAliases);
                    var childArrayShape = preserveArrayShape
                        || IsNativeArrayDeclarationSlot(obj, key, refs, localClrAliases);
                    if (key == "typeArgs" && value is JsonArray methodArguments)
                    {
                        RewriteMethodArguments(obj, methodArguments, owners, refs, localClrAliases,
                            childBoundDeclaration, childArrayShape);
                        continue;
                    }
                    if (key is "argTypes" or "memberSignature" && value is JsonArray argumentTypes
                        && obj["args"] is JsonArray argumentValues)
                    {
                        for (var index = 0; index < argumentTypes.Count; index++)
                        {
                            var argumentType = TypeJson.Read(argumentTypes[index]);
                            if (argumentType == null) continue;
                            var exactValue = key == "argTypes" && index < argumentValues.Count
                                && argumentValues[index] is JsonObject argument
                                && Bool(argument[ExactOuterKey]);
                            var exactOuterSlot = IsInnerConstructionOuterSlot(obj, index, defs, refs);
                            argumentTypes[index] = TypeJson.Write(RewriteType(argumentType, owners, refs,
                                childBoundDeclaration, localClrAliases,
                                preserveArrayShape: childArrayShape,
                                preserveConstructedHead: exactValue || exactOuterSlot));
                        }
                        continue;
                    }
                    if (key == "delegationSig" && value is JsonArray delegationTypes)
                    {
                        var exactOuterSlot = Int(obj[DelegationOuterSlotKey]);
                        for (var index = 0; index < delegationTypes.Count; index++)
                        {
                            var delegationType = TypeJson.Read(delegationTypes[index]);
                            if (delegationType == null) continue;
                            delegationTypes[index] = TypeJson.Write(RewriteType(delegationType, owners, refs,
                                childBoundDeclaration, localClrAliases,
                                preserveConstructedHead: exactOuterSlot == index));
                        }
                        continue;
                    }
                    if (TypeJson.Read(value) is TypeNode type)
                        obj[key] = TypeJson.Write(RewriteType(
                            type, owners, refs, childBoundDeclaration, localClrAliases,
                            preserveArrayShape: childArrayShape,
                            preserveConstructedHead: PreservesConstructionHead(obj, key)
                                || (IsArrayComponentSlot(obj, key) || IsNativeLocationSlot(obj, key))
                                    && PreservesNativeWitnessHead(type, owners, refs)
                                || IsExactLexicalType(obj, key)
                                || IsTypeDefinition(obj) && key == "base"
                                || IsDeclarationOwnerDescriptor(obj) && key == "owner"));
                    else
                        RewriteTypesOnly(value, owners, defs, refs, localClrAliases, childBoundDeclaration, childArrayShape);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    if (value == null) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        arr[i] = TypeJson.Write(RewriteType(
                            type, owners, refs, boundDeclaration || _nativeProofs?.IsPhysicalConstraint(arr, type) == true, localClrAliases,
                            preserveArrayShape: preserveArrayShape,
                            preserveConstructedHead: IsInterfaceDeclarationList(arr)));
                    else
                        RewriteTypesOnly(value, owners, defs, refs, localClrAliases, boundDeclaration, preserveArrayShape);
                }
                break;
        }
    }

    // A declaration selected in another owner is an exact linkage fact, not a Kotlin value slot this pass owns.
    // In particular an @ClrTypeAlias constructor's memberSignature may contain a source projection;
    // its physical descriptor retains the declared native interface, never object. Keep the
    // projection's bound while still lowering its aliases.  A Kotlin-local constructor is deliberately excluded:
    // its declaration is rewritten by this pass too, so its memberSignature must follow the same existential ABI.
    static bool IsBoundDeclarationType(JsonObject owner, string key, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
        => BoundTypeSlot(DomTypeSlotFacts(owner, refs, localClrAliases), key);

    // Preserve only the selected native array shape. This does not make unrelated
    // Kotlin generic values in a bridge into exact constructed CLR values.
    static bool IsNativeArrayDeclarationSlot(JsonObject owner, string key, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases) =>
        NativeArrayTypeSlot(DomTypeSlotFacts(owner, refs, localClrAliases), key);

    // A trusted binding alias still names a Kotlin declaration surface. Its MethodImpl signature must follow that
    // declaration's value ABI; only the already-selected physical native owner freezes CLR witnesses and arrays.
    static bool IsNativeDescriptorOwner(TypeNode.Fqn owner, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases) =>
        _valueSlots?.IsBindingAlias(owner.Name) != true
        && localClrAliases?.ContainsKey(owner.Name) != true
        && IsNativeArrayOwner(owner, refs, localClrAliases);

    static bool IsNativeArrayOwner(TypeNode.Fqn owner, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases) =>
        localClrAliases?.ContainsKey(owner.Name) == true
        || refs?.TryResolveClrOwner(owner.Name, out _, out _) == true
        || refs != null && !refs.HasDotKtOwner(owner.Name)
            && refs.ResolveRefType(owner.Name, owner.Args?.Length ?? 0) != null;

    // Operation owners retain their selected head, not recursively exact Kotlin-supplied arguments.
    // Native memberRef/declaration descriptors separately remain fully bound above.
    static bool PreservesConstructionHead(JsonObject node, string key) =>
        ConstructionHeadSlot(DomTypeSlotFacts(node), key);

    static bool IsArrayComponentSlot(JsonObject node, string key) =>
        ArrayComponentTypeSlot(DomTypeSlotFacts(node), key);

    static bool IsNativeLocationSlot(JsonObject node, string key) =>
        NativeLocationTypeSlot(DomTypeSlotFacts(node), key);

    // Native instantiation witnesses and addressable source locations retain a closed invariant source head.
    // This is not a fully bound foreign descriptor: nested slots still follow their declaration's frame.
    // Variance, opaque aliases and explicit projections keep their established carrier/object representation.
    static bool PreservesNativeWitnessHead(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) =>
        PreservesNativeWitnessHead(type, CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs));

    // Lexical receivers retain their declaring class, with the same projected argument slots as its TypeDef.
    // Consume the producer's role fact, never the spelling of a hidden outer field or temporary.
    static bool IsExactLexicalType(JsonObject node, string key) =>
        ExactLexicalTypeSlot(DomTypeSlotFacts(node), key);

    static bool IsTypeDefinition(JsonObject node) => IsTypeDefinition(Str(node["kind"]));

    static bool IsTypeDefinition(string kind) => kind is "class" or "interface" or "enum" or "struct" or "delegate";

    static bool IsInterfaceDeclarationList(JsonArray entries) =>
        entries.Parent is JsonObject definition && IsTypeDefinition(definition)
            && ReferenceEquals(definition["interfaces"], entries);

    // Override facts, selected inherited implementations and MethodImpls name exact declaration edges, not values.
    static bool IsDeclarationOwnerDescriptor(JsonObject node) =>
        node.Parent is JsonArray entries && entries.Parent is JsonObject method
            && (ReferenceEquals(method["clrBaseImpls"], entries)
                || ReferenceEquals(method["clrInterfaceImpls"], entries)
                || ReferenceEquals(method["overrides"], entries))
        || node.Parent is JsonObject fact && ReferenceEquals(fact["inheritedImplementation"], node);

    static bool IsInnerConstructionOuterSlot(JsonObject node, int index,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (index != 0 || Str(node["k"]) != "new"
            || TypeJson.Read(node["type"]) is not TypeNode.Fqn constructed) return false;
        return defs.TryGetValue(constructed.Name, out var local) && IsInner(local)
            || refs.TryInnerSemanticOwner(constructed.Name, out _);
    }

    // The physical name is intentionally not an ABI oracle: trusted [KotlinType] metadata carries the relation.
    // Allocate against every TypeDef in the emission unit so a user declaration (including a backtick identifier)
    // can never collide with the compiler carrier. The sorted walk makes the chosen suffix deterministic.
    static HashSet<string> AllocateCarrierNameState(IReadOnlyDictionary<string, Owner> owners, IEnumerable<string> declaredNames)
    {
        var used = declaredNames.ToHashSet(StringComparer.Ordinal);
        foreach (var owner in owners.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            var stem = owner.Name + CarrierMark;
            var candidate = stem;
            for (var ordinal = 1; !used.Add(candidate); ordinal++) candidate = stem + "$" + ordinal;
            owner.ErasedName = candidate;
        }
        return used;
    }

    // If G<T>'s existential surface exposes H<T>, substituting object would manufacture the invalid invariant
    // conversion H<T> -> H<object>. H's existential view is part of the same closure and must exist first-class.
    static void MarkNeededClosure(IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, (TypeNode.Fqn spec, bool isInterface)[]> baseContracts)
    {
        // A newly-bound carrier result can initialize the next local in a chain. Discover and normalize to a fixed
        // point so each lexical hop retains the same projected star mask before its consumer is rebound.
        while (true)
        {
            var before = owners.Values.Count(o => o.Needed);
            MarkNeededAncestors(owners, baseContracts);
            foreach (var owner in owners.Values.Where(o => o.Needed).ToList())
            {
                // A star-typed outer receiver cannot be converted to one arbitrary invariant G<object> merely so an
                // inner constructor can consume its hidden outer argument.  The existential outer instead exposes a
                // factory whose result is the inner classifier's own existential view.  Keep that result carrier in
                // the same reachability closure as ordinary owner-dependent member results.
                foreach (var inner in owners.Values.Where(candidate =>
                             candidate.SemanticOwner == owner.Name && candidate.Inner))
                    inner.Needed = true;
                foreach (var (result, parameters) in owner.Signatures)
                {
                    if (result != null) MarkDependentResult(result, owners);
                    foreach (var parameter in parameters)
                        if (parameter != null) MarkDependentResult(parameter, owners);
                }
            }
            if (owners.Values.Count(o => o.Needed) == before) return;
        }
    }

    static void MarkDependentResult(TypeNode type, IReadOnlyDictionary<string, Owner> owners)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } f:
                if (args.Any(ContainsOwnerTv) && owners.TryGetValue(f.Name, out var nested))
                    nested.Needed = true;
                foreach (var arg in args) MarkDependentResult(arg, owners);
                break;
            case TypeNode.Nullable n: MarkDependentResult(n.Of, owners); break;
            case TypeNode.Oblivious o: MarkDependentResult(o.Of, owners); break;
            case TypeNode.Array a: MarkDependentResult(a.Elem, owners); break;
            case TypeNode.ByRef b: MarkDependentResult(b.Of, owners); break;
            case TypeNode.Fn fn:
                MarkDependentResult(fn.Ret, owners);
                foreach (var p in fn.Params) MarkDependentResult(p, owners);
                if (fn.Recv != null) MarkDependentResult(fn.Recv, owners);
                break;
        }
    }

    static void MarkNeededAncestors(IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, (TypeNode.Fqn spec, bool isInterface)[]> baseContracts)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var owner in owners.Values.Where(o => o.Needed).ToList())
            {
                void Mark(TypeNode slot)
                {
                    if (slot is TypeNode.Fqn f
                        && owners.TryGetValue(f.Name, out var ancestor) && !ancestor.Needed)
                    {
                        ancestor.Needed = true;
                        changed = true;
                    }
                }
                Mark(owner.BaseType);
                foreach (var edge in owner.InterfaceTypes) Mark(edge);
                foreach (var (spec, _) in baseContracts[owner.Name]) Mark(spec);
            }
        }
    }

    internal static bool IsSourceGeneric(JsonObject def)
        => IsSourceGeneric(Str(def["name"]), Bool(def["generated"]), HasClrTypeAlias(def),
            (def["typeParams"] as JsonArray)?.Count ?? 0);

    internal static bool IsSourceGeneric(string name, bool generated, bool alias, int ownTypeParameterCount)
    {
        // Lifted/local compiler artifacts are not part of the Kotlin ABI and cannot be named by a
        // downstream star-projected use.  Attaching an existential interface to them also turns
        // their implementation-detail type variables into public CLR MethodImpl signatures.
        if (generated || alias
            || BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(name)) return false;
        return ownTypeParameterCount > 0;
    }

    static bool HasClrTypeAlias(JsonObject def)
    {
        if (def["attrs"] is not JsonArray attrs) return false;
        foreach (var a in attrs.OfType<JsonObject>())
            if (TypeJson.OwnerName(a["attr"]) == "kotlin.clr.ClrTypeAlias") return true;
        return false;
    }

    static bool ContainsUseSiteProjection(TypeNode type) => type switch
    {
        TypeNode.Projection or TypeNode.Star => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsUseSiteProjection),
        TypeNode.Nullable nullable => ContainsUseSiteProjection(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsUseSiteProjection(oblivious.Of),
        TypeNode.Array array => ContainsUseSiteProjection(array.Elem),
        TypeNode.ByRef byRef => ContainsUseSiteProjection(byRef.Of),
        TypeNode.Ptr pointer => ContainsUseSiteProjection(pointer.Of),
        TypeNode.Mod modifier => ContainsUseSiteProjection(modifier.M)
            || ContainsUseSiteProjection(modifier.Of),
        TypeNode.Fn function => ContainsUseSiteProjection(function.Ret)
            || function.Params.Any(ContainsUseSiteProjection)
            || function.Recv != null && ContainsUseSiteProjection(function.Recv)
            || function.Ctx?.Any(ContainsUseSiteProjection) == true,
        _ => false,
    };

    internal static void ProjectionConstraintSelfTest()
    {
        PhysicalAssignabilitySelfTest();
        FunctionRewriteTests.Check("F-bound rewrite", type => RewriteType(type,
            new Dictionary<string, Owner>(), ReferenceMetadataIndex.Build(Array.Empty<string>())));
        var variable = new TypeNode.Tv("method", 0);
        var exact = new TypeNode.Fqn("Bound", new TypeNode[] { variable });
        var star = new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Star() });
        var projected = new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Projection("out", variable) });
        if (ContainsUseSiteProjection(exact) || !ContainsUseSiteProjection(star)
            || !ContainsUseSiteProjection(new TypeNode.Nullable(star)) || !ContainsUseSiteProjection(projected))
            throw new InvalidOperationException("Constraint projection classification lost star/exact argument distinction");
        var physicalFunction = new TypeNode.Fn(false, variable, new TypeNode[] { variable }, null, "System.Func");
        var modified = new TypeNode.Mod(false, new TypeNode.Star(), physicalFunction);
        var rewritten = RewriteType(modified, new Dictionary<string, Owner>(), null);
        if (rewritten is not TypeNode.Mod { M: TypeNode.Fqn { Name: "kotlin.Any" }, Of: TypeNode.Fn { Clr: "System.Func" } })
            throw new InvalidOperationException("Signature modifier projection lost its type traversal or physical delegate family");
        var inner = new TypeNode.Fqn("Owner.Item", new TypeNode[] { variable });
        var owners = new Dictionary<string, Owner> {
            ["Owner.Item"] = new Owner { Name = "Owner.Item", ErasedName = "InnerView", Needed = true,
                Def = JsonNode.Parse("""
                {"kind":"class","mods":{"inner":true},"outerTypeParamCount":1,"typeParams":[{"name":"T"}]}
                """)!.AsObject() },
            ["Bound"] = new Owner { Name = "Bound", ErasedName = "BoundView", Needed = true,
                Def = JsonNode.Parse("""{"kind":"class","typeParams":[{"name":"T"}]}""")!.AsObject() },
        };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        _valueSlots = new GenericRepresentationPolicy(refs.Aliases, refs, new[] { JsonNode.Parse("""
          {"types":[{"kind":"class","name":"Owner.Item","typeParams":[{"name":"T"}]},
                    {"kind":"class","name":"Bound","typeParams":[{"name":"T"}]},
                    {"kind":"class","name":"Construction","typeParams":[{"name":"T"}]},
                    {"kind":"class","name":"Variant","typeParams":[{"name":"T"}]}]}
          """) });
        OwnerConstrainedMethodLowering.Reset(refs);
        SourceProjectionContractSelfTest();
        OwnerConstrainedMethodLowering.Reset(refs);
        if (RewriteType(inner, owners, refs) != new TypeNode.Fqn("InnerView")
            || RewriteType(exact, owners, refs) != new TypeNode.Fqn("BoundView")
            || RewriteType(inner, owners, refs, boundDeclaration: true) != inner
            || RewriteType(inner, owners, refs, preserveConstructedHead: true) != inner)
            throw new InvalidOperationException("Generic value projection lost its carrier or changed an exact linkage slot");
        var physicalCall = new JsonObject {
            ["k"] = "callInstance", [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = true,
            ["sig"] = new JsonArray(TypeJson.Write(exact)), ["calleeParams"] = new JsonArray(TypeJson.Write(exact)),
            ["ret"] = TypeJson.Write(exact), ["dynRet"] = TypeJson.Write(exact), ["sty"] = TypeJson.Write(exact),
            ["args"] = new JsonArray(new JsonObject {
                ["k"] = "cast", [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = true,
                ["type"] = TypeJson.Write(exact), ["e"] = new JsonObject {
                    ["k"] = "local", ["name"] = "value", ["sty"] = TypeJson.Write(exact) } }),
        };
        RewriteTypesOnly(physicalCall, owners, new Dictionary<string, JsonObject>(), refs, null);
        if (TypeJson.Read(physicalCall["sig"][0]) != exact || TypeJson.Read(physicalCall["calleeParams"][0]) != exact
            || TypeJson.Read(physicalCall["ret"]) != exact || TypeJson.Read(physicalCall["args"][0]["type"]) != exact
            || TypeJson.Read(physicalCall["args"][0]["e"]["sty"]) != new TypeNode.Fqn("BoundView"))
            throw new InvalidOperationException("A final physical adapter lost its declaration contract or froze ordinary operand values");
        RemoveTransientMarkers(physicalCall);
        if (physicalCall.ContainsKey(GenericRepresentationPolicy.PhysicalDeclarationSignatureKey)
            || physicalCall["args"][0].AsObject().ContainsKey(GenericRepresentationPolicy.PhysicalDeclarationSignatureKey))
            throw new InvalidOperationException("A physical adapter role escaped into serialized CIR");
        var factoryArguments = new JsonArray(TypeJson.Write(inner), TypeJson.Write(
            new TypeNode.Fqn("Owner.Item", new TypeNode[] { new TypeNode.Star() })));
        RewriteMethodArguments(new JsonObject { [InnerFactoryArgumentsKey] = true }, factoryArguments,
            owners, refs, null, false, false);
        if (TypeJson.Read(factoryArguments[0]) != inner
            || TypeJson.Read(factoryArguments[1]) != new TypeNode.Fqn("InnerView"))
            throw new InvalidOperationException("Selected inner factory lost its exact witness or fabricated a projected construction");
        var component = new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        foreach (var element in new TypeNode[] { component, new TypeNode.Nullable(component) })
            if (RewriteType(new TypeNode.Array(element), owners, refs) != new TypeNode.Array(element))
                throw new InvalidOperationException("A closed invariant array lost its exact allocation component");
        foreach (var kind in new[] { "clrInstance", "clrStatic", "clrGenericInstance", "clrPropGet", "clrPropSet", "newClr" })
        {
            var operation = new JsonObject { ["k"] = kind, ["type"] = TypeJson.Write(inner) };
            if (IsBoundDeclarationType(operation, "type", refs, null)
                || !PreservesConstructionHead(operation, "type")
                || !PreservesConstructionHead(operation, "ownerType")
                || IsBoundDeclarationType(operation, "ret", refs, null)
                || RewriteType(inner, owners, refs,
                    preserveConstructedHead: PreservesConstructionHead(operation, "type")) != inner)
                throw new InvalidOperationException("A bound CLR declaring owner became an ordinary value slot");
        }
        var nativeRefs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var bindingAliases = new Dictionary<string, string> {
            ["SourceAlias"] = "System.Collections.Generic.IReadOnlyList`1",
        };
        var sourceDescriptor = new JsonObject { ["owner"] = TypeJson.Fqn("SourceAlias"),
            ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(inner) };
        var nativeDescriptor = new JsonObject {
            ["owner"] = TypeJson.Write(new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList`1",
                new TypeNode[] { inner })), ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(inner),
        };
        var descriptorMethod = new JsonObject {
            ["clrInterfaceImpls"] = new JsonArray(sourceDescriptor, nativeDescriptor),
        };
        if (IsBoundDeclarationType(sourceDescriptor, "ret", nativeRefs, bindingAliases)
            || IsNativeArrayDeclarationSlot(sourceDescriptor, "ret", nativeRefs, bindingAliases)
            || !IsBoundDeclarationType(nativeDescriptor, "ret", nativeRefs, bindingAliases)
            || !IsNativeArrayDeclarationSlot(nativeDescriptor, "ret", nativeRefs, bindingAliases)
            || RewriteType(inner, owners, nativeRefs,
                boundDeclaration: IsBoundDeclarationType(sourceDescriptor, "ret", nativeRefs, bindingAliases))
                != new TypeNode.Fqn("InnerView")
            || RewriteType(inner, owners, nativeRefs,
                boundDeclaration: IsBoundDeclarationType(nativeDescriptor, "ret", nativeRefs, bindingAliases)) != inner)
            throw new InvalidOperationException("A source alias MethodImpl and an actual native descriptor lost their distinct value roles");
        // Empty references deliberately make this an unresolved owner, not an imported native contract.
        // Native owner/method arguments are covered by the reference-backed cross-DLL roundtrip tests.
        var unresolved = new TypeNode.Fqn("Unresolved.Container", new TypeNode[] { inner });
        var explicitUnresolved = new TypeNode.Fqn(unresolved.Name, new TypeNode[] {
            new TypeNode.Fqn("Unresolved.Other", new TypeNode[] { new TypeNode.Fqn("System.String") }),
        });
        if (RewriteType(unresolved, owners, refs, preserveConstructedHead: true)
                != new TypeNode.Fqn(unresolved.Name, new TypeNode[] { new TypeNode.Fqn("InnerView") })
            || RewriteType(explicitUnresolved, owners, refs, preserveConstructedHead: true) != explicitUnresolved
            || RewriteType(unresolved, owners, refs, boundDeclaration: true) != unresolved)
            throw new InvalidOperationException("Unresolved operation arguments confused Kotlin values with bound descriptors");
        var constructionOwner = JsonNode.Parse("""
          {"kind":"class","typeParams":[{"name":"P"},{"name":"T"},{"name":"Q"},{"name":"U"}]}
          """).AsObject();
        var constructionFrame = new NullableRepresentationFrame(2,
            Array.Empty<int>(), new[] { 2, 0, 3, 1 }, storageIndices: new[] { 0, 1 });
        KotlinSupertypesRecord.Merge(constructionOwner, new JsonObject {
            [NullableRepresentationFrame.MetadataKey] = constructionFrame.ToJson(),
        });
        owners["Construction"] = new Owner { Name = "Construction", ErasedName = "ConstructionView",
            Def = constructionOwner, Needed = true };
        var construction = new TypeNode.Fqn("Construction", Enumerable.Repeat<TypeNode>(inner, 4).ToArray());
        var projectedConstruction = (TypeNode.Fqn)RewriteType(construction, owners, refs, preserveConstructedHead: true);
        if (projectedConstruction.Args[0] != new TypeNode.Fqn("InnerView")
            || projectedConstruction.Args[1] != inner
            || projectedConstruction.Args[2] != new TypeNode.Fqn("InnerView")
            || projectedConstruction.Args[3] != inner)
            throw new InvalidOperationException("Exact construction erased an ordinary witness or retained a concrete storage head");
        if (RewriteType(construction, owners, refs) != new TypeNode.Fqn("ConstructionView"))
            throw new InvalidOperationException("An ordinary invariant value regained an exact construction head");
        _valueSlots.RegisterLocalDefinitions(new Dictionary<string, JsonObject> {
            ["LatePhysical"] = JsonNode.Parse("""{"kind":"class","generated":true,"typeParams":[{"name":"T"}]}""").AsObject(),
        }, new Dictionary<string, string>());
        var latePhysical = new TypeNode.Fqn("LatePhysical", new TypeNode[] { variable });
        if (RewriteType(latePhysical, owners, refs) != latePhysical
            || TryExistentialCarrier("LatePhysical", owners, null, out _))
            throw new InvalidOperationException("A late local physical declaration consulted foreign carrier metadata");
        owners["Variant"] = new Owner { Name = "Variant", ErasedName = "VariantView", Needed = true,
            Def = JsonNode.Parse("""
              {"kind":"class","typeParams":[{"name":"T","variance":"out"}]}
              """).AsObject() };
        _valueSlots.RegisterLocalDefinitions(new Dictionary<string, JsonObject> {
            ["Variant"] = owners["Variant"].Def,
        }, new Dictionary<string, string>());
        var variant = new TypeNode.Fqn("Variant", new TypeNode[] { variable });
        var variantArguments = construction with { Args = Enumerable.Repeat<TypeNode>(variant, 4).ToArray() };
        if (((TypeNode.Fqn)RewriteType(variantArguments, owners, refs, preserveConstructedHead: true))
                .Args.Any(argument => argument != new TypeNode.Fqn("VariantView"))
            || RewriteType(variant, owners, refs, preserveConstructedHead: true) != variant)
            throw new InvalidOperationException("A nested variant witness regained a concrete head or changed its exact constructor");
        NativeProofSourceSelfTest();
        LocalAliasDeclarationSelfTest();
        Console.WriteLine("[constraint projections] self-test OK (star, variance, annotation, exact bound, physical/source separation, native proof demand)");
    }

    static void PhysicalAssignabilitySelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal) {
            ["object"] = new() { ["name"] = "object", ["kind"] = "class" },
            ["Proof.Narrow"] = new() { ["name"] = "Proof.Narrow", ["kind"] = "class", ["base"] = TypeJson.Fqn("object") },
            ["Proof.Value"] = new() { ["name"] = "Proof.Value", ["kind"] = "struct",
                ["interfaces"] = new JsonArray(TypeJson.Fqn("Proof.Marker")) },
            ["Proof.Marker"] = new() { ["name"] = "Proof.Marker", ["kind"] = "interface" },
            ["Proof.Stream"] = new() { ["name"] = "Proof.Stream", ["kind"] = "interface", ["typeParams"] = new JsonArray(new JsonObject {
                ["name"] = "T", ["variance"] = "out",
            }) },
        };
        TypeNode Stream(string element) => new TypeNode.Fqn("Proof.Stream", new[] { new TypeNode.Fqn(element) });
        if (!IsClrAssignable(new TypeNode.Fqn("Proof.Narrow"), new TypeNode.Fqn("object"), definitions, refs, physicalTypes: true)
            || !IsClrAssignable(Stream("Proof.Narrow"), Stream("object"), definitions, refs, physicalTypes: true)
            || IsClrAssignable(Stream("Proof.Value"), Stream("object"), definitions, refs, physicalTypes: true)
            || IsClrAssignable(Stream("object"), Stream("Proof.Narrow"), definitions, refs, physicalTypes: true))
            throw new InvalidOperationException("Physical assignability lost reference-only widening");
        if (!IsClrAssignable(new TypeNode.Fqn("Proof.Value"), new TypeNode.Fqn("object"), definitions, refs,
                physicalTypes: true, allowBoxing: true)
            || !IsClrAssignable(new TypeNode.Fqn("Proof.Value"), new TypeNode.Fqn("Proof.Marker"), definitions, refs,
                physicalTypes: true, allowBoxing: true)
            || IsClrAssignable(Stream("Proof.Value"), Stream("object"), definitions, refs,
                physicalTypes: true, allowBoxing: true))
            throw new InvalidOperationException("Root boxing leaked into CLR generic variance or lost its interface");
        Console.WriteLine("[physical assignability] self-test OK (reference widening, root boxing, invariant value arguments)");
    }

    // Kotlin Array<Int> and IntArray can share a CLR int[] slot. Keep the declaration's generic-array
    // classifier explicitly, including nested positions; a physical signature cannot recover that distinction.
    static bool ContainsGenericArray(TypeNode type) => type switch
    {
        TypeNode.Array => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsGenericArray),
        TypeNode.Nullable nullable => ContainsGenericArray(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsGenericArray(oblivious.Of),
        TypeNode.Projection projection => ContainsGenericArray(projection.Of),
        TypeNode.ByRef byRef => ContainsGenericArray(byRef.Of),
        TypeNode.Ptr pointer => ContainsGenericArray(pointer.Of),
        TypeNode.Mod modifier => ContainsGenericArray(modifier.M) || ContainsGenericArray(modifier.Of),
        TypeNode.Fn function => ContainsGenericArray(function.Ret)
            || function.Params.Any(ContainsGenericArray)
            || function.Recv != null && ContainsGenericArray(function.Recv)
            || function.Ctx?.Any(ContainsGenericArray) == true,
        _ => false,
    };

    // Aliases are not a reversible source-type encoding: ArrayList<T> and a CLR List<T> declaration can have
    // the same physical signature without the same Kotlin classifier. Preserve declaration truth even when the
    // alias needs no nullable/existential companion; reverse projection must not guess a source owner.

    // Kotlin can infer `Array<G<Join>>` from values `G<A>`, `G<B>`, ... when G's source variance makes each
    // conversion legal. CLR arrays are covariant only in their immediate reference element; that does not turn the
    // invariant physical constructions G<A>/G<B> into G<Join>. The already type-checked BIR is the authority for the
    // semantic join. Preserve it physically by allocating the nominal existential view implemented by every G<X>.
    //
    // This is deliberately structural: neither arrayOf nor a library/function name is recognized. Every non-null
    // element must project through the declaration graph to the array element's generic owner, and at least one such
    // construction must differ from the inferred join. An ordinary homogeneous generic array remains reified.

    static T UniqueLocalDelegateTarget<T>(IReadOnlyList<T> candidates) where T : class
        => candidates.Count == 1 ? candidates[0] : null;

    internal static bool IsClrAssignable(TypeNode source, TypeNode target,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs, bool physicalTypes = false,
        bool allowBoxing = false)
        => IsClrAssignable(source, target, CarrierTypeContext.ForDom(defs, refs), physicalTypes, allowBoxing);

    internal static bool IsClrAssignable(TypeNode source, TypeNode target,
        IReadOnlyDictionary<string, IBirTypeDeclaration> defs, ReferenceMetadataIndex refs, bool physicalTypes = false,
        bool allowBoxing = false)
        => IsClrAssignable(source, target, CarrierTypeContext.ForSource(defs, refs), physicalTypes, allowBoxing);

    static Type ResolveClosedReferenceType(TypeNode.Fqn type, ReferenceMetadataIndex refs, bool physicalTypes = false)
    {
        var arity = type.Args?.Length ?? 0;
        var name = !physicalTypes && refs.Aliases.TryGetValue(type.Name, out var alias) ? alias : type.Name;
        var open = physicalTypes ? refs.PhysicalTypeForProof(name, arity) : refs.ResolveNetType(name, arity);
        if (open == null) return null;
        if (arity == 0) return open;
        if (!open.IsGenericTypeDefinition) return null;
        var arguments = type.Args.Select(argument =>
            StripSourceNullability(argument) is TypeNode.Fqn f ? ResolveClosedReferenceType(f, refs, physicalTypes) : null).ToArray();
        if (arguments.Any(argument => argument == null)) return null;
        try { return open.MakeGenericType(arguments); }
        catch { return null; }
    }

    static bool HasKotlinVariantParameter(TypeNode.Fqn application, IReadOnlyList<string> parameters) =>
        application.Args != null && parameters != null && application.Args.Length == parameters.Count
        && parameters.Any(variance => variance is "in" or "out");

    static bool ProjectionCanChangeConstruction(TypeNode.Fqn application,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) =>
        CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs).ProjectionCanChangeConstruction(application);

    // A foreign alias cannot be retrofitted with a nominal carrier. Kotlin collection interface casts erase their
    // arguments; Kotlin variance also includes value arguments, unlike CLR interface variance. Both require
    // an opaque reference slot instead; the exact source application still travels in KotlinType metadata.
    static bool RequiresOpaqueVariantAlias(TypeNode.Fqn application,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases = null) =>
        CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs, localClrAliases).RequiresOpaqueVariantAlias(application);

    // A Kotlin generic reference value does not prove a CLR construction, even when its declaration is invariant:
    // unchecked casts retain the raw classifier. Ordinary source-owned slots therefore use the declaration carrier;
    // construction, native location, inheritance and selected CLR descriptor roles retain their exact contracts.
    static bool RequiresKotlinValueCarrier(TypeNode.Fqn application,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) =>
        CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs).RequiresKotlinValueCarrier(application);

    static bool IsCapturedInner(TypeNode type, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn { Args: { Length: > 0 } } named =>
            owners.TryGetValue(named.Name, out var local) && local.Inner && local.OuterCount > 0
            || refs.TryInnerCapturedCount(named.Name, out var captured) && captured > 0,
        TypeNode.Nullable nullable => IsCapturedInner(nullable.Of, owners, refs),
        TypeNode.Oblivious oblivious => IsCapturedInner(oblivious.Of, owners, refs),
        _ => false,
    };

    // Kotlin's Array is mutable and invariant. If its element is a declaration-site variant Kotlin construction,
    // values with different legal source arguments can enter the same exact Array<G<X>> storage. CLR variance is
    // insufficient for that storage contract: it does not relate value-type constructions, and CLR classes are not
    // variant at all. Use the declaration's nominal existential carrier as the one writable physical element type.
    static bool TryWritableVariantArrayElement(TypeNode element,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        out TypeNode.Fqn semanticElement, out TypeNode.Fqn physicalElement) =>
        TryWritableVariantArrayElement(element, CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs),
            out semanticElement, out physicalElement);

    static void MarkNeededType(TypeNode type, IReadOnlyDictionary<string, Owner> owners, bool runtimeClassifier)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } f:
                if (owners.TryGetValue(f.Name, out var owner)
                    && (runtimeClassifier || args.Any(IsExistentialArgument)
                        || owner.Kind is "class" or "interface"
                            && (owner.SourceGeneric || owner.Inner && owner.OuterCount > 0)))
                    owner.Needed = true;
                foreach (var a in args) MarkNeededType(a, owners, false);
                break;
            case TypeNode.Projection p:
                if (StripProjectionShell(p.Of) is TypeNode.Fqn projected
                    && owners.TryGetValue(projected.Name, out var projectedOwner))
                    projectedOwner.Needed |= HasKotlinVariantParameter(
                        projected, projectedOwner.Variances);
                MarkNeededType(p.Of, owners, false);
                break;
            case TypeNode.Nullable n: MarkNeededType(n.Of, owners, runtimeClassifier); break;
            case TypeNode.Oblivious o: MarkNeededType(o.Of, owners, runtimeClassifier); break;
            case TypeNode.Array a:
                if (StripSourceNullability(a.Elem) is TypeNode.Fqn element
                    && owners.TryGetValue(element.Name, out var arrayOwner) && arrayOwner.HasDefinition
                    && HasKotlinVariantParameter(element, arrayOwner.Variances))
                    arrayOwner.Needed = true;
                MarkNeededType(a.Elem, owners, runtimeClassifier);
                break;
            case TypeNode.ByRef b: MarkNeededType(b.Of, owners, false); break;
            case TypeNode.Fn fn:
                MarkNeededType(fn.Ret, owners, false);
                foreach (var p in fn.Params) MarkNeededType(p, owners, false);
                if (fn.Recv != null) MarkNeededType(fn.Recv, owners, false);
                break;
        }
    }

    // TypeOwnershipLowering prepares a compiler-generated nested type by copying its semantic owner's complete
    // generic frame into the synthetic prefix. That happens while every constraint is still Kotlin-complete. If the
    // existential inner ABI subsequently removes an owner constraint that cannot be expressed physically, leaving
    // the old row on a closure/SAM prefix creates an impossible construction: the owner's now-unconstrained `E` is
    // supplied to the nested copy that still requires `E : T`, and the CLR loader correctly rejects it.
    //
    // Synchronize only compiler-generated owner-prefix slots, from the already-selected physical owner declaration.
    // The removed facts travel too: constrained receiver binding inside the generated body must cast a call through
    // that erased bound just as it does in the owner body. The marker remains bir2cir-internal and is dropped before
    // CIR. User declarations and a generated type's own parameters are untouched.

    static bool InnerConstraintIsRepresentable(
        int parameterIndex, TypeNode constraint, int capturedCount, IReadOnlySet<int> directDependent) =>
        // A direct parameter is instantiated with the physical bottom witness (object), so no source-authored bound
        // on that parameter can remain a valid CLR GenericParamConstraint. A retained parameter may keep every bound
        // expressible solely in its MethodDef/TypeDef frame, including F : Comparable<F>; only references to the
        // unavailable captured frame or a bottom-fixed slot must be omitted.
        !directDependent.Contains(parameterIndex)
        && !ContainsInnerDependency(constraint, capturedCount, directDependent);

    static bool ContainsCapturedInnerParameter(TypeNode type, int capturedCount) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => tv.I < capturedCount,
        TypeNode.Projection projection => ContainsCapturedInnerParameter(projection.Of, capturedCount),
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsCapturedInnerParameter(argument, capturedCount)),
        TypeNode.Nullable nullable => ContainsCapturedInnerParameter(nullable.Of, capturedCount),
        TypeNode.Oblivious oblivious => ContainsCapturedInnerParameter(oblivious.Of, capturedCount),
        TypeNode.Array array => ContainsCapturedInnerParameter(array.Elem, capturedCount),
        TypeNode.ByRef byRef => ContainsCapturedInnerParameter(byRef.Of, capturedCount),
        TypeNode.Ptr pointer => ContainsCapturedInnerParameter(pointer.Of, capturedCount),
        TypeNode.Fn function => ContainsCapturedInnerParameter(function.Ret, capturedCount)
            || function.Params.Any(parameter => ContainsCapturedInnerParameter(parameter, capturedCount))
            || function.Recv != null && ContainsCapturedInnerParameter(function.Recv, capturedCount)
            || function.Ctx?.Any(context => ContainsCapturedInnerParameter(context, capturedCount)) == true,
        _ => false,
    };

    static bool ContainsInnerDependency(TypeNode type, int capturedCount, IReadOnlySet<int> dependent) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => tv.I < capturedCount || dependent.Contains(tv.I),
        TypeNode.Projection projection => ContainsInnerDependency(projection.Of, capturedCount, dependent),
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsInnerDependency(argument, capturedCount, dependent)),
        TypeNode.Nullable nullable => ContainsInnerDependency(nullable.Of, capturedCount, dependent),
        TypeNode.Oblivious oblivious => ContainsInnerDependency(oblivious.Of, capturedCount, dependent),
        TypeNode.Array array => ContainsInnerDependency(array.Elem, capturedCount, dependent),
        TypeNode.ByRef byRef => ContainsInnerDependency(byRef.Of, capturedCount, dependent),
        TypeNode.Ptr pointer => ContainsInnerDependency(pointer.Of, capturedCount, dependent),
        TypeNode.Fn function => ContainsInnerDependency(function.Ret, capturedCount, dependent)
            || function.Params.Any(parameter => ContainsInnerDependency(parameter, capturedCount, dependent))
            || function.Recv != null && ContainsInnerDependency(function.Recv, capturedCount, dependent)
            || function.Ctx?.Any(context => ContainsInnerDependency(context, capturedCount, dependent)) == true,
        _ => false,
    };

    static TypeNode ProjectInnerFactoryType(TypeNode type, int capturedCount,
        IReadOnlyDictionary<int, TypeNode> witnesses, IReadOnlyDictionary<int, int> methodIndex) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I < capturedCount => tv,
        TypeNode.Tv { Scope: "type" } tv when witnesses.TryGetValue(tv.I, out var witness) =>
            ProjectInnerFactoryType(witness, capturedCount, witnesses, methodIndex),
        TypeNode.Tv { Scope: "type" } tv when methodIndex.TryGetValue(tv.I, out var index)
            => new TypeNode.Tv("method", index),
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            ProjectInnerFactoryType(projection.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => ProjectInnerFactoryType(arg, capturedCount, witnesses, methodIndex)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(
            ProjectInnerFactoryType(n.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(
            ProjectInnerFactoryType(o.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Array a => new TypeNode.Array(
            ProjectInnerFactoryType(a.Elem, capturedCount, witnesses, methodIndex)),
        TypeNode.ByRef b => new TypeNode.ByRef(
            ProjectInnerFactoryType(b.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Ptr p => new TypeNode.Ptr(
            ProjectInnerFactoryType(p.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            ProjectInnerFactoryType(fn.Ret, capturedCount, witnesses, methodIndex),
            fn.Params.Select(parameter => ProjectInnerFactoryType(
                parameter, capturedCount, witnesses, methodIndex)).ToArray(),
            fn.Recv == null ? null : ProjectInnerFactoryType(fn.Recv, capturedCount, witnesses, methodIndex), fn.Clr,
            fn.Ctx?.Select(context => ProjectInnerFactoryType(
                context, capturedCount, witnesses, methodIndex)).ToArray()),
        _ => type,
    };

    static string InnerConstructorFactoryName(Owner inner, int ordinal)
    {
        // The full UTF-8 classifier is encoded injectively. Replacing punctuation with '_' made distinct legal
        // Kotlin names such as `A-B` and A_B compete for the same MethodDef name on one existential outer.
        var token = Convert.ToHexString(Encoding.UTF8.GetBytes(inner.Name));
        return "$star$new$" + token + "$" + ordinal;
    }

    static bool IsInner(JsonObject type) =>
        type["mods"] is JsonObject mods && Bool(mods["inner"]);

    internal static string ExistentialSlotIdentity(string declarationId, string semanticCarrierOwner) =>
        DeclarationIdentityBinding.PhysicalOnlyId(declarationId, "existential-slot:" + semanticCarrierOwner);

    static TypeNode ProjectOwnerMethodBound(TypeNode bound, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, bool physical = true, int ownerCount = int.MaxValue)
        => ProjectOwnerMethodBound(bound, CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs),
            physical, ownerCount);

    static TypeNode ProjectOwnerMethodBound(TypeNode bound, CarrierProjectionContext context,
        bool physical = true, int ownerCount = int.MaxValue)
    {
        TypeNode Project(TypeNode node) => node switch
        {
            TypeNode.Tv { Scope: "type" } tv when tv.I < ownerCount => new TypeNode.Star(),
            TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(Project).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(Project(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(Project(o.Of)),
            TypeNode.Array a => new TypeNode.Array(Project(a.Elem), a.Rank, a.SzArray),
            _ => node,
        };
        // A direct edge R : T still supplies T's own bound graph. Constructed bounds instead name an existential
        // receiver: Kotlin variance does not imply the same closed CLR generic construction for value arguments.
        if (bound is TypeNode.Tv) return bound;
        var projected = Project(bound);
        return physical && projected is TypeNode.Fqn named && context.TryExistentialCarrier(named.Name, out var carrier)
            ? new TypeNode.Fqn(carrier) : projected;
    }

    // Kotlin generic bounds relate source views, not necessarily the physical constructions used as method/type
    // arguments. Every compiler-owned closure and its value carrier implements this same nominal classifier.
    // Foreign/native heads have no such compiler-owned proof and retain their existing exact constraint policy.
    static TypeNode PhysicalBoundClassifier(TypeNode bound, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs) => PhysicalBoundClassifier(bound,
            CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs));

    static TypeNode PhysicalBoundClassifier(TypeNode bound, CarrierProjectionContext context) =>
        StripSourceNullability(bound) is TypeNode.Fqn { Args: { Length: > 0 } } named
            && !context.References.TryResolveClrOwner(named.Name, out _, out _)
            && context.TryExistentialCarrier(named.Name, out var carrier)
                ? new TypeNode.Fqn(carrier) : null;

    static bool ProjectsCapturedBound(TypeNode bound, int index, int outerTypeParamCount,
        NullableRepresentationFrame frame, ReferenceMetadataIndex references = null) =>
        ContainsOwnerPrefixTv(bound, outerTypeParamCount)
        || (references == null
            ? OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame)
            : OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame, references));

    static void RewriteMethodArguments(JsonObject call, JsonArray arguments,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases, bool boundDeclaration, bool preserveArrayShape)
    {
        var identity = Str(call[DeclarationIdentityBinding.Key]);
        var owner = TypeJson.Read(call["calleeOwner"] ?? call["ownerType"] ?? call["owner"]) as TypeNode.Fqn;
        var selected = owner != null && identity != null
            && _localDeclarations.TryGetValue((owner.Name, identity), out var declaration) ? declaration
            : _nativeProofs?.LocalMethod(identity);
        var frame = selected != null ? NullableRepresentationTypes.DeclarationMethodFrame(selected)
            : refs.NullableMethodFrame(identity);
        if (frame != null && frame.PhysicalArity != arguments.Count)
            throw new InvalidOperationException("Selected method arguments disagree with the declaration's representation frame");
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = TypeJson.Read(arguments[index]);
            if (argument == null) continue;
            // Method witnesses use the same native-instantiation role as owner arguments and addressed slots.
            // Selected constructor factories additionally carry their already-authored physical frame.
            var witness = frame == null || frame.PhysicalSlot(index).Representation
                is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable;
            arguments[index] = TypeJson.Write(RewriteType(argument, owners, refs,
                boundDeclaration, localClrAliases,
                preserveArrayShape: preserveArrayShape,
                preserveConstructedHead: witness && !ContainsExistentialProjection(argument)
                    && (PreservesNativeWitnessHead(argument, owners, refs)
                    || Bool(call[InnerFactoryArgumentsKey])
                    || _nativeProofs?.ExactMethodArgument(selected, identity, index) == true
                    || StripSourceNullability(argument) is TypeNode.Fqn alias
                    && RequiresOpaqueVariantAlias(alias, owners, refs, localClrAliases)
                    && !ContainsExistentialProjection(alias)
                    && !ProjectionCanChangeConstruction(alias, owners, refs))));
        }
    }

    static bool ContainsManagedReference(TypeNode type) => type switch
    {
        TypeNode.ByRef or TypeNode.Ptr => true,
        TypeNode.Mod modifier => ContainsManagedReference(modifier.M)
            || ContainsManagedReference(modifier.Of),
        TypeNode.Projection projection => ContainsManagedReference(projection.Of),
        TypeNode.Nullable nullable => ContainsManagedReference(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsManagedReference(oblivious.Of),
        TypeNode.Array array => ContainsManagedReference(array.Elem),
        TypeNode.Fqn { Args: { } arguments } => arguments.Any(ContainsManagedReference),
        TypeNode.Fn function => ContainsManagedReference(function.Ret)
            || function.Params.Any(ContainsManagedReference)
            || function.Recv != null && ContainsManagedReference(function.Recv)
            || function.Ctx?.Any(ContainsManagedReference) == true,
        _ => false,
    };

    static TypeNode ProjectedConstructionFallback(TypeNode argument, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localNames)
    {
        argument = argument switch
        {
            TypeNode.Projection { Variance: "out" } projection => projection.Of,
            TypeNode.Projection => new TypeNode.Fqn("kotlin.Any"),
            TypeNode.Star => new TypeNode.Fqn("kotlin.Any"),
            _ => argument,
        };
        while (argument is TypeNode.Nullable nullable) argument = nullable.Of;
        while (argument is TypeNode.Oblivious oblivious) argument = oblivious.Of;
        if (ContainsExistentialProjection(argument)) argument = new TypeNode.Fqn("kotlin.Any");
        var physical = BirTypeLowering.LowerPhysicalType(argument, refs.Aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: true, localTypeNames: localNames, nullableFrames: refs.NullableTypeFrames);
        return physical is TypeNode.Fn function
            ? BirTypeLowering.DelegateFqnOf(function)
                ?? throw new InvalidOperationException("projected constructor fallback has no CLR delegate family")
            : physical;
    }

    static string ProjectedConstructorTypeKey(TypeNode type, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localNames)
    {
        while (type is TypeNode.Projection projection) type = projection.Of;
        var physical = BirTypeLowering.LowerPhysicalType(type, refs.Aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: false, localTypeNames: localNames, nullableFrames: refs.NullableTypeFrames);
        if (physical is TypeNode.Fn function)
            physical = BirTypeLowering.DelegateFqnOf(function)
                ?? throw new InvalidOperationException("projected constructor parameter has no CLR delegate family");
        return physical switch
        {
            TypeNode.Tv variable => (variable.Scope == "method" ? "m" : "t") + variable.I,
            TypeNode.ByRef byRef => "r[" + ProjectedConstructorTypeKey(byRef.Of, refs, localNames) + "]",
            TypeNode.Array array => "a" + (array.SzArray ? "s" : "m") + array.Rank + "["
                + ProjectedConstructorTypeKey(array.Elem, refs, localNames) + "]",
            TypeNode.Fqn { Args: { } arguments } fqn => "g{" + NormalizeProjectedConstructorTypeName(fqn.Name)
                + "}<" + string.Join(",", arguments.Select(argument =>
                    ProjectedConstructorTypeKey(argument, refs, localNames))) + ">",
            TypeNode.Fqn fqn => "n{" + NormalizeProjectedConstructorTypeName(fqn.Name) + "}",
            // LowerPhysicalType retains nullable VALUE types as the semantic nullable shell. Reflection observes the
            // corresponding System.Nullable<T> generic construction, so the structural descriptor must spell that
            // physical shell rather than collide with the non-nullable T overload.
            TypeNode.Nullable nullable => "g{System.Nullable}<"
                + ProjectedConstructorTypeKey(nullable.Of, refs, localNames) + ">",
            TypeNode.Oblivious oblivious => ProjectedConstructorTypeKey(oblivious.Of, refs, localNames),
            TypeNode.Mod modifier => ProjectedConstructorTypeKey(modifier.Of, refs, localNames),
            _ => throw new NotSupportedException(
                $"bir2cir: projected constructor parameter type `{SupertypeGraph.TypeKey(physical)}` "
                + "has no runtime structural key"),
        };
    }

    static string NormalizeProjectedConstructorTypeName(string name)
    {
        var result = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current == '`')
            {
                while (index + 1 < name.Length && char.IsDigit(name[index + 1])) index++;
                continue;
            }
            result.Append(current == '+' ? '.' : current);
        }
        return result.ToString();
    }

    static TypeNode ReplaceEquivalentType(TypeNode type, TypeNode expected, TypeNode replacement)
    {
        if (type == null) return null;
        if (SupertypeGraph.TypeKey(type) == SupertypeGraph.TypeKey(expected)) return replacement;
        return type switch
        {
            TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
                args.Select(argument => ReplaceEquivalentType(argument, expected, replacement)).ToArray()),
            TypeNode.Nullable nullable => new TypeNode.Nullable(
                ReplaceEquivalentType(nullable.Of, expected, replacement)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(
                ReplaceEquivalentType(oblivious.Of, expected, replacement)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
                ReplaceEquivalentType(projection.Of, expected, replacement)),
            TypeNode.Array array => new TypeNode.Array(
                ReplaceEquivalentType(array.Elem, expected, replacement), array.Rank, array.SzArray),
            TypeNode.ByRef byRef => new TypeNode.ByRef(
                ReplaceEquivalentType(byRef.Of, expected, replacement)),
            TypeNode.Ptr pointer => new TypeNode.Ptr(
                ReplaceEquivalentType(pointer.Of, expected, replacement)),
            TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
                ReplaceEquivalentType(modifier.M, expected, replacement),
                ReplaceEquivalentType(modifier.Of, expected, replacement)),
            TypeNode.Fn function => new TypeNode.Fn(function.Suspend,
                ReplaceEquivalentType(function.Ret, expected, replacement),
                function.Params.Select(parameter => ReplaceEquivalentType(parameter, expected, replacement)).ToArray(),
                function.Recv == null ? null : ReplaceEquivalentType(function.Recv, expected, replacement),
                function.Clr,
                function.Ctx?.Select(context => ReplaceEquivalentType(context, expected, replacement)).ToArray()),
            _ => type,
        };
    }

    static bool ContainsExistential(TypeNode type,
        IReadOnlySet<int> typeParameters, IReadOnlySet<int> methodParameters) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Tv { Scope: "type" } tv => typeParameters?.Contains(tv.I) == true,
        TypeNode.Tv { Scope: "method" } tv => methodParameters?.Contains(tv.I) == true,
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsExistential(argument, typeParameters, methodParameters)),
        TypeNode.Nullable nullable => ContainsExistential(nullable.Of, typeParameters, methodParameters),
        TypeNode.Oblivious oblivious => ContainsExistential(oblivious.Of, typeParameters, methodParameters),
        TypeNode.Array array => ContainsExistential(array.Elem, typeParameters, methodParameters),
        TypeNode.ByRef byRef => ContainsExistential(byRef.Of, typeParameters, methodParameters),
        TypeNode.Ptr pointer => ContainsExistential(pointer.Of, typeParameters, methodParameters),
        TypeNode.Mod modifier => ContainsExistential(modifier.M, typeParameters, methodParameters)
            || ContainsExistential(modifier.Of, typeParameters, methodParameters),
        TypeNode.Fn function => ContainsExistential(function.Ret, typeParameters, methodParameters)
            || function.Params.Any(parameter => ContainsExistential(parameter, typeParameters, methodParameters))
            || function.Recv != null && ContainsExistential(function.Recv, typeParameters, methodParameters)
            || function.Ctx?.Any(context => ContainsExistential(context, typeParameters, methodParameters)) == true,
        _ => false,
    };

    static bool TryExistentialCarrier(string semanticOwner, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, out string physicalOwner) =>
        CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs).TryExistentialCarrier(semanticOwner, out physicalOwner);

    static TypeNode StripSourceNullability(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => StripSourceNullability(nullable.Of),
        TypeNode.Oblivious oblivious => StripSourceNullability(oblivious.Of),
        _ => type,
    };

    // Project through the declarations that actually own the relation. `defs` includes lifted local/object types
    // even though they intentionally do not own a public existential ABI; referenced edges come only from trusted
    // metadata. A unique nearest construction is required, matching local slot binding's ambiguity rule.

    // Current-format referenced BIR can carry the exact CLR TypeDef spelling while reflection-derived inheritance
    // edges use the semantic Kotlin spelling. They identify the same declaration after arity/nesting normalization.
    // Keep two distinct exact CLR spellings distinct: flattened semantic names cannot disambiguate their ownership.
    static bool SameDeclarationOwner(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        var leftExact = left.Contains('`') || left.Contains('+');
        var rightExact = right.Contains('`') || right.Contains('+');
        if (leftExact && rightExact) return false;
        return string.Equals(
            ReferenceMetadataIndex.BareOwnerFqn(left).Replace('+', '.'),
            ReferenceMetadataIndex.BareOwnerFqn(right).Replace('+', '.'),
            StringComparison.Ordinal);
    }

    // A star smart-cast keeps the receiver's most-derived Kotlin type (`ComparableRange<*>.isEmpty`) even when the
    // member is declared by an ancestor (`ClosedRange<T>.isEmpty`).  Once the receiver becomes an erased interface,
    // CIR must name that exact declaring interface; ilemit is intentionally not allowed to search/infer it.

    static TypeNode CloseDeclarationType(TypeNode type, IReadOnlyList<TypeNode> ownerArgs,
        IReadOnlyList<TypeNode> methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Count
            => ownerArgs[tv.I] is TypeNode.Star ? new TypeNode.Fqn("kotlin.Any") : ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Count
            => methodArgs[tv.I] is TypeNode.Star ? new TypeNode.Fqn("kotlin.Any") : methodArgs[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            CloseDeclarationType(projection.Of, ownerArgs, methodArgs)),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => CloseDeclarationType(arg, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(CloseDeclarationType(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(CloseDeclarationType(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(CloseDeclarationType(a.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(CloseDeclarationType(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(CloseDeclarationType(p.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            CloseDeclarationType(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(parameter => CloseDeclarationType(parameter, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : CloseDeclarationType(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(context => CloseDeclarationType(context, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    internal static TypeNode SubstituteMethodTypeArguments(TypeNode type, IReadOnlyList<TypeNode> args) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Count => args[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            SubstituteMethodTypeArguments(projection.Of, args)),
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(argument => SubstituteMethodTypeArguments(argument, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteMethodTypeArguments(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteMethodTypeArguments(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteMethodTypeArguments(a.Elem, args), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteMethodTypeArguments(b.Of, args)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteMethodTypeArguments(p.Of, args)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, SubstituteMethodTypeArguments(m.M, args),
            SubstituteMethodTypeArguments(m.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            SubstituteMethodTypeArguments(fn.Ret, args),
            fn.Params.Select(parameter => SubstituteMethodTypeArguments(parameter, args)).ToArray(),
            fn.Recv == null ? null : SubstituteMethodTypeArguments(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(context => SubstituteMethodTypeArguments(context, args)).ToArray()),
        _ => type,
    };

    internal static TypeNode SubstituteDeclarationTypeArguments(TypeNode type,
        IReadOnlyList<TypeNode> ownerArguments, IReadOnlyList<TypeNode> methodArguments) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArguments.Count
            => ownerArguments[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArguments.Count
            => methodArguments[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            SubstituteDeclarationTypeArguments(projection.Of, ownerArguments, methodArguments)),
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(argument => SubstituteDeclarationTypeArguments(
                argument, ownerArguments, methodArguments)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteDeclarationTypeArguments(
            n.Of, ownerArguments, methodArguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteDeclarationTypeArguments(
            o.Of, ownerArguments, methodArguments)),
        TypeNode.Array a => new TypeNode.Array(SubstituteDeclarationTypeArguments(
            a.Elem, ownerArguments, methodArguments), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteDeclarationTypeArguments(
            b.Of, ownerArguments, methodArguments)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteDeclarationTypeArguments(
            p.Of, ownerArguments, methodArguments)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req,
            SubstituteDeclarationTypeArguments(m.M, ownerArguments, methodArguments),
            SubstituteDeclarationTypeArguments(m.Of, ownerArguments, methodArguments)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            SubstituteDeclarationTypeArguments(fn.Ret, ownerArguments, methodArguments),
            fn.Params.Select(parameter => SubstituteDeclarationTypeArguments(
                parameter, ownerArguments, methodArguments)).ToArray(),
            fn.Recv == null ? null : SubstituteDeclarationTypeArguments(
                fn.Recv, ownerArguments, methodArguments), fn.Clr,
            fn.Ctx?.Select(context => SubstituteDeclarationTypeArguments(
                context, ownerArguments, methodArguments)).ToArray()),
        _ => type,
    };

    static bool IsExistentialPhysicalCarrier(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type is TypeNode.Fqn { Args: null } f
            && (owners.Values.Any(owner => owner.ErasedName == f.Name)
                || refs.IsExistentialPhysicalOwner(f.Name));
    }

    static bool ContainsPhysicalExistential(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => IsExistentialPhysicalCarrier(f, owners, refs)
            || f.Args?.Any(argument => ContainsPhysicalExistential(argument, owners, refs)) == true,
        TypeNode.Nullable nullable => ContainsPhysicalExistential(nullable.Of, owners, refs),
        TypeNode.Oblivious oblivious => ContainsPhysicalExistential(oblivious.Of, owners, refs),
        TypeNode.Projection projection => ContainsPhysicalExistential(projection.Of, owners, refs),
        TypeNode.Array array => ContainsPhysicalExistential(array.Elem, owners, refs),
        TypeNode.ByRef byRef => ContainsPhysicalExistential(byRef.Of, owners, refs),
        TypeNode.Ptr pointer => ContainsPhysicalExistential(pointer.Of, owners, refs),
        TypeNode.Mod modifier => ContainsPhysicalExistential(modifier.M, owners, refs)
            || ContainsPhysicalExistential(modifier.Of, owners, refs),
        TypeNode.Fn function => ContainsPhysicalExistential(function.Ret, owners, refs)
            || function.Params.Any(parameter => ContainsPhysicalExistential(parameter, owners, refs))
            || function.Recv != null && ContainsPhysicalExistential(function.Recv, owners, refs)
            || function.Ctx?.Any(context => ContainsPhysicalExistential(context, owners, refs)) == true,
        _ => false,
    };

    internal static bool IsVoidResult(TypeNode type) =>
        type is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "void" or "System.Void" };

    internal static void AlignCallResult(JsonObject call, TypeNode physicalResult, bool protectExactCast)
    {
        var semanticResult = NodeType.Stamp(call);
        if (physicalResult == null || semanticResult == null || physicalResult.Equals(semanticResult)
            || IsVoidResult(physicalResult) && IsVoidResult(semanticResult)) return;

        var hadSty = call["sty"] != null;
        var inner = call.DeepClone().AsObject();
        inner["ret"] = TypeJson.Write(physicalResult);
        if (inner["dynRet"] != null) inner["dynRet"] = TypeJson.Write(physicalResult);
        if (inner["sty"] != null) inner["sty"] = TypeJson.Write(physicalResult);

        var restored = StringCharSequenceBridge.RestoreMaterializedValue(inner, physicalResult, semanticResult);
        if (restored != null)
        {
            call.Clear();
            foreach (var pair in restored) call[pair.Key] = pair.Value?.DeepClone();
            if (hadSty) call["sty"] = TypeJson.Write(semanticResult);
            return;
        }

        foreach (var key in call.Select(pair => pair.Key).ToList()) call.Remove(key);
        call["k"] = "cast";
        call["type"] = TypeJson.Write(semanticResult);
        call["e"] = inner;
        if (hadSty) call["sty"] = TypeJson.Write(semanticResult);
        // This cast is the exact semantic projection of a successfully raw-classifier-tested value, not another
        // Kotlin erased `as G<T>` operation for this pass to collapse back to the existential carrier.
        if (protectExactCast) call["_exactBridgeCast"] = true;
    }

    static readonly HashSet<string> TransientMarkerKeys = new(StringComparer.Ordinal)
    {
        "_exactBridgeCast", "reifiedTypeOperand", ExactBridgeOwnerCallKey,
        ExactOuterKey, DelegationOuterSlotKey, GenericRepresentationPolicy.NativeLocationKey,
        GenericRepresentationPolicy.FieldAddressKey, GenericRepresentationPolicy.PhysicalDeclarationSignatureKey,
        InnerFactoryArgumentsKey,
    };

    // These scalar transport facts survive until the strict address handoff.
    // Generated graph/type visitors do not expose them as semantic child slots.
    static void RemoveTransientMarkers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var field in TransientMarkerKeys) obj.Remove(field);
                foreach (var value in obj.Select(pair => pair.Value).ToList())
                    if (value != null) RemoveTransientMarkers(value);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) RemoveTransientMarkers(value);
                break;
        }
    }

    static TypeNode RewriteType(TypeNode type, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, bool boundDeclaration = false,
        IReadOnlyDictionary<string, string> localClrAliases = null,
        bool preserveConstructedHead = false, bool preserveArrayShape = false)
        => RewriteType(type, CarrierProjectionContext.ForDom(owners, refs, _valueSlots, _nativeProofs, localClrAliases),
            boundDeclaration, preserveConstructedHead, preserveArrayShape);

    static TypeNode RewriteType(TypeNode type, CarrierProjectionContext context, bool boundDeclaration = false,
        bool preserveConstructedHead = false, bool preserveArrayShape = false)
    {
        var owners = context.Owners;
        var refs = context.References;
        var localClrAliases = context.LocalClrAliases;
        TypeNode[] Arguments(TypeNode.Fqn application)
        {
            var nativeOwner = IsNativeArrayOwner(application, refs, localClrAliases);
            // Imported native arguments are exact CLR witnesses. A trusted Kotlin binding alias instead
            // owns its storage projection, even when its physical head is a native generic definition.
            var nativeArguments = nativeOwner && context.Values?.IsBindingAlias(application.Name) != true
                && localClrAliases?.ContainsKey(application.Name) != true;
            var frame = nativeOwner ? null : owners.TryGetValue(application.Name, out var declaration)
                ? context.OwnerFrame(declaration)
                : refs?.NullableTypeFrames.GetValueOrDefault(application.Name);
            // A retained generic head carries native witnesses as well as value
            // companions. Its recorded physical frame applies equally to a new,
            // its value slot, and an exact lexical declaration owner.
            return application.Args.Select((argument, index) => RewriteType(
                argument, context, boundDeclaration,
                preserveArrayShape: nativeOwner || frame != null && frame.PhysicalSlot(index).Representation
                    is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable,
                // An exact declaration proof does not make an existential argument reifiable. Its existing
                // constrained-storage projection must choose a valid bound, not manufacture G<Object> for G<*>.
                preserveConstructedHead: (nativeArguments && PreservesNativeWitnessHead(argument, context)
                        || context.NativeProofs?.ExactTypeArgument(application.Name, index) == true)
                    && !ContainsExistentialProjection(argument)
                    || frame != null && frame.PhysicalSlot(index).Representation
                    is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable
                    // Kotlin declaration-site variance uses the nominal carrier
                    // even as a nested witness. Keeping its concrete head here
                    // would disagree with the argument's allocated value type.
                    && !(StripSourceNullability(argument) is TypeNode.Fqn variant
                        && context.ProjectionCanChangeConstruction(variant)))).ToArray();
        }
        switch (type)
        {
            case TypeNode.Fqn { Args: { } preservedArgs } preserved when preserveConstructedHead:
                return new TypeNode.Fqn(preserved.Name, Arguments(preserved));
            case TypeNode.Fqn preserved when preserveConstructedHead:
                return preserved;
            case TypeNode.Fqn f when !boundDeclaration
                && context.RequiresOpaqueVariantAlias(f):
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Fqn f when !boundDeclaration
                && context.RequiresKotlinValueCarrier(f)
                && context.TryExistentialCarrier(f.Name, out var variantCarrier):
                return new TypeNode.Fqn(variantCarrier);
            case TypeNode.Fqn { Args: { } nestedArgs } nestedForeign
                when !boundDeclaration && nestedArgs.Any(ContainsExistentialProjection)
                    && IsOpaqueForeignProjection(nestedForeign, refs, localClrAliases):
                // A star anywhere below a foreign invariant construction makes the whole construction
                // non-reifiable on the CLR.  In particular Outer<Inner<*>> is not Outer<object> (nor
                // Outer<Inner<object>>); keep the original runtime value in one opaque object slot and let
                // ForeignStarProjectionBinding route member access through the reflection ABI.
                if (IsByRefLikeForeignProjection(nestedForeign, refs, localClrAliases))
                    throw new NotSupportedException(
                        $"bir2cir: foreign byref-like generic star projection `{nestedForeign.Name}<*>` has no boxable CLR existential representation");
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Fqn { Args: { } args } f when !boundDeclaration && args.Any(IsExistentialArgument):
            {
                var erased = owners.TryGetValue(f.Name, out var local) ? local.ErasedName : null;
                if (local != null && local.Needed) return new TypeNode.Fqn(erased);
                if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
                    return new TypeNode.Fqn(referenced);
                // A foreign CLR generic cannot be retrofitted to implement DotKt's nominal existential. Its value
                // slot is object; classifier checks/casts/member dispatch were already authored explicitly by
                // ForeignStarProjectionBinding above. Never manufacture the invariant fiction G<object>.
                if (!refs.HasDotKtOwner(f.Name) && IsOpaqueForeignProjection(f, refs, localClrAliases))
                {
                    if (IsByRefLikeForeignProjection(f, refs, localClrAliases))
                        throw new NotSupportedException(
                            $"bir2cir: foreign byref-like generic star projection `{f.Name}<*>` has no boxable CLR existential representation");
                    return new TypeNode.Fqn("kotlin.Any");
                }
                return new TypeNode.Fqn(f.Name, Arguments(f));
            }
            case TypeNode.Star:
                // A residual star belongs to an unsupported shape (multi-parameter mask / external CLR generic).
                // Keep CIR well-formed with the explicit erasure; supported local/reference one-parameter owners
                // have already become their existential view above.
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Projection p:
            {
                // A use-site projection is existential at the CLR boundary. A compiler-authored local/referenced
                // generic has a nominal non-generic carrier implemented by every closed construction, so retain the
                // classifier without inventing G<object>. Foreign generics have no such nominal view and use the
                // same opaque object representation as a star projection.
                var projected = StripProjectionShell(p.Of);
                if (projected is TypeNode.Fqn f && context.ProjectionCanChangeConstruction(f))
                {
                    if (owners.TryGetValue(f.Name, out var local) && local.Needed)
                        return ReplaceProjectionCore(p.Of, f, new TypeNode.Fqn(local.ErasedName));
                    if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
                        return ReplaceProjectionCore(p.Of, f, new TypeNode.Fqn(referenced));
                }
                return RewriteType(p.Of, context, boundDeclaration);
            }
            case TypeNode.Fqn { Args: { } args } f:
                return new TypeNode.Fqn(f.Name, Arguments(f));
            case TypeNode.Nullable n: return new TypeNode.Nullable(RewriteType(
                n.Of, context, boundDeclaration,
                preserveConstructedHead: preserveConstructedHead, preserveArrayShape: preserveArrayShape));
            case TypeNode.Oblivious o: return new TypeNode.Oblivious(RewriteType(
                o.Of, context, boundDeclaration,
                preserveConstructedHead: preserveConstructedHead, preserveArrayShape: preserveArrayShape));
            case TypeNode.Array a:
                if (!boundDeclaration && !preserveArrayShape && GenericArrayStorageLowering.NeedsStorage(a))
                    return new TypeNode.Fqn("System.Array");
                if (!boundDeclaration && TryWritableVariantArrayElement(
                        a.Elem, context, out _, out var writableElement))
                    return new TypeNode.Array(writableElement, a.Rank, a.SzArray);
                return new TypeNode.Array(RewriteType(
                    a.Elem, context, boundDeclaration,
                    preserveConstructedHead: PreservesNativeWitnessHead(a.Elem, context),
                    preserveArrayShape: preserveArrayShape), a.Rank, a.SzArray);
            case TypeNode.ByRef b: return new TypeNode.ByRef(RewriteType(
                b.Of, context, boundDeclaration,
                preserveConstructedHead: PreservesNativeWitnessHead(b.Of, context), preserveArrayShape: true));
            case TypeNode.Mod m: return new TypeNode.Mod(m.Req,
                RewriteType(m.M, context, boundDeclaration),
                RewriteType(m.Of, context, boundDeclaration));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, RewriteType(
                    fn.Ret, context, boundDeclaration),
                fn.Params.Select(p => RewriteType(
                    p, context, boundDeclaration)).ToArray(),
                fn.Recv == null ? null : RewriteType(
                    fn.Recv, context, boundDeclaration), fn.Clr,
                fn.Ctx?.Select(argument => RewriteType(
                    argument, context, boundDeclaration)).ToArray());
            default: return type;
        }
    }

    static bool IsOpaqueForeignProjection(TypeNode.Fqn type, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        if (localClrAliases != null && localClrAliases.TryGetValue(type.Name, out var physical))
            return !BirTypeLowering.AliasHeadRequiresSourceIdentity(type.Name, physical)
                || BirTypeLowering.ProjectedAliasHasReifiedGenericHead(type.Name, physical, type.Args, refs.Aliases, localClrAliases);
        return ForeignStarProjectionBinding.IsForeignStarType(type, refs);
    }

    static bool IsByRefLikeForeignProjection(TypeNode.Fqn type, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        if (refs.IsByRefLikeFqn(type)) return true;
        return localClrAliases != null && localClrAliases.TryGetValue(type.Name, out var physical)
            && refs.IsByRefLikeFqn(new TypeNode.Fqn(physical, type.Args));
    }

    static bool ContainsOwnerPrefixTv(TypeNode t, int count) => t switch
    {
        TypeNode.Tv { Scope: "type" } tv => tv.I < count,
        TypeNode.Fqn { Args: { } args } => args.Any(arg => ContainsOwnerPrefixTv(arg, count)),
        TypeNode.Nullable n => ContainsOwnerPrefixTv(n.Of, count),
        TypeNode.Oblivious o => ContainsOwnerPrefixTv(o.Of, count),
        TypeNode.Projection p => ContainsOwnerPrefixTv(p.Of, count),
        TypeNode.Array a => ContainsOwnerPrefixTv(a.Elem, count),
        TypeNode.ByRef b => ContainsOwnerPrefixTv(b.Of, count),
        TypeNode.Ptr p => ContainsOwnerPrefixTv(p.Of, count),
        TypeNode.Mod m => ContainsOwnerPrefixTv(m.M, count) || ContainsOwnerPrefixTv(m.Of, count),
        TypeNode.Fn fn => ContainsOwnerPrefixTv(fn.Ret, count)
            || fn.DelegateParams.Any(p => ContainsOwnerPrefixTv(p, count))
            || fn.Ctx?.Any(p => ContainsOwnerPrefixTv(p, count)) == true,
        _ => false,
    };

    internal static bool ContainsOwnerTv(TypeNode t) => t switch
    {
        TypeNode.Tv { Scope: "type" } => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOwnerTv),
        TypeNode.Nullable n => ContainsOwnerTv(n.Of),
        TypeNode.Oblivious o => ContainsOwnerTv(o.Of),
        TypeNode.Projection p => ContainsOwnerTv(p.Of),
        TypeNode.Array a => ContainsOwnerTv(a.Elem),
        TypeNode.ByRef b => ContainsOwnerTv(b.Of),
        TypeNode.Fn fn => ContainsOwnerTv(fn.Ret) || fn.Params.Any(ContainsOwnerTv)
            || (fn.Recv != null && ContainsOwnerTv(fn.Recv)),
        _ => false,
    };

    static bool ContainsStarOrTypeVariable(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection or TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStarOrTypeVariable),
        TypeNode.Nullable n => ContainsStarOrTypeVariable(n.Of),
        TypeNode.Oblivious o => ContainsStarOrTypeVariable(o.Of),
        TypeNode.Array a => ContainsStarOrTypeVariable(a.Elem),
        TypeNode.ByRef b => ContainsStarOrTypeVariable(b.Of),
        TypeNode.Ptr p => ContainsStarOrTypeVariable(p.Of),
        TypeNode.Mod m => ContainsStarOrTypeVariable(m.M) || ContainsStarOrTypeVariable(m.Of),
        TypeNode.Fn fn => ContainsStarOrTypeVariable(fn.Ret) || fn.Params.Any(ContainsStarOrTypeVariable)
            || (fn.Recv != null && ContainsStarOrTypeVariable(fn.Recv))
            || (fn.Ctx?.Any(ContainsStarOrTypeVariable) ?? false),
        _ => false,
    };

    static bool ContainsStar(TypeNode t) => t switch
    {
        TypeNode.Star => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStar),
        TypeNode.Nullable n => ContainsStar(n.Of),
        TypeNode.Oblivious o => ContainsStar(o.Of),
        TypeNode.Array a => ContainsStar(a.Elem),
        TypeNode.ByRef b => ContainsStar(b.Of),
        TypeNode.Fn fn => ContainsStar(fn.Ret) || fn.Params.Any(ContainsStar)
            || (fn.Recv != null && ContainsStar(fn.Recv)),
        _ => false,
    };

    static bool ContainsExistentialProjection(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExistentialProjection),
        TypeNode.Nullable n => ContainsExistentialProjection(n.Of),
        TypeNode.Oblivious o => ContainsExistentialProjection(o.Of),
        TypeNode.Array a => ContainsExistentialProjection(a.Elem),
        TypeNode.ByRef b => ContainsExistentialProjection(b.Of),
        TypeNode.Ptr p => ContainsExistentialProjection(p.Of),
        TypeNode.Mod m => ContainsExistentialProjection(m.M) || ContainsExistentialProjection(m.Of),
        TypeNode.Fn fn => ContainsExistentialProjection(fn.Ret) || fn.Params.Any(ContainsExistentialProjection)
            || (fn.Recv != null && ContainsExistentialProjection(fn.Recv))
            || (fn.Ctx?.Any(ContainsExistentialProjection) ?? false),
        _ => false,
    };

    static bool IsExistentialArgument(TypeNode type) => type is TypeNode.Star or TypeNode.Projection;

    static TypeNode StripProjectionShell(TypeNode type) => type switch
    {
        TypeNode.Nullable n => StripProjectionShell(n.Of),
        TypeNode.Oblivious o => StripProjectionShell(o.Of),
        _ => type,
    };

    static TypeNode ReplaceProjectionCore(TypeNode shell, TypeNode.Fqn core, TypeNode replacement) => shell switch
    {
        TypeNode.Nullable n => new TypeNode.Nullable(ReplaceProjectionCore(n.Of, core, replacement)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(ReplaceProjectionCore(o.Of, core, replacement)),
        _ => replacement,
    };

    static bool ContainsExplicitExistential(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExplicitExistential),
        TypeNode.Nullable n => ContainsExplicitExistential(n.Of),
        TypeNode.Oblivious o => ContainsExplicitExistential(o.Of),
        TypeNode.Array a => ContainsExplicitExistential(a.Elem),
        TypeNode.ByRef b => ContainsExplicitExistential(b.Of),
        TypeNode.Ptr p => ContainsExplicitExistential(p.Of),
        TypeNode.Mod m => ContainsExplicitExistential(m.M) || ContainsExplicitExistential(m.Of),
        TypeNode.Fn fn => ContainsExplicitExistential(fn.Ret) || fn.Params.Any(ContainsExplicitExistential)
            || (fn.Recv != null && ContainsExplicitExistential(fn.Recv))
            || (fn.Ctx?.Any(ContainsExplicitExistential) ?? false),
        _ => false,
    };

    static TypeNode EraseOwnerTv(TypeNode t, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs) => t switch
    {
        TypeNode.Tv { Scope: "type" } => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fqn { Args: { } args } f when args.Any(ContainsOwnerTv)
            && owners.TryGetValue(f.Name, out var nested) && nested.Needed
            => new TypeNode.Fqn(nested.ErasedName),
        TypeNode.Fqn { Args: { } args } f when args.Any(ContainsOwnerTv)
            && refs.TryExistentialPhysicalOwner(f.Name, out var referenced)
            => new TypeNode.Fqn(referenced),
        // A foreign/reified construction containing an owner slot is not covariantly substitutable in general:
        // List<T> is not List<object>, T[] is not object[] for value T, and Func<T,R> has variance of its own.
        // The existential slot therefore carries the whole value as object; the forwarding bridge casts that object
        // back to the exact closed declaration type before calling the real member. DotKt-authored generic results
        // were handled by the two nominal existential cases above and retain their identity-preserving view.
        TypeNode.Fqn { Args: { } args } when args.Any(ContainsOwnerTv)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Array a when ContainsOwnerTv(a.Elem)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.ByRef b when ContainsOwnerTv(b.Of)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fn fn when ContainsOwnerTv(fn)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(
            f.Name, args.Select(a => EraseOwnerTv(a, owners, refs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseOwnerTv(n.Of, owners, refs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(EraseOwnerTv(o.Of, owners, refs)),
        TypeNode.Array a => new TypeNode.Array(EraseOwnerTv(a.Elem, owners, refs)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseOwnerTv(b.Of, owners, refs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, EraseOwnerTv(fn.Ret, owners, refs),
            fn.Params.Select(p => EraseOwnerTv(p, owners, refs)).ToArray(),
            fn.Recv == null ? null : EraseOwnerTv(fn.Recv, owners, refs)),
        _ => t,
    };

    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };

    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static int? Int(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
