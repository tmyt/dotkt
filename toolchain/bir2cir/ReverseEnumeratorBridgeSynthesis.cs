using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE REVERSE ENUMERATOR BRIDGE, stated as ordinary CIR.
//
// A Kotlin class implementing `Iterable`/`Collection`/`List`/`MutableList`/`Set`/… reaches the CLR as a type whose
// supertype graph contains one of the BCL enumerable faces (`IEnumerable<E>`, `IReadOnlyList<E>`, `IList<E>`, …).
// Every one of them obliges `IEnumerator<E> GetEnumerator()`, and the class has only Kotlin's
// `iterator(): Iterator<E>` (hasNext/next). Deciding what that Kotlin meaning becomes physically is this layer's
// job, so BOTH halves of the answer are authored here as ordinary declarations, bodies and MethodImpl descriptors:
//
//   * `dotkt$EnumeratorOverKotlinIterator<T>` — a sealed, module-internal, compiler-owned adapter class that wraps a
//     Kotlin `Iterator<T>` as a BCL `IEnumerator<T>`. Kotlin source cannot express it: `IEnumerator<T>` and the
//     non-generic `IEnumerator` each declare a `Current` slot and the two differ only in return type, which is not a
//     Kotlin overload. It is emitted ONCE PER MODULE rather than shared from the runtime stdlib, because its CLR
//     identity never appears in a signature — every use of an instance is already behind `IEnumerator<E>` — so a
//     module-private copy and a shared one are indistinguishable to every consumer.
//   * on each qualifying class, a public `GetEnumerator()` returning `IEnumerator<E>` and a private
//     `dotkt$NonGenericGetEnumerator()` returning the non-generic `IEnumerator`, each carrying the exact
//     `clrInterfaceImpls` MethodImpl descriptor of the slot it fills.
//
// ilemit consequently emits a TypeDef, its fields, its methods, their bodies and their MethodImpl rows one-to-one,
// and decides nothing: no adapter name, no layout, no qualifying-class predicate, no slot selection.
//
// Runs module-wide (a class's enumerable face can be inherited through a base declared in a sibling file) and after
// every pass that can still add such a face — in particular `ReadOnlyCollectionViewInterfaces`, which states the
// read-only sibling of a mutable face. Non-ref builds only: the reference surface keeps the Kotlin faces, so no type
// in it implements a BCL enumerable interface and nothing there is owed a `GetEnumerator`.
static class ReverseEnumeratorBridgeSynthesis
{
    // #68: `dotkt$…` uses Kotlin's own unspeakable `$`, so a compiler-owned name can never collide with source.
    public const string AdapterName = "dotkt$EnumeratorOverKotlinIterator";
    const string NonGenericBridgeName = "dotkt$NonGenericGetEnumerator";
    const string GetEnumeratorName = "GetEnumerator";
    // The collision-free physical spelling of the generic bridge, for the class that already declares a nullary
    // `GetEnumerator` of its own. `$` is Kotlin's own unspeakable marker, so this can never collide with source.
    const string AliasGetEnumeratorName = "dotkt$GenericGetEnumerator";

    // The Kotlin iteration protocol the adapter wraps. `Iterable<T>.iterator(): Iterator<T>` — the member NAME is
    // part of that Kotlin contract, not a guess about a physical spelling.
    const string IteratorMember = "iterator";
    const string KotlinIterator = "kotlin.collections.Iterator";

    const string IEnumeratorT = "System.Collections.Generic.IEnumerator";
    const string IEnumerableT = "System.Collections.Generic.IEnumerable";
    const string IEnumerator = "System.Collections.IEnumerator";
    const string IEnumerable = "System.Collections.IEnumerable";
    const string IDisposable = "System.IDisposable";

    // The BCL faces that oblige `IEnumerable<E>`: `IEnumerable<E>` itself and every collection interface that derives
    // from it. Keyed on the LOWERED CLR interface identity — the Kotlin collection types that reach them were already
    // consumed by the @ClrTypeAlias substitution — so the rule holds for any source that lands on such a face.
    static readonly string[] EnumerableFaces =
    {
        IEnumerableT,
        CollectionViewFaces.IReadOnlyList,
        CollectionViewFaces.IReadOnlyCollection,
        CollectionViewFaces.IList,
        CollectionViewFaces.ICollection,
        CollectionViewFaces.ISet,
    };

    /// <summary>
    /// Author the reverse bridge across the whole compilation. Returns true when the adapter TypeDef was injected,
    /// so the caller can record it in the emission unit's local-type set.
    /// </summary>
    public static bool ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var defs = SupertypeGraph.Collect(roots);
        JsonArray adapterHost = null;
        foreach (var root in roots)
        {
            if (root is not JsonObject file || file["types"] is not JsonArray types) continue;
            foreach (var type in Declared(types))
            {
                if (!Bridge(type, defs, refs)) continue;
                // The adapter lives in the first file that owes a bridge; every other file's uses name it by the
                // same module-wide identity, exactly like any other type declared next door.
                adapterHost ??= types;
            }
        }
        if (adapterHost == null) return false;
        adapterHost.Add(Adapter());
        return true;
    }

    // Every type declaration in this file, nested types included, in declaration order.
    static IEnumerable<JsonObject> Declared(JsonArray types)
    {
        foreach (var type in types.OfType<JsonObject>())
        {
            yield return type;
            if (type["types"] is JsonArray nested)
                foreach (var child in Declared(nested)) yield return child;
        }
    }

    // Author both GetEnumerator halves on one class. False when the class is owed none.
    static bool Bridge(JsonObject type, IReadOnlyDictionary<string, SupertypeGraph.Def> defs,
        ReferenceMetadataIndex refs)
    {
        if (Str(type["kind"]) != "class") return false;                    // interfaces carry no bodies
        if (Str(type["name"]) is not string owner || owner.Length == 0) return false;
        if (type["methods"] is not JsonArray methods) return false;
        // The wrapped Kotlin iteration source is THIS class's own `iterator()` declaration. A class that OVERRIDES a
        // base's `iterator()` needs its own bridge, because the base's bridge calls the base's declaration through a
        // slot the override may not occupy; a class that declares none and whose base already carries a bridge
        // inherits that bridge, and re-declaring one would take a fresh vtable slot for no gain. (A class that
        // declares none and whose base carries none — an `iterator()` supplied by a non-enumerable superclass or by
        // an interface default — is left without a bridge; that hole predates this pass and is tracked separately.)
        if (!defs.TryGetValue(owner, out var def)) return false;
        if (methods.OfType<JsonObject>().FirstOrDefault(m => IsIteratorDeclaration(m, defs, refs))
            is not JsonObject iterator) return false;
        if (Element(def, defs, refs, TypeJson.Read(iterator["ret"])) is not { } element) return false;

        // The physical MethodDef `GetEnumerator()` may already be occupied by a Kotlin declaration of exactly that
        // CLR signature — the allocated signature is name plus generic arity plus the parameter vector, so a second
        // one is a duplicate MethodDef whatever its return type. Give the bridge a collision-free physical name in
        // that case; it is bound by its MethodImpl descriptor and never by its name, and the author's own member
        // keeps the public spelling. An OVERLOAD (`GetEnumerator(x)`) occupies a different signature entirely and
        // must not suppress the bridge, which is the slot the CLR actually demands.
        var nameTaken = methods.OfType<JsonObject>().Any(m =>
            Str(m["name"]) == GetEnumeratorName && !Bool(m["static"])
            && (m["params"] as JsonArray)?.Count == 0 && Arity(m) == 0);
        var genericName = nameTaken ? AliasGetEnumeratorName : GetEnumeratorName;

        var self = SelfOwner(owner, TypeParameterFrame.Count(type));
        var iteratorCall = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(self),
            ["virtual"] = true,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = Str(iterator["name"]),
            ["sig"] = new JsonArray(),
            ["ret"] = iterator["ret"].DeepClone(),
            ["args"] = new JsonArray(),
        };
        // `IEnumerator<E> GetEnumerator() => new dotkt$EnumeratorOverKotlinIterator<E>(this.iterator())`.
        var genericBody = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "new",
                    ["type"] = TypeJson.Write(Constructed(AdapterName, element)),
                    ["argTypes"] = new JsonArray(TypeJson.Write(Constructed(KotlinIterator, element))),
                    ["args"] = new JsonArray(iteratorCall),
                    // The adapter declares exactly one constructor and this pass authored it; naming its index is
                    // the same explicit local-declaration link every other CIR construction carries.
                    ["localCtorIndex"] = 0,
                },
            },
        };
        var generic = Method(genericName, nameTaken ? "private" : "public",
            Constructed(IEnumeratorT, element), genericBody);
        if (nameTaken) generic["generated"] = true;
        generic["clrInterfaceImpls"] = new JsonArray(Descriptor(
            Constructed(IEnumerableT, element), GetEnumeratorName, Constructed(IEnumeratorT, element)));

        // `IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator()`. The non-generic slot
        // differs from the generic one only in return type, which is not a Kotlin overload, so the body is a private
        // MethodDef named after nothing and bound by its descriptor alone.
        var nonGeneric = Method(NonGenericBridgeName, "private", new TypeNode.Fqn(IEnumerator), new JsonArray
        {
            new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = TypeJson.Write(self),
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = genericName,
                    ["sig"] = new JsonArray(),
                    ["ret"] = TypeJson.Write(Constructed(IEnumeratorT, element)),
                    ["args"] = new JsonArray(),
                },
            },
        });
        nonGeneric["generated"] = true;
        nonGeneric["clrInterfaceImpls"] = new JsonArray(Descriptor(
            new TypeNode.Fqn(IEnumerable), GetEnumeratorName, new TypeNode.Fqn(IEnumerator)));

        methods.Add(generic);
        methods.Add(nonGeneric);
        return true;
    }

    // This class's own `Iterable.iterator()` implementation: an instance, nullary, non-generic declaration under the
    // Kotlin member name whose result IS a `kotlin.collections.Iterator`. Both halves are the Kotlin contract —
    // `Iterable<T>.iterator(): Iterator<T>` — and the return is checked through the supertype graph rather than
    // against a list of names, because an override may narrow it to any subtype (`MutableIterator<T>`,
    // `ListIterator<T>`, the primitive `IntIterator`/`CharIterator`/`LongIterator`, or a user iterator class).
    static bool IsIteratorDeclaration(JsonObject method,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs) =>
        Str(method["name"]) == IteratorMember
        && !Bool(method["static"])
        && (method["params"] as JsonArray)?.Count == 0
        && Arity(method) == 0
        && TypeJson.Read(method["ret"]) is TypeNode.Fqn ret
        && ReachesKotlinIterator(ret.Name, defs, refs);

    // Is this type `kotlin.collections.Iterator`, or a subtype of it? The walk spans both provenances, exactly like
    // SupertypeGraph.Reaches, but compares BARE names: a type declared in THIS emission unit is spelled without its
    // generic arity, while the same type reached through a reference assembly keeps its metadata spelling
    // (`kotlin.collections.Iterator`1`), and the two must answer alike in a stdlib self-build and in an app build.
    static bool ReachesKotlinIterator(string from,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        var queue = new Queue<string>();
        queue.Enqueue(from);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var name = Bare(queue.Dequeue());
            if (!seen.Add(name)) continue;
            if (name == KotlinIterator) return true;
            if (defs.TryGetValue(name, out var def))
            {
                foreach (var parent in def.Interfaces) queue.Enqueue(parent.Name);
                if (def.Base != null) queue.Enqueue(def.Base.Name);
                continue;
            }
            if (refs == null) continue;
            foreach (var (parent, _) in refs.ReferencedSupertypes(name)) queue.Enqueue(parent.Name);
        }
        return false;
    }

    static string Bare(string name) => ReferenceMetadataIndex.BareOwnerFqn(name);

    // The element the class must enumerate: the argument of a BCL enumerable face it reaches, in its own type-
    // parameter frame. When several faces are reachable the one instantiated at the iterator's own element wins, so
    // the constructed adapter and the wrapped iterator agree; otherwise the first reachable face decides, which is
    // the only element any slot on this type can be about.
    static TypeNode Element(SupertypeGraph.Def def, IReadOnlyDictionary<string, SupertypeGraph.Def> defs,
        ReferenceMetadataIndex refs, TypeNode iteratorReturn)
    {
        var iteratorElement = iteratorReturn is TypeNode.Fqn { Args.Length: 1 } ret ? ret.Args[0] : null;
        TypeNode first = null;
        foreach (var (spec, isInterface) in SupertypeGraph.Reachable(def, defs, refs))
        {
            if (!isInterface || spec.Args is not { Length: 1 } args) continue;
            // Bare names: a face stated by this unit carries no arity, one reached through a reference assembly does.
            if (Array.IndexOf(EnumerableFaces, Bare(spec.Name)) < 0) continue;
            if (iteratorElement != null && SupertypeGraph.TypeKey(args[0]) == SupertypeGraph.TypeKey(iteratorElement))
                return args[0];
            first ??= args[0];
        }
        return first;
    }

    // The constructed self `Owner<!0,…,!n-1>` of a generic declaration, else the bare owner: a self-call must name
    // the instantiation `this` actually has, or the callee resolves on the open definition and mismatches it.
    static TypeNode SelfOwner(string owner, int arity) =>
        arity == 0
            ? new TypeNode.Fqn(owner)
            : new TypeNode.Fqn(owner, Enumerable.Range(0, arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());

    static TypeNode Constructed(string name, TypeNode arg) => new TypeNode.Fqn(name, new[] { arg });

    static JsonObject Descriptor(TypeNode owner, string member, TypeNode ret) => new()
    {
        ["owner"] = TypeJson.Write(owner),
        ["member"] = member,
        ["arity"] = 0,
        ["params"] = new JsonArray(),
        ["ret"] = TypeJson.Write(ret),
    };

    // Every body authored here is a straight-line `return`/`throw` except the empty `Dispose`, so state that the
    // body cannot fall through: without it the emitter appends its verifier-safe default-return epilogue, which is
    // dead code plus a `.locals init` slot on every one of these methods.
    static JsonObject Method(string name, string vis, TypeNode ret, JsonArray body, bool terminates = true)
    {
        var method = new JsonObject
        {
            ["name"] = name,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = vis,
            ["params"] = new JsonArray(),
            ["ret"] = TypeJson.Write(ret),
            ["body"] = body,
        };
        if (terminates) method["bodyTerminates"] = true;
        return method;
    }

    // ---- the adapter -----------------------------------------------------------------------------------------

    static TypeNode Tv0 => new TypeNode.Tv("type", 0);

    static JsonObject Adapter()
    {
        var element = Tv0;
        var wrapped = Constructed(KotlinIterator, element);
        var it = new JsonObject { ["k"] = "field", ["ownerType"] = SelfNode(), ["recv"] = This(), ["name"] = "_it" };

        JsonObject Wrapped(string member, TypeNode ret) => new()
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(wrapped),
            ["virtual"] = true,
            ["recv"] = it.DeepClone(),
            ["method"] = member,
            ["sig"] = new JsonArray(),
            ["ret"] = TypeJson.Write(ret),
            ["args"] = new JsonArray(),
        };

        // `bool MoveNext() { if (_it.hasNext()) { _cur = _it.next(); return true } return false }`
        var moveNext = Method("MoveNext", "public", new TypeNode.Fqn("System.Boolean"), new JsonArray
        {
            new JsonObject
            {
                ["k"] = "if",
                ["branches"] = new JsonArray(new JsonObject
                {
                    ["cond"] = Wrapped("hasNext", new TypeNode.Fqn("System.Boolean")),
                    ["body"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "setField",
                            ["ownerType"] = SelfNode(),
                            ["recv"] = This(),
                            ["name"] = "_cur",
                            ["value"] = Wrapped("next", element),
                        },
                        Return(Const("System.Boolean", true)),
                    },
                }),
            },
            Return(Const("System.Boolean", false)),
        });
        moveNext["clrInterfaceImpls"] = new JsonArray(Descriptor(
            new TypeNode.Fqn(IEnumerator), "MoveNext", new TypeNode.Fqn("System.Boolean")));

        // `T get_Current() => _cur` — the generic IEnumerator<T> slot.
        var current = Method("get_Current", "public", element,
            new JsonArray { Return(Cur()) });
        current["specialName"] = true;
        current["clrInterfaceImpls"] = new JsonArray(Descriptor(
            Constructed(IEnumeratorT, element), "get_Current", element));

        // `object System.Collections.IEnumerator.get_Current => _cur` — the non-generic slot. It differs from the
        // generic one only in return type, so it is a private MethodDef bound by its descriptor; the value-type
        // instantiation's box is the ordinary return coercion onto a reference return type.
        var rawCurrent = Method(NonGenericCurrentName, "private", new TypeNode.Fqn("System.Object"),
            new JsonArray { Return(Cur()) });
        rawCurrent["specialName"] = true;
        rawCurrent["generated"] = true;
        rawCurrent["clrInterfaceImpls"] = new JsonArray(Descriptor(
            new TypeNode.Fqn(IEnumerator), "get_Current", new TypeNode.Fqn("System.Object")));

        // `void Reset() => throw new NotSupportedException()` — a Kotlin iterator cannot be restarted.
        var reset = Method("Reset", "public", new TypeNode.Fqn("void"), new JsonArray
        {
            new JsonObject
            {
                ["k"] = "throw",
                ["value"] = new JsonObject
                {
                    ["k"] = "new",
                    ["type"] = TypeJson.Fqn("System.NotSupportedException"),
                    ["argTypes"] = new JsonArray(),
                    ["args"] = new JsonArray(),
                },
            },
        });
        reset["clrInterfaceImpls"] = new JsonArray(Descriptor(
            new TypeNode.Fqn(IEnumerator), "Reset", new TypeNode.Fqn("void")));

        // `void Dispose() {}` — nothing is held that the CLR could release.
        var dispose = Method("Dispose", "public", new TypeNode.Fqn("void"), new JsonArray(), terminates: false);
        dispose["clrInterfaceImpls"] = new JsonArray(Descriptor(
            new TypeNode.Fqn(IDisposable), "Dispose", new TypeNode.Fqn("void")));

        return new JsonObject
        {
            ["name"] = AdapterName,
            ["kind"] = "class",
            ["generated"] = true,
            ["abstract"] = false,
            ["final"] = true,
            ["beforeFieldInit"] = true,
            // Module-private: nothing outside this assembly can name the type, because no signature mentions it.
            ["vis"] = "internal",
            ["typeParams"] = new JsonArray("T"),
            ["base"] = null,
            ["interfaces"] = new JsonArray(
                TypeJson.Write(Constructed(IEnumeratorT, element)),
                TypeJson.Fqn(IEnumerator),
                TypeJson.Fqn(IDisposable)),
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "_it", ["type"] = TypeJson.Write(wrapped), ["vis"] = "private", ["initOnly"] = true },
                new JsonObject { ["name"] = "_cur", ["type"] = TypeJson.Write(element), ["vis"] = "private" }),
            ["ctors"] = new JsonArray(new JsonObject
            {
                ["params"] = new JsonArray(new JsonObject { ["name"] = "source", ["type"] = TypeJson.Write(wrapped) }),
                ["baseArgs"] = null,
                ["thisArgs"] = null,
                ["vis"] = "public",
                ["body"] = new JsonArray(new JsonObject
                {
                    ["k"] = "setField",
                    ["ownerType"] = SelfNode(),
                    ["recv"] = This(),
                    ["name"] = "_it",
                    ["value"] = new JsonObject { ["k"] = "local", ["name"] = "source" },
                }),
            }),
            ["methods"] = new JsonArray(moveNext, current, rawCurrent, reset, dispose),
        };
    }

    const string NonGenericCurrentName = "dotkt$NonGenericCurrent";

    static JsonNode SelfNode() => TypeJson.Write(Constructed(AdapterName, Tv0));
    static JsonObject This() => new() { ["k"] = "this" };
    static JsonObject Cur() => new() { ["k"] = "field", ["ownerType"] = SelfNode(), ["recv"] = This(), ["name"] = "_cur" };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };

    static JsonObject Const(string type, bool value) =>
        new() { ["k"] = "const", ["type"] = TypeJson.Fqn(type), ["value"] = value };

    static int Arity(JsonObject method) => (method["typeParams"] as JsonArray)?.Count ?? 0;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
