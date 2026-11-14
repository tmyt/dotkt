using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE DELEGATE-TARGET HALF OF THE CARRIER-ARGUMENT ERASURE (#86).
//
// A delegate's parameters and return are ARGUMENTS of a reified construction (`Func`2<…>`). When those arguments
// contain an OPEN nullable type variable, NullableGenericErasure gives them the same `object` carrier used by any
// other generic argument: `(T?) -> String` is `Func<object, string>`. A concrete `(Int?) -> String` deliberately
// remains `Func<Nullable<int32>, string>`. The value BOUND into an erased delegate is a lifted method — a
// `newDelegate`'s named static, or a `newClosure`'s class `invoke` — whose own slots the erasure did NOT touch,
// because they are ordinary declaration slots.
//
// The two must agree or there is no delegate at all. ECMA-335 II.14.6 makes a `ldftn` target compatible with a
// delegate only when each of its parameters is assignable FROM the delegate's and its return assignable TO the
// delegate's: `object` and `Nullable<int32>` are assignable in neither direction, so a `Func<object, string>` built
// over a lifted generated method taking `Nullable<int32>` is rejected outright (ilverify DelegateCtor "Unrecognized arguments"; at run
// time an InvalidProgramException before the first instruction executes).
//
// A target slot FOLLOWS the delegate slot it fills wherever the funcType component is the bare `object` the erasure
// produced.
//
// Callable references reach this pass through targets kotc synthesized at the use site: a static forwarder for
// `::fn`, an instance closure for `expr::member`. The authored declaration remains behind an ordinary call in that
// target's body, so this pass moves only compiler-owned slots. Which target slots that reaches differs by position,
// because delegate compatibility is not symmetric: a PARAMETER is contravariant and only `object` is assignable
// from `object`, so every non-`object`
// parameter follows; a RETURN is covariant and a reference already reaches `object`, so only a value / `Nullable<V>`
// / type-variable return follows. Rewriting a reference RETURN is what broke the concrete-delegate ctor in #189
// (`object` is not assignable TO a `Func<string>` slot). See `Align`.
//
// Runs BETWEEN the declaration axis and the use axis, which is what makes the body side free: once the lifted
// method's parameter is `object`, NullableTvErasureCallRealign types every read of it as `object` and narrows at the
// consumer that needs a value, and once its return is `object` the `return` statement's value is boxed by the same
// write axis. Both are ordinary erasure-boundary uses; this pass states the declaration and nothing else.
static partial class DelegateTargetSlotAlignment
{
    static ValueTypeOracle _isValue = _ => false;

    static bool _moved;

    public static bool ApplyMaterialized(IEnumerable<JsonNode> roots,
        IEnumerable<JsonNode> declarations, ValueTypeOracle isValue)
    {
        _isValue = isValue ?? (_ => false);
        var statics = new Dictionary<string, Demand>(StringComparer.Ordinal);
        var closures = new Dictionary<string, Demand>(StringComparer.Ordinal);
        foreach (var root in roots) Collect(root, statics, closures);
        _moved = false;
        foreach (var declaration in declarations.OfType<JsonObject>())
        {
            AlignMethods(declaration["methods"], Str(declaration["fileClass"]) ?? Str(declaration["name"]), statics);
            AlignClosure(declaration, closures);
            AlignClosureTypes(declaration, closures, statics);
        }
        return _moved;
    }

    // The positions one delegate construction requires of its target: the parameter indices stated as `object`
    // (indexed over the funcType's DELEGATE parameters, receiver first, which is the order the lifted method declares
    // them in) and whether the return is stated as `object`.
    sealed class Demand
    {
        public readonly HashSet<int> Params = new();
        public bool Ret;
    }

    static void Collect(JsonNode node, Dictionary<string, Demand> statics, Dictionary<string, Demand> closures)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var k = Str(obj["k"]);
                // A SUSPEND fn is not a delegate at all — its value is a Continuation state machine erased to
                // `object` — so there is no funcType component for a target slot to follow.
                // A resolved direct reference retains the authored declaration's signature. Only a lifted
                // target without a source descriptor owns slots that may follow its function carrier.
                if ((k == "newClosure" || k == "newDelegate" && obj["sig"] is not JsonArray)
                    && TypeJson.Read(obj["funcType"]) is TypeNode.Fn { Suspend: false } fn)
                {
                    var key = k == "newDelegate"
                        ? (Str(obj["method"]) is string method
                            && TypeJson.Read(obj["calleeOwner"] ?? obj["owner"]) is TypeNode.Fqn owner
                                ? owner.Name + "|" + method + "|" + SigKey(obj["sig"]) : null)
                        : (TypeJson.Read(obj["closureType"]) as TypeNode.Fqn)?.Name;
                    if (key != null) Demanded(k == "newDelegate" ? statics : closures, key, fn);
                }
                foreach (var kv in obj) if (kv.Value != null) Collect(kv.Value, statics, closures);
                break;
            }
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Collect(it, statics, closures);
                break;
        }
    }

    // Accumulate across every construction that binds the same target: one lifted method may be bound into two
    // delegate slots, and a slot stated as `object` anywhere is `object` for the declaration.
    static void Demanded(Dictionary<string, Demand> into, string key, TypeNode.Fn fn)
    {
        if (!into.TryGetValue(key, out var d)) into[key] = d = new Demand();
        var ps = fn.DelegateParams;   // the receiver of a `T.() -> R` is the delegate's first parameter (#145)
        for (var i = 0; i < ps.Length; i++) if (IsBareObject(ps[i])) d.Params.Add(i);
        if (IsBareObject(fn.Ret)) d.Ret = true;
    }

    static void AlignClosureTypes(JsonObject owner, Dictionary<string, Demand> closures,
        Dictionary<string, Demand> statics)
    {
        if (owner["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject to)
            {
                AlignMethods(to["methods"], Str(to["name"]), statics);
                AlignClosure(to, closures);
                AlignClosureTypes(to, closures, statics);
            }
    }

    static void AlignClosure(JsonObject owner, Dictionary<string, Demand> closures)
    {
        if (Str(owner["name"]) is string name && closures.TryGetValue(name, out var demand)
            && owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (Str(method["name"]) == "invoke") Align(method, demand);
    }

    // A declaration is the target when its OWNER, NAME and parameter vector are the ones the construction named.
    // The wildcard arm is for a target with no `sig`: kotc omits it only for a target it MINTED — a lifted
    // `dotkt:lambda:<n>`/`dotkt:mref:<n>` — whose name is unique in the file by construction, so there is no overload to confuse
    // it with and nothing for a parameter vector to disambiguate.
    //
    // AMBIGUOUS MEANS NONE. Method generic ARITY is part of the CLI signature (ECMA-335 I.8.6.1.6) and the reference
    // does not carry the target's — `fun make(x: Any?)` and `fun <T> make(x: T?)` are two legal slots whose erased
    // parameter vectors coincide (`NullableGenericOverloadCollision` deliberately lets that pair through for exactly
    // that reason). A demand that cannot say which of them it named may not move either: moving both rewrites a
    // public signature the reference never mentions, and the malformed delegate that results from moving neither
    // fails loudly at emit instead.
    static void AlignMethods(JsonNode methods, string owner, Dictionary<string, Demand> byTarget)
    {
        if (owner == null || methods is not JsonArray a) return;
        foreach (var (method, demand) in SelectedTargets(a.OfType<JsonObject>(), owner, byTarget,
            method => Str(method["name"]), method => Str(method["semanticOwner"]),
            method => (method["params"] as JsonArray ?? new()).Select(parameter => TypeJson.Read(parameter?["type"])).ToArray()))
            Align(method, demand);
    }

    // Both owning renderers select from the same complete target set. In
    // particular an ambiguous wildcard never changes either declaration.
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

    // The construction's frontend-resolved target signature, in the same spelling `AlignMethods` reads a
    // declaration's own parameters in.
    static string SigKey(JsonNode sig) => sig is not JsonArray a
        ? AnySig
        : SignatureKey(a.Select(TypeJson.Read));

    readonly record struct AlignmentPlan(int[] Parameters, bool Return);
    static AlignmentPlan Plan(TypeNode[] parameters, TypeNode result, Demand demand)
        => new(parameters.Select((type, index) => (type, index))
            .Where(slot => demand.Params.Contains(slot.index) && slot.type != null && !IsBareObject(slot.type))
            .Select(slot => slot.index).ToArray(), demand.Ret && result != null && NeedsObjectSeam(result));

    // THE TWO POSITIONS TAKE OPPOSITE RULES, because delegate compatibility is not symmetric (ECMA-335 II.14.6):
    //
    //   * A PARAMETER is CONTRAVARIANT — the target's parameter must be assignable FROM the delegate's. The only type
    //     assignable from `object` is `object`, so EVERY non-`object` target parameter follows, reference included:
    //     an `invokeNullable<String>` whose physical parameter is `Func<object, string>` cannot be filled by a lifted
    //     a lifted method taking `string`, and the erasure makes that slot `object` T-independently.
    //   * A RETURN is COVARIANT — the target's return must be assignable TO the delegate's. A reference return
    //     already is, so it stays exactly as declared (#189: rewriting a `(…) -> String?` lambda's return to `object`
    //     is what broke the concrete-delegate ctor). Only a value / `Nullable<V>` / type-variable return, which needs
    //     a `box` to reach `object` at all, follows.
    //
    // The body side of both is ordinary erasure-boundary flow: the use axis narrows a now-`object` parameter at its
    // first typed consumer and boxes a now-`object` return at the `return`.
    static void Align(JsonObject mo, Demand d)
    {
        var parameters = (mo["params"] as JsonArray ?? new()).Select(parameter => TypeJson.Read(parameter?["type"])).ToArray();
        var plan = Plan(parameters, TypeJson.Read(mo["ret"]), d);
        var argumentPrologueCount = 0;
        if (mo["params"] is JsonArray ps)
            foreach (var i in plan.Parameters)
                if (ps[i] is JsonObject po && parameters[i] is TypeNode pt)
                {
                    if (mo["body"] is JsonArray body && Str(po["name"]) is string sourceName)
                    {
                        var physicalName = CallEvalLowering.FreshBindingId();
                        po["name"] = physicalName;
                        body.Insert(argumentPrologueCount++, new JsonObject
                        {
                            ["k"] = "var", ["name"] = sourceName, ["type"] = TypeJson.Write(pt),
                            ["init"] = new JsonObject
                            {
                                ["k"] = "cast", ["type"] = TypeJson.Write(pt),
                                // Restore the selected target's declaration slot, not a Kotlin unchecked
                                // classifier cast. Its generic arguments must survive existential lowering.
                                ["_exactBridgeCast"] = true,
                                [FunctionValueRepresentation.RestorationKey] = true,
                                ["e"] = new JsonObject
                                {
                                    ["k"] = "local", ["name"] = physicalName,
                                    ["sty"] = TypeJson.Write(ObjFqn),
                                },
                            },
                        });
                    }
                    po["type"] = TypeJson.Write(ObjFqn);
                    _moved = true;
                }
        if (plan.Return)
        {
            mo["ret"] = TypeJson.Write(ObjFqn);
            _moved = true;
        }
    }

    static readonly TypeNode ObjFqn = new TypeNode.Fqn("object");

    static bool IsBareObject(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsBareObject(n.Of),
        TypeNode.Oblivious o => IsBareObject(o.Of),
        TypeNode.Fqn { Name: "object", Args: null } => true,
        _ => false,
    };

    // Whether this target slot is one the `object` seam does NOT already cover. A value type, a structural
    // `Nullable<V>` and a type variable each need the slot itself rewritten (there is no assignability between them
    // and `object` without a `box`/`unbox.any`); a reference is assignable in both directions a delegate needs and
    // stays exactly as declared — that is the #189 rule, and rewriting it would break the concrete-delegate ctor.
    // A CONSTRUCTED struct counts because the oracle classifies its complete FQN, including arguments.
    static bool NeedsObjectSeam(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of),
        TypeNode.Fqn f => _isValue(f),
        _ => false,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
