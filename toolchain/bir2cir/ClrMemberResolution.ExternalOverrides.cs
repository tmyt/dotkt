using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// DECLARATION-SIDE external class override binding. A facadegen-injected CLR base member normally survives Fir2Ir
// through `overriddenSymbols`; kotc then emits `override:true` + its pure-Kotlin `overrides` closure, and
// DeclarationRename confirms the external slot. Fir2Ir can, however, lose that edge when two injected parameter
// classifiers share a simple name in different namespaces (InjfqnAaa.Args / InjfqnBbb.Args): the BIR still carries the
// correct resolved FQNs, but the method arrives as a virtual NewSlot and silently hides the CLR base virtual.
//
// Resolve the CLR relation here, where compile-reference reflection belongs. This runs on CLR-lowered type nodes, before
// ilemit: CIR therefore carries the final `override:true` decision and ilemit remains a 1:1 emitter. There is deliberately
// NO simple-name or arity-only fallback. A candidate must be a non-final virtual on the external base-class chain and its
// complete parameter structure must match the declaration's FQN-preserving CIR signature. Zero matches means "not an
// external override"; more than one is malformed and fails loudly rather than choosing a reflection-order winner.
static partial class ClrMemberResolution
{
    static void ResolveExternalClassOverrides(JsonNode root)
    {
        if (root is not JsonObject ro || ro["types"] is not JsonArray types) return;
        foreach (var item in types)
        {
            if (item is not JsonObject type
                || (type["kind"] as JsonValue)?.GetValue<string>() != "class"
                || TypeJson.Read(type["base"]) is not TypeNode.Fqn baseNode
                || type["methods"] is not JsonArray methods)
                continue;

            // ResolveNetType intentionally excludes DotKt-authored dependencies and local types. Their Kotlin override
            // edges must already be present in BIR; this fallback is only for raw CLR classes injected by facadegen.
            var baseType = _refs.ResolveNetType(
                ReferenceMetadataIndex.BareOwnerFqn(baseNode.Name),
                baseNode.Args?.Length ?? 0);
            if (baseType == null || !baseType.IsClass) continue;

            foreach (var entry in methods)
            {
                if (entry is not JsonObject method
                    || BoolValue(method["static"])
                    || BoolValue(method["override"])
                    || method["clrOverride"] != null
                    || (method["name"] as JsonValue)?.GetValue<string>() is not string name
                    || method["params"] is not JsonArray parameters)
                    continue;

                var declared = parameters.Select((p, i) =>
                    TypeJson.Read((p as JsonObject)?["type"])
                    ?? throw new InvalidOperationException(
                        $"bir2cir: external override '{baseNode.Name}.{name}' param #{i} has an unreadable type node"))
                    .ToList();

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var candidates = new List<MethodInfo>();
                try
                {
                    candidates.AddRange(baseType.GetMethods(flags).Where(m =>
                        m.Name == name && m.IsVirtual && !m.IsFinal && m.GetParameters().Length == declared.Count));
                }
                catch { }

                var matches = MostDerived(candidates.Where(c => OverrideMatch(c.GetParameters(), declared)).ToList());
                if (matches.Count == 0) continue;
                if (matches.Count > 1)
                    throw Malformed(
                        $"external override base={baseNode.Name}.{name}({DescArgs(declared)})",
                        matches);

                method["override"] = true;
            }
        }
    }

    static bool BoolValue(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
}
