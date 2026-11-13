using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ClosureSynthesis;

// Temporarily publish the independently framed declarations of prepared defaults.
// Restore their current transformed fields, not a pre-materialization file copy.
static class PreparedClosureDefaultFrames
{
    internal const string ReturnArgumentKey = "_preparedDelegateReturnArgument";
    internal sealed record StageResult(IReadOnlyList<BirClassDefinition> Types, Action Restore);

    internal static StageResult Stage(BirFileDeclaration file, IEnumerable<MaterializedOccurrence> inputs, ReferenceMetadataIndex refs)
    {
        var roots = inputs.ToArray();
        var materialized = ClosureSynthesis.ApplyMaterialized(file, roots, refs);
        var types = materialized.AppendedTypes;
        var closureNames = materialized.Provenance.Where(source => source.Construction is BirNewClosure)
            .Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var type in types.Where(type => closureNames.Contains(type.Name.Value)))
            type.Methods.Value.Single(method => method.Name.OrDefault() == "invoke")._preparedDelegateReturnArgument = true;
        return new StageResult(types, () => {
            var declarations = types.ToDictionary(type => type.Name.Value, StringComparer.Ordinal);
            var ingredients = new Dictionary<string, BirGraphValue>(StringComparer.Ordinal);
            foreach (var source in materialized.Provenance)
                if (!ingredients.TryAdd(source.Name, source.Ingredients)
                    && !ingredients[source.Name].StructurallyEquals(source.Ingredients))
                    throw new InvalidOperationException("Conflicting prepared closure declarations: " + source.Name);
            void RestoreNode(BirGraphValue node)
            {
                if (node == null || node is BirAttribute or IBirExecutablePayload) return;
                if (node is BirNewClosure { SynthClass.HasValue: false, ClosureType.Value: TypeNode.Fqn name } closure
                    && declarations.TryGetValue(name.Name, out var declaration))
                {
                    // Provenance is captured after CLR SAM normalization: a
                    // converted SAM has closure ingredients, never a full class.
                    var restored = (BirClosureDefinition)ingredients[name.Name].DeepClone();
                    restored.Name = declaration.Name.HasValue ? declaration.Name : default;
                    restored.Fields = declaration.Fields.HasValue ? OptionalField<BirCaptureDescriptor[]>.Of(declaration.Fields.Value
                        .Select(field => ToCapture((BirFieldDeclaration)field.DeepClone())).ToArray()) : default;
                    restored.TypeParams = Copy(declaration.TypeParams, parameter => (BirTypeParameter)parameter.DeepClone());
                    restored.SemanticOwner = declaration.SemanticOwner.HasValue ? declaration.SemanticOwner : default;
                    restored.OuterTypeParamCount = declaration.OuterTypeParamCount.HasValue ? declaration.OuterTypeParamCount : default;
                    restored.OuterTypeParamOffset = declaration.OuterTypeParamOffset.HasValue ? declaration.OuterTypeParamOffset : default;
                    restored.KotlinSupertypesPre = declaration.KotlinSupertypesPre.HasValue ? declaration.KotlinSupertypesPre : default;
                    var invoke = declaration.Methods.Value.Single(method => method.Name.OrDefault() == "invoke");
                    if (invoke.Params.HasValue) restored.Params = invoke.Params.Value.Select(parameter => ToLambdaParameter((BirParameter)parameter.DeepClone())).ToArray();
                    if (invoke.Ret.HasValue) restored.Ret = BirGraphValue.CopyType(invoke.Ret.Value);
                    if (invoke.Body.HasValue) restored.Body = invoke.Body.Value.Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()).ToArray();
                    if (invoke.NullableGenericRet.HasValue) restored.NullableGenericRet = BirGraphValue.CopyType(invoke.NullableGenericRet.Value);
                    if (invoke.RetNullableFlags.HasValue) restored.RetNullableFlags = invoke.RetNullableFlags.Value.ToArray();
                    restored._syntheticFrameBound = true;
                    closure.SynthClass = restored;
                }
                else if (node is BirNewSam { SynthClass.HasValue: false, SamType.Value: TypeNode.Fqn samName } sam
                    && declarations.TryGetValue(samName.Name, out var samDeclaration))
                {
                    var restored = (BirClassDefinition)samDeclaration.DeepClone();
                    restored._syntheticFrameBound = true;
                    sam.SynthClass = restored;
                }
                node.VisitChildren(RestoreNode);
            }
            foreach (var root in roots) RestoreNode(root.Slot.Value);
            if (types.Count != 0 && file.Types.HasValue)
            {
                var removed = types.ToHashSet(ReferenceEqualityComparer.Instance);
                file.Types = file.Types.Value.Where(type => type is not BirClassDefinition declaration || !removed.Contains(declaration)).ToArray();
            }
        });
    }

    static OptionalField<T[]> Copy<T>(OptionalField<T[]> source, Func<T, T> clone) => source.HasValue
        ? OptionalField<T[]>.Of(source.Value.Select(clone).ToArray()) : default;
}
