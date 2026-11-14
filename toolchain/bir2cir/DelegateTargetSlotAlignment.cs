using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A lifted method's ordinary declaration slots must match the reified delegate
// construction that consumes it. `(T?) -> String` can become Func<object,string>
// while a concrete `(Int?) -> String` remains Func<Nullable<int>,string>.
// ECMA-335 II.14.6 requires compatible ldftn parameters and returns; nullable
// value storage and object are not directly interchangeable.
//
// Source callable references reach this pass through compiler-owned static or
// closure forwarders. The authored target stays behind an ordinary call. Once
// those owned slots follow the delegate, the normal nullable use axis narrows
// incoming objects and boxes outgoing values; this pass does not redo body flow.
static partial class DelegateTargetSlotAlignment
{
    static ValueTypeOracle _isValue = _ => false;
    static bool _moved;

    // Parameter indices address the delegate vector, including an extension
    // receiver first. Accumulate requirements from every use of one target.
    sealed class Demand
    {
        public readonly HashSet<int> Params = new();
        public bool Ret;
    }

    static void Demanded(Dictionary<string, Demand> into, string key, TypeNode.Fn fn)
    {
        if (!into.TryGetValue(key, out var d)) into[key] = d = new Demand();
        var ps = fn.DelegateParams;
        for (var i = 0; i < ps.Length; i++) if (IsBareObject(ps[i])) d.Params.Add(i);
        if (IsBareObject(fn.Ret)) d.Ret = true;
    }

    // The wildcard is only for a target without an authored sig: kotc minted
    // that unique lifted target. AMBIGUOUS MEANS NONE. Method generic arity is
    // part of a CLI signature, but the reference cannot distinguish two targets
    // with coinciding erased value vectors. Moving both would rewrite a public
    // declaration never selected by the construction; moving neither leaves a
    // malformed binding to fail at emission rather than guessing a declaration.
    static IEnumerable<(T Method, Demand Demand)> SelectedTargets<T>(IEnumerable<T> methods, string owner,
        Dictionary<string, Demand> byTarget, Func<T, string> name, Func<T, string> semanticOwner,
        Func<T, TypeNode[]> parameters)
    {
        var matched = new Dictionary<Demand, List<T>>();
        foreach (var method in methods)
        {
            if (name(method) is not { } member) continue;
            var targetOwner = semanticOwner(method) ?? owner;
            if (!byTarget.TryGetValue(targetOwner + "|" + member + "|" + SignatureKey(parameters(method)), out var demand)
                && !byTarget.TryGetValue(targetOwner + "|" + member + "|" + AnySig, out demand)) continue;
            if (!matched.TryGetValue(demand, out var hits)) matched[demand] = hits = new();
            hits.Add(method);
        }
        foreach (var (demand, hits) in matched)
            if (hits.Count == 1) yield return (hits[0], demand);
    }

    static string SignatureKey(IEnumerable<TypeNode> parameters)
        => string.Join(",", parameters.Select(type => type == null ? "?" : TypeNode.ToJson(type)));

    const string AnySig = "*";

    readonly record struct AlignmentPlan(int[] Parameters, bool Return);
    static AlignmentPlan Plan(TypeNode[] parameters, TypeNode result, Demand demand)
        => new(parameters.Select((type, index) => (type, index))
            .Where(slot => demand.Params.Contains(slot.index) && slot.type != null && !IsBareObject(slot.type))
            .Select(slot => slot.index).ToArray(), demand.Ret && result != null && NeedsObjectSeam(result));

    // Delegate parameters are CONTRAVARIANT: only an object target parameter
    // accepts every object supplied by the erased delegate, so references also
    // follow. Returns are COVARIANT: references already reach object and must
    // retain their exact declaration (otherwise Func<string> construction fails).
    // Only a value / Nullable<V> / type-variable return needs the object seam.
    static readonly TypeNode ObjFqn = new TypeNode.Fqn("object");

    static bool IsBareObject(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsBareObject(n.Of),
        TypeNode.Oblivious o => IsBareObject(o.Of),
        TypeNode.Fqn { Name: "object", Args: null } => true,
        _ => false,
    };

    // The complete construction matters: a constructed CLR struct still needs
    // boxing, whereas a generic reference does not change the return rule.
    static bool NeedsObjectSeam(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of),
        TypeNode.Fqn f => _isValue(f),
        _ => false,
    };
}
