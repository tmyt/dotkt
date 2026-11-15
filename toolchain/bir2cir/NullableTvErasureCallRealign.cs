using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// NULLABLE-Tv ERASURE call-site realignment (#4; the value-type-array-nullability / generic-boundary
// read family — #113/#117/#120/#142, READ side).
//
// A generic class `Box<T>` with a member typed `…Ref<T?>…` (a constructed generic whose arg is the
// nullable type-VARIABLE `T?`) has that `Nullable(Tv)` erased to `object` on the DECLARATION side by
// NullableGenericErasure.EraseNullableTv — `object` is the only uniform CLR storage that carries a
// real null for BOTH a reference and a value instantiation of the unconstrained T. So Box's emitted
// field/getter/`elem` all return `Ref<object>` (…[]). That erasure is correct (#142) and mandatory.
//
// But a CALL site is emitted by kotc with T ALREADY substituted to the concrete argument — e.g.
// `Box<Int>.get_a()` carries `Array<Ref<Nullable(kotlin.Int)>>`, NOT a bare `Nullable(Tv)`. The blanket
// EraseNullableTv sweep cannot see it (there is no `Tv` left), so it lowers to `Ref<Nullable<int32>>`,
// contradicting the member's ACTUAL erased return `Ref<object>`. `Ref<object>` and `Ref<Nullable<int32>>`
// are UNRELATED invariant reified generics (generic variance is interfaces/delegates only) — no castclass
// reconciles them (a castclass throws), so the read must be typed `Ref<object>` THROUGHOUT. Left alone this
// is an ilverify StackUnexpected (found `Ref`1<object>` expected `Ref`1<Nullable`1<int32>>`) at the element
// read / slot store.
//
// This pass re-derives each local generic call's return by SUBSTITUTING its class/method type-args into the
// EraseNullableTv-applied declaration — the callsite return then equals what the emitted method actually
// returns, BY CONSTRUCTION. This includes ownerless top-level generic functions such as #28's
// `fun <T> boxes(x: T): List<T?>`. A rewrite fires ONLY when the derived type is the object-ERASURE of the
// stamped type (IsObjectErasureOf) — i.e. it differs solely by `object` appearing where the callsite has a
// `Nullable(value)`/concrete arg. This is precisely the erasure boundary and nothing else: a directly-written
// `Ref<Int?>` (whose `Ref` declaration has NO `Nullable(Tv)`, so the derived type equals the stamped one)
// is untouched, and a genuine widen/narrow (not an object-erasure) never matches the gate. The corrected
// receiver type then flows through a per-method forward type-env so a chained `…[i].v` re-stamps `get_v`'s
// owner (`Ref<object>`) and return (`object`) too. A `var` whose declared type is the erasure counterpart of
// its init is retyped when the difference sits inside a constructed-generic arg (irreconcilable), or its init
// is wrapped in a `cast`->declared when the whole value erased to a TOP-LEVEL `object` (ilemit's unbox.any
// reconciles a boxed value / genuine null). Runs in BIR-space (kotlin.* names) right after the DEF-side
// EraseNullableTv, before BirTypeLowering. Body-only, so naturally inert in the ref build.
//
// SCOPE (#4/#28/#86): every USE position of an object-erased slot, read and write alike.
//   READ  — a local generic call into a `var`, a chained `…[i].v` receiver, collection member dispatch
//           (`List<T?>.size` / iterator), an array element read, a field read, a value-typed consumer
//           (`val x: Int? = …`).
//   WRITE — the positions where a value flows INTO a fixed slot, which is the other half of the same formula:
//           `setLocal`, `setField`, `arraySet`, `return`, an `if/else` value-join, and every call/ctor ARGUMENT
//           (whose target is the callee's `Subst(Erase(declared param))`, sig included). See the Writes half.
// REFERENCED declarations are read the same way (#86 D1): the producing assembly states each erased slot's pre-erasure
// Kotlin type on its `[KotlinNullableGeneric]` carrier, and its physical signature — which IS `Erase(declared)`, with
// generic parameters retained — states the rest. So a cross-module call's return, arguments and constructor slots are
// DERIVED from the real declaration exactly as a local one's are, rather than guessed from the call site. The lookup
// refuses a same-shape overload set outright (ReferenceMetadataIndex.TryNullableGenericSlot): name, static-ness, arity
// and generic arity are all a call gives us, and picking one sibling of an overload set would manufacture the very
// mismatch this pass removes.
static partial class NullableTvErasureCallRealign
{
    // The pre-erasure declaration of one member: its return AND its parameter vector. A use is typed
    // `Subst(Erase(<the matching component>), typeArgs)` — never `Erase(Subst(...))`.
    public sealed class DeclSig
    {
        public TypeNode Ret;
        public TypeNode[] Params;
        // True only when UnsafeAccessor selected a declaration from this compilation after declaration-side
        // nullable erasure. Its physical object slot is then not evidence of THIS contract unless the selected
        // declaration's nullable carrier says so; it may belong to another ABI such as unchecked-cast deferral.
        public bool NullableErasureOwnershipKnown;
        // Parameter positions whose declaration the REFERENCED reader deliberately would not state (#86 D1). Null for
        // a same-compilation declaration, which has nothing to refuse. A refused position is NOT the same as an
        // absent one: an absent declaration falls back to the call's own descriptor, a refused one must not, because
        // the descriptor is that same erasure written in the call's substituted vocabulary.
        public bool[] ParamsRefused;
    }

    // Pass-local hand-off from UnsafeAccessor synthesis to the one late declaration-index refresh below. It is
    // consumed while indexing and must not survive into CIR or round-trip metadata.
    public const string NullableErasureOwnershipKnownKey = "nullableErasureOwnershipKnown";

    // Local owner/top-level declarations, captured across ALL roots BEFORE the per-file DEF-side EraseNullableTv
    // mutates declarations in place. ALL members are stored (not only erasure-affected ones): re-deriving `get_v`
    // on a rewritten `Ref<object>` receiver needs the plain `tv{type,0}` declaration too. Ordinary methods still
    // retain their frontend declaration identity when available. The name/arity index remains conservative for
    // generated or virtual calls without that identity. Property calls retain their exact source property identity
    // and frontend-resolved signature, so their overload candidates remain separate.
    public sealed class DeclIndex
    {
        readonly DeclIndex _canonical;
        readonly Dictionary<string, int> _admittedOwners;
        readonly int? _visibleThrough;
        int _admission;

        public DeclIndex()
        {
            _canonical = this;
            _admittedOwners = new(StringComparer.Ordinal);
            ByDeclarationOwner = new(StringComparer.Ordinal);
            ByOwner = new(StringComparer.Ordinal);
            PropertiesByOwner = new(StringComparer.Ordinal);
            TopLevel = new(StringComparer.Ordinal);
            TopLevelPropertiesByOwner = new(StringComparer.Ordinal);
            FileClasses = new(StringComparer.Ordinal);
            Ctors = new(StringComparer.Ordinal);
            Slots = new(StringComparer.Ordinal);
        }

        DeclIndex(DeclIndex index)
        {
            _canonical = index._canonical;
            _admittedOwners = _canonical._admittedOwners;
            _visibleThrough = index._visibleThrough ?? _canonical._admission;
            SourceApplicationOrder = index.SourceApplicationOrder?.CaptureView();
            ByDeclarationOwner = index.ByDeclarationOwner;
            ByOwner = index.ByOwner;
            PropertiesByOwner = index.PropertiesByOwner;
            TopLevel = index.TopLevel;
            TopLevelPropertiesByOwner = index.TopLevelPropertiesByOwner;
            FileClasses = index.FileClasses;
            Ctors = index.Ctors;
            Slots = index.Slots;
        }

        // A view owns no declaration table. It pins visibility of the same
        // captured facts while the canonical index admits later files' types.
        internal DeclIndex BindSourceVisibility() => new(this);
        internal bool IsVisibleOwner(string owner) => _visibleThrough is not { } through
            || owner == null || !_admittedOwners.TryGetValue(owner, out var admitted) || admitted <= through;
        internal void AdvanceSourceAdmission()
        {
            if (!ReferenceEquals(_canonical, this))
                throw new InvalidOperationException("A declaration visibility view cannot admit source facts");
            _admission++;
        }
        internal void RecordOwnerAdmission(string owner) => _canonical._admittedOwners.Add(owner, _canonical._admission);

        internal InnerApplicationOrder SourceApplicationOrder;
        public readonly Dictionary<string, Dictionary<string, DeclSig>> ByDeclarationOwner;
        public readonly Dictionary<string, Dictionary<string, DeclSig>> ByOwner;
        public readonly Dictionary<string, Dictionary<string, List<DeclSig>>> PropertiesByOwner;
        public readonly Dictionary<string, DeclSig> TopLevel;
        // file facade -> property identity/signature -> candidates. A top-level call already carries the exact
        // frontend-resolved calleeOwner; retaining that axis prevents equally-shaped properties in another source
        // file/package from poisoning or supplying this call's declaration.
        public readonly Dictionary<string, Dictionary<string, List<DeclSig>>> TopLevelPropertiesByOwner;
        // The file classes THIS compilation declares. A `callStatic` naming one of them is a same-module top-level
        // call — its declaration is in `TopLevel`, keyed by bare name+arity — and a `callStatic` naming any other
        // owner is a call into a referenced assembly. kotc names the owner in both cases, and MemberCallSubstitution
        // fills it in for the referenced calls that arrived without one, so the owner alone cannot tell them apart.
        public readonly HashSet<string> FileClasses;
        // owner -> ctor arity -> parameter vector. A `new`'s args/argTypes are typed against it, the same way a
        // call's are typed against a method's — a ctor param is a declaration slot like any other (`Cell<T>(x: T?)`).
        public readonly Dictionary<string, Dictionary<int, TypeNode[]>> Ctors;
        // owner -> field/property name -> declared type. The target of a `setField` and the result of a `field` read.
        public readonly Dictionary<string, Dictionary<string, TypeNode>> Slots;
    }

    // The declaration snapshot outlives source application order. Transition its constructed slots with
    // the live declarations, before physical-use flow substitutes owner variables into those same slots.
    public static void ProjectDeclarationApplications(DeclIndex index)
    {
        var order = index.SourceApplicationOrder
            ?? throw new InvalidOperationException("Declaration applications already use physical order");
        TypeNode Project(TypeNode type) => type switch {
            TypeNode.Fqn named => new TypeNode.Fqn(named.Name, order.DeclarationArguments(
                new TypeNode.Fqn(named.Name, named.Args?.Select(Project).ToArray()))),
            TypeNode.Nullable nullable => new TypeNode.Nullable(Project(nullable.Of)),
            TypeNode.Oblivious platform => new TypeNode.Oblivious(Project(platform.Of)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, Project(projection.Of)),
            TypeNode.Array array => new TypeNode.Array(Project(array.Elem), array.Rank, array.SzArray),
            TypeNode.ByRef reference => new TypeNode.ByRef(Project(reference.Of)),
            TypeNode.Fn function => new TypeNode.Fn(function.Suspend, Project(function.Ret),
                function.Params.Select(Project).ToArray(), function.Recv == null ? null : Project(function.Recv),
                function.Clr, function.Ctx?.Select(Project).ToArray()),
            _ => type,
        };
        var signatures = index.ByDeclarationOwner.Values.SelectMany(entries => entries.Values)
            .Concat(index.ByOwner.Values.SelectMany(entries => entries.Values))
            .Concat(index.TopLevel.Values)
            .Concat(index.PropertiesByOwner.Values.SelectMany(entries => entries.Values).SelectMany(entries => entries))
            .Concat(index.TopLevelPropertiesByOwner.Values.SelectMany(entries => entries.Values).SelectMany(entries => entries))
            .Where(signature => signature != null).Distinct();
        foreach (var signature in signatures)
        {
            signature.Ret = Project(signature.Ret);
            signature.Params = signature.Params.Select(Project).ToArray();
        }
        foreach (var slots in index.Slots.Values)
            foreach (var name in slots.Keys.ToArray())
                if (slots[name] != null) slots[name] = Project(slots[name]);
        foreach (var constructors in index.Ctors.Values)
            foreach (var arity in constructors.Keys.ToArray())
                if (constructors[arity] != null) constructors[arity] = constructors[arity].Select(Project).ToArray();
        index.SourceApplicationOrder = null;
    }


    // Capture storage can change after the original declaration snapshot. Update the exact already-owned
    // field and constructor slot together; otherwise later use alignment would restore a stale closed T cast.
    internal static void RebindGeneratedCapture(DeclIndex index, string owner, string field, int position,
        int arity, TypeNode storage)
    {
        if (!index.Slots.TryGetValue(owner, out var fields) || !fields.ContainsKey(field)
            || !index.Ctors.TryGetValue(owner, out var constructors) || !constructors.TryGetValue(arity, out var parameters)
            || position < 0 || position >= parameters.Length)
            throw new InvalidOperationException($"Generated capture '{owner}.{field}' has no indexed storage slot");
        fields[field] = storage;
        parameters[position] = storage;
    }


    internal sealed record MethodSnapshot(string Name, string Identity, string PropertyName, string AccessorKind,
        int Arity, bool Static, DeclSig Signature);


    static void AddMethodSnapshot(DeclIndex index, string owner, MethodSnapshot method,
        Dictionary<string, DeclSig> signatures, Dictionary<string, List<DeclSig>> properties, bool topLevel = false)
    {
        var signature = method.Signature;
        AddUnambiguous(signatures, method.Name + "|" + signature.Params.Length, signature);
        AddDeclarationIdentity(index, owner, method.Identity, signature);
        if (properties != null && method.PropertyName != null)
            AddCandidate(properties, PropertyKey(method.PropertyName, method.AccessorKind, signature.Params.Length,
                method.Arity, topLevel || method.Static), signature);
    }

    static void AddConstructorSnapshot(Dictionary<int, TypeNode[]> constructors, TypeNode[] parameters)
    {
        if (constructors.TryGetValue(parameters.Length, out var prior))
        {
            if (prior != null && !SameVector(prior, parameters)) constructors[parameters.Length] = null;
        }
        else constructors[parameters.Length] = parameters;
    }


    static void AddSlotSnapshot(Dictionary<string, TypeNode> slots, string name, TypeNode type)
    {
        if (slots.TryGetValue(name, out var prior))
        {
            if (prior != null && !prior.Equals(type)) slots[name] = null;
        }
        else slots[name] = type;
    }


    static DeclSig ReadSignature(TypeNode physicalReturn, Func<TypeNode> nullableCarrier,
        Func<TypeNode> sourceReturn, bool ownershipKnown, Func<bool, TypeNode[]> parameters)
    {
        // An explicit non-nullable scalar source carrier owns an independent
        // object transport seam, not nullable erasure. Keep concrete use hints.
        ownershipKnown |= physicalReturn is TypeNode.Fqn { Name: "object" or "System.Object" }
            && sourceReturn() is TypeNode.Tv;
        if (ReadDeclaredSlot(physicalReturn, nullableCarrier, ownershipKnown) is not TypeNode rt) return null;
        return parameters(ownershipKnown) is TypeNode[] ps
            ? new DeclSig { Ret = rt, Params = ps, NullableErasureOwnershipKnown = ownershipKnown }
            : null;
    }


    static TypeNode ReadDeclaredSlot(TypeNode physical, Func<TypeNode> carrier, bool preferCarrier)
    {
        if (preferCarrier && carrier() is TypeNode.Nullable { Of: TypeNode.Tv } scalarNullable)
            return scalarNullable;
        return physical;
    }


    static TypeNode[] ReadParameterVector(int count, Func<int, TypeNode> parameter)
    {
        var result = new TypeNode[count];
        for (var index = 0; index < count; index++)
            if (parameter(index) is TypeNode type) result[index] = type;
            else return null;
        return result;
    }

    // Two same-key members whose declarations DISAGREE poison the whole entry. For a call without exact identity,
    // retaining one same-name/same-arity entry would GUESS which overload was meant — and deriving the wrong
    // member's types manufactures exactly the mismatch this pass exists to remove. A poisoned key falls back to the
    // call's own descriptor, which is at least what the member will be resolved by. (Keeping the components apart —
    // a conflicting return poisoning only the return — was tried and is unsound for the same reason: the surviving
    // component still names one arbitrary overload of the set.)
    static void AddUnambiguous(Dictionary<string, DeclSig> entries, string key, DeclSig sig)
    {
        if (!entries.TryGetValue(key, out var prior)) { entries[key] = sig; return; }
        if (prior == null) return;                                          // already fully poisoned
        if (!prior.Ret.Equals(sig.Ret) || !SameVector(prior.Params, sig.Params)) entries[key] = null;
    }

    static void AddCandidate(Dictionary<string, List<DeclSig>> entries, string key, DeclSig sig)
    {
        if (!entries.TryGetValue(key, out var candidates))
            entries[key] = candidates = new List<DeclSig>();
        candidates.Add(sig);
    }

    static void AddDeclarationIdentity(DeclIndex index, string owner, string identity, DeclSig signature)
    {
        if (owner == null || identity == null) return;
        if (!index.ByDeclarationOwner.TryGetValue(owner, out var declarations))
            index.ByDeclarationOwner[owner] = declarations = new(StringComparer.Ordinal);
        AddUnambiguous(declarations, identity, signature);
    }

    // The frontend has already selected this overload. Keep its source declaration slot,
    // rather than dropping it when another overload shares the name and argument count.
    // The owner axis prevents applying a base declaration's generic frame as a derived frame.
    static DeclSig LookupDeclarationIdentity(string identity, TypeNode.Fqn owner, DeclIndex index)
    {
        if (owner == null || identity == null || !index.IsVisibleOwner(owner.Name)
            || !index.ByDeclarationOwner.TryGetValue(owner.Name, out var declarations)) return null;
        return declarations.GetValueOrDefault(identity);
    }

    static bool SameVector(TypeNode[] a, TypeNode[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    static string PropertyKey(string propertyName, string accessorKind, int argCount, int methodArity,
        bool isStatic) =>
        propertyName + "|" + accessorKind + "|" + argCount + "|" + methodArity + "|" + isStatic;

    // The struct-ness oracle, needed by the WRITE axis to tell an `object` seam that genuinely needs a conversion
    // (a value / type-variable slot) from one that is plain reference assignment.
    static ValueTypeOracle _isValue = _ => false;
    // The REFERENCED declarations (#86 D1). A slot whose declaration is not in this compilation is read off the
    // producing assembly instead — its `[KotlinNullableGeneric]` carrier where the erasure recorded one, its physical
    // signature otherwise — and typed by the identical formula. Null when the build has no references.
    static ReferenceMetadataIndex _refs;
    static InnerApplicationOrder _sourceApplicationOrder;
    static TypeNode[] OwnerArguments(TypeNode.Fqn owner) =>
        _sourceApplicationOrder?.DeclarationArguments(owner) ?? owner?.Args;


    // The per-method type environment: local/param slot types, plus the method's own (already-erased) return type,
    // which is the target of every `return` in the body.
    sealed class Ctx
    {
        public readonly Dictionary<string, TypeNode> Env = new(StringComparer.Ordinal);
        public DeclIndex Idx;
        public TypeNode Ret;
        public NullableRepresentationFrame OwnerNullableFrame;
        public NullableRepresentationFrame MethodNullableFrame;

        public bool IsNullableCompanion(TypeNode type)
        {
            if (type is not TypeNode.Tv variable) return false;
            var frame = variable.Scope == "type" ? OwnerNullableFrame : MethodNullableFrame;
            return frame != null && variable.I >= 0 && variable.I < frame.PhysicalArity
                && frame.PhysicalSlot(variable.I).Representation is
                    NullableRepresentationFrame.Role.Nullable or NullableRepresentationFrame.Role.NullableStorage;
        }
    }


    static bool CanNarrowReferenceArrayProjection(TypeNode physical, TypeNode semantic)
        => CanNarrowReferenceArrayProjection(physical, semantic, _isValue);

    static bool CanNarrowReferenceArrayProjection(TypeNode physical, TypeNode semantic, ValueTypeOracle isValue)
        => ReferenceArrayShape(physical) is TypeNode.Array { Elem: TypeNode.Fqn { Name: "object", Args: null } } pa
           && ReferenceArrayShape(semantic) is TypeNode.Array sa
           && pa.Rank == sa.Rank && pa.SzArray == sa.SzArray
           && !IsSemanticObjectElement(sa.Elem)
           && !NeedsObjectSeam(sa.Elem, isValue);

    // Nullable/oblivious wrappers on a reference array are NRT annotations, not CLR containers. Preserve the full
    // authored type on the cast node (BirTypeLowering records its nullable flags), but compare the physical array
    // shape beneath those wrappers. This is intentionally array-only: a structural Nullable<V> never contains an
    // array, so no value-type nullability can be erased by this projection test.
    static TypeNode ReferenceArrayShape(TypeNode type) => type switch
    {
        TypeNode.Array => type,
        TypeNode.Nullable n => ReferenceArrayShape(n.Of),
        TypeNode.Oblivious o => ReferenceArrayShape(o.Of),
        _ => null,
    };

    static bool IsSemanticObjectElement(TypeNode type) => type switch
    {
        TypeNode.Nullable n => IsSemanticObjectElement(n.Of),
        TypeNode.Oblivious o => IsSemanticObjectElement(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "System.Object" or "kotlin.Any" } => true,
        _ => false,
    };


    // The declaration of a member, keyed by EXACT name+arity (DefaultArgSplice has already run, so an app-build call
    // carries its real arity). A LOCAL owner's declaration is the pre-erasure one this compilation collected; either
    // component may be a poisoned `null` — an ambiguous same-name/same-arity overload set (AddUnambiguous) — and each
    // caller skips only the component it cannot trust.
    //
    // A REFERENCED owner's declaration comes from the producing assembly (#86 D1): its `[KotlinNullableGeneric]`
    // carrier at each slot the erasure rewrote, its physical signature (which IS `Erase(declared)`) elsewhere. That is
    // the real declaration, so the general `Subst(Erase(decl))` rule covers a referenced generic member — including
    // `Iterable<E>.iterator()`, `Iterator<E>.next()` and `List<E>.get(i)` on a receiver corrected to its erased
    // instantiation, which a hardcoded member table used to approximate.
    static DeclSig LookupDecl(TypeNode.Fqn owner, string method, int argCount, int methodArity, bool isStatic,
        DeclIndex idx, UseSelection selection)
    {
        if (idx.IsVisibleOwner(owner.Name) && idx.ByOwner.TryGetValue(owner.Name, out var sigs))
        {
            // An inherited call can still carry the derived owner before owner binding runs. A missing exact
            // identity there must not select a derived sibling; the later flow pass uses the bound base owner.
            if (selection.Identity != null) return null;
            return sigs.TryGetValue(method + "|" + argCount, out var local) ? local : null;
        }
        return LookupReferencedDecl(owner, method, argCount, methodArity, isStatic, selection);
    }

    static DeclSig LookupReferencedDecl(TypeNode.Fqn owner, string method, int argCount, int methodArity,
        bool isStatic, UseSelection selection)
    {
        var signature = selection.Signature;
        if (signature == null || signature.Length != argCount || signature.Any(type => type == null)) signature = null;
        var identity = selection.Identity;
        // Exact closed parameter slots can consume an object-erased argument. This does not grant ownership of
        // unchanged returns: runtime helpers may already carry another lowering's semantic result projection.
        // Keep return facts limited to nullable-erasure carriers or open physical declarations.
        return _refs != null
            && _refs.TryNullableGenericSlot(owner.Name, method, isStatic, argCount, methodArity,
               out var ret, out var ps, out var refused, ownerTypeArguments: OwnerArguments(owner) ?? Array.Empty<TypeNode>(),
               resolvedSignature: signature, includeUnchanged: signature != null || identity != null,
               selectedDeclarationId: identity, includeUnchangedReturn: false)
            ? new DeclSig { Ret = ret, Params = ps, ParamsRefused = refused }
            : null;
    }

    // Property calls retain their frontend-resolved source property and get/set role until BirTypeLowering strips BIR
    // semantics. Use that identity while it exists. Looking the declaration up again by its newly allocated CLR method
    // name would discard the exact Property/MethodSemantics association and collapse same-name accessor overloads back
    // to name+arity — precisely the reverse inference #397 removes. `shapeTypes` is the generic declaration vector
    // before MemberCallSubstitution; `sig` is the same exact vector after binding.
    static DeclSig LookupPropertyDecl(UseSelection selection, TypeNode.Fqn owner, int argCount, int methodArity,
        bool isStatic, DeclIndex idx)
    {
        if (!selection.Property) return null;
        var propertyName = selection.PropertyName;
        var accessorKind = selection.AccessorKind;
        var local = LookupLocalPropertyDecl(selection, owner, argCount, methodArity, isStatic, idx);
        if (local != null) return local;
        if (_refs == null || owner == null) return null;
        var signature = selection.Signature;
        if (signature == null || signature.Length != argCount || signature.Any(type => type == null))
            signature = null;
        return _refs.TryNullableGenericPropertySlot(owner.Name, propertyName, accessorKind, isStatic,
            argCount, methodArity, signature, OwnerArguments(owner) ?? Array.Empty<TypeNode>(),
            out var ret, out var parameters, out var refused)
            ? new DeclSig { Ret = ret, Params = parameters, ParamsRefused = refused }
            : null;
    }

    static DeclSig LookupLocalPropertyDecl(UseSelection selection, TypeNode.Fqn owner, int argCount, int methodArity,
        bool isStatic, DeclIndex idx)
    {
        if (!selection.Property) return null;
        var key = PropertyKey(selection.PropertyName, selection.AccessorKind, argCount, methodArity, isStatic);
        IReadOnlyList<DeclSig> candidates = null;
        if (owner != null && idx.IsVisibleOwner(owner.Name)
            && idx.PropertiesByOwner.TryGetValue(owner.Name, out var ownedProperties)
            && ownedProperties.TryGetValue(key, out var owned))
            candidates = owned;
        else if (isStatic)
        {
            var topLevelOwner = owner?.Name ?? selection.CalleeOwner;
            if (topLevelOwner != null
                && idx.TopLevelPropertiesByOwner.TryGetValue(topLevelOwner, out var properties)
                && properties.TryGetValue(key, out var topLevel))
                candidates = topLevel;
        }
        else if (owner != null && idx.IsVisibleOwner(owner.Name)
            && idx.PropertiesByOwner.TryGetValue(owner.Name, out var properties)
            && properties.TryGetValue(key, out var members))
            candidates = members;
        if (candidates == null || candidates.Count == 0) return null;

        var signature = selection.Signature;
        if (signature == null || signature.Length != argCount || signature.Any(type => type == null))
            signature = null;
        var matches = candidates.Where(candidate => signature == null
                || candidate.Params.Select(parameter => owner?.Args == null
                        ? parameter
                        : SupertypeGraph.SubstOwnerTvs(parameter, OwnerArguments(owner)))
                    .Select((parameter, index) =>
                        ReferenceMetadataIndex.AccessorDeclarationDescribesCall(parameter, signature[index]))
                    .All(match => match))
            .ToList();
        if (matches.Count == 1) return matches[0];
        // Identical declarations can be encountered through more than one input root. They describe the same slot;
        // unlike disagreeing overloads, retaining one does not choose semantics arbitrarily.
        return matches.Count > 1 && matches.Skip(1)
            .All(candidate => candidate.Ret.Equals(matches[0].Ret)
                && SameVector(candidate.Params, matches[0].Params))
            ? matches[0]
            : null;
    }

    // Substitute class-scope `tv{type,i}` with `typeArgs[i]` and method-scope `tv{method,i}` with `methodArgs[i]`,
    // recursively. Returns null when a needed binding is unavailable (caller skips the rewrite).
    static TypeNode Subst(TypeNode t, TypeNode[] typeArgs, TypeNode[] methodArgs)
    {
        switch (t)
        {
            case TypeNode.Tv { Scope: "type" } tv:
                return typeArgs != null && tv.I >= 0 && tv.I < typeArgs.Length ? typeArgs[tv.I] : null;
            case TypeNode.Tv { Scope: "method" } tv:
                return methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length ? methodArgs[tv.I] : null;
            case TypeNode.Fqn { Args: { } a } f:
            {
                var na = new TypeNode[a.Length];
                for (var i = 0; i < a.Length; i++)
                    if (Subst(a[i], typeArgs, methodArgs) is TypeNode s) na[i] = s; else return null;
                return new TypeNode.Fqn(f.Name, na);
            }
            case TypeNode.Fqn f:
                return f;
            case TypeNode.Nullable n:
                return Subst(n.Of, typeArgs, methodArgs) is TypeNode i0 ? new TypeNode.Nullable(i0) : null;
            case TypeNode.Oblivious o:
                return Subst(o.Of, typeArgs, methodArgs) is TypeNode i1 ? new TypeNode.Oblivious(i1) : null;
            case TypeNode.Projection p:
                // This helper authors concrete use-site CIR storage/casts. Projection controls assignability at the
                // Kotlin boundary but is not itself a reifiable CLR type, so substitute through the wrapper.
                return Subst(p.Of, typeArgs, methodArgs);
            case TypeNode.Array ar:
                return Subst(ar.Elem, typeArgs, methodArgs) is TypeNode i2
                    ? new TypeNode.Array(i2, ar.Rank, ar.SzArray)
                    : null;
            case TypeNode.ByRef br:
                return Subst(br.Of, typeArgs, methodArgs) is TypeNode i3 ? new TypeNode.ByRef(i3) : null;
            case TypeNode.Fn fn:
            {
                if (Subst(fn.Ret, typeArgs, methodArgs) is not TypeNode ret) return null;
                var ps = new TypeNode[fn.Params.Length];
                for (var i = 0; i < ps.Length; i++)
                    if (Subst(fn.Params[i], typeArgs, methodArgs) is TypeNode s) ps[i] = s; else return null;
                TypeNode recv = null;
                if (fn.Recv != null)
                {
                    if (Subst(fn.Recv, typeArgs, methodArgs) is not TypeNode r) return null;
                    recv = r;
                }
                TypeNode[] ctx = null;
                if (fn.Ctx != null)
                {
                    ctx = new TypeNode[fn.Ctx.Length];
                    for (var i = 0; i < ctx.Length; i++)
                        if (Subst(fn.Ctx[i], typeArgs, methodArgs) is TypeNode s) ctx[i] = s; else return null;
                }
                return new TypeNode.Fn(fn.Suspend, ret, ps, recv, fn.Clr, ctx);
            }
            default:
                return t;
        }
    }

    internal static void SelfTest()
    {
        SourceSnapshotSelfTest();
        UnsafeSourceSelfTest();
        var roleFrame = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 2, 0, 3, 1 },
            storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 0 });
        var methodRoleFrame = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 1, 3, 0, 2 },
            storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 0 });
        var roleContext = new Ctx { OwnerNullableFrame = roleFrame, MethodNullableFrame = methodRoleFrame };
        foreach (var scope in new[] { "type", "method" })
            foreach (var role in Enum.GetValues<NullableRepresentationFrame.Role>())
                if (roleContext.IsNullableCompanion((scope == "type" ? roleFrame : methodRoleFrame)
                        .Variable(new TypeNode.Tv(scope, 0), role))
                    != (role is NullableRepresentationFrame.Role.Nullable or NullableRepresentationFrame.Role.NullableStorage))
                    throw new InvalidOperationException("Nullable result realignment confused a storage companion with a nullable slot");

        var innerDeclaration = new BirClassDefinition {
            Name = "Outer.Middle", Kind = "class", SemanticOwner = "Outer",
            Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 2,
        };
        var declarationIndex = CollectDeclaredMemberRets(new[] { new BirFileDeclaration {
            Types = new IBirTypeDeclaration[] { innerDeclaration },
        } });
        var sourceApplication = new TypeNode.Fqn("Outer.Middle", new TypeNode[] {
            new TypeNode.Tv("type", 2), new TypeNode.Tv("type", 3),
            new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1),
        });
        var physicalApplication = new TypeNode.Fqn("Outer.Middle", new TypeNode[] {
            new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1),
            new TypeNode.Tv("type", 2), new TypeNode.Tv("type", 3),
        });
        var signature = new DeclSig {
            Ret = sourceApplication,
            Params = new TypeNode[] { new TypeNode.ByRef(TypeNode.Array.General(sourceApplication, 2)) },
        };
        declarationIndex.ByOwner["Leaf"] = new() { ["member"] = signature };
        declarationIndex.ByDeclarationOwner["Leaf"] = new() { ["identity"] = signature };
        declarationIndex.PropertiesByOwner["Leaf"] = new() { ["property"] = new() { signature } };
        declarationIndex.TopLevel["member"] = signature;
        declarationIndex.TopLevelPropertiesByOwner["Facade"] = new() { ["property"] = new() { signature } };
        declarationIndex.Slots["Leaf"] = new() { ["__outer"] = sourceApplication,
            ["value"] = new TypeNode.Tv("type", 3) };
        declarationIndex.Ctors["Leaf"] = new() { [1] = new TypeNode[] { sourceApplication } };
        ProjectDeclarationApplications(declarationIndex);
        if (signature.Ret != physicalApplication
            || signature.Params[0] != new TypeNode.ByRef(TypeNode.Array.General(physicalApplication, 2))
            || declarationIndex.Slots["Leaf"]["__outer"] != physicalApplication
            || declarationIndex.Slots["Leaf"]["value"] != new TypeNode.Tv("type", 3)
            || declarationIndex.Ctors["Leaf"][1][0] != physicalApplication
            || declarationIndex.SourceApplicationOrder != null)
            throw new InvalidOperationException("Declaration snapshot lost physical inner order or projected a shared signature twice");

        var sourceFunction = new TypeNode.Fn(false, new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Int")),
            new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Int")) });
        var physicalCallbackResult = new TypeNode.Fn(false, new TypeNode.Fqn("object"),
            new TypeNode[] { new TypeNode.Fqn("object") }, null, "System.Func");
        var stampedCall = new BirCallStatic { Sty = sourceFunction };
        if (!ReferenceEquals(SourceDerivedCall(stampedCall, physicalCallbackResult, stampedCall.ResultTypeStamp,
                false, false, false, out var flowedResult), stampedCall) || flowedResult != physicalCallbackResult
            || stampedCall.Ret.Value != physicalCallbackResult || stampedCall.Sty.Value != physicalCallbackResult)
            throw new InvalidOperationException("A sty-only function call lost its materialized physical result");
        var callerResult = new TypeNode.Fqn("Result", new TypeNode[] { new TypeNode.Tv("type", 6) });
        var declarationResult = new TypeNode.Fqn("Result", new TypeNode[] { new TypeNode.Tv("type", 2) });
        var resultStamps = new BirCallInstance { Sty = callerResult, Ret = declarationResult, DynRet = physicalCallbackResult };
        if (resultStamps.ResultTypeStamp != callerResult)
            throw new InvalidOperationException("Nullable result flow ignored the instantiated caller-frame stamp");
        resultStamps.Sty = default;
        if (resultStamps.ResultTypeStamp != declarationResult)
            throw new InvalidOperationException("Nullable result flow ignored its declared-result fallback");
        resultStamps.Ret = default;
        if (resultStamps.ResultTypeStamp != physicalCallbackResult || new BirCallInstance().ResultTypeStamp != null)
            throw new InvalidOperationException("Nullable result flow lost its dynamic or unstamped fallback");
        var companionReturn = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var sourceReturn = new TypeNode.Fqn("Box", new TypeNode[] {
            new TypeNode.Nullable(new TypeNode.Tv("type", 0)) });
        if (ReadDeclaredSlot(companionReturn, () => sourceReturn, true) != companionReturn)
            throw new InvalidOperationException("Late accessor re-erased its materialized nullable companion");
        var scalarReturn = new TypeNode.Nullable(new TypeNode.Tv("type", 0));
        if (ReadDeclaredSlot(new TypeNode.Fqn("object"), () => scalarReturn, true) != scalarReturn)
            throw new InvalidOperationException("Late accessor lost scalar nullable-erasure ownership");
        var scalarCall = new BirCallStatic { Ret = new TypeNode.Fqn("object"), Sty = new TypeNode.Fqn("kotlin.String") };
        SourceDerivedCall(scalarCall, new TypeNode.Fqn("object"), new TypeNode.Fqn("object"),
            true, false, false, out _);
        if (scalarCall.Sty.Value != scalarCall.Ret.Value)
            throw new InvalidOperationException("An already-erased scalar descriptor kept its semantic result stamp");
        var typeArgs = new TypeNode[] { new TypeNode.Fqn("System.Int32") };
        var methodArgs = new TypeNode[] { new TypeNode.Fqn("System.String") };

        var arrays = new TypeNode[]
        {
            TypeNode.Array.General(new TypeNode.Tv("type", 0), 2),
            TypeNode.Array.General(new TypeNode.Tv("method", 0), 1),
        };
        var expectedArrays = new TypeNode[]
        {
            TypeNode.Array.General(typeArgs[0], 2),
            TypeNode.Array.General(methodArgs[0], 1),
        };
        for (var i = 0; i < arrays.Length; i++)
            if (Subst(arrays[i], typeArgs, methodArgs) != expectedArrays[i])
                throw new InvalidOperationException(
                    "NullableTvErasureCallRealign self-test dropped a general-array rank/vector facet");

        var function = new TypeNode.Fn(
            Suspend: true,
            Ret: new TypeNode.Tv("type", 0),
            Params: new TypeNode[] { new TypeNode.Tv("method", 0) },
            Recv: new TypeNode.Tv("type", 0),
            Clr: "System.Func",
            Ctx: new TypeNode[]
            {
                new TypeNode.Tv("method", 0),
                TypeNode.Array.General(new TypeNode.Tv("type", 0), 2),
            });
        var expectedFunction = new TypeNode.Fn(
            Suspend: true,
            Ret: typeArgs[0],
            Params: new[] { methodArgs[0] },
            Recv: typeArgs[0],
            Clr: "System.Func",
            Ctx: new TypeNode[]
            {
                methodArgs[0],
                TypeNode.Array.General(typeArgs[0], 2),
            });
        if (Subst(function, typeArgs, methodArgs) != expectedFunction)
            throw new InvalidOperationException(
                "NullableTvErasureCallRealign self-test dropped a function CLR family or context frame");

        var physicalFunction = new TypeNode.Fn(
            false, new TypeNode.Fqn("object"), Array.Empty<TypeNode>(), Clr: "System.Func");
        if (IsObjectErasureOf(
                TypeNode.Array.General(new TypeNode.Fqn("object"), 2),
                new TypeNode.Array(new TypeNode.Fqn("System.String")))
            || IsObjectErasureOf(
                physicalFunction,
                new TypeNode.Fn(false, new TypeNode.Fqn("System.String"), Array.Empty<TypeNode>(),
                    Clr: "kotlin.jvm.functions.Function0"))
            || !IsObjectErasureOf(
                physicalFunction,
                new TypeNode.Fn(false, new TypeNode.Fqn("System.String"), Array.Empty<TypeNode>())))
            throw new InvalidOperationException(
                "NullableTvErasureCallRealign self-test misclassified physical facets during object erasure");

        var receiverFunction = new TypeNode.Fn(false, new TypeNode.Fqn("object"),
            Array.Empty<TypeNode>(), Recv: new TypeNode.Fqn("object"));
        var flattenedFunction = new TypeNode.Fn(false, new TypeNode.Fqn("System.String"),
            new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("System.Int32")) });
        if (!IsObjectErasureOf(receiverFunction, flattenedFunction)
            || IsObjectErasureOf(receiverFunction, flattenedFunction with { Params = Array.Empty<TypeNode>() }))
            throw new InvalidOperationException(
                "NullableTvErasureCallRealign self-test misaligned an extension receiver's physical slot");

        var unitFunction = flattenedFunction with { Ret = new TypeNode.Fqn("kotlin.Unit") };
        var actionFunction = receiverFunction with { Ret = new TypeNode.Fqn("System.Void"), Clr = "System.Action" };
        if (!IsObjectErasureOf(actionFunction, unitFunction)
            || IsObjectErasureOf(actionFunction, unitFunction with { Clr = "System.Func" })
            || IsObjectErasureOf(actionFunction, unitFunction with { Ret = new TypeNode.Nullable(unitFunction.Ret) }))
            throw new InvalidOperationException(
                "NullableTvErasureCallRealign self-test confused void and value-bearing Unit delegate returns");

        Console.WriteLine("[nullable-generic substitution] self-test OK (general arrays + function facets)");
    }

    // Late call-shape consumers use the same declaration-to-use formula as this pass without duplicating its
    // substitution grammar. In particular, constrained dispatch learns its constructed owner only after Apply.
    internal static TypeNode EraseAndSubstituteOwnerSlot(
        TypeNode declared, TypeNode[] ownerArgs, ValueTypeOracle isValue)
        => Subst(NullableGenericErasure.EraseNullableTv(declared, isValue), ownerArgs, null);

    // Whether `candidate` is `expected` with one or more sub-positions collapsed to the erased `object` — i.e.
    // `candidate` == `expected` except that where `expected` has a non-`object` type, `candidate` may have
    // `object`. True for `object` vs anything (a leaf erasure), and structurally through Fqn args / array elem /
    // nullable / byref / fn. This is the exact "object-erasure of" relation that gates every rewrite here.
    static bool IsObjectErasureOf(TypeNode candidate, TypeNode expected)
    {
        if (candidate.Equals(expected)) return true;
        if (candidate is TypeNode.Fqn { Name: "object", Args: null }) return true;
        return (candidate, expected) switch
        {
            (TypeNode.Fqn { Args: { } ca } cf, TypeNode.Fqn { Args: { } ea } ef)
                when SameClassifier(cf, ef) && ca.Length == ea.Length
                => ca.Zip(ea, IsObjectErasureOf).All(x => x),
            (TypeNode.Array c, TypeNode.Array e) when c.Rank == e.Rank && c.SzArray == e.SzArray
                => IsObjectErasureOf(c.Elem, e.Elem),
            (TypeNode.Nullable c, TypeNode.Nullable e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.Oblivious c, TypeNode.Oblivious e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.Projection c, TypeNode.Projection e) when c.Variance == e.Variance
                => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.ByRef c, TypeNode.ByRef e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.Fn c, TypeNode.Fn e)
                // A function value's physical first parameter may be spelled as Recv in its declaration
                // and as Params[0] at the invocation. Compare physical slots, retaining context and ABI checks.
                when c.DelegateParams.Length == e.DelegateParams.Length && c.Suspend == e.Suspend
                    && (c.Clr == null || e.Clr == null || c.Clr == e.Clr)
                    && ContextIsObjectErasureOf(c.Ctx, e.Ctx)
                => IsObjectErasureOf(PhysicalDelegateReturn(c), PhysicalDelegateReturn(e))
                   && c.DelegateParams.Zip(e.DelegateParams, IsObjectErasureOf).All(x => x),
            _ => false,
        };
    }

    // Reflection names System.Void; Kotlin function-return lowering uses the equivalent void token.
    static TypeNode PhysicalDelegateReturn(TypeNode.Fn fn)
        => BirTypeLowering.DelegateReturnSlot(fn) is TypeNode.Fqn { Args: null, Name: "System.Void" }
            ? new TypeNode.Fqn("void") : BirTypeLowering.DelegateReturnSlot(fn);

    static bool ContextIsObjectErasureOf(TypeNode[] candidate, TypeNode[] expected)
    {
        candidate ??= Array.Empty<TypeNode>();
        expected ??= Array.Empty<TypeNode>();
        return candidate.Length == expected.Length && candidate.Zip(expected, IsObjectErasureOf).All(x => x);
    }

    // Current-format ClrExternal classifiers carry their exact CLR TypeDef identity, while nullable-generic
    // declaration carriers retain the Kotlin classifier spelling they describe. They name the same generic head only
    // when the reference index can project that semantic spelling to one unique exact TypeDef. An ambiguous flattened
    // nested identity deliberately has no answer here and therefore never participates in an erasure rewrite.
    static bool SameClassifier(TypeNode.Fqn left, TypeNode.Fqn right)
        => SameClassifier(left, right, _refs);

    static bool SameClassifier(TypeNode.Fqn left, TypeNode.Fqn right, ReferenceMetadataIndex refs)
    {
        if (left.Name == right.Name) return true;
        if (refs == null) return false;
        if (refs.TryExactPhysicalTypeName(left.Name, left.Args?.Length ?? 0, out var leftExact)
            && leftExact != null && leftExact == right.Name)
            return true;
        return refs.TryExactPhysicalTypeName(right.Name, right.Args?.Length ?? 0, out var rightExact)
            && rightExact != null && rightExact == left.Name;
    }


}
