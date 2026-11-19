using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CovariantInterfaceReturnBridge
{
    internal static void SourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning covariant interface bridge: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        ValueTypeOracle isValue = _ => false;
        var broad = new TypeNode.Fqn("CovariantBase");
        var narrow = new TypeNode.Fqn("CovariantDerived");
        TypeNode Box(TypeNode argument) => new TypeNode.Fqn("CovariantBox", new[] { argument });
        var methodVariable = new TypeNode.Tv("method", 0);
        var ownerVariable = new TypeNode.Tv("type", 0);
        BirTypeParameter[] MethodFrame() => new[] { new BirTypeParameter { Name = "M",
            Constraints = new TypeNode[] { new TypeNode.Fqn("System.IComparable", new TypeNode[] { ownerVariable }) } } };
        var slot = new BirFunctionDeclaration { Name = "map", DeclarationId = "generic-slot", Ret = Box(broad),
            Params = new[] { new BirParameter { Name = "value", Type = methodVariable } }, TypeParams = MethodFrame(), Abstract = true };
        var implementation = new BirFunctionDeclaration { Name = "map", DeclarationId = "generic-body", Ret = Box(narrow),
            Params = new[] { new BirParameter { Name = "input", Type = methodVariable } }, TypeParams = MethodFrame(),
            Body = Array.Empty<IBirStatement>(), Overrides = new[] { new BirOverride {
                Owner = new TypeNode.Fqn("GenericContract", new TypeNode[] { ownerVariable }),
                Member = "map", Kind = "method", DeclarationId = slot.DeclarationId,
            } } };
        var contract = new BirInterfaceDefinition { Kind = "interface", Name = "GenericContract",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { slot } };
        var owner = new BirClassDefinition { Kind = "class", Name = "GenericOwner",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { implementation },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("GenericContract", new TypeNode[] { ownerVariable }) } };
        var file = new BirFileDeclaration { FileClass = "CovariantMethods", Types = new IBirTypeDeclaration[] { owner, contract } };
        var roots = new[] { file };
        var pending = KotlinOverrideSlotBridge.CaptureSourceParameters(roots);
        var originalParameters = pending.ReadSourceParameters();
        var bindings = new CurrentDeclarationBindings(roots);
        var originalTypes = file.Types.Value;
        var originalParameterVector = implementation.Params.Value;
        var originalFrame = implementation.TypeParams.Value;
        var originalSnapshot = implementation.DeepClone();
        var callbacks = 0;
        var bridged = ApplyAll(roots, refs, isValue, GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs),
            originalParameters, (actualFile, actualOwner, bridge) => {
                Require(ReferenceEquals(actualFile, file) && ReferenceEquals(actualOwner, owner)
                    && ReferenceEquals(actualOwner.Methods.Value.Last(), bridge) && bridge.ClrInterfaceImpls.Value.Length == 0,
                    "materialization callback lost append identity or moved after descriptor publication");
                Require(pending.AdmitMaterializedMethods(actualOwner, new[] { bridge }, bindings), "exact bridge append did not continue the frozen source view");
                callbacks++;
            });
        var generated = owner.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault());
        var call = (BirCallInstance)((BirReturn)generated.Body.Value.Single()).Value.Value;
        Require(callbacks == 1 && ReferenceEquals(file.Types.Value, originalTypes)
            && ReferenceEquals(owner.Methods.Value[0], implementation) && implementation.StructurallyEquals(originalSnapshot)
            && ReferenceEquals(implementation.Params.Value, originalParameterVector) && ReferenceEquals(implementation.TypeParams.Value, originalFrame),
            "narrow source declaration, frame or owning type inventory changed");
        Require(generated.Name.Value == "dotkt$covar$map$0" && generated.Vis.Value == "private" && generated.Virtual.Value
            && generated.Generated.Value && !generated.Static.Value && !generated.Override.Value && !generated.Abstract.Value
            && !generated.ObjectOverride.Value && generated.PhysicalSlotBridge.Value && !generated.ClrInterfaceSlotBridge.IsPresent
            && generated.Params.Value.Single().Type.Value == methodVariable && generated.Ret.Value == Box(broad)
            && !ReferenceEquals(generated.TypeParams.Value, originalFrame) && !ReferenceEquals(generated.TypeParams.Value[0], originalFrame[0]),
            "exact private bridge shape or independent generic declaration frame changed");
        Require(call.Virtual.Value && call.ClrOwnerResolved.Value && call.OwnerType.Value == new TypeNode.Fqn("GenericOwner", new TypeNode[] { ownerVariable })
            && call.Method.Value == "map" && call.Recv.Value is BirThis && call.Sig.Value.Single() == methodVariable
            && call.TypeArgs.Value.Single() == methodVariable && call.Args.Value.Single() is BirLocal { Name.Value: "input" }
            && call.Ret.Value == Box(narrow) && call.DynRet.Value == Box(narrow) && !call.Sty.IsPresent,
            "forwarder lost narrow virtual dispatch, open signature or explicit method arguments");
        var descriptor = generated.ClrInterfaceImpls.Value.Single();
        Require(descriptor.Owner.Value == owner.Interfaces.Value.Single() && descriptor.Member.Value == "map"
            && descriptor.Arity.Value == 1 && descriptor.Params.Value.Single() == methodVariable && descriptor.Ret.Value == Box(broad)
            && descriptor.TypeParams.Value[0].Constraints.Value.Single() == slot.TypeParams.Value[0].Constraints.Value.Single(),
            "descriptor lost exact owner/method frames or slot return");
        Require(bridged.Count == 1 && ReferenceEquals(bridged.Single().Implementation, implementation)
            && !bridged.Contains(new((BirFunctionDeclaration)implementation.DeepClone(), bridged.Single().Descriptor)),
            "bridged-slot identity matched a copied implementation rather than its actual occurrence");
        var sourceBinding = bindings.GetCapturedSource(implementation);
        var descriptorBinding = bindings.GetSource(descriptor);
        pending.Complete();
        var ledger = new SourceDeclarationProjection(roots, bindings);
        ledger.SealSourceContinuations();
        ledger.RequireOccurrence(implementation); ledger.RequireOccurrence(descriptor);
        Require(ReferenceEquals(sourceBinding.Source, owner.Methods.Value[0])
            && ReferenceEquals(descriptorBinding.Source, generated.ClrInterfaceImpls.Value[0])
            && originalParameters[implementation].Single() == methodVariable,
            "terminal custody lost exact original rows or the actual implementation/descriptor occurrence");
        bindings.CompleteSourceCustody(ledger);
        var released = false;
        try { _ = sourceBinding.Source; } catch (InvalidOperationException) { released = true; }
        Require(released && ReferenceEquals(bridged.Single().Implementation, implementation),
            "custody retained its source capability or changed the actual bridged-slot handle");
        ledger.Release();

        PropertyAndInterfaceSourceSelfTest(refs, isValue, broad, narrow);
        ReferencedAndSuspendSourceSelfTest(refs, isValue, broad, narrow);
        Console.WriteLine("[owning covariant interface bridge] self-test OK (exact source identity, frames, virtual forwarding, property/DIM, referenced slots, suspend and terminal custody)");
    }

    static void PropertyAndInterfaceSourceSelfTest(ReferenceMetadataIndex refs, ValueTypeOracle isValue, TypeNode broad, TypeNode narrow)
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning covariant property bridge: " + message); }
        TypeNode Box(TypeNode value) => new TypeNode.Fqn("CovariantBox", new[] { value });
        BirFunctionDeclaration Getter(string name, TypeNode result, bool abstractMember = false) => new() {
            Name = name, Params = Array.Empty<BirParameter>(), TypeParams = Array.Empty<BirTypeParameter>(), Ret = result,
            Abstract = abstractMember, PropertyName = "value", PropertyAccessor = "get", PropertyAssociation = "source-value",
        };
        BirOverride Edge(string owner) => new() { Owner = new TypeNode.Fqn(owner), Member = "value", Kind = "getter" };
        var first = new BirInterfaceDefinition { Kind = "interface", Name = "FirstContract", Methods = new[] { Getter("first-read", Box(broad), true) } };
        var second = new BirInterfaceDefinition { Kind = "interface", Name = "SecondContract", Methods = new[] { Getter("second-read", Box(broad), true) } };
        var implementation = Getter("narrow-read", Box(narrow));
        implementation.Overrides = new[] { Edge("FirstContract"), Edge("SecondContract") };
        var owner = new BirClassDefinition { Kind = "class", Name = "PropertyOwner", Methods = new[] { implementation },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("FirstContract"), new TypeNode.Fqn("SecondContract") } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { first, second, owner } };
        var roots = new[] { file };
        var slots = ApplyAll(roots, refs, isValue, GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs),
            new Dictionary<BirFunctionDeclaration, TypeNode[]>());
        var bridge = owner.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault());
        Require(slots.Count == 2 && bridge.ClrInterfaceImpls.Value.Length == 2 && owner.Properties.Value.Length == 1
            && bridge.PropertyName.Value == "value" && bridge.PropertyAccessor.Value == "get"
            && bridge.KotlinPropertyAccessorCarrier.Value.SourceAssociation.Value == "source-value"
            && owner.Properties.Value[0].KotlinAccessors.Value.SequenceEqual(new[] { "get" })
            && owner.Properties.Value[0].Type.Value == Box(broad), "equivalent slots did not share one body/property with exact source roles");
        var property = owner.Properties.Value[0];
        var setter = new BirFunctionDeclaration { Name = "broad-write" };
        owner.Methods = owner.Methods.Value.Append(setter).ToArray();
        KotlinPropertyAccessors.AssociateBridgeProperty(owner, setter, "value", "set", "source-value", new[] { Box(broad) }, new TypeNode.Fqn("kotlin.Unit"));
        Require(ReferenceEquals(owner.Properties.Value[0], property) && property.KotlinAccessors.Value.SequenceEqual(new[] { "get", "set" }),
            "exact getter/setter association replaced the property occurrence");

        var baseGetter = Getter("base-read", Box(broad), true);
        var dimGetter = Getter("dim-read", Box(narrow));
        dimGetter.Overrides = new[] { Edge("BaseProperty") };
        var baseInterface = new BirInterfaceDefinition { Kind = "interface", Name = "BaseProperty", Methods = new[] { baseGetter } };
        var derivedInterface = new BirInterfaceDefinition { Kind = "interface", Name = "DerivedProperty", Methods = new[] { dimGetter },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("BaseProperty") } };
        var body = Getter("class-read", Box(narrow));
        body.Overrides = new[] { Edge("DerivedProperty"), Edge("BaseProperty") };
        var implementing = new BirClassDefinition { Kind = "class", Name = "DimOwner", Methods = new[] { body },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("DerivedProperty") } };
        var dimFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { derivedInterface, baseInterface, implementing } };
        var dimRoots = new[] { dimFile };
        ApplyAll(dimRoots, refs, isValue, GenericRepresentationPolicy.CreateSource(dimRoots, refs.Aliases, refs), new Dictionary<BirFunctionDeclaration, TypeNode[]>());
        var dimBridge = derivedInterface.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault());
        var classBridge = implementing.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault());
        Require(dimBridge.ClrInterfaceSlotBridge.Value && !derivedInterface.Properties.IsPresent
            && dimBridge.PropertyAssociation.Value.EndsWith("|exact-interface", StringComparison.Ordinal)
            && classBridge.ClrInterfaceImpls.Value.Length == 1
            && classBridge.ClrInterfaceImpls.Value[0].Owner.Value == new TypeNode.Fqn("BaseProperty"),
            "DIM bridge was treated as another inherited declaration or emitted a duplicate interface Property row");

        // Ambiguity and object-erasure are deliberately owned elsewhere.
        var ambiguous = new BirClassDefinition { Kind = "class", Name = "AmbiguousProperty", Interfaces = owner.Interfaces,
            Methods = new[] { (BirFunctionDeclaration)implementation.DeepClone(), (BirFunctionDeclaration)implementation.DeepClone() } };
        var erasedSlot = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("object") };
        var erasedBody = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = narrow,
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("ErasedContract"), Member = "read", Kind = "method" } } };
        var erased = new BirClassDefinition { Kind = "class", Name = "ErasedOwner", Interfaces = new TypeNode[] { new TypeNode.Fqn("ErasedContract") }, Methods = new[] { erasedBody } };
        var excludedFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { first, second, ambiguous,
            new BirInterfaceDefinition { Kind = "interface", Name = "ErasedContract", Methods = new[] { erasedSlot } }, erased } };
        ApplyAll(new[] { excludedFile }, refs, isValue, GenericRepresentationPolicy.CreateSource(new[] { excludedFile }, refs.Aliases, refs), new Dictionary<BirFunctionDeclaration, TypeNode[]>());
        Require(ambiguous.Methods.Value.Length == 2 && erased.Methods.Value.Length == 1,
            "ambiguous source member or object-erased slot was claimed as covariance");
    }

    static void ReferencedAndSuspendSourceSelfTest(ReferenceMetadataIndex refs, ValueTypeOracle isValue, TypeNode broad, TypeNode narrow)
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning covariant external/suspend bridge: " + message); }
        var exception = new TypeNode.Fqn("System.Exception");
        var invalid = new TypeNode.Fqn("System.InvalidOperationException");
        var implementation = new BirFunctionDeclaration { Name = "GetEnumerator", Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new TypeNode[] { invalid }),
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { exception }),
                Member = "GetEnumerator", Kind = "method" } } };
        var owner = new BirClassDefinition { Kind = "class", Name = "ReferencedCovariant", Methods = new[] { implementation },
            Interfaces = new TypeNode[] { implementation.Overrides.Value[0].Owner.Value } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        var roots = new[] { file };
        var slots = ApplyAll(roots, refs, isValue, GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs), new Dictionary<BirFunctionDeclaration, TypeNode[]>());
        var descriptor = owner.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault()).ClrInterfaceImpls.Value.Single();
        Require(slots.Count == 1 && ReferenceEquals(slots.Single().Implementation, implementation)
            && descriptor.Member.Value == "GetEnumerator"
            && descriptor.Owner.Value is TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "System.Exception" }] }
            && descriptor.Ret.Value == new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new TypeNode[] { exception }),
            "referenced exact MethodDef selection or constructed physical descriptor changed");

        TypeNode Box(TypeNode value) => new TypeNode.Fqn("CovariantBox", new[] { value });
        var slot = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = Box(broad),
            Mods = new BirModifiers { Suspend = true }, SuspendRet = Box(broad), SuspendResult = new TypeNode.Nullable(broad) };
        var body = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = Box(narrow),
            Mods = new BirModifiers { Suspend = true }, SuspendRet = Box(narrow), SuspendResult = narrow,
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("SuspendContract"), Member = "read", Kind = "method" } } };
        var suspended = new BirClassDefinition { Kind = "class", Name = "SuspendOwner", Methods = new[] { body },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("SuspendContract") } };
        var suspendedFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { suspended,
            new BirInterfaceDefinition { Kind = "interface", Name = "SuspendContract", Methods = new[] { slot } } } };
        var suspendedRoots = new[] { suspendedFile };
        ApplyAll(suspendedRoots, refs, isValue, GenericRepresentationPolicy.CreateSource(suspendedRoots, refs.Aliases, refs), new Dictionary<BirFunctionDeclaration, TypeNode[]>());
        var bridge = suspended.Methods.Value.Single(method => method.PhysicalSlotBridge.OrDefault());
        var call = (BirCallInstance)((BirReturn)bridge.Body.Value.Single()).Value.Value;
        Require(bridge.Mods.Value.Suspend.Value && bridge.SuspendRet.Value == Box(broad)
            && bridge.SuspendResult.Value == new TypeNode.Nullable(broad) && call.SuspendCall.Value && call.Virtual.Value
            && !bridge.TypeParams.IsPresent && !call.TypeArgs.IsPresent,
            "suspend logical slot result, absent generic vectors or narrow virtual forwarding changed");
    }
}
