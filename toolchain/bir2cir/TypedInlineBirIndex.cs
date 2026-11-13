using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Private source-declaration snapshots for analysis. Capturing declarations here
// neither replaces the executable splice stash nor emits metadata on live BIR.
sealed class TypedInlineBirIndex
{
    readonly Dictionary<string, BirInlinePayload> identities = new(StringComparer.Ordinal);
    readonly Dictionary<(string Owner, string Name, int Parameters, int Arity), List<BirInlinePayload>> candidates = new();
    readonly Dictionary<(string Name, int Parameters, int Arity), List<BirInlinePayload>> ownerless = new();

    internal TypedInlineBirIndex(IEnumerable<BirFileDeclaration> files)
    {
        foreach (var file in files)
        {
            void Owner(BirGraphValue declaration, string owner)
            {
                foreach (var method in NullableWitnessDemand.TypedMethods(declaration))
                {
                    if (owner == null || method.Mods.OrDefault()?.Inline.OrDefault() != true || !method.Name.HasValue || !method.Body.HasValue) continue;
                    var parameters = method.Params.OrEmpty();
                    var extension = parameters.Length != 0 && parameters[0].Mods.OrDefault()?.ExtensionReceiver.OrDefault() == true;
                    var payload = (BirInlinePayload)new BirInlinePayload {
                        V = 1, Owner = owner, Fqn = owner + "." + method.Name.Value, FileClass = file.FileClass,
                        Recv = InlinePayloadSelection.ReceiverRole(extension, method.Static.OrDefault()), Static = method.Static.OrDefault(),
                        DeclarationId = method.DeclarationId, TypeParams = method.TypeParams.OrEmpty(), Params = parameters,
                        Ret = method.Ret, Body = method.Body.Value, Lifted = Array.Empty<BirFunctionDeclaration>(),
                    }.DeepClone();
                    if (payload.DeclarationId.HasValue && !identities.TryAdd(payload.DeclarationId.Value, payload))
                        throw new InvalidOperationException($"duplicate inline declaration identity '{payload.DeclarationId.Value}'");
                    var key = (owner, method.Name.Value, parameters.Length, method.TypeParams.OrEmpty().Length);
                    if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = new();
                    list.Add(payload);
                    if (owner.StartsWith("kotlin.", StringComparison.Ordinal) && payload.Recv.Value != "dispatch")
                    {
                        var shortKey = (method.Name.Value, parameters.Length, method.TypeParams.OrEmpty().Length);
                        if (!ownerless.TryGetValue(shortKey, out var choices)) ownerless[shortKey] = choices = new();
                        choices.Add(payload);
                    }
                }
                var nested = declaration is BirFileDeclaration root ? root.Types.OrEmpty() : Array.Empty<IBirTypeDeclaration>();
                foreach (var type in nested)
                {
                    var name = type switch {
                        BirClassDefinition value => value.Name.OrDefault(), BirInterfaceDefinition value => value.Name.OrDefault(),
                        BirEnumDefinition value => value.Name.OrDefault(), _ => null,
                    };
                    if (name != null) Owner((BirGraphValue)type, name);
                }
            }
            Owner(file, file.FileClass.OrDefault());
        }
    }

    internal BirInlinePayload Resolve(BirCallInline call, ReferenceMetadataIndex references)
    {
        var owner = (call.Owner.OrDefault() as TypeNode.Fqn)?.Name;
        var callee = (call.Callee.OrDefault() as TypeNode.Fqn)?.Name;
        var name = callee?.Contains('.') == true ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        var result = InlinePayloadSelection.Resolve<BirInlinePayload>(call.DeclarationId.OrDefault(), owner, name, call.Pc.OrDefault(), call.Ga.OrDefault(),
            call.ParamSig.OrDefault(), id => identities.GetValueOrDefault(id), references == null ? null : references.InlineValueByDeclarationIdentity,
            (o, n, p, a) => candidates.GetValueOrDefault((o, n, p, a)), references == null ? null : references.InlineValueCandidates,
            (n, p, a) => ownerless.GetValueOrDefault((n, p, a)), references == null ? null : references.OwnerlessInlineValueCandidates,
            payload => payload.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray());
        if (result.Payload == null) throw new InvalidOperationException("bir2cir: default witness demand: " + result.Diagnostic);
        return (BirInlinePayload)result.Payload.DeepClone();
    }
}

sealed partial class ReferenceMetadataIndex
{
    internal BirInlinePayload InlineValueByDeclarationIdentity(string id)
    {
        if (id != null && _conflictingInlineDeclarationIds.Contains(id))
            throw new InvalidOperationException($"conflicting inline declaration identity '{id}'");
        return id != null && _inlinePayloadByDeclarationId.TryGetValue(id, out var payload) ? (BirInlinePayload)payload.DeepClone() : null;
    }
    internal IReadOnlyList<BirInlinePayload> InlineValueCandidates(string owner, string name, int pc, int ga)
        => owner == null || name == null ? null : CloneInlineValues(_inlinePayloads.GetValueOrDefault($"{owner}|{name}|{pc}|{ga}"));
    internal IReadOnlyList<BirInlinePayload> OwnerlessInlineValueCandidates(string name, int pc, int ga)
        => name == null ? null : CloneInlineValues(_ownerlessInlineCandidates.GetValueOrDefault($"{name}|{pc}|{ga}"));
    static IReadOnlyList<BirInlinePayload> CloneInlineValues(IReadOnlyList<BirInlinePayload> payloads)
        => payloads?.Select(payload => (BirInlinePayload)payload.DeepClone()).ToArray();
}
