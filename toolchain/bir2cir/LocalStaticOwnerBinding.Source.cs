using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LocalStaticOwnerBinding
{
    public static Dictionary<string, LocalStatics> Collect(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, LocalStatics>(StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var type in file.Types.OrEmpty().OfType<IBirTypeDeclaration>())
            {
                if (!type.Name.HasValue) continue;
                var physical = type.Name.Value;
                var semantic = type.StaticCarrier.OrDefault()?.Owner.OrDefault() ?? physical;
                foreach (var method in type.Methods.OrEmpty())
                    if (method.Static.OrDefault() && !method.Generated.OrDefault() && method.Name.HasValue)
                    {
                        var accessor = KotlinPropertyAccessors.TryIdentity(method, out var property, out var kind);
                        AddStatic(result, semantic, physical, method.Name.Value, accessor ? property : null, kind);
                    }
            }
        return result;
    }

    public static BirFileDeclaration Apply(BirFileDeclaration root, Dictionary<string, LocalStatics> index)
    {
        if (index.Count == 0) return root;
        Walk(root);
        return root;

        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirCallStatic call && !call.Owner.IsPresent
                && call.OwnerType.OrDefault() is TypeNode.Fqn owner && call.Method.HasValue
                && index.TryGetValue(owner.Name, out var statics))
            {
                var method = call.Method.Value;
                var kind = call.Prop.OrDefault();
                var accessor = kind is "get" or "set"
                    && statics.PropertyAccessors.TryGetValue(method + "\u001f" + kind, out var physical)
                    ? physical : null;
                if (accessor != null)
                {
                    KotlinPropertyAccessors.PreserveCallIdentity(call, method, kind);
                    call.Prop = default;
                    call.Method = accessor;
                }
                if (accessor != null || kind is not ("get" or "set") && statics.Methods.Contains(method))
                {
                    if (!call.Sig.HasValue && call.ArgTypes.HasValue)
                        call.Sig = call.ArgTypes.Value.Select(BirGraphValue.CopyType).ToArray();
                    call.Owner = new TypeNode.Fqn(statics.PhysicalOwner);
                    call.OwnerType = default;
                }
            }
            node.VisitChildren(Walk);
        }
    }
}
