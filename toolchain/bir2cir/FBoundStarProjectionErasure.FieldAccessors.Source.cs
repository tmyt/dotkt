using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // These declarations expose concrete field storage through the existential
    // surface. They do not replace the field or extend the earlier method index.
    internal static IReadOnlyList<BirFunctionDeclaration> AddFieldAccessorsSource(string ownerName, int arity,
        IBirTypeDeclaration definition, CurrentDeclarationBindings bindings)
    {
        if (!definition.Fields.HasValue) return Array.Empty<BirFunctionDeclaration>();
        var methods = definition.Methods.OrEmpty().ToList();
        var names = methods.Select(method => method.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var appended = new List<BirFunctionDeclaration>();
        bool HasAccessor(string name, string kind) => methods.Any(method =>
            KotlinPropertyAccessors.TryIdentity(method, out var property, out var role) && property == name && role == kind);
        TypeNode OwnerType() => new TypeNode.Fqn(ownerName,
            Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());

        foreach (var field in definition.Fields.Value)
        {
            if (field.Static.OrDefault() || field.Outer.OrDefault()) continue;
            var name = field.Name.OrDefault();
            var type = field.Type.OrDefault();
            if (name == null || type == null || HasAccessor(name, "get")) continue;
            foreach (var kind in new[] { "get", "set" })
            {
                // readOnly restricts the Kotlin surface, not its actual storage.
                if (kind == "set" && field.InitOnly.OrDefault() || HasAccessor(name, kind)) continue;
                var stem = name + "$field$" + kind;
                var physicalName = stem;
                for (var ordinal = 1; !names.Add(physicalName); ordinal++) physicalName = stem + "$" + ordinal;
                IBirExpression access = kind == "get"
                    ? field.Lateinit.OrDefault()
                        ? new BirLateinitGet { OwnerType = OwnerType(), Recv = new BirThis(), Name = name,
                            Ret = BirGraphValue.CopyType(type) }
                        : new BirFieldGet { OwnerType = OwnerType(), Recv = new BirThis(), Name = name,
                            Ret = BirGraphValue.CopyType(type) }
                    : new BirFieldSetExpression { OwnerType = OwnerType(), Recv = new BirThis(), Name = name,
                        Ret = BirGraphValue.CopyType(type), Value = new BirLocal { Name = "value" } };
                var method = new BirFunctionDeclaration {
                    Name = physicalName, Static = false, Vis = "private", Generated = true,
                    Override = false, Virtual = false, Abstract = false, ObjectOverride = false,
                    PropertyName = name, PropertyAccessor = kind, PropertyAssociation = ownerName + ":field:" + name,
                    PhysicalSlotBridge = true,
                    Params = kind == "get" ? Array.Empty<BirParameter>() : new[] {
                        new BirParameter { Name = "value", Type = BirGraphValue.CopyType(type) } },
                    Ret = kind == "get" ? BirGraphValue.CopyType(type) : new TypeNode.Fqn("kotlin.Unit"),
                    Body = kind == "get" ? new IBirStatement[] {
                        new BirReturn { Value = OptionalField<IBirExpression>.Of(access) } } : new IBirStatement[] {
                        new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(access) } },
                    Attrs = Array.Empty<BirAttribute>(),
                };
                methods.Add(method);
                appended.Add(method);
                bindings.GetSource(method);
            }
        }
        // A present fields vector establishes an
        // empty methods vector even if it contains no accessor-eligible field.
        if (appended.Count != 0 || !definition.Methods.HasValue) definition.Methods = methods.ToArray();
        return appended.ToArray();
    }
}
