using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OwnerConstrainedMethodLowering
{
    static void BodyOwnerViewSelfTest()
    {
        var previousReferences = References;
        var previousMethods = Methods.ToArray();
        var previousBindings = MethodBindings;
        var previousPrepared = PreparedMethods;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var unit = new TypeNode.Fqn("kotlin.Unit");
            var methodVariable = new TypeNode.Tv("method", 0);
            var ownerVariable = new TypeNode.Tv("type", 0);
            BirTypeParameter[] Frame() => new[] { new BirTypeParameter { Name = "M",
                Constraints = new TypeNode[] { new TypeNode.Fqn("MethodOwner", new TypeNode[] { ownerVariable }) } } };
            BirTypeParameter[] Owner(string argument) => new[] { new BirTypeParameter { Name = "T",
                Constraints = new TypeNode[] { new TypeNode.Fqn("TypeOwner", new TypeNode[] { new TypeNode.Fqn(argument) }) } } };
            BirCallInstance Call(string owner, string name, bool scalar = false) => new() {
                OwnerType = new TypeNode.Fqn(owner), Method = "read", DeclarationId = "selected-" + owner,
                Recv = new BirLocal { Name = name, ErasedValueVariableFact = scalar
                    ? OptionalField<TypeNode>.Of(name == "m" ? methodVariable : ownerVariable) : default },
                Args = Array.Empty<IBirExpression>(), Ret = unit, TypeArgs = OptionalField<TypeNode[]>.Null,
            };
            BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = expression };
            BirLambdaParameter Parameter(string name, TypeNode type, bool scalar) => new() {
                Name = name, Type = scalar ? new TypeNode.Fqn("object") : type,
                KotlinType = scalar ? OptionalField<TypeNode>.Of(type) : default,
            };
            BirNewSuspendLambda Body(bool scalar = false) => new() {
                Arity = 2,
                Params = new[] { Parameter("m", methodVariable, scalar), Parameter("t", ownerVariable, scalar) },
                SuspendRet = unit,
                Body = new IBirStatement[] {
                    Statement(Call("MethodOwner", "m", scalar)), Statement(Call("TypeOwner", "t", scalar)),
                    Statement(new BirNewSuspendLambda { Arity = 0, Params = Array.Empty<BirLambdaParameter>(), SuspendRet = unit,
                        CapValues = new IBirExpression[] { Call("MethodOwner", "m", scalar) },
                        Body = new IBirStatement[] { Statement(Call("MethodOwner", "m", scalar)) } }) },
            };
            void Expect(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException("Body owner closing: " + message);
            }
            BirCallInstance At(BirNewSuspendLambda body, int index)
                => (BirCallInstance)((BirExpressionStatement)body.Body.Value[index]).Expr.Value;
            BirNewSuspendLambda Nested(BirNewSuspendLambda body)
                => (BirNewSuspendLambda)((BirExpressionStatement)body.Body.Value[2]).Expr.Value;

            var cases = new List<(string Argument, BirNewSuspendLambda Body, BirNewSuspendLambda TypeOnly,
                BirTypeParameter[] Frame, BirTypeParameter[] Owner)>();
            foreach (var (argument, scalar) in new[] {
                ("First", false), ("Second", false), ("First", true), ("Second", true) })
            {
                var body = Body(scalar);
                cases.Add((argument, body, new BirNewSuspendLambda {
                    Arity = 2,
                    Params = body.Params.Value.Select(parameter => (BirLambdaParameter)parameter.DeepClone()).ToArray(),
                    SuspendRet = unit, Body = new IBirStatement[] { Statement(Call("TypeOwner", "t", scalar)) },
                }, Frame(), Owner(argument)));
            }
            var selectedBody = Body();
            // The construction can own a distinct frame, but the actual donor
            // declaration supplies the override-bound plan used for this view.
            selectedBody.TypeParams = new[] { new BirTypeParameter { Name = "Unrelated",
                Constraints = new TypeNode[] { new TypeNode.Fqn("MethodOwner", new TypeNode[] { new TypeNode.Fqn("BodyArgument") }) } } };
            var unchanged = new BirNewSuspendLambda { Arity = 0, Params = Array.Empty<BirLambdaParameter>(), SuspendRet = unit,
                Body = new IBirStatement[] { Statement(new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"),
                    Value = new BirLiteral.IntegerLiteral(42) }) } };
            var expressions = cases.SelectMany(row => new[] { row.Body, row.TypeOnly })
                .Concat(new[] { selectedBody, unchanged }).ToArray();
            var statements = expressions.Select(body => (IBirStatement)Statement(body)).ToArray();
            var holder = new BirFunctionDeclaration { Name = "bodyOwners", DeclarationId = "bodyOwners", Ret = unit,
                Params = Array.Empty<BirParameter>(), TypeParams = Array.Empty<BirTypeParameter>(), Body = statements };
            var root = new BirFileDeclaration { FileClass = "BodyOwners", Types = Array.Empty<IBirTypeDeclaration>(),
                Methods = new[] { holder } };
            var roots = new[] { root };
            var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
            var bindings = FBoundStarProjectionErasure.PrepareCurrentDeclarations(roots, values, out var preparation);
            var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, refs, values);
            var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, proofs, refs, values);
            var context = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
                FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs, surface.Demand.ReservedNames);

            foreach (var row in cases)
            {
                var body = row.Body;
                var originalBody = body.Body.Value;
                var originalParameters = body.Params.Value;
                var originalParameter = originalParameters[0];
                var originalCall = At(body, 0);
                var originalReceiver = originalCall.Recv.Value;
                var nested = Nested(body);
                var originalCaptureVector = nested.CapValues.Value;
                var originalNestedBody = nested.Body.Value;
                var frameBefore = row.Frame[0].DeepClone();
                var ownerBefore = row.Owner[0].DeepClone();
                CloseBodyOwnerViewsSource(body, row.Frame, row.Owner, context,
                    FBoundStarProjectionErasure.ContainsOwnerTv, type => type, _ => null);
                Expect(ReferenceEquals(originalBody, body.Body.Value)
                    && ReferenceEquals(originalParameters, body.Params.Value)
                    && ReferenceEquals(originalParameter, body.Params.Value[0])
                    && ReferenceEquals(originalCall, At(body, 0)) && ReferenceEquals(originalReceiver, At(body, 0).Recv.Value)
                    && ReferenceEquals(holder.Body.Value, statements)
                    && statements.OfType<BirExpressionStatement>().Any(statement => ReferenceEquals(statement.Expr.Value, body)),
                    "body, parameter, receiver or owning occurrence identity changed");
                Expect(row.Frame[0].StructurallyEquals(frameBefore) && row.Owner[0].StructurallyEquals(ownerBefore),
                    "projected constraints mutated the donor declarations");
                Expect(At(body, 0).OwnerType.Value == new TypeNode.Fqn("MethodOwner", new TypeNode[] { ownerVariable })
                    && At(body, 1).OwnerType.Value == new TypeNode.Fqn("TypeOwner", new TypeNode[] { new TypeNode.Fqn(row.Argument) })
                    && At(body, 0).DeclarationId.Value == "selected-MethodOwner" && At(body, 0).TypeArgs.IsNull,
                    "method/type frames, selected declaration identity or explicit type-argument null were lost");
                Expect(((BirCallInstance)nested.CapValues.Value[0]).OwnerType.Value == At(body, 0).OwnerType.Value
                    && At(nested, 0).OwnerType.Value == new TypeNode.Fqn("MethodOwner")
                    && ReferenceEquals(originalCaptureVector, nested.CapValues.Value)
                    && ReferenceEquals(originalNestedBody, nested.Body.Value),
                    "nested body used the enclosing frame, or capture operands were skipped/copied");

                CloseBodyOwnerViewsSource(row.TypeOnly, Array.Empty<BirTypeParameter>(), row.Owner, context,
                    _ => false, type => type, _ => null);
                Expect(At(row.TypeOnly, 0).OwnerType.Value == At(body, 1).OwnerType.Value,
                    "empty method frame suppressed type binding");
            }
            Expect(At(cases[0].Body, 1).OwnerType.Value != At(cases[1].Body, 1).OwnerType.Value
                && At(cases[2].Body, 1).OwnerType.Value != At(cases[3].Body, 1).OwnerType.Value,
                "sibling frames were shared");

            var donorFrame = Frame();
            var donorBefore = donorFrame[0].DeepClone();
            var selectedBound = donorFrame[0].Constraints.Value[0];
            var overrideBounds = new OwnerConstraintOverrideBounds(new[] { new OwnerConstraintOverrideBounds.Row(
                0, selectedBound, selectedBound, Array.Empty<TypeNode>(), new TypeNode[] { new TypeNode.Fqn("DeclarationArgument") }) });
            var plan = PlanConstraintRewrite(new[] { donorFrame[0].Constraints.Value }, Array.Empty<TypeNode[]>(),
                null, overrideBounds, refs, FBoundStarProjectionErasure.ContainsOwnerTv, type => type,
                _ => null, _ => Array.Empty<TypeNode>());
            var selectedFrame = new[] { new BirTypeParameter { Name = "M", Constraints = plan[0].Retained,
                ErasedInnerConstraints = plan[0].Removed } };
            var constructionFrame = selectedBody.TypeParams.Value;
            var constructionBefore = constructionFrame[0].DeepClone();
            CloseBodyOwnerViewsSource(selectedBody, selectedFrame, Owner("Outer"), context,
                FBoundStarProjectionErasure.ContainsOwnerTv, type => type, _ => null);
            Expect(At(selectedBody, 0).OwnerType.Value == new TypeNode.Fqn("MethodOwner",
                    new TypeNode[] { new TypeNode.Fqn("DeclarationArgument") })
                && donorFrame[0].StructurallyEquals(donorBefore)
                && ReferenceEquals(selectedBody.TypeParams.Value, constructionFrame)
                && constructionFrame[0].StructurallyEquals(constructionBefore),
                "construction metadata replaced the selected declaration-frame plan, or a donor was mutated");

            var unchangedBody = unchanged.Body.Value;
            var before = unchanged.DeepClone();
            CloseBodyOwnerViewsSource(unchanged, Array.Empty<BirTypeParameter>(), Array.Empty<BirTypeParameter>(),
                context, _ => false, type => type, _ => null);
            Expect(ReferenceEquals(unchangedBody, unchanged.Body.Value) && unchanged.StructurallyEquals(before),
                "no-rewrite body changed");
        }
        finally
        {
            Methods.Clear();
            Methods.AddRange(previousMethods);
            MethodBindings = previousBindings;
            PreparedMethods = previousPrepared;
            References = previousReferences;
        }
        Console.WriteLine("[body owner closing] self-test OK (owning continuation, frames, nested/capture, sibling, selected plan, no-rewrite)");
    }
}
