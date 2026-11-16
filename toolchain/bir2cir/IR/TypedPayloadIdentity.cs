using System;
using System.Collections.Generic;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LexicalDeclarationIds
{
    internal static void Freshen(params BirGraphValue[] roots)
    {
        var clone = FreshCloneIdentity();
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        var generated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
            root?.Visit(node => {
                if (node is BirFunctionDeclaration { Params.HasValue: true, Body.HasValue: true, DeclarationId.HasValue: true } method && method.Generated.OrDefault()
                    && !generated.TryAdd(method.DeclarationId.Value, DeclarationIdentityBinding.PhysicalOnlyId(method.DeclarationId.Value, "clone:" + clone)))
                    throw new InvalidOperationException("duplicate generated declaration identity in cloned BIR payload");
                if (node is BirLocalFunction { Id.HasValue: true } local && !locals.TryAdd(local.Id.Value, local.Id.Value + "$clone" + clone))
                    throw new InvalidOperationException($"duplicate local function declaration id '{local.Id.Value}' in one cloned BIR payload");
            });
        if (locals.Count == 0 && generated.Count == 0) return;
        string Rename(string id) => id != null && generated.TryGetValue(id, out var next) ? next : id;
        foreach (var root in roots)
            root?.Visit(node => {
                switch (node)
                {
                    case BirFunctionDeclaration value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirConstructor value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirCallStatic value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirCallInstance value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirNewDelegate value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirCallInline value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirNewBoundDelegate value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirConstrainedCall value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirOverride value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirEventAdd value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirEventRemove value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirPropertyGet value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                    case BirPropertySet value when value.DeclarationId.HasValue: value.DeclarationId = Rename(value.DeclarationId.Value); break;
                }
                switch (node)
                {
                    case BirLocalFunction value when value.Id.HasValue && locals.TryGetValue(value.Id.Value, out var id): value.Id = id; break;
                    case BirCallLocal value when value.Id.HasValue && locals.TryGetValue(value.Id.Value, out var id): value.Id = id; break;
                    case BirLocalFunctionReference value when value.Id.HasValue && locals.TryGetValue(value.Id.Value, out var id): value.Id = id; break;
                }
            });
    }
}

static partial class CallEvalLowering
{
    internal static void FreshenPlanIds(BirGraphValue root)
    {
        void Walk(BirGraphValue node)
        {
            var bindings = node switch {
                BirCallEvaluation plan => plan.Bindings.OrEmpty(), BirConstructor constructor => constructor.DelegationBindings.OrEmpty(),
                _ => Array.Empty<BirCallBinding>(),
            };
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
                if (binding.Id.HasValue)
                {
                    var old = binding.Id.Value;
                    var fresh = FreshBindingId();
                    names[old] = fresh;
                    binding.Id = fresh;
                }
            if (names.Count != 0)
                node.Visit(child => {
                    if (child is BirBindingReference { Id.HasValue: true } read && names.TryGetValue(read.Id.Value, out var fresh)) read.Id = fresh;
                });
            node.VisitChildren(Walk);
        }
        Walk(root);
    }
}
