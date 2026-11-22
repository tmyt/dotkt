using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class CapturedGenericInvocation
{
    // This edge denotes this producer's result, not all expressions of the
    // same projected Kotlin type. Match in the selected consumer's signature.
    internal class NativeResultUse(Invocation consumer, BirGraphValue producer)
    {
        internal BirGraphValue Producer => producer;
        internal BirGraphValue Replacement;

        internal static NativeResultUse Direct(BirGraphValue node)
        {
            if (!Invocation.Admits(node)) return null;
            var consumer = new Invocation(node);
            if (consumer.Instance || consumer.Member == null ||
                consumer.Arguments.OrDefault() is not [BirGraphValue producer] ||
                !producer.MethodWitnessFacts.HasValue || !Invocation.Admits(producer)) return null;
            return new(consumer, producer);
        }

        internal virtual IEnumerable<(TypeNode Type, IBirExpression Value)> Environment => [];

        internal virtual bool TryClose(TypeNode exact, ReferenceMetadataIndex refs, out TypeNode result,
            out Func<IBirExpression, IBirExpression[], IBirExpression> consume)
        {
            result = null; consume = null;
            if (consumer.Member.DeclaringType.OrDefault() is not TypeNode.Fqn owner ||
                consumer.Member.ParameterTypes.OrDefault() is not [var parameter] ||
                consumer.TypeArguments.OrDefault() is not { } authored ||
                !TrySelectedMethod(consumer, owner, refs, out var selected) || !selected.IsPublic)
                return false;
            var args = new TypeNode[authored.Length];
            bool Bind(TypeNode formal, TypeNode actual)
            {
                if (formal is TypeNode.Tv { Scope: "method" } variable)
                {
                    ref var binding = ref args[variable.I];
                    return (binding ??= actual).Equals(actual);
                }
                if (formal is TypeNode.Fqn f && actual is TypeNode.Fqn a && f.Name == a.Name &&
                    f.Args is { } fs && a.Args is { } xs && fs.Length == xs.Length)
                    return fs.Zip(xs).All(pair => Bind(pair.First, pair.Second));
                if (formal is TypeNode.ByRef fr && actual is TypeNode.ByRef ar) return Bind(fr.Of, ar.Of);
                if (formal is TypeNode.Array fa && actual is TypeNode.Array aa && fa.Rank == aa.Rank &&
                    fa.SzArray == aa.SzArray) return Bind(fa.Elem, aa.Elem);
                return formal.Equals(actual);
            }
            var ret = SignatureValueTypes.Of(consumer.Member.ReturnType.Value);
            bool Open(TypeNode t) { bool found = false; Map(t, v => { found = true; return v; }); return found; }
            if (Open(owner) || Open(ret) || RequiresTypedSlot(selected.ReturnType) ||
                !Bind(SupertypeGraph.SubstOwnerTvs(SignatureValueTypes.Of(parameter), owner.Args ?? []), exact) ||
                args.Any(arg => arg == null)) return false;
            result = ret;
            consume = (value, _) => {
                var call = consumer.Forward(owner, args.Length, [parameter], ret, false);
                switch (call)
                {
                    case BirCallStatic s: s.TypeArgs = args; s.Args = new[] { value }; break;
                    case BirGenericStatic s: s.TypeArgs = args; s.Args = new[] { value }; break;
                }
                return (IBirExpression)call;
            };
            return true;
        }
    }
}
