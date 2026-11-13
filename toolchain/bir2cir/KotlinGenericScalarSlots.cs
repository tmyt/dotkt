using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A non-reified Kotlin scalar parameter/result transports a value, not a request to
// extract the caller's exact CLR T. Keep that witness in the declaration's generic
// frame; native signatures, managed locations and concrete scalar slots remain exact.
static partial class KotlinGenericScalarSlots
{
    internal sealed partial record Projection(Dictionary<string, int[]> Parameters, HashSet<string> Returns,
        GenericRepresentationPolicy Policy, HashSet<string> Intrinsics, ReferenceMetadataIndex References)
    {
        internal bool ProjectsMethod(bool nativeSignature, IEnumerable<string> overrideIdentities) =>
            !nativeSignature && !overrideIdentities.Any(id => id != null
                && (Intrinsics.Contains(id) || References?.TryDeclarationIdentity(id,
                    out _, out _, out var intrinsic, out _) == true && intrinsic != null));

        internal void Record(string identity, int[] projected, TypeNode sourceReturn)
        {
            if (identity == null) return;
            if (projected.Length != 0) Parameters[identity] = projected;
            if (sourceReturn is TypeNode.Tv) Returns.Add(identity);
        }

        internal int[] CallParameters(string identity, IEnumerable<string> overrideIdentities)
        {
            // A fake override can carry only its exact override closure. Project a position
            // only when every selected declaration agrees on the opaque parameter contract.
            var identities = identity != null ? new[] { identity } : overrideIdentities.ToArray();
            if (identities.Length == 0 || identities.Any(id => id == null)) return Array.Empty<int>();
            int[] Indices(string id) => Parameters.TryGetValue(id, out var localIndices)
                ? localIndices : References?.GenericScalarParameters(id) ?? Array.Empty<int>();
            return identities.Select(Indices).Aggregate((left, right) => left.Intersect(right).ToArray());
        }

        // Late synthesized methods join this same authority; they do not create another
        // projection or replay the original source inventory.
        internal void ProjectMethod(JsonObject method)
        {
            if (!ProjectsMethod(Policy.HasNativeMethodSignature(method),
                (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(edge => edge[DeclarationIdentityBinding.Key]?.GetValue<string>()))) return;
            var owner = method.Parent?.Parent as JsonObject;
            var ownerFrame = owner == null ? null : KotlinSupertypesRecord.ReadNullableFrame(owner);
            var methodFrame = NullableRepresentationTypes.DeclarationMethodFrame(method);
            var slots = method["params"] as JsonArray ?? new JsonArray();
            var projected = ParameterIndices(slots.Select(parameter => TypeJson.Read(parameter["type"])).ToArray(),
                KotlinCollectionSlotSynthesis.ErasedQueryOverride(method) != null);
            foreach (var index in projected)
            {
                var parameter = slots[index].AsObject();
                parameter["kotlinType"] ??= parameter["nullableGeneric"]?.DeepClone()
                    ?? JsonValue.Create(TypeNode.ToJson(SourceVariable((TypeNode.Tv)TypeJson.Read(parameter["type"]), ownerFrame, methodFrame)));
                parameter["type"] = TypeJson.Fqn("object");
            }
            if (ProjectsReturn(TypeJson.Read(method["ret"]), method["mods"]?["suspend"]?.GetValue<bool>() == true))
            {
                method["retKotlinType"] ??= method["nullableGenericRet"]?.DeepClone()
                    ?? JsonValue.Create(TypeNode.ToJson(SourceVariable((TypeNode.Tv)TypeJson.Read(method["ret"]), ownerFrame, methodFrame)));
                method["ret"] = TypeJson.Fqn("object");
            }
            Record(method[DeclarationIdentityBinding.Key]?.GetValue<string>(), projected,
                method["retKotlinType"]?.GetValue<string>() is string saved ? TypeNode.Parse(saved) : null);
        }

        internal TypeNode.Tv Result(JsonObject call, ReferenceMetadataIndex refs) =>
            call["k"]?.GetValue<string>() is "callStatic" or "callInstance"
            && TypeJson.Read(call["sty"] ?? call["dynRet"]) is TypeNode.Tv variable
            && call[DeclarationIdentityBinding.Key]?.GetValue<string>() is string identity
            && (Returns.Contains(identity) || refs.IsGenericScalarReturn(identity)) ? variable : null;
    }

    static int[] ParameterIndices(IReadOnlyList<TypeNode> slots, bool query) => query ? Array.Empty<int>()
        : Enumerable.Range(0, slots.Count).Where(index => slots[index] is TypeNode.Tv).ToArray();

    // Suspend has no ordinary scalar CLR result slot; its cold Object/hot Task
    // result belongs to suspend lowering, independently of scalar parameters.
    static bool ProjectsReturn(TypeNode result, bool suspend) => !suspend && result is TypeNode.Tv;

    static TypeNode SourceVariable(TypeNode.Tv physical, NullableRepresentationFrame owner, NullableRepresentationFrame method) =>
        (physical.Scope == "method" ? method : owner)?.SemanticVariable(physical) ?? physical;
}
