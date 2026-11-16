using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    internal static void SourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning declaration signatures: " + message); }
        var obj = new TypeNode.Fqn("System.Object");
        var text = new TypeNode.Fqn("System.String");
        var nothing = new TypeNode.Fqn("void");
        BirFunctionDeclaration Method(string id, TypeNode parameter, TypeNode result = null) => new() {
            Name = "keep", DeclarationId = id, Params = new[] { new BirParameter { Name = "value", Type = parameter } },
            Ret = result ?? nothing, Body = Array.Empty<IBirStatement>(),
        };
        static void ApplyPrivate(BirFileDeclaration file, SignatureAllocationPlan plan)
            => ApplyAll(new[] { file }, plan, (_, _) => { });

        TypeNode[] BoundSignature(bool referenceBuild)
        {
            var tv = new TypeNode.Tv("method", 0);
            var methods = new[] { "kotlin.Comparable", "Probe.Sink" }.Select((bound, index) => {
                var method = Method("bound-" + index, tv, tv);
                method.TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn(bound, new TypeNode[] { tv }) } } };
                return method;
            }).ToArray();
            var files = Capture(new[] { new BirFileDeclaration { FileClass = "BoundSignature", Methods = methods } });
            var current = ComparableRepresentationLowering.Apply(files, referenceBuild)[0];
            var allocation = Plan(new[] { current });
            Require(allocation.Parameters.Count == 2, "captured bounds no longer distinguish erased constraints");
            ApplyPrivate(current, allocation);
            return current.Methods.Value.Select(method => method.Params.Value[0].Type.Value).ToArray();
        }
        Require(BoundSignature(true).SequenceEqual(BoundSignature(false)), "bound signature identity differs across reference/runtime erasure");
        var methodVariable = new TypeNode.Tv("method", 0);
        var referencedMarker = new TypeNode.Fqn("Probe.ReferencedModifier", new TypeNode[] { methodVariable });
        var referencedKey = (false, referencedMarker.Name, 1);
        ReferencedModifiers.Add(referencedKey);
        try
        {
            var expected = new TypeNode.Mod(false, referencedMarker, methodVariable);
            var signature = new TypeNode.Mod(false, referencedMarker, obj);
            var qualifier = new TypeNode.Fqn("Probe.NativeQualifier");
            Require(WithIdentityModifiers(signature, methodVariable) == expected
                && WithIdentityModifiers(signature, expected) == expected
                && WithIdentityModifiers(new TypeNode.Mod(true, qualifier, signature), new TypeNode.Mod(true, qualifier, methodVariable))
                    == new TypeNode.Mod(true, qualifier, expected), "referenced identity duplicated modifiers or changed native qualifiers");
        }
        finally { ReferencedModifiers.Remove(referencedKey); }
        var ownerVariable = new TypeNode.Tv("type", 0);
        var signatureFacts = new BirParameter {
            NominalParameterSignatureIdentity = new TypeNode.Fqn("Probe.Marker", new TypeNode[] { ownerVariable, methodVariable }),
            GenericParameterSignatureBounds = new TypeNode[] { ownerVariable, methodVariable },
        };
        RewriteParameterFacts(signatureFacts, type => SupertypeGraph.SubstOwnerTvs(type, new TypeNode[] { obj }));
        Require(signatureFacts.NominalParameterSignatureIdentity.Value == new TypeNode.Fqn("Probe.Marker", new TypeNode[] { obj, methodVariable })
            && signatureFacts.GenericParameterSignatureBounds.Value.SequenceEqual(new TypeNode[] { obj, methodVariable })
            && SignatureValueTypes.Of(new TypeNode.Mod(false, referencedMarker, methodVariable)) == methodVariable
            && MethodVariables(new TypeNode.Nullable(methodVariable)).SequenceEqual(new[] { 0 })
            && MethodVariables(new TypeNode.Fqn("Probe.Box", new TypeNode[] { methodVariable })).SequenceEqual(new[] { 0 }),
            "source signature facts mixed owner and method frames or leaked into value slots");
        var function = new TypeNode.Fn(false, nothing, new TypeNode[] { methodVariable });
        var boundMarker = Marker("test-bound", Array.Empty<TypeNode>());
        var functionParameter = new BirParameter { Type = WithIdentityModifiers(new TypeNode.Mod(false, boundMarker,
            new TypeNode.Mod(false, function, obj)), obj), FunctionSignatureType = function };
        Require(FunctionSignatureIdentity.SignatureType(functionParameter)
            is TypeNode.Mod { M: TypeNode.Fn, Of: TypeNode.Mod { M: TypeNode.Fqn, Of: TypeNode.Fqn } },
            "bound projection duplicated a function signature discriminator");

        // Nominal aliases have equal value ABI but keep their independent source
        // discriminator. The actual body and operand records must not be cloned.
        var left = Method("first", new TypeNode.Fqn("Logical.Left"));
        var right = Method("second", new TypeNode.Fqn("Logical.Right"));
        var spare = Method("spare", new TypeNode.Fqn("Unused.Marker")); spare.Name = "spare";
        var constructors = new[] {
            new BirConstructor { DeclarationId = "first-ctor", Params = new[] { new BirParameter { Type = new TypeNode.Fqn("Logical.Left") } } },
            new BirConstructor { DeclarationId = "second-ctor", Params = new[] { new BirParameter { Type = new TypeNode.Fqn("Logical.Right") } } },
        };
        var captured = Capture(new[] { new BirFileDeclaration { FileClass = "AliasProbe", Methods = new[] { left, right, spare },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Kind = "class", Name = "Constructed", Ctors = constructors } } } })[0];
        left = captured.Methods.Value[0]; right = captured.Methods.Value[1];
        constructors = captured.Types.Value[0].Ctors.Value;
        var stripped = (BirFileDeclaration)captured.DeepClone(); RemoveCapturedFacts(stripped);
        Require(!stripped.Methods.Value[0].Params.Value[0].NominalParameterSignatureIdentity.IsPresent,
            "physical capture leaked into source carrier");
        foreach (var method in captured.Methods.Value.Take(2)) method.Params.Value[0].Type = obj;
        foreach (var constructor in constructors) constructor.Params.Value[0].Type = obj;
        var plan = Plan(new[] { captured });
        Require(plan.Parameters.Count == 4 && plan.Parameters.Values.All(types => types[0] is TypeNode.Mod), "nominal/constructor allocation missing");
        var receiver = new BirLocal { Name = "receiver" };
        var argument = new BirLocal { Name = "argument" };
        var call = new BirCallStatic { DeclarationId = "first", Method = "keep", Args = new IBirExpression[] { argument } };
        var instance = new BirCallInstance { DeclarationId = "first", Method = "keep", Recv = receiver };
        var constrained = new BirConstrainedCall { DeclarationId = "first", Method = "keep" };
        var openDelegate = new BirNewDelegate { DeclarationId = "first", Method = "keep" };
        var boundDelegate = new BirNewBoundDelegate { DeclarationId = "first", Method = "keep", Recv = new BirLocal { Name = "bound" } };
        var creation = new BirNew { ConstructorDeclarationId = "first-ctor", Type = new TypeNode.Fqn("Constructed") };
        left.Body = new IBirStatement[] { new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = instance },
            new BirExpressionStatement { Expr = constrained }, new BirExpressionStatement { Expr = openDelegate },
            new BirExpressionStatement { Expr = boundDelegate }, new BirExpressionStatement { Expr = creation } };
        var body = left.Body.Value;
        constructors[1].DelegationDeclarationId = "first-ctor";
        var accessor = new BirFunctionDeclaration { Name = "access", UnsafeSignatureTargetDeclarationId = "first",
            Params = new[] { new BirParameter { Type = new TypeNode.Fqn("AliasProbe") }, new BirParameter { Type = obj } } };
        captured.Methods = captured.Methods.Value.Append(accessor).ToArray();
        var removed = new List<IBirTypeDeclaration>();
        ApplyAll(new[] { captured }, plan, (actual, rows) => {
            Require(ReferenceEquals(actual, captured) && rows.All(row => actual.Types.Value.Any(value => ReferenceEquals(value, row))),
                "marker retirement was not reported before detach");
            removed.AddRange(rows);
        });
        Require(removed.Count > 0 && removed.All(row => !captured.Types.Value.Any(value => ReferenceEquals(value, row)))
            && captured.Types.Value.Any(type => Markers.ContainsKey(type.Name.Value)), "marker liveness/retirement changed");
        Require(left.DeclarationSignatureAllocation.Value && constructors[0].DeclarationSignatureAllocation.Value
            && !left.Params.Value[0].GenericParameterSignatureBounds.IsPresent
            && !left.Params.Value[0].NominalParameterSignatureIdentity.IsPresent
            && ReferenceEquals(left.Body.Value, body) && ReferenceEquals(call.Args.Value[0], argument)
            && ReferenceEquals(instance.Recv.Value, receiver), "allocation changed current graph ownership or fact lifetime");
        Require(new[] { call.CalleeParams.Value, instance.CalleeParams.Value, constrained.CalleeParams.Value,
                openDelegate.CalleeParams.Value, boundDelegate.CalleeParams.Value, accessor.Params.Value.Skip(1).Select(parameter => parameter.Type.Value).ToArray() }
                .All(types => types[0] == left.Params.Value[0].Type.Value)
            && creation.MemberSignature.Value[0] == constructors[0].Params.Value[0].Type.Value
            && constructors[1].DelegationSig.Value[0] == constructors[0].Params.Value[0].Type.Value
            && !accessor.UnsafeSignatureTargetDeclarationId.IsPresent,
            "call/delegate/unsafe/constructor selected signature handoff changed");
        Require(SignatureValueTypes.Of(left.Params.Value[0].Type.Value) == obj
            && !ReferenceEquals(call.CalleeParams.Value, instance.CalleeParams.Value)
            && !ReferenceEquals(call.CalleeParams.Value, plan.Parameters["first"]), "signature snapshots alias current vectors or change value ABI");

        // Allocated local override families close the owner's frame exactly
        // once, while keeping method variables open and avoiding redundant bodies.
        {
            BirFunctionDeclaration GenericMethod(string id, string nominal, TypeNode argument)
            {
                var method = Method(id, new TypeNode.Fqn(nominal, new[] { argument }), methodVariable);
                method.Virtual = true; method.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
                return method;
            }
            var slots = new[] { GenericMethod("slot-left", "Logical.Left", ownerVariable), GenericMethod("slot-right", "Logical.Right", ownerVariable) };
            foreach (var slot in slots) slot.Abstract = true;
            var implementations = new[] { GenericMethod("body-left", "Logical.Left", obj), GenericMethod("body-right", "Logical.Right", obj) };
            for (var index = 0; index < implementations.Length; index++)
                implementations[index].Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("FrameSlot", new TypeNode[] { obj }),
                    Member = "keep", Kind = "method", DeclarationId = slots[index].DeclarationId } };
            var file = Capture(new[] { new BirFileDeclaration { FileClass = "Framed", Types = new IBirTypeDeclaration[] {
                new BirInterfaceDefinition { Kind = "interface", Name = "FrameSlot", TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = slots },
                new BirClassDefinition { Kind = "class", Name = "FrameBody", Interfaces = new TypeNode[] { new TypeNode.Fqn("FrameSlot", new TypeNode[] { obj }) },
                    Methods = implementations },
            } } })[0];
            var contract = file.Types.Value[0]; var owner = file.Types.Value[1];
            foreach (var slot in contract.Methods.Value) slot.Params.Value[0].Type = ownerVariable;
            foreach (var implementation in owner.Methods.Value) implementation.Params.Value[0].Type = new TypeNode.Fqn("object");
            var allocation = Plan(new[] { file });
            Require(allocation.Parameters.Count == 4
                && allocation.Parameters["body-left"][0] == SupertypeGraph.SubstOwnerTvs(allocation.Parameters["slot-left"][0], new TypeNode[] { obj }),
                "allocated override did not inherit the selected closed signature");
            ApplyPrivate(file, allocation);
            BindSelectedOverridesSource(new[] { file }, allocation, null, _ => false,
                (_, _, _) => throw new InvalidOperationException("Physical object aliases produced a redundant signature bridge"), _ => { }, _ => { });
            Require(owner.Methods.Value.Length == 2 && owner.Methods.Value.All(method => {
                var descriptor = method.ClrInterfaceImpls.Value.Single();
                return descriptor.Owner.Value == new TypeNode.Fqn("FrameSlot", new TypeNode[] { obj })
                    && descriptor.Arity.Value == 1 && descriptor.Ret.Value == methodVariable;
            }), "selected local descriptor lost its closed owner or open method frame");
        }

        // A body-only allocation must repair each already-selected relation,
        // including the native descriptor snapshots recorded by the final owner.
        foreach (var includeNative in new[] { false, true })
        {
            var slot = Method("implicit-slot", obj, text); slot.Virtual = true; slot.Abstract = true;
            var first = Method("implicit-left", new TypeNode.Fqn("Logical.Left"), text); first.Virtual = true;
            var second = Method("implicit-right", new TypeNode.Fqn("Logical.Right"), text); second.Virtual = true;
            var contract = new BirInterfaceDefinition { Kind = "interface", Name = "ImplicitSlot", Methods = new[] { slot } };
            var owner = new BirClassDefinition { Kind = "class", Name = "ImplicitBody", Interfaces = new TypeNode[] { new TypeNode.Fqn("ImplicitSlot") },
                Methods = new[] { first, second } };
            var file = Capture(new[] { new BirFileDeclaration { FileClass = "Implicit", Types = new IBirTypeDeclaration[] { contract, owner } } })[0];
            contract = (BirInterfaceDefinition)file.Types.Value[0]; owner = (BirClassDefinition)file.Types.Value[1];
            first = owner.Methods.Value[0]; second = owner.Methods.Value[1];
            RecordSelectedImplementationSlot(first, contract.Methods.Value[0]);
            var previous = new List<BirMethodImpl>();
            if (includeNative)
                foreach (var isInterface in new[] { false, true })
                {
                    var selected = new BirMethodImpl { Owner = new TypeNode.Fqn(isInterface ? "NativeSlot" : "NativeBase"),
                        Member = "keep", Arity = 0, Params = new TypeNode[] { obj }, Ret = text,
                        TypeParams = OptionalField<BirTypeParameter[]>.Null, DotktValueReturn = false };
                    RecordSelectedPhysicalSlot(first, selected, isInterface);
                    if (isInterface) first.ClrInterfaceImpls = new[] { selected }; else first.ClrBaseImpls = new[] { selected };
                    previous.Add(selected);
                    Require(DescriptorKey(selected) == selected.ToDom().ToJsonString(), "selected metadata leaf hash bytes changed");
                }
            first.Params.Value[0].Type = obj; second.Params.Value[0].Type = obj;
            var selectedPlan = Plan(new[] { file });
            Require(selectedPlan.Parameters.Count == 2 && !selectedPlan.Parameters.ContainsKey("implicit-slot"), "body-only collision setup changed");
            ApplyPrivate(file, selectedPlan);
            var appended = new List<BirFunctionDeclaration>(); var admitted = new List<BirMethodImpl>(); var retired = new List<BirMethodImpl>();
            BindSelectedOverridesSource(new[] { file }, selectedPlan, null, _ => false,
                (actualFile, actualOwner, rows) => {
                    Require(ReferenceEquals(actualFile, file) && ReferenceEquals(actualOwner, owner)
                        && rows.All(row => owner.Methods.Value.Any(value => ReferenceEquals(value, row))), "bridge publication preceded attachment");
                    appended.AddRange(rows);
                }, descriptor => {
                    Require(owner.Methods.Value.Any(method => method.ClrInterfaceImpls.OrEmpty().Concat(method.ClrBaseImpls.OrEmpty())
                        .Any(value => ReferenceEquals(value, descriptor))), "descriptor admission preceded attachment");
                    admitted.Add(descriptor);
                }, descriptor => {
                    Require(first.ClrInterfaceImpls.OrEmpty().Concat(first.ClrBaseImpls.OrEmpty()).Any(value => ReferenceEquals(value, descriptor)),
                        "descriptor retirement followed detach"); retired.Add(descriptor);
                });
            Require(owner.Methods.Value.Length == (includeNative ? 5 : 3) && appended.Count == (includeNative ? 3 : 1)
                && admitted.Count == appended.Count && retired.Count == previous.Count
                && previous.All(row => retired.Any(value => ReferenceEquals(row, value)))
                && !first.SignatureOverrideDeclarations.IsPresent && first.Virtual.Value,
                "selected native/local slot materialization or exact retirement changed");
            Require(owner.Methods.Value[2].Params.Value[0].Type.Value == obj
                && owner.Methods.Value[2].ClrInterfaceImpls.Value.Single().Member.Value == "keep"
                && owner.Methods.Value[2].DeclarationId.IsPresent == false,
                "implicit signature bridge changed output signature or invented source identity");
            if (includeNative)
                Require(first.ClrInterfaceImpls.Value.Length == 0 && first.ClrBaseImpls.Value.Length == 0
                    && admitted.Skip(1).All(row => row.TypeParams.IsNull && row.DotktValueReturn.IsPresent && !row.DotktValueReturn.Value),
                    "native descriptor optional presence was lost");
        }
        Console.Error.WriteLine("[source declaration signatures] self-test OK (nominal allocation, constructor/call handoffs, exact slot births/retirement)");
    }
}
