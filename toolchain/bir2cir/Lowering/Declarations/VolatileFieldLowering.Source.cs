using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// IsVolatile is a CLR field/access fact. State the access prefix here, including
// constructed local generic fields; ilemit must not rediscover it from FieldInfo.
static partial class VolatileFieldLowering
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var fields = new HashSet<(string Owner, string Name)>();
        var volatileFields = new HashSet<(string Owner, string Name)>();
        foreach (var root in roots)
        {
            Collect(root.Fields.OrEmpty(), root.FileClass.OrDefault());
            foreach (var type in root.Types.OrEmpty()) Collect(type.Fields.OrEmpty(), type.Name.OrDefault());
        }
        foreach (var root in roots) Visit(root, Stamp, postorder: true);
        foreach (var root in roots) Visit(root, PropagatePointers);

        void Collect(IEnumerable<BirFieldDeclaration> declarations, string owner)
        {
            if (owner == null) return;
            foreach (var field in declarations)
                if (field.Name.OrDefault() is string name)
                {
                    fields.Add((owner, name));
                    if (field.Volatile.OrDefault()) volatileFields.Add((owner, name));
                }
        }
        void Set(TypeNode type, string name, Action stamp)
        {
            if (type is TypeNode.Fqn owner && name != null && (volatileFields.Contains((owner.Name, name))
                || !fields.Contains((owner.Name, name)) && refs.TryResolveVolatileField(owner, name))) stamp();
        }
        void Stamp(BirGraphValue node)
        {
            switch (node)
            {
                case BirFieldGet value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirSetField value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirFieldSetExpression value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirStaticFieldGet value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirStaticFieldSet value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirLateinitGet value: Set(value.OwnerType.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirPropertyGet value: Set(value.Type.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
                case BirPropertySet value: Set(value.Type.OrDefault(), value.Name.OrDefault(), () => value.Volatile = true); break;
            }
        }
    }

    static void Visit(BirGraphValue node, Action<BirGraphValue> visit, bool postorder = false)
    {
        if (node == null || node is IBirExecutablePayload) return;
        if (!postorder) visit(node);
        node.VisitChildren(child => Visit(child, visit, postorder));
        if (postorder) visit(node);
    }

    // Preserve the existing per-body name collection/marking rule. This is not
    // a stronger alias analysis or a replay of managed-pointer allocation.
    static void PropagatePointers(BirGraphValue node)
    {
        var body = node switch {
            BirFunctionDeclaration value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
            BirConstructor value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
            BirClosureDefinition value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
            BirNewSuspendLambda value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
            BirInlineLambda value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
            _ => null,
        };
        if (body == null) return;
        var pointers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body) Visit((BirGraphValue)statement, child => {
            if (child is BirVariable { Name.HasValue: true, Init.HasValue: true } variable
                && variable.Init.Value is BirByRefOf { Inner.HasValue: true } address
                && Volatile((BirGraphValue)address.Inner.Value)) pointers.Add(variable.Name.Value);
        });
        if (pointers.Count == 0) return;
        foreach (var statement in body) Visit((BirGraphValue)statement, child => {
            if (child is BirByRefLoad load && load.Local.OrDefault() is string readName && pointers.Contains(readName)) load.Volatile = true;
            else if (child is BirByRefStore store && store.Local.OrDefault() is string writeName && pointers.Contains(writeName)) store.Volatile = true;
        });
    }

    static bool Volatile(BirGraphValue value) => value switch {
        BirFieldGet node => node.Volatile.OrDefault(), BirSetField node => node.Volatile.OrDefault(),
        BirFieldSetExpression node => node.Volatile.OrDefault(), BirStaticFieldGet node => node.Volatile.OrDefault(),
        BirStaticFieldSet node => node.Volatile.OrDefault(), BirLateinitGet node => node.Volatile.OrDefault(),
        BirPropertyGet node => node.Volatile.OrDefault(), BirPropertySet node => node.Volatile.OrDefault(),
        BirByRefLoad node => node.Volatile.OrDefault(), BirByRefStore node => node.Volatile.OrDefault(),
        _ => false,
    };
}
