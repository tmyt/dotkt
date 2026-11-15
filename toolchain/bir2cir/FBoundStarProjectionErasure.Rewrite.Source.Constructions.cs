using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static BirGraphValue BindStarInnerConstructionSource(BirGraphValue node, IReadOnlySet<int> existentialTypes,
        IReadOnlySet<int> existentialMethods, SourceRewriteState state)
    {
        if (node is not BirNew construction || construction.Type.OrDefault() is not TypeNode.Fqn innerType
            || !construction.Args.HasValue || construction.Args.Value.Length == 0) return node;
        var arguments = construction.Args.Value;
        // A present empty declaration signature is authoritative; an absent or
        // null vector may use the selected argument signature instead.
        var signature = construction.MemberSignature.HasValue ? construction.MemberSignature.Value : construction.ArgTypes.OrDefault();
        if (signature is not { Length: > 0 } || signature[0] is not TypeNode.Fqn selectedOuter) return node;
        var suppliedOuter = SourceCarrierExpressionType(arguments[0] as BirGraphValue);
        var exact = arguments[0] is BirThis or BirNew || SourceExactOuter((BirGraphValue)arguments[0]);
        var variant = !exact && state.Projection.RequiresKotlinValueCarrier(selectedOuter);
        if (!variant && (suppliedOuter == null || !ContainsExistential(suppliedOuter, existentialTypes, existentialMethods))) return node;
        var context = state.Realization;
        var innerDefinition = state.Definition(innerType.Name);
        var local = innerDefinition?.Mods.OrDefault()?.Inner.OrDefault() == true;
        var referenced = state.References.TryInnerSemanticOwner(innerType.Name, out var referencedOuter);
        if (!local && !referenced) return node;

        string physicalOwner;
        string physicalMethod;
        TypeNode[] parameters;
        TypeNode[] declarationParameters = null;
        TypeNode result;
        TypeNode[] typeArguments;
        SourceReferenceContract reference = null;
        TypeNode.Fqn referenceOwner = null;
        if (local)
        {
            var declaredOuter = innerDefinition.SemanticOwner.OrDefault();
            if (declaredOuter == null || !LocalDeclarationReachesSource(selectedOuter.Name, declaredOuter, state)
                || !state.Owners.TryGetValue(declaredOuter, out var outer)
                || !state.Owners.TryGetValue(innerType.Name, out var inner) || !outer.Needed || !inner.Needed)
                throw new InvalidOperationException($"bir2cir: local inner construction '{innerType.Name}' has no exact existential owner '{selectedOuter.Name}<*>'");
            var identity = construction.ConstructorDeclarationId.OrDefault();
            var matches = innerDefinition.Ctors.OrEmpty().Where(constructor => constructor.Params.OrEmpty().Length == arguments.Length
                && constructor.DeclarationId.OrDefault() == identity).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(
                $"bir2cir: star-projected inner construction '{inner.Name}' resolves to {matches.Length} exact local constructors");
            var original = context.Bindings.GetCapturedSource(matches[0]);
            var factory = context.Declarations.InnerFactories(outer)[original];
            // The factory is an authored physical declaration. Its selected
            // signature stays live until this call binds; only original method
            // matching uses the independently frozen source signatures.
            declarationParameters = factory.Slot.Params.OrEmpty()
                .Select(parameter => BirGraphValue.CopyType(FunctionSignatureIdentity.SignatureType(parameter))).ToArray();
            physicalOwner = outer.ErasedName;
            physicalMethod = factory.Slot.Name.OrDefault();
            parameters = factory.Slot.Params.OrEmpty().Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray();
            result = factory.Slot.Ret.OrDefault();
            typeArguments = InnerFactoryCallTypeArgumentsSource(factory, innerType.Args ?? Array.Empty<TypeNode>());
            reference = factory.ReferenceContract;
            if (reference != null)
                referenceOwner = (TypeNode.Fqn)SubstituteDeclarationTypeArguments(reference.Owner,
                    innerType.Args.Take(inner.OuterCount).ToArray(), Array.Empty<TypeNode>());
        }
        else
        {
            if (!state.References.TryExistentialInnerConstructorFactory(referencedOuter, innerType,
                construction.ConstructorDeclarationId.OrDefault(), out physicalOwner, out physicalMethod, out parameters,
                out result, out typeArguments, out referenceOwner, out var invoke))
                throw new InvalidOperationException($"bir2cir: referenced inner construction '{innerType.Name}' through star-projected outer "
                    + $"'{referencedOuter}<*>' has no exact existential constructor factory");
            if (referenceOwner != null)
                reference = new SourceReferenceContract(referenceOwner, new BirFunctionDeclaration {
                    Name = physicalMethod, Params = parameters.Select((type, index) => new BirParameter {
                        Name = "p" + index, Type = BirGraphValue.CopyType(type),
                    }).ToArray(), Ret = BirGraphValue.CopyType(result),
                }, invoke);
        }
        var replacement = new BirCallInstance {
            OwnerType = new TypeNode.Fqn(physicalOwner), Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(arguments[0]), Method = physicalMethod,
            Sig = parameters.Select(BirGraphValue.CopyType).ToArray(), Ret = BirGraphValue.CopyType(result),
            Args = arguments.Skip(1).ToArray(),
        };
        if (typeArguments is { Length: > 0 })
        {
            replacement.TypeArgs = typeArguments.Select(BirGraphValue.CopyType).ToArray();
            replacement._innerFactoryArguments = true;
        }
        if (declarationParameters != null) replacement.CalleeParams = declarationParameters;
        return reference != null ? (BirGraphValue)BindReferenceContractSource(replacement, reference, Array.Empty<TypeNode>(),
            false, null, null, referenceOwner) : replacement;
    }

    static bool LocalDeclarationReachesSource(string from, string target, SourceRewriteState state)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!seen.Add(name)) continue;
            if (name == target) return true;
            if (state.Definition(name) is not { } definition) continue;
            if (definition.Base.OrDefault() is TypeNode.Fqn parent) pending.Enqueue(parent.Name);
            foreach (var edge in definition.Interfaces.OrEmpty().OfType<TypeNode.Fqn>()) pending.Enqueue(edge.Name);
        }
        return false;
    }

    static TypeNode[] InnerFactoryCallTypeArgumentsSource(SourceInnerFactory factory, IReadOnlyList<TypeNode> arguments)
    {
        var inner = factory.Inner;
        if (arguments.Count != inner.Arity) throw new InvalidOperationException(
            $"inner construction '{inner.Name}' has {arguments.Count} type arguments; expected {inner.Arity}");
        var captured = inner.OuterCount;
        for (var index = 0; index < factory.TypeArgs.Length; index++)
            if (factory.TypeArgs[index] == InnerFactoryBottomTypeArgument
                && arguments[captured + index] is not TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" })
                throw new InvalidOperationException($"inner construction '{inner.Name}' through a star-projected outer must use kotlin.Nothing "
                    + $"for owner-dependent type argument {index}");
        return factory.TypeArgs.Select((position, index) => (position, index)).Where(row => row.position >= 0)
            .OrderBy(row => row.position).Select(row => BirGraphValue.CopyType(arguments[captured + row.index])).ToArray();
    }

    static BirGraphValue LowerProjectedConstructionSource(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not BirNew construction || construction.Type.OrDefault() is not TypeNode.Fqn { Args: { Length: > 0 } } owner
            || !owner.Args.Any(ContainsExistentialProjection) || !construction.MemberSignature.HasValue
            || !construction.Args.HasValue || construction.MemberSignature.Value.Length != construction.Args.Value.Length) return node;
        var signature = construction.MemberSignature.Value;
        if (signature.Any(type => type == null) || !ConstructionHasHiddenWitnessSource(owner, state)) return node;
        var result = state.Projection.Rewrite(owner);
        if (result is TypeNode.Fqn projected && projected.Name == owner.Name) return node;
        if (signature.Any(ContainsManagedReference)) throw new NotSupportedException(
            $"bir2cir: projected constructor `{owner.Name}` has a ref/out or pointer parameter; "
            + "the object[] reflection ABI cannot preserve managed-reference aliasing");
        var locals = state.Realization.SourceDefinitions.Keys.ToHashSet(StringComparer.Ordinal);
        var text = new TypeNode.Fqn("kotlin.String");
        var systemType = new TypeNode.Fqn("System.Type");
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var invocation = new BirCallStatic {
            Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"), Method = "starProjectionConstruct",
            Sig = new TypeNode[] { systemType, new TypeNode.Array(text), new TypeNode.Array(systemType), new TypeNode.Array(any) },
            Ret = any,
            Args = new IBirExpression[] {
                new BirClassReference { Type = new TypeNode.Fqn(owner.Name) },
                new BirNewArray { Elem = text, Elems = signature.Select(type => (IBirExpression)new BirConstant {
                    Type = text, Value = new BirLiteral.TextLiteral(ProjectedConstructorTypeKey(state.Projection.Rewrite(type), state.References, locals)),
                }).ToArray() },
                new BirNewArray { Elem = systemType, Elems = owner.Args.Select(argument => (IBirExpression)new BirClassReference {
                    Type = ProjectedConstructionFallback(argument, state.References, locals),
                }).ToArray() },
                new BirNewArray { Elem = any, Elems = construction.Args.Value.ToArray() },
            },
        };
        ForeignStarProjectionBinding.RequireRuntimeFallback();
        return IsObjectish(result) ? invocation : new BirCast {
            Type = BirGraphValue.CopyType(result), E = OptionalField<IBirExpression>.Of(invocation),
        };
    }

    static bool ConstructionHasHiddenWitnessSource(TypeNode.Fqn owner, SourceRewriteState state)
    {
        var frame = state.Owners.TryGetValue(owner.Name, out var definition) ? state.Projection.OwnerFrame(definition)
            : state.References.NullableTypeFrames.GetValueOrDefault(owner.Name);
        return owner.Args.Where((argument, index) => (frame == null || frame.PhysicalSlot(index).Representation
                is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)
            && ContainsExistentialProjection(argument)
            && (state.Realization.NativeProofs.ExactTypeArgument(owner.Name, index)
                || StripSourceNullability(argument) is not TypeNode.Fqn nominal
                || !state.Projection.TryExistentialCarrier(nominal.Name, out var carrier)
                || StripSourceNullability(state.Projection.Rewrite(argument)) != new TypeNode.Fqn(carrier))).Any();
    }
}
