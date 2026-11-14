using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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

    // Admit TYPE declarations materialized after the module-wide snapshot. Shared ref cells call this before
    // NullableGenericErasure; UnsafeAccessor holders call it afterwards. The latter's callable entry carries the
    // one-shot ownership key only when synthesis selected a local declaration, so ReadSig may consult its propagated
    // pre-erasure return carrier without mistaking every other late synthetic or external accessor for that case.
    // Only the recursive `types` walk admits new owners; facade members are already indexed. Existing type names
    // are ignored, making each call an additive transition rather than an ambiguity-producing rescan.
    public static void CollectNewSyntheticTypes(JsonNode root, DeclIndex idx)
    {
        idx.SourceApplicationOrder?.Add(root);
        CollectNewTypeContainers(root, idx);
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

    // The same generated holder method can be visited once through the new-type walk and once through the
    // generated-member walk. Keep its one-shot ownership fact stable across both index additions, then consume it
    // only after both views have been collected so they cannot disagree and poison the declaration key.
    public static void ConsumeNullableErasureOwnership(JsonNode root)
    {
        if (root is JsonObject obj)
        {
            obj.Remove(NullableErasureOwnershipKnownKey);
            foreach (var child in obj.Select(pair => pair.Value).ToArray())
                if (child != null) ConsumeNullableErasureOwnership(child);
        }
        else if (root is JsonArray array)
            foreach (var child in array.ToArray())
                if (child != null) ConsumeNullableErasureOwnership(child);
    }

    // UnsafeAccessor has a second materialization shape: for a non-generic target owner it appends a generated extern
    // method to the EXISTING caller type (or file facade) instead of creating a holder type. Type admission deliberately
    // ignores an owner already in ByOwner, so admit only those generated methods here. Their allocated names are the
    // exact identity used by the replacement call, and their post-erasure signature is the authoritative physical
    // declaration. Ordinary existing declarations and property candidate sets remain untouched.
    public static void CollectNewSyntheticMembers(JsonNode root, DeclIndex idx)
    {
        if (root is not JsonObject file) return;
        if (file["methods"] is JsonArray topMethods)
            CollectGeneratedMethods(topMethods, idx.TopLevel);
        CollectTypeMethods(file["types"]);

        void CollectTypeMethods(JsonNode node)
        {
            if (node is not JsonArray types) return;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Str(type["name"]) is string name && type["methods"] is JsonArray methods)
                {
                    if (!idx.ByOwner.TryGetValue(name, out var declarations))
                        idx.ByOwner[name] = declarations = new Dictionary<string, DeclSig>(StringComparer.Ordinal);
                    CollectGeneratedMethods(methods, declarations);
                }
                CollectTypeMethods(type["types"]);
            }
        }
    }

    static void CollectGeneratedMethods(JsonArray methods, Dictionary<string, DeclSig> declarations)
    {
        foreach (var method in methods.OfType<JsonObject>())
            if (Bool(method["generated"]) && Str(method["name"]) is string name
                && ReadSig(method) is DeclSig sig)
                AddUnambiguous(declarations, name + "|" + sig.Params.Length, sig);
    }

    internal sealed record MethodSnapshot(string Name, string Identity, string PropertyName, string AccessorKind,
        int Arity, bool Static, DeclSig Signature);

    static void CollectNewTypeContainers(JsonNode node, DeclIndex idx)
    {
        if (node is not JsonObject o) return;
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    if (Str(to["name"]) is string nm) AdmitNewType(idx, nm, () => CaptureRecipe(to));
                    CollectNewTypeContainers(to, idx);   // late materialized nested types
                }
    }

    static MethodSnapshot ReadMethodSnapshot(JsonObject method)
    {
        if (Str(method["name"]) is not string name || ReadSig(method) is not DeclSig signature) return null;
        KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var accessorKind);
        return new MethodSnapshot(name, Str(method[DeclarationIdentityBinding.Key]), propertyName, accessorKind,
            (method["typeParams"] as JsonArray)?.Count ?? 0, Bool(method["static"]), signature);
    }

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

    static void CollectSlots(JsonNode decls, Dictionary<string, TypeNode> slots)
    {
        if (decls is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject o && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode t)
                AddSlotSnapshot(slots, n, t);
    }

    static void AddSlotSnapshot(Dictionary<string, TypeNode> slots, string name, TypeNode type)
    {
        if (slots.TryGetValue(name, out var prior))
        {
            if (prior != null && !prior.Equals(type)) slots[name] = null;
        }
        else slots[name] = type;
    }

    static DeclSig ReadSig(JsonObject mo)
    {
        return ReadSignature(TypeJson.Read(mo["ret"]), () => ReadTypeCarrier(mo["nullableGenericRet"]),
            () => ReadTypeCarrier(mo["retKotlinType"]), Bool(mo[NullableErasureOwnershipKnownKey]),
            preferCarrier => ReadParams(mo["params"], preferCarrier));
    }

    static TypeNode ReadTypeCarrier(JsonNode carrier) => Str(carrier) is string encoded ? TypeNode.Parse(encoded) : null;

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

    // A declaration synthesized after NullableGenericErasure carries its physical slot in `type`/`ret` and the
    // exact pre-erasure Kotlin slot in the same carrier as an ordinary declaration. Only a scalar nullable variable
    // establishes the object-erasure boundary owned here. Constructed slots already carry their materialized
    // companion arguments; re-erasing their source metadata would incorrectly replace G<N(T)> with G<object>.
    static TypeNode ReadDeclaredSlot(JsonObject slot, string physicalKey, string carrierKey,
        bool preferCarrier)
    {
        return ReadDeclaredSlot(TypeJson.Read(slot[physicalKey]), () => ReadTypeCarrier(slot[carrierKey]), preferCarrier);
    }

    static TypeNode ReadDeclaredSlot(TypeNode physical, Func<TypeNode> carrier, bool preferCarrier)
    {
        if (preferCarrier && carrier() is TypeNode.Nullable { Of: TypeNode.Tv } scalarNullable)
            return scalarNullable;
        return physical;
    }

    // The declared parameter vector, or null when any slot is untyped — a partially-read vector would silently
    // realign the wrong positions.
    static TypeNode[] ReadParams(JsonNode ps, bool preferCarrier = false)
    {
        if (ps is not JsonArray a) return Array.Empty<TypeNode>();
        return ReadParameterVector(a.Count, index => a[index] is JsonObject parameter
            ? ReadDeclaredSlot(parameter, "type", "nullableGeneric", preferCarrier) : null);
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

    static void ApplyDeclaredUses(JsonNode root, DeclIndex idx, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs)
    {
        _isValue = isValue ?? (_ => false);
        _refs = refs;
        _sourceApplicationOrder = idx.SourceApplicationOrder;
        ApplyRec(root, idx);
    }

    // The CROSS-MODULE half (#86 D1) — the same formula after a distinct capability transition. It exists because of
    // WHEN a referenced callee acquires an owner: kotc emits a referenced top-level call
    // as `callStatic owner=null`, and only MemberCallSubstitution attributes it to the file class the reference index
    // is keyed by, so those calls have no resolvable declaration on the first run. Every rewrite here is gated on a
    // difference plus the object-erasure relation, so re-deriving a slot the first run already corrected is a no-op.
    public static void ApplyAfterReferencedOwnerBinding(
        JsonNode root, DeclIndex idx, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
        => ApplyDeclaredUses(root, idx, isValue, refs);

    // The SAME-MODULE inherited-member half. A call emitted against its Kotlin receiver owner can only be matched to
    // the declaration that owns its nullable-generic slot after InheritedMemberOwnerBinding closes the base-chain
    // substitution (`Derived : Base<String>` reading `Base<T>.values: Array<T?>`). Until then the source-use pass has
    // no declaration under `Derived` and correctly refuses to guess. Re-flow after that distinct identity transition
    // so the physical result remains `Subst(Erase(declaration))`; for a reference array, ApplyDerivedRet authors the
    // explicit checked projection back to the concrete Kotlin array seen by the consumer. The same gates make this
    // idempotent for calls already handled by either earlier phase.
    public static void ApplyAfterInheritedOwnerBinding(
        JsonNode root, DeclIndex idx, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
        => ApplyPhysicalUses(root, idx, isValue, refs);

    // UnsafeAccessorLowering replaces a Kotlin member edge with a newly-declared wrapper call. The wrapper's
    // declaration is the first authoritative statement of that new call's physical result; re-flow once after the
    // holder types have been admitted to the index so an object-erased result is projected at its actual consumer.
    // This is necessary for lifted closure bodies too: their protected access can only become an UnsafeAccessor after
    // the original inherited-member pass, and retaining the pre-rewrite concrete `ret` would lie about the wrapper.
    public static void ApplyAfterUnsafeAccessorSynthesis(
        JsonNode root, DeclIndex idx, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
        => ApplyPhysicalUses(root, idx, isValue, refs);

    static void ApplyPhysicalUses(JsonNode root, DeclIndex idx, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
    {
        _isValue = isValue ?? (_ => false);
        _refs = refs;
        _sourceApplicationOrder = null;
        ApplyRec(root, idx);
    }

    static void ApplyRec(JsonNode root, DeclIndex idx)
    {
        if (root is not JsonObject o) return;
        ProcessMethods(o["methods"], idx, OwnerNullableFrame(o));
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    ProcessCtors(to, idx);
                    ApplyRec(to, idx);
                }
    }

    // A CONSTRUCTOR body is a body, and its base/this DELEGATION arguments are call arguments — into the delegated
    // constructor's own (erased) parameter vector. `class Derived(y: Int?) : Base<Int>(y)` hands a `Nullable<int32>`
    // to a `Base<T>..ctor(object)` and is invalid IL without the box, exactly as `Base<Int>(y)` written as an
    // expression would be. Skipping `ctors` left that whole surface out of the use axis.
    static void ProcessCtors(JsonObject to, DeclIndex idx)
    {
        if (to["ctors"] is not JsonArray ctors) return;
        var ownerArgs = OwnTypeArgs(to);
        var baseType = TypeJson.Read(to["base"]) as TypeNode.Fqn;
        TryVisibleConstructors(baseType?.Name, idx, out var baseParams);
        var ownName = Str(to["name"]);
        foreach (var c in ctors)
        {
            if (c is not JsonObject co) continue;
            var ctx = new Ctx { Idx = idx, OwnerNullableFrame = OwnerNullableFrame(to) };
            if (co["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        ctx.Env[pn] = pt;
            DelegationArgs(co, "baseArgs", baseParams, OwnerArguments(baseType), ctx);
            TryVisibleConstructors(ownName, idx, out var ownParams);
            DelegationArgs(co, "thisArgs", ownParams, ownerArgs, ctx);
            if (co["body"] is JsonNode body) Eval(body, ctx);
        }
    }

    static void DelegationArgs(JsonObject co, string key, Dictionary<int, TypeNode[]> byArity,
        TypeNode[] ownerArgs, Ctx ctx)
    {
        if (co[key] is not JsonArray args) return;
        var argTypes = new TypeNode[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (args[i] != null) argTypes[i] = Eval(args[i], ctx);
        TypeNode[] declParams = null;
        byArity?.TryGetValue(args.Count, out declParams);
        RealignDelegation(args, declParams, ownerArgs, argTypes);
    }

    // A generic owner's own type parameters, as the arguments a `this(...)` delegation substitutes with (a ctor
    // delegating within `Holder<T>` stays at `Holder<T>`).
    static TypeNode[] OwnTypeArgs(JsonObject to)
    {
        if (to["typeParams"] is not JsonArray tps || tps.Count == 0) return null;
        var args = new TypeNode[tps.Count];
        for (var i = 0; i < tps.Count; i++) args[i] = new TypeNode.Tv("type", i);
        return args;
    }

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

    static NullableRepresentationFrame OwnerNullableFrame(JsonObject owner)
        => Str(owner[KotlinSupertypesRecord.PreKey]) is string encoded
            && JsonNode.Parse(encoded)?[NullableRepresentationFrame.MetadataKey] is JsonNode frame
                ? NullableRepresentationFrame.Read(frame) : null;

    static void ProcessMethods(JsonNode methods, DeclIndex idx, NullableRepresentationFrame ownerFrame)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
            if (m is JsonObject mo)
            {
                var ctx = new Ctx { Idx = idx, Ret = TypeJson.Read(mo["ret"]), OwnerNullableFrame = ownerFrame,
                    MethodNullableFrame = Str(mo[NullableRepresentationTypes.MethodFrameKey]) is string encoded
                        ? NullableRepresentationFrame.Read(JsonNode.Parse(encoded)) : null };
                if (mo["params"] is JsonArray ps)
                    foreach (var p in ps)
                        if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                            ctx.Env[pn] = pt;
                if (mo["body"] is JsonNode body) Eval(body, ctx);
            }
    }

    // Forward type-flow evaluation of a body node: rewrites erasure-boundary uses in place and returns the
    // node's static type (null for statements / unknown). A `var` registers its (possibly-retyped) type in
    // `ctx.Env` before its siblings are visited, so a later read of that local re-derives against the corrected type.
    static TypeNode Eval(JsonNode node, Ctx ctx)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var it in a) if (it != null) Eval(it, ctx);
                return null;
            case JsonObject o:
                break;
            default:
                return null;
        }
        var obj = (JsonObject)node;
        switch (Str(obj["k"]))
        {
            case "var":
                EvalVar(obj, ctx);
                return null;
            case "local":
                // A materialized subtree can retain reads of enclosing locals without carrying the whole enclosing
                // method into its normalization work item. While the enclosing source body is available, keep the
                // explicit stamp aligned with the flowed declaration type; a later construction-boundary clone can
                // then use that stamp as the exact source-frame fact.
                if (Str(obj["name"]) is string ln && ctx.Env.GetValueOrDefault(ln) is TypeNode flowedLocal)
                {
                    RestampSty(obj, flowedLocal);
                    return flowedLocal;
                }
                return NodeType.Stamp(obj);
            case "const":
                return TypeJson.Read(obj["type"]);
            case "new":
                return EvalNew(obj, ctx);
            case "cast":
                if (obj["e"] != null) Eval(obj["e"], ctx);
                return TypeJson.Read(obj["type"]);
            case "callStatic":
                return EvalCallStatic(obj, ctx);
            case "arrayGet":
            {
                var arrType = obj["array"] != null ? Eval(obj["array"], ctx) : null;
                if (obj["index"] != null) Eval(obj["index"], ctx);
                if (arrType is TypeNode.Array arr)
                {
                    // Re-stamp the ldelem `elem` token ONLY when the flowed array element is the object-erasure
                    // of the stamped one (same discipline as every other rewrite here) — caps the blast radius to
                    // the erasure family even if a flat-env local type is stale.
                    if (TypeJson.Read(obj["elem"]) is TypeNode cur && !cur.Equals(arr.Elem) && IsObjectErasureOf(arr.Elem, cur))
                    {
                        obj["elem"] = TypeJson.Write(arr.Elem);
                        RestampSty(obj, arr.Elem);   // `elem` IS this node's result slot
                    }
                    return arr.Elem;
                }
                return TypeJson.Read(obj["elem"]);
            }
            case "callInstance":
                return EvalCallInstance(obj, ctx);
            case string ck when IsClrBoundKind(ck):
                return EvalClrCall(obj, ctx);
            case string mk when ClrBoundNode.IsMemberAccess(mk):
                return EvalClrMemberAccess(obj, ctx);
            case "delegateInvoke":
                // A `(…) -> R` invocation is a call whose DECLARATION is the function type itself: the erasure has
                // already given `funcType` its physical components, so each argument fills the slot named there and
                // the value produced is that type's RETURN. Without the return the node reports no type at all and
                // every consumer of a lambda result falls outside the realignment; without the arguments an `Int`
                // handed to an object-erased `(Int?) -> R` parameter reaches the delegate unboxed.
                return EvalDelegateInvoke(obj, ctx);
            case "field":
                return EvalField(obj, ctx);
            case "setLocal":
                EvalSetLocal(obj, ctx);
                return null;
            case "setField":
            case "setFieldExpr":
            case "staticFieldSet":
                EvalSetField(obj, ctx);
                return null;
            case "arraySet":
                EvalArraySet(obj, ctx);
                return null;
            case "return":
            case "returnExpr":
                EvalReturn(obj, ctx);
                return null;
            case "cond":
                return EvalCond(obj, ctx);
            case "newArray":
            case "newArrayInit":
            case "newArraySized":
                // An array CONSTRUCTION — including the pack a `vararg xs: T?` call site builds. Its `elem` is the
                // array's element type, so it is both this node's result type and the target its own elements fill.
                return EvalNewArray(obj, ctx);
            case "forEachInline":
                // A loop VARIABLE is a slot, and this node states its type in `elem`. Binding it is what lets a value
                // consumer inside the loop body be reconciled at all.
                return EvalForEach(obj, ctx);
            case "forArray":
                // `for (x in arr)` states BOTH the `ldelem` token and the loop variable's slot in one `elem`, so it is
                // the read axis at an array element exactly as `arrayGet` is — and it must be re-derived from the array
                // that actually flows. An `Array<Int?>` param is `object[]` (#86 D2) while the stamp taken before the
                // erasure still says `Nullable<int32>`; leaving it emits `ldelem Nullable<int32>` over an `object[]`.
                return EvalForArray(obj, ctx);
            case "nullableHasValue":
            case "nullableValue":
                // Both read a structural `Nullable<V>` — `HasValue` and `Value`. An erased slot hands them an
                // `object` instead, which has neither, so the operand narrows first: `unbox.any Nullable<V>` turns a
                // boxed value into a present one and a genuine null into an empty one, which is exactly the pair of
                // states the erasure represents. `x?.f()` on a `T?` param at `T = Int` is this shape.
                return EvalNullableUnwrap(obj, ctx);
            case "valueBlock":
                // The value a statement-then-expression block produces is its `result`'s. Reporting nothing here
                // blinds every use whose operand is one — `t!!.x` puts its null-check temp inside exactly this shape.
                // A valueBlock may carry EITHER of two statement lists and its consumers run `stmts` then `body`, so
                // both are walked; missing one silently drops a whole subtree out of the realignment.
                if (obj["stmts"] != null) Eval(obj["stmts"], ctx);
                if (obj["body"] != null) Eval(obj["body"], ctx);
                return obj["result"] != null ? Eval(obj["result"], ctx) : null;
            default:
                // Unknown statement/expression: recurse every child, then report a `type`/`ret` if it has one.
                foreach (var kv in obj) if (kv.Value != null) Eval(kv.Value, ctx);
                return TypeJson.Read(obj["type"]) ?? TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        }
    }

    // A node's RESULT TYPE just changed, so the frontend `sty` stamp on it changes with it — the spec §2.7
    // invariant, which is a contract on every pass that retypes a result and not on the deriver that reads one.
    // `sty` is a claim about the value the node produces, never a historical note about the node it used to be,
    // and every downstream deriver reads it FIRST (bir-common/NodeType.cs PRECEDENCE).
    //
    // Here the stale stamp is not merely imprecise. `List<object>` (what the erased declaration actually returns)
    // and `List<Nullable<int32>>` (what kotc stamped at the substituted call site) are UNRELATED invariant reified
    // generics — the very reason this pass exists — so a slot declared from the pre-erasure stamp is INVALID IL
    // rather than a diagnosable drop. Two compositions reach it: the erased call sitting LEFT of a suspending
    // operand, where stage 0 declares the plan's spill local from the stamp, and the erased call BEING the
    // suspension, where the awaited state-machine field is declared from it.
    static void RestampSty(JsonObject obj, TypeNode derived)
    {
        if (obj["sty"] != null) obj["sty"] = TypeJson.Write(derived);
    }

    static void EvalChildrenOf(JsonObject obj, string arrayKey, Ctx ctx)
    {
        if (obj[arrayKey] is JsonArray args)
            foreach (var arg in args) if (arg != null) Eval(arg, ctx);
    }

    static void EvalVar(JsonObject obj, Ctx ctx)
    {
        var env = ctx.Env;
        var initType = obj["init"] != null ? Eval(obj["init"], ctx) : null;
        var name = Str(obj["name"]);
        var declType = TypeJson.Read(obj["type"]);
        if (name == null) return;
        if (declType != null && initType != null && !initType.Equals(declType))
        {
            // A platform (`V!`) local inherits its PHYSICAL representation from the reflected value slot that
            // initialized it. In particular `T? where T : struct` is Nullable<V>, even though dll2klib/Kotlin spell
            // the flexible surface as oblivious(V). Retype this carrier instead of unwrapping at the store: the
            // subsequent Kotlin `!!`/safe-call must still observe HasValue and produce Kotlin's null behavior.
            if (declType is TypeNode.Oblivious platform
                && (initType.Equals(platform.Of)
                    || initType is TypeNode.Nullable nullable && nullable.Of.Equals(platform.Of)))
            {
                obj["type"] = TypeJson.Write(initType);
                env[name] = initType;
                return;
            }
            if (obj["init"] is JsonObject initNode
                && CoerceForTarget(initNode, initType, declType) is JsonNode coercedInit)
            {
                obj["init"] = coercedInit;
                env[name] = declType;
                return;
            }
            if (!IsObjectErasureOf(initType, declType))
            {
                env[name] = declType;
                return;
            }
            // A DIFFERENCE THE CLR CAN CONVERT keeps the declared slot and wraps the init: the whole value erased to a
            // TOP-LEVEL `object` (`val x: Int? = r.v`), reconciled by `unbox.any` for a value declared type and
            // `castclass` for a reference one. Anything else is retyped below.
            // The erasure sits INSIDE a constructed-generic arg / array elem (e.g. `val r: Ref<Int?> =
            // b.a[0]` -> `Ref<object>`). Ref<object> and Ref<Nullable<int32>> are irreconcilable invariant
            // reified generics — retype the slot to the erased form and keep propagating.
            obj["type"] = TypeJson.Write(initType);
            env[name] = initType;
            return;
        }
        env[name] = declType ?? initType;
    }

    static TypeNode EvalCallInstance(JsonObject obj, Ctx ctx)
    {
        var idx = ctx.Idx;
        var recvType = obj["recv"] != null ? Eval(obj["recv"], ctx) : null;
        var stampedRet = StampedResult(obj);
        if (Str(obj["method"]) is not string method) { RealignArgs(obj, null, null, null, null, ctx); return stampedRet; }

        var nodeOwner = TypeJson.Read(obj["ownerType"]);
        // Flow can refine arguments of the selected classifier (Ref<T> -> Ref<object>), but a derived receiver
        // does not replace the selected declaring owner. InheritedMemberOwnerBinding already supplied that owner's
        // exact construction; looking up the same property on the receiver would discard the inheritance edge.
        var erasedRecv = recvType is TypeNode.Fqn { Name: "object", Args: null };
        var flowedOwner = erasedRecv ? null : recvType as TypeNode.Fqn;
        var declaredOwner = nodeOwner as TypeNode.Fqn;
        var owner = flowedOwner != null && (declaredOwner == null || flowedOwner.Name == declaredOwner.Name)
            ? flowedOwner : declaredOwner;
        if (owner == null) { RealignArgs(obj, null, null, null, null, ctx); return stampedRet; }

        // A value returned through an object-erased generic boundary carries the erased instantiation in the
        // receiver flow. Keep every subsequent member dispatch on that same instantiation; the member's own return is
        // then re-derived from its declaration against those corrected owner args, below.
        if (nodeOwner is TypeNode.Fqn stampedOwner && !owner.Equals(stampedOwner)
            && IsObjectErasureOf(owner, stampedOwner))
            obj["ownerType"] = TypeJson.Write(owner);

        // THE RECEIVER IS A USE POSITION TOO. Reconcile the flowed value with the member's bare owner: an erased
        // object narrows through a cast/unbox, while a proven-present Nullable<V> reads V before value dispatch.
        if (obj["recv"] is JsonObject recvNode
            && CoerceForTarget(recvNode, recvType, owner) is JsonNode coercedReceiver)
            obj["recv"] = coercedReceiver;

        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        var selected = SelectUseDeclaration(ReadUseSelection(obj), owner, method, argCount,
            methodArgs?.Length ?? 0, false, idx);
        var decl = selected.Declaration;
        // THE ARGUMENT AXIS: each parameter slot is `Subst(Erase(declared param))` exactly as the return is; with no
        // declaration the call's own descriptor stands in (see RealignArgs).
        RealignArgs(obj, decl?.Params, decl?.ParamsRefused, selected.OwnerArguments, methodArgs, ctx,
            exactPropertyTarget: selected.ExactProperty);
        if (decl?.Ret == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set

        var erasedRet = NullableGenericErasure.EraseNullableTv(decl.Ret, _isValue);
        var derived = Subst(erasedRet, OwnerArguments(owner), methodArgs);
        return ApplyDerivedRet(obj, derived, stampedRet, !erasedRet.Equals(decl.Ret),
            decl.NullableErasureOwnershipKnown, ctx.IsNullableCompanion(derived));
    }

    static TypeNode EvalCallStatic(JsonObject obj, Ctx ctx)
    {
        var stampedRet = StampedResult(obj);
        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        // A top-level fun's declaration lives in one of two indexes, and the OWNER decides which: this compilation's own
        // file class means the same-module `TopLevel` index (keyed by bare name+arity), anything else means a call into
        // a referenced assembly. Both re-derive the generic function's declaration from its pre-erasure form, just as
        // EvalCallInstance does for a generic class member.
        var selected = Str(obj["method"]) is string method
            ? SelectUseDeclaration(ReadUseSelection(obj), StaticOwner(obj) as TypeNode.Fqn,
                method, argCount, methodArgs?.Length ?? 0, true, ctx.Idx)
            : default;
        var decl = selected.Declaration;
        var ownerArgs = selected.OwnerArguments;
        RealignArgs(obj, decl?.Params, decl?.ParamsRefused, ownerArgs, methodArgs, ctx,
            exactPropertyTarget: selected.ExactProperty);
        if (decl?.Ret == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set
        var erasedRet = NullableGenericErasure.EraseNullableTv(decl.Ret, _isValue);
        var derived = Subst(erasedRet, ownerArgs, methodArgs);
        return ApplyDerivedRet(obj, derived, stampedRet, !erasedRet.Equals(decl.Ret),
            decl.NullableErasureOwnershipKnown, ctx.IsNullableCompanion(derived));
    }

    // The expression stamp is already instantiated in the caller frame; ret can still be declaration-relative.
    // Read the shared sty -> ret -> dynRet precedence rather than substituting a declaration result a second time.
    // The selected declaration independently supplies the physical result used for realignment below.
    static TypeNode StampedResult(JsonObject obj)
        => NodeType.Stamp(obj);

    // Take the derived result type, ONLY when it is the object-erasure of what the call site stamped — the exact
    // erasure boundary, never a genuine widen/narrow. A direct-write `Ref<Int?>` (derived == stamped) is untouched.
    //
    // A call may state its result in `ret`/`dynRet` or only in the frontend `sty` stamp. The shared stamp reader prefers
    // the instantiated expression type over declaration-relative results. A rewrite STATES the corrected physical
    // type in `ret` even where none was written before:
    // that is what stops ilemit re-inferring the member's return from a call whose surrounding slots have moved, and
    // it is the same shape UncheckedGenericCastReturnErasure.ApplyReferenced uses at this boundary. `sty` moves with
    // it, per spec §2.7 — a stamp is a claim about the value produced, never a note about the node it used to be.
    // If ret, dynRet and sty are ALL absent, record a result only when the declaration's return was erased.
    // Otherwise this branch leaves the unstamped call alone. A present stamp instead participates in the
    // reconciliation below, including an explicit result cast for a narrower reference-array projection.
    static TypeNode ApplyDerivedRet(JsonObject obj, TypeNode derived, TypeNode stampedRet, bool erasureApplied,
        bool nullableErasureOwnershipKnown = false, bool nullableCompanionResult = false)
    {
        var plan = SelectDerivedResult(derived, stampedRet, erasureApplied,
            nullableErasureOwnershipKnown, nullableCompanionResult);
        switch (plan.Action)
        {
            case DerivedResultAction.Return:
                obj["ret"] = TypeJson.Write(plan.Type);
                if (plan.Dynamic && obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(plan.Type);
                RestampSty(obj, plan.Type);
                break;
            case DerivedResultAction.Stamp:
                RestampSty(obj, plan.Type);
                break;
            case DerivedResultAction.Cast:
                WrapResultCast(obj, derived, plan.Type);
                break;
        }
        return plan.Type;
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

    // Replace the call in place because Eval walks a mutable JsonObject rather than returning rewritten nodes.  The
    // inner call states the exact MethodDef result; the outer cast states the concrete Kotlin value seen by every
    // later consumer.  A second referenced-use pass sees the already-physical inner call and is therefore idempotent.
    static void WrapResultCast(JsonObject call, TypeNode physical, TypeNode semantic)
    {
        var hadSty = call["sty"] != null;
        var inner = call.DeepClone().AsObject();
        inner["ret"] = TypeJson.Write(physical);
        if (inner["dynRet"] != null) inner["dynRet"] = TypeJson.Write(physical);
        RestampSty(inner, physical);

        foreach (var key in call.Select(kv => kv.Key).ToList()) call.Remove(key);
        call["k"] = "cast";
        call["type"] = TypeJson.Write(semantic);
        call["e"] = inner;
        if (hadSty) call["sty"] = TypeJson.Write(semantic);
    }

    // The owner of a static call. kotc names a cross-module callee's file class in `ownerType`; MemberCallSubstitution
    // later restates it as `owner`. A same-module top-level call has neither, and falls to the TopLevel index.
    static TypeNode StaticOwner(JsonObject obj)
        => TypeJson.Read(obj["ownerType"]) ?? TypeJson.Read(obj["owner"]) ?? TypeJson.Read(obj["calleeOwner"]);

    // A CONSTRUCTION is a call whose declaration is the owner's constructor: its args are typed
    // `Subst(Erase(ctor param), the constructed type's own args)`. `Cell<Int>(null)` is the ctor twin of
    // `pickOr<Int>(null, 7)` and fails identically without this.
    static TypeNode EvalNew(JsonObject obj, Ctx ctx)
    {
        var type = TypeJson.Read(obj["type"]);
        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        var selected = SelectConstructorUse(type as TypeNode.Fqn, argCount, ctx.Idx);
        RealignArgs(obj, selected.Parameters, selected.Refusals, OwnerArguments(type as TypeNode.Fqn), null, ctx);
        // The construction may have been RETYPED by the caller's argument realignment before this ran.
        return TypeJson.Read(obj["type"]);
    }

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
        var stampedCall = new JsonObject { ["sty"] = TypeJson.Write(sourceFunction) };
        if (ApplyDerivedRet(stampedCall, physicalCallbackResult, StampedResult(stampedCall), false) != physicalCallbackResult
            || TypeJson.Read(stampedCall["ret"]) != physicalCallbackResult
            || TypeJson.Read(stampedCall["sty"]) != physicalCallbackResult)
            throw new InvalidOperationException("A sty-only function call lost its materialized physical result");
        var callerResult = new TypeNode.Fqn("Result", new TypeNode[] { new TypeNode.Tv("type", 6) });
        var declarationResult = new TypeNode.Fqn("Result", new TypeNode[] { new TypeNode.Tv("type", 2) });
        var resultStamps = new JsonObject {
            ["sty"] = TypeJson.Write(callerResult), ["ret"] = TypeJson.Write(declarationResult),
            ["dynRet"] = TypeJson.Write(physicalCallbackResult),
        };
        if (StampedResult(resultStamps) != callerResult)
            throw new InvalidOperationException("Nullable result flow ignored the instantiated caller-frame stamp");
        resultStamps.Remove("sty");
        if (StampedResult(resultStamps) != declarationResult)
            throw new InvalidOperationException("Nullable result flow ignored its declared-result fallback");
        resultStamps.Remove("ret");
        if (StampedResult(resultStamps) != physicalCallbackResult || StampedResult(new JsonObject()) != null)
            throw new InvalidOperationException("Nullable result flow lost its dynamic or unstamped fallback");
        var companionReturn = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var sourceReturn = new TypeNode.Fqn("Box", new TypeNode[] {
            new TypeNode.Nullable(new TypeNode.Tv("type", 0)) });
        var accessor = new JsonObject {
            ["ret"] = TypeJson.Write(companionReturn), ["nullableGenericRet"] = TypeNode.ToJson(sourceReturn),
        };
        if (ReadDeclaredSlot(accessor, "ret", "nullableGenericRet", true) != companionReturn)
            throw new InvalidOperationException("Late accessor re-erased its materialized nullable companion");
        accessor["ret"] = TypeJson.Fqn("object");
        var scalarReturn = new TypeNode.Nullable(new TypeNode.Tv("type", 0));
        accessor["nullableGenericRet"] = TypeNode.ToJson(scalarReturn);
        if (ReadDeclaredSlot(accessor, "ret", "nullableGenericRet", true) != scalarReturn)
            throw new InvalidOperationException("Late accessor lost scalar nullable-erasure ownership");
        var scalarCall = new JsonObject {
            ["ret"] = TypeJson.Fqn("object"), ["sty"] = TypeJson.Fqn("kotlin.String"),
        };
        ApplyDerivedRet(scalarCall, new TypeNode.Fqn("object"), new TypeNode.Fqn("object"), true);
        if (TypeJson.Read(scalarCall["sty"]) != TypeJson.Read(scalarCall["ret"]))
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

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static bool Bool(JsonNode n) => (n as JsonValue)?.TryGetValue<bool>(out var b) == true && b;
}
