using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ConstructionSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning carrier constructions: " + message); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var text = new TypeNode.Fqn("kotlin.String");
        BirFunctionDeclaration Nested(string name) => new() { Name = name, DeclarationId = name,
            Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>() };
        BirValueBlock Operand(BirFunctionDeclaration nested) => new() { Type = text,
            Stmts = new IBirStatement[] { new BirLocalFunction { Id = nested.Name.Value, Decl = nested } },
            Result = OptionalField<IBirExpression>.Of(new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("value") }) };
        try
        {
            var t = new TypeNode.Tv("type", 0);
            var outer = new BirClassDefinition { Name = "ConstructOuter", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var outerApplication = new TypeNode.Fqn("ConstructOuter", new TypeNode[] { new TypeNode.Star() });
            var constructor = new BirConstructor { DeclarationId = "construct-inner", Params = new[] {
                new BirParameter { Name = "outer", Outer = true, Type = new TypeNode.Fqn("ConstructOuter", new TypeNode[] { t }) },
                new BirParameter { Name = "value", Type = new TypeNode.Tv("type", 1) },
            }, Body = Array.Empty<IBirStatement>() };
            var inner = new BirClassDefinition { Name = "ConstructOuter.Inner", Kind = "class", Vis = "public",
                SemanticOwner = "ConstructOuter", Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
                TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "R" } },
                Ctors = new[] { constructor }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var plain = new BirClassDefinition { Name = "ConstructionPlain", Kind = "class", Methods = Array.Empty<BirFunctionDeclaration>(),
                Ctors = new[] { new BirConstructor { DeclarationId = "plain-constructor", Params = new[] {
                    new BirParameter { Name = "outerValue", Type = outerApplication }, new BirParameter { Name = "value", Type = text },
                }, Body = Array.Empty<IBirStatement>() } } };
            var nested = Nested("innerOperandLocal");
            var operand = Operand(nested);
            var receiver = new BirLocal { Name = "outer", Sty = outerApplication };
            var signature = constructor.Params.Value.Select(parameter => BirGraphValue.CopyType(parameter.Type.Value)).ToArray();
            var construction = new BirNew {
                Type = new TypeNode.Fqn(inner.Name.Value, new TypeNode[] { new TypeNode.Star(), text }),
                ConstructorDeclarationId = constructor.DeclarationId, MemberSignature = signature,
                ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.Object"), text },
                Args = new IBirExpression[] { receiver, operand },
            };
            var caller = new BirFunctionDeclaration { Name = "construct", DeclarationId = "construct", Ret = unit,
                Params = new[] { new BirParameter { Name = "outer", Type = outerApplication } },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(construction) } } };
            var root = new BirFileDeclaration { FileClass = "ConstructionCalls", Types = new IBirTypeDeclaration[] { outer, inner, plain },
                Methods = new[] { caller } };
            var context = SourceSynthesisTestContext(root, references);
            var nestedBinding = context.Bindings.GetCapturedSource(nested);
            SynthesizeSource(context.Owners[outer.Name.Value], context);
            var state = new SourceRewriteState(context, (_, _) => null);
            var replacement = BindStarInnerConstructionSource(construction, null, null, state);
            ((BirExpressionStatement)caller.Body.Value[0]).Expr = OptionalField<IBirExpression>.Of((IBirExpression)replacement);
            var factory = context.Declarations.InnerFactories(context.Owners[outer.Name.Value]).Values.Single();
            Require(replacement is BirCallInstance call && call.Method.Value == factory.Slot.Name.Value
                && call.OwnerType.Value == new TypeNode.Fqn(context.Owners[outer.Name.Value].ErasedName)
                && ReferenceEquals(call.Recv.Value, receiver) && ReferenceEquals(call.Args.Value.Single(), operand)
                && call.TypeArgs.Value is [TypeNode.Fqn { Name: "kotlin.String" }] && call._innerFactoryArguments.Value
                && call.Sig.Value.Single() is TypeNode.Tv { Scope: "method", I: 0 }
                && call.CalleeParams.Value.SequenceEqual(factory.Signature)
                && ReferenceEquals(((BirLocalFunction)operand.Stmts.Value[0]).Decl.Value, nested),
                "inner factory lost selected binding/signature/frame, actual receiver or nested operand declaration");
            var noSignature = new BirNew { Type = construction.Type, ConstructorDeclarationId = constructor.DeclarationId,
                Args = new IBirExpression[] { new BirLocal { Name = "outer", Sty = outerApplication }, new BirConstant {
                    Type = text, Value = new BirLiteral.TextLiteral("value") } },
                MemberSignature = Array.Empty<TypeNode>(), ArgTypes = signature };
            Require(ReferenceEquals(BindStarInnerConstructionSource(noSignature, null, null, state), noSignature),
                "an explicitly empty declaration signature fell back to argument types");
            var ordinary = new BirNew { Type = new TypeNode.Fqn(plain.Name.Value), ConstructorDeclarationId = "plain-constructor",
                MemberSignature = new TypeNode[] { outerApplication, text },
                Args = new IBirExpression[] { new BirLocal { Name = "outer", Sty = outerApplication }, new BirConstant {
                    Type = text, Value = new BirLiteral.TextLiteral("value") } } };
            Require(ReferenceEquals(BindStarInnerConstructionSource(ordinary, null, null, state), ordinary),
                "a first ordinary source argument was treated as a hidden inner receiver");
            _ = root.ToDom(context.Bindings.ObserveProjection);
            context.Bindings.BindProjected();
            Require(context.Bindings.GetPhysical(nestedBinding.Physical) == nestedBinding,
                "inner construction replaced a registered nested declaration instead of moving its operand");
            context.Declarations.ReleaseSourceRows();
        }
        finally { OwnerConstrainedMethodLowering.Reset(references, _ => null); }

        try
        {
            var t = new TypeNode.Tv("type", 0);
            var input = new BirClassDefinition { Name = "ConstructionInput", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var holder = new BirClassDefinition { Name = "ConstructionHolder", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>(),
                Ctors = new[] { new BirConstructor { DeclarationId = "holder-constructor", Params = new[] {
                    new BirParameter { Name = "value", Type = new TypeNode.Fqn(input.Name.Value, new TypeNode[] { t }) },
                }, Body = Array.Empty<IBirStatement>() } } };
            var nested = Nested("projectedOperandLocal");
            var projectedInput = new TypeNode.Fqn(input.Name.Value, new TypeNode[] { new TypeNode.Star() });
            var operand = Operand(nested);
            operand.Type = projectedInput;
            operand.Result = OptionalField<IBirExpression>.Of(new BirLocal { Name = "input", Sty = projectedInput });
            var construction = new BirNew { Type = new TypeNode.Fqn(holder.Name.Value, new TypeNode[] { new TypeNode.Star() }),
                ConstructorDeclarationId = "holder-constructor",
                MemberSignature = new TypeNode[] { new TypeNode.Fqn(input.Name.Value, new TypeNode[] { t }) },
                Args = new IBirExpression[] { operand } };
            var caller = new BirFunctionDeclaration { Name = "captureConstruction", DeclarationId = "capture-construction", Ret = unit,
                Params = new[] { new BirParameter { Name = "input", Type = projectedInput } },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(construction) } } };
            var root = new BirFileDeclaration { FileClass = "ProjectedConstructionCalls", Types = new IBirTypeDeclaration[] { input, holder },
                Methods = new[] { caller } };
            var context = SourceSynthesisTestContext(root, references);
            var nestedBinding = context.Bindings.GetCapturedSource(nested);
            foreach (var owner in context.Owners.Values.Where(owner => owner.Needed)) SynthesizeSource(owner, context);
            var state = new SourceRewriteState(context, (_, _) => null);
            var replacement = LowerProjectedConstructionSource(construction, state);
            ((BirExpressionStatement)caller.Body.Value[0]).Expr = OptionalField<IBirExpression>.Of((IBirExpression)replacement);
            var invocation = replacement is BirCast cast ? (BirCallStatic)cast.E.Value : (BirCallStatic)replacement;
            Require(invocation.Owner.Value == new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt")
                && invocation.Method.Value == "starProjectionConstruct" && invocation.Args.Value.Length == 4
                && invocation.Args.Value[1] is BirNewArray { Elems.Value: [BirConstant { Value.Value: BirLiteral.TextLiteral }] }
                && invocation.Args.Value[2] is BirNewArray { Elems.Value: [BirClassReference] }
                && invocation.Args.Value[3] is BirNewArray runtime && ReferenceEquals(runtime.Elems.Value.Single(), operand)
                && ReferenceEquals(((BirLocalFunction)operand.Stmts.Value[0]).Decl.Value, nested)
                && ForeignStarProjectionBinding.UsedRuntimeFallback,
                "projected construction lost structural descriptor, fallback types or actual runtime operands");
            _ = root.ToDom(context.Bindings.ObserveProjection);
            context.Bindings.BindProjected();
            Require(context.Bindings.GetPhysical(nestedBinding.Physical) == nestedBinding,
                "reflection construction cloned a registered nested declaration");
            context.Declarations.ReleaseSourceRows();
        }
        finally { OwnerConstrainedMethodLowering.Reset(references, _ => null); }
        Console.WriteLine("[owning carrier constructions] self-test OK (selected factory frames, signature presence and registered operand moves across one frontier)");
    }
}
