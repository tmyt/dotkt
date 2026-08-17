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
    public const string NarrowingAdapterName = "dotkt$EnumeratorOverNarrowedKotlinIterator";
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
        var needsAdapter = false;
        var needsNarrowingAdapter = false;
        foreach (var root in roots)
        {
            if (root is not JsonObject file || file["types"] is not JsonArray types) continue;
            foreach (var type in Declared(types))
            {
                if (!Bridge(type, defs, refs, out var narrows)) continue;
                // The adapter lives in the first file that owes a bridge; every other file's uses name it by the
                // same module-wide identity, exactly like any other type declared next door.
                adapterHost ??= types;
                if (narrows) needsNarrowingAdapter = true;
                else needsAdapter = true;
            }
        }
        if (adapterHost == null) return false;
        if (needsAdapter) adapterHost.Add(Adapter(AdapterName, narrows: false));
        if (needsNarrowingAdapter) adapterHost.Add(Adapter(NarrowingAdapterName, narrows: true));
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
        ReferenceMetadataIndex refs, out bool narrows)
    {
        narrows = false;
        if (Str(type["kind"]) != "class") return false;                    // interfaces carry no bodies
        if (Str(type["name"]) is not string owner || owner.Length == 0) return false;
        if (type["methods"] is not JsonArray methods) return false;
        if (!defs.TryGetValue(owner, out var def)) return false;
        if (FindIteratorProvider(def, defs, refs) is not { } iterator) return false;
        if (Element(def, defs, refs, iterator.Return) is not { } element) return false;
        var iteratorElement = IteratorElement(iterator.Return, defs, refs);
        if (iteratorElement == null) return false;
        narrows = SupertypeGraph.TypeKey(iteratorElement) != SupertypeGraph.TypeKey(element);

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
            ["ownerType"] = TypeJson.Write(iterator.Owner),
            ["virtual"] = true,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = iterator.Name,
            ["sig"] = new JsonArray(),
            ["ret"] = TypeJson.Write(iterator.Return),
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
                    ["type"] = TypeJson.Write(narrows
                        ? Constructed(NarrowingAdapterName, iteratorElement, element)
                        : Constructed(AdapterName, element)),
                    ["argTypes"] = new JsonArray(TypeJson.Write(Constructed(KotlinIterator, iteratorElement))),
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
        SourceName(method) == IteratorMember
        && !Bool(method["static"])
        && (method["params"] as JsonArray)?.Count == 0
        && Arity(method) == 0
        && TypeJson.Read(method["ret"]) is TypeNode.Fqn ret
        && IteratorElement(ret, defs, refs) != null;

    // DeclarationIdentityBinding may already have allocated a collision-free CLR MethodDef name. Its retained
    // source identity is authoritative for the Kotlin protocol; the physical name is only the call operand.
    static string SourceName(JsonObject method) =>
        Str(method["declarationSourceName"])
        ?? Str(method[DeclarationRename.SourceMemberKey])
        ?? Str(method["name"]);

    sealed record IteratorProvider(TypeNode.Fqn Owner, string Name, TypeNode Return, bool Abstract);

    // Resolve the declaration that supplies `iterator()` in the receiver's own type-parameter frame. Class members
    // win before interface defaults, exactly as CLR dispatch does. For interfaces, retain only the most-specific
    // declarations; a most-specific abstract redeclaration suppresses an ancestor default, while multiple unrelated
    // defaults are rejected rather than selected by traversal order.
    static IteratorProvider FindIteratorProvider(SupertypeGraph.Def cls,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        var self = SelfOwner(cls.Name, cls.Arity) as TypeNode.Fqn;
        if (DeclaredIterator(self, cls, defs, refs, inherited: false) is { } own) return own;

        var current = cls.Base;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            if (defs.TryGetValue(current.Name, out var localBase))
            {
                if (DeclaredIterator(current, localBase, defs, refs, inherited: true) is { } inherited) return inherited;
                var args = SupertypeGraph.EffectiveArgs(current, localBase.Arity);
                if (args == null) return null;
                current = localBase.Base == null
                    ? null
                    : SupertypeGraph.SubstOwnerTvs(localBase.Base, args) as TypeNode.Fqn;
                continue;
            }
            if (ReferencedIterator(current, refs) is { } referenced) return referenced;
            var currentArgs = current.Args ?? Array.Empty<TypeNode>();
            current = refs?.ReferencedSupertypes(current.Name)
                .Where(parent => !parent.isInterface)
                .Select(parent => SupertypeGraph.SubstOwnerTvs(parent.spec, currentArgs) as TypeNode.Fqn)
                .FirstOrDefault(parent => parent != null);
        }

        var declarations = SupertypeGraph.Reachable(cls, defs, refs)
            .Where(reachable => reachable.isInterface)
            .Select(reachable =>
            {
                if (defs.TryGetValue(reachable.spec.Name, out var local))
                    return DeclaredIterator(reachable.spec, local, defs, refs, inherited: true);
                return ReferencedIterator(reachable.spec, refs);
            })
            .Where(candidate => candidate != null)
            .ToList();
        var mostSpecific = declarations.Where(candidate => !declarations.Any(other =>
                !ReferenceEquals(candidate, other)
                && SupertypeGraph.Reaches(other.Owner.Name, candidate.Owner.Name, defs, refs)))
            .ToList();
        return mostSpecific.Count == 1 && !mostSpecific[0].Abstract ? mostSpecific[0] : null;
    }

    static IteratorProvider DeclaredIterator(TypeNode.Fqn owner, SupertypeGraph.Def def,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs, bool inherited)
    {
        var args = SupertypeGraph.EffectiveArgs(owner, def.Arity);
        if (args == null) return null;
        var candidates = def.Methods.OfType<JsonObject>()
            // A private declaration is not inherited and therefore cannot suppress the interface default selected
            // for the derived class. The declaring class itself may still call its own private member, so apply this
            // accessibility boundary only while walking bases/interfaces.
            .Where(method => (!inherited || Str(method["vis"]) != "private")
                && IsIteratorDeclaration(method, defs, refs))
            .Select(method => new IteratorProvider(owner, Str(method["name"]),
                SupertypeGraph.SubstOwnerTvs(TypeJson.Read(method["ret"]), args), Bool(method["abstract"])))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    static IteratorProvider ReferencedIterator(TypeNode.Fqn owner, ReferenceMetadataIndex refs)
    {
        if (refs == null) return null;
        var candidates = refs.AccessibleDeclaredKotlinInstanceMethods(owner, IteratorMember, 0)
            .Where(method => method.Parameters.Length == 0
                && method.Return is TypeNode.Fqn ret
                && IteratorElement(ret, new Dictionary<string, SupertypeGraph.Def>(), refs) != null)
            .Select(method => new IteratorProvider(owner, method.PhysicalName, method.Return, method.IsAbstract))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    // Resolve the actual `Iterator<E>` face of the provider's declared return. The return may itself be Iterator<E>,
    // but it may equally be a non-generic primitive iterator, a user non-generic cursor, or a generic subtype whose
    // own first argument is unrelated to E. Preserve construction through every local/reference supertype edge and
    // accept exactly one E; guessing from the returned type's own arity would mis-state the adapter constructor ABI.
    static TypeNode IteratorElement(TypeNode iteratorReturn,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        if (iteratorReturn is not TypeNode.Fqn start) return null;
        var queue = new Queue<TypeNode.Fqn>();
        queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var elements = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var spec = queue.Dequeue();
            if (!seen.Add(SupertypeGraph.TypeKey(spec))) continue;
            var bare = Bare(spec.Name);
            if (bare == KotlinIterator && spec.Args is { Length: 1 } iteratorArgs)
            {
                elements.TryAdd(SupertypeGraph.TypeKey(iteratorArgs[0]), iteratorArgs[0]);
                continue;
            }

            if ((defs.TryGetValue(spec.Name, out var def) || defs.TryGetValue(bare, out def))
                && SupertypeGraph.EffectiveArgs(spec, def.Arity) is { } localArgs)
            {
                foreach (var parent in def.Interfaces)
                    if (SupertypeGraph.SubstOwnerTvs(parent, localArgs) is TypeNode.Fqn constructed)
                        queue.Enqueue(constructed);
                if (def.Base != null
                    && SupertypeGraph.SubstOwnerTvs(def.Base, localArgs) is TypeNode.Fqn constructedBase)
                    queue.Enqueue(constructedBase);
                continue;
            }

            if (refs == null) continue;
            var referencedArgs = spec.Args ?? Array.Empty<TypeNode>();
            foreach (var (parent, _) in refs.ReferencedSupertypes(spec.Name))
                if (SupertypeGraph.SubstOwnerTvs(parent, referencedArgs) is TypeNode.Fqn constructed)
                    queue.Enqueue(constructed);
        }
        return elements.Count == 1 ? elements.Values.Single() : null;
    }

    static string Bare(string name) => ReferenceMetadataIndex.BareOwnerFqn(name);

    // The element the class must enumerate: the argument of a BCL enumerable face it reaches, in its own type-
    // parameter frame. When several faces are reachable the one instantiated at the iterator's own element wins, so
    // the constructed adapter and the wrapped iterator agree; otherwise the first reachable face decides, which is
    // the only element any slot on this type can be about.
    static TypeNode Element(SupertypeGraph.Def def, IReadOnlyDictionary<string, SupertypeGraph.Def> defs,
        ReferenceMetadataIndex refs, TypeNode iteratorReturn)
    {
        var iteratorElement = IteratorElement(iteratorReturn, defs, refs);
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
    static TypeNode Constructed(string name, TypeNode first, TypeNode second) =>
        new TypeNode.Fqn(name, new[] { first, second });

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
    static TypeNode Tv1 => new TypeNode.Tv("type", 1);

    static JsonObject Adapter(string name, bool narrows)
    {
        var sourceElement = Tv0;
        var targetElement = narrows ? Tv1 : Tv0;
        var wrapped = Constructed(KotlinIterator, sourceElement);
        var self = SelfNode(name, narrows);
        var it = new JsonObject { ["k"] = "field", ["ownerType"] = self.DeepClone(), ["recv"] = This(), ["name"] = "_it" };

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

        JsonObject NextValue()
        {
            var next = Wrapped("next", sourceElement);
            return narrows
                ? new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(targetElement), ["e"] = next }
                : next;
        }

        // `bool MoveNext() { if (_it.hasNext()) { _cur = (TTarget)_it.next(); return true } return false }`.
        // The cast is explicit CIR because adapting an iterator's element to the enumerable slot is bir2cir-owned
        // representation work; ilemit only emits its ordinary box/cast/unbox sequence.
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
                            ["ownerType"] = self.DeepClone(),
                            ["recv"] = This(),
                            ["name"] = "_cur",
                            ["value"] = NextValue(),
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
        var current = Method("get_Current", "public", targetElement,
            new JsonArray { Return(Cur(self)) });
        current["specialName"] = true;
        current["clrInterfaceImpls"] = new JsonArray(Descriptor(
            Constructed(IEnumeratorT, targetElement), "get_Current", targetElement));

        // `object System.Collections.IEnumerator.get_Current => _cur` — the non-generic slot. It differs from the
        // generic one only in return type, so it is a private MethodDef bound by its descriptor; the value-type
        // instantiation's box is the ordinary return coercion onto a reference return type.
        var rawCurrent = Method(NonGenericCurrentName, "private", new TypeNode.Fqn("System.Object"),
            new JsonArray { Return(Cur(self)) });
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
            ["name"] = name,
            ["kind"] = "class",
            ["generated"] = true,
            ["abstract"] = false,
            ["final"] = true,
            ["beforeFieldInit"] = true,
            // Module-private: nothing outside this assembly can name the type, because no signature mentions it.
            ["vis"] = "internal",
            ["typeParams"] = narrows ? new JsonArray("TSource", "TTarget") : new JsonArray("T"),
            ["base"] = null,
            ["interfaces"] = new JsonArray(
                TypeJson.Write(Constructed(IEnumeratorT, targetElement)),
                TypeJson.Fqn(IEnumerator),
                TypeJson.Fqn(IDisposable)),
            ["fields"] = new JsonArray(
                new JsonObject { ["name"] = "_it", ["type"] = TypeJson.Write(wrapped), ["vis"] = "private", ["initOnly"] = true },
                new JsonObject { ["name"] = "_cur", ["type"] = TypeJson.Write(targetElement), ["vis"] = "private" }),
            ["ctors"] = new JsonArray(new JsonObject
            {
                ["params"] = new JsonArray(new JsonObject { ["name"] = "source", ["type"] = TypeJson.Write(wrapped) }),
                ["baseArgs"] = null,
                ["thisArgs"] = null,
                ["vis"] = "public",
                ["body"] = new JsonArray(new JsonObject
                {
                    ["k"] = "setField",
                    ["ownerType"] = self.DeepClone(),
                    ["recv"] = This(),
                    ["name"] = "_it",
                    ["value"] = new JsonObject { ["k"] = "local", ["name"] = "source" },
                }),
            }),
            ["methods"] = new JsonArray(moveNext, current, rawCurrent, reset, dispose),
        };
    }

    const string NonGenericCurrentName = "dotkt$NonGenericCurrent";

    static JsonNode SelfNode(string name, bool narrows) => TypeJson.Write(
        narrows ? Constructed(name, Tv0, Tv1) : Constructed(name, Tv0));
    static JsonObject This() => new() { ["k"] = "this" };
    static JsonObject Cur(JsonNode self) => new()
        { ["k"] = "field", ["ownerType"] = self.DeepClone(), ["recv"] = This(), ["name"] = "_cur" };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };

    static JsonObject Const(string type, bool value) =>
        new() { ["k"] = "const", ["type"] = TypeJson.Fqn(type), ["value"] = value };

    static int Arity(JsonObject method) => (method["typeParams"] as JsonArray)?.Count ?? 0;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
