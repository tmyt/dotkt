using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    // Make evaluation order explicit at the consuming expression itself.
    // Only the receiver and operands *before* the captured result are
    // evaluated outside its continuation. Subsequent operands stay lexical.
    static BirFunctionDeclaration ResultExpression(BirGraphValue node, BirTypeParameter[] methodFrame, TypeNode lexicalReturn,
        ReferenceMetadataIndex refs, bool finishFixed, out ResultReturn relay)
    {
        relay = null;
        var values = node switch {
            BirCallInstance i => i.Args.OrDefault(), BirGenericInstance i => i.Args.OrDefault(),
            BirCallStatic s => s.Args.OrDefault(), BirGenericStatic s => s.Args.OrDefault(), _ => null,
        };
        if (values == null) return null;
        TypeNode AtSite(TypeNode t, TypeNode.Fqn head, Invocation call) =>
            FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(SignatureValueTypes.Of(t),
                head.Args ?? [], call.TypeArguments.OrEmpty());
        TypeNode type = null;
        int position = 0;
        for (; position < values.Length; position++)
        {
            if (values[position] is not BirGraphValue { MethodWitnessFacts.HasValue: true } arg || !LiveResultUse.InCurrentPass(arg, finishFixed)) continue;
            var producer = new Invocation(arg);
            if (producer.Member?.DeclaringType.OrDefault() is not TypeNode.Fqn target) continue;
            type = AtSite(producer.Member.ReturnType.Value, target, producer);
            if (ByRefLike(type, refs)) break;
        }
        if (position == values.Length) return null;
        var site = new Invocation(node);
        if (site.Member?.DeclaringType.OrDefault() is not TypeNode.Fqn owner) return null;
        var result = AtSite(site.Member.ReturnType.Value, owner, site);
        if (result is TypeNode.ByRef || ByRefLike(result, refs)) return null;
        var prefix = new List<IBirStatement>();
        BirLocal Bind(TypeNode storage, IBirExpression init) {
            var id = CallEvalLowering.FreshBindingId();
            prefix.Add(new BirVariable { Name = id, Type = storage, Init = OptionalField<IBirExpression>.Of(
                storage is TypeNode.ByRef && init is not BirByRefOf ? new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(init) } : init) });
            return new BirLocal { Name = id, Sty = storage };
        }
        // Keep this exact selected occurrence, including its declaration and
        // source facts. Only the operands' evaluation sites move.
        if (site.Instance)
        {
            bool address = refs.IsValueType(owner) && TypedCallEvalLowering.IsLvalueFormer(site.Receiver);
            var receiver = Bind(address ? new TypeNode.ByRef(owner) : owner, site.Receiver);
            if (site.Node is BirCallInstance i) i.Recv = receiver;
            else ((BirGenericInstance)site.Node).Recv = receiver;
        }
        for (int i = 0; i < position; i++)
            values[i] = Bind(AtSite(site.Member.ParameterTypes.Value[i], owner, site), values[i]);
        values[position] = Bind(type, values[position]);
        relay = ResultReturn.Extract(TypedProtectedRegions.Expressions([(IBirExpression)site.Node]), lexicalReturn, result, prefix);
        return new BirFunctionDeclaration { Ret = result, TypeParams = methodFrame, Params = Array.Empty<BirParameter>(),
            Body = prefix.Concat(Body(site.Node, IsVoid(result), false)).ToArray() };
    }

    static BirValueBlock ExpressionBody(BirFunctionDeclaration region, ResultReturn relay)
    {
        var statements = region.Body.Value;
        var result = statements[^1] switch {
            BirReturn r => r.Value, BirExpressionStatement s => s.Expr, _ => throw new InvalidOperationException("Missing result expression"),
        };
        var block = new BirValueBlock { Type = region.Ret, Stmts = statements[..^1], Result = result };
        return relay?.Replay(block) ?? block;
    }

    // A return or branch leaving the outlined code must resume at its
    // original caller/label. Carry the selected exit as data so Kotlin
    // catch/finally behavior does not depend on the capture boundary.
    sealed class ResultReturn
    {
        readonly string pending = CallEvalLowering.FreshBindingId(), saved = CallEvalLowering.FreshBindingId();
        readonly List<IBirStatement> transfers = [];
        static BirVariable Variable(string name, TypeNode type, IBirExpression init) =>
            new() { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(init) };
        static BirSetLocal Set(string name, IBirExpression value) => new() { Name = name, Value = OptionalField<IBirExpression>.Of(value) };
        internal static ResultReturn Extract(BirGraphSlot[] parts, TypeNode caller, TypeNode result, List<IBirStatement> prefix)
        {
            ResultReturn plan = null;
            var hasValue = false;
            foreach (var edge in TypedProtectedRegions.Edges(parts, externalOnly: true).AsEnumerable().Reverse()) {
                var slot = edge.Slot;
                var node = slot.Value;
                plan ??= new();
                var value = node switch { BirReturn r => r.Value, BirReturnExpression e => e.Value, _ => default };
                var pre = new List<IBirStatement>();
                if (value.HasValue) {
                    hasValue |= !IsVoid(caller);
                    pre.Add(IsVoid(caller) ? new BirExpressionStatement { Expr = value } : Set(plan.saved, value.Value));
                }
                plan.transfers.Add(node switch {
                    BirReturn or BirReturnExpression => new BirReturn { Value = !value.HasValue || IsVoid(caller) ? default :
                        OptionalField<IBirExpression>.Of(new BirLocal { Name = plan.saved }) },
                    BirBranchIf branch => new BirGoto { Id = branch.Id },
                    _ => (IBirStatement)node,
                });
                pre.Add(Set(plan.pending, Number(plan.transfers.Count)));
                var defaultResult = IsVoid(result) ? default(OptionalField<IBirExpression>) :
                    OptionalField<IBirExpression>.Of(new BirDefaultValue { Type = result });
                if (node is BirReturnExpression)
                    slot.Replace(new BirValueBlock { Stmts = pre.ToArray(), Result = new BirReturnExpression { Value = defaultResult } });
                else {
                    pre.Add(new BirReturn { Value = defaultResult });
                    slot.Replace(node is BirBranchIf branch ? new BirIf { Branches = new[] { new BirBranch {
                        Cond = branch.On.Value ? branch.Cond : OptionalField<IBirExpression>.Of(new BirUnaryOperation { Op = "!", E = branch.Cond }),
                        Body = pre.ToArray() } } } : new BirBlock { Body = pre.ToArray() });
                }
            }
            if (plan == null) return null;
            prefix.Insert(0, Variable(plan.pending, Int, Number(0)));
            if (hasValue) prefix.Insert(0, Variable(plan.saved, caller, new BirDefaultValue { Type = caller }));
            return plan;
        }
        internal BirValueBlock Replay(BirValueBlock block)
        {
            var tail = block.Stmts.Value.ToList();
            var id = CallEvalLowering.FreshBindingId();
            bool unit = IsVoid(block.Type.Value);
            tail.Add(unit ? new BirExpressionStatement { Expr = block.Result } : Variable(id, block.Type.Value, block.Result.Value));
            tail.Add(new BirIf { Branches = transfers.Select((t,i) => new BirBranch {
                Cond = new BirBinaryOperation { Op = "==", Lhs = new BirLocal { Name = pending }, Rhs = Number(i+1) },
                Body = new[] { t },
            }).ToArray() });
            return block with { Stmts = tail.ToArray(), Result = OptionalField<IBirExpression>.Of(unit ?
                new BirConstant { Type = block.Type, Value = new BirLiteral.NullLiteral() } : new BirLocal { Name = id, Sty = block.Type }) };
        }
    }

    static BirGraphValue BindLocalResultConsumer(Invocation site, string name, TypeNode exact)
    {
        if (site.Member?.DeclaringType.OrDefault() is not TypeNode.Fqn owner ||
            site.Arguments.OrDefault() is not { } values || site.TypeArguments.OrDefault() is not { } authored)
            return site.Node;
        var formals = site.Member.ParameterTypes.Value.Select(SignatureValueTypes.Of).ToArray();
        var args = new TypeNode[authored.Length];
        bool IsResult(IBirExpression v) => v is BirLocal local && local.Name.OrDefault() == name;
        var origins = Enumerable.Range(0, values.Length).Where(i => IsResult(values[i])).ToArray();
        if (origins.Length == 0 || !origins.All(i => BindResult(
                SupertypeGraph.SubstOwnerTvs(formals[i], owner.Args ?? []), exact, args))) return site.Node;
        for (int i = 0; i < args.Length; i++) args[i] ??= authored[i];
        TypeNode Close(TypeNode t, TypeNode[] a) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(t, owner.Args ?? [], a);
        var arguments = values.Select((arg, i) => {
            var closed = Close(formals[i], args);
            return IsResult(arg) || closed.Equals(Close(formals[i], authored)) || closed is TypeNode.ByRef ? arg : Cast(closed, arg);
        }).ToArray();
        var result = Close(SignatureValueTypes.Of(site.Member.ReturnType.Value), args);
        var call = site.Forward(owner, args.Length, formals, result, false);
        switch (call)
        {
            case BirCallStatic s: s.Args = arguments; s.TypeArgs = args; break;
            case BirGenericStatic s: s.Args = arguments; s.TypeArgs = args; break;
            case BirCallInstance c: c.Args = arguments; c.TypeArgs = args; c.Recv = OptionalField<IBirExpression>.Of(site.Receiver); break;
            case BirGenericInstance c: c.Args = arguments; c.TypeArgs = args; c.Recv = OptionalField<IBirExpression>.Of(site.Receiver); break;
        }
        return call;
    }

    static bool BindResult(TypeNode formal, TypeNode actual, TypeNode[] args)
    {
        if (formal is TypeNode.Tv { Scope: "method" } variable)
        {
            ref var binding = ref args[variable.I];
            return (binding ??= actual).Equals(actual);
        }
        if (formal is TypeNode.Fqn f && actual is TypeNode.Fqn a && f.Name == a.Name &&
            f.Args is { } fs && a.Args is { } xs && fs.Length == xs.Length)
            return fs.Zip(xs).All(pair => BindResult(pair.First, pair.Second, args));
        if (formal is TypeNode.ByRef fr && actual is TypeNode.ByRef ar) return BindResult(fr.Of, ar.Of, args);
        if (formal is TypeNode.Array fa && actual is TypeNode.Array aa && fa.Rank == aa.Rank &&
            fa.SzArray == aa.SzArray) return BindResult(fa.Elem, aa.Elem, args);
        return formal.Equals(actual);
    }
}
