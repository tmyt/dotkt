using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ReferenceContractsSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning reference contracts: " + message); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            var variable = new TypeNode.Tv("type", 1);
            var method = new BirFunctionDeclaration { Name = "write", DeclarationId = "write-reference",
                Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.ByRef(variable) } },
                Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
            var definition = new BirClassDefinition { Name = "RefOwner", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "Hidden", Variance = "out" },
                    new BirTypeParameter { Name = "Kept", Variance = "in" } }, Methods = new[] { method } };
            var root = new BirFileDeclaration { FileClass = "ReferenceTest", Types = new IBirTypeDeclaration[] { definition } };
            var context = SourceSynthesisTestContext(root, references);
            SynthesizeSource(context.Owners["RefOwner"], context);
            var selected = context.Declarations.Slots(context.Owners["RefOwner"]).Values.Single();
            var contract = selected.ReferenceContract;
            var referenceType = (BirInterfaceDefinition)root.Types.Value[1];
            var erased = (BirInterfaceDefinition)root.Types.Value[2];
            Require(contract != null && ReferenceEquals(referenceType.Methods.Value[0], selected.Slot)
                && referenceType.TypeParams.Value.Length == 1 && !referenceType.TypeParams.Value[0].Variance.IsPresent
                && referenceType.ManagedReferenceContract.Value.Owner.Value == "RefOwner"
                && referenceType.ManagedReferenceContract.Value.Declaration.Value == "write-reference"
                && referenceType.ManagedReferenceContract.Value.Slots.Value.SequenceEqual(new[] { 1 })
                && contract.Owner.Args is [TypeNode.Tv { Scope: "type", I: 1 }]
                && selected.Slot.Params.Value.Single().Type.Value is TypeNode.ByRef { Of: TypeNode.Tv { Scope: "type", I: 0 } }
                && erased.Methods.Value.Length == 0, "pointee frame, invariant retained parameters or declaration metadata changed");
            var invoker = referenceType.Methods.Value[1];
            var call = (BirCallInstance)((BirExpressionStatement)invoker.Body.Value.Single()).Expr.Value;
            Require(invoker.Name.Value == "$ref$invoke" && invoker.Static.Value && invoker.Generated.Value
                && invoker.Params.Value[0].Type.Value == new TypeNode.Fqn("System.Object")
                && invoker.Params.Value[1].Type.Value is TypeNode.ByRef { Of: TypeNode.Tv { I: 0 } }
                && call.Recv.Value is BirCast { _exactBridgeCast.Value: true }
                && call.Args.Value.Single() is BirLocal { Name.Value: "p0" }
                && definition.Interfaces.Value.OfType<TypeNode.Fqn>().Any(type => type.Name == contract.Owner.Name),
                "reference invoker boxed/copied its managed argument or lost exact receiver dispatch");
            var receiver = new BirThis();
            var argument = new BirLocal { Name = "value" };
            var selectedSignature = new TypeNode[] { new TypeNode.ByRef(variable) };
            var pending = Array.Empty<IBirExpression>();
            var invocation = new BirCallInstance {
                OwnerType = new TypeNode.Fqn("RefOwner", new TypeNode[] { new TypeNode.Tv("type", 0), variable }),
                Recv = OptionalField<IBirExpression>.Of(receiver), Args = new IBirExpression[] { argument },
                Method = "write", DeclarationId = "write-reference", Static = false, Virtual = true, Super = false,
                Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("RefOwner"), Member = "write", Kind = "method" } },
                Ret = new TypeNode.Fqn("kotlin.Unit"), Sty = new TypeNode.Fqn("kotlin.Unit"),
                TypeArgs = Array.Empty<TypeNode>(), Sig = selectedSignature, MemberSignature = selectedSignature,
                MemberOwnerTypeParams = definition.TypeParams.Value, MemberVisibility = "public", ShapeTypes = selectedSignature,
                NullablePendingWitnesses = pending, NullableWitnessCount = 0, ClrOwnerResolved = true,
            };
            var rewritten = BindReferenceContractSource(invocation, contract,
                new TypeNode[] { new TypeNode.Tv("type", 0), variable }, false, null, null);
            method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(rewritten) } };
            Require(rewritten is BirCallStatic replacement && replacement.Owner.Value == contract.Owner
                && replacement.CalleeOwner.Value == contract.Owner && replacement.Method.Value == "$ref$invoke"
                && !replacement.DeclarationId.IsPresent && !replacement.OwnerType.IsPresent && !replacement.Virtual.IsPresent
                && !replacement.MemberOwnerTypeParams.IsPresent && !replacement.MemberVisibility.IsPresent && !replacement.ShapeTypes.IsPresent
                && ReferenceEquals(replacement.MemberSignature.Value, selectedSignature)
                && ReferenceEquals(replacement.NullablePendingWitnesses.Value, pending) && replacement.NullableWitnessCount.Value == 0
                && replacement.Sty.Value == invocation.Sty.Value
                && replacement.Args.Value is [BirThis, BirLocal { Name.Value: "value" }]
                && ReferenceEquals(replacement.Args.Value[0], receiver) && ReferenceEquals(replacement.Args.Value[1], argument)
                && replacement.Sig.Value[0] == new TypeNode.Fqn("System.Object")
                && replacement.Sig.Value[1] is TypeNode.ByRef { Of: TypeNode.Tv { I: 0 } }
                && !ReferenceEquals(replacement.Sig.Value, replacement.CalleeParams.Value),
                "reference invocation lost target selection, common facts, receiver-first order or actual managed operands");
            var projected = root.ToDom(context.Bindings.ObserveProjection);
            Require(invocation.ClrOwnerResolved.Value
                && !projected["types"][0]["methods"][0]["body"][0]["expr"].AsObject().ContainsKey("clrOwnerResolved"),
                "a consumed instance-only owner-resolution fact leaked onto the selected static invoker");
            context.Bindings.BindProjected();
            Require(context.Bindings.GetPhysical(context.Owners["RefOwner"].DefinitionBinding.Physical)
                == context.Owners["RefOwner"].DefinitionBinding, "reference synthesis broke exact original owner binding");
            context.Declarations.ReleaseSourceRows();
        }
        finally { OwnerConstrainedMethodLowering.Reset(references, _ => null); }
        Console.WriteLine("[owning managed-reference contracts] self-test OK (invariant pointee frame, actual interface slot and raw ref invoker)");
    }
}
