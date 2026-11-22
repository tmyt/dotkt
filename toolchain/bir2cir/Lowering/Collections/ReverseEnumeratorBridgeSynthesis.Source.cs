using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReverseEnumeratorBridgeSynthesis
{
    internal static IReadOnlyList<BirClassDefinition> ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs,
        Action<BirFileDeclaration, IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> publishMethods,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> publishTypes,
        IReadOnlyDictionary<string, SlotReservation> reservations = null)
    {
        var definitions = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var type in roots.SelectMany(root => root.Types.OrEmpty()))
            if (type.Name.HasValue) declarations[type.Name.Value] = type;
        BirFileDeclaration adapterHost = null;
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
            {
                if (type.Kind.OrDefault() is not ("class" or "interface") || string.IsNullOrEmpty(type.Name.OrDefault())
                    || !type.Methods.HasValue || !definitions.TryGetValue(type.Name.Value, out var definition)) continue;
                if (Plan(definition, definitions, refs, declarations, reservations) is not { } plan)
                {
                    if (NativeRawForwarder(type, definition, definitions, refs) is { } raw)
                    {
                        type.Methods = type.Methods.Value.Append(raw).ToArray();
                        if (type.Kind.Value == "interface") raw.ClrInterfaceSlotBridge = true;
                        publishMethods(root, type, [raw]);
                    }
                    continue;
                }
                var nameTaken = type.Methods.Value.Any(method => method.Name.OrDefault() == GetEnumeratorName
                    && !method.Static.OrDefault() && method.Params.HasValue && method.Params.Value.Length == 0
                    && method.TypeParams.OrEmpty().Length == 0);
                var genericName = nameTaken ? AliasGetEnumeratorName : GetEnumeratorName;
                var (iterator, element) = plan;
                var iteratorCall = new BirCallInstance {
                    OwnerType = BirGraphValue.CopyType(iterator.Owner), Virtual = true,
                    Recv = E(new BirThis()), Method = iterator.Name, Sig = Array.Empty<TypeNode>(),
                    Ret = BirGraphValue.CopyType(iterator.Return), Args = Array.Empty<IBirExpression>(),
                };
                IBirExpression result;
                if (NodeType.IsNothing(iterator.SemanticReturn))
                {
                    // The face supplies E, but there is no iterator value to adapt.
                    iteratorCall.Sty = iterator.SemanticReturn;
                    result = MaterializedExecutable.ReturnedCall(iteratorCall);
                }
                else
                {
                    result = new BirNew {
                        Type = Constructed(AdapterName, BirGraphValue.CopyType(element)),
                        ArgTypes = new[] { KotlinIteratorPhysicalProtocol.Carrier(refs) },
                        Args = new IBirExpression[] { iteratorCall }, LocalCtorIndex = 0,
                    };
                    adapterHost ??= root;
                }
                var generic = SourceMethod(genericName, nameTaken ? "private" : "public", Constructed(IEnumeratorT, element),
                    new IBirStatement[] { SourceReturn(result) });
                if (nameTaken) generic.Generated = true;
                generic.ClrInterfaceImpls = new[] { SourceDescriptor(Constructed(IEnumerableT, element),
                    GetEnumeratorName, Constructed(IEnumeratorT, element)) };
                var nonGeneric = RawForwarder(SelfOwner(type.Name.Value, TypeParameterFrame.Count(type)), genericName,
                    Constructed(IEnumeratorT, element));
                var appended = new[] { generic, nonGeneric };
                type.Methods = type.Methods.Value.Concat(appended).ToArray();
                publishMethods(root, type, appended);
            }
        if (adapterHost == null) return Array.Empty<BirClassDefinition>();
        var adapter = AdapterSource(AdapterName, refs);
        adapterHost.Types = adapterHost.Types.OrEmpty().Append<IBirTypeDeclaration>(adapter).ToArray();
        publishTypes(adapterHost, new IBirTypeDeclaration[] { adapter });
        return new[] { adapter };
    }

    // Fresh authored output only: no source declaration is represented by this
    // template. Callers attach the actual returned record before publication.
    internal static BirClassDefinition AdapterSource(string name, ReferenceMetadataIndex refs)
    {
        var sourceElement = Tv0;
        var targetElement = Tv0;
        var wrapped = KotlinIteratorPhysicalProtocol.Carrier(refs);
        TypeNode Self() => Constructed(name, Tv0);
        BirFieldGet Field(string field) => new() { OwnerType = Self(), Recv = E(new BirThis()), Name = field };
        IBirExpression Wrapped(string member, TypeNode result) => KotlinIteratorPhysicalProtocol.CallSource(
            refs, Field("_it"), member, sourceElement, result);
        var boolean = new TypeNode.Fqn("System.Boolean");
        var rawElement = new TypeNode.Fqn("System.Object");
        var moveNext = SourceMethod("MoveNext", "public", boolean, new IBirStatement[] {
            new BirIf { Branches = new[] { new BirBranch {
                Cond = E(Wrapped("hasNext", boolean)), Body = new IBirStatement[] {
                    new BirSetField { OwnerType = Self(), Recv = E(new BirThis()), Name = "_cur",
                        Value = E(FunctionValueRepresentation.RuntimeViewCall(Wrapped("next", rawElement), arity: null)) },
                    SourceReturn(new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(true) }),
                },
            } } },
            SourceReturn(new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) }),
        });
        moveNext.ClrInterfaceImpls = new[] { SourceDescriptor(new TypeNode.Fqn(IEnumerator), "MoveNext", boolean) };
        var current = SourceMethod("get_Current", "public", targetElement, new IBirStatement[] {
            SourceReturn(new BirCast { Type = targetElement, E = E(Field("_cur")) }) });
        current.SpecialName = true;
        current.ClrInterfaceImpls = new[] { SourceDescriptor(Constructed(IEnumeratorT, targetElement), "get_Current", targetElement) };
        var rawCurrent = SourceMethod(NonGenericCurrentName, "private", rawElement,
            new IBirStatement[] { SourceReturn(Field("_cur")) });
        rawCurrent.SpecialName = true; rawCurrent.Generated = true;
        rawCurrent.ClrInterfaceImpls = new[] { SourceDescriptor(new TypeNode.Fqn(IEnumerator), "get_Current", rawElement) };
        var reset = SourceMethod("Reset", "public", new TypeNode.Fqn("void"), new IBirStatement[] {
            new BirThrow { Value = E(new BirNew { Type = new TypeNode.Fqn("System.NotSupportedException"),
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() }) },
        });
        reset.ClrInterfaceImpls = new[] { SourceDescriptor(new TypeNode.Fqn(IEnumerator), "Reset", new TypeNode.Fqn("void")) };
        var dispose = SourceMethod("Dispose", "public", new TypeNode.Fqn("void"), Array.Empty<IBirStatement>(), terminates: false);
        dispose.ClrInterfaceImpls = new[] { SourceDescriptor(new TypeNode.Fqn(IDisposable), "Dispose", new TypeNode.Fqn("void")) };
        return new BirClassDefinition {
            Name = name, Kind = "class", Generated = true, Abstract = false, Final = true,
            BeforeFieldInit = true, Vis = "internal", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = OptionalField<TypeNode>.Null,
            Interfaces = new TypeNode[] { Constructed(IEnumeratorT, targetElement), new TypeNode.Fqn(IEnumerator), new TypeNode.Fqn(IDisposable) },
            Fields = new[] {
                new BirFieldDeclaration { Name = "_it", Type = BirGraphValue.CopyType(wrapped), Vis = "private", InitOnly = true },
                new BirFieldDeclaration { Name = "_cur", Type = rawElement, Vis = "private" },
            },
            Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "source", Type = BirGraphValue.CopyType(wrapped) } },
                BaseArgs = OptionalField<IBirExpression[]>.Null, ThisArgs = OptionalField<IBirExpression[]>.Null, Vis = "public",
                Body = new IBirStatement[] { new BirSetField { OwnerType = Self(), Recv = E(new BirThis()), Name = "_it",
                    Value = E(new BirLocal { Name = "source" }) } },
            } },
            Methods = new[] { moveNext, current, rawCurrent, reset, dispose },
        };
    }

    static BirFunctionDeclaration RawForwarder(TypeNode owner, string member, TypeNode result)
    {
        var raw = SourceMethod(NonGenericBridgeName, "private", new TypeNode.Fqn(IEnumerator),
            [SourceReturn(new BirCallInstance {
                OwnerType = BirGraphValue.CopyType(owner), Virtual = true, Recv = E(new BirThis()),
                Method = member, Sig = Array.Empty<TypeNode>(), Ret = BirGraphValue.CopyType(result), Args = Array.Empty<IBirExpression>(),
            })]);
        raw.Generated = true;
        raw.ClrInterfaceImpls = new[] { SourceDescriptor(new TypeNode.Fqn(IEnumerable), GetEnumeratorName, new TypeNode.Fqn(IEnumerator)) };
        return raw;
    }

    // A Kotlin alias can expose just the generic enumerator. Its selected CLR slot also owes
    // IEnumerable.GetEnumerator; call the generic slot rather than guessing a Kotlin body name.
    static BirFunctionDeclaration NativeRawForwarder(IBirTypeDeclaration type, SupertypeGraph.Def definition,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs)
    {
        if (refs == null) return null;
        var slots = type.Methods.Value.Where(m => !m.Abstract.OrDefault()).SelectMany(m => m.ClrInterfaceImpls.OrEmpty())
            .Where(d => d.Member.OrDefault() == GetEnumeratorName && d.Params.OrEmpty().Length == 0
                && d.Arity.OrDefault() == 0).ToArray();
        if (slots.Any(d => d.Owner.OrDefault() is TypeNode.Fqn raw && Bare(raw.Name) == IEnumerable)) return null;
        var generic = slots.Where(d => d.Owner.OrDefault() is TypeNode.Fqn { Args.Length: 1 } face
            && Bare(face.Name) == IEnumerableT && d.Ret.OrDefault() is TypeNode.Fqn { Args.Length: 1 } result
            && Bare(result.Name) == IEnumeratorT && ClrMemberResolution.SameInterfaceSlotType(face.Args[0], result.Args[0]))
            .DistinctBy(d => d.Owner.Value).ToArray();
        if (generic.Length != 1) return null;
        string PhysicalName(TypeNode.Fqn spec) => Bare(refs.ExactReflectedOwner(spec.Name, spec.Args?.Length ?? 0) ?? spec.Name);
        if (definition.Base != null && SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = definition.Base }, definitions, refs)
            .Any(e => e.isInterface && PhysicalName(e.spec) is IEnumerable or IEnumerableT)) return null;
        if (type.Methods.Value.Any(m => m.Name.OrDefault() == GetEnumeratorName && !m.Static.OrDefault()
            && (m.Virtual.OrDefault() || m.Override.OrDefault())
            && m.Vis.OrDefault() == "public" && m.Params.OrEmpty().Length == 0 && m.TypeParams.OrEmpty().Length == 0
            && m.Ret.OrDefault() is TypeNode.Fqn ret && PhysicalName(ret) == IEnumerator)) return null;
        return RawForwarder(generic[0].Owner.Value, GetEnumeratorName, generic[0].Ret.Value);
    }

    static OptionalField<IBirExpression> E(IBirExpression expression) => OptionalField<IBirExpression>.Of(expression);
    static BirReturn SourceReturn(IBirExpression expression) => new() { Value = E(expression) };
    static BirMethodImpl SourceDescriptor(TypeNode owner, string member, TypeNode result) => new() {
        Owner = BirGraphValue.CopyType(owner), Member = member, Arity = 0, Params = Array.Empty<TypeNode>(), Ret = BirGraphValue.CopyType(result),
    };
    static BirFunctionDeclaration SourceMethod(string name, string visibility, TypeNode result, IBirStatement[] body, bool terminates = true)
    {
        var method = new BirFunctionDeclaration { Name = name, Static = false, Override = false, Virtual = true,
            Abstract = false, ObjectOverride = false, Vis = visibility, Params = Array.Empty<BirParameter>(),
            Ret = BirGraphValue.CopyType(result), Body = body };
        if (terminates) method.BodyTerminates = true;
        return method;
    }
}
