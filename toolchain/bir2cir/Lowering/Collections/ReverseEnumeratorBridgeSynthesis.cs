using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// THE REVERSE ENUMERATOR BRIDGE, stated as ordinary CIR.
//
// A Kotlin class implementing `Iterable`/`Collection`/`List`/`MutableList`/`Set`/… reaches the CLR as a type whose
// supertype graph contains one of the BCL enumerable faces (`IEnumerable<E>`, `IReadOnlyList<E>`, `IList<E>`, …).
// Every one of them obliges `IEnumerator<E> GetEnumerator()`, and the class has only Kotlin's
// `iterator(): Iterator<E>` (hasNext/next). Deciding what that Kotlin meaning becomes physically is this layer's
// job, so BOTH halves of the answer are authored here as ordinary declarations, bodies and MethodImpl descriptors:
//
//   * `dotkt$EnumeratorOverKotlinIterator<T>` — a sealed, module-internal, compiler-owned adapter class that wraps a
//     Kotlin iterator's existential carrier as a BCL `IEnumerator<T>`. Kotlin source cannot express it: `IEnumerator<T>` and the
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
// General override allocation queries this protocol's slot ownership earlier, using the same provider/element
// rule and physical interface identities, so it cannot install a competing foreign-base forwarding bridge.
static partial class ReverseEnumeratorBridgeSynthesis
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

    // Reserve exactly the slots this pass will materialize. Earlier override allocation must not map a
    // foreign base's enumeration onto them when Kotlin's selected iterator owns the iteration protocol.
    // The comparison uses constructed physical signatures, not generated bridge names or method names alone.
    public sealed record SlotReservation(TypeNode Element)
    {
        public bool Owns(TypeNode.Fqn owner, string member, int arity, TypeNode[] parameters,
            TypeNode result, Func<TypeNode, TypeNode> physical, ReferenceMetadataIndex refs)
        {
            if (member != GetEnumeratorName || arity != 0 || parameters.Length != 0
                || physical(owner) is not TypeNode.Fqn face || physical(result) is not TypeNode.Fqn ret)
                return false;
            // A referenced Kotlin interface can inherit the CLR slot without declaring it. Resolve the exact
            // physical owner for this ownership query only; do not replace the general override pass's Kotlin
            // signature facts (notably a logical suspend result) with reflected physical return types.
            if (Bare(face.Name) is not (IEnumerable or IEnumerableT))
            {
                if (!ClrMemberResolution.TryResolveAliasedInterfaceSlot(null, refs, face, member, arity,
                        Array.Empty<TypeNode>(), null, face.Args ?? Array.Empty<TypeNode>(),
                        out var declarationOwner, out _, out _, out _, wantedReturn: ret)) return false;
                face = declarationOwner;
            }
            if (Bare(face.Name) == IEnumerable && face.Args is not { Length: > 0 })
                return Bare(ret.Name) == IEnumerator && ret.Args is not { Length: > 0 };
            return Bare(face.Name) == IEnumerableT && face.Args is { Length: 1 } args
                && Bare(ret.Name) == IEnumeratorT && ret.Args is { Length: 1 } retArgs
                && ClrMemberResolution.SameInterfaceSlotType(args[0], Element)
                && ClrMemberResolution.SameInterfaceSlotType(retArgs[0], Element);
        }
    }

    internal static SlotReservation ReserveSlots(SupertypeGraph.Def def,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, IBirTypeDeclaration> declarations)
        => Plan(def, defs, refs, declarations) is { } plan ? new SlotReservation(plan.Element) : null;

    static (IteratorProvider Iterator, TypeNode Element)? Plan(SupertypeGraph.Def def,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, IBirTypeDeclaration> declarations)
    {
        if (def.Kind != "class" || FindIteratorProvider(def, defs, refs, declarations) is not { } iterator
            || Element(def, defs, refs, iterator.SemanticReturn) is not { } element) return null;
        return (iterator, element);
    }

    // Preserved Kotlin declaration facts choose the iteration protocol; the
    // current emitted return independently selects the physical call descriptor.
    sealed record IteratorProvider(TypeNode.Fqn Owner, string Name, TypeNode Return, TypeNode SemanticReturn, bool Abstract);

    // Resolve the declaration that supplies `iterator()` in the receiver's own type-parameter frame. Class members
    // win before interface defaults, exactly as CLR dispatch does. For interfaces, retain only the most-specific
    // declarations; a most-specific abstract redeclaration suppresses an ancestor default, while multiple unrelated
    // defaults are rejected rather than selected by traversal order.
    static IteratorProvider FindIteratorProvider(SupertypeGraph.Def cls,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, IBirTypeDeclaration> declarations)
    {
        var self = SelfOwner(cls.Name, cls.Arity) as TypeNode.Fqn;
        if (DeclaredIterator(self, cls, defs, refs, inherited: false, declarations) is { } own) return own;

        var current = cls.Base;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            if (defs.TryGetValue(current.Name, out var localBase))
            {
                if (DeclaredIterator(current, localBase, defs, refs, inherited: true, declarations) is { } inherited) return inherited;
                var args = SupertypeGraph.EffectiveArgs(current, localBase.Arity);
                if (args == null) return null;
                current = localBase.Base == null
                    ? null
                    : SupertypeGraph.SubstOwnerTvs(localBase.Base, args) as TypeNode.Fqn;
                continue;
            }
            if (ReferencedIterator(current, refs) is { } referenced) return referenced;
            var currentArgs = current.Args ?? Array.Empty<TypeNode>();
            current = refs?.ReferencedSupertypes(current)
                .Where(parent => !parent.isInterface)
                .Select(parent => SupertypeGraph.SubstOwnerTvs(parent.spec, currentArgs) as TypeNode.Fqn)
                .FirstOrDefault(parent => parent != null);
        }

        var interfaceDeclarations = SupertypeGraph.Reachable(cls, defs, refs)
            .Where(reachable => reachable.isInterface)
            .Select(reachable =>
            {
                if (defs.TryGetValue(reachable.spec.Name, out var local))
                    return DeclaredIterator(reachable.spec, local, defs, refs, inherited: true, declarations);
                return ReferencedIterator(reachable.spec, refs);
            })
            .Where(candidate => candidate != null)
            .ToList();
        var mostSpecific = interfaceDeclarations.Where(candidate => !interfaceDeclarations.Any(other =>
                !ReferenceEquals(candidate, other)
                && SupertypeGraph.ReachesDeclaration(other.Owner, candidate.Owner, defs, refs)))
            .ToList();
        return mostSpecific.Count == 1 && !mostSpecific[0].Abstract ? mostSpecific[0] : null;
    }

    // This class's own `Iterable.iterator()` implementation: an instance, nullary, non-generic declaration under the
    // Kotlin member name whose result IS a `kotlin.collections.Iterator`. Both halves are the Kotlin contract —
    // `Iterable<T>.iterator(): Iterator<T>` — and the return is checked through the supertype graph rather than
    // against a list of names, because an override may narrow it to any subtype (`MutableIterator<T>`,
    // `ListIterator<T>`, the primitive `IntIterator`/`CharIterator`/`LongIterator`, or a user iterator class).
    // DeclarationIdentityBinding may already have allocated a collision-free CLR MethodDef name. Its retained
    // source identity is authoritative for the Kotlin protocol; the physical name is only the call operand.
    static IteratorProvider DeclaredIterator(TypeNode.Fqn owner, SupertypeGraph.Def def,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs, bool inherited,
        IReadOnlyDictionary<string, IBirTypeDeclaration> declarations)
    {
        var args = SupertypeGraph.EffectiveArgs(owner, def.Arity);
        if (args == null) return null;
        var candidates = IteratorDeclarations(def, declarations)
            .Where(method => (!inherited || method.Visibility != "private")
                && method.SourceName == IteratorMember && !method.Static && method.Parameters == 0
                && method.Arity == 0 && method.SemanticReturn is TypeNode.Fqn ret
                && (NodeType.IsNothing(ret) || IteratorElement(ret, defs, refs) != null))
            .Select(method => new IteratorProvider(owner, method.Name,
                SupertypeGraph.SubstOwnerTvs(method.Return, args),
                SupertypeGraph.SubstOwnerTvs(method.SemanticReturn, args), method.Abstract))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    sealed record IteratorDeclaration(string SourceName, string Name, string Visibility, bool Static,
        int Parameters, int Arity, TypeNode Return, TypeNode SemanticReturn, bool Abstract);

    static IEnumerable<IteratorDeclaration> IteratorDeclarations(SupertypeGraph.Def definition,
        IReadOnlyDictionary<string, IBirTypeDeclaration> declarations)
    {
        foreach (var method in declarations[definition.Name].Methods.OrEmpty())
            yield return new(method.DeclarationSourceName.OrDefault() ?? method.KotlinSourceMember.OrDefault() ?? method.Name.OrDefault(),
                method.Name.OrDefault(), method.Vis.OrDefault(), method.Static.OrDefault(),
                method.Params.HasValue ? method.Params.Value.Length : -1, method.TypeParams.OrEmpty().Length,
                method.Ret.OrDefault(), method.RetNothing.OrDefault() ? new TypeNode.Fqn("kotlin.Nothing")
                    : method.RetKotlinType.OrDefault() ?? method.Ret.OrDefault(), method.Abstract.OrDefault());
    }

    static IteratorProvider ReferencedIterator(TypeNode.Fqn owner, ReferenceMetadataIndex refs)
    {
        if (refs == null) return null;
        var candidates = refs.AccessibleDeclaredKotlinInstanceMethods(owner, IteratorMember, 0)
            .Where(method => method.Parameters.Length == 0
                && method.SemanticReturn is TypeNode.Fqn ret
                && (NodeType.IsNothing(ret) || IteratorElement(ret, new Dictionary<string, SupertypeGraph.Def>(), refs) != null))
            .Select(method => new IteratorProvider(owner, method.PhysicalName, method.Return, method.SemanticReturn, method.IsAbstract))
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
            foreach (var (parent, _) in refs.ReferencedSupertypes(spec))
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
        var localNames = defs.Keys.ToHashSet(StringComparer.Ordinal);
        TypeNode PhysicalElement(TypeNode type) => type == null ? null
            : BirTypeLowering.LowerPhysicalType(type, refs.Aliases, refs.IsValueType,
                refs.PhysicalTypeNames, typeArg: true, localTypeNames: localNames);
        var iteratorElement = PhysicalElement(IteratorElement(iteratorReturn, defs, refs));
        TypeNode first = null;
        foreach (var (spec, isInterface) in SupertypeGraph.Reachable(def, defs, refs))
        {
            if (!isInterface) continue;
            // Reference declarations carry CLR arity; alias bindings use the Kotlin declaration owner.
            var aliasOwner = Bare(spec.Name);
            var declaration = refs.Aliases.ContainsKey(aliasOwner) ? new TypeNode.Fqn(aliasOwner, spec.Args) : spec;
            var physicalFace = BirTypeLowering.LowerPhysicalType(declaration, refs.Aliases, refs.IsValueType,
                refs.PhysicalTypeNames, typeArg: false, localTypeNames: localNames) as TypeNode.Fqn;
            if (physicalFace?.Args is not { Length: 1 } args) continue;
            // Bare names: a face stated by this unit carries no arity, one reached through a reference assembly does.
            if (Array.IndexOf(EnumerableFaces, Bare(physicalFace.Name)) < 0) continue;
            // Referenced supertype metadata retains Kotlin arguments even though this pass authors CLR CIR.
            var physicalElement = PhysicalElement(args[0]);
            if (iteratorElement != null && SupertypeGraph.TypeKey(physicalElement) == SupertypeGraph.TypeKey(iteratorElement))
                return physicalElement;
            first ??= physicalElement;
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

    static TypeNode Tv0 => new TypeNode.Tv("type", 0);
    const string NonGenericCurrentName = "dotkt$NonGenericCurrent";
}
