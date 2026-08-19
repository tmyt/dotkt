using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// bir2cir's single, sole transform. Rewrites the Kotlin type vocabulary in a BIR-shaped JSON tree into the
// CLR-codegen vocabulary ilemit consumes, producing a BIR-SHAPED CIR (same node shape; only type strings change).
//
// Mode gate (a property of the build, selected by the `--build-stdlib` CLI flag):
//   refBuild = StdlibMode == Metadata (`--build-stdlib=metadata`)  -> the pure-Kotlin REFERENCE surface.
// In the REFERENCE build a kotlin.* primitive token is kept VERBATIM (pure-Kotlin metadata; the bare FQN
// "kotlin.Int" stays "kotlin.Int"); the rewrite is a pure passthrough. In EVERY other build (the runtime stdlib,
// and all app builds) a bare kotlin.* primitive lowers to its CLR token (kotlin.Int -> int, ...).
//
// COMPREHENSIVE WALK — kotc emits a bare `kotlin.*` FQN for every source-type primitive at EVERY position:
// signatures, expression/statement type tokens (call owners, conv targets, generic constraints, array elem
// types, lambda/func types, ...). So the lowering recurses the WHOLE node tree and rewrites every type-bearing
// string (see TypeKeys + the `sig` comma-list + the `attrs`/attribute-class force path), not just the signature
// keys — a primitive left un-lowered in an expression position reaches ilemit as `kotlin.Byte` and fails to
// resolve ("cannot resolve .NET type kotlin.Byte").
static class BirTypeLowering
{
    // Kotlin's generic Enum<E> classifier is represented by the non-generic CLR System.Enum classifier. Passes that
    // synthesize ABI for physically reified generic owners must consult this rule instead of treating the BIR arity
    // as proof that a CLR generic TypeDef exists.
    internal static bool ErasesGenericApplicationToNonGenericClassifier(string fqn) => fqn == "kotlin.Enum";

    // The `Span<T>` identity pair, in ONE place: kotc emits the faithful `kotlin.clr.Span` intrinsic name and this
    // pass owns the BCL substitution below. Passes that run BEFORE the lowering and must reason about the CLR type
    // (ReferenceMetadataIndex.IsByRefLikeFqn — `System.Span<T>` is a `ref struct`) canonicalize through these two
    // constants rather than restating either spelling.
    public const string SpanIntrinsicFqn = "kotlin.clr.Span";
    public const string SpanClrFqn = "System.Span";

    // The bare kotlin.* tokens and their CLR-codegen lowering. Consulted only in the non-reference
    // (substitute/app) build; the reference build keeps every kotlin.* token verbatim.
    //
    // kotc emits ONLY the type's FQN identity (kotlin.String / kotlin.Any / kotlin.UInt / ...), never a CLR
    // resolution marker — so EVERY @Clr-bound foundational type lowers HERE, uniformly, exactly like the signed/
    // bool/char primitives: kotlin.String -> string, kotlin.Any -> object, and the unsigned set (note
    // kotlin.UByte is an UNSIGNED byte = System.Byte, token "byte", NOT the signed "sbyte"). The whole set is
    // mode-gated by refBuild (LowerTypeString below): the reference surface keeps kotlin.* verbatim, every other
    // build lowers. kotlin.Unit is the ONE token NOT here: it is position-dependent (return -> void via the
    // ReturnKeys path; a Unit VALUE keeps the emitted Unit type — you cannot have a `void` field), handled
    // separately. KotlinAllToClr (the attribute-blob force map) additionally carries kotlin.Unit -> void and is
    // applied UNCONDITIONALLY because an attribute blob needs a concrete System.* type even in the ref build.
    // #55: the non-force `KotlinToClr` map was DELETED. It was pure redundancy — every entry (kotlin.Int -> "int",
    // kotlin.String -> "string", …) merely SHADOWED the primitive's own `@ClrTypeAlias("System.Int32"/"System.String"/…)`,
    // which bir2cir already scans from the ref.dll into the `_aliases` index. A primitive now lowers to its BCL alias
    // (System.Int32/System.SByte/…) via `AliasBcl` in LowerType/LowerLeaf, exactly like every other @ClrTypeAlias type;
    // ilemit's MapType resolves `System.Int32` to `typeof(int)` identically to the old shorthand, and its three
    // name-keyed opcode switches (EmitConst/EmitConv/ConstArgValue) normalize the alias back to the shorthand alphabet.
    // Only `KotlinAllToClr` (below) survives, for the attribute-blob force path where no ref.dll is loaded.

    // The FULL kotlin.* -> CLR map, used UNCONDITIONALLY (both modes) on the attribute-metadata force path. A
    // custom-attribute's constructor-argument / field / property types are encoded into the assembly's attribute
    // blob, which the CLR custom-attribute encoder accepts ONLY for concrete System.* types — never the emitted
    // pure-Kotlin class. So even in the reference build (where every other kotlin.* primitive is kept verbatim)
    // an attribute-carried type must lower to its real CLR token, including String/Any/Unit and the unsigned set.
    static readonly IReadOnlyDictionary<string, string> KotlinAllToClr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Int"] = "int",
        ["kotlin.Long"] = "long",
        ["kotlin.Short"] = "short",
        ["kotlin.Byte"] = "sbyte",
        ["kotlin.Double"] = "double",
        ["kotlin.Float"] = "float",
        ["kotlin.Boolean"] = "bool",
        ["kotlin.Char"] = "char",
        ["kotlin.Nothing"] = "object",
        ["kotlin.String"] = "string",
        ["kotlin.Any"] = "object",
        ["kotlin.Unit"] = "void",
        ["kotlin.UInt"] = "uint",
        ["kotlin.ULong"] = "ulong",
        ["kotlin.UByte"] = "byte",
        ["kotlin.UShort"] = "ushort",
    };

    // A specialized primitive array FQN -> its element FQN. kotc emits the faithful `kotlin.IntArray` /
    // `kotlin.UIntArray` identity (like signed IntArray, #76 unified the unsigned set to the same faithful shape —
    // no value-class decomposition in kotc); bir2cir DECOMPOSES it to `Array(elem)` here (the representation
    // decision) in EVERY build — the element then lowers to the CLR primitive (app/rt: Int->System.Int32,
    // UByte->System.Byte) or stays kotlin.* (ref) like any other type-arg. ArrayConstructionLowering uses the SAME
    // map to derive the sized ctor + the array intrinsics' element.
    public static readonly IReadOnlyDictionary<string, string> PrimArrayElem = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.IntArray"] = "kotlin.Int",
        ["kotlin.LongArray"] = "kotlin.Long",
        ["kotlin.DoubleArray"] = "kotlin.Double",
        ["kotlin.FloatArray"] = "kotlin.Float",
        ["kotlin.BooleanArray"] = "kotlin.Boolean",
        ["kotlin.CharArray"] = "kotlin.Char",
        ["kotlin.ByteArray"] = "kotlin.Byte",
        ["kotlin.ShortArray"] = "kotlin.Short",
        // #76: the unsigned specialized arrays lower to the UNSIGNED native array (byte[]/ushort[]/uint[]/ulong[]),
        // uniformly with signed. Their value-class `.storage` backing (the SIGNED array) + the wrap-ctor over a
        // signed array are erased to a same-underlying-primitive reinterpret cast in MemberCallSubstitution.
        ["kotlin.UByteArray"] = "kotlin.UByte",
        ["kotlin.UShortArray"] = "kotlin.UShort",
        ["kotlin.UIntArray"] = "kotlin.UInt",
        ["kotlin.ULongArray"] = "kotlin.ULong",
    };

    // Every JSON key whose string (or string[]) value is a TYPE reference, across signatures, expressions and
    // statements. Lowering must catch a primitive WHEREVER it sits. Identity/data keys
    // that may carry a kotlin.*-looking string but are NOT types (name/value/var/method/id/kind/...) are
    // deliberately excluded — lowering them would corrupt a declaration name or a string literal. `sig` (a
    // comma-joined type list) and `attrs` (attribute applications) get their own handling below.
    static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal)
    {
        // signature positions (the original TypeProperties set)
        "type", "ownerType", "calleeOwner", "ret", "suspendRet", "base", "interfaces", "argTypes", "delegationSig",
        // BIR-only exact constructor declaration vector.  UnsafeAccessor consumes it when it rewrites the edge;
        // otherwise same-unit constructor binding consumes its physically-lowered form after this pass.
        "memberSignature",
        // expression / statement type positions
        "dynRet", "funcType", "typeArgs", "constraints", "recvType", "iface", "excType",
        "keyType", "valType", "iterType", "accessOwner", "elem", "to", "owner",
        "samType", "closureType",
        // W1-S1 (#46): the clrGeneric* FIR-resolved member descriptor — the callee's DECLARED param types (OPEN,
        // method-tv positional), lowered to the CLR vocabulary for exact scalar memberRef resolution (replaces `shapes`).
        "resolvedMemberParams",
        // additional type-reference keys ilemit reads (absent in today's BIR but lowered for robustness)
        "elemType", "accType", "clrType", "tupleType", "parameterTypes",
        // A MethodImpl descriptor's parameter vector (`clrInterfaceImpls`/`clrBaseImpls`) is a RAW array of type
        // nodes, and ilemit matches it against a method builder keyed by LOWERED declaration params — so it has to
        // arrive in the same vocabulary. A declaration's own `params` are `{name, type}` objects rather than type
        // nodes, so they take the ordinary recursive walk exactly as before; only the raw vector changes.
        "params",
    };

    // The RETURN-slot keys. kotlin.Unit is the ONE position-dependent token: kotc's birType change made it emit
    // bare "kotlin.Unit" everywhere (it was "void" in a return slot before). A Unit RETURN is the Kotlin "no value"
    // convention -> CLR `void` (a Unit-returning fun is a void method; the entry point `fun main(): Unit` MUST be
    // void or the CLR rejects the program). This is UNIFORM across ref AND substitute/app — a Unit-returning method
    // is void in both, matching the prior behaviour — so it is NOT mode-gated. A kotlin.Unit VALUE (a field, a
    // generic arg like Sequence<Unit>, a receiver) keeps the emitted Unit type (you cannot have a `void` field) — it
    // rides as a structured `{t:fqn,name:"kotlin.Unit"}` node and passes through unchanged. (Mirrors kotc birTypeDeleg's
    // "kotlin.Unit -> void in return, kotlin.Unit in type-arg" split.) The numeric primitives are NOT
    // position-dependent — they lower uniformly everywhere via their @ClrTypeAlias (the AliasBcl path, #55).
    // RETURN-POSITION type keys, grouped by a SHARED PROPERTY (a return-slot type, where kotlin.Unit lowers to
    // `void` via LowerReturnValued) — NOT synonyms. The members are DISTINCT ROLES that can coexist on one node:
    // `ret` (plain return), `dynRet` (@Clr dynamic-dispatch return), `suspendRet` (a suspend fn/lambda's T of
    // Continuation<T>) — e.g. a callInstance carries ret+dynRet, a newSuspendLambda carries ret+suspendRet. This is
    // the return-position parallel to `TypeKeys` (value-position types). (Dead keys `selRet`/`returnType` — 0 BIR
    // emit, no value ever read — were removed in #37 m5.)
    static readonly HashSet<string> ReturnKeys = new(StringComparer.Ordinal)
    {
        "ret", "dynRet", "suspendRet",
    };

    static readonly string[] ModifierPrefixes = { "byref:", "array:", "nullable:" };

    // The ref.dll @ClrTypeAlias index (Kotlin FQN -> BCL), set per top-level Lower() call. Consulted for EVERY CLR-bound
    // type token — the foundational primitives (kotlin.Int -> System.Int32, kotlin.String -> System.String, …) AND the
    // rest (collections -> System...IReadOnlyCollection, StringBuilder, Regex, …). #55: the primitives are no longer
    // shadowed by a hardcoded map; they resolve here like any other alias. Single-threaded per bir2cir run, so a static
    // binding is sufficient.
    static IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.Ordinal);

    // The struct-ness ORACLE (#37/#48 nullability fold), set per top-level Lower() call. True for a VALUE type FQN
    // (a foundational primitive, a ref.dll struct/enum, or a LOCAL enum/struct in this compilation). Decides whether a
    // `{t:nullable}` node keeps its wrapper (value inner -> `System.Nullable<T>`) or is STRIPPED to the bare inner
    // (reference inner -> the CLR type is nullable in IL regardless; the `?` rides an NRT byte the decl walk emitted).
    static Func<string, bool> _isValueFqn = _ => false;

    static string AliasBcl(string fqn) => _aliases.TryGetValue(fqn, out var bcl) ? bcl : null;

    // ARG-POSITION VARIANCE COLLAPSE (Root V): the INVARIANT BCL sibling of each covariant readonly collection interface,
    // used ONLY at generic-arg depth >= 1 (see LowerType) where the covariant alias is unrescuable against a concrete
    // invariant value — `IList<T>` does NOT inherit `IReadOnlyList<T>`, so `Dictionary<K,IList<V>>` inhabits no
    // `IDictionary<K,IReadOnlyList<V>>` (invariant). The concrete BCL type inhabits these exactly: List<T>/HashSet<T>
    // implement IList<T>/ICollection<T>. (Iterable->IEnumerable is covariant, no collapse; Map/MutableMap already
    // collapse to IDictionary at head.) HEAD-position seams (a head IList<T> value into a readonly IReadOnlyList<T>
    // slot) are materialized as explicit CIR casts by CollectionViewCallCoercion after this transform.
    static readonly IReadOnlyDictionary<string, string> InvariantSibling = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.collections.List"] = "System.Collections.Generic.IList",
        ["kotlin.collections.Collection"] = "System.Collections.Generic.ICollection",
        ["kotlin.collections.Set"] = "System.Collections.Generic.ICollection",
    };

    // KProperty's generic parameters are not collection-storage slots: each one is substituted directly into a CLR
    // interface method parameter/return (`KProperty1<T,V>.get(T):V`, `KMutableProperty1.set(T,V)`, etc.). Lowering a
    // concrete `List<X>` argument through Root-V here would produce `KProperty1<IList<X>,V>` while the implementing
    // method's head-position parameter lowers to `IReadOnlyList<X>`, making the generated reference type unloadable.
    // Treat these carrier arguments as method-slot heads (typeArg:false); recursion inside the argument still applies
    // Root-V normally, so `KProperty0<List<List<X>>>.get()` and its return stay byte-identical at every depth.
    // ClrPropertyStub<V> must use the same rule because it supplies the generated reference's KProperty<V> base face.
    static readonly HashSet<string> InterfaceMethodSlotCarriers = new(StringComparer.Ordinal)
    {
        "kotlin.reflect.KProperty",
        "kotlin.reflect.KMutableProperty",
        "kotlin.reflect.KProperty0",
        "kotlin.reflect.KMutableProperty0",
        "kotlin.reflect.KProperty1",
        "kotlin.reflect.KMutableProperty1",
        "kotlin.reflect.KProperty2",
        "kotlin.reflect.KMutableProperty2",
        "kotlin.reflect.ClrPropertyStub",
    };

    /// <summary>
    /// Which CLR type a Kotlin-surface type HEAD becomes, given its already-lowered arguments. The single
    /// implementation of that decision.
    /// </summary>
    /// <remarks>
    /// This exists because there are two callers, not one. `LowerType` applies it to the declarations this pass
    /// lowers; the member-reference serializer applies it to a signature read back out of the reference twin,
    /// which speaks the Kotlin surface while the member being named lives in the runtime twin, which speaks
    /// this. Those two must agree at EVERY branch — the erasure of a generic classifier, the contravariant
    /// `Comparable<Any?>` collapse, the arg-position variance collapse, the plain alias — and the only way to
    /// guarantee they do is for there to be one branch each. A serializer that reproduced "the same rule"
    /// reproduced two of the four, and named members that exist in neither twin.
    ///
    /// `bcl` is the type's @ClrTypeAlias target, or null when it has none; `loweredArgs` is null for a leaf.
    /// `collapseInvariant` is the caller's position judgement — a storage slot collapses, a head or method slot
    /// does not — because only the caller knows which of the two vocabularies its position came from.
    /// </remarks>
    internal static TypeNode PhysicalHead(string kotlinFqn, string bcl, TypeNode[] loweredArgs, bool collapseInvariant)
    {
        // `kotlin.Enum<E>` -> the NON-generic `System.Enum` (a Kotlin enum is a real CLR System.Enum, not the
        // generic stdlib class); drop the self-referential arg (`where T : Enum`).
        if (ErasesGenericApplicationToNonGenericClassifier(kotlinFqn) && loweredArgs != null)
            return new TypeNode.Fqn("System.Enum");
        // A leaf: a @ClrTypeAlias type — a foundational primitive (kotlin.Int -> System.Int32) or a non-primitive
        // BCL (StringBuilder/Regex/IComparable/…) -> the BCL FQN. Otherwise the name stands: user / stdlib /
        // in-assembly names are unchanged, trusted external DotKt identities become their physical metadata names.
        if (loweredArgs == null) return new TypeNode.Fqn(bcl ?? PhysicalName(kotlinFqn));
        // `Comparable<*>` / `Comparable<Any?>` -> the NON-generic `System.IComparable` (contravariant; no value
        // type is IComparable<object>). A concrete arg keeps the generic form. Accept both the semantic alias and
        // its already-physical head: representation passes may author a CLR call owner before this final lowering,
        // and that owner must make the same collapse decision as an ordinary declaration/value slot.
        if ((bcl ?? kotlinFqn) == "System.IComparable" && loweredArgs.Length == 1
            && ComparableApplicationCollapses(loweredArgs[0]))
            return new TypeNode.Fqn("System.IComparable");
        if (bcl == null) return new TypeNode.Fqn(PhysicalName(kotlinFqn), loweredArgs);
        // ARG-POSITION VARIANCE COLLAPSE (Root V): in a storage slot a covariant readonly collection interface ->
        // its INVARIANT sibling, so a concrete invariant value inhabits the nested slot EXACTLY. The head keeps the
        // covariant alias; CollectionViewCallCoercion materializes any resulting call-site seam as a CIR cast.
        if (collapseInvariant && InvariantSibling.TryGetValue(kotlinFqn, out var inv))
            return new TypeNode.Fqn(inv, loweredArgs);
        // A generic application: a @ClrTypeAlias GENERIC owner -> the BCL generic (ilemit arity-constructs).
        return new TypeNode.Fqn(bcl, loweredArgs);
    }

    /// <summary>
    /// True for a type name this pass emits when it lowers a Kotlin FUNCTION TYPE to a CLR delegate.
    /// </summary>
    /// <remarks>
    /// A reader that meets one of these in a physical signature is looking at what the document calls `fn`, and
    /// the difference is not cosmetic: a function type's parameters and return are METHOD SLOTS, while an
    /// ordinary constructed generic's arguments are storage. Any rule that is positional — the arg-position
    /// variance collapse, the nullable-generic erasure — answers differently for the two, so a reader that
    /// cannot tell them apart asks the wrong question of every delegate it sees.
    /// </remarks>
    internal static bool IsLoweredFunctionType(string clrFqn) =>
        clrFqn is "System.Func" or "System.Action"
            or "DotKt.Runtime.CompilerServices.KFunc" or "DotKt.Runtime.CompilerServices.KAction";

    // The method-slot rule, readable by the member-reference serializer.
    //
    // bir2cir resolves a stdlib member against the REFERENCE twin, which declares the Kotlin surface, while the
    // member a reference has to name lives in the RUNTIME twin, which declares what this pass produces. Naming
    // that member therefore means applying THIS lowering to a signature read back out of metadata — and the
    // collapse is position-dependent, so a serializer that walks a signature applying only the head alias names a
    // member that exists in neither twin. Exposing the rule, rather than letting the serializer restate it, is
    // what stops two spellings of one decision from drifting; that drift is exactly what happened once, and it
    // was invisible because the descriptor the reference got compared against restated the rule the same way.

    internal static bool IsMethodSlotCarrier(string kotlinFqn) =>
        InterfaceMethodSlotCarriers.Contains(kotlinFqn);

    // A synthesized result slot sometimes has to be named before this lowering pass runs (the suspend
    // TaskCompletionSource<R>/RootContinuation<R> drive is the canonical case). Its public Task<R> must retain the
    // same readonly head type a Kotlin call observes; spelling that BCL head explicitly also exempts this one coherent
    // producer/consumer slot from Root-V's generic-argument collapse. Nested collection arguments still lower normally.
    internal static TypeNode AsReadonlyResultSlot(TypeNode t) => t switch
    {
        TypeNode.Fqn { Name: "kotlin.collections.List", Args: not null } f
            => new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList", f.Args),
        TypeNode.Fqn { Name: "kotlin.collections.Collection" or "kotlin.collections.Set", Args: not null } f
            => new TypeNode.Fqn("System.Collections.Generic.IReadOnlyCollection", f.Args),
        TypeNode.Nullable n => new TypeNode.Nullable(AsReadonlyResultSlot(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(AsReadonlyResultSlot(o.Of)),
        _ => t,
    };

    // Whether the INNER of a `{t:nullable}` node is a value type — evaluated on the SEMANTIC (pre-lowering) inner so a
    // struct/enum/primitive FQN is recognized before it is rewritten to a CLR shorthand / BCL name. A function type,
    // array, byRef, or type variable is treated as a reference (stripped). A value FQN keeps the wrapper (a value `T?`
    // is the structural `System.Nullable<T>`) — INCLUDING a constructed one: `ArraySegment<String>` is a struct and
    // `ArraySegment<String>?` is a `Nullable<ArraySegment<String>>`. The oracle answers false for a constructed
    // REFERENCE generic, so `List<String>?` still strips.
    static bool IsValueNullableInner(TypeNode of) => of switch
    {
        TypeNode.Fqn f => _isValueFqn(f.Name),
        _ => false,
    };

    // === STRUCTURED TypeNode lowering (#37 m1) =================================================
    // The freeze put every kotc type field as a structured `{t:…}` Type node (DotKt.Bir.TypeNode). The
    // string helpers below (LowerTypeString/…) survive ONLY for the still-string legacy fields (`sig`
    // comma-list, a literal `accessOwner`/clr* owner FQN); every OBJECT-valued type slot flows through
    // LowerType(TypeNode). Output vocabulary mirrors the old strings exactly, wrapped in TypeNode:
    //   primitive -> Fqn("System.Int32"/…) (its @ClrTypeAlias BCL form, #55; ilemit's opcode switches normalize it back),
    //   Unit-in-return / const|try Unit value -> Fqn("void"), a suspend fn VALUE slot -> Fqn("object"),
    //   @ClrTypeAlias owner -> Fqn(bcl[,args]) (no clr:/clrg: marker — ilemit derives from the name),
    //   an in-assembly/user/stdlib FQN -> unchanged, a `tv` -> unchanged (ilemit maps scope+i to !i/!!i).
    static readonly TypeNode VoidType = new TypeNode.Fqn("void");
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    // The one representation decision shared by full type lowering and the semantic-boundary Comparable bridge pass.
    // Reference-nullability/oblivious wrappers are annotations that disappear before the physical generic head is
    // chosen, so Comparable<Any?> and Comparable<Any!> occupy the non-generic IComparable face just like
    // Comparable<object>. A nullable value or ordinary reference classifier does not collapse.
    internal static bool ComparableApplicationCollapses(TypeNode t) => t switch
    {
        TypeNode.Nullable n => ComparableApplicationCollapses(n.Of),
        TypeNode.Oblivious o => ComparableApplicationCollapses(o.Of),
        TypeNode.Fqn f when f.Args == null =>
            f.Name is "object" or "System.Object" or "kotlin.Any" or "kotlin.Nothing",
        _ => false,
    };

    static string PhysicalName(string semanticName) =>
        _localTypeNames.Contains(semanticName) ? semanticName
        : _physicalTypeNames.TryGetValue(semanticName, out var physical) ? physical : semanticName;

    // typeArg = "this type sits in a generic type-ARGUMENT position": a primitive there stays BOXED
    // (kotlin.Int / the JVM-boxing dual-representation — Comparable<kotlin.Int>, IReadOnlyList<kotlin.Int>);
    // a bare/value primitive lowers to the CLR shorthand. Only Fqn.args propagate typeArg=true; array/byref/
    // nullable/fn element+param+return positions are value positions (typeArg=false).
    public static TypeNode LowerType(TypeNode t, bool refBuild, bool force, bool typeArg)
    {
        switch (t)
        {
            case TypeNode.Fqn f:
                {
                    // A SIGNED primitive array (`kotlin.IntArray`) -> `Array(elem)` in EVERY build (ref included): this is
                    // the array REPRESENTATION, not a primitive substitution, so it fires before the refBuild passthrough.
                    // The element then lowers on the recursive call (kotlin.Int -> System.Int32 in app/rt, verbatim in ref).
                    if (f.Args == null && PrimArrayElem.TryGetValue(f.Name, out var arrElemFq))
                        return new TypeNode.Array(LowerType(new TypeNode.Fqn(arrElemFq), refBuild, force, typeArg: false));
                    // `kotlin.clr.Span<T>` -> the real `System.Span<T>` in EVERY build (ref included). A synthetic interop
                    // marker with NO ref.dll @ClrTypeAlias definition; kotc emits the FAITHFUL `kotlin.clr.Span` identity
                    // and bir2cir OWNS the BCL substitution (M11 — the last naked `System.*` name left in kotc). Placed
                    // before the refBuild passthrough so the ref build substitutes it too, matching the former kotc birType
                    // (which emitted `System.Span` unconditionally); the element lowers like any generic type-arg.
                    if (f.Name == SpanIntrinsicFqn && f.Args != null)
                        return new TypeNode.Fqn(SpanClrFqn, f.Args.Select(a => LowerType(a, refBuild, force, typeArg: true)).ToArray());
                    // The reference build keeps Kotlin type semantics verbatim, but CIR must still name an external
                    // DotKt TypeDef by its exact CLR metadata identity. This matters for nested types under generic owners:
                    // `Outer.Nested` physically lives at `Outer`1+Nested` even when Nested itself declares no arguments.
                    if (!force && refBuild)
                        return new TypeNode.Fqn(PhysicalName(f.Name),
                            f.Args?.Select(a => LowerType(a, refBuild, force: false, typeArg: true)).ToArray());
                    // `kotlin.Enum<E>` -> the NON-generic `System.Enum` (a Kotlin enum is a real CLR System.Enum, not
                    // the generic stdlib class); drop the self-referential arg (`where T : Enum`).
                    var methodSlotCarrier = f.Args != null && InterfaceMethodSlotCarriers.Contains(f.Name);
                    var loweredArgs = f.Args?.Select(a => LowerType(a, refBuild, force,
                        typeArg: methodSlotCarrier ? false : true)).ToArray();
                    if (loweredArgs == null)
                    {
                        // A leaf: a foundational primitive (numeric/bool/char + String/Any/Nothing + the unsigned set)
                        // lowers to the CLR type in EVERY position — a type-arg primitive reifies as the CLR value type
                        // (`List<Int>` -> IReadOnlyList<System.Int32>), the CLR-idiomatic form (the boxed `kotlin.*` isn't an
                        // emitted type in the substitute/app build; the ref build keeps kotlin.* via the refBuild passthrough).
                        // #55: the non-force path reads the primitive's `@ClrTypeAlias("System.Int32")` straight from the
                        // ref.dll index (AliasBcl below) — the hardcoded KotlinToClr shadow was DELETED. The force/attribute-
                        // blob path keeps KotlinAllToClr: a custom-attribute blob needs a concrete System.* even in the ref
                        // build, which has no ref.dll to read.
                        if (force && KotlinAllToClr.TryGetValue(f.Name, out var clr)) return new TypeNode.Fqn(clr);
                    }
                    return PhysicalHead(f.Name, AliasBcl(f.Name), loweredArgs, collapseInvariant: typeArg && !refBuild);
                }
            case TypeNode.Tv:
                return t;   // scope+i preserved; ilemit maps scope:"type"->!i / scope:"method"->!!i
            case TypeNode.Fn fn:
                // A suspend-fn VALUE in a general TYPE slot is a Continuation state-machine OBJECT (not a delegate)
                // -> erase to object; a plain fn is a delegate (Func/Action) with lowered ret/params.
                // This erasure is also why LowerFnDelegate's arity ceiling does not reach a suspend function type,
                // and why it must NOT be hoisted in front of this branch: a suspend type never becomes a delegate,
                // so its arity costs nothing. MEASURED, not assumed — across the whole stdlib build every suspend fn
                // that does reach the delegate path (LowerFuncTypeValued) has arity 1, the sequence/iterator receiver
                // lambda whose arity the stdlib's own signature fixes; an app's suspend lambda is replaced by its
                // state machine before this pass runs. Coverage: roundtrip's cross-module invokeWideSuspend23.
                return fn.Suspend ? ObjectType : LowerFnDelegate(fn, refBuild, force);
            case TypeNode.Nullable n:
                {
                    // #37/#48: a VALUE `T?` stays `System.Nullable<T>` (ilemit builds it — the inner is kept verbatim in the
                    // ref build, lowered to the CLR primitive otherwise); a REFERENCE `T?` is STRIPPED to the bare lowered
                    // inner in EVERY build — a CLR reference is nullable in IL regardless, and its `?` was already emitted as
                    // an NRT byte by the decl walk. NEVER produce `Nullable<referenceType>` (ilemit's MapNullable asserts the
                    // inner is a value type, in the ref build too). Decided on the SEMANTIC inner via the struct-ness oracle.
                    // Only VALUE inners reach here (so `typeArg` is moot): bir2cir's ReferenceNullableStrip (Program.cs) removes
                    // every reference-`T?` wrapper — INCLUDING nested type-args — BEFORE this pass, so a nullable collection
                    // type-arg (`Map<K, List<V>?>`) already had its `?` stripped and collapses via the bare-List path (Root-V).
                    // (This is why the #100/H3 "propagate typeArg through Nullable" idea was a no-op — the smuggle can't occur here.)
                    var lowered = LowerType(n.Of, refBuild, force, typeArg: false);
                    return IsValueNullableInner(n.Of) ? new TypeNode.Nullable(lowered) : lowered;
                }
            case TypeNode.Array a:
                return new TypeNode.Array(LowerType(a.Elem, refBuild, force, typeArg: false));
            case TypeNode.ByRef b:
                return new TypeNode.ByRef(LowerType(b.Of, refBuild, force, typeArg: false));
            case TypeNode.Oblivious ob:
                // #8 — an NRT-OBLIVIOUS `T!` (a reference-KLIB-projected `[MaybeNull]`/platform-flexible type: a value-type
                // arg OR a reference) lowers to the BARE lowered inner in EVERY build — NEVER a `Nullable<T>` wrapper. It
                // is a pure nullability ANNOTATION (NullableAttribute=0), not a container: the inner keeps THIS node's
                // position (typeArg propagated, unlike Nullable which is a value-position container). Distinct from `T?`
                // (TypeNode.Nullable): a GENUINE `Int?` stays `Nullable<int32>`, but an oblivious `Int!`
                // (`ThreadLocal<Int>.Value`, a `[MaybeNull]` value getter) becomes bare `int32` — reads `0` when unset,
                // and the `== null` branch is statically false. A reference inner (`String!`) becomes a bare NRT-oblivious
                // ref (its `?`-vs-not is a benign NullableFlags byte). ilemit has NO oblivious case, so the wrapper MUST
                // NOT survive here (kotc emits Oblivious for `@kotlin.internal.ir.FlexibleNullability` — see bir-cir-spec §1).
                return LowerType(ob.Of, refBuild, force, typeArg);
            default:
                return t;
        }
    }

    // Some representation passes run before the full-tree lowering but must compare a Kotlin declaration with an
    // exact CLR slot read from metadata. Use the one canonical lowering rule under the reference facts for that
    // comparison; do not duplicate a partial primitive/@ClrTypeAlias table in the caller.
    internal static bool SamePhysicalSlotType(TypeNode left, TypeNode right,
        IReadOnlyDictionary<string, string> aliases, Func<string, bool> isValueFqn,
        IReadOnlyDictionary<string, string> physicalTypeNames, bool returnPosition,
        IReadOnlySet<string> localTypeNames = null)
    {
        TypeNode LowerSlot(TypeNode type) => returnPosition
            && type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null }
                ? VoidType
                : CanonicalPhysicalSlotType(LowerPhysicalType(
                    type, aliases, isValueFqn, physicalTypeNames, typeArg: false, localTypeNames));
        return LowerSlot(left).Equals(LowerSlot(right));
    }

    // A representation pass that runs before the full-tree lowering may already have to author a PHYSICAL type
    // inside a CIR-only carrier. Configure the same facts Lower() will use and invoke the same recursive rule; a
    // caller must not reproduce only the primitive, alias, collection-collapse or delegate branch it happened to
    // encounter first. The statics are per-run state, so preserve them just as SamePhysicalSlotType historically did.
    internal static TypeNode LowerPhysicalType(TypeNode type,
        IReadOnlyDictionary<string, string> aliases, Func<string, bool> isValueFqn,
        IReadOnlyDictionary<string, string> physicalTypeNames, bool typeArg,
        IReadOnlySet<string> localTypeNames = null)
    {
        var savedAliases = _aliases;
        var savedIsValue = _isValueFqn;
        var savedPhysicalNames = _physicalTypeNames;
        var savedLocalNames = _localTypeNames;
        try
        {
            _aliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _isValueFqn = isValueFqn ?? (_ => false);
            _physicalTypeNames = physicalTypeNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _localTypeNames = localTypeNames ?? new HashSet<string>(StringComparer.Ordinal);
            return LowerType(type, refBuild: false, force: false, typeArg);
        }
        finally
        {
            _aliases = savedAliases;
            _isValueFqn = savedIsValue;
            _physicalTypeNames = savedPhysicalNames;
            _localTypeNames = savedLocalNames;
        }
    }

    // CIR admits both the primitive shorthand used by metadata readers (`int`, `bool`, ...) and the corresponding
    // BCL FQN produced by @ClrTypeAlias lowering. ilemit maps each pair to the same System.Type, so an earlier
    // representation pass comparing physical slots must canonicalize the pair as well. Keep this recursive because
    // the spelling can occur beneath arrays, nullable value types, byrefs, function types, and constructed generics.
    internal static TypeNode CanonicalPhysicalSlotType(TypeNode type) => type switch
    {
        TypeNode.Fqn { Args: null } f => new TypeNode.Fqn(f.Name switch
        {
            "void" => "System.Void",
            "int" => "System.Int32",
            "long" => "System.Int64",
            "short" => "System.Int16",
            "sbyte" => "System.SByte",
            "double" => "System.Double",
            "float" => "System.Single",
            "bool" => "System.Boolean",
            "char" => "System.Char",
            "string" => "System.String",
            "object" => "System.Object",
            "uint" => "System.UInt32",
            "ulong" => "System.UInt64",
            "byte" => "System.Byte",
            "ushort" => "System.UInt16",
            _ => f.Name,
        }),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(
            f.Name, args.Select(CanonicalPhysicalSlotType).ToArray()),
        TypeNode.Array a => new TypeNode.Array(
            CanonicalPhysicalSlotType(a.Elem), a.Rank, a.SzArray),
        TypeNode.Nullable n => new TypeNode.Nullable(CanonicalPhysicalSlotType(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(CanonicalPhysicalSlotType(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(CanonicalPhysicalSlotType(b.Of)),
        TypeNode.Ptr p => new TypeNode.Ptr(CanonicalPhysicalSlotType(p.Of)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req,
            CanonicalPhysicalSlotType(m.M), CanonicalPhysicalSlotType(m.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            CanonicalPhysicalSlotType(fn.Ret),
            fn.Params.Select(CanonicalPhysicalSlotType).ToArray(),
            fn.Recv == null ? null : CanonicalPhysicalSlotType(fn.Recv), fn.Clr,
            fn.Ctx?.Select(CanonicalPhysicalSlotType).ToArray()),
        _ => type,
    };

    // The widest Kotlin function arity that has a CLR delegate. `System.Func`/`Action` carry 0..16; the DotKt stdlib
    // defines `KAction`17..22` / `KFunc`18..23` for 17..22 (#220). The cap is not a property of the frontend — it
    // resolves `kotlin.FunctionN` for arbitrary N, because its builtin provider synthesizes the class on demand — it
    // is a property of the REPRESENTATION: a delegate must be a real type in a real assembly, and an unbounded family
    // cannot be pre-baked into the stdlib. Extending it means a variadic representation, not one more row.
    //
    // It bounds DELEGATES, so it bounds non-suspend function types only. A `suspend` function type is erased to an
    // object carrier before it could arrive here (LowerType's Fn case) and has NO arity limit: 23 suspend parameters
    // compile, emit and run exactly as 2 do, same-module and across a module boundary.
    internal const int CanonicalDelegateMinArity = 17;
    internal const int CanonicalDelegateMaxArity = 22;
    const int MaxBclDelegateArity = CanonicalDelegateMinArity - 1;

    // A function type kept as a DELEGATE (a `funcType` slot, or a plain fn in a type slot): lower ret (a Unit
    // ret -> void, Action vs Func) + params + receiver; the suspend flag is folded to false (the delegate shape
    // is preserved — the sequence/iterator closure path needs a real Func/Action, not an object-erased SM value).
    // The physical CLR delegate family is decided HERE and carried explicitly in CIR — INCLUDING the decision that a
    // given Kotlin function type has no CLR delegate at all, which is this layer's call to make and not ilemit's.
    // ilemit must not change the nominal ABI based on whether one of the resolved types happens to still be a
    // TypeBuilder.
    internal static TypeNode LowerFnDelegate(TypeNode.Fn fn, bool refBuild, bool force)
    {
        var ret = (fn.Ret is TypeNode.Fqn rf && rf.Args == null && rf.Name == "kotlin.Unit")
            ? VoidType : LowerType(fn.Ret, refBuild, force, typeArg: false);
        var ps = fn.Params.Select(p => LowerType(p, refBuild, force, typeArg: false)).ToArray();
        var recv = fn.Recv == null ? null : LowerType(fn.Recv, refBuild, force, typeArg: false);
        int arity = ps.Length + (recv == null ? 0 : 1);
        if (arity > CanonicalDelegateMaxArity)
            throw new InvalidOperationException(
                $"bir2cir: {_file}: a function type of {arity} parameters has no CLR delegate. System.Func/Action "
                + $"carry arities 0..{MaxBclDelegateArity} and the DotKt stdlib defines KFunc/KAction for "
                + $"{CanonicalDelegateMinArity}..{CanonicalDelegateMaxArity}; the family cannot go further because each arity "
                + "is a distinct pre-baked type in the stdlib and Kotlin's function types are unbounded. A receiver "
                + "counts toward the arity. Group the parameters into a class, or pass them as a collection.");
        bool returnsVoid = ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
        string clr = returnsVoid
            ? arity <= MaxBclDelegateArity ? "System.Action" : "DotKt.Runtime.CompilerServices.KAction"
            : arity <= MaxBclDelegateArity ? "System.Func" : "DotKt.Runtime.CompilerServices.KFunc";
        return new TypeNode.Fn(false, ret, ps, recv, clr);
    }

    /// <summary>
    /// The constructed delegate a lowered function type IS, named.
    /// </summary>
    /// <remarks>
    /// `LowerFnDelegate` leaves the node an `fn` carrying the delegate's family in `clr`, and the emitter builds
    /// the constructed type from that. A member reference has to NAME the type, so the same construction is
    /// spelled here — beside the pass that decided the family, so the two cannot drift.
    ///
    /// `Action` takes the parameters alone; `Func` takes the parameters then the return. A receiver is the
    /// leading parameter either way, exactly as the arity above counts it.
    /// </remarks>
    internal static TypeNode.Fqn DelegateFqnOf(TypeNode.Fn lowered)
    {
        if (lowered.Clr == null) return null;
        // DelegateParams is the shared property the EMITTER builds its delegate from — it prepends an extension
        // receiver so a receiver-lambda and the flat closure bound to it land on the same CLR delegate. Rebuilding
        // that list here instead is a second implementation of one decision, and the two disagreed.
        var args = new List<TypeNode>(lowered.DelegateParams);
        bool returnsVoid = lowered.Ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
        if (!returnsVoid) args.Add(lowered.Ret);
        return args.Count == 0
            ? new TypeNode.Fqn(lowered.Clr)
            : new TypeNode.Fqn(lowered.Clr, args.ToArray());
    }

    // Read a structured Type node out of the BIR JSON, lower it, and write it back.
    static JsonNode LowerTypeObject(JsonNode node, bool refBuild, bool force, bool typeArg)
    {
        var tn = TypeNode.Parse(node.ToJsonString());
        return TypeNode.Write(LowerType(tn, refBuild, force, typeArg));
    }

    // True iff a JSON value is a structured Type node (has a `t` discriminator) rather than a legacy type STRING
    // or a k-tagged sub-node.
    static bool IsTypeObject(JsonNode n) =>
        n is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s != null;

    // The source file being lowered, for the refusals this pass can raise. Set per `Lower` call alongside the other
    // per-call statics; there is no finer location to give, because a type node carries no position of its own.
    static string _file = "<unknown>";
    static IReadOnlyDictionary<string, string> _physicalTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal);
    static IReadOnlySet<string> _localTypeNames = new HashSet<string>(StringComparer.Ordinal);

    public static JsonNode Lower(JsonNode root, bool refBuild, IReadOnlyDictionary<string, string> aliases = null,
        Func<string, bool> isValueFqn = null, string file = null,
        IReadOnlyDictionary<string, string> physicalTypeNames = null,
        IReadOnlySet<string> localTypeNames = null)
    {
        _aliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _isValueFqn = isValueFqn ?? (_ => false);
        _file = string.IsNullOrEmpty(file) ? "<unknown>" : file;
        _physicalTypeNames = physicalTypeNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _localTypeNames = localTypeNames ?? new HashSet<string>(StringComparer.Ordinal);
        return LowerNode(root, refBuild, force: false);
    }

    // `force` == "this subtree carries attribute-blob metadata": lower with the FULL map, ignoring refBuild. It is
    // set when entering an attribute-class declaration (base : System.Attribute) or an `attrs` application array,
    // and propagates to the whole subtree.
    static JsonNode LowerNode(JsonNode node, bool refBuild, bool force)
    {
        if (node is JsonObject obj)
        {
            var here = force || IsAttributeClass(obj);
            // ROOT-V DEPTH: a collection-CONSTRUCTION node's element/value type key is a generic type-argument of the
            // built collection (depth >= 1), so it collapses like a `typeArgs` element — the literal `listOf(listOf(…))`
            // must build a `List<IList<..>>` so it inhabits the collapsed consumer slot (pairnest). newArray's `elem` is
            // NOT collapsed — arrays are held uncollapsed on BOTH sides (the `Array` type case + newArray here) so they
            // stay mutually consistent. (This is NOT array covariance: `IList<int>[]` is in fact NOT assignable to
            // `IReadOnlyList<int>[]` — the element interfaces are unrelated; a concrete-element store into a readonly
            // element array works only by the runtime value implementing that element interface.)
            var nodeK = (obj["k"] as JsonValue)?.GetValue<string>();
            var collCtor = nodeK is "newList" or "newSet" or "newMap";
            var copy = new JsonObject();
            foreach (var kv in obj)
            {
                // STEP-1 clrName migration: kotc emits a pure-Kotlin `overrides` marker (the override closure) so a
                // future bir2cir decl-rename pass can derive BCL slot names from the ref.dll @ClrIntrinsic. It is
                // bir2cir-internal metadata — strip it here so it never reaches the CIR/ilemit (keeps emit byte-identical).
                if (kv.Key == DeclarationRename.SourceMemberKey)
                {
                    continue;
                }
                if (kv.Key is "overrides" or "fakeOverride"
                    or KotlinPropertyAccessors.InheritedImplementationKey
                    or KotlinPropertyAccessors.InheritedDefaultAccessorsKey
                    or KotlinPropertyAccessors.InheritedDefaultMethodsKey
                    or KotlinPropertyAccessors.SuspendSourceParamsKey
                    or KotlinPropertyAccessors.SuspendSourceRetKey
                    or KotlinPropertyAccessors.SuspendTaskResultKey
                    or FBoundStarProjectionErasure.SourceMemberKey) continue;
                // #122: the frontend static-type stamp `sty` is bir2cir-internal (consumed by StaticType up through the
                // CharSequence bridge). Strip it here so it never reaches CIR/ilemit — a consumed hint, not a CIR slot.
                if (kv.Key == "sty") continue;
                if (kv.Value == null) { copy[kv.Key] = null; continue; }
                // #395: this is an opaque snapshot of the frontend declaration signature. It exists precisely because
                // the ordinary signature below may erase to the same CLR shape as another Kotlin overload, so applying
                // this transform to the snapshot would destroy the only authoritative cross-module distinction.
                if (kv.Key == DeclarationIdentityBinding.SemanticSignatureKey)
                    copy[kv.Key] = kv.Value.DeepClone();
                else if (kv.Key == "attrs")
                    copy[kv.Key] = LowerNode(kv.Value, refBuild, force: true);   // attribute application -> blob metadata
                else if (kv.Key is "sig" or "getSig" or "setSig")
                    copy[kv.Key] = LowerSigValue(kv.Value, refBuild, here);   // sig = param types
                else if (ReturnKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerReturnValued(kv.Value, refBuild, here);   // Unit-in-return -> void (uniform)
                else if (kv.Key == "funcType")
                    copy[kv.Key] = LowerFuncTypeValued(kv.Value, refBuild, here);  // delegate slot -> keep sfunc as func:
                else if (kv.Key == "ownerType" || kv.Key == "owner")
                    copy[kv.Key] = LowerOwnerValued(kv.Value, refBuild, here);   // primitive-array owner stays kotlin.IntArray
                else if (kv.Key == "typeArgs" || (collCtor && kv.Key is "elem" or "keyType" or "valType"))
                    copy[kv.Key] = LowerTypeValued(kv.Value, refBuild, here, typeArg: true);   // Root V: depth>=1 positions collapse
                else if (TypeKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerTypeValued(kv.Value, refBuild, here);
                else
                    copy[kv.Key] = LowerNode(kv.Value, refBuild, here);
            }
            // H2 — SUSPEND FUNCTION-TYPE POSITION metadata. LowerTypeString erases an `sfunc:` token (a `suspend (…)->T`
            // type) to `object` at a param/field/property `type` or a method `ret` (a suspend-lambda VALUE is a
            // Continuation-based state-machine object, not a Func delegate). That fold destroys the suspend ORIGIN and
            // its arg/return SHAPE in the CLR signature, so a re-consuming DotKt assembly can no longer tell
            // `fun run(block: suspend () -> T)` from a plain function-typed one. Record the RAW pre-erasure `sfunc:`
            // token alongside — mirroring the `nullable`/`retNullable` positional-fact model — so ilemit can stamp
            // [KotlinSuspendFunctionType(raw)] and dll2klib restore the suspend function type on re-consumption. This
            // carries the SHAPE STRING (not a bare flag): the erased CLR type is `object`, from which the arg/return
            // types are otherwise unrecoverable. Additive — ilemit reads it only on param/return/field/property builders;
            // harmless on any other node that happens to carry an sfunc-typed `type`/`ret`.
            // The PRE-erasure shape wins where one was stashed: a `suspend (…) -> T?` has had its `Nullable(Tv)`
            // object-erased by now (#86), and recording the erased shape would make this carrier faithfully restore
            // `suspend () -> object` — a consumer then cannot bind the slot at all. See NullableGenericErasure's
            // RecordSuspendFnShapes for why the fact is stashed there rather than carried by the nullable-generic
            // carrier. The stash is consumed here and dropped: it is a bir2cir hand-off and never reaches CIR.
            var h2t = StashedSuspendFn(obj, NullableGenericErasure.SuspendFnPre) ?? SuspendFnSlot(obj["type"]);
            var h2r = StashedSuspendFn(obj, NullableGenericErasure.RetSuspendFnPre) ?? SuspendFnSlot(obj["ret"]);
            if (h2t != null) copy["suspendFnType"] = h2t;
            if (h2r != null) copy["retSuspendFnType"] = h2r;
            copy.Remove(NullableGenericErasure.SuspendFnPre);
            copy.Remove(NullableGenericErasure.RetSuspendFnPre);
            // #133 case3 — KOTLIN `Nothing` RETURN metadata. LowerType erases a `kotlin.Nothing` return to `object`
            // (KotlinAllToClr / the leaf map) — Nothing has no CLR analog. That fold destroys the "this never returns
            // normally" fact, so a re-consuming DotKt assembly widens `if (c) x else fail()` to Any? instead of keeping
            // x's type. Record the pre-erasure fact alongside (the `nullable`/`retSuspendFnType` positional-fact model)
            // so RoundtripMetadata stamps a bare [KotlinNothing] on the return and dll2klib restores Nothing. A
            // `Nothing?` return already stripped its reference-`?` (ReferenceNullableStrip) to a bare `kotlin.Nothing`
            // here, its nullability carried by the [Nullable] byte — so the bare-Fqn check covers both.
            if (IsNothingRet(obj["ret"])) copy["retNothing"] = true;
            // ANNOTATION-BASE DERIVATION (annotation-base-lowering-to-bir2cir, USER 2026-07-02): kotc emits a user
            // `annotation class` as a plain class carrying `"annotation":true` (base:null) — the Kotlin fact. bir2cir
            // is the Kotlin<->CLR layer that DERIVES the CLR base: an annotation class extends System.Attribute. Set
            // the base here (the `clr:` form ilemit resolves to the referenced .NET type) and drop the Kotlin-only
            // flag so it never reaches the CIR/ilemit. The `here`/force path above already lowered its field/ctor
            // types with the full map (IsAttributeClass recognizes the flag), so the attribute is emittable.
            if (ModFlag(obj, "annotation"))
            {
                copy["base"] = TypeNode.Write(new TypeNode.Fqn("System.Attribute"));
                (copy["mods"] as JsonObject)?.Remove("annotation");   // drop the Kotlin-only flag; never reaches CIR/ilemit
            }
            // UNIT -> void DERIVATION (unit-fold-in-bir2cir, USER 2026-07-05): kotc emits the pure Kotlin `kotlin.Unit`
            // FQN identity for a "no value" position — the Unit-literal `const` type and a Unit-valued `try` expression
            // `type` — instead of naming the CLR `void` shorthand. bir2cir DERIVES `void` HERE, node-kind-scoped, so
            // ONLY these two value-slot positions fold. A Unit as a genuine VALUE elsewhere (a `var`/field/param type, a
            // generic TYPE-ARG like `Continuation[kotlin.Unit]`) stays `kotlin.Unit` — a `void` field/param/arg is
            // invalid metadata. (Return slots fold via the ReturnKeys/LowerReturnSlot path; this covers the rest.)
            if (copy["k"] is JsonValue kv2 && kv2.TryGetValue<string>(out var kind2) && (kind2 == "const" || kind2 == "try")
                && copy["type"] is JsonObject tobj && tobj["t"] is JsonValue tvt && tvt.TryGetValue<string>(out var tvts)
                && tvts == "fqn" && tobj["name"] is JsonValue tnm && tnm.TryGetValue<string>(out var tnms) && tnms == "kotlin.Unit")
                copy["type"] = TypeNode.Write(VoidType);
            return copy;
        }

        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
                copy.Add(item == null ? null : LowerNode(item, refBuild, force));
            return copy;
        }

        return node.DeepClone();
    }

    // A type declaration is an attribute class iff kotc flagged it `"annotation":true` (the pure-Kotlin "this is an
    // annotation" fact) — bir2cir DERIVES the `: System.Attribute` base from that flag (annotation-base-lowering-to-
    // bir2cir, USER 2026-07-02; kotc no longer names the CLR base). Also true once the base has already been derived
    // (an already-`System.Attribute`-based class, e.g. a .NET attribute surfaced with a real base). Its ctor params /
    // fields / property accessors must carry concrete CLR types so the attribute is emittable — hence the force path.
    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool ModFlag(JsonObject obj, string name) => obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;

    static bool IsAttributeClass(JsonObject obj) =>
        ModFlag(obj, "annotation") ||
        (obj["base"] is JsonObject b && b["t"] is JsonValue bt && bt.TryGetValue<string>(out var bts) && bts == "fqn" &&
         b["name"] is JsonValue bn && bn.TryGetValue<string>(out var s) && s != null &&
         s.EndsWith("System.Attribute", StringComparison.Ordinal));

    // If a `type`/`ret` slot holds a suspend function type (Fn{suspend:true}), return the STRUCTURED fn node for the
    // H2 suspendFnType/retSuspendFnType metadata stamping (the slot's type itself is erased to `object` by LowerType,
    // so its arg/return SHAPE would otherwise be unrecoverable). Spec §0/§1: the metadata IS the structured Type node
    // (the old `sfunc:` string folds into it) — ilemit/dll2klib consume the Fn directly, never a re-rendered string.
    // The PRE-erasure suspend function shape NullableGenericErasure stashed on this node, as the structured Fn node
    // the carrier wants, or null when nothing was stashed.
    static JsonNode StashedSuspendFn(JsonObject obj, string factKey)
        => (obj[factKey] as JsonValue)?.TryGetValue<string>(out var s) == true && s != null
            ? System.Text.Json.Nodes.JsonNode.Parse(s) : null;

    static JsonNode SuspendFnSlot(JsonNode slot)
    {
        if (slot is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s == "fn"
            && o["suspend"] is JsonValue sv && sv.TryGetValue<bool>(out var susp) && susp)
            return o.DeepClone();
        return null;
    }

    // #133 case3 — true iff a `ret` slot is the bare `kotlin.Nothing` FQN (a `fun f(): Nothing`), the pre-erasure fact
    // RoundtripMetadata reads to stamp [KotlinNothing]. A `Nothing?` return already stripped its reference-`?` to a bare
    // `kotlin.Nothing` (ReferenceNullableStrip) by this point, so the bare-Fqn check covers the nullable case too.
    static bool IsNothingRet(JsonNode slot) =>
        slot is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s == "fqn"
        && o["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && n == "kotlin.Nothing";

    // A `sig` value is a STRUCTURED array of parameter-type TypeNodes (#37 m3b) — the overload key ilemit matches
    // against a method def's lowered `params[].type`. Lower each element through the SAME structured type path the
    // def params use (LowerTypeObject), so the call-side sig and the def-side params stay in the SAME vocabulary
    // (identical SigTokenOf render), else overload resolution misses.
    static JsonNode LowerSigValue(JsonNode val, bool refBuild, bool force)
    {
        if (val is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
                copy.Add(item == null ? null : IsTypeObject(item) ? LowerTypeObject(item, refBuild, force, typeArg: false) : LowerNode(item, refBuild, force));
            return copy;
        }
        return LowerNode(val, refBuild, force);
    }

    // The `funcType` key names the DELEGATE type constructed by a newClosure/newDelegate/delegateInvoke. A suspend-fn
    // delegate (`sfunc:`) here is a genuine CLR delegate — the pre-P3 sequence/iterator closure path (`iterator {}`
    // yields a `newClosure` whose funcType is `sfunc:void:SequenceScope[..]`) — NOT an object-erased SM value slot.
    // So fold `sfunc:`->`func:` for THIS key only (delegate shape preserved), then lower normally; every OTHER type
    // slot (param/field/return/receiver) erases `sfunc:`->`object`. The APP suspend-lambda SM path never reaches
    // here: its `newSuspendLambda` is replaced by a `new <SM>` node (SuspendLambdaLowering) before type lowering.
    static JsonNode LowerFuncTypeValued(JsonNode val, bool refBuild, bool force)
    {
        if (IsTypeObject(val))
        {
            var tn = TypeNode.Parse(val.ToJsonString());
            // A suspend fn in a funcType slot is a genuine delegate here (the sequence/iterator closure path) — keep
            // the shape, folding suspend->false; a plain fn likewise. Any non-fn type lowers normally.
            return TypeNode.Write(tn is TypeNode.Fn fn ? LowerFnDelegate(fn, refBuild, force) : LowerType(tn, refBuild, force, false));
        }
        // funcType is ALWAYS a structured `fn` node (kotc emits `TypeNode.Fn`, #37 #49) — the string `func:`/`sfunc:`
        // funcType form is retired. A non-object slot can only be a rare bare-FQN synthetic; lower it structurally.
        return LowerTypeValued(val, refBuild, force);
    }

    // A return-slot value: a bare top-level `kotlin.Unit` -> `void` (UNIFORM, both modes); otherwise the normal type
    // lowering (so a return like clrg:List[kotlin.Int] still lowers its inner Int).
    static JsonNode LowerReturnValued(JsonNode val, bool refBuild, bool force)
    {
        if (IsTypeObject(val))
        {
            var tn = TypeNode.Parse(val.ToJsonString());
            // A Unit RETURN is the CLR `void` convention (uniform across ref AND substitute/app).
            if (tn is TypeNode.Fqn f && f.Args == null && f.Name == "kotlin.Unit")
                return TypeNode.Write(VoidType);
            return TypeNode.Write(LowerType(tn, refBuild, force, typeArg: false));
        }
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerReturnSlot(s, refBuild, force));
        return LowerTypeValued(val, refBuild, force);
    }

    static string LowerReturnSlot(string s, bool refBuild, bool force) =>
        s == "kotlin.Unit" ? "void" : LowerTypeString(s, refBuild, force);

    // An OWNER slot (`ownerType`/`owner`) value. A primitive-array owner (`intArray.iterator()`) stays the bare
    // `kotlin.IntArray` FQN — ilemit's ParseOwnerSlot resolves that identity to the CLR array type directly (it
    // expects a Fqn/string, NOT a decomposed `Array(elem)` node). Decomposing the owner to `Array(int)` here would
    // hand ilemit an owner node it cannot read. Every OTHER owner (a collection, a user type, a primitive) lowers
    // normally. Mirrors the pre-#73-2b-A behavior, where kotc emitted the un-lowered `kotlin.IntArray` owner.
    static JsonNode LowerOwnerValued(JsonNode val, bool refBuild, bool force)
    {
        if (IsTypeObject(val) && TypeNode.Parse(val.ToJsonString()) is TypeNode.Fqn f
            && f.Args == null && PrimArrayElem.ContainsKey(f.Name))
            return TypeNode.Write(f);   // keep kotlin.IntArray verbatim for ilemit's array-owner resolution
        return LowerTypeValued(val, refBuild, force);
    }

    // A type-bearing key's value: a scalar type string, an array of type strings (interfaces/argTypes/constraints/
    // typeArgs), or — for a few node shapes — a nested object, which is recursed structurally.
    // `typeArg` = this value sits at generic-argument DEPTH (a call/ctor `typeArgs` list element, or a collection-
    // construction element/value key on newList/newSet/newMap), so its element HEADS collapse per the Root-V rule.
    // Default false: a `type`/`ret`/`argTypes`/`sig`/`base` head is depth-0 (keeps covariant); only its NESTED Args
    // collapse via LowerType's own `typeArg:true` recursion.
    static JsonNode LowerTypeValued(JsonNode val, bool refBuild, bool force, bool typeArg = false)
    {
        if (IsTypeObject(val))
            return LowerTypeObject(val, refBuild, force, typeArg);

        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerTypeString(s, refBuild, force));

        if (val is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                if (item != null && IsTypeObject(item))
                    copy.Add(LowerTypeObject(item, refBuild, force, typeArg));
                else if (item is JsonValue iv && iv.TryGetValue<string>(out var its))
                    copy.Add(JsonValue.Create(LowerTypeString(its, refBuild, force)));
                else
                    copy.Add(item == null ? null : LowerNode(item, refBuild, force));
            }
            return copy;
        }

        return LowerNode(val, refBuild, force);
    }

    // Lower a STILL-STRING type slot (a synthetic interface name like `dotkt$CharSequence`, a StringCharSequenceBridge
    // adapter's `kotlin.String` slot): rewrite a bare @ClrTypeAlias kotlin.* type to its BARE BCL FQN (numeric/bool/char
    // + String/Any + the unsigned set + non-generic BCL) and recurse nested `[...]` args. Output carries NO legacy
    // string-token grammar (`clr:`/`clrg:`/`@`) — every type is a bare FQN / CLR shorthand ilemit resolves by name (#48);
    // a user/stdlib FQN (kotlin.collections.List) and the position-dependent kotlin.Unit value pass through unchanged.
    public static string LowerTypeString(string raw, bool refBuild, bool force = false)
    {
        // Function types are structured `fn` nodes now (#37 #49): the `func:`/`sfunc:` STRING type token is retired,
        // so this string resolver never receives one. It survives only for the bare-FQN + CLR-shorthand LEAF slots
        // that kotc/bir2cir still emit as strings (synthetic interface names like `dotkt$CharSequence`, the synthesized
        // StringCharSequenceBridge adapter's `kotlin.String` slots) — resolved by the kotlin.* map / LowerLeaf below.
        var t = raw.Trim();
        // The reference build keeps kotlin.* primitives verbatim (general path); the attribute force path lowers
        // unconditionally. A token without "kotlin." carries nothing to rewrite.
        if ((!force && refBuild) || !raw.Contains("kotlin.", StringComparison.Ordinal)) return raw;

        if (t.Length == 0) return raw;

        foreach (var p in ModifierPrefixes)
            if (t.StartsWith(p, StringComparison.Ordinal))
                return p + LowerTypeString(t[p.Length..], refBuild, force);

        var br = t.IndexOf('[');
        if (br >= 0 && t.EndsWith("]", StringComparison.Ordinal))
        {
            var head = t[..br];
            var inner = t[(br + 1)..^1];
            var args = string.Join(",", SplitTopLevel(inner).Select(a => LowerTypeString(a, refBuild, force)));
            // A @ClrTypeAlias GENERIC type used as a type constructor (supertype/interface/type-arg/field), e.g.
            // kotlin.collections.Collection[E] -> System.Collections.Generic.IReadOnlyCollection[E]. ilemit builds the
            // generic by arg count. The foundational primitives never appear as a generic head, so the primitive-alias
            // path need not gate here. No `@`-decorated head reaches this string path (#48: type-args are structured nodes).
            // `kotlin.Enum<E>` -> the NON-generic `System.Enum` (C2): a Kotlin `enum class` is emitted as a real CLR
            // `System.Enum`-backed enum (ilemit `DefineEnum`), which does NOT extend the stdlib's generic `kotlin.Enum<E>`
            // class. So a `fun <T : Enum<T>> …` self-referential bound (`kotlin.Enum[gp:T]`) must lower to `System.Enum`
            // (the CLR `where T : Enum` idiom) or a real enum type argument violates the constraint (VerificationException).
            // Drop the self-referential type arg — System.Enum is non-generic.
            if (head == "kotlin.Enum") return "System.Enum";
            if (head == "System.IComparable" && (args == "object" || args == "System.Object"))
                return "System.IComparable";
            if (!head.StartsWith("clr", StringComparison.Ordinal) && AliasBcl(head) is string genericBcl)
            {
                // `Comparable<*>` / `Comparable<Any?>` (the star / Any-projected comparable — kotc token
                // `kotlin.Comparable[object]`) -> the NON-generic `System.IComparable`, NOT `IComparable<object>` (C2).
                // `System.IComparable<in T>` is contravariant, so no VALUE type is `IComparable<object>` (a boxed Int is
                // `IComparable<int>` / non-generic `IComparable` only). The `compareBy`/`compareValuesBy` selector
                // `(T) -> Comparable<*>?` and its boxed selector value must ride the non-generic dispatch spine
                // (clrRawCompareTo's `as IComparable`); a reified `IComparable<object>` castclass fails on every primitive.
                // A CONCRETE arg (`Comparable<C>` / `Comparable<gp:T>`) keeps the generic form (`sorted`'s element cast).
                // The star/Any arg arrives as the shorthand "object" (a kotc-emitted CLR token) or, now that the
                // primitive alias path lowers a bare kotlin.Any leaf, as the bare "System.Object" (#55) — accept both.
                if (genericBcl == "System.IComparable" && (args == "object" || args == "System.Object")) return "System.IComparable";
                return genericBcl + "[" + args + "]";
            }
            return head + "[" + args + "]";
        }

        return LowerLeaf(t, force);
    }

    static string LowerLeaf(string t, bool force)
    {
        // A bare kotlin.* foundational leaf (numeric/bool/char + String/Any + the unsigned set) lowers to its BARE BCL
        // FQN via the active map; all other leaves (CLR shorthand, the position-dependent kotlin.Unit value, user/stdlib
        // FQNs like kotlin.collections.List) pass through. No legacy `clrg:`/`@` grammar is recognized/emitted (#48) —
        // type-args travel as structured `{t:fqn}` nodes, so the `@`-decorated dual-representation STRING form is gone.
        // The attribute-blob force path keeps the hardcoded KotlinAllToClr map (no ref.dll in the ref build). #55: the
        // non-force `KotlinToClr` shadow was deleted, so a bare primitive falls to AliasBcl (its ref.dll @ClrTypeAlias).
        if (force && KotlinAllToClr.TryGetValue(t, out var clr)) return clr;
        // A @ClrTypeAlias type used bare — a foundational primitive (kotlin.Int -> System.Int32) OR a non-generic
        // BCL (StringBuilder/Regex/Match/IComparable/TextWriter/...) -> the BARE <bcl> FQN (no legacy `clr:` prefix —
        // ilemit derives resolution from the name; #48), read from the ref.dll alias index.
        if (AliasBcl(t) is string bcl) return bcl;
        return t;
    }

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();

        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0)
            {
                result.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(value[start..].Trim());
        return result;
    }
}
