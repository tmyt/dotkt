using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ClosureSynthesis;

static partial class DefaultArgSplice
{
    internal static Action PrepareInlineDefaults(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var restore = new List<Action>();
        var declarations = new TypedInlineBirIndex(roots);
        foreach (var file in roots)
        {
            var hoist = new List<BirFunctionDeclaration>();
            var materialized = new List<MaterializedOccurrence>();
            var owner = file.FileClass.OrDefault();
            var expansion = new TypedDefaultExpansion(refs, hoist, owner, "source default preparation");
            var fileContext = new CaptureContext(owner ?? "<file>", file.Pos.OrDefault()?.F.OrDefault(), file.Pos.OrDefault()?.L.OrDefault());
            void Walk(BirGraphValue node, CaptureContext context)
            {
                if (node == null || node is BirAttribute or IBirExecutablePayload) return;
                context = ContextFor(node, context);
                foreach (var slot in ExpandMaterialized(node, expansion, hoist))
                    materialized.Add(new MaterializedOccurrence(slot, context));
                if (node is BirCallInline call && call.Args.HasValue && call.Args.Value.Any(argument => argument == null)
                    && !call.PreparedDefaults.HasValue)
                {
                    var payload = declarations.Resolve(call, refs, "inline default preparation");
                    var parameters = payload.Params.Value;
                    var extension = payload.Recv.OrDefault() == "extensionParam";
                    var prepared = new IBirExpression[call.Args.Value.Length];
                    for (var index = 0; index < prepared.Length; index++)
                    {
                        if (call.Args.Value[index] != null) continue;
                        var slot = index + (extension ? 1 : 0);
                        var parameter = parameters[slot];
                        var expression = parameter.Default.HasValue ? Clone(parameter.Default.Value)
                            : MaterializeDefault(ParameterDefault(parameter)
                                ?? throw new InvalidOperationException("Omitted inline argument has no default"), hoist, owner);
                        expression = (IBirExpression)Rehome((BirGraphValue)expression, call.SemanticOwner.OrDefault() ?? owner, FreshDefaultIdentity());
                        expression = (IBirExpression)TypedExecutableFrames.Prebind((BirGraphValue)expression);
                        expression = (IBirExpression)TypedExecutableFrames.Substitute((BirGraphValue)expression, call.TypeArgs.OrEmpty(),
                            payload.TypeParams.OrEmpty().Length, call.Recvs.OrDefault()?.DispatchTypeArgs.OrDefault());
                        prepared[index] = expression;
                        var at = index;
                        materialized.Add(new MaterializedOccurrence(new BirGraphSlot(
                            () => (BirGraphValue)call.PreparedDefaults.Value[at],
                            replacement => call.PreparedDefaults.Value[at] = (IBirExpression)replacement), context));
                    }
                    call.PreparedDefaults = prepared;
                }
                node.VisitChildSlots(slot => Walk(slot.Value, context));
            }
            Walk(file, fileContext);
            // Keep a monotone drain so recorded slots continue to address the
            // actual helper occurrence after it is attached to the file.
            for (var index = 0; index < hoist.Count; index++)
            {
                var helper = hoist[index];
                Walk(helper, fileContext);
                if (!file.Methods.HasValue) throw new InvalidOperationException("Default preparation has no methods for carried helpers");
                file.Methods = file.Methods.Value.Append(helper).ToArray();
            }
            var closureFrames = PreparedClosureDefaultFrames.Stage(file, materialized, refs);
            var suspendFrames = PreparedSuspendDefaultFrames.Stage(file,
                materialized.Concat(closureFrames.Types.Select(type => new MaterializedOccurrence(
                    new BirGraphSlot(() => type, _ => throw new InvalidOperationException("A staged declaration cannot change kind")), fileContext))));
            restore.Add(() => { suspendFrames(); closureFrames.Restore(); });
        }
        return () => { foreach (var action in restore) action(); };
    }

    // Track only values the shared omission expander actually introduces. The
    // slots, not snapshots of their current values, survive SAM normalization.
    static IReadOnlyList<BirGraphSlot> ExpandMaterialized(BirGraphValue node, TypedDefaultExpansion expansion,
        List<BirFunctionDeclaration> hoist)
    {
        var pending = new List<BirCallBinding>();
        var vectors = new List<(Func<IBirExpression[]> Read, int Count)>();
        void Bindings(BirCallBinding[] bindings) => pending.AddRange(bindings.Where(binding => binding.Expr.OrDefault() is BirDefaultArgument));
        void Arguments(Func<IBirExpression[]> read) { if (read() is { } values) vectors.Add((read, values.Length)); }
        if (node is BirCallEvaluation evaluation && evaluation.Expr.HasValue)
        {
            Bindings(evaluation.Bindings.OrEmpty());
            switch (evaluation.Expr.Value)
            {
                case BirNew call: Arguments(() => call.Args.OrDefault()); break;
                case BirCallStatic call: Arguments(() => call.Args.OrDefault()); break;
                case BirCallInstance call: Arguments(() => call.Args.OrDefault()); break;
            }
        }
        foreach (var constructor in ConstructorOwner(node).Constructors)
        {
            Bindings(constructor.DelegationBindings.OrEmpty());
            Arguments(() => constructor.BaseArgs.OrDefault());
            Arguments(() => constructor.ThisArgs.OrDefault());
        }
        var firstHoist = hoist.Count;
        expansion.ExpandNode(node);
        var result = pending.Where(binding => binding.Expr.HasValue && binding.Expr.Value is not BirDefaultArgument)
            .Select(binding => new BirGraphSlot(() => (BirGraphValue)binding.Expr.Value,
                replacement => binding.Expr = OptionalField<IBirExpression>.Of((IBirExpression)replacement))).ToList();
        foreach (var (read, count) in vectors)
            for (var index = count; index < read().Length; index++)
            {
                var at = index;
                result.Add(new BirGraphSlot(() => (BirGraphValue)read()[at], replacement => read()[at] = (IBirExpression)replacement));
            }
        for (var index = firstHoist; index < hoist.Count; index++)
        {
            var at = index;
            result.Add(new BirGraphSlot(() => hoist[at], replacement => hoist[at] = (BirFunctionDeclaration)replacement));
        }
        return result;
    }
}
