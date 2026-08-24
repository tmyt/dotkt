using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Raw inline/default bodies and early KotlinSupertypes snapshots are deliberately opaque while ordinary lowering
// runs, but they still cross an assembly boundary. Once TypeOwnershipLowering has selected every local TypeDef's
// nestedIn representation, bind type identities inside those carriers to that exact producer metadata identity. This
// is authored from current declarations/reference facts; a consumer never guesses an owner from a generated name and
// there is intentionally no legacy-DLL fallback.
static class OpaqueCarrierTypeBinding
{
    const string KotlinDefault = "kotlin.clr.KotlinDefault";

    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var declarations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var fileClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>())
        {
            if (Str(root["fileClass"]) is string fileClass) fileClasses.Add(fileClass);
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    if (Str(type["name"]) is string name && !declarations.TryAdd(name, type))
                    {
                        // Shared synthesized declarations may intentionally be repeated in several BIR roots and are
                        // de-duplicated by ilemit. They still have one representation; disagreement is the ambiguity.
                        var prior = declarations[name];
                        var priorArity = prior["typeParams"] is JsonArray priorParams ? priorParams.Count : 0;
                        var arity = type["typeParams"] is JsonArray typeParams ? typeParams.Count : 0;
                        if (Str(prior["nestedIn"]) != Str(type["nestedIn"]) || priorArity != arity)
                            throw new InvalidOperationException(
                                $"conflicting Kotlin type declaration identity '{name}' in opaque carrier binding");
                    }
        }

        var physicalBySemantic = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        string Physical(string name)
        {
            if (fileClasses.Contains(name)) return name;
            if (physicalBySemantic.TryGetValue(name, out var cached)) return cached;
            if (!declarations.TryGetValue(name, out var declaration)) return name;
            if (!active.Add(name))
                throw new InvalidOperationException($"cyclic Kotlin semantic type ownership at '{name}'");
            try
            {
                var ownArity = declaration["typeParams"] is JsonArray typeParams ? typeParams.Count : 0;
                var ownMetadataName = ownArity > 0 && !name.Contains('`') ? name + "`" + ownArity : name;
                if (Str(declaration["nestedIn"]) is not string owner)
                    return physicalBySemantic[name] = ownMetadataName;

                // Match ilemit's one-to-one metadata spelling: nestedIn supplies the parent and the declaration's last
                // dotted segment supplies the nested TypeDef name. Synthesized semantic identities need not use an
                // owner-prefixed source spelling (state machines deliberately do not).
                var simple = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;
                if (ownArity > 0 && !simple.Contains('`')) simple += "`" + ownArity;
                return physicalBySemantic[name] = Physical(owner) + "+" + simple;
            }
            finally { active.Remove(name); }
        }

        // Only identities whose representation actually changed need binding. Top-level names continue through the
        // ordinary type lowering path; nested names become exact '+' metadata tokens in the producer-authored carrier.
        foreach (var (name, declaration) in declarations)
            if (declaration["nestedIn"] != null) _ = Physical(name);

        foreach (var root in roots)
        {
            if (physicalBySemantic.Count > 0) RewriteCarrierSlots(root, physicalBySemantic);
            BindSupertypeRecords(root, physicalBySemantic, refs);
        }
    }

    // KotlinSupertypes is captured before ownership lowering so it retains source nullability, stars, Kotlin inner
    // argument order, and the flattened Kotlin type-parameter frame. Once ownership has selected exact local and
    // referenced TypeDefs, bind only classifier names inside that opaque snapshot. Arguments deliberately remain in
    // Kotlin metadata order; dll2klib consumes the exact '+' path as a nested classifier without rotating them again.
    static void BindSupertypeRecords(JsonNode node, IReadOnlyDictionary<string, string> localPhysical,
        ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] {
                KotlinSupertypesRecord.PreKey,
                NullableGenericErasure.MethodTypeParameterBoundsPre,
            })
            {
                if ((obj[key] as JsonValue)?.TryGetValue<string>(out var encoded) != true) continue;
                var payload = JsonNode.Parse(encoded)
                    ?? throw new InvalidOperationException($"{key} pass-local payload decoded to null");
                Rewrite(payload);
                obj[key] = payload.ToJsonString();
            }
            foreach (var child in obj.Select(kv => kv.Value).Where(value => value != null).ToList())
                BindSupertypeRecords(child, localPhysical, refs);
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value != null).ToList())
                BindSupertypeRecords(child, localPhysical, refs);

        void Rewrite(JsonNode current)
        {
            if (current is JsonObject type)
            {
                if (Str(type["t"]) == "fqn" && Str(type["name"]) is string name)
                {
                    var arity = type["args"] is JsonArray args ? args.Count : 0;
                    if (localPhysical.TryGetValue(name, out var local)) type["name"] = local;
                    else if (refs.TryExactPhysicalTypeName(name, arity, out var exact) && exact != null)
                        type["name"] = exact;
                }
                foreach (var child in type.Select(kv => kv.Value).Where(value => value != null).ToList())
                    Rewrite(child);
            }
            else if (current is JsonArray array)
                foreach (var child in array.Where(value => value != null).ToList()) Rewrite(child);
        }
    }

    static void RewriteCarrierSlots(JsonNode node, IReadOnlyDictionary<string, string> physicalBySemantic)
    {
        if (node is JsonObject obj)
        {
            if ((obj["inlineBir"] as JsonValue)?.TryGetValue<string>(out var encoded) == true
                && !string.IsNullOrEmpty(encoded))
            {
                var payload = BirCarrier.DecodeBody(BirCarrier.JsonV1, Convert.FromBase64String(encoded));
                BindPayload(payload, physicalBySemantic, preserveInlineParameterSignature: true);
                obj["inlineBir"] = Convert.ToBase64String(BirCarrier.EncodeBody(BirCarrier.JsonV1, payload));
            }

            if ((obj["suspendResult"] as JsonValue)?.TryGetValue<string>(out var suspendResult) == true
                && !string.IsNullOrEmpty(suspendResult))
            {
                var payload = JsonNode.Parse(suspendResult)
                    ?? throw new InvalidOperationException("suspend-result carrier decoded to null");
                BindPayload(payload, physicalBySemantic, preserveInlineParameterSignature: false);
                obj["suspendResult"] = payload.ToJsonString();
            }

            if (TypeJson.OwnerName(obj["attr"]) == KotlinDefault
                && obj["args"] is JsonArray args && args.Count >= 2
                && args[1] is JsonObject carrierArg
                && (carrierArg["value"] as JsonValue)?.TryGetValue<string>(out var carrierJson) == true
                && !string.IsNullOrEmpty(carrierJson))
            {
                var payload = JsonNode.Parse(carrierJson)
                    ?? throw new InvalidOperationException("KotlinDefault carrier decoded to null");
                BindPayload(payload, physicalBySemantic, preserveInlineParameterSignature: false);
                carrierArg["value"] = payload.ToJsonString();
            }

            foreach (var value in obj.Select(kv => kv.Value).Where(value => value != null).ToList())
                RewriteCarrierSlots(value, physicalBySemantic);
        }
        else if (node is JsonArray array)
            foreach (var value in array.Where(value => value != null).ToList())
                RewriteCarrierSlots(value, physicalBySemantic);
    }

    static void BindPayload(JsonNode payload, IReadOnlyDictionary<string, string> physicalBySemantic,
        bool preserveInlineParameterSignature)
    {
        // A synthClass is a complete declaration carried into the consumer and gets a new consumer-side owner. Its own
        // identity must remain semantic here; only references to producer-resident declarations become physical tokens.
        var carried = new HashSet<string>(StringComparer.Ordinal);
        void CollectCarried(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                if (obj["synthClass"] is JsonObject synth && Str(synth["name"]) is string name)
                    carried.Add(name);
                foreach (var child in obj.Select(kv => kv.Value).Where(value => value != null).ToList())
                    CollectCarried(child);
            }
            else if (current is JsonArray array)
                foreach (var child in array.Where(value => value != null).ToList()) CollectCarried(child);
        }
        CollectCarried(payload);

        void Rewrite(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string name
                    && !carried.Contains(name) && physicalBySemantic.TryGetValue(name, out var physical))
                    obj["name"] = physical;
                foreach (var (key, child) in obj.Select(kv => (kv.Key, kv.Value)).Where(entry => entry.Value != null).ToList())
                {
                    // The root inline declaration signature is Kotlin-semantic BIR and is the exact overload key a
                    // consumer's callInline.paramSig matches before splicing. Only the opaque executable body needs
                    // producer-physical nested identities; rewriting this header would make the current-format
                    // producer disagree with an equally current consumer.
                    if (preserveInlineParameterSignature && ReferenceEquals(obj, payload) && key == "params") continue;
                    Rewrite(child);
                }
            }
            else if (current is JsonArray array)
                foreach (var child in array.Where(value => value != null).ToList()) Rewrite(child);
        }
        Rewrite(payload);
    }
}
