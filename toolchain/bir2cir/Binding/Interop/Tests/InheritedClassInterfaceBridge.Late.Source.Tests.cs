using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedClassInterfaceBridge
{
    internal static void LateSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning late inherited bridge: " + message); }
        var text = new TypeNode.Fqn("kotlin.String");
        var variable = new TypeNode.Tv("type", 0);
        (BirFileDeclaration File, BirClassDefinition Owner, BirClassDefinition Base,
            BirFunctionDeclaration Target, BirFunctionDeclaration Slot, BirInheritedClassMethod Fact) Fixture()
        {
            var target = new BirFunctionDeclaration { Name = "read", DeclarationId = "base-read",
                Params = new[] { new BirParameter { Name = "input", Type = variable } }, Ret = variable,
                TypeParams = Array.Empty<BirTypeParameter>(), Body = Array.Empty<IBirStatement>() };
            var slot = new BirFunctionDeclaration { Name = "read", DeclarationId = "interface-read", Abstract = true,
                Params = new[] { new BirParameter { Name = "value", Type = variable,
                    NullableGeneric = new TypeNode.Nullable(variable) } }, Ret = variable,
                NullableGenericRet = new TypeNode.Nullable(variable), TypeParams = Array.Empty<BirTypeParameter>() };
            var fact = new BirInheritedClassMethod { Member = "read",
                Params = new[] { new BirParameter { Name = "value", Type = text } }, Ret = text,
                InheritedImplementation = new BirInheritedImplementation { Owner = new TypeNode.Fqn("Base"), Member = "read",
                    TypeParams = Array.Empty<BirTypeParameter>() },
                Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("Contract"), Member = "read",
                    Kind = "method", DeclarationId = "interface-read" } } };
            var owner = new BirClassDefinition { Kind = "class", Name = "Derived",
                Base = new TypeNode.Fqn("Base", new TypeNode[] { text }),
                Interfaces = new TypeNode[] { new TypeNode.Fqn("Contract", new TypeNode[] { text }) },
                InheritedClassMethods = new[] { fact } };
            var baseType = new BirClassDefinition { Kind = "class", Name = "Base",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { target } };
            var file = new BirFileDeclaration { FileClass = "BridgeFixture", Types = new IBirTypeDeclaration[] { owner, baseType } };
            // A current FBound materialization can append the interface after
            // the original inventory. Selection must read the current vector.
            file.Types = file.Types.Value.Append<IBirTypeDeclaration>(new BirInterfaceDefinition { Kind = "interface", Name = "Contract",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { slot } }).ToArray();
            return (file, owner, baseType, target, slot, fact);
        }

        var fixture = Fixture();
        var originalTypes = fixture.File.Types.Value;
        var originalTargetParameters = fixture.Target.Params.Value;
        var originalFacts = fixture.Owner.InheritedClassMethods.Value;
        var calls = 0;
        BirFunctionDeclaration reported = null;
        ApplyAll(new[] { fixture.File }, (owner, appended) => {
            Require(ReferenceEquals(owner, fixture.Owner) && appended.Count == 1
                && ReferenceEquals(owner.Methods.Value.Last(), appended[0]), "producer callback did not observe the actual attachment");
            calls++;
            reported = appended[0];
        });
        var bridge = fixture.Owner.Methods.Value.Single();
        var call = (BirCallInstance)((BirReturn)bridge.Body.Value.Single()).Value.Value;
        Require(calls == 1 && ReferenceEquals(bridge, reported) && ReferenceEquals(fixture.File.Types.Value, originalTypes)
            && ReferenceEquals(fixture.Owner.InheritedClassMethods.Value, originalFacts)
            && ReferenceEquals(fixture.Target.Params.Value, originalTargetParameters)
            && !fixture.Target.Virtual.OrDefault(), "live source records or inherited target were replaced");
        Require(bridge.Virtual.Value && !bridge.Static.Value && !bridge.Override.Value && !bridge.Abstract.Value
            && bridge.Vis.Value == "public" && bridge.Generated.Value
            && bridge.DeclarationId.Value == DeclarationIdentityBinding.InheritedProjectionId("Derived", new[] { "base-read" })
            && bridge.DeclarationSourceName.Value == "read" && bridge.SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "interface-read" }),
            "physical slot identity or selected declaration association changed");
        Require(call.OwnerType.Value == new TypeNode.Fqn("Base", new TypeNode[] { text }) && !call.Virtual.Value
            && call.ClrOwnerResolved.Value && call.DeclarationId.Value == "base-read" && call.Method.Value == "read"
            && call.Sig.Value.Single() == variable && call.Ret.Value == text && call.DynRet.Value == text && call.Sty.Value == text
            && call.Args.Value.Single() is BirLocal { Name.Value: "value" } && !call.TypeArgs.IsPresent,
            "exact target call lost open signature or already-closed result");
        var descriptor = bridge.ClrInterfaceImpls.Value.Single();
        Require(bridge.TypeParams.HasValue && bridge.TypeParams.Value.Length == 0 && !descriptor.TypeParams.IsPresent
            && bridge.PhysicalSlotBridge.Value && descriptor.Owner.Value == fixture.Owner.Interfaces.Value.Single()
            && descriptor.Params.Value.Single() == text && descriptor.Ret.Value == text
            && bridge.Params.Value[0].NullableGeneric.Value == new TypeNode.Nullable(text)
            && bridge.NullableGenericRet.Value == new TypeNode.Nullable(text)
            && bridge.Overrides.Value.Single().DeclarationId.Value == "interface-read",
            "nullable semantic facts, exact descriptor or empty-vector presence changed");
        ApplyAll(new[] { fixture.File }, (_, _) => calls++);
        Require(calls == 1 && ReferenceEquals(fixture.Owner.Methods.Value.Single(), bridge), "repeated execution appended a duplicate physical slot");
        bridge.Params.Value[0].Name = "changed";
        bridge.TypeParams = new[] { new BirTypeParameter { Name = "independent" } };
        bridge.Overrides.Value[0].Member = "changed";
        Require(fixture.Slot.Params.Value[0].Name.Value == "value" && fixture.Slot.TypeParams.Value.Length == 0
            && fixture.Fact.Overrides.Value[0].Member.Value == "read", "new bridge shared mutable declaration occurrences");

        // Derived appears first. The base must nevertheless be normalized
        // before any inherited selection can see its still-nonvirtual method.
        var normalized = Fixture();
        normalized.Base.Interfaces = new TypeNode[] { new TypeNode.Fqn("Contract", new TypeNode[] { variable }) };
        var targetMethods = normalized.Base.Methods.Value;
        ApplyAll(new[] { normalized.File });
        Require(normalized.Target.Virtual.Value && normalized.Owner.Methods.Value.Length == 0
            && ReferenceEquals(normalized.Base.Methods.Value, targetMethods)
            && ReferenceEquals(normalized.Base.Methods.Value[0], normalized.Target), "module-wide normalization order changed virtual dispatch");

        var ambiguous = Fixture();
        ambiguous.Base.Methods = new[] { ambiguous.Target, (BirFunctionDeclaration)ambiguous.Target.DeepClone() };
        ApplyAll(new[] { ambiguous.File });
        Require(ambiguous.Owner.Methods.Value.Length == 0, "ambiguous selected base implementation was guessed");
        var abstractOwner = Fixture(); abstractOwner.Owner.Abstract = true;
        ApplyAll(new[] { abstractOwner.File });
        Require(abstractOwner.Owner.Methods.HasValue && abstractOwner.Owner.Methods.Value.Length == 0,
            "abstract owner synthesized a concrete slot or lost empty-vector publication");

        var unit = Fixture();
        var unitType = new TypeNode.Fqn("kotlin.Unit");
        unit.Slot.Ret = unitType; unit.Target.Ret = unitType; unit.Fact.Ret = unitType;
        unit.Slot.NullableGenericRet = default;
        ApplyAll(new[] { unit.File });
        Require(unit.Owner.Methods.Value.Single().Body.Value.Single() is BirExpressionStatement { Expr.Value: BirCallInstance },
            "Unit forwarder became a value return");

        var suspend = Fixture();
        suspend.Slot.Mods = new BirModifiers { Suspend = true };
        suspend.Target.Mods = new BirModifiers { Suspend = true };
        suspend.Slot.SuspendResult = new TypeNode.Nullable(variable);
        ApplyAll(new[] { suspend.File });
        var suspendBridge = suspend.Owner.Methods.Value.Single();
        Require(suspendBridge.Mods.Value.Suspend.Value && suspendBridge.SuspendRet.Value == text
            && suspendBridge.SuspendResult.Value == new TypeNode.Nullable(text)
            && !suspendBridge.PhysicalSlotBridge.IsPresent && !suspendBridge.ClrInterfaceImpls.IsPresent
            && ((BirCallInstance)((BirReturn)suspendBridge.Body.Value.Single()).Value.Value).SuspendCall.Value,
            "suspend logical result and physical slot obligations were conflated");

        var generic = Fixture();
        var methodVariable = new TypeNode.Tv("method", 0);
        var genericParameters = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { variable } } };
        generic.Slot.TypeParams = genericParameters;
        generic.Target.TypeParams = genericParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        generic.Fact.InheritedImplementation.Value.TypeParams = genericParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        generic.Slot.Params.Value[0].Type = methodVariable; generic.Target.Params.Value[0].Type = methodVariable;
        generic.Slot.Params.Value[0].NullableGeneric = new TypeNode.Nullable(methodVariable);
        generic.Fact.Params.Value[0].Type = methodVariable;
        generic.Slot.Ret = methodVariable; generic.Target.Ret = methodVariable; generic.Fact.Ret = methodVariable;
        generic.Slot.NullableGenericRet = new TypeNode.Nullable(methodVariable);
        ApplyAll(new[] { generic.File });
        var genericBridge = generic.Owner.Methods.Value.Single();
        var genericCall = (BirCallInstance)((BirReturn)genericBridge.Body.Value.Single()).Value.Value;
        Require(genericBridge.TypeParams.Value.Single().Constraints.Value.Single() == text
            && genericBridge.ClrInterfaceImpls.Value.Single().TypeParams.Value.Single().Constraints.Value.Single() == text
            && genericCall.TypeArgs.Value.Single() == methodVariable && genericCall.Ret.Value == methodVariable,
            "method-generic forwarder lost owner-substituted constraints or method frame");
        genericBridge.TypeParams.Value[0].Name = "changed";
        Require(generic.Slot.TypeParams.Value[0].Name.Value == "M"
            && genericBridge.ClrInterfaceImpls.Value.Single().TypeParams.Value[0].Name.Value == "M",
            "bridge type parameters shared their mutable descriptor or interface occurrence");
        Console.WriteLine("[owning late inherited slots] self-test OK (current materializations, module order, exact slots, nullable/suspend facts, generic frames, independent occurrences)");
    }
}
