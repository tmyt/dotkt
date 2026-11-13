using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CompanionExtensionBinding
{
    static void RewriteUses(BirGraphValue root, IReadOnlyDictionary<string, Binding> bindings, ReferenceMetadataIndex refs)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            if (node is BirAttribute attribute)
            {
                if (attribute.Attr.OrDefault() is TypeNode.Fqn { Name: DefaultBirCarrier.AttributeName })
                    DefaultBirCarrier.Rewrite(attribute, expression => (IBirExpression)Walk((BirGraphValue)expression));
                return node;
            }
            node = RewriteUse(node, bindings, refs);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        Walk(root);
    }

    static BirGraphValue RewriteUse(BirGraphValue node, IReadOnlyDictionary<string, Binding> bindings, ReferenceMetadataIndex refs)
    {
        var receiver = node switch {
            BirCallInline value => value.CompanionReceiver.OrDefault(),
            BirCallStatic value => value.CompanionReceiver.OrDefault(),
            BirNewDelegate value => value.CompanionReceiver.OrDefault(),
            BirStaticFieldGet value => value.CompanionReceiver.OrDefault(),
            BirStaticFieldSet value => value.CompanionReceiver.OrDefault(),
            BirLateinitGet value => value.CompanionReceiver.OrDefault(),
            _ => null,
        };
        if (receiver == null) return node;
        var kind = ((BirGraphNode)node).Tag;
        static string Owner(TypeNode type) => (type as TypeNode.Fqn)?.Name;
        var owner = node switch {
            BirCallInline value => Owner(value.Owner.OrDefault()),
            BirCallStatic value => Owner(value.CalleeOwner.OrDefault()) ?? Owner(value.Owner.OrDefault()) ?? Owner(value.OwnerType.OrDefault()),
            BirNewDelegate value => Owner(value.CalleeOwner.OrDefault()),
            BirStaticFieldGet value => Owner(value.OwnerType.OrDefault()),
            BirStaticFieldSet value => Owner(value.OwnerType.OrDefault()),
            BirLateinitGet value => Owner(value.OwnerType.OrDefault()), _ => null,
        };
        var name = node switch {
            BirCallInline value => Owner(value.Callee.OrDefault()), BirCallStatic value => value.Method.OrDefault(),
            BirNewDelegate value => value.Method.OrDefault(), BirStaticFieldGet value => value.Name.OrDefault(),
            BirStaticFieldSet value => value.Name.OrDefault(), BirLateinitGet value => value.Name.OrDefault(), _ => null,
        };
        var id = node.DeclarationIdentityFact.OrDefault();
        var plan = PlanUse(new(kind, id, TypeNode.ToJson(receiver), owner, name,
            (node as BirCallStatic)?.Prop.OrDefault()), bindings, refs);
        var target = plan.Target;
        switch (node)
        {
            case BirCallInline value:
                value.Owner = new TypeNode.Fqn(target.PhysicalOwner);
                value.Callee = new TypeNode.Fqn(target.PhysicalOwner + "." + target.PhysicalName);
                value.CompanionReceiver = default;
                break;
            case BirCallStatic value:
                if (!plan.Identified && plan.Kind is "get" or "set")
                { value.PropertyName = plan.SourceName; value.PropertyAccessor = plan.Kind; }
                value.Method = target.PhysicalName;
                var physical = new TypeNode.Fqn(target.PhysicalOwner);
                if (value.CalleeOwner.HasValue) value.CalleeOwner = physical;
                else if (value.Owner.HasValue) value.Owner = physical;
                else if (value.OwnerType.HasValue) value.OwnerType = physical;
                else value.CalleeOwner = physical;
                value.Prop = default; value.CompanionReceiver = default;
                break;
            case BirNewDelegate value:
                value.Method = target.PhysicalName; value.CalleeOwner = new TypeNode.Fqn(target.PhysicalOwner);
                value.CompanionReceiver = default;
                break;
            default:
                IBirExpression argument = (node as BirStaticFieldSet)?.Value.OrDefault();
                var style = node switch {
                    BirStaticFieldGet value => value.Sty,
                    BirLateinitGet value => value.Sty, _ => default,
                };
                return new BirCallStatic {
                    OwnerType = new TypeNode.Fqn(target.PhysicalOwner), Method = target.PhysicalName,
                    PropertyName = plan.SourceName, PropertyAccessor = plan.Kind, Sty = style,
                    Sig = plan.Kind == "set" && target.ValueType != null ? new[] { TypeNode.Parse(target.ValueType) } : Array.Empty<TypeNode>(),
                    Args = argument == null ? Array.Empty<IBirExpression>() : new[] { argument },
                };
        }
        return node;
    }
}
