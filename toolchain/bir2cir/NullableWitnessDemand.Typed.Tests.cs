using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class NullableWitnessDemand
{
    static void TypedDefaultDemandSelfTest()
    {
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        var variable = new TypeNode.Tv("method", 0);
        BirTypeParameter Parameter(string name) => new() { Name = name };
        BirConstant False() => new() { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) };
        var function = new TypeNode.Fn(false, boolean, Array.Empty<TypeNode>());
        var lifted = new BirFunctionDeclaration {
            Name = "defaultTarget", DeclarationId = "default-target", Generated = true, Static = true,
            TypeParams = new[] { Parameter("T") }, Params = Array.Empty<BirParameter>(), Ret = boolean,
            Body = new IBirStatement[] { new BirReturn { Value = new BirIsInstance { Type = variable,
                E = new BirConstant { Type = new TypeNode.Fqn("kotlin.Any"), Value = new BirLiteral.NullLiteral() } } } },
        };
        var carrier = new BirDefaultCarrier {
            Expr = new BirNewDelegate { Method = "defaultTarget", DeclarationId = "default-target", FuncType = function,
                TypeArgs = new TypeNode[] { variable }, CalleeOwner = new TypeNode.Fqn("Producer") },
            Lifted = new[] { lifted },
        };
        var target = new BirFunctionDeclaration {
            Name = "inlineDefault", DeclarationId = "inline-default", Static = true, Mods = new BirModifiers { Inline = true },
            TypeParams = new[] { Parameter("T") }, Ret = boolean, Body = Array.Empty<IBirStatement>(),
            Params = new[] { new BirParameter { Name = "action", Type = boolean },
                new BirParameter { Name = "matches", Type = function, Attrs = new[] { DefaultBirCarrier.Create(1, carrier) } } },
        };
        BirCallInline Call(bool omit) => new() {
            Owner = new TypeNode.Fqn("DemandProbe"), Callee = new TypeNode.Fqn("DemandProbe.inlineDefault"),
            DeclarationId = "inline-default", Pc = 2, Ga = 1, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) },
            Args = new IBirExpression[] { False(), omit ? null : new BirNewDelegate {
                Method = "provided", DeclarationId = "provided", CalleeOwner = new TypeNode.Fqn("DemandProbe"),
                FuncType = function, TypeArgs = Array.Empty<TypeNode>(),
            } }, RetType = boolean,
        };
        var omitted = Call(true);
        var explicitCall = Call(false);
        BirFunctionDeclaration Caller(string name, BirCallInline call) => new() {
            Name = name, DeclarationId = name, Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { Parameter("A"), Parameter("B") }, Ret = boolean,
            Body = new IBirStatement[] { new BirReturn { Value = call } },
        };
        var root = new BirFileDeclaration { FileClass = "DemandProbe", Methods = new[] {
            target, Caller("omitted", omitted), Caller("explicit", explicitCall), new BirFunctionDeclaration {
                Name = "provided", DeclarationId = "provided", Static = true, Params = Array.Empty<BirParameter>(), Ret = boolean,
                Body = new IBirStatement[] { new BirReturn { Value = False() } },
            },
        } };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var demands = Collect(new[] { root }, refs).LocalDeclarations;
        if (!demands["omitted"].SequenceEqual(new[] { 1 }) || demands["explicit"].Length != 0
            || demands["inline-default"].Length != 0 || omitted.Args.Value[1] != null
            || lifted.Name.Value != "defaultTarget" || lifted.DeclarationId.Value != "default-target")
            throw new InvalidOperationException("Owning default demand lost omission specificity, hoisted target mapping, or source independence");
        var index = new TypedInlineBirIndex(new[] { root });
        var first = DefaultArgSplice.CreateDemandView(root, refs, index);
        var second = DefaultArgSplice.CreateDemandView(root, refs, index);
        var firstTarget = first.Methods.Value.Last();
        var secondTarget = second.Methods.Value.Last();
        if (ReferenceEquals(firstTarget, secondTarget) || firstTarget.Name.Value == secondTarget.Name.Value
            || firstTarget.DeclarationId.Value == secondTarget.DeclarationId.Value)
            throw new InvalidOperationException("Independent demand views share a lifted declaration identity");
        var firstCall = (BirCallInline)((BirReturn)first.Methods.Value[1].Body.Value[0]).Value.Value;
        if (firstCall.Args.Value[1] is not BirNewDelegate value || value.Method.Value != firstTarget.Name.Value
            || value.DeclarationId.Value != firstTarget.DeclarationId.Value
            || value.TypeArgs.Value[0] != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Owning default demand detached the lifted declaration/use or its caller frame");
        Console.WriteLine("[owning default witness demand] self-test OK (omitted/explicit, nested fixed point, lifted identity, caller frame, independent views)");
    }
}
