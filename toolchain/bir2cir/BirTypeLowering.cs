using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Shared scalar CLR representation policy. The owning graph projection in the
// Source partial applies this same engine through generated physical slot roles.
//
// Mode gate (a property of the build, selected by the `--build-stdlib` CLI flag):
//   refBuild = StdlibMode == Metadata (`--build-stdlib=metadata`)  -> the pure-Kotlin REFERENCE surface.
// In the REFERENCE build a kotlin.* primitive token is kept VERBATIM (pure-Kotlin metadata; the bare FQN
// "kotlin.Int" stays "kotlin.Int"); the rewrite is a pure passthrough. In EVERY other build (the runtime stdlib,
// and all app builds) a bare kotlin.* primitive lowers to its CLR token (kotlin.Int -> int, ...).
//
// Signatures, executable operands, owners, delegate slots and forced attributes
// have distinct entry roles; no serialized graph or ambient last-file context
// participates in the scalar recursion.
static partial class BirTypeLowering
{
    // A synthesized declaration/MethodImpl may fill a value-returning generic CLR slot even when its closed
    // Kotlin type is Unit. This representation fact is consumed here, never serialized into final CIR.
    internal const string ValueReturnKey = "dotktValueReturn";
    // Kotlin's generic Enum<E> admits both CLR enums and singleton classes, so its value view is System.Object. Passes that
    // synthesize ABI for physically reified generic owners must consult this rule instead of treating the BIR arity
    // as proof that a CLR generic TypeDef exists.
    internal static bool ErasesGenericApplicationToNonGenericClassifier(string fqn) => fqn == "kotlin.Enum";

    // Preserve the semantic head until PhysicalHead can select a collapsed classifier. This includes applications
    // that always erase their arguments, and aliases whose collapse depends on the final lowered arguments.
    internal static bool AliasHeadRequiresSourceIdentity(string source, string bcl) =>
        ErasesGenericApplicationToNonGenericClassifier(source) || bcl is "System.IComparable" or "System.Object";

    // A pre-lowering projection pass must ask the same head-selection rule as the final type lowering. Remove only
    // the existential shell that FBoundStarProjectionErasure itself will erase, lower the resulting arguments, and
    // delegate the classifier decision to PhysicalHead. Argument aliases participate in that same decision;
    // do not use the full-tree lowering's ambient state before that lowering has even run.
    internal static bool ProjectedAliasHasReifiedGenericHead(
        string kotlinFqn, string bcl, IReadOnlyList<TypeNode> arguments,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<string, string> localAliases = null)
    {
        if (localAliases != null)
        {
            var combined = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
            foreach (var (name, target) in localAliases) combined[name] = target;
            aliases = combined;
        }
        TypeNode Bound(TypeNode type) => type switch
        {
            TypeNode.Star => new TypeNode.Fqn("kotlin.Any"),
            TypeNode.Projection projection => Bound(projection.Of),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Bound(oblivious.Of)),
            TypeNode.Nullable nullable => new TypeNode.Nullable(Bound(nullable.Of)),
            _ => type,
        };
        var lowered = arguments?.Select(argument =>
            LowerPhysicalType(Bound(argument), aliases, null, null, typeArg: true)).ToArray();
        return PhysicalHead(kotlinFqn, bcl, lowered)
            is TypeNode.Fqn { Args: not null };
    }

    // The `Span<T>` identity pair, in ONE place: kotc emits the faithful `kotlin.clr.Span` intrinsic name and this
    // pass owns the BCL substitution below. Passes that run BEFORE the lowering and must reason about the CLR type
    // (ReferenceMetadataIndex.IsByRefLikeFqn — `System.Span<T>` is a `ref struct`) canonicalize through these two
    // constants rather than restating either spelling.
    public const string SpanIntrinsicFqn = "kotlin.clr.Span";
    public const string SpanClrFqn = "System.Span";
    public const string PointerIntrinsicFqn = "kotlin.clr.ClrPointer";

    // Validate the semantic carrier at the last all-Kotlin boundary, before star/F-bound passes can replace a
    // `ClrPointer<*>` pointee with object and erase the evidence needed for a precise refusal. A pointer itself is not
    // a legal CLR generic argument, while an array OF pointers is a managed array type and may occupy one.
    static void PointerValidateType(TypeNode root, string file)
    {
        void Refuse(string message) => throw new InvalidDataException($"bir2cir: {file}: {message}");

        void ValidateType(TypeNode type, bool directGenericArgument)
        {
            switch (type)
            {
                case TypeNode.Fqn { Name: FunctionPointerVocabulary.Pointer } pointer:
                    if (directGenericArgument)
                        Refuse("A CLR function pointer cannot occupy a CLR generic argument");
                    var shape = FunctionPointerShape(pointer);
                    ValidateType(shape.Ret, directGenericArgument: false);
                    foreach (var parameter in shape.Params) ValidateType(parameter, directGenericArgument: false);
                    return;
                case TypeNode.Fqn { Name: FunctionPointerVocabulary.Required or FunctionPointerVocabulary.Optional, Args: [_, var modified] }:
                    ValidateType(modified, directGenericArgument);
                    return;
                case TypeNode.Fqn { Name: FunctionPointerVocabulary.Value, Args: [var value] }:
                    ValidateType(value, directGenericArgument);
                    return;
                case TypeNode.Fqn { Name: var name, Args: [var element] } when FunctionPointerVocabulary.ArrayRanks.ContainsKey(name):
                    ValidateType(element, directGenericArgument: false);
                    return;
                case TypeNode.Fqn { Name: PointerIntrinsicFqn } pointer:
                    if (pointer.Args is not { Length: 1 })
                        Refuse($"{PointerIntrinsicFqn} requires exactly one pointee type");
                    if (directGenericArgument)
                        Refuse($"{PointerIntrinsicFqn} cannot occupy a CLR generic argument; ECMA unmanaged "
                            + "pointers are signature types, not valid generic arguments");
                    if (pointer.Args[0] is TypeNode.Star)
                        Refuse($"{PointerIntrinsicFqn}<*> has no exact CLR pointee signature");
                    ValidateType(pointer.Args[0], directGenericArgument: false);
                    return;
                case TypeNode.Fqn { Args: { } args }:
                    foreach (var argument in args) ValidateType(argument, directGenericArgument: true);
                    return;
                case TypeNode.Fn fn:
                    ValidateType(fn.Ret, directGenericArgument: true);
                    foreach (var parameter in fn.Params) ValidateType(parameter, directGenericArgument: true);
                    if (fn.Recv != null) ValidateType(fn.Recv, directGenericArgument: true);
                    if (fn.Ctx != null)
                        foreach (var context in fn.Ctx) ValidateType(context, directGenericArgument: true);
                    return;
                case TypeNode.Array array:
                    ValidateType(array.Elem, directGenericArgument: false);
                    return;
                case TypeNode.ByRef byRef:
                    ValidateType(byRef.Of, directGenericArgument: false);
                    return;
                case TypeNode.Nullable nullable:
                    ValidateType(nullable.Of, directGenericArgument);
                    return;
                case TypeNode.Oblivious oblivious:
                    ValidateType(oblivious.Of, directGenericArgument);
                    return;
                case TypeNode.Projection projection:
                    ValidateType(projection.Of, directGenericArgument);
                    return;
            }
        }

        ValidateType(root, directGenericArgument: false);
    }


    // The bare kotlin.* tokens and their CLR-codegen lowering. Consulted only in the non-reference
    // (substitute/app) build; the reference build keeps every kotlin.* token verbatim.
    //
    // kotc emits ONLY the type's FQN identity (kotlin.String / kotlin.Any / kotlin.UInt / ...), never a CLR
    // resolution marker — so EVERY @Clr-bound foundational type lowers HERE, uniformly, exactly like the signed/
    // bool/char primitives: kotlin.String -> string, kotlin.Any -> object, and the unsigned set (note
    // kotlin.UByte is an UNSIGNED byte = System.Byte, token "byte", NOT the signed "sbyte"). The whole set is
    // mode-gated by refBuild: the reference surface keeps kotlin.* verbatim, every other
    // build lowers. kotlin.Unit is the ONE token NOT here: it is position-dependent (return -> void via the
    // Return slot; a Unit VALUE keeps the emitted Unit type — you cannot have a `void` field), handled
    // separately. KotlinAllToClr (the attribute-blob force map) additionally carries kotlin.Unit -> void and is
    // applied UNCONDITIONALLY because an attribute blob needs a concrete System.* type even in the ref build.
    // #55: the non-force `KotlinToClr` map was DELETED. It was pure redundancy — every entry (kotlin.Int -> "int",
    // kotlin.String -> "string", …) merely SHADOWED the primitive's own `@ClrTypeAlias("System.Int32"/"System.String"/…)`,
    // which bir2cir already scans from the ref.dll into the authoritative alias index. A primitive lowers to its BCL alias
    // (System.Int32/System.SByte/…) via `AliasBcl` in LowerType, exactly like every other @ClrTypeAlias type;
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


    static string AliasBcl(string fqn, PhysicalTypeContext context) =>
        context.Aliases.TryGetValue(fqn, out var bcl) ? bcl : null;

    internal static bool UsesReadOnlyCollectionFace(string name) =>
        name is "kotlin.collections.List" or "kotlin.collections.Collection" or "kotlin.collections.Set";

    // KProperty's generic parameters are not collection-storage slots: each one is substituted directly into a CLR
    // interface method parameter/return (`KProperty1<T,V>.get(T):V`, `KMutableProperty1.set(T,V)`, etc.). Lowering a
    // concrete `List<X>` argument through Root-V here would produce `KProperty1<IList<X>,V>` while the implementing
    // method's head-position parameter lowers to `IReadOnlyList<X>`, making the generated reference type unloadable.
    // Treat these carrier arguments as method-slot heads (typeArg:false); recursion inside the argument still applies
    // Root-V normally, so `KProperty0<List<List<X>>>.get()` and its return use the same slot projection at every depth.
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
    /// `Comparable<Any?>` collapse, the plain alias — and the only way to
    /// guarantee they do is for there to be one branch each. A serializer that reproduced "the same rule"
    /// reproduced two of the four, and named members that exist in neither twin.
    ///
    /// `bcl` is the type's @ClrTypeAlias target, or null when it has none; `loweredArgs` is null for a leaf.
    /// </remarks>
    internal static TypeNode PhysicalHead(string kotlinFqn, string bcl, TypeNode[] loweredArgs,
        IReadOnlyDictionary<string, NullableRepresentationFrame> nullableFrames = null,
        IReadOnlyDictionary<string, string> physicalNames = null, IReadOnlySet<string> localTypes = null)
    {
        // An object-valued binding has no CLR generic TypeDef. Its source arguments remain metadata facts,
        // not physical arguments of System.Object (including the native/singleton enum union).
        if ((ErasesGenericApplicationToNonGenericClassifier(kotlinFqn) || bcl == "System.Object") && loweredArgs != null)
            return new TypeNode.Fqn("System.Object");
        // A leaf: a @ClrTypeAlias type — a foundational primitive (kotlin.Int -> System.Int32) or a non-primitive
        // BCL (StringBuilder/Regex/IComparable/…) -> the BCL FQN. Otherwise the name stands: user / stdlib /
        // in-assembly names are unchanged, trusted external DotKt identities become their physical metadata names.
        if (loweredArgs == null) return new TypeNode.Fqn(bcl ?? PhysicalName(kotlinFqn, physicalNames, localTypes));
        if (bcl == null) return new TypeNode.Fqn(PhysicalName(kotlinFqn, physicalNames, localTypes), loweredArgs);
        // Companions belong to the Kotlin declaration/implementation frame, not the aliased CLR TypeDef.
        // The explicit correspondence also handles enclosing companions interleaved with ordinary parameters.
        if (nullableFrames != null && nullableFrames.TryGetValue(kotlinFqn, out var frame))
        {
            if (loweredArgs.Length != frame.PhysicalArity)
                throw new InvalidOperationException($"Alias '{kotlinFqn}' application has {loweredArgs.Length} physical arguments; its declaration frame requires {frame.PhysicalArity}");
            loweredArgs = frame.OrdinaryArguments(loweredArgs);
        }
        // `Comparable<*>` / `Comparable<Any?>` -> the NON-generic `System.IComparable` (contravariant; no value
        // type is IComparable<object>). A concrete arg keeps the generic form.
        if (bcl == "System.IComparable" && loweredArgs.Length == 1
            && ComparableApplicationCollapses(loweredArgs[0]))
            return new TypeNode.Fqn("System.IComparable");
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

    // Select the surviving arguments without lowering their annotation wrappers. The NRT byte walk must use
    // the same physical head/frame as the signature, but still read nullability from the semantic arguments.
    internal static TypeNode[] AnnotationArguments(TypeNode.Fqn type,
        IReadOnlyDictionary<string, string> aliases, ValueTypeOracle isValue,
        IReadOnlyDictionary<string, string> physicalNames = null, IReadOnlySet<string> localTypes = null,
        bool refBuild = false, IReadOnlyDictionary<string, NullableRepresentationFrame> nullableFrames = null)
    {
        if (LowerPhysicalType(type, aliases, isValue, physicalNames, typeArg: false,
                localTypes, refBuild, nullableFrames) is not TypeNode.Fqn { Args: not null })
            return null;
        if (!refBuild && aliases.ContainsKey(type.Name)
            && nullableFrames != null && nullableFrames.TryGetValue(type.Name, out var frame))
            return frame.OrdinaryArguments(type.Args);
        return type.Args;
    }

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
    static bool IsValueNullableInner(TypeNode of, PhysicalTypeContext context) => of switch
    {
        TypeNode.Fqn f => context.IsValue(f),
        _ => false,
    };

    // === STRUCTURED TypeNode lowering (#37 m1) =================================================
    // Every kotc type field is a structured `{t:…}` Type node (DotKt.Bir.TypeNode) and flows through
    // LowerType(TypeNode). Output vocabulary is likewise structured:
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

    static string PhysicalName(string semanticName, IReadOnlyDictionary<string, string> physicalNames,
        IReadOnlySet<string> localTypes) => localTypes?.Contains(semanticName) == true ? semanticName
        : physicalNames?.TryGetValue(semanticName, out var physical) == true ? physical : semanticName;

    // Generic arguments and array elements are reified storage positions. They must use the same projection:
    // closing Array<T> with List<String> must name the same array as a concrete Array<List<String>> declaration.
    // Byref and function parameter/return slots remain method-slot positions.
    static TypeNode LowerType(TypeNode t, PhysicalTypeContext context, bool force, bool typeArg)
    {
        switch (t)
        {
            case TypeNode.Fqn f:
                {
                    if (TryLowerNativeSignature(f, context, force, typeArg, out var native)) return native;
                    // A SIGNED primitive array (`kotlin.IntArray`) -> `Array(elem)` in EVERY build (ref included): this is
                    // the array REPRESENTATION, not a primitive substitution, so it fires before the context.RefBuild passthrough.
                    // The element then lowers on the recursive call (kotlin.Int -> System.Int32 in app/rt, verbatim in ref).
                    if (f.Args == null && PrimArrayElem.TryGetValue(f.Name, out var arrElemFq))
                        return new TypeNode.Array(LowerType(new TypeNode.Fqn(arrElemFq), context, force, typeArg: false));
                    // dll2klib represents an ECMA unmanaged pointer as the compile-time Kotlin vocabulary
                    // `ClrPointer<T>`. It is not a nominal CLR class: materialize the physical pointer here, at the
                    // Kotlin-to-CLR representation boundary. A pointee is a value position, not a generic storage
                    // argument, and `void*` must use the CLR void token rather than Kotlin's Unit value type.
                    if (f.Name == PointerIntrinsicFqn)
                    {
                        if (f.Args is not { Length: 1 })
                            throw new InvalidDataException($"{PointerIntrinsicFqn} requires exactly one pointee type");
                        if (typeArg)
                            throw new InvalidDataException(
                                $"bir2cir: {context.File}: {PointerIntrinsicFqn} cannot occupy a CLR generic argument; "
                                + "ECMA unmanaged pointers are signature types, not valid generic arguments");
                        if (f.Args[0] is TypeNode.Star)
                            throw new InvalidDataException(
                                $"bir2cir: {context.File}: {PointerIntrinsicFqn}<*> has no exact CLR pointee signature");
                        var pointee = LowerNativeSlot(f.Args[0], context, force, voidPosition: true);
                        return new TypeNode.Ptr(pointee);
                    }
                    // `kotlin.clr.Span<T>` -> the real `System.Span<T>` in EVERY build (ref included). A synthetic interop
                    // marker with NO ref.dll @ClrTypeAlias definition; kotc emits the FAITHFUL `kotlin.clr.Span` identity
                    // and bir2cir OWNS the BCL substitution (M11 — the last naked `System.*` name left in kotc). Placed
                    // before the context.RefBuild passthrough so the ref build substitutes it too, matching the former kotc birType
                    // (which emitted `System.Span` unconditionally); the element lowers like any generic type-arg.
                    if (f.Name == SpanIntrinsicFqn && f.Args != null)
                        return new TypeNode.Fqn(SpanClrFqn, f.Args.Select(a => LowerType(a, context, force, typeArg: true)).ToArray());
                    // The reference build keeps Kotlin type semantics verbatim, but CIR must still name an external
                    // DotKt TypeDef by its exact CLR metadata identity. This matters for nested types under generic owners:
                    // `Outer.Nested` physically lives at `Outer`1+Nested` even when Nested itself declares no arguments.
                    if (!force && context.RefBuild)
                        return new TypeNode.Fqn(PhysicalName(f.Name, context.PhysicalNames, context.LocalNames),
                            f.Args?.Select(a => LowerType(a, context, force: false, typeArg: true)).ToArray());
                    // PhysicalHead selects the common value view for Kotlin enum bases after lowering arguments.
                    var methodSlotCarrier = f.Args != null && InterfaceMethodSlotCarriers.Contains(f.Name);
                    var loweredArgs = f.Args?.Select(a => LowerType(a, context, force,
                        typeArg: methodSlotCarrier ? false : true)).ToArray();
                    if (loweredArgs == null)
                    {
                        // A leaf: a foundational primitive (numeric/bool/char + String/Any/Nothing + the unsigned set)
                        // lowers to the CLR type in EVERY position — a type-arg primitive reifies as the CLR value type
                        // (`List<Int>` -> IReadOnlyList<System.Int32>), the CLR-idiomatic form (the boxed `kotlin.*` isn't an
                        // emitted type in the substitute/app build; the ref build keeps kotlin.* via the context.RefBuild passthrough).
                        // #55: the non-force path reads the primitive's `@ClrTypeAlias("System.Int32")` straight from the
                        // ref.dll index (AliasBcl below) — the hardcoded KotlinToClr shadow was DELETED. The force/attribute-
                        // blob path keeps KotlinAllToClr: a custom-attribute blob needs a concrete System.* even in the ref
                        // build, which has no ref.dll to read.
                        if (force && KotlinAllToClr.TryGetValue(f.Name, out var clr)) return new TypeNode.Fqn(clr);
                    }
                    return PhysicalHead(f.Name, AliasBcl(f.Name, context), loweredArgs,
                        context.NullableFrames, context.PhysicalNames, context.LocalNames);
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
                return fn.Suspend ? ObjectType : LowerFnDelegate(fn, context, force);
            case TypeNode.Nullable n:
                {
                    // #37/#48: a VALUE `T?` stays `System.Nullable<T>` (ilemit builds it — the inner is kept verbatim in the
                    // ref build, lowered to the CLR primitive otherwise); a REFERENCE `T?` is STRIPPED to the bare lowered
                    // inner in EVERY build — a CLR reference is nullable in IL regardless, and its `?` was already emitted as
                    // an NRT byte by the decl walk. NEVER produce `Nullable<referenceType>` (ilemit's MapNullable asserts the
                    // inner is a value type, in the ref build too). Decided on the SEMANTIC inner via the struct-ness oracle.
                    // Except Unit's return-contract distinction, only VALUE inners reach here: ReferenceNullableStrip
                    // removes reference-`T?` wrappers — INCLUDING nested type-args — BEFORE this pass, so a nullable collection
                    // type-arg (`Map<K, List<V>?>`) already had its `?` stripped and collapses via the bare-List path (Root-V).
                    // (This is why the #100/H3 "propagate typeArg through Nullable" idea was a no-op — the smuggle can't occur here.)
                    var lowered = LowerType(n.Of, context, force, typeArg: false);
                    return IsValueNullableInner(n.Of, context) ? new TypeNode.Nullable(lowered) : lowered;
                }
            case TypeNode.Array a:
                return new TypeNode.Array(LowerType(a.Elem, context, force, typeArg: false), a.Rank, a.SzArray);
            case TypeNode.ByRef b:
                return new TypeNode.ByRef(LowerType(b.Of, context, force, typeArg: false));
            case TypeNode.Mod modifier:
                return new TypeNode.Mod(modifier.Req,
                    LowerType(modifier.M, context, force, typeArg: false),
                    LowerType(modifier.Of, context, force, typeArg));
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
                return LowerType(ob.Of, context, force, typeArg);
            default:
                return t;
        }
    }

    // Some representation passes run before the full-tree lowering but must compare a Kotlin declaration with an
    // exact CLR slot read from metadata. Use the one canonical lowering rule under the reference facts for that
    // comparison; do not duplicate a partial primitive/@ClrTypeAlias table in the caller.
    // A referenced value-returning slot's physical kotlin.Unit is a class, not Kotlin's Unit-as-void return.
    internal static bool SamePhysicalSlotType(TypeNode left, TypeNode right,
        IReadOnlyDictionary<string, string> aliases, ValueTypeOracle isValueFqn,
        IReadOnlyDictionary<string, string> physicalTypeNames, bool returnPosition,
        IReadOnlySet<string> localTypeNames = null,
        IReadOnlyDictionary<string, NullableRepresentationFrame> nullableFrames = null,
        bool leftReturnsValue = false)
    {
        TypeNode LowerSlot(TypeNode type, bool returnsValue = false) => returnPosition && !returnsValue
            && type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null }
                ? VoidType
                : CanonicalPhysicalSlotType(LowerPhysicalType(
                    type, aliases, isValueFqn, physicalTypeNames, typeArg: false, localTypeNames,
                    nullableFrames: nullableFrames));
        return LowerSlot(left, leftReturnsValue).Equals(LowerSlot(right));
    }

    internal static void SelfTestSlotReturns()
    {
        var aliases = new Dictionary<string, string>();
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var nullableUnit = new TypeNode.Nullable(unit);
        bool Equal(TypeNode left, TypeNode right, bool value = false) => SamePhysicalSlotType(
            left, right, aliases, _ => false, null, returnPosition: true, leftReturnsValue: value);
        if (!Equal(unit, VoidType) || Equal(unit, nullableUnit)
            || !Equal(unit, nullableUnit, value: true) || Equal(unit, unit, value: true)
            || Equal(unit, VoidType, value: true))
            throw new InvalidOperationException("Slot comparison lost the distinction between Unit values and void returns");
        Console.WriteLine("[slot returns] self-test OK (source Unit, nullable Unit, physical Unit value)");
        var accessor = new BirPropertyDeclaration {
            GetRet = new TypeNode.Fqn("kotlin.Int"), SetRet = unit,
        };
        Apply(new BirFileDeclaration { Properties = new[] { accessor } }, new PhysicalTypeContext(false,
            aliases: new Dictionary<string, string> { ["kotlin.Int"] = "System.Int32" }));
        if (accessor.GetRet.Value != new TypeNode.Fqn("System.Int32") || accessor.SetRet.Value != VoidType)
            throw new InvalidOperationException("Accessor return descriptors did not follow CLR return lowering");
        var owner = new TypeNode.Fqn("Box", new TypeNode[]
        {
            new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>()),
        });
        var selected = new BirMemberReference { DeclaringType = owner, Name = "selected" };
        var call = new BirNativeInstance { OwnerType = owner, MemberRef = selected };
        Apply(new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } },
        } } }, new PhysicalTypeContext(false));
        if (call.OwnerType.Value != selected.DeclaringType.Value
            || selected.DeclaringType.Value is not TypeNode.Fqn
                { Args: [TypeNode.Fn { Clr: "System.Action" }] }
            || selected.Name.Value != "selected" || !ReferenceEquals(call.MemberRef.Value, selected))
            throw new InvalidOperationException("Selected member owner lost physical function-family lowering");
    }

    internal static void SelfTestMethodImplMetadata()
    {
        var suspendFn = new TypeNode.Fn(true, new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>());
        var nothing = new TypeNode.Fqn("kotlin.Nothing");
        foreach (var carrier in new[] { "clrInterfaceImpls", "clrBaseImpls" })
        foreach (var ret in new TypeNode[] { suspendFn, nothing })
        foreach (var stashed in new[] { false, true })
        {
            var descriptor = new BirMethodImpl {
                Owner = new TypeNode.Fqn("Slot"), Member = "read", Arity = 0,
                Params = Array.Empty<TypeNode>(), Ret = ret,
            };
            var method = new BirFunctionDeclaration {
                Name = "read", Params = Array.Empty<BirParameter>(), Ret = ret,
            };
            if (carrier == "clrInterfaceImpls") method.ClrInterfaceImpls = new[] { descriptor };
            else method.ClrBaseImpls = new[] { descriptor };
            if (stashed && ReferenceEquals(ret, suspendFn))
                method.RetSuspendFnTypePre = suspendFn;
            Apply(new BirFileDeclaration { Methods = new[] { method } }, new PhysicalTypeContext(false,
                aliases: new Dictionary<string, string> { ["kotlin.Nothing"] = "object" }));
            var slot = descriptor.ToDom().AsObject();
            var hasFact = ReferenceEquals(ret, suspendFn)
                ? method.RetSuspendFnType.HasValue && TypeNodeDataEquality.Equal(method.RetSuspendFnType.Value, suspendFn)
                : method.RetNothing.OrDefault();
            if (!hasFact || method.RetSuspendFnTypePre.IsPresent || slot.Count != 5
                || descriptor.Owner.Value != new TypeNode.Fqn("Slot") || descriptor.Member.Value != "read"
                || descriptor.Arity.Value != 0 || descriptor.Params.Value.Length != 0
                || descriptor.Ret.Value != new TypeNode.Fqn("object")
                || slot.ContainsKey("retSuspendFnType") || slot.ContainsKey("retNothing"))
                throw new InvalidOperationException("MethodImpl must retain only physical slot facts while declarations retain Kotlin return metadata");
        }
        Console.WriteLine("[MethodImpl metadata] self-test OK (interface/base slots, suspend/Nothing declaration facts)");
    }

    // Explicit-fact consumers and the structural owner share one recursive engine.
    // No invocation installs a last-file context for another caller to inherit.
    internal static TypeNode LowerPhysicalType(TypeNode type,
        IReadOnlyDictionary<string, string> aliases, ValueTypeOracle isValueFqn,
        IReadOnlyDictionary<string, string> physicalTypeNames, bool typeArg,
        IReadOnlySet<string> localTypeNames = null, bool refBuild = false,
        IReadOnlyDictionary<string, NullableRepresentationFrame> nullableFrames = null)
        => LowerType(type, new PhysicalTypeContext(refBuild, aliases, isValueFqn,
            physicalTypeNames, localTypeNames, nullableFrames), force: false, typeArg);

    // CIR admits both the primitive shorthand used by metadata readers (`int`, `bool`, ...) and the corresponding
    // BCL FQN produced by @ClrTypeAlias lowering. ilemit maps each pair to the same System.Type, so an earlier
    // representation pass comparing physical slots must canonicalize the pair as well. Keep this recursive because
    // the spelling can occur beneath arrays, nullable value types, byrefs, function types, and constructed generics.
    // Shared expression inference uses Kotlin names for computed primitive results.
    // Reconcile those results with already-physical declaration slots without re-lowering aliases.
    // Unit here denotes a value, not the attribute-blob map's void marker. In particular,
    // a null constant with Unit's reference type must remain null rather than become a void expression.
    internal static TypeNode CanonicalExpressionResult(TypeNode type) =>
        type is TypeNode.Fqn { Args: null } f && f.Name != "kotlin.Unit"
            && KotlinAllToClr.TryGetValue(f.Name, out var primitive)
            ? CanonicalPhysicalSlotType(new TypeNode.Fqn(primitive)) : CanonicalPhysicalSlotType(type);

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
        TypeNode.Fqn { Name: "System.Nullable" or "System.Nullable`1", Args: [var value] } =>
            new TypeNode.Nullable(CanonicalPhysicalSlotType(value)),
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
        TypeNode.FnPtr pointer => pointer with {
            Ret = CanonicalPhysicalSlotType(pointer.Ret),
            Params = pointer.Params.Select(CanonicalPhysicalSlotType).ToArray(),
        },
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
    internal static TypeNode LowerFnDelegate(TypeNode.Fn fn, PhysicalTypeContext context, bool force)
    {
        // A function with an explicit CLR family already carries a physical return contract. In particular,
        // Func<..., Unit> must not become Action when a later member-binding step visits it again.
        var ret = LowerType(DelegateReturnSlot(fn), context, force, typeArg: false);
        var ps = fn.Params.Select(p => LowerType(p, context, force, typeArg: false)).ToArray();
        var recv = fn.Recv == null ? null : LowerType(fn.Recv, context, force, typeArg: false);
        return PhysicalDelegate(ret, ps, recv, fn.Clr, context.File);
    }

    // These slots already describe a CLR method: a Unit value return is not Kotlin's void convention.
    internal static TypeNode.Fn PhysicalDelegate(TypeNode ret, TypeNode[] ps,
        TypeNode recv = null, string family = null, string file = null)
    {
        int arity = ps.Length + (recv == null ? 0 : 1);
        if (arity > CanonicalDelegateMaxArity)
            throw new InvalidOperationException(
                $"bir2cir: {file ?? "<unknown>"}: a function type of {arity} parameters has no CLR delegate. System.Func/Action "
                + $"carry arities 0..{MaxBclDelegateArity} and the DotKt stdlib defines KFunc/KAction for "
                + $"{CanonicalDelegateMinArity}..{CanonicalDelegateMaxArity}; the family cannot go further because each arity "
                + "is a distinct pre-baked type in the stdlib and Kotlin's function types are unbounded. A receiver "
                + "counts toward the arity. Group the parameters into a class, or pass them as a collection.");
        bool returnsVoid = ret is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
        string clr = family ?? (returnsVoid
            ? arity <= MaxBclDelegateArity ? "System.Action" : "DotKt.Runtime.CompilerServices.KAction"
            : arity <= MaxBclDelegateArity ? "System.Func" : "DotKt.Runtime.CompilerServices.KFunc");
        return new TypeNode.Fn(false, ret, ps, recv, clr);
    }

    internal static TypeNode DelegateReturnSlot(TypeNode.Fn fn) =>
        fn.Clr == null && fn.Ret is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" }
            ? VoidType : fn.Ret;

    // Preserve annotation wrappers when called before lowering. Both the physical delegate construction and
    // the CLR-facing NRT walk use this order: receiver, parameters, and a value-bearing return (if any).
    internal static TypeNode[] DelegateTypeArguments(TypeNode.Fn fn)
    {
        var args = new List<TypeNode>(fn.DelegateParams);
        var ret = DelegateReturnSlot(fn);
        if (ret is not TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }) args.Add(ret);
        return args.ToArray();
    }

    /// <summary>The constructed delegate a lowered function type is, named.</summary>
    /// <remarks>The function's explicit CLR family and shared argument order also drive its emitted signature.</remarks>
    internal static TypeNode.Fqn DelegateFqnOf(TypeNode.Fn lowered)
    {
        if (lowered.Clr == null) return null;
        // DelegateParams is the shared property the EMITTER builds its delegate from — it prepends an extension
        // receiver so a receiver-lambda and the flat closure bound to it land on the same CLR delegate. Rebuilding
        // that list here instead is a second implementation of one decision, and the two disagreed.
        var args = DelegateTypeArguments(lowered);
        return args.Length == 0
            ? new TypeNode.Fqn(lowered.Clr)
            : new TypeNode.Fqn(lowered.Clr, args);
    }

    // A type declaration is an attribute class iff kotc flagged it `"annotation":true` (the pure-Kotlin "this is an
    // annotation" fact) — bir2cir DERIVES the `: System.Attribute` base from that flag (annotation-base-lowering-to-
    // bir2cir, USER 2026-07-02; kotc no longer names the CLR base). Also true once the base has already been derived
    // (an already-`System.Attribute`-based class, e.g. a .NET attribute surfaced with a real base). Its ctor params /
    // fields / property accessors must carry concrete CLR types so the attribute is emittable — hence the force path.
    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool ModFlag(JsonObject obj, string name) => obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;

    internal static bool IsAttributeClass(JsonObject obj) =>
        ModFlag(obj, "annotation") ||
        (obj["base"] is JsonObject b && b["t"] is JsonValue bt && bt.TryGetValue<string>(out var bts) && bts == "fqn" &&
         b["name"] is JsonValue bn && bn.TryGetValue<string>(out var s) && s != null &&
         s.EndsWith("System.Attribute", StringComparison.Ordinal));

}
