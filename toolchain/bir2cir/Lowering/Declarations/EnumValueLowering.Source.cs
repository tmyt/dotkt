using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumValueLowering
{
    internal static bool Apply(BirFileDeclaration file, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, (string Name, string Ordinal)> localRichFields, ISet<string> localBasicEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localExplicitEnums, IReadOnlyDictionary<string, BasicEnumMetadata> localParseEnums)
    {
        var runtimeLookup = false;
        Walk(file);
        return runtimeLookup;

        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            node = Rewrite(node);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        BirGraphValue Rewrite(BirGraphValue node)
        {
            if (node is BirCallInstance call && call.Prop.OrDefault() == "get"
                && call.Method.OrDefault() is "name" or "ordinal"
                && call.Args.HasValue && call.Args.Value.Length == 0 && call.Recv.HasValue && call.OwnerType.HasValue)
            {
                var member = call.Method.Value;
                var declaration = DeclaringEnumForProperty(call, member);
                var owner = declaration ?? call.OwnerType.Value;
                if (member == "name" && declaration != null)
                    node = new BirEnumName { E = OptionalField<IBirExpression>.Of(Copy(call.Recv.Value)), Type = BirGraphValue.CopyType(declaration) };
                else if (owner is TypeNode.Fqn propertyEnum
                    && TryRichInstanceFields(propertyEnum.Name, refs, localRichFields, out var name, out var ordinal))
                    return RichField(call.Recv.Value, owner, member == "name" ? name : ordinal, member == "name" ? "kotlin.String" : "kotlin.Int");
            }
            if (node is BirEnumValue value && value.Entry.HasValue && value.Type.OrDefault() is TypeNode.Fqn entryOwner)
            {
                if (localExplicitEnums.TryGetValue(entryOwner.Name, out var metadata)) Stamp(value, metadata);
                else if (refs.TryKotlinRichEnumEntryField(entryOwner.Name, value.Entry.Value, out var field))
                    return new BirStaticFieldGet { OwnerType = BirGraphValue.CopyType(entryOwner), Name = field };
                else if (refs.TryKotlinBasicEnum(entryOwner.Name, out metadata)) Stamp(value, metadata);
                else if (refs.ResolveNetEnumConstant(entryOwner.Name, value.Entry.Value) is { } physical)
                { value.Underlying = physical.Underlying; value.PhysicalValue = physical.Value; }
            }
            if (node is BirEnumParse { Type.HasValue: true, Arg.HasValue: true } parse)
            {
                if (parse.Type.Value is TypeNode.Fqn parseOwner
                    && (localParseEnums.TryGetValue(parseOwner.Name, out var metadata)
                        || refs.TryKotlinBasicEnum(parseOwner.Name, out metadata) || refs.TryNativeEnumEntries(parseOwner.Name, out metadata)))
                    return ExplicitParse(parseOwner, parse.Arg.Value, metadata);
                if (parse.Type.Value is TypeNode.Tv variable)
                {
                    runtimeLookup = true;
                    return RuntimeCall(variable, "kotlinEnumValueOf", parse.Arg.Value);
                }
            }
            var instance = node switch {
                BirEnumName name when name.E.HasValue && name.Type.HasValue => (name.E.Value, name.Type.Value, true),
                BirEnumOrdinal ordinal when ordinal.E.HasValue && ordinal.Type.HasValue => (ordinal.E.Value, ordinal.Type.Value, false),
                _ => ((IBirExpression)null, (TypeNode)null, false),
            };
            if (instance.Item2 is TypeNode.Fqn enumOwner)
            {
                var (receiver, type, nameOperation) = instance;
                if (TryRichInstanceFields(enumOwner.Name, refs, localRichFields, out var name, out var ordinal))
                    return RichField(receiver, type, nameOperation ? name : ordinal, nameOperation ? "kotlin.String" : "kotlin.Int");
                if (nameOperation) return new BirObjectMethod { Method = "ToString", Recv = OptionalField<IBirExpression>.Of(Copy(receiver)) };
                var ordinalNode = (BirEnumOrdinal)node;
                if (localExplicitEnums.TryGetValue(enumOwner.Name, out var metadata) || refs.TryKotlinBasicEnum(enumOwner.Name, out metadata))
                    ordinalNode.Values = ValuesValue(metadata);
                else if (localBasicEnums.Contains(enumOwner.Name)) ordinalNode.Type = default;
                else if (refs.TryNativeEnumEntries(enumOwner.Name, out metadata)) ordinalNode.Values = ValuesValue(metadata);
            }
            if (node is BirEnumValues { Type.HasValue: true } values)
            {
                if (values.Type.Value is TypeNode.Fqn valuesOwner
                    && (localExplicitEnums.TryGetValue(valuesOwner.Name, out var metadata)
                        || refs.TryKotlinBasicEnum(valuesOwner.Name, out metadata) || refs.TryNativeEnumEntries(valuesOwner.Name, out metadata)))
                    values.Values = ValuesValue(metadata);
                if (values.Type.Value is TypeNode.Tv variable)
                {
                    runtimeLookup = true;
                    return RuntimeCall(variable, "kotlinEnumValues", null);
                }
            }
            return node;
        }
    }

    static IBirExpression Copy(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();

    static TypeNode DeclaringEnumForProperty(BirCallInstance call, string member)
    {
        if (!call.DeclarationId.HasValue || !call.Overrides.HasValue) return null;
        var declaration = call.Overrides.Value
            .Where(edge => edge.DeclarationId.OrDefault() == call.DeclarationId.Value
                && edge.Member.OrDefault() == member && edge.Kind.OrDefault() == "getter")
            .Select(edge => edge.Owner.OrDefault()).OfType<TypeNode.Fqn>()
            .SingleOrDefault(owner => owner is { Name: "kotlin.Enum", Args: [TypeNode.Fqn] });
        return declaration?.Args[0];
    }

    static BirFieldGet RichField(IBirExpression receiver, TypeNode owner, string field, string result)
        => new() { OwnerType = BirGraphValue.CopyType(owner), Recv = OptionalField<IBirExpression>.Of(Copy(receiver)),
            Name = field, Ret = new TypeNode.Fqn(result) };

    static void Stamp(BirEnumValue value, BasicEnumMetadata metadata)
    {
        var physical = metadata.Entries.SingleOrDefault(entry => entry.Name == value.Entry.Value)
            ?? throw new InvalidOperationException($"bir2cir: explicit enum '{((TypeNode.Fqn)value.Type.Value).Name}' has no entry '{value.Entry.Value}'");
        value.Underlying = metadata.Underlying;
        value.PhysicalValue = physical.PhysicalValue;
    }

    static BirPhysicalEnumValue[] ValuesValue(BasicEnumMetadata metadata) => metadata.Entries.Select(entry => new BirPhysicalEnumValue {
        Ordinal = entry.Ordinal, Underlying = metadata.Underlying, PhysicalValue = entry.PhysicalValue,
    }).ToArray();

    static BirCast RuntimeCall(TypeNode.Tv variable, string method, IBirExpression argument)
    {
        var token = new BirClassReference { Type = BirGraphValue.CopyType(variable) };
        return new BirCast { Type = argument == null ? new TypeNode.Array(BirGraphValue.CopyType(variable)) : BirGraphValue.CopyType(variable),
            E = new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.EnumRuntimeKt"), Method = method,
                Sig = argument == null ? new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }
                    : new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.String") },
                Args = argument == null ? new IBirExpression[] { token } : new IBirExpression[] { token, Copy(argument) }, Ret = new TypeNode.Fqn("kotlin.Any") } };
    }

    static BirValueBlock ExplicitParse(TypeNode.Fqn owner, IBirExpression argument, BasicEnumMetadata metadata)
    {
        // Kotlin valueOf admits an exact declared name, not Enum.Parse's numeric strings or flag combinations.
        // Bind the argument once and retain declaration order, including aliased physical constants.
        var name = "__enumParse$" + System.Threading.Interlocked.Increment(ref _parseTemp);
        var text = new TypeNode.Fqn("kotlin.String");
        IBirExpression result = new BirThrowExpression { Value = new BirNew {
            Type = new TypeNode.Fqn("kotlin.IllegalArgumentException"), ArgTypes = new TypeNode[] { new TypeNode.Nullable(text) },
            Args = new IBirExpression[] { new BirConstant { Type = text, Value = new BirLiteral.TextLiteral($"No enum constant {owner.Name}") } },
        } };
        foreach (var entry in metadata.Entries.Reverse())
            result = new BirConditional { Type = BirGraphValue.CopyType(owner),
                Cond = new BirObjectEquals { Lhs = new BirLocal { Name = name },
                    Rhs = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral(entry.Name) } },
                Then = new BirEnumValue { Type = BirGraphValue.CopyType(owner), Entry = entry.Name, Ordinal = entry.Ordinal,
                    Underlying = metadata.Underlying, PhysicalValue = entry.PhysicalValue },
                Else = OptionalField<IBirExpression>.Of(result) };
        return new BirValueBlock { Type = BirGraphValue.CopyType(owner),
            Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = text, Init = OptionalField<IBirExpression>.Of(Copy(argument)) } },
            Result = OptionalField<IBirExpression>.Of(result) };
    }
}
