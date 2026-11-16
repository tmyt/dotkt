using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Realign a generic subtype cast from the already-constructed source interface/base. If `D<X> : I<X>` and an
// expression has static type `I<object>`, a frontend cast target `D<T>` must become `D<object>` after an ABI erasure of
// I; otherwise CLR reification turns the Kotlin-erased cast into an InvalidCastException. The hierarchy equation is
// solved structurally, with ambiguity/incomplete mappings skipped.
static partial class GenericDowncastRealignment
{
    public sealed class Def
    {
        public int Arity;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
    }

    public static Dictionary<string, Def> Collect(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var type in file.Types.OrEmpty())
                if (type.Name.HasValue)
                    result[type.Name.Value] = new Def {
                        // This consumer has always used the own declaration vector,
                        // not the captured+own physical frame used by SupertypeGraph.
                        Arity = type.TypeParams.OrEmpty().Length,
                        Base = BirGraphValue.CopyType(type.Base.OrDefault()) as TypeNode.Fqn,
                        Interfaces = type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>()
                            .Select(value => (TypeNode.Fqn)BirGraphValue.CopyType(value)).ToArray(),
                    };
        return result;
    }

    static TypeNode.Fqn Realign(TypeNode.Fqn target, TypeNode.Fqn source, IReadOnlyDictionary<string, Def> defs)
    {
        if (!defs.TryGetValue(target.Name, out var def) || def.Arity == 0
            || target.Args == null || target.Args.Length != def.Arity || source.Args == null) return null;
        var symbolic = new TypeNode.Fqn(target.Name,
            Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        var super = FindSuper(symbolic, source.Name, defs);
        if (super?.Args == null || super.Args.Length != source.Args.Length) return null;
        var solved = new TypeNode[def.Arity];
        for (var i = 0; i < super.Args.Length; i++)
            if (!Unify(super.Args[i], source.Args[i], solved)) return null;
        if (solved.Any(x => x == null)) return null;
        var changed = false;
        for (var i = 0; i < solved.Length; i++) if (target.Args[i] != solved[i]) changed = true;
        return changed ? new TypeNode.Fqn(target.Name, solved) : null;
    }

    static TypeNode.Fqn FindSuper(TypeNode.Fqn start, string targetName, IReadOnlyDictionary<string, Def> defs)
    {
        var queue = new Queue<TypeNode.Fqn>(); queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.ToString())) continue;
            if (current.Name == targetName) return current;
            if (!defs.TryGetValue(current.Name, out var def)) continue;
            var args = def.Arity == 0 ? Array.Empty<TypeNode>() : current.Args;
            if (args == null || args.Length != def.Arity) continue;
            if (def.Base != null) queue.Enqueue((TypeNode.Fqn)Subst(def.Base, args));
            foreach (var iface in def.Interfaces) queue.Enqueue((TypeNode.Fqn)Subst(iface, args));
        }
        return null;
    }

    static bool Unify(TypeNode pattern, TypeNode actual, TypeNode[] solved)
    {
        if (pattern is TypeNode.Tv { Scope: "type" } tv && tv.I >= 0 && tv.I < solved.Length)
        {
            if (solved[tv.I] == null) { solved[tv.I] = actual; return true; }
            return solved[tv.I] == actual;
        }
        if (pattern is TypeNode.Fqn pf && actual is TypeNode.Fqn af && pf.Name == af.Name)
        {
            if (pf.Args == null || af.Args == null) return pf.Args == null && af.Args == null;
            return pf.Args.Length == af.Args.Length && pf.Args.Zip(af.Args, (p, a) => Unify(p, a, solved)).All(x => x);
        }
        if (pattern is TypeNode.Nullable pn && actual is TypeNode.Nullable an) return Unify(pn.Of, an.Of, solved);
        return pattern == actual;
    }

    static TypeNode Subst(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name, nested.Select(t => Subst(t, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Subst(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Subst(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(Subst(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(Subst(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Subst(fn.Ret, args), fn.Params.Select(p => Subst(p, args)).ToArray(),
            fn.Recv == null ? null : Subst(fn.Recv, args)),
        _ => type,
    };

}
