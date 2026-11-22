using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    // The continuation is a lexical method so its private members and return
    // statements keep their meaning. Only the adapter ever names the opened
    // native type. Ordinary caller locals are borrowed, not copied back.
    sealed class LiveResultUse(BirGraphValue producer, BirVariable location, int position,
        BirFunctionDeclaration enclosing, TypeNode.Fqn owner, string helper,
        Dictionary<string, TypeNode> free) : NativeResultUse(null, producer)
    {
        static readonly TypeNode Element = new TypeNode.Tv("method", 0);
        static TypeNode Reference(TypeNode type) => type is TypeNode.ByRef ? type : new TypeNode.ByRef(type);
        static BirByRefOf Address(IBirExpression value) => new() { Inner = OptionalField<IBirExpression>.Of(value) };
        internal override IEnumerable<(TypeNode, IBirExpression)> Environment => free.Select(pair =>
            (Reference(pair.Value), (IBirExpression)new BirLocal { Name = pair.Key, Sty = pair.Value }));

        internal override bool TryClose(TypeNode exact, ReferenceMetadataIndex refs, out TypeNode result,
            out Func<IBirExpression, IBirExpression[], IBirExpression> consume)
        {
            result = enclosing.Ret.Value;
            consume = null;
            if (exact is not TypeNode.ByRef reference || result is TypeNode.ByRef || ByRefLike(result, refs)) return false;
            consume = (value, env) => {
                var id = CallEvalLowering.FreshBindingId();
                return new BirValueBlock { Type = enclosing.Ret,
                    Stmts = new IBirStatement[] { new BirVariable { Name = id, Type = reference, Init = Address(value) } },
                    Result = new BirCallStatic { Owner = owner, Method = helper, Ret = enclosing.Ret,
                        CalleeRet = enclosing.Ret, Sig = Signature(), TypeArgs = new[] { reference.Of },
                        Args = new IBirExpression[] { new BirLocal { Name = id, Sty = reference } }.Concat(env).ToArray() } };
            };
            return true;
        }

        TypeNode[] Signature() => new TypeNode[] { new TypeNode.ByRef(Element) }.Concat(free.Values.Select(Reference)).ToArray();

        internal BirFunctionDeclaration Finish()
        {
            var body = enclosing.Body.Value;
            var suffix = body.Skip(position+1).Select(stmt => (IBirStatement)Rewrite((BirGraphValue)stmt)).ToArray();
            enclosing.Body = body.Take(position).Concat(Body(Replacement, IsVoid(enclosing.Ret.Value), false)).ToArray();
            var method = Method(helper, [new BirTypeParameter { Name = "T" }], Signature(), enclosing.Ret.Value, suffix, true, "internal");
            method.Params.Value[0].Name = location.Name;
            foreach (var (p, name) in method.Params.Value.Skip(1).Zip(free.Keys)) p.Name = name;
            return method;
        }

        BirGraphValue Rewrite(BirGraphValue node)
        {
            if (node is IBirExecutablePayload or BirMemberReference) return node;
            if (node is BirByRefOf { Inner.HasValue: true } address && address.Inner.Value is BirLocal loc &&
                free.ContainsKey(loc.Name.Value)) return new BirLocal { Name = loc.Name };
            node.VisitCurrentFrameSlots(s => s.Replace(Rewrite(s.Value)));
            var name = location.Name.Value;
            return node switch {
                BirByRefLoad read when read.Local.OrDefault() == name =>
                    Cast(read.Elem.Value, read with { Local = default, Ptr = new BirLocal { Name = name }, Elem = Element }),
                BirByRefStore write when write.Local.OrDefault() == name =>
                    write with { Local = default, Ptr = new BirLocal { Name = name }, Elem = Element, Value = Cast(Element, write.Value.Value) },
                BirLocal local when free.TryGetValue(local.Name.Value, out var t) && t is not TypeNode.ByRef =>
                    new BirByRefLoad { Ptr = new BirLocal { Name = local.Name }, Elem = t },
                BirSetLocal set when free.TryGetValue(set.Name.Value, out var t) && t is not TypeNode.ByRef =>
                    new BirExpressionStatement { Expr = new BirByRefStore { Ptr = new BirLocal { Name = set.Name }, Elem = t, Value = set.Value } },
                _ => node,
            };
        }

        internal static LiveResultUse Find(BirFunctionDeclaration method, TypeNode.Fqn owner,
            IReadOnlyDictionary<string, TypeNode> incoming, Func<string> allocate)
        {
            if (!method.Body.HasValue || !method.Ret.HasValue) return null;
            var body = method.Body.Value;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] is not BirVariable { Type.HasValue: true, Init.HasValue: true } v ||
                    v.Type.Value is not TypeNode.ByRef || v.Init.Value is not BirByRefOf r ||
                    r.Inner.OrDefault() is not BirGraphValue producer || !producer.MethodWitnessFacts.HasValue ||
                    !Invocation.Admits(producer)) continue;
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
                if (needsThis) continue;
                return new(producer, v, i, method, owner, allocate(), free);
            }
            return null;
        }
    }
}
