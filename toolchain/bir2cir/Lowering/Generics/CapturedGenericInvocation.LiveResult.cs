using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    // The continuation is a lexical method so its private members and return
    // statements keep their meaning. Only the adapter ever names the opened
    // native type. Ordinary caller locals are borrowed, not copied back.
    internal sealed class LiveResultUse(BirGraphValue producer, BirVariable location, int position,
        BirFunctionDeclaration enclosing, TypeNode.Fqn owner, string helper,
        Dictionary<string, TypeNode> free, bool receiver, bool valueOwner, BirTypeParameter[] ownerFrame)
    {
        internal BirGraphValue Producer => producer;
        internal string LexicalOwner => owner.Name;
        internal BirGraphValue Replacement;
        TypeNode liftedResult;
        BirTypeParameter[] liftedFrame;
        readonly HashSet<string> exactLocals = new(StringComparer.Ordinal) { location.Name.Value };
        TypeNode Element => ((TypeNode.ByRef)liftedResult).Of;
        internal TypeNode InInvoke(TypeNode type) => Map(type, v => new TypeNode.Tv("method",
            v.Scope == "type" ? v.I : ownerFrame.Length + v.I));
        internal TypeNode[] InvokeArguments => (owner.Args ?? []).Concat(enclosing.TypeParams.OrEmpty()
            .Select((_, i) => (TypeNode)new TypeNode.Tv("method", i))).ToArray();
        internal BirTypeParameter[] InvokeFrame => ownerFrame.Concat(enclosing.TypeParams.OrEmpty()).Select((p,i) => {
            var param = CloneParameter(p, InInvoke);
            param.Name = "C" + i; param.Variance = default; return param;
        }).ToArray();
        static TypeNode Reference(TypeNode type) => type is TypeNode.ByRef ? type : new TypeNode.ByRef(type);
        static BirByRefOf Address(IBirExpression value) => new() { Inner = OptionalField<IBirExpression>.Of(value) };
        internal IEnumerable<(TypeNode Type, IBirExpression Value)> Environment {
            get {
                if (receiver) yield return (valueOwner ? Reference(owner) : owner, new BirThis());
                foreach (var pair in free) yield return (Reference(pair.Value), new BirLocal { Name = pair.Key, Sty = pair.Value });
            }
        }

        internal bool TryClose(TypeNode exact, BirTypeParameter[] adapterFrame, ReferenceMetadataIndex refs, out TypeNode result,
            out Func<IBirExpression, IBirExpression[], IBirExpression> consume)
        {
            result = enclosing.Ret.Value;
            consume = null;
            if (exact is not TypeNode.ByRef && !ByRefLike(exact, refs) || result is TypeNode.ByRef || ByRefLike(result, refs)) return false;
            TypeNode Lift(TypeNode t) => Map(t, v => new TypeNode.Tv("method", enclosing.TypeParams.OrEmpty().Length + v.I));
            liftedResult = Lift(exact);
            liftedFrame = adapterFrame.Select(p => CloneParameter(p, Lift)).ToArray();
            consume = (value, env) => {
                var id = CallEvalLowering.FreshBindingId();
                var ret = InInvoke(enclosing.Ret.Value);
                var host = InInvoke(owner);
                var types = InvokeArguments.Skip(ownerFrame.Length).Select(InInvoke)
                    .Concat(adapterFrame.Select((_, i) => (TypeNode)new TypeNode.Tv("type", i))).ToArray();
                var args = new IBirExpression[] { new BirLocal { Name = id, Sty = exact } }.Concat(env.Skip(receiver ? 1 : 0)).ToArray();
                IBirExpression use = receiver
                    ? new BirCallInstance { OwnerType = host, Method = helper, Virtual = false, Recv = OptionalField<IBirExpression>.Of(env[0]),
                        Ret = ret, CalleeRet = enclosing.Ret, Sig = Signature(), TypeArgs = types, Args = args }
                    : new BirCallStatic { Owner = host, Method = helper, Ret = ret,
                        CalleeRet = enclosing.Ret, Sig = Signature(), TypeArgs = types, Args = args };
                return new BirValueBlock { Type = ret,
                    Stmts = new IBirStatement[] { new BirVariable { Name = id, Type = exact,
                        Init = OptionalField<IBirExpression>.Of(exact is TypeNode.ByRef ? Address(value) : value) } },
                    Result = OptionalField<IBirExpression>.Of(use) };
            };
            return true;
        }

        TypeNode[] Signature() => new[] { liftedResult }.Concat(free.Values.Select(Reference)).ToArray();

        internal BirFunctionDeclaration Finish(Func<Invocation, BirFunctionDeclaration> localConsumer, ReferenceMetadataIndex refs)
        {
            var body = enclosing.Body.Value;
            // A local copy of the span still has the producer's exact type.
            // Use all definitions of each slot: spelling the same projected
            // type on two independent initializers is not a witness.
            if (liftedResult is not TypeNode.ByRef)
            {
                var inputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                void Collect(BirGraphValue node)
                {
                    var (name, value) = node switch {
                        BirVariable v => (v.Name.OrDefault(), v.Init.OrDefault()),
                        BirSetLocal v => (v.Name.OrDefault(), v.Value.OrDefault()),
                        _ => ((string)null, null),
                    };
                    if (name != null && value != null)
                    {
                        if (!inputs.TryGetValue(name, out var defs)) inputs.Add(name, defs = []);
                        defs.Add((value as BirLocal)?.Name.OrDefault());
                    }
                    if (node is not (IBirExecutablePayload or BirMemberReference))
                        node.VisitCurrentFrameSlots(s => Collect(s.Value));
                }
                foreach (var stmt in body[(position+1)..]) Collect((BirGraphValue)stmt);
                bool changed;
                do {
                    changed = false;
                    foreach (var (name, defs) in inputs)
                        if (defs.All(exactLocals.Contains)) changed |= exactLocals.Add(name);
                } while (changed);
            }
            var suffix = body.Skip(position+1).Select(stmt => (IBirStatement)Rewrite((BirGraphValue)stmt, localConsumer, refs)).ToArray();
            enclosing.Body = body.Take(position).Concat(Body(Replacement, IsVoid(enclosing.Ret.Value), false)).ToArray();
            var frame = enclosing.TypeParams.OrEmpty().Select(p => (BirTypeParameter)p.DeepClone())
                .Concat(liftedFrame).ToArray();
            var method = Method(helper, frame, Signature(), enclosing.Ret.Value, suffix, !receiver, "internal");
            method.Params.Value[0].Name = location.Name;
            foreach (var (p, name) in method.Params.Value.Skip(1).Zip(free.Keys)) p.Name = name;
            return method;
        }

        BirGraphValue Rewrite(BirGraphValue node, Func<Invocation, BirFunctionDeclaration> localConsumer, ReferenceMetadataIndex refs)
        {
            if (node is IBirExecutablePayload or BirMemberReference) return node;
            // For a ref formal the load is an lvalue, not a Kotlin value
            // read. Give the operand its exact pointee before recursively
            // adapting ordinary reads to their Kotlin view.
            if (liftedResult is TypeNode.ByRef && Invocation.Admits(node))
            {
                var call = new Invocation(node);
                if (ResultConsumer(call, localConsumer) is { } selected && call.Arguments.OrDefault() is { } values)
                    for (int i = 0; i < values.Length; i++)
                        if (SignatureValueTypes.Of(selected.Parameters[i]) is TypeNode.ByRef &&
                            values[i] is BirByRefLoad read && read.Local.OrDefault() == location.Name.Value)
                            values[i] = read with { Local = default, Ptr = new BirLocal { Name = location.Name }, Elem = Element };
            }
            if (node is BirByRefOf { Inner.HasValue: true } address && address.Inner.Value is BirLocal loc &&
                free.ContainsKey(loc.Name.Value)) return new BirLocal { Name = loc.Name };
            node.VisitCurrentFrameSlots(s => s.Replace(Rewrite(s.Value, localConsumer, refs)));
            var name = location.Name.Value;
            if (Invocation.Admits(node))
                node = BindLocalResultConsumer(new Invocation(node), exactLocals, liftedResult, localConsumer, refs);
            return node switch {
                BirVariable variable when exactLocals.Contains(variable.Name.OrDefault()) => variable with { Type = liftedResult },
                BirLocal local when exactLocals.Contains(local.Name.OrDefault()) => local with { Sty = liftedResult },
                BirByRefLoad read when read.Local.OrDefault() == name =>
                    Cast(read.Elem.Value, read with { Local = default, Ptr = new BirLocal { Name = name }, Elem = Element }),
                BirByRefStore write when write.Local.OrDefault() == name =>
                    write with { Local = default, Ptr = new BirLocal { Name = name }, Elem = Element, Value = Cast(Element, write.Value.Value) },
                BirByRefLoad read when read.Local.OrDefault() is string source && free.ContainsKey(source) =>
                    read with { Local = default, Ptr = new BirLocal { Name = source } },
                BirByRefStore write when write.Local.OrDefault() is string dest && free.ContainsKey(dest) =>
                    write with { Local = default, Ptr = new BirLocal { Name = dest } },
                BirLocal local when free.TryGetValue(local.Name.Value, out var t) && t is not TypeNode.ByRef =>
                    new BirByRefLoad { Ptr = new BirLocal { Name = local.Name }, Elem = t },
                BirSetLocal set when free.TryGetValue(set.Name.Value, out var t) && t is not TypeNode.ByRef =>
                    new BirExpressionStatement { Expr = new BirByRefStore { Ptr = new BirLocal { Name = set.Name }, Elem = t, Value = set.Value } },
                _ => node,
            };
        }

        // A continuation starts at the producing occurrence. A join also
        // reachable without executing it belongs to the enclosing method.
        internal static int SingleEntryEnd(IBirStatement[] statements, int start)
        {
            var jumps = new HashSet<int>();
            var labels = new Dictionary<int,int>();
            void Scan(BirGraphValue node, int at, bool outside)
            {
                if (outside && node is BirGoto g) jumps.Add(g.Id.Value);
                if (outside && node is BirBranchIf b) jumps.Add(b.Id.Value);
                if (node is BirLabel l) labels[l.Id.Value] = at;
                node.VisitCurrentFrameSlots(child => Scan(child.Value, at, outside));
            }
            for (int i = 0; i < statements.Length; i++) Scan((BirGraphValue)statements[i], i, i <= start);
            int end = statements.Length;
            while (true)
            {
                int next = jumps.Select(id => labels.GetValueOrDefault(id, -1))
                    .Where(i => i > start && i < end).DefaultIfEmpty(end).Min();
                if (next == end) return end;
                for (int i = next; i < end; i++) Scan((BirGraphValue)statements[i], i, true);
                end = next;
            }
        }

        internal static bool InCurrentPass(BirGraphValue p, bool finishFixed)
        {
            if (!Invocation.Admits(p) || !p.MethodWitnessFacts.HasValue) return false;
            var call = SelectedInvocation(new Invocation(p), out _);
            return call.Member?.ReturnType.OrDefault() is { } ret &&
                ContainsCapturedSlot(ret, p.MethodWitnessFacts.Value.Select(w => w.Argument.Value).ToArray()) &&
                call.TypeArguments.OrDefault() is { } args && (finishFixed || !args.Any(t => t is TypeNode.Tv));
        }

        internal static BirGraphValue ProducerOf(IBirStatement statement, ReferenceMetadataIndex refs, bool finishFixed) =>
            statement is BirVariable { Type.HasValue: true, Init.HasValue: true } v &&
            (v.Init.Value is BirByRefOf r ? r.Inner.OrDefault() : v.Init.Value) is BirGraphValue { MethodWitnessFacts.HasValue: true } p &&
            InCurrentPass(p, finishFixed) && (v.Type.Value is TypeNode.ByRef || ByRefLike(v.Type.Value, refs)) ? p : null;

        internal static int BlockProducer(BirValueBlock block, ReferenceMetadataIndex refs, bool finishFixed)
        {
            if (!block.Result.HasValue || !block.Stmts.HasValue) return -1;
            var statements = block.Stmts.Value;
            var first = Array.FindIndex(statements, s => ProducerOf(s, refs, finishFixed) != null);
            return first >= 0 ? first : InCurrentPass((BirGraphValue)block.Result.Value, finishFixed) ? statements.Length : -1;
        }

        internal static LiveResultUse Find(BirFunctionDeclaration method, string ownerName, BirTypeParameter[] ownerFrame, bool valueOwner,
            ReferenceMetadataIndex refs,
            IReadOnlyDictionary<string, TypeNode> incoming, Func<string> allocate, bool finishFixed)
        {
            if (!method.Body.HasValue || !method.Ret.HasValue) return null;
            var body = method.Body.Value;
            for (var i = 0; i < body.Length; i++)
            {
                if (ProducerOf(body[i], refs, finishFixed) is not { } producer) continue;
                var v = (BirVariable)body[i];
                var scope = incoming.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                foreach (var prior in body.Take(i).OfType<BirVariable>()) scope[prior.Name.Value] = prior.Type.Value;
                var free = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
                bool needsThis = false;
                void Visit(BirGraphValue n)
                {
                    var id = n switch { BirLocal l => l.Name.OrDefault(), BirSetLocal s => s.Name.OrDefault(),
                        BirByRefLoad read => read.Local.OrDefault(), BirByRefStore write => write.Local.OrDefault(), _ => null };
                    if (id != null && scope.TryGetValue(id, out var type)) free.TryAdd(id, type);
                    needsThis |= n is BirThis;
                    if (n is not (IBirExecutablePayload or BirMemberReference)) n.VisitCurrentFrameSlots(s => Visit(s.Value));
                }
                foreach (var stmt in body.Skip(i+1)) Visit((BirGraphValue)stmt);
                var owner = new TypeNode.Fqn(ownerName, ownerFrame.Length == 0 ? null :
                    ownerFrame.Select((_, j) => (TypeNode)new TypeNode.Tv("type", j)).ToArray());
                return new(producer, v, i, method, owner, allocate(), free, needsThis, valueOwner, ownerFrame);
            }
            return null;
        }
    }
}
