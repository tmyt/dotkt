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
        Func<TypeNode.Fqn, TypeNode> storageConstraint = null, bool sourceParameters = false)
    {
        if (slotParameters == null) return;
        var rows = (implementation._ownerConstraintOverrideBounds.OrDefault()
            ?? new OwnerConstraintOverrideBounds(Array.Empty<OwnerConstraintOverrideBounds.Row>()))
            .Append(BuildOverrideRows(implementation.NullableRepresentationFrame.OrDefault(),
                ConstraintVectors(implementation.TypeParams.OrDefault()), ConstraintVectors(slotParameters), ownerArgs,
                ConstraintVectors(ownerParameters), ConstraintVectors(implementationOwnerParameters), storageConstraint, sourceParameters));
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
        Func<TypeNode.Fqn, TypeNode> storageConstraint, bool sourceParameters)
    {
        IEnumerable<TypeNode> Independent(TypeNode bound, IReadOnlyList<TypeNode[]> constraints) =>
            IndependentBounds(bound, constraints.Count, index => constraints[index]);
        for (var index = 0; index < slotConstraints.Count; index++)
        foreach (var bound in slotConstraints[index])
        {
            if (!FBoundStarProjectionErasure.ContainsOwnerTv(bound)) continue;
            var physicalIndex = sourceParameters && frame != null ? frame.SourcePosition(index) : index;
            var template = sourceParameters && frame != null
                ? MapVariables(bound, tv => tv.Scope == "method"
                    ? frame.Variable(tv, NullableRepresentationFrame.Role.Ordinary) : tv)
                : bound;
            var closedBound = SupertypeGraph.SubstOwnerTvs(template, ownerArgs);
            // Materialization can publish classifier consequences on companion
            // slots of the SAME source parameter; weaken those together.
            var physicalBounds = new[] { closedBound }.Concat(Independent(closedBound, implementationOwnerConstraints)
                .OfType<TypeNode.Fqn>()
                .Select(type => storageConstraint == null ? type : storageConstraint(type))
                .Where(type => type != null)).Distinct().ToArray();
            var positions = frame == null ? new[] { physicalIndex } : Enumerable.Range(0, frame.PhysicalArity)
                .Where(position => position == physicalIndex
                    || frame.PhysicalSlot(position).SourceIndex == frame.PhysicalSlot(physicalIndex).SourceIndex
                        && implementationConstraints[position].Any(physicalBounds.Contains));
            foreach (var position in positions)
            foreach (var physicalBound in physicalBounds.Where(candidate =>
                position == physicalIndex && candidate.Equals(closedBound)
                || implementationConstraints[position].Contains(candidate)))
                yield return new OwnerConstraintOverrideBounds.Row(position, physicalBound, template,
                    Independent(template, ownerConstraints).ToArray(), ownerArgs);
        }
    }
}
