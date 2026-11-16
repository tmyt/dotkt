using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // Capture before inner-application projection changes declaration rows.
    // Encoded source facts and executable carriers are not live declarations.
    internal static void RecordArrayTypeEdges(IReadOnlyList<BirFileDeclaration> roots)
    {
        Dictionary<int, TypeNode[]> Bounds(IEnumerable<BirTypeParameter> parameters)
        {
            var result = new Dictionary<int, TypeNode[]>();
            var index = 0;
            foreach (var parameter in parameters)
            {
                var constraints = parameter.Constraints.OrEmpty();
                if (constraints.Any(ContainsGenericArray))
                    result[index] = constraints.Select(BirGraphValue.CopyType).ToArray();
                index++;
            }
            return result;
        }

        SourceTypeFacts TypeFacts(SourceTypeFacts before, TypeNode baseType,
            IEnumerable<TypeNode> interfaces, IEnumerable<BirTypeParameter> parameters)
        {
            var bounds = before?.NullableFrame != null ? null : Bounds(parameters);
            return (before ?? SourceTypeFacts.Empty).Merge(
                ContainsGenericArray(baseType) ? BirGraphValue.CopyType(baseType) : null,
                interfaces.Where(ContainsGenericArray).Select(BirGraphValue.CopyType).ToArray(), bounds);
        }

        void Visit(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration declaration && declaration.Kind.HasValue)
            {
                var before = NullableRepresentationViews.TypeFacts(node);
                var after = TypeFacts(before, declaration.Base.OrDefault(),
                    declaration.Interfaces.OrEmpty(), declaration.TypeParams.OrEmpty());
                if (!ReferenceEquals(after, before ?? SourceTypeFacts.Empty))
                    NullableRepresentationViews.SetTypeFacts(node, after);
            }
            else if (node is BirInheritedImplementation implementation && implementation.Kind.HasValue)
            {
                // A projected implementation descriptor has its own declared
                // constraint rows. Its Kind enters the same type-facts policy;
                // it is not a named method declaration with value parameters.
                var before = implementation.KotlinSupertypesPre.OrDefault();
                var after = TypeFacts(before, null, Array.Empty<TypeNode>(), implementation.TypeParams.OrEmpty());
                if (!ReferenceEquals(after, before ?? SourceTypeFacts.Empty))
                    implementation.KotlinSupertypesPre = after;
            }
            else if (node is BirFunctionDeclaration method && method.Name.HasValue
                && method.Params.HasValue && !method.NullableRepresentationFrame.HasValue)
            {
                var bounds = Bounds(method.TypeParams.OrEmpty());
                if (bounds.Count > 0)
                {
                    var before = NullableRepresentationViews.BoundsFacts(method);
                    NullableRepresentationViews.SetBoundsFacts(method,
                        before == null ? new SourceBoundsSnapshot(bounds) : before.Merge(bounds));
                }
            }
            node.VisitGenericFrame((_, _) => { }, (role, child) => {
                if (!NullableRepresentationViews.Frozen(role)) Visit(child);
            });
        }
        foreach (var root in roots) Visit(root);
    }
}
