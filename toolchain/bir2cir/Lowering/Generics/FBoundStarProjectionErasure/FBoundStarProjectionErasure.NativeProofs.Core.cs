using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        readonly ReferenceMetadataIndex references;
        readonly CurrentDeclarationBindings bindings;
        readonly IReadOnlyDictionary<string, DeclarationBinding> definitions;
        readonly IReadOnlyDictionary<string, SupertypeGraph.Def> sourceSupertypes;
        readonly Dictionary<DeclarationBinding, Dictionary<int, List<TypeNode>>> rows = new();
        readonly Dictionary<string, DeclarationBinding> methods;
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> foreignTypes = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> foreignMethods = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> lateLocalTypes = new(StringComparer.Ordinal);

        internal Dictionary<int, List<TypeNode>> ReadRows(IReadOnlyList<BirTypeParameter> parameters, bool nativeAncestors,
            NullableRepresentationFrame frame = null, bool physicalDeclaration = false,
            Func<int, TypeNode, bool> projectsBound = null) => ReadRows(parameters?.Select(parameter =>
                parameter.Constraints.OrEmpty()).ToArray(), nativeAncestors, frame, physicalDeclaration, projectsBound);

        Dictionary<int, List<TypeNode>> ReadRows(IReadOnlyList<TypeNode[]> parameters, bool nativeAncestors,
            NullableRepresentationFrame frame = null, bool physicalDeclaration = false,
            Func<int, TypeNode, bool> projectsBound = null)
        {
            var result = new Dictionary<int, List<TypeNode>>();
            for (var index = 0; index < (parameters?.Count ?? 0); index++)
            {
                if (frame != null && frame.PhysicalSlot(index).Representation
                    is not (NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)) continue;
                var bounds = parameters[index].OfType<TypeNode.Fqn>()
                    .Where(bound => projectsBound?.Invoke(index, bound) != true)
                    .SelectMany(bound => nativeAncestors
                        && OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame, references)
                        ? NativeAncestors(bound) : (physicalDeclaration ? ExactPhysicalProof(bound) : ExactNative(bound))
                            ? new[] { NativeIdentity(bound) } : Enumerable.Empty<TypeNode.Fqn>())
                    .Select(bound => BirGraphValue.CopyType(bound)).Distinct().ToList();
                if (bounds.Count != 0) result[index] = bounds;
            }
            return result;
        }

        bool ExactNative(TypeNode.Fqn bound) => !IsObjectish(bound) && !ContainsUseSiteProjection(bound)
            && !definitions.ContainsKey(bound.Name) && !references.HasDotKtOwner(bound.Name)
            && references.ResolveForeignProjectionType(bound.Name, bound.Args) != null;

        bool ExactPhysicalProof(TypeNode.Fqn bound) => !IsObjectish(bound) && !ContainsUseSiteProjection(bound)
            && !references.IsExistentialPhysicalOwner(bound.Name) && ProofType(bound) != null;

        Type ProofType(TypeNode.Fqn bound) => references.ResolveForeignProjectionType(bound.Name, bound.Args)
            ?? references.ResolveRefType(bound.Name, bound.Args?.Length ?? 0);

        TypeNode.Fqn NativeIdentity(TypeNode.Fqn bound) => new(ProofType(bound).FullName,
            bound.Args?.Select(BirGraphValue.CopyType).ToArray());

        IEnumerable<TypeNode.Fqn> NativeAncestors(TypeNode.Fqn bound)
        {
            var classBound = false;
            foreach (var edge in SupertypeGraph.Reachable(new SupertypeGraph.Def { Interfaces = new[] { bound } },
                sourceSupertypes, references))
            {
                if (!ExactNative(edge.spec)) continue;
                var type = references.ResolveForeignProjectionType(edge.spec.Name, edge.spec.Args);
                if (!type.IsInterface)
                {
                    if (classBound) continue;
                    classBound = true;
                }
                yield return NativeIdentity(edge.spec);
            }
        }

        void Register(DeclarationBinding declaration, bool nativeAncestors) =>
            rows[declaration] = ReadRows(Constraints(declaration), nativeAncestors, Frame(declaration),
                projectsBound: !TypeDeclaration(declaration)
                    ? (index, bound) => ProjectsMethodBound(declaration, index, bound)
                    : Generated(declaration) ? (index, bound) => ProjectsCapturedBound(bound, index,
                        OuterCount(declaration), Frame(declaration), references) : null);

        internal bool Add(DeclarationBinding declaration, int index, TypeNode bound)
        {
            if (index >= Constraints(declaration).Count) return false;
            var frame = Frame(declaration);
            if (frame != null && frame.PhysicalSlot(index).Representation
                is not (NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)) return false;
            if (!rows.TryGetValue(declaration, out var slots)) rows[declaration] = slots = new();
            if (!slots.TryGetValue(index, out var constraints)) slots[index] = constraints = new();
            if (constraints.Contains(bound)) return false;
            constraints.Add(BirGraphValue.CopyType(bound));
            return true;
        }

        IReadOnlyDictionary<int, List<TypeNode>> Rows(DeclarationBinding declaration) =>
            declaration != null && rows.TryGetValue(declaration, out var value) ? value : new Dictionary<int, List<TypeNode>>();

        IReadOnlyDictionary<int, List<TypeNode>> TypeRows(string owner)
        {
            if (lateLocalTypes.TryGetValue(owner, out var physicalRows)) return physicalRows;
            if (definitions.TryGetValue(owner, out var local)) return Rows(local);
            if (!foreignTypes.TryGetValue(owner, out var constraints))
                foreignTypes[owner] = constraints = ReadRows(references.PhysicalOwnerTypeParametersInPhysicalFrame(owner), false,
                    references.NullableTypeFrames.GetValueOrDefault(owner), physicalDeclaration: true);
            return constraints;
        }

        IReadOnlyDictionary<int, List<TypeNode>> MethodRows(string identity)
        {
            if (identity == null) return new Dictionary<int, List<TypeNode>>();
            if (!foreignMethods.TryGetValue(identity, out var constraints))
                foreignMethods[identity] = constraints = ReadRows(references.PhysicalMethodTypeParametersInPhysicalFrame(identity), false,
                    references.NullableMethodFrame(identity), physicalDeclaration: true);
            return constraints;
        }

        internal IEnumerable<TypeNode> Bounds(DeclarationBinding declaration, int index) =>
            Rows(declaration).TryGetValue(index, out var constraints)
                ? constraints.Where(bound => !RetainedSourceProof(declaration, index, bound)
                    && !(bound is TypeNode.Fqn named && ProofType(named)?.IsClass == true
                        && constraints.OfType<TypeNode.Fqn>().Any(other => other != named && ProofType(other)?.IsClass == true
                            && SupertypeGraph.Reaches(other, named, sourceSupertypes, references))))
                : Enumerable.Empty<TypeNode>();

        bool RetainedSourceProof(DeclarationBinding declaration, int index, TypeNode required)
        {
            var parameters = Constraints(declaration);
            if (required is not TypeNode.Fqn target || index >= parameters.Count) return false;
            var typeDeclaration = TypeDeclaration(declaration);
            var frame = Frame(declaration);
            foreach (var bound in parameters[index].OfType<TypeNode.Fqn>())
            {
                var ownsClassifier = definitions.TryGetValue(bound.Name, out var local) && SourceGeneric(local)
                    || references.TryExistentialPhysicalOwner(bound.Name, out _);
                if (ContainsUseSiteProjection(bound) || !typeDeclaration && ContainsOwnerTv(bound)
                    || OwnerConstrainedMethodLowering.HasConstructedDependency(bound,
                        typeDeclaration ? "type" : "method", index, frame, references) && (ownsClassifier || !typeDeclaration)) continue;
                var alias = references.ResolveForeignProjectionType(bound.Name, bound.Args);
                var existing = alias == null ? bound : new TypeNode.Fqn(alias.FullName, alias.IsGenericTypeDefinition ? bound.Args : null);
                if (SupertypeGraph.Reaches(existing, target, sourceSupertypes, references)) return true;
            }
            return false;
        }

        static bool TypeDeclaration(DeclarationBinding declaration) =>
            declaration.Source is IBirTypeDeclaration type && IsTypeDefinition(type.Kind.OrDefault());

        static IReadOnlyList<BirTypeParameter> SourceParameters(BirGraphValue source) => source switch {
            IBirTypeDeclaration value => TypeParameterFrame.Declarations(value).ToArray(), BirFunctionDeclaration value => value.TypeParams.OrEmpty(),
            BirMethodImpl value => value.TypeParams.OrEmpty(),
            BirNewSuspendLambda value => value.TypeParams.OrEmpty(), BirClosureDefinition value => value.TypeParams.OrEmpty(),
            BirFileDeclaration or BirConstructor or BirInlineLambda => Array.Empty<BirTypeParameter>(),
            _ => throw new InvalidOperationException("Unaccounted native proof declaration: " + source.GetType().Name),
        };

        // Placement separates declaration-owned captures without changing !N.
        // Callable and MethodImpl generic frames have no enclosing prefix.
        static IReadOnlyList<TypeNode[]> Constraints(DeclarationBinding declaration) =>
            SourceParameters(declaration.Source).Select(parameter => parameter.Constraints.OrEmpty()).ToArray();

        static NullableRepresentationFrame Frame(DeclarationBinding declaration) =>
            TypeDeclaration(declaration) ? NullableRepresentationViews.TypeFacts(declaration.Source)?.NullableFrame
                : (declaration.Source as BirFunctionDeclaration)?.NullableRepresentationFrame.OrDefault();

        static bool Generated(DeclarationBinding declaration) =>
            declaration.Source switch { IBirTypeDeclaration type => type.Generated.OrDefault(),
                BirFunctionDeclaration method => method.Generated.OrDefault(), _ => false };

        static int OuterCount(DeclarationBinding declaration) =>
            (declaration.Source as BirClassDefinition)?.OuterTypeParamCount.OrDefault() ?? 0;

        static string Identity(DeclarationBinding declaration) =>
            (declaration.Source as BirFunctionDeclaration)?.DeclarationId.OrDefault();

        bool ProjectsMethodBound(DeclarationBinding declaration, int index, TypeNode bound) =>
            OwnerConstrainedMethodLowering.ProjectsBound(index, bound, Frame(declaration),
                (declaration.Source as BirFunctionDeclaration)?._ownerConstraintOverrideBounds.OrDefault(), references);

        static bool SourceGeneric(DeclarationBinding declaration) =>
            declaration.Source is IBirTypeDeclaration type && IsSourceGeneric(type);
    }
}
