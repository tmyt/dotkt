using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    // A protected native call may already be a static access thunk. Keep
    // that selected call and its full signature (including the receiver).
    static (TypeNode.Fqn Owner, TypeNode[] Parameters, TypeNode Return)? ResultConsumer(Invocation site,
        Func<Invocation, BirFunctionDeclaration> local) =>
        site.Member?.DeclaringType.OrDefault() is TypeNode.Fqn owner ?
            (owner, site.Member.ParameterTypes.Value, site.Member.ReturnType.Value) :
        site.Node is BirCallStatic { NativeAccessorTarget: { } access, Sig.HasValue: true } bridge &&
            site.Owner is TypeNode.Fqn host ? (host, [bridge.Sig.Value[0], ..access.Member.ParameterTypes.Value], access.Member.ReturnType.Value) :
        site.Owner is TypeNode.Fqn declaring && local(site) is { } method
            ? (declaring, method.Params.OrEmpty().Select(p => p.Type.Value).ToArray(), method.Ret.Value) : null;

    // Make evaluation order explicit at the consuming expression itself.
    // Only the receiver and operands *before* the captured result are
    // evaluated outside its continuation. Subsequent operands stay lexical.
    static BirFunctionDeclaration ResultExpression(BirGraphValue node, BirTypeParameter[] methodFrame, TypeNode lexicalReturn,
        ReferenceMetadataIndex refs, bool finishFixed, Func<Invocation, BirFunctionDeclaration> local, out ResultReturn relay)
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
        BirValueBlock operandBlock = null;
        int blockProducer = -1;
        Invocation site = null;
        int position = 0;
        for (; position < values.Length; position++)
        {
            // A Kotlin expression can produce a span inside an inline block.
            // Its last value feeds this argument without crossing a new CLR
            // boundary. Keep the ordinary consumer inside that same region.
            if (values[position] is BirValueBlock block && (blockProducer = LiveResultUse.BlockProducer(block, refs, finishFixed)) >= 0 &&
                ResultConsumer(site ??= new Invocation(node), local) is { } targetConsumer &&
                ByRefLike(AtSite(targetConsumer.Parameters[position], targetConsumer.Owner, site), refs))
            {
                operandBlock = block;
                break;
            }
            if (values[position] is not BirGraphValue { MethodWitnessFacts.HasValue: true } arg || !LiveResultUse.InCurrentPass(arg, finishFixed)) continue;
            var producer = new Invocation(arg);
            if (producer.Member?.DeclaringType.OrDefault() is not TypeNode.Fqn target) continue;
            type = AtSite(producer.Member.ReturnType.Value, target, producer);
            if (ByRefLike(type, refs)) break;
        }
        if (position == values.Length) return null;
        site ??= new Invocation(node);
        if (ResultConsumer(site, local) is not { } consumer) return null;
        var owner = consumer.Owner;
        var result = AtSite(consumer.Return, owner, site);
        if (result is TypeNode.ByRef || ByRefLike(result, refs)) return null;
        var prefix = new List<IBirStatement>();
        BirLocal Bind(TypeNode storage, IBirExpression init, List<IBirStatement> into = null) {
            var id = CallEvalLowering.FreshBindingId();
            (into ?? prefix).Add(new BirVariable { Name = id, Type = storage, Init = OptionalField<IBirExpression>.Of(
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
            values[i] = Bind(AtSite(consumer.Parameters[i], owner, site), values[i]);
        var remaining = TypedProtectedRegions.Expressions([(IBirExpression)site.Node]);
        IBirStatement[] tail = [];
        if (operandBlock == null) values[position] = Bind(type, values[position]);
        else
        {
            var statements = operandBlock.Stmts.Value;
            int cut = Math.Min(blockProducer+1, statements.Length);
            prefix.AddRange(statements[..cut]);
            var suffix = statements[cut..].ToList();
            values[position] = operandBlock.Result.Value is BirLocal l ? l :
                Bind(AtSite(consumer.Parameters[position], owner, site), operandBlock.Result.Value,
                    blockProducer == statements.Length ? prefix : suffix);
            tail = suffix.ToArray();
            remaining = [..TypedProtectedRegions.Statements(tail), ..remaining];
        }
        relay = ResultReturn.Extract(remaining, lexicalReturn, result, prefix);
        prefix.AddRange(tail);
        return ResultFrame(prefix, (IBirExpression)site.Node, result, methodFrame, relay);
    }

    static BirFunctionDeclaration ResultFrame(List<IBirStatement> prefix, IBirExpression value, TypeNode result,
        BirTypeParameter[] frame, ResultReturn exits)
    {
        prefix.AddRange(exits?.FallThrough(value) ?? Body((BirGraphValue)value, IsVoid(result), false));
        return new() { Ret = exits != null ? Int : result, TypeParams = frame, Params = Array.Empty<BirParameter>(),
            Body = prefix.ToArray() };
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
    sealed class ResultReturn(TypeNode result)
    {
        readonly string ordinary = CallEvalLowering.FreshBindingId();
        readonly List<IBirStatement> transfers = [];
        static BirVariable Variable(string name, TypeNode type, IBirExpression init) =>
            new() { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(init) };
        static BirSetLocal Set(string name, IBirExpression value) => new() { Name = name, Value = OptionalField<IBirExpression>.Of(value) };
        internal static ResultReturn Extract(BirGraphSlot[] parts, TypeNode caller, TypeNode result, List<IBirStatement> prefix)
        {
            ResultReturn plan = null;
            foreach (var edge in TypedProtectedRegions.Edges(parts, externalOnly: true).AsEnumerable().Reverse()) {
                var slot = edge.Slot;
                var node = slot.Value;
                plan ??= new(result);
                var value = node switch { BirReturn r => r.Value, BirReturnExpression e => e.Value, _ => default };
                var pre = new List<IBirStatement>();
                var saved = CallEvalLowering.FreshBindingId();
                if (value.HasValue && !IsVoid(caller))
                    prefix.Insert(0, Variable(saved, caller, new BirDefaultValue { Type = caller }));
                if (value.HasValue)
                    pre.Add(IsVoid(caller) ? new BirExpressionStatement { Expr = value } : Set(saved, value.Value));
                plan.transfers.Add(node switch {
                    BirReturn or BirReturnExpression => new BirReturn { Value = !value.HasValue || IsVoid(caller) ? default :
                        OptionalField<IBirExpression>.Of(new BirLocal { Name = saved }) },
                    BirBranchIf branch => new BirGoto { Id = branch.Id },
                    _ => (IBirStatement)node,
                });
                // Return the transfer itself through CLR unwind. A finally
                // that cancels a transfer cannot leave it pending, nor can a
                // canceled inner return overwrite an outer return's value.
                var code = Number(plan.transfers.Count);
                if (node is BirReturnExpression)
                    slot.Replace(new BirValueBlock { Stmts = pre.ToArray(), Result = new BirReturnExpression { Value = code } });
                else {
                    pre.Add(new BirReturn { Value = code });
                    slot.Replace(node is BirBranchIf branch ? new BirIf { Branches = new[] { new BirBranch {
                        Cond = branch.On.Value ? branch.Cond : OptionalField<IBirExpression>.Of(new BirUnaryOperation { Op = "!", E = branch.Cond }),
                        Body = pre.ToArray() } } } : new BirBlock { Body = pre.ToArray() });
                }
            }
            if (plan == null) return null;
            if (!IsVoid(result)) prefix.Insert(0, Variable(plan.ordinary, result, new BirDefaultValue { Type = result }));
            return plan;
        }
        internal IBirStatement[] FallThrough(IBirExpression value) =>
            [IsVoid(result) ? new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(value) } : Set(ordinary, value),
                new BirReturn { Value = Number(0) }];
        internal BirValueBlock Replay(BirValueBlock block)
        {
            var tail = block.Stmts.Value.ToList();
            var pending = CallEvalLowering.FreshBindingId();
            tail.Add(Variable(pending, Int, block.Result.Value));
            tail.Add(new BirIf { Branches = transfers.Select((t,i) => new BirBranch {
                Cond = new BirBinaryOperation { Op = "==", Lhs = new BirLocal { Name = pending }, Rhs = Number(i+1) },
                Body = new[] { t },
            }).ToArray() });
            return block with { Type = result, Stmts = tail.ToArray(), Result = OptionalField<IBirExpression>.Of(IsVoid(result) ?
                new BirConstant { Type = result, Value = new BirLiteral.NullLiteral() } : new BirLocal { Name = ordinary, Sty = result }) };
        }
    }

    static BirGraphValue BindLocalResultConsumer(Invocation site, HashSet<string> exactLocals, TypeNode exact,
        Func<Invocation, BirFunctionDeclaration> local, ReferenceMetadataIndex refs)
    {
        if (site.Arguments.OrDefault() is not { } values || site.TypeArguments.OrDefault() is not { Length: > 0 } authored ||
            ResultConsumer(site, local) is not { } consumer)
            return site.Node;
        var owner = consumer.Owner;
        var formals = consumer.Parameters.Select(SignatureValueTypes.Of).ToArray();
        var args = new TypeNode[authored.Length];
        bool IsResult(int i) => exact is TypeNode.ByRef ?
            formals[i] is TypeNode.ByRef && values[i] is BirByRefLoad { Ptr.HasValue: true } read &&
                read.Ptr.Value is BirLocal ptr && exactLocals.Contains(ptr.Name.OrDefault()) :
            values[i] is BirLocal local && exactLocals.Contains(local.Name.OrDefault());
        var origins = Enumerable.Range(0, values.Length).Where(IsResult).ToArray();
        if (origins.Length == 0 || !origins.All(i => BindResult(
                SupertypeGraph.SubstOwnerTvs(formals[i], owner.Args ?? []), exact, args, refs))) return site.Node;
        var opened = args.Select(arg => arg != null).ToArray();
        for (int i = 0; i < args.Length; i++) args[i] ??= authored[i];
        TypeNode Close(TypeNode t, TypeNode[] a) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(t, owner.Args ?? [], a);
        var arguments = values.Select((arg, i) => {
            var closed = Close(formals[i], args);
            return IsResult(i) || closed.Equals(Close(formals[i], authored)) || closed is TypeNode.ByRef ? arg : Cast(closed, arg);
        }).ToArray();
        var result = Close(SignatureValueTypes.Of(consumer.Return), args);
        var call = site.Forward(owner, args.Length,
            site.Node is BirCallStatic { NativeAccessorTarget: not null } thunk ? thunk.Sig.Value : formals,
            result, site.Member == null);
        if (site.Node.MethodWitnessFacts.HasValue)
        {
            var remaining = site.Node.MethodWitnessFacts.Value.Where(fact => !opened[fact.Argument.Value]).ToArray();
            call.MethodWitnessFacts = remaining.Length == 0 ? default : OptionalField<BirMethodWitness[]>.Of(remaining);
        }
        switch (call)
        {
            case BirCallStatic s: s.Args = arguments; s.TypeArgs = args; break;
            case BirGenericStatic s: s.Args = arguments; s.TypeArgs = args; break;
            case BirCallInstance c: c.Args = arguments; c.TypeArgs = args; c.Recv = OptionalField<IBirExpression>.Of(site.Receiver); break;
            case BirGenericInstance c: c.Args = arguments; c.TypeArgs = args; c.Recv = OptionalField<IBirExpression>.Of(site.Receiver); break;
        }
        return call;
    }

    static bool BindResult(TypeNode formal, TypeNode actual, TypeNode[] args, ReferenceMetadataIndex refs)
    {
        if (formal is TypeNode.Tv { Scope: "method" } variable)
        {
            ref var binding = ref args[variable.I];
            return (binding ??= actual).Equals(actual);
        }
        if (formal is TypeNode.Fqn f && actual is TypeNode.Fqn a && f.Args is { } fs && a.Args is { } xs && fs.Length == xs.Length &&
            (f.Name == a.Name || refs.PhysicalConstraintType(f) is TypeNode.Fqn lowered &&
                refs.ExactReflectedOwner(lowered.Name, fs.Length) == refs.ExactReflectedOwner(a.Name, xs.Length)))
            return fs.Zip(xs).All(pair => BindResult(pair.First, pair.Second, args, refs));
        if (formal is TypeNode.ByRef fr && actual is TypeNode.ByRef ar) return BindResult(fr.Of, ar.Of, args, refs);
        if (formal is TypeNode.Array fa && actual is TypeNode.Array aa && fa.Rank == aa.Rank &&
            fa.SzArray == aa.SzArray) return BindResult(fa.Elem, aa.Elem, args, refs);
        return formal.Equals(actual);
    }
}
