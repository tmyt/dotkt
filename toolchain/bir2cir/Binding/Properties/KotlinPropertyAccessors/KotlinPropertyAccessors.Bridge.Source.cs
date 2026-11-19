using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    sealed record BridgePropertyPlan(string Association, TypeNode Type);

    static BridgePropertyPlan PlanBridgeProperty(string accessorKind, string sourceAssociation,
        TypeNode[] slotParams, TypeNode slotRet, bool exactInterface = false)
    {
        if (accessorKind is not ("get" or "set"))
            throw new InvalidOperationException("property override bridge has no getter/setter role");
        if (!exactInterface && accessorKind == "set" && slotParams.Length == 0)
            throw new InvalidOperationException("property setter override bridge has no value parameter");
        if (string.IsNullOrEmpty(sourceAssociation))
            throw new InvalidOperationException("property override bridge has no source association");
        if (exactInterface) return new("dotkt$bridge$property$" + sourceAssociation + "|exact-interface", null);
        var propertyType = accessorKind == "get" ? slotRet : slotParams[^1];
        var indexParams = accessorKind == "get" ? slotParams : slotParams[..^1];
        return new("dotkt$bridge$property$" + sourceAssociation + "|" + SupertypeGraph.TypeKey(propertyType)
            + "(" + string.Join(",", indexParams.Select(SupertypeGraph.TypeKey)) + ")", propertyType);
    }

    static bool CanMergeBridgeProperty(string name, TypeNode type, string[] roles,
        string propertyName, string accessorKind, TypeNode propertyType)
        => name == propertyName && type != null && type.Equals(propertyType)
            && roles != null && !roles.Contains(accessorKind);

    static void StampBridgeProperty(BirFunctionDeclaration bridge, string propertyName, string accessorKind,
        string sourceAssociation, string association)
    {
        bridge.PropertyName = propertyName;
        bridge.PropertyAccessor = accessorKind;
        bridge.PropertyAssociation = association;
        bridge.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts {
            Name = propertyName, Kind = accessorKind, Association = association, SourceAssociation = sourceAssociation,
        };
    }

    internal static void AssociateBridgeProperty(IBirTypeDeclaration owner, BirFunctionDeclaration bridge,
        string propertyName, string accessorKind, string sourceAssociation, TypeNode[] slotParams, TypeNode slotRet)
    {
        var plan = PlanBridgeProperty(accessorKind, sourceAssociation, slotParams, slotRet);
        StampBridgeProperty(bridge, propertyName, accessorKind, sourceAssociation, plan.Association);
        var property = owner.Properties.OrEmpty().SingleOrDefault(candidate => candidate.PropertyAssociation.OrDefault() == plan.Association);
        if (property == null)
        {
            owner.Properties = owner.Properties.OrEmpty().Append(new BirPropertyDeclaration {
                Name = propertyName, Type = BirGraphValue.CopyType(plan.Type),
                KotlinAccessors = new[] { accessorKind }, PropertyAssociation = plan.Association,
            }).ToArray();
            return;
        }
        if (!CanMergeBridgeProperty(property.Name.OrDefault(), property.Type.OrDefault(), property.KotlinAccessors.OrDefault(),
                propertyName, accessorKind, plan.Type))
        {
            var associatedMethods = string.Join(",", owner.Methods.OrEmpty()
                .Where(method => method.PropertyAssociation.OrDefault() == plan.Association).Select(method => method.Name.OrDefault()));
            throw new InvalidOperationException("property override bridges do not form one unique CLR Property shape: "
                + $"owner={owner.Name.OrDefault() ?? "<file>"}, property={propertyName}, accessor={accessorKind}, "
                + $"bridge={bridge.Name.OrDefault()}, association={plan.Association}, methods={associatedMethods}");
        }
        property.KotlinAccessors = property.KotlinAccessors.Value.Append(accessorKind).ToArray();
    }

    // Interface MethodImpl bodies are private forwarding methods, not additional Property accessors.
    // Two distinct slot declarations can require separate bodies with identical physical signatures.
    internal static void MarkExactInterfaceBridgeProperty(BirFunctionDeclaration bridge, string propertyName,
        string accessorKind, string sourceAssociation)
    {
        var plan = PlanBridgeProperty(accessorKind, sourceAssociation, null, null, exactInterface: true);
        StampBridgeProperty(bridge, propertyName, accessorKind, sourceAssociation, plan.Association);
    }
}
