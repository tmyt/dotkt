using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SharedSyntheticSynthesis
{
    // Source declarations remain the actual owning occurrences. This pass only
    // appends representation declarations and consumes the explicit cell registry.
    public static void Apply(BirFileDeclaration file, ReferenceMetadataIndex refs,
        ICollection<CellStorage> sharedCells = null)
    {
        if (!file.Types.HasValue) file.Types = Array.Empty<IBirTypeDeclaration>();
        var present = file.Types.Value.Where(type => type.Name.HasValue)
            .Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
        if (file.RefTypes.HasValue)
        {
            var specs = new Dictionary<string, RefCellSpec>(StringComparer.Ordinal);
            foreach (var declaration in file.RefTypes.Value)
                if (declaration.Name.HasValue && declaration.Elem.HasValue)
                {
                    var spec = new RefCellSpec(declaration);
                    spec.Bind(declaration.TypeParams.Value);
                    specs.Add(spec.Name, spec);
                }

            var live = CollectCellUses(file);
            foreach (var spec in specs.Values)
            {
                // A possible cell in an inline hint is not a live heap allocation.
                if (!live.Contains(spec.Name)
                    && FieldLegality.Classify(spec.Elem, refs.IsByRefLikeFqn, out _) == FieldRejection.ByRefLike)
                    continue;
                if (!present.Add(spec.Name)) continue;
                var cell = BuildRefCell(spec);
                var position = file.Pos.OrDefault();
                ClosureSynthesis.RecordCaptureLegality(spec.Name,
                    cell.Fields.Value.Select(field => (field.Name.Value, field.Type.Value)),
                    FieldLegality.PosPrefix(position?.F.OrDefault(), position?.L.HasValue == true ? position.L.Value : null),
                    "<file>", refs);
                file.Types = file.Types.Value.Append(cell).ToArray();
                sharedCells?.Add(new CellStorage(spec.Name, "v", 0));
            }
            // The selected constructor is the declaration authored here, not a
            // use-site approximation of its physical storage parameter.
            BindConstructions(file);
            file.RefTypes = default;

            void BindConstructions(BirGraphValue node)
            {
                if (node is BirRefTypeDeclaration) return;
                if (node is BirNew { Type.Value: TypeNode.Fqn owner } construction
                    && specs.TryGetValue(owner.Name, out var spec))
                    construction.MemberSignature = new[] { spec.ElementInFrame() };
                node.VisitChildren(BindConstructions);
            }
        }

        DropSyntheticTypeArgs(file);
        if (!present.Contains(CharSeq) && ReferencesCharSequence(file))
            file.Types = file.Types.Value.Append(BuildCharSequence()).ToArray();
    }

    readonly record struct TvKey(string Scope, int Index);

    sealed class RefCellSpec
    {
        internal string Name { get; }
        internal TypeNode Elem { get; }
        internal OptionalField<SourceTypeFacts> KotlinFacts { get; }
        internal OptionalField<bool> NativeLocation { get; }
        internal List<TvKey> Free { get; } = new();
        internal Dictionary<TvKey, TypeNode[]> Bounds { get; } = new();

        internal RefCellSpec(BirRefTypeDeclaration declaration)
        {
            Name = declaration.Name.Value;
            Elem = BirGraphValue.CopyType(declaration.Elem.Value);
            KotlinFacts = declaration.KotlinSupertypesPre.HasValue ? declaration.KotlinSupertypesPre : default;
            NativeLocation = declaration._nativeLocation;
            AddFreeTvs(Elem, Free);
            Sort();
        }

        internal void Bind(BirTypeParameter[] parameters)
        {
            for (var index = 0; index < parameters.Length; index++)
                if (!Free.Contains(new TvKey("type", index))) Free.Add(new("type", index));
            if (KotlinFacts.OrDefault()?.NullableFrame is { } frame && frame.PhysicalArity != parameters.Length)
                throw new InvalidOperationException("Ref-cell frame does not match its physical parameters");
            for (var index = 0; index < Free.Count; index++)
            {
                var key = Free[index];
                if (key.Scope != "type" || key.Index < 0 || key.Index >= parameters.Length)
                    throw new InvalidOperationException(
                        $"ref-cell `{Name}` cannot resolve declared {key.Scope} type parameter #{key.Index}");
                var bounds = parameters[key.Index].Constraints.OrEmpty().Select(BirGraphValue.CopyType).ToArray();
                Bounds.Add(key, bounds);
                foreach (var bound in bounds) AddFreeTvs(bound, Free);
            }
            Sort();
        }

        void Sort() => Free.Sort((left, right) => {
            var scope = StringComparer.Ordinal.Compare(left.Scope, right.Scope);
            return scope != 0 ? scope : left.Index.CompareTo(right.Index);
        });

        internal TypeNode ElementInFrame() => RemapTvs(Elem, Positions());
        internal Dictionary<TvKey, int> Positions() => Free.Select((key, index) => (key, index))
            .ToDictionary(entry => entry.key, entry => entry.index);
    }

    static void AddFreeTvs(TypeNode type, List<TvKey> free)
        => BirGraphNameFields.VisitTypeNodes(type, node => {
            if (node is not TypeNode.Tv variable) return;
            var key = new TvKey(variable.Scope, variable.I);
            if (!free.Contains(key)) free.Add(key);
        });

    static TypeNode RemapTvs(TypeNode type, IReadOnlyDictionary<TvKey, int> positions)
        => TypedExecutableFrames.RewriteType(type, variable => positions.TryGetValue(new(variable.Scope, variable.I), out var index)
            ? new TypeNode.Tv("type", index) : null);

    static BirClassDefinition BuildRefCell(RefCellSpec spec)
    {
        var positions = spec.Positions();
        var parameters = spec.Free.Select((key, index) => {
            var parameter = new BirTypeParameter { Name = "T" + index };
            if (spec.Bounds[key].Length != 0)
                parameter.Constraints = spec.Bounds[key].Select(bound => RemapTvs(bound, positions)).ToArray();
            return parameter;
        }).ToArray();
        var self = new TypeNode.Fqn(spec.Name, spec.Free.Count == 0 ? null : Enumerable.Range(0, spec.Free.Count)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        return new BirClassDefinition {
            Name = spec.Name, Kind = "class", Generated = true, Abstract = false, Vis = "public",
            TypeParams = parameters, Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(),
            KotlinSupertypesPre = spec.KotlinFacts,
            Fields = new[] { new BirFieldDeclaration {
                Name = "v", Type = spec.ElementInFrame(), _nativeLocation = spec.NativeLocation,
            } },
            Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "v", Type = spec.ElementInFrame() } },
                BaseArgs = OptionalField<IBirExpression[]>.Null, ThisArgs = OptionalField<IBirExpression[]>.Null, Vis = "public",
                Body = new IBirStatement[] { new BirSetField {
                    OwnerType = self, Recv = new BirThis(), Name = "v", Value = new BirLocal { Name = "v" },
                } },
            } },
            Methods = Array.Empty<BirFunctionDeclaration>(),
        };
    }

    static BirInterfaceDefinition BuildCharSequence()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirFunctionDeclaration Method(string name, TypeNode result, params BirParameter[] parameters) => new() {
            Name = name, Static = false, Override = false, Virtual = true, Abstract = true,
            ObjectOverride = false, Vis = "public", Params = parameters, Ret = result, Body = Array.Empty<IBirStatement>(),
        };
        var length = Method("length", integer);
        length.PropertyName = "length";
        length.PropertyAccessor = "get";
        length.PropertyAssociation = "charsequence-length";
        return new BirInterfaceDefinition {
            Name = CharSeq, Kind = "interface", Generated = true, Base = OptionalField<TypeNode>.Null,
            Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Methods = new[] {
                length,
                Method("get", new TypeNode.Fqn("kotlin.Char"), new BirParameter { Name = "index", Type = integer }),
                Method("subSequence", new TypeNode.Fqn(CharSeq), new BirParameter { Name = "startIndex", Type = integer },
                    new BirParameter { Name = "endIndex", Type = integer }),
            },
            Properties = new[] { new BirPropertyDeclaration {
                Name = "length", Type = integer, KotlinAccessors = new[] { "get" }, PropertyAssociation = "charsequence-length",
            } },
            KotlinType = new TypeNode.Fqn("kotlin.CharSequence"),
        };
    }

    static HashSet<string> CollectCellUses(BirFileDeclaration file)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Walk(file);
        return names;
        void Walk(BirGraphValue node)
        {
            if (node is BirRefTypeDeclaration) return;
            node.VisitOwnTypeFields((field, type) => {
                // This is a possible capture-cell hint, not an allocation/use.
                if (field == "sharedCellType") return;
                BirGraphNameFields.VisitTypeNodes(type, typeNode => {
                    if (typeNode is TypeNode.Fqn named) names.Add(named.Name);
                });
            });
            node.VisitChildren(Walk);
        }
    }

    static bool ReferencesCharSequence(BirFileDeclaration file)
    {
        var found = false;
        foreach (var method in file.Methods.OrEmpty()) Walk(method);
        foreach (var field in file.Fields.OrEmpty()) Walk(field);
        foreach (var type in file.Types.OrEmpty()) Walk((BirGraphValue)type);
        return found;
        void Walk(BirGraphValue node)
        {
            if (found) return;
            node.VisitStringFields(value => found |= value == CharSeq);
            node.VisitChildren(Walk);
        }
    }

    public static void DropSyntheticTypeArgs(BirGraphValue node)
    {
        node.ClearSyntheticFrameFacts();
        node.VisitChildren(DropSyntheticTypeArgs);
    }
}
