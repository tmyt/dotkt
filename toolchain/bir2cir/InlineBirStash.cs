using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Capture representation-selected executable BIR before per-file lowering.
// The local index retains producer source positions; the outgoing opaque DLL
// carrier hides those positions without changing the live method body.
static class InlineBirStash
{
    internal static InlineBirIndex Current { get; private set; } = new();
    public static void Reset() => Current = new();
    public static void Stash(BirFileDeclaration root) => Current.Stash(root);
}

// This is the executable publication authority. The earlier TypedInlineBirIndex
// is an independent analysis snapshot and neither publishes nor replaces it.
sealed class InlineBirIndex
{
    readonly Dictionary<string, List<BirInlinePayload>> candidates = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<BirInlinePayload>> ownerless = new(StringComparer.Ordinal);
    readonly Dictionary<string, BirInlinePayload> identities = new(StringComparer.Ordinal);

    // Selection reads these frozen snapshots. Only the selected payload is
    // materialized into an independently owned graph at the execution boundary.
    internal BirInlinePayload Declaration(string id) => id == null ? null : identities.GetValueOrDefault(id);
    internal IReadOnlyList<BirInlinePayload> Candidates(string owner, string name, int parameters, int arity) =>
        candidates.GetValueOrDefault($"{owner}|{name}|{parameters}|{arity}");
    internal IReadOnlyList<BirInlinePayload> OwnerlessCandidates(string name, int parameters, int arity) =>
        name == null ? null : ownerless.GetValueOrDefault($"{name}|{parameters}|{arity}");

    internal void Stash(BirFileDeclaration root)
    {
        var fileClass = root.FileClass.OrDefault();
        var generated = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
        foreach (var method in root.Methods.OrEmpty())
            if (method.Generated.OrDefault() && method.Name.HasValue)
                generated.TryAdd(method.Name.Value, method);
        if (fileClass != null)
            foreach (var method in root.Methods.OrEmpty()) StashMethod(fileClass, method, fileClass, generated);
        foreach (var type in root.Types.OrEmpty())
        {
            if (!type.Name.HasValue) continue;
            var owner = type.Name.Value;
            // Keep the physical owner in the published payload, but index a
            // moved generic companion static by its explicit semantic owner.
            var lookupOwner = type.StaticCarrier.OrDefault()?.Owner.OrDefault() ?? owner;
            foreach (var method in type.Methods.OrEmpty()) StashMethod(owner, method, fileClass, generated, lookupOwner);
        }
    }

    void StashMethod(string owner, BirFunctionDeclaration method, string fileClass,
        IReadOnlyDictionary<string, BirFunctionDeclaration> generated, string lookupOwner = null)
    {
        if (method.Mods.OrDefault()?.Inline.OrDefault() != true || !method.Name.HasValue || !method.Body.HasValue) return;
        var parameters = method.Params.OrEmpty();
        var arity = method.TypeParams.OrEmpty().Length;
        var extension = parameters.Length != 0 && parameters[0].Mods.OrDefault()?.ExtensionReceiver.OrDefault() == true;
        var isStatic = method.Static.OrDefault();
        var role = InlinePayloadSelection.ReceiverRole(extension, isStatic);
        var name = method.Name.Value;
        var payload = (BirInlinePayload)new BirInlinePayload {
            V = 1, Fqn = owner + "." + name, Owner = owner, FileClass = OptionalField<string>.Of(fileClass),
            Recv = role, Static = isStatic,
            TypeParams = method.TypeParams.OrEmpty(), Params = parameters,
            Ret = method.Ret.HasValue ? method.Ret : OptionalField<TypeNode>.Null,
            Body = method.Body.Value,
            DeclarationId = method.DeclarationId.HasValue ? method.DeclarationId : default,
        }.DeepClone();
        var lifted = new List<BirFunctionDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in payload.Body.Value) CollectLifted((BirGraphValue)statement, fileClass, generated, seen, lifted);
        payload.Lifted = lifted.ToArray();
        DeclarationSignatureIdentity.RemoveCapturedFacts(payload);

        if (payload.DeclarationId.HasValue && !identities.TryAdd(payload.DeclarationId.Value, payload))
            throw new InvalidOperationException($"duplicate inline declaration identity '{payload.DeclarationId.Value}'");
        var key = $"{lookupOwner ?? owner}|{name}|{parameters.Length}|{arity}";
        if (!candidates.TryGetValue(key, out var choices)) candidates[key] = choices = new();
        choices.Add(payload);
        if (owner.StartsWith("kotlin.", StringComparison.Ordinal) && role != "dispatch")
        {
            var shortKey = $"{name}|{parameters.Length}|{arity}";
            if (!ownerless.TryGetValue(shortKey, out var local)) ownerless[shortKey] = local = new();
            local.Add(payload);
        }

        var outgoing = (BirInlinePayload)payload.DeepClone();
        MaterializedBirPayload.HideSourceSpans(outgoing);
        method.InlineBir = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, outgoing);
    }

    static void CollectLifted(BirGraphValue node, string fileClass,
        IReadOnlyDictionary<string, BirFunctionDeclaration> generated, HashSet<string> seen,
        List<BirFunctionDeclaration> into)
    {
        if (node is BirNewDelegate target && target.CalleeOwner.OrDefault() is TypeNode.Fqn owner && owner.Name == fileClass
            && target.Method.HasValue && generated.TryGetValue(target.Method.Value, out var declaration) && seen.Add(target.Method.Value))
        {
            var clone = (BirFunctionDeclaration)declaration.DeepClone();
            into.Add(clone);
            // A generated delegate target may itself create another generated
            // delegate. User-authored references are never copied into carriers.
            CollectLifted(clone, fileClass, generated, seen, into);
        }
        node.VisitChildren(child => CollectLifted(child, fileClass, generated, seen, into));
    }
}
