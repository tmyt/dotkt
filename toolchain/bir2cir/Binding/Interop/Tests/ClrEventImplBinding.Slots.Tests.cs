using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventImplBinding
{
    internal static void SelfTestTypedEventSlots()
    {
        TypeNode T(string name) => new TypeNode.Fqn(name);
        var integer = T("kotlin.Int");
        var text = T("kotlin.String");
        var variable = new TypeNode.Tv("type", 1);
        TypeNode Delegate(TypeNode element) => new TypeNode.Fqn("System.Action", new[] { Copy(element) });
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        BirLocal Local(string name) => new() { Name = name };
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Typed event slot binding: " + message);
        }
        BirFunctionDeclaration Accessor(string kind, TypeNode element, string visibility = "public", bool isStatic = false) => new() {
            Name = kind + "_Changed", SpecialName = true, Vis = visibility, Static = isStatic,
            Params = new[] { new BirParameter { Name = "handler", Type = Delegate(element),
                FunctionSignatureType = new TypeNode.Fn(false, T("kotlin.Unit"), new[] { Copy(element) }) } },
            Ret = T("kotlin.Unit"), Body = Array.Empty<IBirStatement>(),
        };
        var baseAdd = Accessor("add", variable);
        var baseRemove = Accessor("remove", variable, "protected");
        var raiseDeclaration = new BirFunctionDeclaration { Name = "raisePhysical", DeclarationId = "exact-raise", SpecialName = true,
            Params = new[] { new BirParameter { Name = "value", Type = variable } }, Ret = T("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
        var baseEvent = new BirClrEventBacking { K = "clrEventDecl", Name = "Changed", DelegateType = Delegate(variable), EventRaiseDeclarationId = "exact-raise" };
        var baseOwner = new BirClassDefinition { Name = "Base", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } }, TypeParams = new[] { new BirTypeParameter { Name = "Element" } },
            Methods = new[] { baseAdd, baseRemove, raiseDeclaration }, ClrEvents = new[] { baseEvent } };
        var other = new BirInterfaceDefinition { Name = "Other", Kind = "interface",
            Methods = new[] { Accessor("add", text), Accessor("remove", text) },
            ClrEvents = new[] { new BirClrEventBacking { K = "clrEventDecl", Name = "Changed", DelegateType = Delegate(text) } } };
        var staticAdd = Accessor("add", text, isStatic: true);
        var staticRemove = Accessor("remove", text, "private", isStatic: true);
        var staticRaise = new BirFunctionDeclaration { Name = "raiseStatic", DeclarationId = "exact-static-raise", Static = true,
            Params = new[] { new BirParameter { Name = "value", Type = text } }, Ret = T("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
        var staticOwner = new BirClassDefinition { Name = "StaticOwner", Kind = "class", Methods = new[] { staticAdd, staticRemove, staticRaise },
            ClrEvents = new[] { new BirClrEventBacking { K = "clrEventDecl", Name = "Changed", DelegateType = Delegate(text), Static = true,
                EventRaiseDeclarationId = "exact-static-raise" } } };
        var derived = new BirClassDefinition { Name = "Derived", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = new TypeNode.Fqn("Base", new[] { text, (TypeNode)new TypeNode.Tv("type", 0) }) };
        var closedBase = new TypeNode.Fqn("Base", new[] { text, integer });
        var receiver = Local("receiver");
        var handler = Local("handler");
        var inherited = new BirEventAdd { Type = new TypeNode.Fqn("Derived", new[] { integer }), Event = "Changed", Recv = receiver, Handler = handler };
        var constrained = new BirEventRemove { Type = new TypeNode.Tv("method", 0), Event = "Changed", Recv = Local("bounded"), Handler = Local("handler") };
        var staticSubscription = new BirEventRemove { Type = T("StaticOwner"), Event = "Changed", Static = true, Handler = Local("stringHandler") };
        var externalHandler = Local("external");
        var external = new BirEventAdd { Type = T("System.ComponentModel.INotifyPropertyChanged"), Event = "PropertyChanged",
            Recv = Local("externalReceiver"), Handler = externalHandler };
        var constrainedStatement = Statement(constrained);
        var staticStatement = Statement(staticSubscription);
        var caller = new BirFunctionDeclaration { Name = "caller", Params = new[] {
            new BirParameter { Name = "receiver", Type = new TypeNode.Fqn("Derived", new[] { integer }) },
            new BirParameter { Name = "bounded", Type = new TypeNode.Tv("method", 0) },
            new BirParameter { Name = "handler", Type = Delegate(integer) },
            new BirParameter { Name = "stringHandler", Type = Delegate(text) },
            new BirParameter { Name = "external", Type = T("System.ComponentModel.PropertyChangedEventHandler") },
            new BirParameter { Name = "externalReceiver", Type = T("System.ComponentModel.INotifyPropertyChanged") },
        }, TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new[] { closedBase } } },
            Ret = T("kotlin.Unit"),
            Body = new IBirStatement[] { Statement(inherited), constrainedStatement, staticStatement, Statement(external) } };

        // Equal method records in different closure TypeDefs must retain their
        // actual enclosing frames, including a captured-parameter prefix.
        var firstSubscription = new BirEventAdd { Type = new TypeNode.Tv("type", 0), Event = "Changed", Recv = Local("receiver"), Handler = Local("handler") };
        var secondSubscription = (BirEventAdd)firstSubscription.DeepClone();
        BirClassDefinition Closure(string name, TypeNode bound, BirEventAdd operation) => new() {
            Name = name, Kind = "class", CapturedTypeParams = new[] { new BirTypeParameter { Name = "C", Constraints = new[] { bound } } },
            TypeParams = new[] { new BirTypeParameter { Name = "Own" } },
            Methods = new[] { new BirFunctionDeclaration { Name = "invoke", Params = new[] {
                new BirParameter { Name = "receiver", Type = new TypeNode.Tv("type", 0) },
                new BirParameter { Name = "handler", Type = T("object") },
            },
                Ret = T("kotlin.Unit"), Body = new IBirStatement[] { Statement(operation) } } },
        };
        var firstClosure = Closure("FirstClosure", closedBase, firstSubscription);
        var secondClosure = Closure("SecondClosure", T("Other"), secondSubscription);
        Require(firstClosure.Methods.Value[0].StructurallyEquals(secondClosure.Methods.Value[0]), "sibling-frame fixture must exercise equal methods");
        var producer = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { baseOwner, other, staticOwner } };
        var consumer = new BirFileDeclaration { Methods = new[] { caller }, Types = new IBirTypeDeclaration[] { derived, firstClosure, secondClosure } };
        var originalOwners = consumer.Types.Value;
        BindLocalSubscriptionsAll(new[] { producer, consumer });
        Require(ReferenceEquals(consumer.Types.Value, originalOwners) && ReferenceEquals(consumer.Methods.Value[0], caller)
            && inherited.LocalAccessor.Value && inherited.Dispatch.Value == "callvirt"
            && inherited.AccessorOwner.Value is TypeNode.Fqn { Name: "Base", Args: [TypeNode.Fqn { Name: "kotlin.String" }, TypeNode.Fqn { Name: "kotlin.Int" }] }
            && inherited.DelegateType.Value is TypeNode.Fqn { Name: "System.Action", Args: [TypeNode.Fqn { Name: "kotlin.Int" }] }
            && ReferenceEquals(inherited.Recv.Value, receiver) && inherited.Handler.Value is BirCast cast && ReferenceEquals(cast.E.Value, handler)
            && inherited.DeclarationId.Value == baseAdd.DeclarationId.Value
            && inherited.Sig.Value is [TypeNode.Mod { Of: TypeNode.Fqn { Name: "System.Action", Args: [TypeNode.Tv { Scope: "type", I: 1 }] } }],
            "cross-file constructed owner/delegate/open physical signature/actual identity");
        Require(constrainedStatement.Expr.Value is BirConstrainedCall { RecvType.Value: TypeNode.Tv { Scope: "method", I: 0 },
                Iface.Value: TypeNode.Fqn { Name: "Base" }, Method.Value: "remove_Changed", DeclarationId.Value: var selected,
                Virtual.Value: true, Args.Value: [BirCast] }
            && selected == baseRemove.DeclarationId.Value, "protected constrained subscription must enter the ordinary lexical-access call path");
        Require(staticStatement.Expr.Value is BirCallStatic { Owner.Value: TypeNode.Fqn { Name: "StaticOwner" }, Virtual.Value: false,
                Method.Value: "remove_Changed", Args.Value: [BirCast] }, "private static subscription must not acquire a receiver");
        Require(firstSubscription.DelegateType.Value is TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.Int" }] }
            && secondSubscription.DelegateType.Value is TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] },
            "sibling closure frame identity/captured prefix");
        Require(!external.LocalAccessor.IsPresent && !external.DelegateType.IsPresent && ReferenceEquals(external.Handler.Value, externalHandler),
            "referenced events must remain for native member binding");

        var argument = new BirCallStatic { Method = "argument", Ret = integer, Args = Array.Empty<IBirExpression>() };
        var thisReceiver = new BirThis { Sty = new TypeNode.Fqn("Derived", new[] { integer }) };
        var raising = Statement(new BirEventRaise { Type = closedBase, Event = "Changed", Super = true, Recv = thisReceiver,
            Args = new IBirExpression[] { argument } });
        var raisingStatic = Statement(new BirEventRaise { Type = T("StaticOwner"), Event = "Changed", Static = true,
            Args = new IBirExpression[] { new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("value") } } });
        caller.Body = caller.Body.Value.Concat(new IBirStatement[] { raising, raisingStatic }).ToArray();
        BindRaisesAll(new[] { producer, consumer }, null);
        Require(!baseEvent.EventRaiseDeclarationId.IsPresent && !staticOwner.ClrEvents.Value[0].EventRaiseDeclarationId.IsPresent
            && raising.Expr.Value is BirCallInstance { Method.Value: "raisePhysical", DeclarationId.Value: "exact-raise",
                Sig.Value: [TypeNode.Tv { Scope: "type", I: 1 }], Super.Value: true, Virtual.Value: false } raised
            && ReferenceEquals(raised.Recv.Value, thisReceiver) && ReferenceEquals(raised.Args.Value[0], argument)
            && !raised.Ret.IsPresent && !raised.Sty.IsPresent,
            "selected local raise identity/open signature/super receiver/argument single evaluation/fact absence");
        Require(raisingStatic.Expr.Value is BirCallStatic { Method.Value: "raiseStatic", DeclarationId.Value: "exact-static-raise",
            Sig.Value: [TypeNode.Fqn { Name: "kotlin.String" }], Virtual.Value: false }, "selected static raise declaration");
        Require(!other.ClrEvents.Value[0].EventRaiseDeclarationId.IsPresent && baseAdd.DeclarationId.HasValue && baseRemove.DeclarationId.HasValue,
            "accessor-only declarations must not gain a raise identity");
        Console.WriteLine("[event slots] self-test OK (cross-file declarations, constructed/captured frames, sibling identity, lexical calls and exact raises)");
    }
}
