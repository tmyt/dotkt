using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ONE TRANSITIVE SUPERTYPE WALK, SHARED BY EVERY PASS THAT ASKS "WHAT DOES THIS TYPE INHERIT?".
//
// Two passes need the same graph and would answer differently if each walked its own. `KotlinOverrideSlotBridge`
// walks it to find the slots an erased override must fill; `ForeignNullableGenericCrossing` walks it to find the
// slots a .NET supertype declares that no Kotlin body can fill. A second copy of the walk is not a duplication of
// convenience — the first round of this work had one, it saw only DIRECT supertypes, and a class deriving from a
// .NET interface that merely EXTENDED the declaring one compiled clean and died at load. So the walk lives here
// once, and a pass that needs it asks rather than reimplements.
//
// The graph spans BOTH provenances, because a chain crosses freely between them: a Kotlin `interface KI : ITake`
// declared in this compilation is a hop on the way to a .NET declaration, and a referenced `IDerived : IBase` is a
// hop on the way to another referenced one. A LOCAL type answers from its own BIR declaration; a REFERENCED one
// answers from `ReferenceMetadataIndex.ReferencedSupertypes`, which reads the reflected shape (`GetInterfaces()`,
// already transitive, plus the direct base) of the producing assembly.
//
// Every supertype is yielded as a CONSTRUCTED spec in the STARTING type's own type-parameter frame: each hop
// substitutes the declared supertype arguments, so `class C : Derived<Int>` where `Derived<T> : Sink<T>` reaches
// `Sink<Int>` and not `Sink<T>`. A MethodImpl names the type that DECLARES the slot, so that framing is what makes
// the descriptor resolvable; a pass that only needs the NAME simply ignores the arguments.
static class SupertypeGraph
{
    // A type DECLARED IN THIS COMPILATION, indexed by its BIR name. `Node`/`Methods` are the live JSON, so a pass
    // that rewrites a declaration rewrites the tree.
    public sealed class Def
    {
        public string Name;
        public string Kind;
        public int Arity;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonObject Node;
        public JsonArray Methods;
    }

    // Every type declared across the given roots, nested types included. Later roots win on a name collision, which
    // cannot happen in a well-formed compilation and is not a decision this walk gets to make.
    public static Dictionary<string, Def> Collect(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, result);
        return result;
    }

    static void CollectFrom(JsonNode node, Dictionary<string, Def> result)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is not string name) continue;
            result[name] = new Def
            {
                Name = name,
                Kind = Str(type["kind"]),
                Arity = TypeParameterFrame.Count(type),
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Node = type,
                Methods = type["methods"] as JsonArray ?? new JsonArray(),
            };
            CollectFrom(type, result);
        }
    }

    // Every supertype this type reaches, as a CONSTRUCTED spec in its own type-parameter frame: the interface graph
    // (transitively, so a base interface's redeclared slot is reached) and the base-class chain. Bounded by a visited
    // set keyed on the constructed spec, so cyclic or repeated metadata terminates.
    public static IEnumerable<(TypeNode.Fqn spec, bool isInterface)> Reachable(Def cls,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs)
    {
        var queue = new Queue<(TypeNode.Fqn, bool)>();
        foreach (var i in cls.Interfaces) queue.Enqueue((i, true));
        if (cls.Base != null) queue.Enqueue((cls.Base, false));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (spec, isInterface) = queue.Dequeue();
            if (!seen.Add(TypeKey(spec))) continue;
            yield return (spec, isInterface);
            if (defs.TryGetValue(spec.Name, out var def))
            {
                var args = EffectiveArgs(spec, def.Arity);
                if (args == null) continue;
                foreach (var parent in def.Interfaces) queue.Enqueue(((TypeNode.Fqn)SubstOwnerTvs(parent, args), true));
                if (def.Base != null) queue.Enqueue(((TypeNode.Fqn)SubstOwnerTvs(def.Base, args), false));
                continue;
            }
            // A REFERENCED supertype's own graph continues the walk, so a slot DECLARED one level up is reached as a
            // spec of its own. That matters twice: a MethodImpl names the type that declares the slot, so a directive
            // naming the intermediate type is looked up under a spec the emitter never asks about; and a .NET
            // interface that merely EXTENDS the declaring one hands over none of its base's members to reflection.
            if (refs == null) continue;
            var refArgs = spec.Args ?? Array.Empty<TypeNode>();
            foreach (var (parent, parentIsInterface) in refs.ReferencedSupertypes(spec))
                if (SubstOwnerTvs(parent, refArgs) is TypeNode.Fqn constructed)
                    queue.Enqueue((constructed, parentIsInterface));
        }
    }

    // Declaration reachability ignores construction arguments, matching an override marker to the declaration family
    // it names while retaining exact current-format owner spelling and flattened arity. Current-format external
    // markers carry that arity in the CLR name even when they omit construction args.
    public static bool ReachesDeclaration(TypeNode.Fqn from, TypeNode.Fqn owner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs) =>
        ReachesCore(from, owner, defs, refs, exactConstruction: false);

    // Constructed reachability is used when authorization depends on the exact direct-interface instance. A class may
    // inherit `I<string>` through its base while directly re-listing only `I<int>`; matching names there would grant
    // the new declaration a MethodImpl for the wrong constructed slot.
    public static bool Reaches(TypeNode.Fqn from, TypeNode.Fqn owner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs) =>
        ReachesCore(from, owner, defs, refs, exactConstruction: true);

    static bool ReachesCore(TypeNode.Fqn from, TypeNode.Fqn owner,
        IReadOnlyDictionary<string, Def> defs, ReferenceMetadataIndex refs, bool exactConstruction)
    {
        var queue = new Queue<TypeNode.Fqn>();
        queue.Enqueue(from);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ownerKey = TypeKey(owner);
        while (queue.Count > 0)
        {
            var spec = queue.Dequeue();
            if (!seen.Add(TypeKey(spec))) continue;
            if (exactConstruction ? TypeKey(spec) == ownerKey : SameDeclaration(spec, owner)) return true;
            if (defs.TryGetValue(spec.Name, out var def))
            {
                var args = EffectiveArgs(spec, def.Arity);
                if (args == null) continue;
                foreach (var parent in def.Interfaces)
                    if (SubstOwnerTvs(parent, args) is TypeNode.Fqn constructed)
                        queue.Enqueue(constructed);
                if (def.Base != null && SubstOwnerTvs(def.Base, args) is TypeNode.Fqn baseType)
                    queue.Enqueue(baseType);
                continue;
            }
            if (refs == null) continue;
            var refArgs = spec.Args ?? Array.Empty<TypeNode>();
            foreach (var (parent, _) in refs.ReferencedSupertypes(spec))
                if (SubstOwnerTvs(parent, refArgs) is TypeNode.Fqn constructed)
                    queue.Enqueue(constructed);
        }
        return false;
    }

    static bool SameDeclaration(TypeNode.Fqn left, TypeNode.Fqn right) =>
        left.Name == right.Name && DeclarationArity(left) == DeclarationArity(right);

    static int DeclarationArity(TypeNode.Fqn type) => type.Name.Contains('`')
        ? MemberRefNode.ArityOfName(type.Name)
        : type.Args?.Length ?? 0;

    public static TypeNode[] EffectiveArgs(TypeNode.Fqn spec, int arity)
    {
        if (arity == 0) return Array.Empty<TypeNode>();
        return spec.Args is { } args && args.Length == arity ? args : null;
    }

    public static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, SubstOwnerTvs(p.Of, args)),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args), a.Rank, a.SzArray),
        TypeNode.ByRef r => new TypeNode.ByRef(SubstOwnerTvs(r.Of, args)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstOwnerTvs(p.Of, args)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, SubstOwnerTvs(m.M, args), SubstOwnerTvs(m.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(p => SubstOwnerTvs(p, args)).ToArray()),
        _ => type,
    };

    public static string TypeKey(TypeNode t) => TypeJson.Write(t).ToJsonString();

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
