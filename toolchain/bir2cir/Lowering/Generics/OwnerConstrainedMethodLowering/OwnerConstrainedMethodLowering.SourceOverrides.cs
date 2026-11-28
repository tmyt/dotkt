using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OwnerConstrainedMethodLowering
{
    internal static void RecordOverride(BirFunctionDeclaration implementation, IReadOnlyList<BirTypeParameter> slotParameters,
        TypeNode[] ownerArgs, IReadOnlyList<BirTypeParameter> ownerParameters,
        IReadOnlyList<BirTypeParameter> implementationOwnerParameters = null,
        GenericRepresentationPolicy representations = null, bool sourceParameters = false)
    {
        if (slotParameters == null) return;
        var rows = (implementation._ownerConstraintOverrideBounds.OrDefault()
            ?? new OwnerConstraintOverrideBounds(Array.Empty<OwnerConstraintOverrideBounds.Row>()))
            .Append(BuildOverrideRows(implementation.NullableRepresentationFrame.OrDefault(),
                ConstraintVectors(implementation.TypeParams.OrDefault()), ConstraintVectors(slotParameters), ownerArgs,
                ConstraintVectors(ownerParameters), ConstraintVectors(implementationOwnerParameters), representations, sourceParameters));
        if (rows.Rows.Count > 0)
            implementation._ownerConstraintOverrideBounds = OptionalField<OwnerConstraintOverrideBounds>.Of(rows);
    }

    static TypeNode[][] ConstraintVectors(JsonArray parameters) => parameters?.Select(parameter =>
        parameter is JsonObject declaration && declaration["constraints"] is JsonArray constraints
            ? constraints.Select(TypeJson.Read).Where(bound => bound != null).ToArray() : Array.Empty<TypeNode>()).ToArray()
        ?? Array.Empty<TypeNode[]>();

    static TypeNode[][] ConstraintVectors(IReadOnlyList<BirTypeParameter> parameters) => parameters?.Select(parameter =>
        parameter.Constraints.OrEmpty()).ToArray() ?? Array.Empty<TypeNode[]>();

    // Both owning readers supply the exact current declaration constraints. All
    // source-to-physical frame and consequence decisions live in this one builder.
    static IEnumerable<OwnerConstraintOverrideBounds.Row> BuildOverrideRows(NullableRepresentationFrame frame,
        IReadOnlyList<TypeNode[]> implementationConstraints, IReadOnlyList<TypeNode[]> slotConstraints,
        TypeNode[] ownerArgs, IReadOnlyList<TypeNode[]> ownerConstraints,
        IReadOnlyList<TypeNode[]> implementationOwnerConstraints,
        GenericRepresentationPolicy representations, bool sourceParameters)
    {
        IEnumerable<TypeNode> Independent(TypeNode bound, IReadOnlyList<TypeNode[]> constraints) =>
            IndependentBounds(bound, constraints.Count, index => constraints[index]);
        TypeNode[] Classifiers(IEnumerable<TypeNode> bounds) => bounds.OfType<TypeNode.Fqn>()
            .Select(type => representations == null ? type : representations.StorageConstraint(type))
            .Where(type => type != null).Distinct().ToArray();
        TypeNode[] StorageProofs(TypeNode[] classifiers) => classifiers.SelectMany(representations.StorageBoundRows)
            .Where(row => !row.Erased).Select(row => row.Bound).Distinct().ToArray();
        // A dependent formal C : A retains that CLR edge. Only classifier
        // consequences inherited from A's owner-dependent bound need rewriting.
        IEnumerable<(TypeNode Bound, bool Direct)> OwnerBounds(int start)
        {
            var seen = new HashSet<int>();
            IEnumerable<(TypeNode, bool)> Visit(int at)
            {
                if (!seen.Add(at)) yield break;
                foreach (var bound in slotConstraints[at])
                    if (FBoundStarProjectionErasure.ContainsOwnerTv(bound)) yield return (bound, at == start);
                    else if (bound is TypeNode.Tv { Scope: "method" } parent &&
                        (sourceParameters || frame == null || frame.PhysicalSlot(parent.I).Representation == NullableRepresentationFrame.Role.Ordinary))
                        foreach (var inherited in Visit(parent.I)) yield return inherited;
            }
            return Visit(start);
        }
        for (var index = 0; index < slotConstraints.Count; index++)
        foreach (var (bound, direct) in OwnerBounds(index))
        {
            var physicalIndex = sourceParameters && frame != null ? frame.SourcePosition(index) : index;
            var template = sourceParameters && frame != null
                ? MapVariables(bound, tv => tv.Scope == "method"
                    ? frame.Variable(tv, NullableRepresentationFrame.Role.Ordinary) : tv)
                : bound;
            var closedBound = SupertypeGraph.SubstOwnerTvs(template, ownerArgs);
            // Materialization can publish classifier consequences on companion
            // slots of the SAME source parameter; weaken those together.
            var classifiers = Classifiers(Independent(closedBound, implementationOwnerConstraints));
            var ordinaryBounds = new[] { closedBound }.Concat(classifiers).Distinct().ToArray();
            var storageBounds = representations == null ? ordinaryBounds : StorageProofs(classifiers);
            var openBounds = Independent(template, ownerConstraints).ToArray();
            var openStorage = representations == null ? openBounds : StorageProofs(Classifiers(openBounds));
            var positions = frame == null ? new[] { physicalIndex } : Enumerable.Range(0, frame.PhysicalArity)
                .Where(position => position == physicalIndex
                    || frame.PhysicalSlot(position).SourceIndex == frame.PhysicalSlot(physicalIndex).SourceIndex);
            foreach (var position in positions)
            {
                var storage = frame?.PhysicalSlot(position).Representation == NullableRepresentationFrame.Role.Storage;
                foreach (var physicalBound in (storage ? storageBounds : ordinaryBounds).Where(candidate =>
                    direct && position == physicalIndex && candidate.Equals(closedBound)
                    || implementationConstraints[position].Contains(candidate)))
                    yield return new OwnerConstraintOverrideBounds.Row(position, physicalBound, template,
                        storage ? openStorage : openBounds, ownerArgs);
            }
        }
    }
}
