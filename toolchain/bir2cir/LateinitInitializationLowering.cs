using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A lateinit slot distinguishes uninitialized storage from every initialized value, including default(T).
// Its logical accessors retain their Kotlin signatures; a private helper exposes only initialization state.
static class LateinitInitializationLowering
{
    sealed record Storage(string Owner, BirGraphValue Declaration, BirFieldDeclaration Field, TypeNode ValueType)
    {
        public string QueryMethod;
        public bool Static => Field.Static.OrDefault();
    }

    public static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var identities = new Dictionary<string, Storage>(StringComparer.Ordinal);
        var fields = new Dictionary<(string Owner, string Name, bool Static), Storage>();
        void Collect(BirGraphValue declaration, string owner, IEnumerable<BirFieldDeclaration> declaredFields)
        {
            if (owner == null) return;
            foreach (var field in declaredFields)
            {
                if (field.LateinitDeclarationId.OrDefault() is not string id) continue;
                var storage = new Storage(owner, declaration, field, BirGraphValue.CopyType(field.Type.Value));
                identities.Add(id, storage);
                fields.Add((owner, field.Name.OrDefault(), storage.Static), storage);
                if (storage.Static && declaration is IBirTypeDeclaration type && type.StaticCarrier.OrDefault()?.Owner.OrDefault() is string sourceOwner)
                    fields.Add((sourceOwner, field.Name.OrDefault(), true), storage);
                field.Type = new TypeNode.Fqn("object");
                field.Vis = "private";
            }
        }
        foreach (var root in roots)
        {
            Collect(root, root.FileClass.OrDefault(), root.Fields.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Collect((BirGraphValue)type, type.Name.OrDefault(), type.Fields.OrEmpty());
        }
        // Public defaults carry queries opaquely, so their authored target must exist even without a local query.
        foreach (var storage in identities.Values) EnsureQueryMethod(storage);
        BirGraphValue Rewrite(BirGraphValue node)
        {
            node.VisitChildSlots(slot => {
                var current = slot.Value;
                var replacement = Rewrite(current);
                if (!ReferenceEquals(current, replacement)) slot.Replace(replacement);
            });
            if (node is BirFunctionDeclaration { Params.HasValue: true, Body.HasValue: true } method
                && method.DeclarationId.OrDefault() is string declarationId && identities.TryGetValue(declarationId, out var declarationStorage))
            {
                method.Lateinit = true;
                RoundtripMetadata.StampLateinitState(method, declarationStorage.Owner, declarationStorage.QueryMethod, declarationStorage.Static);
            }
            if (node is BirLateinitIsInitialized query)
            {
                var id = query.LateinitDeclarationId.OrDefault();
                var target = identities.TryGetValue(id, out var local)
                    ? new LateinitStateTarget(local.Owner, local.QueryMethod, local.Static) : refs.LateinitStateForDeclaration(id);
                var owner = new TypeNode.Fqn(target.Owner, target.Static ? null : (query.OwnerType.OrDefault() as TypeNode.Fqn)?.Args);
                var boolean = new TypeNode.Fqn("kotlin.Boolean");
                return target.Static ? new BirCallStatic {
                    OwnerType = owner, Method = target.Method, Args = Array.Empty<IBirExpression>(),
                    Sig = Array.Empty<TypeNode>(), Ret = boolean, MemberVisibility = "private",
                    MemberSignature = Array.Empty<TypeNode>(), MemberReturnType = boolean,
                } : new BirCallInstance {
                    OwnerType = owner, Method = target.Method, Args = Array.Empty<IBirExpression>(),
                    Sig = Array.Empty<TypeNode>(), Ret = boolean, MemberVisibility = "private",
                    MemberSignature = Array.Empty<TypeNode>(), MemberReturnType = boolean, Recv = query.Recv, Virtual = false,
                };
            }
            var (ownerType, name, isStatic) = node switch {
                BirLateinitGet read => (read.OwnerType.OrDefault(), read.Name.OrDefault(), read.Static.OrDefault()),
                BirSetField write => (write.OwnerType.OrDefault(), write.Name.OrDefault(), false),
                BirFieldSetExpression write => (write.OwnerType.OrDefault(), write.Name.OrDefault(), false),
                BirStaticFieldSet write => (write.OwnerType.OrDefault(), write.Name.OrDefault(), true),
                _ => ((TypeNode)null, null, false),
            };
            if (ownerType is not TypeNode.Fqn ownerName || name == null
                || !fields.TryGetValue((ownerName.Name, name, isStatic), out var storage)) return node;
            static OptionalField<IBirExpression> Box(OptionalField<IBirExpression> value) => OptionalField<IBirExpression>.Of(
                new BirCast { Type = new TypeNode.Fqn("object"), E = value.HasValue ? value : OptionalField<IBirExpression>.Null });
            switch (node)
            {
                case BirSetField write: write.Value = Box(write.Value); return node;
                case BirFieldSetExpression write: write.Value = Box(write.Value); return node;
                case BirStaticFieldSet write: write.Value = Box(write.Value); return node;
                case BirLateinitGet read:
                    return new BirCast {
                        Type = BirGraphValue.CopyType(read.Sty.OrDefault() ?? storage.ValueType),
                        E = new BirLateinitGet {
                            Name = name, Value = OptionalField<IBirExpression>.Of(RawRead(storage, BirGraphValue.CopyType(ownerType), read.Recv)),
                            LateinitSourceName = read.LateinitSourceName.OrDefault() ?? name,
                        },
                    };
            }
            return node;
        }
        foreach (var root in roots) Rewrite(root);
        foreach (var storage in identities.Values) storage.Field.LateinitDeclarationId = default;
    }

    static IBirExpression RawRead(Storage storage, TypeNode owner, OptionalField<IBirExpression> receiver) => storage.Static
        ? new BirStaticFieldGet { OwnerType = owner, Name = storage.Field.Name }
        : new BirFieldGet { OwnerType = owner, Name = storage.Field.Name, Recv = receiver.HasValue ? receiver : OptionalField<IBirExpression>.Null };

    static string EnsureQueryMethod(Storage storage)
    {
        if (storage.QueryMethod != null) return storage.QueryMethod;
        var existing = storage.Declaration is BirFileDeclaration file ? file.Methods.OrEmpty() : ((IBirTypeDeclaration)storage.Declaration).Methods.OrEmpty();
        var names = existing.Select(method => method.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var ordinal = 0;
        while (!names.Add(storage.QueryMethod = "dotkt<lateinitInitialized>" + ordinal++)) { }
        var parameters = storage.Declaration is IBirTypeDeclaration type ? type.TypeParams.OrEmpty() : Array.Empty<BirTypeParameter>();
        var arguments = parameters.Select((_, index) => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var read = RawRead(storage, new TypeNode.Fqn(storage.Owner, arguments.Length == 0 ? null : arguments), OptionalField<IBirExpression>.Of(new BirThis()));
        var helper = new BirFunctionDeclaration {
            Name = storage.QueryMethod, Static = storage.Static, Vis = "private", Generated = true,
            Override = false, Virtual = false, Abstract = false, ObjectOverride = false,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Boolean"),
            Body = new IBirStatement[] { new BirReturn { Value = new BirBinaryOperation {
                Op = "!=", Lhs = new BirCast { Type = new TypeNode.Fqn("object"), E = OptionalField<IBirExpression>.Of(read) },
                Rhs = new BirConstant { Type = new TypeNode.Fqn("object"), Value = new BirLiteral.NullLiteral() },
            } } },
        };
        var methods = existing.Append(helper).ToArray();
        if (storage.Declaration is BirFileDeclaration root) root.Methods = methods;
        else ((IBirTypeDeclaration)storage.Declaration).Methods = methods;
        return storage.QueryMethod;
    }
}
