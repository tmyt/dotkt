using System.IO;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    static IBirExpression FactoryClone(IBirExpression expression)
        => expression == null ? null : (IBirExpression)((BirGraphValue)expression).DeepClone();
    static TypeNode FactoryType(TypeNode type) => BirGraphValue.CopyType(type);
    static OptionalField<IBirExpression> FactoryExpression(IBirExpression expression)
        => expression == null ? OptionalField<IBirExpression>.Null : OptionalField<IBirExpression>.Of(expression);

    internal static IBirExpression TransformStorageField(BirFieldGet node)
    {
        if (node.Name.OrDefault() != "storage" || node.OwnerType.OrDefault() is not TypeNode.Fqn owner
            || !UnsignedArraySignedElem.TryGetValue(owner.Name, out var signedElement)) return null;
        return new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn(signedElement)),
            E = FactoryExpression(FactoryClone(node.Recv.OrDefault())) };
    }

    internal static IBirExpression TransformStaticField(BirStaticFieldGet node, ReferenceMetadataIndex refs)
    {
        if (node.Name.OrDefault() != "INSTANCE" || node.OwnerType.OrDefault() is not TypeNode.Fqn owner
            || string.IsNullOrEmpty(owner.Name) || !refs.TryResolveClrOwner(owner.Name, out _, out _)) return null;
        return new BirConstant { Type = new TypeNode.Fqn("object"), Value = new BirLiteral.NullLiteral() };
    }

    internal static IBirExpression TransformNew(BirNew node, ReferenceMetadataIndex refs)
    {
        if (node.Type.OrDefault() is not TypeNode.Fqn owner) return null;
        var arguments = node.Args.OrEmpty();
        // The sized form has already been consumed by ArrayConstructionLowering.
        // A surviving unsigned-array one-argument construction is its storage wrap.
        if (UnsignedArraySignedElem.ContainsKey(owner.Name)
            && BirTypeLowering.PrimArrayElem.TryGetValue(owner.Name, out var unsignedElement) && arguments.Length == 1)
            return new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn(unsignedElement)),
                E = FactoryExpression(FactoryClone(arguments[0])) };
        var viaAlias = refs.TryResolveClrOwner(owner.Name, out var physicalName, out var kind);
        if (!viaAlias)
        {
            if (refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name), owner.Args?.Length ?? 0) == null) return null;
            physicalName = owner.Name;
            kind = "class";
        }
        // Inline construction is identity, not a signed conversion: preserve bits.
        if (refs.IsInlineValueClass(owner.Name) && arguments.Length == 1) return FactoryClone(arguments[0]);
        if (kind is "struct" or "enum") return null;
        var target = viaAlias ? (TypeNode.Fqn)ClrOwnerType(refs, owner) : new TypeNode.Fqn(physicalName, owner.Args);
        BirCallBinding[] plan = null;
        var declared = node.ArgTypes.OrDefault();
        if (arguments.Length == 2 && refs.Aliases.ContainsKey(owner.Name) && declared?.Length == 2
            && declared[1] is TypeNode.Fqn { Args: null, Name: "kotlin.Float" or "float" })
        {
            plan = MapAwayArguments(arguments, declared, 1, out arguments);
            declared = new[] { declared[0] };
        }
        var signature = CtorArgTypes(declared, arguments.Length);
        var actual = arguments.Select(FactoryClone).ToArray();
        if (viaAlias) CoerceCharSequenceCtorArgs(actual, signature);
        var lowered = new BirNewClr { Type = FactoryType(target), ArgTypes = signature, Args = actual };
        if (viaAlias && refs.IsKotlinHashStorageOwner(owner.Name)) KotlinHashStorageLowering.RequestComparer(lowered);
        // Only a projected CLR declaration keeps the authored native signature.
        if (!viaAlias && node.MemberSignature.HasValue)
            lowered.MemberSignature = node.MemberSignature.Value.Select(FactoryType).ToArray();
        return plan == null ? lowered : MaterialiseMappedArguments(plan, lowered, target);
    }

    static TypeNode[] CtorArgTypes(TypeNode[] declared, int argumentCount)
    {
        if (declared == null)
            throw new InvalidDataException("bir2cir: malformed current `new` node: required `argTypes` is absent or is not an array");
        if (declared.Length != argumentCount)
            throw new InvalidDataException($"bir2cir: malformed current `new` node: `argTypes` count {declared.Length} does not match `args` count {argumentCount}");
        for (var index = 0; index < declared.Length; index++)
            if (declared[index] == null)
                throw new InvalidDataException($"bir2cir: malformed current `new` node: `argTypes[{index}]` is not a structured Type node");
        return declared.Select(FactoryType).ToArray();
    }

    static BirCallBinding[] MapAwayArguments(IBirExpression[] arguments, TypeNode[] types, int keep, out IBirExpression[] slots)
    {
        var bindings = new BirCallBinding[arguments.Length];
        slots = new IBirExpression[keep];
        for (var index = 0; index < arguments.Length; index++)
        {
            var expression = arguments[index];
            var id = CallEvalLowering.FreshBindingId();
            bindings[index] = new BirCallBinding { Id = id, Expr = FactoryExpression(FactoryClone(expression)),
                Stable = ValueStability.IsReReadable((expression as BirGraphNode)?.Tag,
                    expression is BirEnumValue { PhysicalValue.HasValue: true, Underlying.HasValue: true }) };
            if (index < types.Length && types[index] != null) bindings[index].Type = FactoryType(types[index]);
            if (index < keep) slots[index] = new BirBindingReference { Id = id };
        }
        return bindings;
    }

    static IBirExpression MaterialiseMappedArguments(BirCallBinding[] plan, BirGraphValue lowered, TypeNode type)
    {
        var (statements, replacements) = TypedCallEvalLowering.Materialise(plan, new[] { lowered }, "a mapped constructor", isValue: _isValue);
        var result = TypedCallEvalLowering.Substitute(lowered, replacements);
        AssertNoPlanVocabulary(result);
        return statements.Length == 0 ? (IBirExpression)result : new BirValueBlock {
            Type = FactoryType(type), Stmts = statements, Result = FactoryExpression((IBirExpression)result),
        };
    }

    internal static void AssertNoPlanVocabulary(BirGraphValue node)
    {
        node.Visit(value => {
            if (value is BirBindingReference)
                throw new InvalidOperationException("bir2cir: a locally-authored call plan binding was not resolved into its slot — the "
                    + "read is not on the emitted node's eager operand spine, so CallEvalLowering.Materialise "
                    + "could not inline it. Whatever now wraps the kept arguments needs an arm in "
                    + "CallEvalLowering.EagerKinds.");
        });
    }

    static void CoerceCharSequenceCtorArgs(IBirExpression[] arguments, TypeNode[] signature)
    {
        for (var index = 0; index < signature.Length && index < arguments.Length; index++)
        {
            var type = signature[index];
            while (type is TypeNode.Nullable nullable) type = nullable.Of;
            if (type is not TypeNode.Fqn { Name: SharedSyntheticSynthesis.CharSeq or "kotlin.CharSequence" }) continue;
            arguments[index] = new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.LibraryKt"), Method = "toString",
                Sig = new TypeNode[] { new TypeNode.Fqn("object") }, Args = new[] { arguments[index] } };
            signature[index] = new TypeNode.Fqn("kotlin.String");
        }
    }

    internal static IBirExpression TryFactorySubst(BirCallStatic node, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context)
    {
        var arguments = node.Args.OrEmpty();
        var typeArguments = node.TypeArgs.OrDefault();
        var declarationId = node.DeclarationId.OrDefault();
        _localFactories.TryGetValue(declarationId ?? "", out var local);
        string collectionKind, arrayKind, elementHint;
        if (local != null)
            (collectionKind, arrayKind, elementHint) = (local.CollectionKind, local.ArrayKind, local.ArrayElementHint);
        else if (node.DeclarationReferencedFactory.OrDefault())
        {
            if (declarationId == null || !refs.TryDeclarationFactory(declarationId, out collectionKind, out arrayKind, out elementHint))
                throw new InvalidOperationException($"bir2cir: selected referenced declaration identity '{declarationId}' lost its factory binding");
        }
        else return null;
        if (collectionKind != null)
        {
            var elements = FactoryElems(arguments, local?.VarargPositions ?? refs.DeclarationFactoryVarargPositions(declarationId));
            if (elements == null || typeArguments == null) return null;
            var frame = local != null ? local.Frame : refs.NullableMethodFrame(declarationId);
            var element = typeArguments[frame?.SourcePosition(0) ?? 0];
            if (element == null) return null;
            if (collectionKind == "set")
            {
                var literal = KotlinHashStorageLowering.SetLiteral(element, elements.Select(FactoryClone));
                if (node.Sty.HasValue) literal.Sty = FactoryType(node.Sty.Value);
                return literal;
            }
            return new BirNewList { Elem = FactoryType(element), Elems = elements.Select(FactoryClone).ToArray(),
                Sty = node.Sty.HasValue ? OptionalField<TypeNode>.Of(FactoryType(node.Sty.Value)) : default };
        }
        if (arrayKind == null) return null;
        if (arrayKind == "sized")
        {
            var element = ArrayConstructionLowering.ArrayElementOf(node.Ret.OrDefault());
            return element == null || arguments.Length < 1 ? null : new BirNewArraySized {
                Elem = CanonicalArrayElem(element), Size = FactoryExpression(FactoryClone(arguments[0])),
            };
        }
        var wrapper = arguments.Length == 1 ? arguments[0] as BirNewArray : null;
        var resultElement = ArrayConstructionLowering.ArrayElementOf(node.Sty.OrDefault());
        var arrayElement = resultElement ?? (typeArguments is { Length: > 0 } ? typeArguments[0] : null)
            ?? wrapper?.Elem.OrDefault() ?? (elementHint == null ? null : new TypeNode.Fqn(elementHint));
        if (arrayElement == null) return null;
        var canonical = CanonicalArrayElem(arrayElement);
        if (wrapper == null && arguments.Length == 1)
        {
            var spread = arguments[0] is BirSpreadConcat existing ? (BirSpreadConcat)existing.DeepClone()
                : new BirSpreadConcat { Parts = new[] { new BirSpreadPart { Spread = true,
                    E = FactoryExpression(FactoryClone(arguments[0])) } } };
            spread.Elem = FactoryType(canonical);
            NormalizeSpreadParts(spread, canonical, context);
            return spread;
        }
        return new BirNewArray { Elem = FactoryType(canonical), Elems = (wrapper?.Elems.OrEmpty() ?? Array.Empty<IBirExpression>()).Select(FactoryClone).ToArray() };
    }

    static IEnumerable<IBirExpression> FactoryElems(IBirExpression[] arguments, int[] varargPositions)
    {
        if (varargPositions.Length == 0) return arguments;
        if (varargPositions.Length == 1 && varargPositions[0] == 0 && arguments.Length == 1 && arguments[0] is BirNewArray wrapper)
            return wrapper.Elems.OrEmpty();
        return null;
    }

    internal static BirSpreadConcat TransformSpread(BirSpreadConcat spread, CollectionHelperBinding.CollectionSourceContext context)
    {
        var element = CanonicalArrayElem(spread.Elem.OrDefault());
        spread.Elem = element == null ? OptionalField<TypeNode>.Null : OptionalField<TypeNode>.Of(element);
        NormalizeSpreadParts(spread, element, context);
        return spread;
    }

    static void NormalizeSpreadParts(BirSpreadConcat spread, TypeNode target, CollectionHelperBinding.CollectionSourceContext context)
    {
        foreach (var part in spread.Parts.OrEmpty())
        {
            if (!part.E.HasValue) continue;
            var operand = part.E.Value;
            var source = BirGraphStaticType.Surface(operand, context.Variables);
            if (part.Spread.OrDefault())
            {
                if (source is TypeNode.Fqn { Name: "System.Collections.Generic.IEnumerable", Args.Length: 1 } enumerable
                    && enumerable.Args[0].Equals(target)) continue;
                part.E = ValueElementIterableCoercion.CastElements(operand, target);
            }
            else
            {
                var physicalSource = source == null ? null : NullableGenericErasure.EraseNullableTv(source, _isValue);
                if (physicalSource != null && physicalSource.Equals(target)) continue;
                part.E = new BirCast { Type = FactoryType(target), E = FactoryExpression(FactoryClone(operand)) };
            }
        }
    }

    internal static IBirExpression CompilerMetadataIntrinsicValue(string intrinsic, ReferenceMetadataIndex refs)
    {
        var definitions = intrinsic switch {
            "dotkt.collectionMapStorageTypes" => refs.KotlinMapStorageDefinitions(),
            "dotkt.equalityStorage.List" => refs.KotlinEqualityStorageDefinitions("List"),
            "dotkt.equalityStorage.Set" => refs.KotlinEqualityStorageDefinitions("Set"),
            "dotkt.equalityStorage.Map" => refs.KotlinEqualityStorageDefinitions("Map"),
            _ => null,
        };
        return definitions == null ? null : new BirNewArray { Elem = new TypeNode.Fqn("System.Type"),
            Elems = definitions.Select(name => (IBirExpression)new BirClassReference { Type = new TypeNode.Fqn(name) }).ToArray() };
    }
}
