using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    internal static void FinalizationSourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning final properties: " + message); }
        var text = new TypeNode.Fqn("System.String");
        var integer = new TypeNode.Fqn("System.Int32");
        BirParameter Parameter(TypeNode type) => new() { Type = type };
        BirFunctionDeclaration Accessor(string role, string name, string association, string identity = null, string explicitName = null) => new() {
            Name = name, PropertyName = "value", PropertyAccessor = role, PropertyAssociation = association,
            DeclarationId = identity != null ? OptionalField<string>.Of(identity) : default,
            ExplicitClrName = explicitName != null ? OptionalField<string>.Of(explicitName) : default,
            Params = role == "get" ? new[] { Parameter(integer) } : new[] { Parameter(integer), Parameter(text) },
            Ret = role == "get" ? text : new TypeNode.Fqn("void"),
        };
        BirPropertyDeclaration Property(string association, string name = "value") => new() {
            Name = name, Type = text, PropertyAssociation = association, KotlinAccessors = new[] { "get", "set" },
        };
        var getter1 = Accessor("get", "left-get", "local", "left-get-id", "left-get");
        var setter1 = Accessor("set", "left-set", "local", "left-set-id", "left-set");
        var getter2 = Accessor("get", "right-get", "local", "right-get-id", "right-get");
        var setter2 = Accessor("set", "right-set", "local", "right-set-id", "right-set");
        getter1.PhysicalSlotBridge = false;
        getter1.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts {
            Name = "value", Kind = "get", Association = "local", SourceAssociation = "source", Event = "event", Inherited = true,
        };
        var receiver = new BirLocal { Name = "receiver" };
        var call = new BirCallInstance { Method = "selected", Recv = receiver, Args = Array.Empty<IBirExpression>(),
            PropertyName = "value", PropertyAccessor = "get", DeclarationId = "selected-id" };
        getter1.Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } };
        var property1 = Property("local"); var property2 = Property("local");
        var methods1 = new[] { setter1, getter1 }; var methods2 = new[] { getter2, setter2 };
        var roots = new[] {
            new BirFileDeclaration { FileClass = "Shared", Methods = methods1, Properties = new[] { property1 } },
            new BirFileDeclaration { FileClass = "Shared", Methods = methods2, Properties = new[] { property2 } },
        };
        var propertyVector1 = roots[0].Properties.Value; var parameters = getter1.Params.Value;
        FinalizePhysicalProperties(roots);
        Need(property1.Name.Value == "left-get" && property2.Name.Value == "right-get"
            && property1.Get.Value == "left-get" && property1.Set.Value == "left-set"
            && property2.Get.Value == "right-get" && property2.Set.Value == "right-set",
            "own-file association or role-based getter naming changed across a shared facade");
        Need(property1.GetSig.Value.Length == 1 && property1.SetSig.Value.Length == 2
            && TypeNodeDataEquality.Equal(property1.GetSig.Value[0], integer)
            && TypeNodeDataEquality.Equal(property1.GetRet.Value, getter1.Ret.Value), "final accessor signatures were not refreshed from actual methods");
        Need(!property1.KotlinAccessors.IsPresent && !property1.PropertyAssociation.IsPresent
            && !getter1.PropertyName.IsPresent && !getter1.PropertyAccessor.IsPresent && !getter1.PropertyAssociation.IsPresent
            && !getter1.ExplicitClrName.IsPresent && !getter1.PhysicalSlotBridge.IsPresent
            && !call.PropertyName.IsPresent && !call.PropertyAccessor.IsPresent,
            "final phase facts were not consumed on actual declarations and calls");
        var carrier = getter1.KotlinPropertyAccessorCarrier.Value;
        Need(carrier.Name.Value == "value" && carrier.Kind.Value == "get" && carrier.Association.Value == "local"
            && !carrier.SourceAssociation.IsPresent && !carrier.Event.IsPresent && !carrier.Inherited.IsPresent,
            "collision carrier no longer matches the existing replacement policy");
        Need(ReferenceEquals(roots[0].Methods.Value, methods1) && ReferenceEquals(roots[1].Methods.Value, methods2)
            && ReferenceEquals(roots[0].Properties.Value, propertyVector1) && ReferenceEquals(getter1.Params.Value, parameters)
            && ReferenceEquals(call.Recv.Value, receiver) && call.Method.Value == "selected" && call.DeclarationId.Value == "selected-id",
            "finalization copied declarations/operands or changed unrelated selected identities");

        // A genuine authored bridge can collide with the source Property shape.
        // Only its exact bridge association authorizes a stable physical suffix.
        var ordinary = Accessor("get", "ordinary", "ordinary");
        var ordinarySetter = Accessor("set", "setter-explicit", "ordinary", explicitName: "setter-explicit");
        var bridge1 = Accessor("get", "bridge-one", "unused");
        var bridge2 = Accessor("get", "bridge-two", "unused");
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { ordinary, ordinarySetter, bridge1, bridge2 },
            Properties = new[] { new BirPropertyDeclaration { Name = "value", Type = text,
                PropertyAssociation = "ordinary", KotlinAccessors = new[] { "get", "set" } } } };
        AssociateBridgeProperty(owner, bridge1, "value", "get", "source-one", new TypeNode[] { integer }, text);
        AssociateBridgeProperty(owner, bridge2, "value", "get", "source-two", new TypeNode[] { integer }, text);
        var bridgeRows = owner.Properties.Value;
        var bridgeAssociation1 = bridge1.PropertyAssociation.Value; var bridgeAssociation2 = bridge2.PropertyAssociation.Value;
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        FinalizePhysicalProperties(new[] { file });
        Need(bridgeRows[0].Name.Value == "value"
            && bridgeRows[1].Name.Value == "value$bridge$" + DeclarationIdentityBinding.StableSuffix(bridgeAssociation1)
            && bridgeRows[2].Name.Value == "value$bridge$" + DeclarationIdentityBinding.StableSuffix(bridgeAssociation2)
            && bridge1.KotlinPropertyAccessorCarrier.Value.SourceAssociation.Value == "source-one"
            && bridge2.KotlinPropertyAccessorCarrier.Value.SourceAssociation.Value == "source-two"
            && ReferenceEquals(owner.Properties.Value, bridgeRows), "exact bridge allocation or noncollision carrier merge changed");

        // Keep the old finalizer's return handling: no speculative late
        // retirement or new refusal when refreshing a method-generic accessor.
        var generic = Accessor("get", "generic", "generic");
        generic.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        generic.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts { SourceAssociation = "origin", Event = "event", Inherited = true };
        var lateProperty = new BirPropertyDeclaration { Name = "value", Type = text,
            PropertyAssociation = "generic", KotlinAccessors = new[] { "get" } };
        var lateRoot = new BirFileDeclaration { Methods = new[] { generic }, Properties = new[] { lateProperty } };
        FinalizePhysicalProperties(new[] { lateRoot });
        Need(ReferenceEquals(lateRoot.Properties.Value.Single(), lateProperty) && lateProperty.GetMethodArity.Value == 1
            && lateProperty.Set.IsNull && !lateProperty.SetSig.IsPresent
            && generic.KotlinPropertyAccessorCarrier.Value.SourceAssociation.Value == "origin"
            && generic.KotlinPropertyAccessorCarrier.Value.Event.Value == "event" && generic.KotlinPropertyAccessorCarrier.Value.Inherited.Value,
            "late refresh dropped a row or changed shared generic/carrier mutation policy");

        var first = Accessor("get", "unallocated-left", "first", "first-id");
        var second = Accessor("get", "unallocated-right", "second", "second-id");
        Refuse(new[] { new BirFileDeclaration { Methods = new[] { first, second }, Properties = new[] {
            new BirPropertyDeclaration { Name = "value", Type = text, PropertyAssociation = "first", KotlinAccessors = new[] { "get" } },
            new BirPropertyDeclaration { Name = "value", Type = text, PropertyAssociation = "second", KotlinAccessors = new[] { "get" } },
        } } }, "automatic hash suffixes are not used");

        var cross1 = Accessor("get", "occupied", "one", "one", "occupied");
        var cross2 = Accessor("get", "different", "two", "two", "different");
        var cross3 = Accessor("get", "plain", "three", "three");
        Refuse(new[] { new BirFileDeclaration { Methods = new[] { cross1, cross2, cross3 }, Properties = new[] {
            new BirPropertyDeclaration { Name = "value", Type = text, PropertyAssociation = "one", KotlinAccessors = new[] { "get" } },
            new BirPropertyDeclaration { Name = "value", Type = text, PropertyAssociation = "two", KotlinAccessors = new[] { "get" } },
            new BirPropertyDeclaration { Name = "occupied", Type = text, PropertyAssociation = "three", KotlinAccessors = new[] { "get" } },
        } } }, "final CLR PropertyDef identities collide");
        Console.WriteLine("[owning final physical properties] self-test OK (facades, exact associations, getter names, signatures, carriers, bridges, cleanup and collisions)");

        static void Refuse(IReadOnlyList<BirFileDeclaration> files, string expected)
        {
            try { FinalizePhysicalProperties(files); }
            catch (InvalidOperationException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Final property collision was not rejected: " + expected);
        }
    }
}
