using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LocalStaticOwnerBinding
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var method = new BirFunctionDeclaration { Name = "invoke", Static = true };
        var getter = new BirFunctionDeclaration { Name = "readValue", Static = true, PropertyName = "value", PropertyAccessor = "get" };
        var generated = new BirFunctionDeclaration { Name = "helper", Static = true, Generated = true };
        var type = new BirClassDefinition { Name = "PhysicalCarrier", StaticCarrier = new BirStaticCarrier { Owner = "SourceOwner" },
            Methods = new[] { method, getter, generated } };
        var producer = new BirFileDeclaration { FileClass = "Producer", Types = new IBirTypeDeclaration[] { type } };
        var index = Collect(new[] { producer });
        Require(index.Count == 1 && index["SourceOwner"].PhysicalOwner == "PhysicalCarrier"
            && index["SourceOwner"].Methods.SetEquals(new[] { "invoke", "readValue" }), "exact source/carrier declaration index");
        BirCallStatic Call(string member) => new() { OwnerType = new TypeNode.Fqn("SourceOwner"), Method = member,
            ArgTypes = new TypeNode[] { integer }, Args = new IBirExpression[] { new BirLocal { Name = "arg", Sty = integer } } };
        var call = Call("invoke"); call.Sig = OptionalField<TypeNode[]>.Null;
        var property = Call("value"); property.Prop = "get";
        var unresolvedProperty = Call("missing"); unresolvedProperty.Prop = "set";
        var helper = Call("helper");
        var explicitNull = Call("invoke"); explicitNull.Owner = OptionalField<TypeNode>.Null;
        var explicitEmpty = Call("invoke"); explicitEmpty.Sig = Array.Empty<TypeNode>();
        var operations = new[] { call, property, unresolvedProperty, helper, explicitNull, explicitEmpty };
        var body = operations.Select(x => (IBirStatement)new BirExpressionStatement { Expr = x }).ToArray();
        var consumerMethod = new BirFunctionDeclaration { Name = "consumer", Body = body };
        var consumer = new BirFileDeclaration { FileClass = "Consumer", Methods = new[] { consumerMethod } };
        Require(ReferenceEquals(Apply(consumer, index), consumer) && ReferenceEquals(consumerMethod.Body.Value, body), "owning declaration and body identity");
        Require(call.Owner.OrDefault() is TypeNode.Fqn { Name: "PhysicalCarrier" } && !call.OwnerType.IsPresent
            && call.Sig.Value.Length == 1 && !ReferenceEquals(call.Sig.Value, call.ArgTypes.Value), "exact owner and independent signature fill");
        Require(property.Method.OrDefault() == "readValue" && !property.Prop.IsPresent
            && property.PropertyName.OrDefault() == "value" && property.PropertyAccessor.OrDefault() == "get", "source property identity survives accessor allocation");
        Require(unresolvedProperty.OwnerType.HasValue && helper.OwnerType.HasValue && explicitNull.Owner.IsNull
            && explicitNull.OwnerType.HasValue && explicitEmpty.Sig.Value.Length == 0, "unselected, generated, explicit-null owner and empty signature are not reinterpreted");
        var conflict = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { new BirClassDefinition {
            Name = "OtherCarrier", StaticCarrier = new BirStaticCarrier { Owner = "SourceOwner" }, Methods = new[] { method },
        } } };
        try { Collect(new[] { producer, conflict }); throw new Exception("missing conflicting owner refusal"); }
        catch (InvalidOperationException error) when (error.Message.Contains("multiple local static owners")) { }
        Console.WriteLine("[typed local static owners] self-test OK (module snapshot, explicit carrier, accessor identity, presence and exclusions)");

        static void Require(bool condition, string site)
        {
            if (!condition) throw new InvalidOperationException("Local static owner source self-test failed: " + site);
        }
    }
}
