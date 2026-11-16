using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
        TypedPlannedDefaultDemandSelfTest(carrier, function, boolean);
        Console.WriteLine("[owning default witness demand] self-test OK (omitted/explicit, nested fixed point, lifted identity, caller frame, independent views)");
    }

    static void TypedPlannedDefaultDemandSelfTest(BirDefaultCarrier innerCarrier, TypeNode function, TypeNode boolean)
    {
        BirConstant False() => new() { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) };
        BirBindingReference Read(string id, TypeNode type) => new() { Id = id, Sty = type };
        BirCallBinding Omission(string id, TypeNode type) => new() {
            Id = id, Phase = "default", Type = type, Expr = new BirDefaultArgument(), Stable = false,
        };
        BirCallEvaluation HelperPlan(TypeNode argument) => new() {
            Type = boolean, SemanticOwner = "DemandPlans",
            Bindings = new[] { Omission("callback", function) },
            Expr = new BirCallStatic {
                OwnerType = new TypeNode.Fqn("external.Defaults"), Method = "invoke", DeclarationId = "external-inner",
                Sig = new[] { function }, TypeArgs = new[] { argument }, Ret = boolean,
                Args = new IBirExpression[] { Read("callback", function) },
            },
        };
        var wrapper = new BirFunctionDeclaration {
            Name = "wrapper", DeclarationId = "wrapper", Generated = true, Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = Array.Empty<BirParameter>(), Ret = boolean,
            Body = new IBirStatement[] { new BirReturn { Value = HelperPlan(new TypeNode.Tv("method", 0)) } },
        };
        var outerCarrier = new BirDefaultCarrier {
            Expr = new BirNewDelegate {
                Method = "wrapper", DeclarationId = "wrapper", FuncType = function,
                TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) }, CalleeOwner = new TypeNode.Fqn("external.Defaults"),
            }, Lifted = new[] { wrapper },
        };
        var enumType = new TypeNode.Fqn("external.Choice");
        var ownerDefault = new BirIsInstance {
            Type = new TypeNode.Tv("type", 0), E = new BirDefaultParameter { Idx = 0 },
        };
        var metadata = new ReferenceDotKtMetadata();
        metadata.KotlinDefaultsByDeclarationId["external-static"] = new Dictionary<int, IBirExpression> {
            [1] = ownerDefault,
            [2] = new BirEnumValue { Type = enumType, Entry = "One", PhysicalValue = "1" },
            [3] = new BirEnumValue { Type = enumType, Entry = "One", Underlying = "System.Int32", PhysicalValue = "1" },
        };
        metadata.KotlinDefaultsByDeclarationId["external-inner"] = new Dictionary<int, IBirExpression> { [0] = innerCarrier };
        metadata.KotlinDefaultsByDeclarationId["external-outer"] = new Dictionary<int, IBirExpression> { [0] = outerCarrier };
        metadata.KotlinDefaultsByDeclarationId["external-constant"] = new Dictionary<int, IBirExpression> { [0] = False() };
        metadata.KotlinDefaults[new("external.Base", ReferenceMetadataIndex.CtorKeyName, 2)] =
            new Dictionary<int, IBirExpression> { [1] = ownerDefault };
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var callerVariable = new TypeNode.Tv("method", 1);
        var planned = new BirCallEvaluation {
            Type = boolean, SemanticOwner = "DemandPlans",
            Bindings = new[] {
                new BirCallBinding { Id = "given", Phase = "explicit", Type = boolean, Expr = False(), Stable = true },
                Omission("test", boolean), Omission("semanticEnum", enumType), Omission("physicalEnum", enumType),
            },
            Expr = new BirCallStatic {
                OwnerType = new TypeNode.Fqn("external.Generic", new TypeNode[] { callerVariable }),
                Method = "check", DeclarationId = "external-static", Ret = boolean,
                Sig = new[] { boolean, boolean, enumType, enumType },
                Args = new IBirExpression[] { Read("given", boolean), Read("test", boolean), Read("semanticEnum", enumType), Read("physicalEnum", enumType) },
            },
        };
        var draining = HelperPlan(callerVariable);
        ((BirCallStatic)draining.Expr.Value).DeclarationId = "external-outer";
        BirFunctionDeclaration Caller(string name, IBirExpression expression) => new() {
            Name = name, DeclarationId = name, Params = Array.Empty<BirParameter>(), Ret = boolean,
            TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } },
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(expression) } },
        };
        var delegation = new BirConstructor {
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>(),
            BaseArgs = new IBirExpression[] { Read("first", boolean), Read("second", boolean) },
            DelegationBindings = new[] {
                new BirCallBinding { Id = "first", Phase = "explicit", Type = boolean, Expr = False(), Stable = true },
                Omission("second", boolean),
            },
        };
        var source = new BirFileDeclaration {
            FileClass = "DemandPlans", Methods = new[] {
                Caller("planned", planned), Caller("draining", draining),
                Caller("trailing", new BirCallStatic {
                    OwnerType = new TypeNode.Fqn("external.Defaults"), Method = "constant", DeclarationId = "external-constant",
                    Sig = new[] { boolean }, Args = Array.Empty<IBirExpression>(), Ret = boolean,
                }),
            },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition {
                Name = "DemandDerived", Base = new TypeNode.Fqn("external.Base", new TypeNode[] { new TypeNode.Tv("type", 1) }),
                TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } }, Ctors = new[] { delegation },
            } },
        };
        var demands = Collect(new[] { source }, refs).LocalDeclarations;
        if (!demands["planned"].SequenceEqual(new[] { 1 }) || !demands["draining"].SequenceEqual(new[] { 1 })
            || demands["trailing"].Length != 0)
            throw new InvalidOperationException("Default demand lost static owner closure or recursively drained helper demand");
        var view = DefaultArgSplice.CreateDemandView(source, refs, new TypedInlineBirIndex(new[] { source }));
        var plan = (BirCallEvaluation)((BirReturn)view.Methods.Value[0].Body.Value[0]).Value.Value;
        if (plan.Bindings.Value[1].Expr.Value is not BirIsInstance {
                Type.Value: TypeNode.Tv { Scope: "method", I: 1 }, E.Value: BirBindingReference { Id.Value: "given" } }
            || plan.Bindings.Value[2].Stable.OrDefault() || !plan.Bindings.Value[3].Stable.OrDefault())
            throw new InvalidOperationException("Planned default lost an evaluated argument read, owner frame, or complete physical-enum stability");
        var constructor = ((BirClassDefinition)view.Types.Value[0]).Ctors.Value[0];
        if (constructor.DelegationBindings.Value[1].Expr.Value is not BirIsInstance {
                Type.Value: TypeNode.Tv { Scope: "type", I: 1 }, E.Value: BirBindingReference { Id.Value: "first" } }
            || ((BirCallStatic)((BirReturn)view.Methods.Value[2].Body.Value[0]).Value.Value).Args.Value is not [BirConstant])
            throw new InvalidOperationException("Default demand failed constructor delegation or a trailing metadata constant");
        if (view.Methods.Value.Length != 5 || planned.Bindings.Value[1].Expr.Value is not BirDefaultArgument
            || delegation.DelegationBindings.Value[1].Expr.Value is not BirDefaultArgument
            || !refs.KotlinDefaultValuesForDeclarationIdentity("external-static")[1].Equals(ownerDefault)
            || wrapper.Name.Value != "wrapper")
            throw new InvalidOperationException("Demand helper drain changed source or metadata ownership");
        foreach (var helper in view.Methods.Value.Skip(3))
        {
            var unresolved = false;
            helper.Visit(node => { if (node is BirDefaultArgument) unresolved = true; });
            if (unresolved) throw new InvalidOperationException("A recursively hoisted helper retained its omitted default");
        }
    }
}
