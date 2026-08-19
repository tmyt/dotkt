using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class MemberCallSubstitution
{
    // Top-level fun names DEFINED in the current compilation (this assembly's file-class statics). A `callStatic
    // owner=null` to one of these stays owner-less (ilemit's FindStatic finds the local sibling) — only a name NOT
    // defined here is a candidate for referenced-stdlib owner attribution. Single-threaded per run, so static is fine.
    static IReadOnlySet<string> _localTopLevelFns = new HashSet<string>(StringComparer.Ordinal);
    // Whether to attribute referenced top-level stdlib funs to their file-class owner (APP build only; OFF for the
    // stdlib self-build, where every such fun is local — see the StdlibMode == App gate at the call site in the Driver).
    static bool _attributeTopLevelOwner;
    // FQNs of local types that declare their OWN concrete (non-abstract, nullary) `iterator()` — e.g. the concrete
    // `kotlin.collections.LinkedHashSet`. A `this.iterator()` on such a type binds to that real slot and must NOT be
    // rerouted to the ClrIteratorBridge (which returns the base `Iterator`, not the declared `MutableIterator`). The
    // reroute is ONLY for the AbstractMutable* bases whose abstract iterator() slot vanished onto the BCL IEnumerable face.
    static HashSet<string> _typesWithConcreteIterator = new(StringComparer.Ordinal);
    internal readonly record struct LocalPropertyAccessorKey(
        string Owner, string Property, string Kind, int MethodArity, int ParameterCount);

    internal sealed record LocalPropertyAccessor(string PhysicalName, TypeNode[] Parameters);

    // Module-wide local property declarations, including their complete Kotlin accessor signature. A call on a local
    // Kotlin override must be recognized before an external ancestor PropertyInfo is considered (notably a Kotlin
    // newslot overriding a non-virtual CLR property), but its physical name is consumed only after the declaration
    // rename passes have actually run.
    static IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> _localPropertyAccessors
        = new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>();
    static IReadOnlySet<string> _localPropertyOwners = new HashSet<string>(StringComparer.Ordinal);
    // Exact declaration identities carrying the stdlib's local sequence-filter representation marker. The runtime
    // stdlib build cannot discover its own declarations through ReferenceMetadataIndex, so it supplies the same fact
    // directly from BIR. Calls are still selected by declaration identity, never by the source function name.
    static IReadOnlySet<string> _localSequenceFilterNotNullDeclarations
        = new HashSet<string>(StringComparer.Ordinal);
    // #76: the four unsigned specialized array value classes -> their SIGNED backing-array element FQN. kotc emits
    // `kotlin.U*Array` as a faithful array identity (like signed IntArray) and STOPS emitting/decomposing the value
    // class; bir2cir OWNS both the native representation (via PrimArrayElem -> the UNSIGNED native array byte[]/uint[]/
    // ...) AND the value-class `.storage` erasure. The backing field `storage` is declared as the SIGNED array
    // (UByteArray.storage : ByteArray = sbyte[], UIntArray.storage : IntArray = int[], ...). Since same-size same-
    // underlying-primitive arrays are assignment-compatible (ECMA-335 array-element-compatible-with — byte[]<->sbyte[],
    // ushort[]<->short[], uint[]<->int[], ulong[]<->long[]), a `storage` read is a runtime-valid reinterpret cast of
    // the receiver to the signed array, and the wrap-ctor(storage: SignedArray) is the inverse reinterpret to the
    // unsigned native array — NOT a real field access / construction. These nodes appear ONLY in the runtime-stdlib
    // self-build (consumer code never touches `.storage`); the ref build squashes bodies so it needs nothing here, and
    // MemberCallSubstitution runs on the !RefBuild path only.
    static readonly IReadOnlyDictionary<string, string> UnsignedArraySignedElem = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.UByteArray"] = "kotlin.Byte",
        ["kotlin.UShortArray"] = "kotlin.Short",
        ["kotlin.UIntArray"] = "kotlin.Int",
        ["kotlin.ULongArray"] = "kotlin.Long",
    };

    // #139 site-2: an unsigned specialized array is the SAME native N-bit-integer array as its same-width SIGNED
    // counterpart (UByte=Byte, UShort=Short, UInt=Int, ULong=Long per #53/#54), so a member call on an unsigned-array
    // owner resolves against the emitted signed-array class — the identical native-array method-holder. bir2cir OWNS
    // this Kotlin<->CLR array identity, so it rewrites the call `ownerType` to the signed-array FQN here (the ownerType
    // survives only in the rt self-build; consumer CIR is fully lowered). This RETIRES ilemit's NativeArrayOwner alias
    // (Emitter.Types.cs / Resolve.cs FindMethod) — the layer-purity fix: ilemit re-resolved a Kotlin equivalence it
    // should never have known. Sig/args are unchanged, so the resolved method is byte-identical to the former alias.
    static readonly IReadOnlyDictionary<string, string> UnsignedArraySignedOwner = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.UByteArray"] = "kotlin.ByteArray",
        ["kotlin.UShortArray"] = "kotlin.ShortArray",
        ["kotlin.UIntArray"] = "kotlin.IntArray",
        ["kotlin.ULongArray"] = "kotlin.LongArray",
    };

    // Rewrite a member-resolving node's unsigned-array owner to the same-width signed-array FQN. A no-op for a node
    // TransformCall already substituted (a clrInstance/clrStatic/clrPropGet BCL call has no unsigned owner) or whose
    // owner is not an unsigned specialized array. Covers EVERY node kind that reaches ilemit's FindMethod — the exact
    // set the retired NativeArrayOwner alias covered: a member call / property accessor (`callInstance`), a static call
    // (`callStatic`, owner in `owner`), and a bound method reference (`newBoundDelegate`/`newBoundClrDelegate`).
    static JsonNode RewriteUnsignedArrayOwner(JsonNode node)
    {
        if (node is not JsonObject o) return node;
        string field = (o["k"] as JsonValue)?.GetValue<string>() switch
        {
            "callInstance" or "newBoundDelegate" or "newBoundClrDelegate" => "ownerType",
            "callStatic" => "owner",
            _ => null,
        };
        if (field != null && TypeJson.Read(o[field]) is TypeNode.Fqn owner
            && UnsignedArraySignedOwner.TryGetValue(owner.Name, out var signed))
        {
            var rewritten = TypeJson.Write(owner.Args != null ? new TypeNode.Fqn(signed, owner.Args) : new TypeNode.Fqn(signed));
            o[field] = rewritten;
            // Keep #204's bound-delegate dispatch identity in lockstep with the exact member owner.
            if ((o["k"] as JsonValue)?.GetValue<string>() == "newBoundDelegate")
                o["calleeOwner"] = rewritten.DeepClone();
        }
        return node;
    }

    // A star-projection/erased type-arg token: `object`/`kotlin.Any`, possibly nullable/oblivious-wrapped (a star K/V
    // projects to `Any?`, i.e. `{t:nullable,of:kotlin.Any}` post-#48). Used by the Map<*,*> extension guard (#74a).
    static bool IsErasedAny(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsErasedAny(n.Of),
        TypeNode.Oblivious o => IsErasedAny(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true,
        _ => false,
    };

    // The struct-ness oracle, for the `Array<X?>` element canonicalization (#86 D2) the array factories below apply.
    static Func<string, bool> _isValue = _ => false;

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTopLevelFns, bool attributeTopLevelOwner, Func<string, bool> isValue,
        IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> localPropertyAccessors,
        IReadOnlySet<string> localSequenceFilterNotNullDeclarations)
    {
        _localTopLevelFns = localTopLevelFns;
        _attributeTopLevelOwner = attributeTopLevelOwner;
        _isValue = isValue ?? (_ => false);
        _localPropertyAccessors = localPropertyAccessors
            ?? new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>();
        _localPropertyOwners = _localPropertyAccessors.Keys.Select(key => key.Owner)
            .ToHashSet(StringComparer.Ordinal);
        _localSequenceFilterNotNullDeclarations = localSequenceFilterNotNullDeclarations
            ?? new HashSet<string>(StringComparer.Ordinal);
        _typesWithConcreteIterator = CollectConcreteIteratorTypes(root);
        return Rewrite(root, refs, new SubstCtx());
    }

    public static IReadOnlySet<string> CollectLocalSequenceFilterNotNullDeclarations(IEnumerable<JsonNode> roots)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void WalkOwner(JsonObject owner)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Str(method[DeclarationIdentityBinding.Key]) is string id
                        && method["attrs"] is JsonArray attrs
                        && attrs.OfType<JsonObject>().Any(attr =>
                            TypeJson.OwnerName(attr["attr"]) == "kotlin.clr.ClrSequenceFilterNotNull"))
                        result.Add(id);
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) WalkOwner(root);
        return result;
    }

    public static IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>
        CollectLocalPropertyAccessors(IEnumerable<JsonNode> roots)
    {
        var candidates = new Dictionary<LocalPropertyAccessorKey, List<LocalPropertyAccessor>>();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["name"]) is string owner && obj["kind"] != null && obj["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                        if (!KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                            && KotlinPropertyAccessors.TryIdentity(method, out var property, out var kind)
                            && Str(method["name"]) is string physical
                            && method["params"] is JsonArray parameters)
                        {
                            var parameterTypes = parameters.OfType<JsonObject>()
                                .Select(parameter => TypeJson.Read(parameter["type"]))
                                .ToArray();
                            if (parameterTypes.Length != parameters.Count || parameterTypes.Any(type => type == null))
                                continue;
                            var key = new LocalPropertyAccessorKey(owner, property, kind,
                                (method["typeParams"] as JsonArray)?.Count ?? 0, parameters.Count);
                            if (!candidates.TryGetValue(key, out var accessors))
                                candidates[key] = accessors = new List<LocalPropertyAccessor>();
                            accessors.Add(new LocalPropertyAccessor(physical, parameterTypes));
                        }
                foreach (var child in obj.Select(pair => pair.Value).ToArray())
                    if (child != null) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray()) if (child != null) Walk(child);
        }
        foreach (var root in roots) Walk(root);
        return candidates.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<LocalPropertyAccessor>)pair.Value);
    }

    internal static bool TryResolveLocalPropertyAccessor(
        IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> accessors,
        string owner, string property, string kind, int methodArity,
        int parameterCount, IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments,
        out string physicalName)
    {
        physicalName = null;
        var key = new LocalPropertyAccessorKey(owner, property, kind, methodArity, parameterCount);
        if (accessors == null || !accessors.TryGetValue(key, out var declared)) return false;
        var matches = declared.Where(candidate => signature == null || candidate.Parameters
                .Select(parameter => ownerTypeArguments == null
                    ? parameter
                    : SupertypeGraph.SubstOwnerTvs(parameter, ownerTypeArguments))
                .Select((parameter, index) =>
                    ReferenceMetadataIndex.AccessorDeclarationDescribesCall(parameter, signature[index]))
                .All(match => match))
            .Select(candidate => candidate.PhysicalName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1) return false;
        physicalName = matches[0];
        return true;
    }

    // Local type FQNs that DECLARE a concrete nullary `iterator()` of their own (a real slot, so a self-call binds to it
    // instead of the ClrIteratorBridge reroute below). A concrete generic collection class (LinkedHashSet) is the case.
    static HashSet<string> CollectConcreteIteratorTypes(JsonNode root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root is JsonObject o && o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string name
                    && to["methods"] is JsonArray ms
                    && ms.OfType<JsonObject>().Any(m =>
                        (m["name"] as JsonValue)?.GetValue<string>() == "iterator"
                        && (m["abstract"] as JsonValue)?.GetValue<bool>() != true
                        && (m["params"] as JsonArray) is { Count: 0 }))
                    set.Add(name);
        return set;
    }

    // Lexical type environment carried DOWN the walk: a name->type-token map for the enclosing decl's params, and a
    // type-param-name->constraint-tokens map for its generic parameters. Populated at each declaration node (anything
    // carrying `params`/`typeParams`) so a call site can recover its receiver's STATIC type — needed to route a call
    // whose receiver is a generic parameter (`destination: C where C : MutableCollection<R>`) through constrained
    // dispatch instead of a plain callvirt on a padded ICollection<object> owner (which mis-dispatches; see Constrainify).
    sealed class SubstCtx
    {
        // VarTypes/TpConstraints hold STRUCTURED types (a param/local's slot Type, a type-param's constraint Types) —
        // walked natively by Constrainify/CollElemArg/MapKvArgs (a receiver's static type / a collection element).
        public readonly Dictionary<string, TypeNode> VarTypes;
        public readonly Dictionary<string, List<TypeNode>> TpConstraints;
        public SubstCtx()
        {
            VarTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<TypeNode>>(StringComparer.Ordinal);
        }
        SubstCtx(SubstCtx parent)
        {
            VarTypes = new Dictionary<string, TypeNode>(parent.VarTypes, StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<TypeNode>>(parent.TpConstraints, StringComparer.Ordinal);
        }
        // A child scope extended with this declaration's params + generic-parameter constraints. Returns `this`
        // unchanged when the node introduces no bindings (so plain nodes don't allocate a scope).
        //
        // SHADOWED-LOCAL disambiguation (bundle-6 BUG-2): a method/lambda's own local `var` decls must ALSO enter
        // VarTypes, so a `var x` that SHADOWS a same-named param `x` of a different type wins (its own type is what a
        // receiver read resolves to). Without this, a shadowing local was skipped and a call whose receiver is the
        // local kept the PARAM's (possibly `gp:`) type — mis-routing Constrainify to a constrained dispatch on a
        // concrete-typed local. Recorded AFTER params so the local shadows; scoped to this decl's own body (the walk
        // stops at a nested param-bearing decl, so an inner lambda's locals don't leak up). Mirrors the SM's
        // kotc now gives every IrVariable an identity-derived name, so a shadowing local is already a distinct binding.
        public SubstCtx Extend(JsonObject decl)
        {
            var ps = decl["params"] as JsonArray;
            var tps = decl["typeParams"] as JsonArray;
            // A method/accessor DECLARATION (a `params`+`body` node with no expression `k`) needs its local `var` types
            // recorded even when it has ZERO params — a param-less getter (get_groupValues) otherwise left VarTypes empty,
            // so a receiver read of a materialized local (mapTo's concrete `destination: ArrayList<String>`) could not
            // recover its element type and CollElemArg fell back to the `object` variance-approximation.
            var isDecl = ps != null && decl["body"] != null && decl["k"] == null;
            if ((ps == null || ps.Count == 0) && (tps == null || tps.Count == 0) && !isDecl) return this;
            var child = new SubstCtx(this);
            if (ps != null)
                foreach (var p in ps)
                    if (p is JsonObject po && (po["name"] as JsonValue)?.GetValue<string>() is string pn
                        && TypeJson.Read(po["type"]) is TypeNode pt)
                        child.VarTypes[pn] = UnwrapNullability(pt);
            // TpConstraints is keyed POSITIONALLY, matching a receiver's `tv` (scope+index) — a class decl's params are
            // the TYPE scope, a method/fun's are the METHOD scope (the common constrained-build/compareTo receiver).
            if (tps != null)
            {
                var scope = (decl["kind"] as JsonValue)?.GetValue<string>() is "class" or "interface" ? "type" : "method";
                for (var i = 0; i < tps.Count; i++)
                    if (tps[i] is JsonObject to && to["constraints"] is JsonArray cs)
                        child.TpConstraints[scope + ":" + i] =
                            cs.Select(c => TypeJson.Read(c)).Where(c => c != null).ToList();
            }
            // Walk a DECLARATION's body once to record its local vars (a local shadows a same-name param; and a
            // materialized collection local is the receiver whose element type CollElemArg/Constrainify recover).
            if (isDecl && decl["body"] is JsonNode body) RecordLocalVars(body, child.VarTypes);
            return child;
        }

        // Strip the OUTER nullability annotation (`{t:nullable}` / `{t:oblivious}`) off a receiver-slot type before it is
        // recorded in VarTypes (#37/#48). A receiver's declared nullability is IRRELEVANT to which CLR owner/element type
        // its member calls dispatch on — but every VarTypes reader (RecvStaticType, CollElemArg, MapKvArgs) pattern-matches
        // the RAW node against `TypeNode.Fqn`/`Tv`. A `Map<K,V>?` receiver is a `TypeNode.Nullable`, so it failed those
        // matches and fell back to the type-arg-STRIPPING `BareOwnerFqn` -> `IDictionary<object,object>` (a value-type-
        // invariance EntryPointNotFound at run). Unwrapping here keeps a nullable receiver's concrete type args intact,
        // exactly as the pre-#48 scalar-`nullable`-flag world did (where the slot type was already the bare `Fqn`).
        static TypeNode UnwrapNullability(TypeNode t) => t switch
        {
            TypeNode.Nullable n => UnwrapNullability(n.Of),
            TypeNode.Oblivious o => UnwrapNullability(o.Of),
            _ => t,
        };

        // Record the `var name/type` of every local declaration in this decl's own body, so a local shadows a
        // same-named param. Stops at a nested param-bearing declaration (an inner lambda/fun scopes its own locals).
        static void RecordLocalVars(JsonNode node, Dictionary<string, TypeNode> vars)
        {
            switch (node)
            {
                case JsonObject o:
                    if ((o["k"] as JsonValue)?.GetValue<string>() == "var"
                        && (o["name"] as JsonValue)?.GetValue<string>() is string vn
                        && TypeJson.Read(o["type"]) is TypeNode vt)
                        vars[vn] = UnwrapNullability(vt);
                    if (o["params"] is JsonArray ip && ip.Count > 0) return;   // nested decl: its locals are its own
                    foreach (var kv in o) if (kv.Value != null) RecordLocalVars(kv.Value, vars);
                    break;
                case JsonArray a:
                    foreach (var it in a) if (it != null) RecordLocalVars(it, vars);
                    break;
            }
        }
    }

    static JsonNode Rewrite(JsonNode node, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        if (node is JsonObject obj)
        {
            var childCtx = ctx.Extend(obj);   // params/typeParams of THIS decl scope its children (the body / sub-exprs)
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, refs, childCtx);   // children first (bottom-up)
            return Transform(copy, refs, childCtx);
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Rewrite(item, refs, ctx));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        var result = (node["k"] as JsonValue)?.GetValue<string>() switch
        {
            "new" => TransformNew(node, refs) ?? node,
            "callInstance" => TransformCall(node, refs, instance: true, ctx) ?? node,
            "callStatic" => TransformCall(node, refs, instance: false, ctx) ?? node,
            "staticField" => TransformStaticField(node, refs) ?? node,
            "field" => TransformStorageField(node) ?? node,
            _ => node,
        };
        // #139 site-2: after any substitution, rewrite a surviving unsigned-array member owner to its signed-array FQN
        // (all four FindMethod-reaching node kinds; a no-op when the node was substituted to a BCL call or is not a
        // member-resolving kind). ilemit's NativeArrayOwner alias is retired, so this is the sole owner-alias site.
        return RewriteUnsignedArrayOwner(result);
    }

    // A companion INSTANCE load on a CLR-bound owner (`String.Companion` as a value — e.g. the receiver arg of a
    // companion-extension call like `String.format(...)`): the pure-Kotlin type the ref build emits carries the
    // companion INSTANCE field, but the substituted BCL type (System.String) has none — the substitution erases the
    // companion's runtime representation. kotc flattens a plain companion, so the companion-extension `__self`
    // param is a plain `object` whose value is never used: lower the load to a null object const.
    // #76 EDIT 2 — the unsigned-array value-class `.storage` erasure. kotc emits a read of the SIGNED backing array
    // as a field node `{k:field, name:"storage", ownerType:kotlin.U*Array, recv:R}` (IrGetField). Since kotlin.U*Array
    // now lowers to the UNSIGNED native array (byte[]/uint[]/ushort[]/ulong[]) and `storage` is the SIGNED array
    // (sbyte[]/int[]/short[]/long[]), the read collapses to a same-underlying-primitive REINTERPRET cast of the
    // receiver to the signed array type — NOT a `ldfld storage` (System.Byte[] has no `storage` field). This is a
    // distinct branch from the scalar inline-erasure (`get_data()` -> `{k:conv}`): a conv to an array is nonsensical.
    static JsonNode TransformStorageField(JsonObject node)
    {
        if ((node["name"] as JsonValue)?.GetValue<string>() != "storage") return null;
        var owner = TypeJson.OwnerName(node["ownerType"]);
        if (owner == null || !UnsignedArraySignedElem.TryGetValue(owner, out var signedElem)) return null;
        return new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeJson.Write(new TypeNode.Array(new TypeNode.Fqn(signedElem))),
            ["e"] = node["recv"]?.DeepClone(),
        };
    }

    static JsonNode TransformStaticField(JsonObject node, ReferenceMetadataIndex refs)
    {
        if ((node["name"] as JsonValue)?.GetValue<string>() != "INSTANCE") return null;
        var owner = TypeJson.OwnerName(node["ownerType"]);
        if (string.IsNullOrEmpty(owner) || !refs.TryResolveClrOwner(owner, out _, out _)) return null;
        return new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("object"), ["value"] = null };
    }

    // `new T(..)` on a CLR-bound REFERENCE owner -> newClr. A value-type (struct) owner is left untouched: a value
    // primitive keeps its identity (the inline-value-class / unsigned representation is a primitive concern handled
    // by type lowering + kotc, not a member-call substitution).
    static JsonNode TransformNew(JsonObject node, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(node["type"]) is not TypeNode.Fqn ownerFqn) return null;

        // #76 EDIT 3 — the unsigned-array WRAP-CTOR erasure (inverse of the `.storage` reinterpret). The @PublishedApi
        // `constructor(storage: SignedArray)` wraps a signed array into the unsigned specialized array (e.g.
        // `UIntArray(storage.sliceArray(indices))`). Since kotlin.U*Array lowers to the UNSIGNED native array and the
        // arg is the SIGNED native array, the wrap is a same-underlying-primitive REINTERPRET cast to the unsigned
        // array type, NOT a real construction. The SIZED `constructor(size: Int)` was already turned into newArraySized
        // by ArrayConstructionLowering (which defers ONLY the array-arg wrap-ctor), so any surviving 1-arg
        // `new kotlin.U*Array` here is the wrap-ctor. Element = the UNSIGNED element (PrimArrayElem: UByteArray->UByte).
        if (UnsignedArraySignedElem.ContainsKey(ownerFqn.Name)
            && BirTypeLowering.PrimArrayElem.TryGetValue(ownerFqn.Name, out var unsElem)
            && node["args"] is JsonArray wrapArgs && wrapArgs.Count == 1)
            return new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(new TypeNode.Array(new TypeNode.Fqn(unsElem))),
                ["e"] = wrapArgs[0].DeepClone(),
            };

        var viaAlias = refs.TryResolveClrOwner(ownerFqn.Name, out var bcl, out var kind);
        if (!viaAlias)
        {
            // REFERENCE-KLIB-PROJECTED .NET owner (A2 tail / #73 M4 newClr): kotc emits a plain `new` by the .NET-FQN
            // identity (it no longer decides the ctor SHAPE); the newClr decision moves HERE, resolved off the loaded
            // refs — the exact axis NetInteropBinding uses for an external .NET CALL. Keep the .NET-FQN name verbatim
            // (an arity-qualified `Task`1`/nested `Outer+Inner` projected name diverges from its Kotlin ClassId name, so
            // it must ride through unchanged — do NOT re-derive it from a Kotlin type token). No struct/enum skip: a
            // .NET struct ctor is a valid `newobj`, and kotc emitted newClr for a projected struct too. Also
            // catches a REFERENCED Kotlin library class (`new mylib.W(..)`, ktproj-pr) whose dll is on the refs — the
            // same axis #61 established for its CALLs; ilemit's EmitClrNew resolves it identically.
            if (refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqn.Name), ownerFqn.Args?.Length ?? 0) == null) return null;
            bcl = ownerFqn.Name; kind = "class";
        }

        // Inline-class CONSTRUCTION erasure (the BOX, mirror of the `.data` unbox collapse): an @JvmInline value class
        // erases to its single backing field's primitive CLR form, so `new UByte(arg)` IS `arg` (no System.Byte(byte)
        // ctor exists). Collapse to the lone arg UNCHANGED — never a conv: the int32 stack bits are already the value,
        // and a signed conv (Conv_I1) would sign-extend and corrupt an unsigned high bit (UByte 200 -> -56). Width is
        // truncated/masked at the byte-typed store/use sites. (Codex-confirmed: identity, not conv.)
        if (refs.IsInlineValueClass(ownerFqn.Name) &&
            node["args"] is JsonArray ctorArgs && ctorArgs.Count == 1)
            return ctorArgs[0].DeepClone();

        if (kind is "struct" or "enum") return null;

        // A GENERIC @ClrTypeAlias owner (`new HashSet<E>()`) must carry its element args so ilemit reconstructs the
        // instantiation: the structured `Fqn(bcl, sourceArgs)` (the SAME generic-alias form BirTypeLowering produces
        // for type positions — the newClr `type` is a TypeKey, so the subsequent type-lowering pass lowers the args). A
        // non-generic owner is the bare BCL Fqn.
        var typeNode = ownerFqn.Args != null ? new TypeNode.Fqn(bcl, ownerFqn.Args) : new TypeNode.Fqn(bcl);

        var args = node["args"] as JsonArray ?? new JsonArray();

        // JVM (initialCapacity: Int, loadFactor: Float) collection ctor -> the capacity-only (int) BCL ctor. .NET's
        // HashSet/Dictionary have NO (int, float) constructor (loadFactor is a JVM hashtable concept), so a
        // `HashSet<Int>(16, 0.75f)` call would mis-resolve to the `(IEnumerable, IEqualityComparer)` overload and throw
        // at run. Map away the trailing loadFactor SLOT (and its declared argType) so the overload key becomes a bare
        // (int). Gated on a @ClrTypeAlias owner whose declared 2nd ctor param is a Float — the loadFactor idiom is
        // unique to the stdlib collection aliases (no BCL type reaching here has a genuine (int, float) ctor), and it
        // covers every alias that declares one at once (HashSet, HashMap's Dictionary, LinkedHashMap's
        // OrderedDictionary), which is what makes this a rule about mapped-away parameters rather than a HashSet case.
        //
        // The mapped-away argument still EVALUATES (#278). Kotlin evaluates every argument expression a call supplies
        // exactly once, in argument order, whether or not the emitted CLR shape has a slot for its VALUE — so losing
        // the loadFactor SLOT must not lose the `HashSet(16, computeLoadFactor())` CALL. `plan` re-expresses the
        // arguments as the call-evaluation plan they always were, and the decision is deferred to the pass that owns
        // it (see MapAwayArguments).
        JsonArray plan = null;
        if (args.Count == 2 && refs.Aliases.ContainsKey(ownerFqn.Name)
            && node["argTypes"] is JsonArray dat && dat.Count == 2 && IsFloatArg(dat[1]))
        {
            plan = MapAwayArguments(args, dat, keep: 1, out args);
            node["argTypes"] = new JsonArray { dat[0].DeepClone() };
        }

        var newClrArgTypes = CtorArgTypes(node, args);
        var newClrArgs = (JsonArray)args.DeepClone();
        // M10 coercion applies ONLY to an @ClrTypeAlias owner (the alias route). A BCL type can never declare a
        // `kotlin.CharSequence`/`dotkt$CharSequence` ctor param, and a REFERENCED KOTLIN library class reached through
        // the external-owner fallback (`new mylib.W(cs: CharSequence)`) DOES — coercing there would corrupt its real
        // `dotkt$CharSequence` param (its compiled ctor takes the adapter, not String). The M10 target
        // `kotlin.text.StringBuilder` is a @ClrTypeAlias, so it always resolves via the alias route.
        if (viaAlias) CoerceCharSequenceCtorArgs(newClrArgs, newClrArgTypes);
        var lowered = new JsonObject
        {
            ["k"] = "newClr",
            ["type"] = TypeJson.Write(typeNode),
            ["argTypes"] = newClrArgTypes,
            ["args"] = newClrArgs,
        };
        return plan == null ? lowered : MaterialiseMappedArguments(plan, lowered, typeNode);
    }

    /// Re-express the arguments of a call whose CLR shape KEEPS only the leading `keep` of them as a call-evaluation
    /// plan: one binding per ORIGINAL argument, in Kotlin argument order, carrying its declared type. The kept ones
    /// become a `bindRef` read in their own slot (returned through `slots`); the mapped-away ones are bound and read by
    /// NOBODY, which is precisely the shape that says "Kotlin evaluated this value and the emitted call has no slot
    /// for it". `stable` is bir2cir's own Q1 answer (ValueStability.IsReReadable) — this pass supplied the expressions,
    /// so kotc had no binding here to judge.
    static JsonArray MapAwayArguments(JsonArray args, JsonArray argTypes, int keep, out JsonArray slots)
    {
        var bindings = new JsonArray();
        slots = new JsonArray();
        for (var i = 0; i < args.Count; i++)
        {
            var id = CallEvalLowering.FreshBindingId();
            var binding = new JsonObject
            {
                ["id"] = id,
                ["expr"] = args[i].DeepClone(),
                ["stable"] = ValueStability.IsReReadable(args[i]),
            };
            if (i < argTypes.Count && argTypes[i] != null) binding["type"] = argTypes[i].DeepClone();
            bindings.Add(binding);
            if (i < keep) slots.Add(new JsonObject { ["k"] = "bindRef", ["id"] = id });
        }
        return bindings;
    }

    /// Give the plan to the pass that decides what an unread binding costs. CallEvalLowering.Materialise answers BOTH
    /// questions this site must not answer twice: a binding nothing reads is evaluated into a local unless Q2
    /// (ValueStability.IsDroppable) says the evaluation is unobservable, and its PREFIX rule then materialises every
    /// earlier non-stable argument so a kept value cannot slide behind a mapped-away one. That is why the mapping
    /// builds a plan instead of prepending an evaluate-and-discard statement of its own — an ad-hoc discard would be a
    /// second, drifting copy of those two rules, and it would evaluate the loadFactor ahead of the capacity.
    ///
    /// The common `HashSet(16, 0.75f)` literal idiom materialises NOTHING (a const is droppable, and the surviving
    /// argument then inlines back into its slot), so it emits the same bare `newClr` as before — no dead temp.
    static JsonNode MaterialiseMappedArguments(JsonArray plan, JsonObject lowered, TypeNode type)
    {
        var (stmts, repl) = CallEvalLowering.Materialise(plan, new List<JsonNode> { lowered }, "a mapped constructor");
        var result = CallEvalLowering.Substitute(lowered, repl);
        // CHOKEPOINT, the same one `CallEvalLowering.Apply` asserts after its own lowering: the plan vocabulary must
        // not survive the pass that authored it. A kept argument's `bindRef` sits in an eager `newClr` slot, so it is
        // always resolved — but that is a property of how the slots are built above, and a later change to `keep` or
        // to the coercions the slots pass through could break it silently. This says so instead.
        AssertNoPlanVocabulary(result);
        if (stmts.Count == 0) return result;
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["type"] = TypeJson.Write(type),
            ["stmts"] = stmts,
            ["result"] = result,
        };
    }

    /// A `bindRef` left in an emitted node would reach ilemit as an unknown kind. Every plan authored in this pass puts
    /// its readers on the lowered call's eager operand spine, so `Materialise` must resolve them. Keep one invariant
    /// assertion shared by constructor-shape adapters and exact intrinsic argument adapters.
    static void AssertNoPlanVocabulary(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if ((o["k"] as JsonValue)?.GetValue<string>() == "bindRef")
                    throw new InvalidOperationException(
                        "bir2cir: a locally-authored call plan binding was not resolved into its slot — the "
                        + "read is not on the emitted node's eager operand spine, so CallEvalLowering.Materialise "
                        + "could not inline it. Whatever now wraps the kept arguments needs an arm in "
                        + "CallEvalLowering.EagerKinds.");
                foreach (var kv in o) if (kv.Value != null) AssertNoPlanVocabulary(kv.Value);
                break;
            case JsonArray a:
                foreach (var it in a) if (it != null) AssertNoPlanVocabulary(it);
                break;
        }
    }

    // M10 (#73) — the CharSequence -> String ctor-argument coercion. A ctor param typed CharSequence (the stdlib's
    // `StringBuilder(content: CharSequence)`, lowered to the synthetic `dotkt$CharSequence`) matches NO BCL constructor
    // — System.Text.StringBuilder takes only `(string)`/`(int)` (Codex-confirmed), so the newClr's arity fallback
    // mis-binds and ilemit throws InvalidProgram. Coerce each such argument to String via the null-safe, virtual
    // `kotlin.LibraryKt.toString(object)` (the SAME node CharSeqStringLowering.CoerceOrNull emits) and retarget its
    // argType to kotlin.String, so the `StringBuilder(String)` overload binds by exact GetConstructor. This lets the
    // real stdlib `CharSequence.reversed() = StringBuilder(this).reverse()` compile — retiring kotc's `strReversed`.
    static void CoerceCharSequenceCtorArgs(JsonArray args, JsonArray argTypes)
    {
        for (var i = 0; i < argTypes.Count && i < args.Count; i++)
        {
            var t = TypeJson.Read(argTypes[i]);
            while (t is TypeNode.Nullable nn) t = nn.Of;
            var name = (t as TypeNode.Fqn)?.Name;
            if (name != SharedSyntheticSynthesis.CharSeq && name != "kotlin.CharSequence") continue;
            var orig = args[i].DeepClone();
            args[i] = new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"),
                ["method"] = "toString",
                ["sig"] = new JsonArray { TypeJson.Fqn("object") },
                ["args"] = new JsonArray { orig },
            };
            argTypes[i] = TypeJson.Fqn("kotlin.String");
        }
    }

    // The newClr's ctor-overload key. kotc emits the ctor's DECLARED param types on the `new` node's `argTypes`, but they
    // reference the class's OWN type parameters (`ArrayList<E>`'s copy ctor -> `Collection[gp:E]`). Substitute those with
    // the instantiation's type args (`ArrayList[kotlin.Int]` => E:=kotlin.Int) so the lowered argType is a RESOLVABLE,
    // precise overload key (`IReadOnlyCollection[int]`) — this disambiguates List's `IEnumerable<T>` ctor from its `int`
    // capacity ctor (a bare `object`/unbound type arg matches neither, so ilemit mis-picked `List(int)` ->
    // InvalidProgramException). The 2nd ctor arg is a Float (the JVM loadFactor idiom).
    static bool IsFloatArg(JsonNode n) =>
        TypeNode.Parse(n.ToJsonString()) is TypeNode.Fqn { Args: null, Name: "kotlin.Float" or "float" };

    static JsonArray CtorArgTypes(JsonObject node, JsonArray args)
    {
        var declared = node["argTypes"]!.AsArray();
        if (declared.Count != args.Count) throw new InvalidOperationException();
        var result = new JsonArray();
        foreach (var a in declared)
            result.Add(a!.DeepClone());
        return result;
    }

    // A `callStatic owner=null` to a @ClrCollectionFactory/@ClrArrayFactory top-level fun -> its construction node, or
    // null when the call is not a factory (or is a non-decomposable mapOf -> left as a plain call). The element/key/value
    // TYPES come from the call's `typeArgs` (the canonical source: correct for empty factories, single-element overloads,
    // and mapOf's [K,V]); the ELEMENTS from the vararg argument (kotc emits it as a `newArray`), the lone non-vararg
    // element, or none. Mirrors the retired kotc factory recognition (BirEmitter.kt LIST/SET/MAP/ARRAY_FACTORY sites).
    static JsonNode TryFactorySubst(JsonObject node, ReferenceMetadataIndex refs, string fn)
    {
        var args = node["args"] as JsonArray ?? new JsonArray();
        var typeArgs = node["typeArgs"] as JsonArray;
        var exactFactory = (node[DeclarationIdentityBinding.ReferencedFactoryKey] as JsonValue)?
            .TryGetValue<bool>(out var selectedFactory) == true && selectedFactory;
        string collKind;
        string arrKind;
        string arrElemHint;
        if (exactFactory)
        {
            if (Str(node[DeclarationIdentityBinding.Key]) is not string selectedId
                || !refs.TryDeclarationFactory(selectedId, out collKind, out arrKind, out arrElemHint))
                throw new InvalidOperationException(
                    $"bir2cir: selected referenced declaration identity '{node[DeclarationIdentityBinding.Key]}' lost its factory binding");
        }
        else
        {
            collKind = refs.CollectionFactoryKind(fn);
            arrKind = refs.ArrayFactoryKind(fn);
            arrElemHint = refs.ArrayFactoryElemHint(fn);
        }

        if (collKind != null)
        {
            if (collKind == "map")
            {
                var kt = TypeArgAt(typeArgs, 0);
                var vt = TypeArgAt(typeArgs, 1);
                if (kt == null || vt == null) return null;                       // can't reconstruct K,V -> plain call
                var entries = new JsonArray();
                // The vararg wrapper newArray's elem is `kotlin.Pair<K,V>` (never K), so a lone newArray arg IS the
                // vararg (wrapperElemType=null). Each element must be an INLINE Pair construction to be split, in either
                // of the two shapes kotc can now emit: a `new kotlin.Pair(k,v)` LITERAL, or a `callStatic .to(k,v)` — the
                // `a to b` idiom (#52 Phase 3 stopped kotc synthesizing `new kotlin.Pair` for `to`; it emits the plain
                // infix `to` call, whose body IS `Pair(this, that)`, so its two args ARE the key/value). Splitting both
                // avoids building the real body's `Pair<K,V>[]` vararg array, which would ArrayTypeMismatch under reified
                // generics when the elements are more-specifically-typed (`Pair<String,String>` into `Pair<String,Any>[]`).
                // A non-inline Pair (`mapOf(pairVar)`) matches neither shape and aborts the substitution -> the real
                // mapOf body runs (the single-element homogeneous case that does NOT hit the covariance mismatch).
                foreach (var el in FactoryElems(args, null))
                {
                    if (el is JsonObject eo && PairKV(eo) is JsonArray pa && pa.Count == 2)
                        entries.Add(new JsonObject { ["key"] = pa[0].DeepClone(), ["value"] = pa[1].DeepClone() });
                    else
                        return null;
                }
                return CarryFactoryStaticType(node, new JsonObject
                {
                    ["k"] = "newMap", ["keyType"] = kt.DeepClone(), ["valType"] = vt.DeepClone(), ["entries"] = entries,
                });
            }
            var elemT = TypeArgAt(typeArgs, 0);
            if (elemT == null) return null;                                     // can't reconstruct elem -> plain call
            var elems = new JsonArray();
            foreach (var el in FactoryElems(args, elemT)) elems.Add(el.DeepClone());
            return CarryFactoryStaticType(node, new JsonObject
            {
                ["k"] = collKind == "set" ? "newSet" : "newList", ["elem"] = elemT.DeepClone(), ["elems"] = elems,
            });
        }

        if (arrKind != null)
        {
            if (arrKind == "sized")                                             // arrayOfNulls<T>(size) -> newArraySized
            {
                var elemT = TypeArgAt(typeArgs, 0);
                if (elemT == null || args.Count < 1) return null;
                // `arrayOfNulls<T>` returns `Array<T?>` — the element is the NULLABLE form of the type argument, NOT the
                // bare T, and the call's typeArgs[0] is the non-null T (`kotlin.Int`). Wrapping it in `Nullable` states
                // that, and `CanonicalArrayElem` then gives it its ONE physical form (#86 D2): `object` for a
                // possibly-value element (an open `T` or a value `Int`/`Boolean` — the array is `object[]` either way,
                // so an open body and a value instantiation of it meet), and the bare element for a reference `T`
                // (`arrayOfNulls<String>` is a `string[]`; ReferenceNullableStrip drops the `?` there anyway). Skip an
                // already-nullable typeArg (`arrayOfNulls<Int?>`) to avoid a malformed `Nullable(Nullable)` double-wrap.
                var elemNode = TypeJson.Read(elemT);
                var nullableElem = elemNode is TypeNode.Nullable ? elemNode : new TypeNode.Nullable(elemNode);
                return new JsonObject
                {
                    ["k"] = "newArraySized",
                    ["elem"] = TypeJson.Write(CanonicalArrayElem(nullableElem)),
                    ["size"] = args[0].DeepClone(),
                };
            }
            // "vararg": arrayOf<T>(...) / intArrayOf(...) -> newArray. kotc emits the vararg as a single `newArray` arg
            // whenever it was written as a list of elements — INCLUDING an empty list, since an omitted vararg is
            // filled with the empty array of the element type. The elem source, in precedence: typeArgs[0] (the
            // generic arrayOf<T>) -> the vararg wrapper's own elem (a concrete primitive factory declares no type
            // parameter, so `intArrayOf(1,2)` and `intArrayOf()` are both answered here) -> the ref.dll return-type
            // hint, which is left for the shapes that reach this arm with NO wrapper at all: a lone spread
            // (`intArrayOf(*xs)`, which kotc forwards as the existing array) and a mixed `spreadConcat`
            // (`intArrayOf(1, *xs)`). NOTE that both of those shapes are mis-lowered today for a reason this lookup
            // does not reach: with no wrapper there are no elements to copy, so the substitution builds an EMPTY array.
            var wrapper = args.Count == 1 && args[0] is JsonObject w && (w["k"] as JsonValue)?.GetValue<string>() == "newArray" ? w : null;
            var arrElem = TypeArgAt(typeArgs, 0) ?? wrapper?["elem"]
                ?? (arrElemHint is string hint ? TypeJson.Fqn(hint) : null);
            if (arrElem == null) return null;                                   // no element source -> plain call
            var arrElems = new JsonArray();
            foreach (var el in (wrapper?["elems"] as JsonArray) ?? new JsonArray()) arrElems.Add(el.DeepClone());
            // `arrayOf<Int?>(1, null)` names a NULLABLE value element in its own type argument, which is the same
            // `Array<Int?>` the declaration axis makes `object[]` — so the allocation obeys the same rule (#86 D2).
            return new JsonObject
            {
                ["k"] = "newArray",
                ["elem"] = TypeJson.Write(CanonicalArrayElem(TypeJson.Read(arrElem))),
                ["elems"] = arrElems,
            };
        }
        return null;
    }

    // A collection factory's source call already carries the frontend's exact instantiated result type — including
    // the distinction between List/MutableList/ArrayList and Map/MutableMap/concrete map faces. Preserve that fact on
    // the construction which replaces the call instead of asking an early structural consumer to guess the surface
    // from `newList`/`newMap`. BirTypeLowering consumes and removes `sty` before CIR, like every other call rewrite.
    static JsonObject CarryFactoryStaticType(JsonObject source, JsonObject construction)
    {
        if (source["sty"] is JsonNode sty) construction["sty"] = sty.DeepClone();
        return construction;
    }

    // Sequence.filterNotNull carries an exact stdlib binding fact. Its source Sequence<T?> is object-elemented when T
    // may be a value type, so materialize the CLR-specific adapter whose declared output is genuinely Sequence<T>.
    // The marker, not the function name, selects this representation.
    static JsonObject SequenceFilterNotNullAdapter(JsonObject node)
    {
        var args = node["args"] as JsonArray;
        var sig = node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray;
        var typeArgs = node["typeArgs"] as JsonArray;
        if (args == null || args.Count != 1 || sig == null || sig.Count != 1
            || typeArgs == null || typeArgs.Count != 1 || TypeJson.Read(typeArgs[0]) is not TypeNode elem)
            throw new InvalidOperationException("bir2cir: malformed @ClrSequenceFilterNotNull call");
        return CarryFactoryStaticType(node, new JsonObject
        {
            ["k"] = "new",
            ["type"] = TypeJson.Write(new TypeNode.Fqn(
                "kotlin.sequences.ClrFilteringNotNullSequence",
                new TypeNode[] { elem })),
            ["argTypes"] = new JsonArray { sig[0]!.DeepClone() },
            ["args"] = new JsonArray { args[0]!.DeepClone() },
        });
    }

    // The physical element of an array whose Kotlin element type is `elem` (#86, owned by NullableGenericErasure): an
    // array element is a reified ARGUMENT. The factories above build arrays AFTER the declaration-axis erasure has
    // run, so they apply the rule themselves rather than inheriting it from a sweep that has already gone by.
    static TypeNode CanonicalArrayElem(TypeNode elem)
        => elem == null ? null : NullableGenericErasure.EraseArgument(elem, _isValue);

    // An INLINE Pair construction's two operands (key, value), or null if `el` is not one. Two shapes: a `new
    // kotlin.Pair(k,v)` literal, or a `callStatic .to(k,v)` — the `a to b` idiom whose stdlib body is `Pair(this,
    // that)` (so its two args ARE the operands). By the time this runs the `to` call has been owner-attributed to its
    // file class (bottom-up transform), so match on method="to" + a `kotlin.Pair` return, not on owner=null.
    static JsonArray PairKV(JsonObject el)
    {
        var k = (el["k"] as JsonValue)?.GetValue<string>();
        if (k == "new" && TypeJson.OwnerName(el["type"]) == "kotlin.Pair" && el["args"] is JsonArray na && na.Count == 2)
            return na;
        if (k == "callStatic" && (el["method"] as JsonValue)?.GetValue<string>() == "to"
            && TypeJson.OwnerName(el["ret"]) == "kotlin.Pair" && el["args"] is JsonArray ta && ta.Count == 2)
            return ta;
        return null;
    }

    // The i-th call type argument (a structured Type node), or null when absent. The canonical element/key/value source.
    static JsonNode TypeArgAt(JsonArray typeArgs, int i) => typeArgs != null && i < typeArgs.Count ? typeArgs[i] : null;

    // The element nodes of a factory call: the single vararg argument's `elems` when args is one `newArray` that IS the
    // vararg wrapper (its elem matches `wrapperElemType`; pass null to accept any lone newArray, for mapOf whose wrapper
    // elem is `Pair<K,V>` not the map key), otherwise the args verbatim (the lone non-vararg element, or none for empty).
    static IEnumerable<JsonNode> FactoryElems(JsonArray args, JsonNode wrapperElemType)
    {
        if (args.Count == 1 && args[0] is JsonObject o && (o["k"] as JsonValue)?.GetValue<string>() == "newArray"
            && (wrapperElemType == null || JsonNode.DeepEquals(o["elem"], wrapperElemType)))
            return (o["elems"] as JsonArray ?? new JsonArray());
        return args;
    }

    static JsonNode TransformCall(JsonObject node, ReferenceMetadataIndex refs, bool instance, SubstCtx ctx = null)
    {
        var ownerFqnNode = TypeJson.Read(node[instance ? "ownerType" : "owner"]) as TypeNode.Fqn;
        var ownerToken = ownerFqnNode?.Name;
        if (string.IsNullOrEmpty(ownerToken))
        {
            // Top-level fun call (`callStatic owner=null`) bound by @ClrIntrinsic. Two shapes (sourced from the ref.dll):
            //   FQ "System.X.Y"  -> a fully-qualified BCL static: split at the last '.' -> clrStatic System.X.Y(args).
            //   bare "Name"      -> an EXTENSION receiver's instance method (`Array<T>.nativeClone()`@ClrIntrinsic("Clone")
            //                       -> recv.Clone()): clrInstance on the first arg (the extension receiver). The first
            //                       sig type is the receiver type; the rest are the method args.
            var fn = (node["method"] as JsonValue)?.GetValue<string>();
            if (instance || string.IsNullOrEmpty(fn)) return null;
            // A reference-KLIB-projected STATIC property on a referenced DotKt type carries its declaring type in
            // `ownerType`, while callStatic's `owner` remains null. Bind a real CLR property/public field immediately,
            // before the owner-null top-level-property convention below allocates its dedicated accessor name.
            // The declaring type is resolved from the reference metadata universe, so this is independent of package
            // names and covers class-like enum entries as well as ordinary companion/static properties.
            if ((node["prop"] as JsonValue)?.GetValue<string>() is ("get" or "set") and var injectedPropKind
                && TypeJson.Read(node["ownerType"]) is TypeNode.Fqn injectedPropOwner
                && ResolveInjectedPropertyOwner(refs, injectedPropOwner) is var injected
                && injected.Type != null && NetInteropBinding.MemberIsPropertyOrField(injected.Type, fn))
            {
                var injectedPropArgs = node["args"] as JsonArray ?? new JsonArray();
                var shaped = new JsonObject
                {
                    ["k"] = injectedPropKind == "get" ? "clrPropGet" : "clrPropSet",
                    // A static on a GENERIC declaring type has no enclosing type argument in Kotlin syntax, but its
                    // CIL MemberRef parent must still be a closed TypeSpec. Close it from the exact reflected TypeDef,
                    // exactly as the .NET binder does for the same shape.
                    ["type"] = NetInteropBinding.CloseStaticOwner(injected.Owner, injected.Type),
                    ["name"] = fn,
                    ["static"] = true,
                    ["recv"] = null,
                };
                if (injectedPropKind == "get") shaped["ret"] = node["ret"]?.DeepClone();
                else shaped["value"] = injectedPropArgs.Count > 0 ? injectedPropArgs[0]?.DeepClone() : null;
                return shaped;
            }
            // A projected static CLASS member already carries its exact declaring type. Keep that declaration identity
            // ahead of the ownerless top-level indexes: a referenced top-level function may legitimately have the same
            // name and signature. File-class owners remain on the top-level path below, where their overload indexes are
            // required. The distinction comes only from trusted producer metadata, never a name convention.
            var injectedClassSignature = node["argTypes"] as JsonArray ?? node["sig"] as JsonArray;
            if (node["prop"] == null && injectedClassSignature != null &&
                TypeJson.Read(node["ownerType"]) is TypeNode.Fqn injectedClassOwner &&
                refs.HasDotKtOwner(injectedClassOwner.Name) &&
                !refs.IsFileClassOwner(injectedClassOwner.Name))
            {
                node["owner"] = TypeJson.Write(injectedClassOwner);
                node["sig"] ??= injectedClassSignature.DeepClone();
                return node;
            }
            // #81/#157: an owner-null top-level PROPERTY accessor carries the bare property identity plus an explicit
            // `"prop":"get"/"set"` role. Two producers share this path: a top-level extension property
            // (#81/C7: `val List<T>.lastIndex`, `val Int.absoluteValue` — resolves via the recvKey branch), and a
            // plain (non-extension) cross-module top-level val deserialized from a metadata klib whose parent is a
            // package fragment (#157: `COROUTINE_SUSPENDED` — resolves via the zero-arg single-candidate branch;
            // this replaced a COROUTINE_SUSPENDED-specific owner-rebind band-aid, deleted as redundant). Referenced
            // properties bind through their exact metadata association; local properties retain the semantic role until
            // the common forward allocator runs.
            var topLevelPropertyAccess = Str(node["prop"]);
            var exactReferencedSequenceFilter =
                (node[DeclarationIdentityBinding.ReferencedSequenceFilterNotNullKey] as JsonValue)?
                    .TryGetValue<bool>(out var referencedSequenceFilter) == true && referencedSequenceFilter;
            var exactLocalSequenceFilter = Str(node[DeclarationIdentityBinding.Key]) is string localSequenceFilterId
                && _localSequenceFilterNotNullDeclarations.Contains(localSequenceFilterId);
            if (exactReferencedSequenceFilter || exactLocalSequenceFilter)
                return SequenceFilterNotNullAdapter(node);
            // Collection/array FACTORY (`listOf`/`setOf`/`mapOf`/`arrayOf`/`intArrayOf`/`arrayOfNulls`): a
            // @ClrCollectionFactory/@ClrArrayFactory marker on the ref.dll top-level fun -> re-emit the
            // newList/newSet/newMap/newArray/newArraySized CONSTRUCTION node (the recognition kotc used to do via its
            // LIST/SET/MAP/ARRAY_FACTORY tables). Handled first so a factory never falls through to the plain top-level
            // owner-attribution below. A non-decomposable form (`mapOf(pairVariable)` — not a `to`-Pair literal) returns
            // null here and stays a plain call to the real factory body.
            var exactFactory = (node[DeclarationIdentityBinding.ReferencedFactoryKey] as JsonValue)?
                .TryGetValue<bool>(out var selectedFactory) == true && selectedFactory;
            if (TryFactorySubst(node, refs, fn) is JsonNode factoryNode) return factoryNode;
            // A selected factory that cannot be represented as a construction (notably mapOf(pairVariable)) must call
            // that exact declaration's body. Keep its semantic node intact for the late identity binder instead of
            // falling through to any erased owner/name/signature resolver in this pass.
            if (exactFactory) return null;
            var args0 = node["args"] as JsonArray ?? new JsonArray();
            var sigParts0 = SplitSig(node);
            // #395: the early declaration-identity binder has already selected this exact ref.dll declaration.
            // Resolve only that declaration's @ClrIntrinsic representation; never repeat overload resolution from
            // owner/name/signature after erasure. Local declarations do not carry this transient marker.
            if ((node[DeclarationIdentityBinding.ReferencedIntrinsicKey] as JsonValue)?.TryGetValue<bool>(out var exact)
                    == true && exact
                && Str(node[DeclarationIdentityBinding.Key]) is string selectedId)
            {
                if (!refs.TryDeclarationIdentity(selectedId, out _, out _, out var selectedIntrinsic,
                        out var selectedByref)
                    || string.IsNullOrEmpty(selectedIntrinsic))
                    throw new InvalidOperationException(
                        $"bir2cir: selected referenced declaration identity '{selectedId}' lost its intrinsic binding");
                if (selectedIntrinsic.LastIndexOf('.') is var selectedDot && selectedDot > 0)
                    return ClrCallNode(node,
                        new TypeNode.Fqn(selectedIntrinsic[..selectedDot]),
                        selectedIntrinsic[(selectedDot + 1)..], selectedIntrinsic[(selectedDot + 1)..],
                        args0, instance: false, selectedByref);
                if (sigParts0.Count >= 1)
                    return TopLevelExtensionInstance(node, refs, selectedIntrinsic, args0, sigParts0, ctx)
                        ?? throw new InvalidOperationException(
                            $"bir2cir: selected intrinsic declaration '{selectedId}' has no extension receiver");
                throw new InvalidOperationException(
                    $"bir2cir: selected intrinsic declaration '{selectedId}' has unsupported binding '{selectedIntrinsic}'");
            }
            if (topLevelPropertyAccess is "get" or "set")
            {
                KotlinPropertyAccessors.PreserveCallIdentity(node, fn, topLevelPropertyAccess);
                // A same-compilation top-level declaration carries its exact semantic file owner. Keep the call ownerless
                // for ilemit's local file-class lookup and let the final forward allocator name its accessor. A referenced
                // property may carry a projected file-class owner; otherwise it must be attributed from authoritative
                // reference metadata here.
                if (TypeJson.OwnerName(node["calleeOwner"]) != null) return null;
                var projectedOwner = TypeJson.OwnerName(node["ownerType"]);
                if (projectedOwner != null && !refs.IsFileClassOwner(projectedOwner)) projectedOwner = null;
                var propertySignature = sigParts0.Count == args0.Count ? sigParts0.ToArray() : null;
                var propertyMethodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
                if (_attributeTopLevelOwner && refs.TryResolveTopLevelProperty(fn, topLevelPropertyAccess,
                    projectedOwner, args0.Count, propertyMethodArity, propertySignature,
                    out var propertyOwner, out var physicalAccessor))
                {
                    node.Remove("prop");
                    node["method"] = fn = physicalAccessor;
                    node["owner"] = TypeJson.Fqn(propertyOwner);
                    PromoteGenericShapeToSig(node);
                }
                else if (!_attributeTopLevelOwner) return null;
                else
                    throw new InvalidOperationException(
                        $"bir2cir: unresolved top-level property accessor '{fn}' ({topLevelPropertyAccess}) — "
                        + "the frontend-resolved property has no exact reference MethodSemantics association");
            }
            // STAR-PROJECTED Map<*,*> cross-module extension (#74a): `m[key]`/`m.containsKey(key)` on a star-projected
            // `Map<*,*>` receiver is NOT dispatched as the Map interface MEMBER (a star receiver's `K`-typed param
            // isn't a viable member-call argument) — Kotlin instead resolves the top-level `@kotlin.internal.
            // OnlyInputTypes` extension `Map<out K,V>.get`/`.containsKey` (Maps.kt). That extension is `@InlineOnly`
            // but is NOT actually inlined cross-module (the frontend klib carries no IR bodies for it), so it arrives
            // HERE as a genuine generic top-level call instantiated K=V=`object`/`Any?` (the star erasure). Its
            // compiled body re-casts internally to the covariance-safe non-generic `IDictionary` facade
            // (`ClrRawDictionary`), but the CALL BOUNDARY's own formal param — `Map<K,V>` = the INVARIANT generic
            // `IDictionary<object,object>` at this instantiation — throws InvalidCastException first (the real
            // receiver's runtime type, e.g. `Dictionary<String,Int>`, is not assignable to it). Recognize this
            // exact shape and emit the non-generic `IDictionary.get_Item`/`.Contains` call directly (its indexer is
            // null-on-missing, matching Kotlin `Map.get`'s null-on-missing exactly) — bypassing the generic route.
            if ((fn == "get" || fn == "containsKey") && args0.Count == 2 && sigParts0.Count >= 1
                && sigParts0[0] is TypeNode.Fqn { Name: "kotlin.collections.Map" or "kotlin.collections.MutableMap" }
                && node["typeArgs"] is JsonArray starTypeArgs && starTypeArgs.Count >= 1
                && starTypeArgs.All(t => IsErasedAny(TypeJson.Read(t))))
                return new JsonObject
                {
                    ["k"] = "clrInstance",
                    ["type"] = TypeJson.Fqn("System.Collections.IDictionary"),
                    ["method"] = fn == "get" ? "get_Item" : "Contains",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") },
                    ["ret"] = TypeJson.Fqn(fn == "get" ? "System.Object" : "System.Boolean"),
                    ["recv"] = args0[0]?.DeepClone(),
                    ["args"] = new JsonArray { args0[1]?.DeepClone() },
                };
            // A top-level @ClrIntrinsic bound to a FQ BCL static. Resolve the EXACT overload by the call's full
            // ParamKey signature first (sqrt/abs/pow -> System.Math.* for Double/Int/Long but System.MathF.* for
            // Float; a non-intrinsic sibling like Double.pow(Int) MISSES here). Fall back to the name-only map only for
            // UNAMBIGUOUS names (isNaN, clrTimestamp) — never for a name whose overloads split across Math/MathF,
            // and never for a name that ALSO has a real-bodied (non-intrinsic) top-level overload: `sort`'s 8
            // primitive-array intrinsics all agree on "System.Array.Sort" (not "ambiguous"), yet the name fallback
            // captured the real-bodied `MutableList<T>.sort()` call inside the compiled `sorted()` body.
            var sigKey0 = string.Join(",", sigParts0.Select(t => ReferenceMetadataIndex.ParamKey(t)));
            if ((refs.TryTopLevelIntrinsicBySig(fn, sigKey0, out var fq)
                    || (!refs.IsAmbiguousTopLevelIntrinsic(fn) && !refs.HasNonIntrinsicTopLevel(fn)
                        && refs.TryTopLevelIntrinsic(fn, out fq)))
                && fq.LastIndexOf('.') is var dot && dot > 0)
                return ClrCallNode(node, new TypeNode.Fqn(fq[..dot]), fq[(dot + 1)..], fq[(dot + 1)..], args0, instance: false, refs.TopLevelByrefPositions(fn));
            // bare-intrinsic extension: resolve by the call's FULL ParamKey signature (receiver-first) so it binds the
            // EXACT @ClrIntrinsic overload — `substring(Int)` never captures the same-arity non-intrinsic `substring(IntRange)`
            // (#46 same-name collapse: the IntRange overload has a Kotlin body and must fall through to the top-level path).
            if (sigParts0.Count >= 1 && refs.TryExtMemberIntrinsic(fn, sigKey0, out var extMember))
                return TopLevelExtensionInstance(node, refs, extMember, args0, sigParts0, ctx);
            // A NON-intrinsic referenced top-level stdlib fun (getOrElse/first/...): kotc emits owner=null (it cannot
            // know the file-class — that is CLR/ref knowledge). In an APP build, attribute it to the file-class the
            // ref.dll says it lives in, so ilemit's owner-present FindMethod reflects it against the runtime stdlib —
            // exactly how the iterator bridge `callStatic kotlin.collections.ClrIteratorBridgeKt.*` already resolves.
            // A same-module call already carries its exact `calleeOwner`; that dispatch fact — not a compilation-wide
            // name set — is the local-sibling authority. The old `_localTopLevelFns.Contains(fn)` guard collapsed
            // packages and hid `kotlin.coroutines.resume` merely because kotlinx.coroutines also declares an unrelated
            // top-level `resume`, leaving the stdlib call ownerless at the CIR boundary.
            // In a stdlib self-build attribution remains disabled by `_attributeTopLevelOwner`.
            if (_attributeTopLevelOwner && TypeJson.OwnerName(node["calleeOwner"]) == null)
            {
                var recvKey = sigParts0.Count >= 1 ? RecvKeyOf(sigParts0[0]) : "";
                // The FINE first-param key disambiguates the array overloads a coarse "[]" recvKey collapses (signed vs
                // unsigned specialized arrays vs the generic Array<T>) so the owner pins the RIGHT file-class (#153).
                var firstParamKey = sigParts0.Count >= 1 ? ReferenceMetadataIndex.ParamKey(sigParts0[0]) : null;
                if (refs.TryResolveTopLevelStatic(fn, recvKey, firstParamKey, out var fileClassOwner))
                {
                    node["owner"] = TypeJson.Fqn(fileClassOwner);   // owner is a birType-emitted (structured Fqn) slot
                    PromoteGenericShapeToSig(node);
                    return node;
                }
                // #25 RESIDUAL: a GENERIC top-level call carries `shapeTypes` (no concrete `sig`), so its recovered
                // receiver-key is EMPTY — and TryResolveTopLevelStatic then can NOT disambiguate the owner whenever the
                // bare fun name lives under more than one file-class in the ref index (two referenced libs, or a
                // common-fragment `*CommonKt` file class stamped asymmetrically from its actual sibling — the reporter's
                // atomicfu `atomicArrayOfNulls`). But kotc ALREADY carried the reference-KLIB-projected file class in
                // `ownerType`: every top-level path that emits `shapeTypes` (plainExternalTopLevelCall + its
                // lift-forwarder mirrors) stamps a non-empty referenced `ownerType` alongside it, so it is always
                // present here — adopt it as the owner and promote `shapeTypes`->`sig`, so ilemit resolves the
                // overload by sig then MakeGenericMethod instead of dropping to the name-only pick that reported
                // "static method not found". Gated on the generic fingerprint (`shapeTypes` + no `sig`) so a non-generic
                // owner-null call the index simply doesn't know is left untouched (its owner stays null).
                if (node["shapeTypes"] is JsonArray && node["sig"] == null
                    && TypeJson.OwnerName(node["ownerType"]) is string injectedOwner && injectedOwner.Length > 0)
                {
                    node["owner"] = TypeJson.Fqn(injectedOwner);
                    PromoteGenericShapeToSig(node);
                    return node;
                }

                // The non-generic counterpart of the residual path above. reference-KLIB-projected static callables carry
                // their declaring type in `ownerType` and their exact parameter list in `argTypes`; kotc preserves
                // both but emits the neutral callStatic owner slot as null. Once the top-level indexes have had first
                // refusal, that projected declaring type is authoritative: move it onto callStatic's CLR owner axis.
                // Some frontend paths already materialize `sig` while others leave only `argTypes`; both represent the
                // same resolved declaration and must converge to the same CIR. The rule is structural and applies to
                // every referenced non-generic static callable, without knowing a library, type, or member name.
                var injectedStaticSignature = node["argTypes"] as JsonArray ?? node["sig"] as JsonArray;
                if (injectedStaticSignature != null
                    && TypeJson.Read(node["ownerType"]) is TypeNode.Fqn injectedStaticOwner)
                {
                    node["owner"] = TypeJson.Write(injectedStaticOwner);
                    node["sig"] ??= injectedStaticSignature.DeepClone();
                    return node;
                }
            }
            return null;
        }

        // A BOUND companion member physically belongs to the nested carrier, while its CLR binding belongs to the
        // semantic outer alias and represents a CLR static. Recover the outer owner only from the validated
        // [KotlinCompanion] association and require an actual @ClrIntrinsic/@ClrProperty/@ClrConv member record.
        // Intrinsic-less companion methods retain their ordinary carrier instance body; mapping the whole companion
        // would incorrectly route e.g. Regex.Companion.fromLiteral to a nonexistent BCL/helper static.
        var memberOwnerToken = ownerToken;
        var companionMember = (node["method"] as JsonValue)?.GetValue<string>();
        var companionArgs = node["args"] as JsonArray ?? new JsonArray();
        var companionMethodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        var companionDeclarationArgs = node["argTypes"] as JsonArray
            ?? node["shapeTypes"] as JsonArray ?? node["sig"] as JsonArray;
        IReadOnlyList<TypeNode> companionSignature = null;
        if (companionDeclarationArgs != null)
        {
            var parsed = companionDeclarationArgs.Select(TypeJson.Read).ToList();
            if (parsed.All(t => t != null)) companionSignature = parsed;
        }
        // A zero-parameter declaration's empty vector is fully identifying even when an older producer omitted the
        // redundant descriptor. A non-empty call without a declaration vector is not exact and must fail closed.
        if (companionSignature == null && companionArgs.Count == 0)
            companionSignature = Array.Empty<TypeNode>();
        // Calls imported from KLIB still name the representation-neutral semantic companion here. Member attributes,
        // however, live on the validated physical carrier. Cross that boundary only through the explicit metadata map.
        var companionCarrierToken = refs.TryCompanionPhysicalOwner(memberOwnerToken, out var mappedCompanionCarrier)
            ? mappedCompanionCarrier
            : memberOwnerToken;
        var companionOwnerFqn = ReferenceMetadataIndex.BareOwnerFqn(companionCarrierToken);
        var companionHasClrBinding = refs.TryExactMemberClrBinding(
            companionOwnerFqn, companionMember, companionMethodArity, companionSignature,
            out var exactCompanionBinding);
        ExactClrMemberBinding inheritedExactMemberBinding = null;
        JsonNode mappedCompanionRecv = null;
        if (instance && companionHasClrBinding &&
            refs.TryCompanionSemanticOwner(companionCarrierToken, out var companionSemanticOwner) &&
            refs.TryResolveClrOwner(companionSemanticOwner, out _, out _))
        {
            mappedCompanionRecv = node["recv"]?.DeepClone();
            ownerToken = companionSemanticOwner;
            ownerFqnNode = new TypeNode.Fqn(companionSemanticOwner, ownerFqnNode.Args);
            instance = false;
        }

        // #76 EDIT 2 (defensive) — a `get_storage()` accessor call on an unsigned-array value class, should kotc emit
        // the backing-field read as a property getter callInstance rather than a raw `{k:field}`. Same erasure as
        // TransformStorageField: reinterpret the receiver to the SIGNED array. Handled BEFORE the CLR-owner gate below
        // (kotlin.U*Array is not @ClrTypeAlias-bound, so it would otherwise return null unresolved).
        if (instance && KotlinPropertyAccessors.IsCall(node, "storage", "get")
            && UnsignedArraySignedElem.TryGetValue(ownerToken, out var storageSignedElem))
            return new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(new TypeNode.Array(new TypeNode.Fqn(storageSignedElem))),
                ["e"] = node["recv"]?.DeepClone(),
            };

        // Rule 2-inherited (interface implementation call): a non-@ClrTypeAlias class can implement a member whose
        // physical slot is owned by a CLR-bound Kotlin interface. The reference assembly deliberately keeps the
        // class's pure-Kotlin declaration name (`EventSubscription.close`), while the runtime implementation has the
        // interface slot name (`Dispose`). DeclarationRename has already consumed the frontend's override closure and
        // renamed this call, but its direct semantic owner still cannot pass the CLR-owner gate below. Re-anchor the
        // call to the exact constructed interface declaration from that same closure and restore its Kotlin member
        // identity; the ordinary alias rules then choose the physical representation. This is important for more than
        // the simple 1:1 intrinsic path: collection members such as MutableCollection.add have Kotlin semantics that
        // require the existing dispatcher rules instead of a direct BCL call.
        //
        // The override owners and signature are expressed in the direct Kotlin owner's type-parameter frame. Close
        // both from that owner's use-site instantiation before consulting an interface declaration: copying an open
        // `MutableList<type#0>` into a non-generic caller would give the type variable a new and invalid meaning.
        // Only an exact declaration signature in the correct caller frame is authority. Multiple Kotlin interface
        // faces may intentionally share one source/physical binding (List.get and MutableList.get both become
        // get_Item); coalesce that representation and keep the first closed owner in the frontend closure. Distinct
        // bindings are refused rather than selected by override-list order. A direct CLR-bound owner remains authoritative.
        if (instance && node["super"] == null && !refs.TryResolveClrOwner(ownerToken, out _, out _)
            && node["overrides"] is JsonArray inheritedOverrides
            && !KotlinPropertyAccessors.TryCallIdentity(node, out _, out _))
        {
            var renamedMember = Str(node["method"]);
            var directOwnerArgs = ownerFqnNode.Args ?? Array.Empty<TypeNode>();
            var inheritedSignature = companionSignature?
                .Select(type => SupertypeGraph.SubstOwnerTvs(type, directOwnerArgs)).ToArray();
            var inherited = new List<(TypeNode.Fqn Owner, string Member, ExactClrMemberBinding Binding)>();
            foreach (var candidate in inheritedOverrides.OfType<JsonObject>())
            {
                if (Str(candidate["kind"]) != "method"
                    || TypeJson.Read(candidate["owner"]) is not TypeNode.Fqn candidateOwner
                    || Str(candidate["member"]) is not string candidateMember
                    || (candidate["arity"] as JsonValue)?.GetValue<int>() != companionArgs.Count)
                    continue;
                var constructedOwner = candidateOwner.Args is null
                    ? candidateOwner
                    : new TypeNode.Fqn(candidateOwner.Name, candidateOwner.Args
                        .Select(type => SupertypeGraph.SubstOwnerTvs(type, directOwnerArgs)).ToArray());
                if (!refs.TryResolveClrOwner(constructedOwner.Name, out _, out var candidateOwnerKind)
                    || candidateOwnerKind != "interface"
                    || refs.OwnerArity(constructedOwner.Name) != (constructedOwner.Args?.Length ?? 0)
                    || !refs.TryExactMemberClrBinding(
                        ReferenceMetadataIndex.BareOwnerFqn(constructedOwner.Name), candidateMember,
                        companionMethodArity, inheritedSignature,
                        constructedOwner.Args ?? Array.Empty<TypeNode>(), out var candidateBinding)
                    || candidateBinding.Intrinsic == null
                    || candidateBinding.Intrinsic != renamedMember)
                    continue;
                inherited.Add((constructedOwner, candidateMember, candidateBinding));
            }
            var distinctInherited = inherited.GroupBy(candidate => string.Join("\u001f",
                    candidate.Member, candidate.Binding.Intrinsic,
                    candidate.Binding.CountStart, candidate.Binding.CountEnd,
                    string.Join(",", candidate.Binding.ByrefPositions ?? Array.Empty<int>())),
                    StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
            if (distinctInherited.Count > 1)
                throw new InvalidOperationException(
                    $"bir2cir: inherited CLR interface call '{ownerToken}.{renamedMember}' has "
                    + $"{distinctInherited.Count} distinct exact slot bindings: "
                    + string.Join(", ", distinctInherited.Select(candidate =>
                        $"{SupertypeGraph.TypeKey(candidate.Owner)}.{candidate.Member}->{candidate.Binding.Intrinsic}")));
            if (distinctInherited.Count == 1)
            {
                var inheritedSlot = distinctInherited[0];
                ownerFqnNode = inheritedSlot.Owner;
                ownerToken = inheritedSlot.Owner.Name;
                memberOwnerToken = ownerToken;
                node["method"] = inheritedSlot.Member;
                inheritedExactMemberBinding = inheritedSlot.Binding;
                companionSignature = inheritedSignature;
                if (inheritedSignature != null)
                    node["sig"] = new JsonArray(inheritedSignature.Select(TypeJson.Write).ToArray());
            }
        }

        // A declaration in this compilation owns this semantic accessor. Bind the physical name allocated from that
        // declaration before an external ancestor property is considered. Ambiguous same-identity declarations were
        // deliberately omitted from the module-wide index and continue through the ordinary exact-resolution path.
        if (instance && !refs.TryResolveClrOwner(ownerToken, out _, out _)
            && KotlinPropertyAccessors.TryCallIdentity(node, out var localProperty, out var localAccessor))
        {
            var owner = ReferenceMetadataIndex.BareOwnerFqn(ownerToken);
            var paramCount = (node["args"] as JsonArray)?.Count ?? 0;
            var signatureNode = node["sig"] as JsonArray ?? node["argTypes"] as JsonArray;
            var accessorSignature = signatureNode?.Select(TypeJson.Read).ToArray();
            if (accessorSignature != null &&
                (accessorSignature.Length != paramCount || accessorSignature.Any(type => type == null)))
                accessorSignature = null;
            var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
            var isLocal = TryResolveLocalPropertyAccessor(_localPropertyAccessors,
                owner, localProperty, localAccessor, methodArity, paramCount, accessorSignature,
                ownerFqnNode.Args ?? Array.Empty<TypeNode>(), out var localPhysicalAccessor);
            // A local declaration owns this call, but its final physical name is deliberately not predicted here.
            // DeclarationRename and every synthesis/move pass run before the final module-wide accessor index is
            // collected; KotlinPropertyAccessors.AllocateAll consumes that exact result at the physical boundary.
            if (isLocal) return null;
            var referencedVirtual = false;
            var isReferenced = refs.TryKotlinPropertyAccessor(owner, localProperty, localAccessor,
                paramCount, methodArity, accessorSignature, ownerFqnNode.Args ?? Array.Empty<TypeNode>(),
                out localPhysicalAccessor, out referencedVirtual);
            if (isReferenced)
            {
                KotlinPropertyAccessors.PreserveCallIdentity(node, localProperty, localAccessor);
                node.Remove("prop");
                node["method"] = localPhysicalAccessor;
                if (referencedVirtual) node["virtual"] = true;
                return null;
            }
        }

        // Rule 2p-inherited (property-accessor override chain): a `.message`/`.cause` read dispatches through a subclass
        // receiver whose STATIC owner is either a USER class (`AppErr : Exception`) — not CLR-bound at all — or a
        // non-redeclaring @ClrTypeAlias subclass (`kotlin.Exception` inherits `message` from `kotlin.Throwable`) — so
        // neither carries the @ClrProperty binding on its OWN members. The binding lives on the CLR-bound ANCESTOR that
        // DECLARES the property (`kotlin.Throwable.message` -> @ClrProperty "Message"). Walk the `overrides` marker (kotc
        // stamps it on every accessor call) to that ancestor and route the read to clrPropGet/clrPropSet on ITS BCL
        // owner. Mirrors Rule 3-inherited (printStackTrace). The DIRECT-owner @ClrProperty (a self-declared member such
        // as StringBuilder.capacity()) takes priority — handled by Rule 2p below — so this fires only when the direct
        // owner has no binding of its own. Runs BEFORE the CLR-owner gate so a NON-CLR-bound direct owner still resolves.
        {
            var pmember = (node["method"] as JsonValue)?.GetValue<string>();
            var pargs = node["args"] as JsonArray ?? new JsonArray();
            KotlinPropertyAccessors.TryCallIdentity(node, out var sourceProperty, out var sourceAccessorKind);
            var directHasProp = instance && !string.IsNullOrEmpty(pmember)
                && refs.TryResolveClrOwner(ownerToken, out _, out _)
                && sourceProperty != null
                && refs.TryExternalPropertyAccessor(memberOwnerToken, sourceProperty, sourceAccessorKind,
                    pargs.Count, companionMethodArity, companionSignature,
                    ownerFqnNode.Args ?? Array.Empty<TypeNode>(),
                    out _, out _, out _);
            if (instance && !directHasProp && !string.IsNullOrEmpty(pmember) && node["overrides"] is JsonArray povChain)
                foreach (var o in povChain)
                    if (o is JsonObject oo && TypeJson.OwnerName(oo["owner"]) is string ovOwner
                        && Str(oo["member"]) == sourceProperty
                        && Str(oo["kind"]) == (sourceAccessorKind == "set" ? "setter" : "getter")
                        // A stdlib runtime self-build reads the reference twin of its own declarations. The module's
                        // local owner is authoritative; never reinterpret that twin as an external CLR property.
                        && !_localPropertyOwners.Contains(ovOwner)
                        && refs.TryExternalPropertyAccessor(ovOwner, sourceProperty, sourceAccessorKind,
                            pargs.Count, companionMethodArity, companionSignature,
                            Array.Empty<TypeNode>(), out var ovBcl, out var povName, out _))
                        // When the direct semantic owner is itself CLR-bound, keep its constructed type. The
                        // override entry names only the bare Kotlin declaration (`Collection.size`) and therefore
                        // cannot carry the derived owner's type-argument substitution (`List<T>`). The CLR member
                        // resolver will re-anchor the property to its exact declaring base interface while preserving
                        // that constructed edge. A non-CLR user subclass still starts from the bound ancestor.
                        return ClrPropNode(node, ClrOwnerType(refs, ownerFqnNode)
                            ?? ClrOwnerType(refs, new TypeNode.Fqn(ovOwner)) ?? new TypeNode.Fqn(ovBcl), povName,
                            sourceAccessorKind == "set" ? ClrPropWrite : ClrPropRead, pmember, pargs,
                            sourceAccessorKind);
        }

        // A Kotlin-collection `iterator()` on an EMITTED (non-@ClrTypeAlias) collection type — a `kotlin.collections.
        // AbstractMutable*` self-call: its abstract iterator() slot vanished when its collection supertype substituted
        // to the BCL IEnumerable face, so `this.iterator()` finds no slot. Route it to the ClrIteratorBridge over the
        // receiver (the exact target the @ClrTypeAlias-interface path — Rule 5 — uses; here the owner is a CLASS not in
        // the alias table, so that rule never reaches it). Element type = the owner's first type-arg. GUARD: a type that
        // DECLARES its own concrete iterator() keeps a real slot — leave its `.iterator()` call alone so it binds to the
        // declared `MutableIterator`-returning method (the bridge returns the base `Iterator`, dropping remove()/set()).
        // Covers BOTH a same-file declarer (the stdlib self-build's concrete LinkedHashSet, via the local scan) AND a
        // NON-local one (an APP's `linkedSetOf(..).iterator().remove()`, via the ref.dll — EntryPointNotFound otherwise).
        if (instance && ownerToken.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && (node["method"] as JsonValue)?.GetValue<string>() == "iterator"
            && node["args"] is JsonArray itArgs && itArgs.Count == 0
            && ownerFqnNode != null && !refs.TryResolveClrOwner(ownerToken, out _, out _)
            && !_typesWithConcreteIterator.Contains(ReferenceMetadataIndex.BareOwnerFqn(ownerToken))
            && !refs.DeclaresConcreteIterator(ownerToken))
            return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable",
                OwnerElemArg(ownerFqnNode), itArgs);

        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var kind))
        {
            // A property-accessor call whose enclosing type carries no @ClrTypeAlias binding — the overwhelmingly
            // common ordinary Kotlin property. kotc emits its bare property identity plus an explicit get/set role on
            // both axes. Preserve that identity, then apply the common forward physical-name allocation so the call
            // resolves to the emitted accessor:
            //   • SAME-module owner -> ilemit's `_types` FindMethod finds the emitted dedicated accessor.
            //   • RE-IMPORTED cross-module Kotlin owner (#17: a `--ref` Kotlin assembly whose type is skipped by
            //     NetInteropBinding's ResolveNetType because it is stdlib/compiler-synthetic vocabulary) -> ilemit's
            //     EXTERNAL-owner ResolveMethod reflects the public
            //     dedicated accessor off the referenced dll. Without this the bare `method:"<p>",prop:"get"`
            //     reaches ilemit and its ResolveMethod looks for a literal method `<p>` -> "method …value() not found".
            // A normally-packaged cross-module Kotlin owner (`shapes.Rectangle.area`) never reaches here — NetInterop-
            // Binding already reshaped it to clrPropGet/clrPropSet. The `prop` carrier is consumed only after copying
            // its facts to the explicit identity fields. `index-get`/`index-set` belongs to NetInteropBinding.
            if ((node["prop"] as JsonValue)?.GetValue<string>() is ("get" or "set") and var uProp
                && (node["method"] as JsonValue)?.GetValue<string>() is string uMember)
            {
                KotlinPropertyAccessors.PreserveCallIdentity(node, uMember, uProp);
                node.Remove("prop");
                node["method"] = KotlinPropertyAccessors.PhysicalName(uMember, uProp);
            }
            return null;
        }

        var member = (node["method"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(member)) return null;
        var ownerFqn = ReferenceMetadataIndex.BareOwnerFqn(memberOwnerToken);
        var args = node["args"] as JsonArray ?? new JsonArray();
        // #78: the STATIC property-accessor marker for a call whose owner IS CLR-bound — carried down to Rule 2p
        // (below) so the explicit @ClrProperty binding is tried on the static axis too, not just instance.
        var staticPropMarker = !instance ? (node["prop"] as JsonValue)?.GetValue<string>() : null;
        if (staticPropMarker != null)
        {
            KotlinPropertyAccessors.PreserveCallIdentity(node, member, staticPropMarker);
            node.Remove("prop");
        }

        // Every authored CLR member role is selected from the same frontend-resolved declaration identity. Property,
        // conversion, intrinsic name, byref shape and argument adapters must never be assembled from same-arity
        // siblings. A missing declaration vector is not exact and therefore states no binding.
        var exactMemberBinding = inheritedExactMemberBinding;
        var hasExactMemberBinding = exactMemberBinding != null || refs.TryExactMemberClrBinding(
            ownerFqn, member, companionMethodArity, companionSignature, out exactMemberBinding);

        // Rule Conv (numeric primitive CONVERSION): the member carries @ClrConv on the ref.dll (`kotlin.Int.toLong`,
        // `kotlin.Double.toInt`, `kotlin.Char.toInt`, ...) -> emit `{k:conv, to:<callee return type>, e:<receiver>}`, the
        // SAME node kotc used to synthesize from the retired NUMBER_CONV name-heuristic. The `to` is the callee's own
        // declared return token (a pre-lowering Kotlin FQN, e.g. `kotlin.Long`); BirTypeLowering later lowers it to the
        // CLR primitive and ilemit selects conv.i4/conv.i8/conv.r8/char. A conversion is nullary (no args). Handled first
        // so it never falls through to Rule 2/3 (the conversion members are intrinsic-less, so IsRule3Member excludes them).
        if (instance && args.Count == 0 && hasExactMemberBinding && exactMemberBinding.Conv
            && exactMemberBinding.ConvTo != null)
            return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(exactMemberBinding.ConvTo), ["e"] = node["recv"]?.DeepClone() };

        // Rule 0 (inline-class ERASURE / unbox): the backing-field getter of an @JvmInline value class erased to its
        // primitive CLR form (`uint.get_data()`) is the unbox — the receiver value IS the field. Collapse it to a
        // `conv` of the receiver to the field's declared type (never a `ldfld data` — System.UInt32 has no `data`). This
        // is the GENERAL inline-erasure rule, not a UInt.toInt special-case; it fixes both the inlined `x.data` and the
        // rule-3 helper body's `self.data`, after which all the unsigned conversions fold to a plain cast.
        var inlineFieldGetter = KotlinPropertyAccessors.TryCallIdentity(node,
            out var inlineProperty, out var inlineAccessor)
            ? refs.TryInlineFieldGetter(ownerFqn, inlineProperty, inlineAccessor, out var inlineConv)
            : refs.TryInlineFieldGetter(ownerFqn, member, out inlineConv);
        if (instance && inlineFieldGetter)
            return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(inlineConv), ["e"] = node["recv"]?.DeepClone() };

        // The CLR owner TYPE the call addresses (a ClrRef-resolvable BCL token; see ClrOwnerType).
        TypeNode clrOwner = ClrOwnerType(refs, ownerFqnNode) ?? new TypeNode.Fqn(bcl);

        // The alias-companion gate selected one declaration by its complete signature. Consume that SAME binding;
        // re-querying by name+argument-count here would allow a same-arity sibling to change the CLR member or byref
        // shape after the exact decision had already succeeded.
        if (mappedCompanionRecv != null && exactCompanionBinding.PropertyName != null)
        {
            // A source property carries prop:get/set. A standalone function annotated @ClrProperty does not, so derive
            // its direction from the exact binding's access bits/member shape. Passing the resulting marker also says
            // that this alias-companion property is static; the mapped carrier receiver is preserved separately below.
            var exactPropMarker = staticPropMarker;
            if (exactPropMarker == null)
            {
                var canRead = (exactCompanionBinding.PropertyAccess & ClrPropRead) != 0;
                var canWrite = (exactCompanionBinding.PropertyAccess & ClrPropWrite) != 0;
                var writes = canWrite && (!canRead || args.Count == 1);
                exactPropMarker = writes ? "set" : "get";
            }
            var exactProp = (JsonObject)ClrPropNode(node, clrOwner, exactCompanionBinding.PropertyName,
                exactCompanionBinding.PropertyAccess, member, args, exactPropMarker, forceStatic: true);
            return CallEvalLowering.PreserveUnreadValueBefore(mappedCompanionRecv, exactProp,
                $"mapped companion property '{companionOwnerFqn}.{member}'");
        }

        // Rule 2p (explicit PROPERTY accessor): the member carries @ClrProperty(access, name) -> route EXPLICITLY to
        // clrPropGet(name) [READ] / clrPropSet(name) [WRITE] on the BCL owner, from the stated access role — NOT the old
        // get_/set_ intrinsic-string-prefix sniff. Handled before Rule 2/3 so a @ClrProperty stub (setLength/capacity/
        // ticks) is neither routed as a plain method nor hoisted as a rule-3 body. #78: also tried on the STATIC axis
        // (a companion computed property carrying the `"prop":"get"/"set"` marker) — a @ClrProperty binding is keyed
        // purely by owner+bare-name+argcount, with no instance/static distinction of its own.
        var hasSemanticProperty = KotlinPropertyAccessors.TryCallIdentity(
            node, out var semanticProperty, out var semanticAccessor);
        if (hasSemanticProperty)
        {
            var hasPhysicalProperty = refs.TryExternalPropertyAccessor(
                ownerFqn, semanticProperty, semanticAccessor,
                args.Count, companionMethodArity, companionSignature,
                ownerFqnNode.Args ?? Array.Empty<TypeNode>(),
                out _, out var semanticClrProperty, out _);
            if (hasPhysicalProperty)
            {
                var prop = (JsonObject)ClrPropNode(node, clrOwner, semanticClrProperty,
                    semanticAccessor == "set" ? ClrPropWrite : ClrPropRead, member, args, semanticAccessor,
                    forceStatic: mappedCompanionRecv != null);
                return CallEvalLowering.PreserveUnreadValueBefore(
                    mappedCompanionRecv, prop, $"mapped companion property '{companionOwnerFqn}.{member}'");
            }
        }
        // A standalone Kotlin function may intentionally carry @ClrProperty without being a property accessor. Its
        // complete declaration identity selects the binding just as it does for an intrinsic; name+arity is not an
        // overload key.
        if (!hasSemanticProperty && (instance || staticPropMarker is "get" or "set") &&
            hasExactMemberBinding && exactMemberBinding.PropertyName != null)
        {
            var prop = (JsonObject)ClrPropNode(node, clrOwner, exactMemberBinding.PropertyName,
                exactMemberBinding.PropertyAccess, member, args, staticPropMarker,
                forceStatic: mappedCompanionRecv != null);
            return CallEvalLowering.PreserveUnreadValueBefore(
                mappedCompanionRecv, prop, $"mapped companion property '{companionOwnerFqn}.{member}'");
        }
        // PRE-Rule-2 semantic override: the Kotlin MUTATION members of an @ClrTypeAlias'd collection interface whose
        // spelling has no usable slot on the BCL face. `add` is @ClrIntrinsic("Add") (the binding drives the
        // implementor-side DeclarationRename) but the CALL semantics diverge — Kotlin `add` returns the
        // changed-Boolean while `ICollection<T>.Add` is VOID (a brIf on the phantom result was a stack underflow);
        // `addAll`, `removeAll`, `retainAll` and `MutableList.addAll(index, …)` have no ICollection/IList slot at
        // all. Route all of them to the ClrCollectionDefaults dispatchers BEFORE the intrinsic rule. Those
        // dispatchers test the compiler-authored Kotlin slot interface first, so a Kotlin implementer's OVERRIDE is
        // reached, and fall back to a BCL-only default otherwise — the earlier unconditional helper call silently
        // bypassed such an override, and the unrouted `removeAll`/`retainAll` reached a runtime name lookup.
        // The 2-arg add(index, e) Insert form falls through to the intrinsic.
        if (instance && kind == "interface" && ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && CollectionSlotHelper(member, args.Count, ownerFqn) is string slotHelper)
            return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", slotHelper,
                CollElemArg(node, refs, ctx, ownerFqnNode), args);

        // PRE-Rule-2 semantic override: MutableList.set(i,e) / removeAt(i) @ClrIntrinsic(set_Item/RemoveAt), but the
        // BCL slots are VOID while Kotlin RETURNS the previous/removed element — binding the intrinsic directly
        // underflows the stack when the result is consumed (`val old = list.set(i,e)` -> InvalidProgramException).
        // Route to the ClrCollectionDefaults wrappers (clrListSet/clrListRemoveAt) that read the old element, perform
        // the void mutation, and return it. `retType` carries the concrete element type for the boxing/convert at the
        // call site (the helper's own `!!0` is out of scope). The void-returning 2-arg add(i,e) Insert form is left
        // on the intrinsic path.
        if (instance && kind == "interface" && ownerFqn == "kotlin.collections.MutableList"
            && (((member is "set" or "set_Item") && args.Count == 2) || ((member is "removeAt" or "RemoveAt") && args.Count == 1)))
        {
            var listHelper = member is "set" or "set_Item" ? "clrListSet" : "clrListRemoveAt";
            var listCall = (JsonObject)CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", listHelper, OwnerElemArg(ownerFqnNode), args);
            if (RetToken(node) is JsonNode lret && !IsTvType(lret)) listCall["ret"] = lret;
            return listCall;
        }

        // Rule 1c (PRIMITIVE compareTo): `x.compareTo(y)` on a boxed kotlin.<Prim> -> `System.<Prim>.CompareTo`
        // (IComparable<T>). The boxed kotlin.* primitive is NOT emitted in the runtime (it is substituted to the BCL
        // value type), so a member call on the omitted class must route to the BCL value type's CompareTo. This is the
        // bir2cir home of the former kotc primitive-compareTo lowering (layer purity): kotc emits the plain
        // `callInstance kotlin.Int.compareTo`; the primitive->BCL knowledge lives here. Placed BEFORE Rule 3 because a
        // primitive that carries a rule-3 body (Char) would otherwise route to its `dotkt$ClrH_kotlin_Char` helper —
        // WRONG (and self-recursive inside that helper's own body). The 8 signed/bool/char primitives only.
        // The receiver arm ALSO covers a spliced generic `T : Comparable<T>` body whose R concretized to a primitive
        // (maxByOrNull's `maxValue < v`, maxValue: R->System.Int32): the declared owner is the aliased `kotlin.Comparable`
        // (so the owner arm misses), and the fallthrough `IComparable<object>.CompareTo` dispatch is INVALID on an unboxed
        // value type — a native SIGSEGV (Int32 implements IComparable<Int32>/IComparable, never IComparable<object>).
        // Constrainify only rescues a TYPE-VARIABLE receiver; a CONCRETE primitive receiver must bind the struct's own
        // CompareTo(prim) here.
        if (instance && member == "compareTo" && args.Count == 1
            && (CompareToBclTarget(ownerFqn)
                ?? (node["recv"] is JsonObject cmpRecv && RecvStaticType(cmpRecv, ctx, true) is TypeNode.Fqn rf
                    ? CompareToBclTarget(rf.Name) : null)) is string primBcl)
            return new JsonObject
            {
                ["k"] = "clrInstance",
                ["type"] = TypeJson.Fqn(primBcl),
                ["method"] = "CompareTo",
                ["argTypes"] = new JsonArray { TypeJson.Fqn(primBcl) },
                ["ret"] = TypeJson.Fqn("System.Int32"),
                ["recv"] = node["recv"]?.DeepClone(),
                ["args"] = args.DeepClone(),
            };

        // Rule 2: the member carries @ClrIntrinsic -> a direct BCL call.
        if (mappedCompanionRecv != null && exactCompanionBinding.Intrinsic != null)
        {
            var exactIntrinsicCall = ExactIntrinsicCall(node, clrOwner, member, args, instance: false,
                exactCompanionBinding, refs, ctx, ownerToken);
            return exactIntrinsicCall is JsonObject exactIntrinsicObject
                ? CallEvalLowering.PreserveUnreadValueBefore(mappedCompanionRecv, exactIntrinsicObject,
                    $"mapped companion intrinsic '{companionOwnerFqn}.{member}'")
                : exactIntrinsicCall;
        }
        // The frontend has already selected one Kotlin overload. Consume that complete declaration identity here;
        // name+argument-count is not an overload key and lets an intrinsic sibling capture a same-arity real body
        // (for example StringBuilder.append(String?) capturing append(CharSequence?)). The exact binding also owns
        // the byref vector, so the physical call cannot be assembled from facts belonging to two declarations.
        if (hasExactMemberBinding && exactMemberBinding.Intrinsic != null)
        {
            var intrinsicCall = ExactIntrinsicCall(node, clrOwner, member, args, instance,
                exactMemberBinding, refs, ctx, ownerToken);
            return intrinsicCall is JsonObject intrinsicObject
                ? CallEvalLowering.PreserveUnreadValueBefore(mappedCompanionRecv, intrinsicObject,
                    $"mapped companion intrinsic '{companionOwnerFqn}.{member}'")
                : intrinsicCall;
        }

        // Rule 3: a concrete member of a CLR-bound CLASS with NO @ClrIntrinsic carries a real Kotlin body, which
        // AliasHelperHoist lifts to the static helper `dotkt$ClrH_<owner>` (driven by the SAME class binding that brought us here).
        // `IsRule3Member` (ref.dll: the member is concrete + intrinsic-less) is the signal to hoist it; the helper
        // is emitted into the same runtime assembly. NEVER for an INTERFACE owner: an @ClrTypeAlias interface's members
        // are abstract in source (no helper is emitted for it — confirmed: every emitted dotkt$ClrH_* is a class), so
        // its abstract collection members (isEmpty/contains/iterator/...) need the ClrCollectionDefaults routing (Rule 5), not
        // a non-existent helper. (The ref.dll mis-reports these as non-abstract, so IsRule3Member alone false-positives.)
        if (kind != "interface" && KotlinPropertyAccessors.TryCallIdentity(node,
                out var rule3Property, out var rule3Accessor)
            && refs.TryRule3PropertyAccessor(ownerFqn, rule3Property, rule3Accessor,
                out var rule3AccessorMethod))
            return Rule3HelperCall(node, refs, ownerFqnNode, rule3AccessorMethod, args, instance);
        if (kind != "interface" && refs.IsRule3Member(ownerFqn, member))
            return Rule3HelperCall(node, refs, ownerFqnNode, member, args, instance);

        // Rule 3-inherited: the concrete rule-3 body lives on an ANCESTOR, not the static call owner. `printStackTrace`
        // has its real body on kotlin.Throwable but is called through a kotlin.Exception/RuntimeException subclass
        // receiver — IsRule3Member keys on the static owner (Exception) and misses it, so the call would fall through to
        // Rule 4 as a bogus `System.Exception.printStackTrace` (NRE). Walk the `overrides` marker to the CLR-bound
        // non-interface ancestor that actually declares the concrete intrinsic-less body and route to ITS helper; the
        // subclass receiver is assignable to the ancestor-typed __self. Only when the direct owner had no rule-3 match.
        if (kind != "interface" && instance && node["overrides"] is JsonArray ovChain)
            foreach (var o in ovChain)
                if (o is JsonObject oo
                    && TypeJson.OwnerName(oo["owner"]) is string ovOwner
                    && (oo["member"] as JsonValue)?.GetValue<string>() is string ovMember
                    && refs.TryResolveClrOwner(ovOwner, out _, out var ovKind) && ovKind != "interface")
                {
                    var overrideKind = Str(oo["kind"]);
                    var propertyAccessor = overrideKind switch { "getter" => "get", "setter" => "set", _ => null };
                    if (propertyAccessor != null && refs.TryRule3PropertyAccessor(ovOwner, ovMember,
                        propertyAccessor, out var inheritedAccessor))
                        return Rule3HelperCall(node, refs, new TypeNode.Fqn(ovOwner), inheritedAccessor, args, instance);
                    if (propertyAccessor == null && refs.IsRule3Member(ovOwner, ovMember))
                        return Rule3HelperCall(node, refs, new TypeNode.Fqn(ovOwner), ovMember, args, instance);
                }

        // Rule 5m (MAP-interface defaults): Map/MutableMap both alias IDictionary<K,V> (see the stdlib rationale), but
        // most Kotlin map members have no 1:1 IDictionary equivalent — `get` is null-on-missing while get_Item THROWS,
        // put/remove return the previous value, and the keys/values/entries views are Kotlin-typed. Route them to the
        // rt's ClrMapDefaults statics, generic over BOTH type args (the 2-type-arg mirror of CollDefaultCall). Members
        // that DO bind 1:1 (@ClrIntrinsic size/containsKey/clear + MutableMap keys/values) were already renamed to
        // their BCL slot by DeclarationRename and fall through to Rule 4; the defensive get_keys/get_values entries
        // below catch an un-renamed MutableMap accessor call (no overrides metadata) as a direct property read.
        if (instance && kind == "interface" &&
            (ownerFqn == "kotlin.collections.Map" || ownerFqn == "kotlin.collections.MutableMap"))
        {
            // STAR-PROJECTED Map<*,*> (#74a): `get`/`containsKey` on an ALL-erased Map/MutableMap owner would
            // otherwise route to the generic ClrMapDefaultsKt.clrMapGet/clrMapContainsKey helper below, whose FORMAL
            // param is `Map<K,V>` = the INVARIANT generic `IDictionary<object,object>` at this K=V=object
            // instantiation. The real receiver's runtime type (e.g. `Dictionary<String,Int>`) is NOT assignable to
            // that generic instantiation (CLR generics are reified + invariant) even though the helper's BODY
            // immediately re-casts to the covariance-safe NON-generic `IDictionary` facade (`ClrRawDictionary`) —
            // the call BOUNDARY itself throws InvalidCastException before the body ever runs. Skip the generic
            // helper entirely and emit the non-generic call directly: `IDictionary.get_Item`/`.Contains` (both
            // implemented by every `Dictionary<K,V>` regardless of K/V — `IDictionary<K,V> : IDictionary`, so no
            // recv cast is needed). `IDictionary`'s indexer is null-on-missing, matching Kotlin `Map.get` exactly.
            if (FaithfulHints.IsStarProjectedColl(ownerFqnNode) && args.Count >= 1 && member is "get" or "containsKey")
                return new JsonObject
                {
                    ["k"] = "clrInstance",
                    ["type"] = TypeJson.Fqn("System.Collections.IDictionary"),
                    ["method"] = member == "get" ? "get_Item" : "Contains",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") },
                    ["ret"] = TypeJson.Fqn(member == "get" ? "System.Object" : "System.Boolean"),
                    ["recv"] = node["recv"]?.DeepClone(),
                    ["args"] = new JsonArray { args[0].DeepClone() },
                };
            var mutable = ownerFqn == "kotlin.collections.MutableMap";
            var semanticPropertyAccess = Str(node[KotlinPropertyAccessors.KindKey]) ?? Str(node["prop"]);
            var helper = (member, semanticPropertyAccess, args.Count, mutable) switch
            {
                ("get", _, 1, _) => "clrMapGet",
                // size / containsKey are UNBOUND (no @ClrIntrinsic) — a direct Count/ContainsKey reads through the
                // INVARIANT generic IDictionary<K,V> and throws EntryPointNotFound on a value-type-mismatched map (a
                // groupBy result). Route to the covariance-safe non-generic helpers (ICollection.Count / IDictionary
                // .Contains). This also makes mapValues' transitive `mapCapacity(this.size)` covariance-safe.
                ("size", "get", 0, _) => "clrMapSize",
                ("containsKey", _, 1, _) => "clrMapContainsKey",
                ("isEmpty", _, 0, _) => "clrMapIsEmpty",
                ("containsValue", _, 1, _) => "clrMapContainsValue",
                ("getOrDefault", _, 2, _) => "clrMapGetOrDefault",
                ("keys", "get", 0, false) => "clrMapKeys",
                ("values", "get", 0, false) => "clrMapValues",
                ("entries", "get", 0, false) => "clrMapEntries",
                ("entries", "get", 0, true) => "clrMapMutableEntries",
                ("put", _, 2, true) => "clrMapPut",
                ("remove", _, 1, true) => "clrMapRemove",
                ("remove", _, 2, true) => "clrMapRemoveKV",
                ("putAll", _, 1, true) => "clrMapPutAll",
                ("putIfAbsent", _, 2, true) => "clrMapPutIfAbsent",
                ("replace", _, 2, true) => "clrMapReplace",
                ("replace", _, 3, true) => "clrMapReplaceKVV",
                ("merge", _, 3, true) => "clrMapMerge",
                _ => null,
            };
            if (helper != null)
                return MapDefaultCall(node, helper, ownerFqnNode, args, refs, ctx);
            if (mutable && semanticPropertyAccess == "get" && args.Count == 0 && member is "keys" or "values")
                return ClrPropNode(node, clrOwner, member == "keys" ? "Keys" : "Values", ClrPropRead, member, args, "get");
            // else fall through to Rule 4: an already-BCL member name on the aliased IDictionary owner.
        }

        // Rule 5 (collection-interface defaults): the substituted BCL IReadOnly*/I* interfaces lack isEmpty/contains/
        // containsAll/indexOf/lastIndexOf/subList/listIterator/iterator, so an @ClrTypeAlias collection-interface call
        // routes to the rt's ClrCollectionDefaults / ClrIteratorBridge helpers — the bir2cir home of that Kotlin<->CLR
        // relation. The element type is the
        // owner token's first type arg; the helper is generic over it. `kotlin.sequences.Sequence` is ALSO
        // @ClrTypeAlias-ed to IEnumerable (same face) and its sole member `iterator()` vanishes on the BCL interface
        // exactly like the collection interfaces — so route `Sequence.iterator()` through the SAME bridge (the
        // `yieldAll(sequence: Sequence<T>): Unit = yieldAll(sequence.iterator())` self-call in SequenceBuilder).
        else if (instance && kind == "interface"
            && (ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal) || ownerFqn == "kotlin.sequences.Sequence"))
        {
            var elem = OwnerElemArg(ownerFqnNode);
            if (member == "iterator" && args.Count == 0)
            {
                // MutableList's CLR face only exposes IEnumerable<T>.GetEnumerator, which cannot implement Kotlin's
                // MutableIterator.remove contract. The resolved Kotlin owner is authoritative here: route it to the
                // live IList-backed mutable adapter instead of narrowing its declared return to Iterator<T>.
                if (ownerFqn == "kotlin.collections.MutableList")
                    return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt",
                        "clrMutableListIterator", elem, args);
                return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable", elem, args);
            }
            if (member == "listIterator")
            {
                var idx = args.Count >= 1 ? args : new JsonArray { new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = 0 } };
                if (ownerFqn == "kotlin.collections.MutableList")
                    return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt",
                        "clrMutableListListIterator", elem, idx);
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", "clrListListIterator", elem, idx);
            }
            if (CollectionDefaults.TryGetValue(member, out var helperMethod))
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", helperMethod, elem, args);
        }

        // A frontend-resolved property call may reach the general alias rules only while a specialized intrinsic,
        // helper, or exact PropertyInfo/MethodSemantics binding is still able to consume it. Falling through as a plain
        // method would discard the semantic identity and recreate the old get_/set_ convention lookup. Compiler-owned
        // artifacts without the required association are unsupported, so fail at this boundary instead.
        if (hasSemanticProperty)
            throw new InvalidOperationException(
                $"bir2cir: unresolved CLR property accessor '{ownerFqn}.{semanticProperty}' ({semanticAccessor}) — "
                + "the frontend-resolved property has no exact CLR property, intrinsic, or helper binding");

        // Rule 4 (already-resolved CLR member name): earlier bir2cir binding supplies the exact member name — both the
        // universal object/comparable renames (compareTo/equals/hashCode/toString -> CompareTo/Equals/GetHashCode/
        // ToString) and the collection accessors/methods (get_Item/get_Count/Add/set_Item/RemoveAt/Insert/Remove/Clear/
        // GetEnumerator/...). The ref.dll member is kept under its Kotlin name (`get`/`compareTo`), so rules 2/3 miss by
        // name; but the emitted name is already the BCL member, which exists on the alias's BCL type. A BCL name is
        // PascalCase or a get_/set_ accessor (Kotlin members are lowercase camelCase) -> route to clrInstance/clrPropGet
        // on the BCL type. This also rescues the call from the shorthand owner that plain `callInstance` resolution
        // (ilemit ParseOwner / ResolveMethod) cannot handle.
        //
        // MAKE-IT-LOUD gate (H1): a lowercase-camelCase Kotlin member reaching here has no BCL equivalent by that name
        // AND no @ClrIntrinsic/@ClrProperty/rule-3 binding — a genuine routing MISS on either owner kind. It used to be
        // tolerated for an INTERFACE owner because ilemit could still find the member by runtime name lookup; that
        // escape is gone (the Kotlin members without a physical slot are all routed above, to a dispatcher that reaches
        // a Kotlin override through a real interface slot), so both owner kinds refuse at compile time, naming
        // `owner.member`. An owner that does not resolve is not evidence of a miss and is left alone.
        if (instance && kind == "interface") AssertInterfaceMemberRouted(ownerFqn, ownerFqnNode, member, refs);
        if (instance && kind != "interface" && !string.IsNullOrEmpty(member)
            && refs.TryDeclaresAccessibleInstanceMethod(
                ownerFqn, ownerFqnNode?.Args?.Length ?? 0, member, out var declaresMember)
            && !declaresMember)
            throw new InvalidOperationException(
                $"bir2cir: unresolved CLR member '{ownerFqn}.{member}' — the CLR-bound "
                + $"{kind} owner '{ownerToken}' has no @ClrIntrinsic/@ClrProperty/rule-3 binding and is not a BCL member "
                + "declared by that exact name. This is a routing MISS: fix the stdlib binding or the owner alias, "
                + "do not let it fall to a silent runtime dynamic-dispatch NRE.");
        return Constrainify(ClrCallNode(node, clrOwner, member, member, args, instance), node, refs, ctx, ownerToken);
    }

    static (JsonNode Owner, Type Type) ResolveInjectedPropertyOwner(
        ReferenceMetadataIndex refs, TypeNode.Fqn semanticOwner)
    {
        // A generic Kotlin owner's companion-block properties live on the trusted non-generic carrier. Give that
        // explicit declaring identity first refusal before the owner-null top-level property index; otherwise an
        // unrelated top-level property with the same source identity can silently capture the call.
        if (refs.TryGenericStaticCarrier(semanticOwner.Name, out var carrier))
            return (TypeJson.Fqn(carrier), refs.ResolveRefType(carrier, 0));
        return (TypeJson.Write(semanticOwner),
            refs.ResolveRefType(semanticOwner.Name, semanticOwner.Args?.Length ?? 0));
    }

    // Generic-parameter receiver on a CLR-aliased INTERFACE: bir2cir would emit `clrInstance` on the interface owner
    // padded to <object> (ClrOwnerType has no receiver type args to fill), and ilemit's plain `callvirt
    // ICollection<object>::Add` MIS-DISPATCHES — the runtime value (`List<R>`) implements `ICollection<R>`, not <object>,
    // so the JIT finds no slot and throws EntryPointNotFoundException. This is the collection-BUILDING crash:
    // `mapTo`/`filterTo`/`toCollection`'s `destination.add(...)` where `destination: C` and `C : MutableCollection<R>`.
    // Re-express it as constrained dispatch — `constrained. !!C ; callvirt ICollection<R>::Add` — instantiating the
    // interface with the receiver type-parameter's own constraint args (its constraint chain reaches the call owner).
    // Fires ONLY for a local/param receiver whose STATIC type is `gp:X` and whose constraint is a CLR-bound interface;
    // a concrete-class receiver (`ArrayList().add`) already dispatches fine and is left as a plain clrInstance.
    static JsonNode Constrainify(JsonNode built, JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, string ownerToken)
    {
        if (ctx == null || built is not JsonObject call) return built;
        if ((call["k"] as JsonValue)?.GetValue<string>() != "clrInstance") return built;
        if (node["recv"] is not JsonObject recv) return built;
        // The receiver's STATIC type. A local/param -> VarTypes. For a CompareTo call ONLY (the constrained-compareTo
        // case), also recover it from a callInstance receiver's declared return (`retType`/`ret`) or an arrayGet's
        // `elem`: a `gp:X` receiver reached via a member call (`ClosedRange.start.compareTo`) or an array read
        // (`a[i].compareTo`) still needs `constrained.` for value-type-safe dispatch. The collection.add path stays
        // LOCAL-only (unchanged) so broadening the receiver shapes cannot re-route a non-compareTo interface call.
        var isCompareTo = (call["method"] as JsonValue)?.GetValue<string>() == "CompareTo";
        var vt = RecvStaticType(recv, ctx, isCompareTo);
        if (vt is not TypeNode.Tv tvRecv) return built;   // only a generic-parameter receiver needs constrained dispatch
        // The call's declaring owner must itself be a CLR-bound INTERFACE (concrete-class members dispatch fine already).
        if (!refs.TryResolveClrOwner(ownerToken, out var ownerBcl, out var ownerKind) || ownerKind != "interface")
            return built;
        TypeNode[] cargs;
        if (isCompareTo)
        {
            // COMPARETO (System.IComparable): `T : Comparable<T>` means the interface is `IComparable<recvType>` — the
            // arg IS the receiver's own static type.
            cargs = new TypeNode[] { vt };
        }
        else
        {
            // Collection-BUILD (mapTo/filterTo `destination.add`): the element args come from the receiver
            // type-parameter's own collection-interface constraint (`MutableCollection<R>` -> [R]). Requires the
            // constraint to be present on THIS declaration (a local/param receiver of a generic method); local-only.
            if (!ctx.TpConstraints.TryGetValue(tvRecv.Scope + ":" + tvRecv.I, out var cons)) return built;
            cargs = null;
            foreach (var c in cons)
                if (c is TypeNode.Fqn cf && cf.Args != null && refs.TryResolveClrOwner(cf.Name, out _, out var ck)
                    && ck == "interface") { cargs = cf.Args; break; }
            if (cargs == null) return built;
        }

        var cc = new JsonObject
        {
            ["k"] = "constrainedCall",
            ["recvType"] = TypeJson.Write(vt),
            ["iface"] = TypeJson.Write(new TypeNode.Fqn(ownerBcl, cargs)),
            ["method"] = (call["method"] as JsonValue)?.GetValue<string>(),
            ["recv"] = call["recv"]?.DeepClone(),
            ["args"] = (call["args"] as JsonArray)?.DeepClone() ?? new JsonArray(),
        };
        if (call["argTypes"] is JsonArray at) cc["argTypes"] = at.DeepClone();
        if (call["ret"] is JsonNode rv) cc["ret"] = rv.DeepClone();
        return cc;
    }

    // The receiver expression's static type token, for constrained-dispatch recovery. A local/param resolves via
    // VarTypes; for the constrained-COMPARETO case a callInstance receiver's declared return (`retType`/`ret`) and an
    // arrayGet's element (`elem`) also carry it (`ClosedRange.get_start(): T` -> compareTo; `a[i]: T` -> compareTo).
    // null when the shape carries no recoverable static type.
    static TypeNode RecvStaticType(JsonObject recv, SubstCtx ctx, bool allowExprShapes)
    {
        // kotc stamps the Kotlin static type on expression nodes. In particular, a synthesized SAM body reads a
        // captured constrained receiver through `{k:field,sty:C}` rather than a local; that declaration fact is the
        // only sound route back to `C : MutableCollection<T>` / `M : MutableMap<K,V>`.
        if (TypeJson.Read(recv["sty"]) is TypeNode stamped)
            return stamped switch
            {
                TypeNode.Nullable n => n.Of,
                TypeNode.Oblivious o => o.Of,
                _ => stamped,
            };
        var rk = (recv["k"] as JsonValue)?.GetValue<string>();
        if (rk == "local")
            return (recv["name"] as JsonValue)?.GetValue<string>() is string vn
                && ctx.VarTypes.TryGetValue(vn, out var vt) ? vt : null;
        if (!allowExprShapes) return null;
        if (rk == "field")
            return TypeJson.Read(recv["ret"]);
        if (rk == "callInstance")
            return TypeJson.Read(recv["ret"]);
        if (rk == "arrayGet")
            return TypeJson.Read(recv["elem"]);
        return null;
    }

    // The BCL value type whose `CompareTo` a primitive `compareTo` routes to (mirrors the former kotc primitive-compareTo
    // lowering). Accepts every spelling a compareTo owner OR a concrete receiver static type may carry at this pass:
    // the boxed kotlin.<Prim>, the already-lowered System.<Prim>, and the primitive shorthand. null for a non-primitive.
    static string CompareToBclTarget(string name) => name switch
    {
        "kotlin.Int" or "System.Int32" or "int" => "System.Int32",
        "kotlin.Long" or "System.Int64" or "long" => "System.Int64",
        "kotlin.Byte" or "System.SByte" or "sbyte" => "System.SByte",
        "kotlin.Short" or "System.Int16" or "short" => "System.Int16",
        "kotlin.Float" or "System.Single" or "float" => "System.Single",
        "kotlin.Double" or "System.Double" or "double" => "System.Double",
        "kotlin.Char" or "System.Char" or "char" => "System.Char",
        "kotlin.Boolean" or "System.Boolean" or "bool" => "System.Boolean",
        _ => null,
    };

    // The collection ELEMENT type arg for a defaults-helper call: the owner token's own arg
    // (`MutableCollection[gp:R]` -> gp:R), or — when the owner is BARE because the receiver is a generic
    // parameter (`destination: C where C : MutableCollection<R>`) — the receiver's collection-interface
    // constraint's arg (the same recovery Constrainify performs). Falls back to `object`.
    static readonly TypeNode ObjType = new TypeNode.Fqn("object");
    // See through a nullability wrapper (#37/#48): the `in`/`out` variance over-approximation `kotlin.Any` is emitted as
    // the nullable-wrapped `Any?` (`{t:nullable,of:kotlin.Any}`), so the object-ish test on a map/collection owner arg
    // must unwrap it to keep the CollElemArg/MapKvArgs constraint-recovery firing (pre-#48 it saw a bare Fqn `kotlin.Any`).
    static bool IsObjType(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjType(n.Of),
        TypeNode.Oblivious o => IsObjType(o.Of),
        TypeNode.Fqn { Args: null } f => f.Name == "object" || f.Name == "kotlin.Any",
        _ => false,
    };

    static TypeNode CollElemArg(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, TypeNode.Fqn ownerFqn)
    {
        var own = OwnerElemArg(ownerFqn);
        if (!IsObjType(own) || ownerFqn.Args != null) return own;
        // The owner is BARE (`kotlin.collections.MutableCollection`, no type args): the frontend dropped the element
        // when inlining `mapTo`/`filterTo`'s `destination.add(...)`. Recover it from the RECEIVER's declared type.
        if (ctx != null && node["recv"] is JsonObject recv
            && RecvStaticType(recv, ctx, allowExprShapes: true) is TypeNode vt)
        {
            // (a) The receiver is a type-PARAMETER (`destination: C where C : MutableCollection<R>`): its element comes
            // from the collection-interface constraint's arg (the same recovery Constrainify performs).
            if (vt is TypeNode.Tv tvR)
            {
                if (ctx.TpConstraints.TryGetValue(tvR.Scope + ":" + tvR.I, out var cons))
                    foreach (var c in cons)
                        if (c is TypeNode.Fqn cf && cf.Args is { Length: >= 1 } && refs.TryResolveClrOwner(cf.Name, out _, out var ck)
                            && ck == "interface" && cf.Args[0] != null) return cf.Args[0];
            }
            // (b) The receiver is a CONCRETE generic collection local (`__inlN : ArrayList<String>`, mapTo's
            // materialized destination): its OWN first type-arg is the element. Without this the helper's typeArg stays
            // the frontend's `object` over-approximation. Mirrors MapKvArgs' bare-owner recovery.
            else if (vt is TypeNode.Fqn)
            {
                var elem = OwnerElemArg((TypeNode.Fqn)vt);
                if (!IsObjType(elem)) return elem;
            }
        }
        return ObjType;
    }

    // The (K, V) type args for a map-defaults helper call — the two-arg twin of CollElemArg. The owner token's own args
    // (`Map[gp:K,gp:V]`) when present and concrete; otherwise — when the owner is BARE or an OVER-APPROXIMATED position
    // (`MutableMap` bare / `MutableMap[kotlin.Any,V]`, because the receiver is a `gp:M` whose `in K` projection erased the
    // key to Any) — the receiver type-parameter's INVARIANT map-interface constraint (`M : MutableMap[gp:K,gp:V]`). This
    // undoes the variance approximation so `associateWith`/`associateBy`'s `destination.put(..)` emits clrMapPut<K,V>, not
    // <object,object> whose `IDictionary<object,..>::ContainsKey` finds no slot on the runtime dict -> EntryPointNotFound.
    static (TypeNode, TypeNode) MapKvArgs(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, TypeNode.Fqn ownerFqn)
    {
        var (k, v) = OwnerKvArgs(ownerFqn);
        if (!IsObjType(k) && !IsObjType(v)) return (k, v);
        if (ctx != null && refs != null && node["recv"] is JsonObject recv
            && RecvStaticType(recv, ctx, allowExprShapes: true) is TypeNode.Tv tvR
            && ctx.TpConstraints.TryGetValue(tvR.Scope + ":" + tvR.I, out var cons))
        {
            foreach (var c in cons)
            {
                if (c is not TypeNode.Fqn cf || cf.Args is not { Length: >= 2 } || !refs.TryResolveClrOwner(cf.Name, out _, out var ck) || ck != "interface") continue;
                // Only OVERRIDE an over-approximated (object/kotlin.Any) position; a genuinely-concrete owner arg wins.
                if (IsObjType(k) && cf.Args[0] != null) k = cf.Args[0];
                if (IsObjType(v) && cf.Args[1] != null) v = cf.Args[1];
                break;
            }
        }
        return (k, v);
    }

    // Kotlin MUTATION members of an @ClrTypeAlias'd collection interface -> the ClrCollectionDefaults dispatcher that
    // implements them. This map only says WHERE each member is implemented; MEMBERSHIP of the "no physical slot" set
    // is DERIVED from the resolved BCL type (AssertCollectionMemberRouted below), so a member that acquires or loses
    // a BCL slot changes the compiler's behavior without anyone editing a list, and a derived-unbacked member with no
    // entry here is a hard error rather than a silent fall-through.
    static string CollectionSlotHelper(string member, int argCount, string ownerFqn) => (member, argCount) switch
    {
        ("add" or "Add", 1) => "clrCollAdd",
        ("addAll", 1) => "clrCollAddAll",
        ("removeAll", 1) => "clrCollRemoveAll",
        ("retainAll", 1) => "clrCollRetainAll",
        ("addAll", 2) when ownerFqn == "kotlin.collections.MutableList" => "clrListAddAllAt",
        _ => null,
    };

    // The routing above must be COMPLETE for a CLR-bound INTERFACE owner: once this pass is done, no Kotlin-spelled
    // member may reach the BCL owner under a name that owner does not declare. The test is the DERIVED rule, not a
    // name list — ask the resolved physical type whether it declares a member of this exact name. If it does not, an
    // earlier rule was obliged to route the call, and failing to do so used to degrade into a runtime
    // `GetType().GetMethod(name).Invoke(...)` lookup. Refuse at compile time instead, naming owner.member.
    //
    // Scoped to a KOTLIN SPELLING (lowercase-camelCase), which is the same criterion the Rule-4 comment above states:
    // an already-renamed BCL spelling (`MoveNext`, `Add`) legitimately arrives here from an earlier binding and is
    // resolved by name on the BCL owner. A physical accessor name (`get_Count`, `add_Changed`) needs no exclusion of
    // its own — it is only ever produced by a binding that already proved the owner declares it, so the DERIVED test
    // below passes it; this rule never parses an accessor spelling (#397). An owner that does not resolve is not
    // evidence of a miss and is left alone.
    static void AssertInterfaceMemberRouted(string ownerFqn, TypeNode.Fqn ownerFqnNode, string member,
        ReferenceMetadataIndex refs)
    {
        if (!IsKotlinSpelledMember(member)
            || !refs.TryDeclaresAccessibleInstanceMethod(
                ownerFqn, ownerFqnNode?.Args?.Length ?? 0, member, out var declares)
            || declares)
            return;
        throw new InvalidOperationException(
            $"bir2cir: unrouted Kotlin member '{ownerFqn}.{member}' — the CLR interface its @ClrTypeAlias names "
            + $"declares no '{member}', so the member has no physical slot. A Kotlin member in that position must be "
            + "routed to a physical implementation (for the collection interfaces, a kotlin.collections."
            + "ClrCollectionDefaults dispatcher plus the DotKt.Runtime.CompilerServices Kotlin slot interface that "
            + "makes an override reachable). Add the routing or the missing helper; do not let it fall through to a "
            + "member nothing can link.");
    }

    // A Kotlin source spelling as opposed to an already-bound CLR member name: lowercase-camelCase, and not one of
    // the compiler's own `dotkt`-marked synthetics (whose owner is always already resolved).
    static bool IsKotlinSpelledMember(string member) =>
        !string.IsNullOrEmpty(member) && char.IsLower(member[0])
        && !member.StartsWith("dotkt", StringComparison.Ordinal);

    // Kotlin collection-interface member -> the rt ClrCollectionDefaults static (recv-first, generic over elem).
    // iterator() and listIterator() are handled separately (different owner / default index).
    static readonly Dictionary<string, string> CollectionDefaults = new(StringComparer.Ordinal)
    {
        ["isEmpty"] = "clrCollIsEmpty",
        ["contains"] = "clrCollContains",
        ["containsAll"] = "clrCollContainsAll",
        ["indexOf"] = "clrListIndexOf",
        ["lastIndexOf"] = "clrListLastIndexOf",
        ["subList"] = "clrListSubList",
    };

    // A `callStatic <helperOwner>.<helperMethod>(recv, args...)` typed over the collection's element. Mirrors kotc's
    // collDefault emission shape (owner=ClrCollectionDefaultsKt / ClrIteratorBridgeKt, recv prepended, typeArgs=[elem]).
    static JsonNode CollDefaultCall(JsonObject node, string helperOwner, string helperMethod, TypeNode elem, JsonArray args)
    {
        var hargs = new JsonArray();
        if (node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(helperOwner),
            ["method"] = helperMethod,
            ["sig"] = CollectionHelperSig(helperOwner, helperMethod),
            ["args"] = hargs,
            ["typeArgs"] = new JsonArray { TypeJson.Write(elem) },
        };
    }

    // The 2-type-arg map mirror of CollDefaultCall: `callStatic ClrMapDefaultsKt.<helper>(recv, args...)` typed over
    // the map owner token's [K,V] instantiation args.
    static JsonNode MapDefaultCall(JsonObject node, string helperMethod, TypeNode.Fqn ownerFqn, JsonArray args, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        var hargs = new JsonArray();
        if (node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        var (k, v) = MapKvArgs(node, refs, ctx, ownerFqn);
        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn("kotlin.collections.ClrMapDefaultsKt"),
            ["method"] = helperMethod,
            ["sig"] = MapHelperSig(helperMethod),
            ["args"] = hargs,
            ["typeArgs"] = new JsonArray { TypeJson.Write(k), TypeJson.Write(v) },
        };
        // Carry the call's statically-known return (same rationale + `gp:` guard as Rule3HelperCall): a helper
        // returning the BARE map value param (`getOrDefault` -> V) reflects as the callee's own `!!1` at the call
        // site — boxing that out-of-scope token is invalid metadata -> BadImageFormatException at run (both the
        // Map- and MutableMap-typed receivers). `retType` lets ilemit box/convert the concrete instantiation.
        if (RetToken(node) is JsonNode ret && !IsTvType(ret)) call["ret"] = ret;
        return call;
    }

    static JsonArray CollectionHelperSig(string owner, string method)
    {
        var tv = new TypeNode.Tv("method", 0);
        TypeNode Gen(string name) => new TypeNode.Fqn(name, new TypeNode[] { tv });
        var ps = (owner, method) switch
        {
            ("kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable") => new[] { Gen("kotlin.collections.ClrEnumerable") },
            (_, "clrCollAdd") => new TypeNode[] { Gen("kotlin.collections.MutableCollection"), tv },
            (_, "clrCollAddAll" or "clrCollRemoveAll" or "clrCollRetainAll") =>
                new TypeNode[] { Gen("kotlin.collections.MutableCollection"), Gen("kotlin.collections.Collection") },
            (_, "clrListAddAllAt") => new TypeNode[]
                { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int"), Gen("kotlin.collections.Collection") },
            (_, "clrCollContains") => new TypeNode[] { Gen("kotlin.collections.Collection"), tv },
            (_, "clrCollContainsAll") => new TypeNode[] { Gen("kotlin.collections.Collection"), Gen("kotlin.collections.Collection") },
            (_, "clrCollIsEmpty") => new[] { Gen("kotlin.collections.Collection") },
            (_, "clrListSet") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int"), tv },
            (_, "clrListRemoveAt") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrMutableListIterator") => new[] { Gen("kotlin.collections.MutableList") },
            (_, "clrMutableListListIterator") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrListIndexOf" or "clrListLastIndexOf") => new TypeNode[] { Gen("kotlin.collections.List"), tv },
            (_, "clrListListIterator") => new TypeNode[] { Gen("kotlin.collections.List"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrListSubList") => new TypeNode[] { Gen("kotlin.collections.List"), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            _ => throw new InvalidOperationException($"bir2cir: no authored descriptor for collection helper {owner}.{method}"),
        };
        return new JsonArray(ps.Select(TypeJson.Write).ToArray());
    }

    static JsonArray MapHelperSig(string method)
    {
        TypeNode any = new TypeNode.Fqn("kotlin.Any");
        TypeNode k = new TypeNode.Tv("method", 0);
        TypeNode v = new TypeNode.Tv("method", 1);
        TypeNode[] ps = method switch
        {
            "clrMapIsEmpty" or "clrMapSize" or "clrMapKeys" or "clrMapValues" or "clrMapEntries"
                or "clrMapMutableEntries" => new[] { any },
            "clrMapGet" or "clrMapContainsKey" or "clrMapRemove" => new[] { any, k },
            "clrMapContainsValue" => new[] { any, v },
            "clrMapPut" or "clrMapGetOrDefault" or "clrMapRemoveKV" or "clrMapPutIfAbsent" or "clrMapReplace"
                => new[] { any, k, v },
            "clrMapMerge" => new TypeNode[]
            {
                any, k, v,
                new TypeNode.Fn(false, new TypeNode.Fqn("object"), new[] { v, v }, null, "System.Func"),
            },
            "clrMapPutAll" => new[] { any, any },
            "clrMapReplaceKVV" => new[] { any, k, v, v },
            _ => throw new InvalidOperationException($"bir2cir: no authored descriptor for map helper {method}"),
        };
        return new JsonArray(ps.Select(TypeJson.Write).ToArray());
    }

    // The first TWO top-level type arguments of a map owner token (`kotlin.collections.Map[gp:K,gp:V]`); `object` when
    // erased/unbound.
    static (TypeNode, TypeNode) OwnerKvArgs(TypeNode.Fqn ownerFqn)
    {
        var args = ownerFqn.Args;
        return (args is { Length: >= 1 } && args[0] != null ? args[0] : ObjType,
                args is { Length: >= 2 } && args[1] != null ? args[1] : ObjType);
    }

    // The first top-level type argument of an owner Fqn (`kotlin.collections.List<E>` -> E); `object` if none.
    static TypeNode OwnerElemArg(TypeNode.Fqn ownerFqn) =>
        ownerFqn.Args is { Length: >= 1 } args && args[0] != null ? args[0] : ObjType;

    // A bare-@ClrIntrinsic top-level EXTENSION fun: `fn(recv, rest...)` -> `recv.<intrinsic>(rest...)`. The extension
    // receiver is the first arg; the first `sig` type is its (CLR) type, the rest are the method's arg types. ilemit
    // resolves the BCL member on that receiver type (incl. its array-Clone / dynamic-dispatch fallbacks).
    static List<TypeNode> SplitSig(JsonObject node)
    {
        var result = new List<TypeNode>();
        if (node["sig"] is JsonArray arr)
            foreach (var el in arr)
                if (TypeJson.Read(el) is TypeNode tn) result.Add(tn);
        return result;
    }

    // A GENERIC top-level call carries its declared parameter SHAPE in `shapeTypes` (the method-type-var-relative
    // param types) INSTEAD of the concrete `sig`/`argTypes`/`ret` a non-generic sibling gets — kotc emits only the
    // pure-Kotlin overload-matching shape for a generic call. Once such a call is owner-attributed to a referenced
    // file-class (a re-imported cross-module `kotlinx.*`/DotKt Kotlin lib that NetInteropBinding leaves as a plain
    // callStatic, so it never became a `clrGeneric*` node), ilemit's callStatic path resolves the overload via `sig`
    // (SigString -> FindReflectedMethodBySig) and only THEN MakeGenericMethod's it with `typeArgs`. With NO `sig` it
    // drops to the name-only arity pick and MIS-BINDS among a same-name overload set — an arity-2 defaulted sibling
    // (whose non-const default is then passed null), or a sole-generic factory reported "static method not found".
    // Promote `shapeTypes` to `sig` (kept OPEN: a `gp:T` param must match the OPEN generic method, NOT the
    // substituted concrete type), and stamp the concrete `argTypes` (typeArgs substituted for the method type-vars).
    // The call-RESULT type (`ret`) is NOT stamped: ilemit derives it off the resolved+MakeGenericMethod'd method
    // (ApplyTypeArgs), and the ref.dll's declared return for a same-name overload set can't be matched by name+arity
    // alone (an object-erased generic return would mislead). No-op when `sig` is already present or `shapeTypes` absent.
    // Only reached for a REFERENCED (non-local) top-level fun — the caller's `!_localTopLevelFns.Contains(fn)` gate
    // excludes this-module lift-thunk callStatics, which also carry `shapeTypes` but no `typeArgs` (so `methodArgs`
    // would be null); a null `methodArgs` still leaves any `Tv` open, so `argTypes` is harmless even if one slipped in.
    static void PromoteGenericShapeToSig(JsonObject node)
    {
        if (node["sig"] != null || node["shapeTypes"] is not JsonArray shapeTypes) return;
        var methodArgs = (node["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        node["sig"] = shapeTypes.DeepClone();   // OPEN param shapes verbatim (a method-tv stays `gp:T`)
        var argTypes = new JsonArray();
        foreach (var st in shapeTypes)
            if (TypeJson.Read(st) is TypeNode t) argTypes.Add(TypeJson.Write(SubstMethodTv(t, methodArgs)));
        node["argTypes"] = argTypes;            // concrete arg types (method type-vars substituted)
        node.Remove("shapeTypes");              // consumed into sig/argTypes — drop the transient shape carrier
    }

    // Substitute a method-scope type variable `Tv{method,i}` -> `methodArgs[i]` (the call's i-th type argument),
    // recursively through the structured TypeNode. A class-scope tv / an out-of-range index / a null arg is left as-is.
    static TypeNode SubstMethodTv(TypeNode t, TypeNode[] methodArgs) => t switch
    {
        TypeNode.Tv { Scope: "method" } tv when methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length && methodArgs[tv.I] != null => methodArgs[tv.I],
        TypeNode.Fqn { Args: { } fa } f => new TypeNode.Fqn(f.Name, fa.Select(a => SubstMethodTv(a, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstMethodTv(n.Of, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstMethodTv(o.Of, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstMethodTv(a.Elem, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstMethodTv(b.Of, methodArgs)),
        TypeNode.Fn fnv => new TypeNode.Fn(fnv.Suspend, SubstMethodTv(fnv.Ret, methodArgs), fnv.Params.Select(p => SubstMethodTv(p, methodArgs)).ToArray(), fnv.Recv == null ? null : SubstMethodTv(fnv.Recv, methodArgs)),
        _ => t,
    };

    // The receiver-type key of a call's first-arg type (mirrors ReferenceMetadataIndex.RecvKey on the ref.dll side).
    // A specialized primitive-array Fqn maps to "[]" via RecvKeyOfFqn (same collapse the ref-side applies to a real
    // int[]) — see the helper for why generic `Array<T>` already reaches "[]" and the primitive variants need it (#153).
    static string RecvKeyOf(TypeNode sig0) => sig0 switch
    {
        TypeNode.Array => "[]",
        TypeNode.Tv => "gp",
        TypeNode.Nullable n => RecvKeyOf(n.Of),
        TypeNode.ByRef b => RecvKeyOf(b.Of),
        TypeNode.Fqn f => ReferenceMetadataIndex.RecvKeyOfFqn(f.Name),
        _ => "",
    };

    // A CLR-bound owner token's ClrRef-resolvable BCL type: a non-generic alias is its bare BCL FQN ("System.String"
    // -- NOT the "string" shorthand, which ilemit ClrRef can't resolve as a clr* `type`); a generic alias keeps its
    // element args (clrg:<bcl>[<args>], or [object x arity] when the token erased them). Null if not CLR-bound.
    static TypeNode ClrOwnerType(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn)
    {
        if (!refs.TryResolveClrOwner(ownerFqn.Name, out var bcl, out _)) return null;
        var arity = refs.OwnerArity(ownerFqn.Name);
        if (ownerFqn.Args != null || arity > 0)
        {
            // Pad a PARTIALLY-erased arg list to the alias's declared arity (a star-projection `Map<K, *>` reaches here
            // as `kotlin.collections.Map<K>` — 1 of IDictionary's 2 args; ilemit's GenericType would fail to resolve
            // `IDictionary`1`). The trailing/all erased args become `object`.
            var kept = (ownerFqn.Args ?? Array.Empty<TypeNode>()).Where(a => a != null).ToList();
            for (var i = kept.Count; i < arity; i++) kept.Add(ObjType);
            if (kept.Count > 0) return new TypeNode.Fqn(bcl, kept.ToArray());
        }
        return new TypeNode.Fqn(bcl);
    }

    static JsonNode TopLevelExtensionInstance(JsonObject node, ReferenceMetadataIndex refs, string intrinsic, JsonArray args, List<TypeNode> sigParts, SubstCtx ctx)
    {
        if (args.Count == 0) return null;   // no receiver -> not an extension shape; leave for FindStatic to report
        // The extension receiver's CLR owner type. PREFER the receiver EXPRESSION's STRUCTURED static type (from ctx):
        // a param/local typed `MutableCollection<T>` carries the CONCRETE tv element arg (`[tv method 0]`). The legacy
        // sig0 string's `BareOwnerFqn` STRIPS the receiver's type-args, so a generic-collection receiver would resolve
        // to the INVARIANT `ICollection<object>` and mis-dispatch at run (`ICollection<object>::Add` on a runtime
        // `List<string>` -> EntryPointNotFoundException — the stdlib `clrCollNativeAdd`@ClrIntrinsic("Add") crash). The
        // structured receiver keeps `ICollection<gp:T>`. Fall back to the sig0 bare owner when no structured Fqn is
        // recoverable; the receiver `type` slot must be the ClrRef-resolvable BCL Fqn, not the "string" shorthand.
        var sig0 = sigParts.Count > 0 ? sigParts[0] : null;
        TypeNode recvClr = null;
        if (ctx != null && args[0] is JsonObject recv0 && RecvStaticType(recv0, ctx, allowExprShapes: false) is TypeNode.Fqn structRecv
            && ClrOwnerType(refs, structRecv) is TypeNode roStruct)
            recvClr = roStruct;
        else if (sig0 is TypeNode.Fqn sig0f && ClrOwnerType(refs, new TypeNode.Fqn(ReferenceMetadataIndex.BareOwnerFqn(sig0f.Name))) is TypeNode roBare)
            recvClr = roBare;
        JsonNode recvType = recvClr != null
            ? TypeJson.Write(recvClr)
            : (sig0 != null ? TypeJson.Write(sig0) : InferArgType(args[0]));

        var argTypes = new JsonArray();
        for (var i = 1; i < sigParts.Count; i++) argTypes.Add(TypeJson.Write(sigParts[i]));
        var rest = new JsonArray();
        for (var i = 1; i < args.Count; i++) rest.Add(args[i]?.DeepClone());

        var call = new JsonObject
        {
            ["k"] = "clrInstance",
            ["type"] = recvType,
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
            ["recv"] = args[0].DeepClone(),
            ["args"] = rest,
        };
        if (RetToken(node) is JsonNode ret) call["ret"] = ret;
        return call;
    }

    // @ClrProperty(access) flag values (mirror `kotlin.clr.READ`/`WRITE`): a get accessor / a set accessor; `READ|WRITE`
    // (both bits) is a get+set property whose specific call is disambiguated by its explicit role or argument count.
    const int ClrPropRead = 1, ClrPropWrite = 2;

    // Build a clrPropGet/clrPropSet node for a .NET property `prop` on the BCL owner `bcl`. Used by BOTH the explicit
    // @ClrProperty accessor (Rule 2p; `prop` is the bare BCL property "Length") and an exact reflected PropertyInfo
    // binding. `access` = READ/WRITE flags; when both are set, the explicit role or arg count (1 = write)
    // picks the direction. WRITE takes the single value arg; READ carries the return type. On the STATIC axis a non-null
    // `propMarker` ("get"/"set", #78/#81) OVERRIDES the arg-count heuristic (it encodes the accessor kind explicitly),
    // and a leading `__self` extension-receiver arg makes the accessor an INSTANCE property on __self (WRITE value = args[^1]).
    static JsonNode ClrPropNode(JsonObject node, TypeNode clrOwner, string prop, int access, string member, JsonArray args,
        string propMarker = null, bool forceStatic = false)
    {
        var wantRead = (access & ClrPropRead) != 0;
        var wantWrite = (access & ClrPropWrite) != 0;
        // #81: the STATIC-axis `"prop":"get"/"set"` marker encodes the accessor KIND explicitly — trust it over the
        // `args.Count == 1` heuristic, which mis-reads an EXTENSION getter's lone `__self` arg (count 1) as a WRITE.
        // The heuristic stays for the instance axis (no marker), where args are pure value args.
        var explicitKind = propMarker;
        if (explicitKind == null && KotlinPropertyAccessors.TryCallIdentity(node, out _, out var preservedKind))
            explicitKind = preservedKind;
        var write = explicitKind is "get" or "set"
            ? explicitKind == "set"
            : wantRead && wantWrite
                ? args.Count == 1
                : wantWrite;
        // #81: a STATIC EXTENSION property accessor prepends its extension receiver as the LEADING arg (getter
        // `[__self]`; setter `[__self, value]`) rather than in node["recv"]. Detect it by arg count past the
        // direction the marker fixed (getter with 1 arg / setter with 2 args carries a `__self`) — it becomes the
        // .NET receiver, so the accessor is an INSTANCE property on `__self`, not a static.
        var staticAxis = forceStatic || Str(node["k"]) == "callStatic";
        var extRecv = staticAxis && propMarker is ("get" or "set") && args.Count > (write ? 1 : 0)
            ? args[0] : null;
        var pg = new JsonObject
        {
            ["k"] = write ? "clrPropSet" : "clrPropGet",
            ["type"] = TypeJson.Write(clrOwner),
            ["name"] = prop,
            // A marker-bound static computed property (no __self) is a genuine STATIC accessor; an extension binds
            // on __self (instance); the instance axis (no marker) stays instance.
            ["static"] = staticAxis && propMarker != null && extRecv == null,
            ["recv"] = (extRecv ?? node["recv"])?.DeepClone(),
        };
        if (!write && RetToken(node) is JsonNode ret) pg["ret"] = ret;
        // Carry the frontend static-type stamp (#122) so a LATE consumer (StringCharSequenceBridge) recovers the
        // property's type even when it is non-generic (no `ret`) — e.g. a String-typed getter feeding a CharSequence slot.
        if (!write && node["sty"] is JsonNode pgSty) pg["sty"] = pgSty.DeepClone();
        // WRITE value = the LAST arg (past a leading `__self` on the extension axis); args[0] on the instance/plain axis.
        if (write && args.Count >= 1) pg["value"] = (extRecv != null ? args[^1] : args[0]).DeepClone();
        // Carry the `super` (non-virtual) marker (issue #14) onto the substituted accessor so ilemit emits a
        // non-virtual `call` to the base accessor slot — an INSTANCE super.prop only (a static prop has no `super`).
        if (node["super"] is JsonNode supProp && (propMarker == null || extRecv != null)) pg["super"] = supProp.DeepClone();
        return pg;
    }

    // A clrInstance / clrStatic node. A call carrying explicit Kotlin property identity emits clrPropGet/clrPropSet on
    // the resolved bare CLR Property name; otherwise this is a plain method call. A standalone function bound to a
    // property is routed explicitly by @ClrProperty (Rule 2p) before this node is built.
    // Prefix `byref:` onto the argTypes at each @ClrRefArgument position (idempotent), so ilemit resolves the `ref`/`out`
    // BCL overload and emits the address-load for that arg (the byref shape a `ref`/`out` parameter needs).
    static void WrapByref(JsonArray argTypes, int[] byrefPositions)
    {
        if (byrefPositions == null) return;
        foreach (var i in byrefPositions)
        {
            if (i < 0 || i >= argTypes.Count) continue;
            // A structured arg type -> ByRef(inner). Every argType is a `{t:…}` node (#48); the legacy `byref:`
            // sig-string form is retired. Idempotent (an already-ByRef inner is left alone).
            if (TypeJson.Read(argTypes[i]) is TypeNode tn && tn is not TypeNode.ByRef)
                argTypes[i] = TypeJson.Write(new TypeNode.ByRef(tn));
        }
    }

    static JsonNode ClrCallNode(JsonObject node, TypeNode clrOwner, string intrinsic, string member, JsonArray args,
        bool instance, int[] byrefPositions = null, JsonArray exactArgTypes = null)
    {
        var argTypes = exactArgTypes?.DeepClone() as JsonArray ?? InferArgTypes(node, args);
        WrapByref(argTypes, byrefPositions);
        var ret = RetToken(node);

        // A genuine property call carries the source identity and get/set role explicitly. The resolved intrinsic is
        // the bare CLR Property name (for example "Length"), so it becomes the clrProp name verbatim.
        if (instance && KotlinPropertyAccessors.TryCallIdentity(node, out _, out var accessorKind)
            && args.Count == (accessorKind == "set" ? 1 : 0))
            return ClrPropNode(node, clrOwner, intrinsic,
                accessorKind == "set" ? ClrPropWrite : ClrPropRead, member, args, accessorKind);

        var call = new JsonObject
        {
            ["k"] = instance ? "clrInstance" : "clrStatic",
            ["type"] = TypeJson.Write(clrOwner),
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
        };
        if (ret != null) call["ret"] = ret;
        // Carry the frontend static-type stamp (#122) so a LATE consumer recovers a non-generic (ret-less) call's
        // return type — e.g. a String-returning BCL/app call feeding the CharSequence bridge.
        if (node["sty"] is JsonNode callSty) call["sty"] = callSty.DeepClone();
        if (instance) call["recv"] = node["recv"]?.DeepClone();
        call["args"] = args.DeepClone();
        // Carry the `super` (non-virtual) marker (issue #14) onto the substituted clrInstance so ilemit emits a
        // non-virtual `call` to the base slot (like C#'s `base.M()`) instead of a `callvirt` that would re-dispatch to
        // THIS class's override -> infinite recursion. A super call is always instance; never stamp it on a clrStatic.
        if (instance && node["super"] is JsonNode superFlag) call["super"] = superFlag.DeepClone();
        // Thread the source call's generic type arguments onto the substituted clr call. A generic Kotlin
        // @ClrIntrinsic method (`fun <T> Array<T>.nativeFill(...)`) binds to a generic BCL method
        // (`System.Array.Fill<T>(T[],T,int,int)`); ilemit needs the type args to MakeGenericMethod the resolved
        // definition (else it emits an OPEN generic MethodSpec -> "method/type not fully instantiated" at run,
        // the windowed/RingBuffer.removeFirst -> _ArraysKt.fill -> Array.Fill NRE). ilemit instantiates ONLY when
        // the resolved BCL method is itself a generic DEFINITION, so threading these onto a call whose target is
        // non-generic (nativeClone -> Array.Clone) is a harmless no-op there.
        if (node["typeArgs"] is JsonArray callTypeArgs && callTypeArgs.Count > 0)
            call["typeArgs"] = callTypeArgs.DeepClone();
        CoerceCharSeqArgsToString(argTypes, call["args"] as JsonArray);
        return call;
    }

    // Build one exact member-intrinsic call, applying any argument-shape adapter owned by that SAME declaration.
    // @ClrCountFromExclusiveEnd marks an end-index slot whose CLR target takes a count. Rewriting it to `end-start`
    // gives `start` a second reader, so the original receiver/arguments are first represented as one ordered binding
    // plan and materialised by the canonical call-evaluation logic. This preserves Kotlin order and single evaluation
    // for arbitrary expressions; it is metadata-driven and does not recognize a library or member name.
    static JsonNode ExactIntrinsicCall(JsonObject node, TypeNode clrOwner, string member, JsonArray args, bool instance,
        ExactClrMemberBinding binding, ReferenceMetadataIndex refs, SubstCtx ctx, string ownerToken)
    {
        if (binding.CountStart < 0 && binding.CountEnd < 0)
            return Constrainify(ClrCallNode(node, clrOwner, binding.Intrinsic, member, args, instance,
                binding.ByrefPositions), node, refs, ctx, ownerToken);
        if (binding.CountStart < 0 || binding.CountEnd <= binding.CountStart || binding.CountEnd >= args.Count)
            throw new InvalidDataException(
                $"invalid CLR count adapter on {ownerToken}.{member}: start={binding.CountStart}, "
                + $"end={binding.CountEnd}, args={args.Count}");

        var bindings = new JsonArray();
        JsonObject Bind(JsonNode expression, JsonNode type, bool address = false)
        {
            var id = CallEvalLowering.FreshBindingId();
            var item = new JsonObject
            {
                ["id"] = id,
                ["expr"] = expression?.DeepClone(),
                ["stable"] = ValueStability.IsReReadable(expression),
            };
            if (address) item["kind"] = "address";
            if (type != null) item["type"] = type.DeepClone();
            bindings.Add(item);
            return new JsonObject { ["k"] = "bindRef", ["id"] = id };
        }

        JsonObject recvRef = null;
        if (instance)
            recvRef = Bind(node["recv"], TypeJson.Write(clrOwner));
        // Normalize physical BCL-boundary arguments BEFORE they enter the evaluation plan. In particular, a semantic
        // CharSequence argument is represented by a String snapshot at this boundary; materialising the semantic
        // value first would create a dotkt$CharSequence temp even when the source local has already collapsed to
        // System.String. The normalized argTypes travel into ClrCallNode so it does not reconstruct the old semantic
        // vector and wrap the plan reader a second time.
        var exactArgTypes = InferArgTypes(node, args);
        var physicalArgs = args.DeepClone() as JsonArray ?? new JsonArray();
        CoerceCharSeqArgsToString(exactArgTypes, physicalArgs);
        var adaptedArgs = new JsonArray();
        for (var i = 0; i < physicalArgs.Count; i++)
        {
            // The exact, boundary-normalized parameter vector is also the truthful type of a value materialised for
            // that slot. It already accounts for representation changes such as CharSequence -> String; consulting
            // the expression first can recover an older frontend `sty` and recreate the semantic type after the
            // physical boundary has deliberately replaced it. Fall back to expression typing only if the exact
            // vector is unavailable (defensive for malformed/incomplete BIR; exact metadata normally supplies it).
            TypeNode actualType = null;
            if (physicalArgs[i] is JsonObject expression)
            {
                if (Str(expression["k"]) == "local" && Str(expression["name"]) is string localName)
                    ctx?.VarTypes.TryGetValue(localName, out actualType);
                actualType ??= RecvStaticType(expression, ctx, allowExprShapes: true);
            }
            actualType ??= CallEvalLowering.StaticTypeOf(physicalArgs[i]);
            var bindingType = i < exactArgTypes.Count
                ? exactArgTypes[i]
                : actualType is TypeNode type ? TypeJson.Write(type) : null;
            adaptedArgs.Add(Bind(physicalArgs[i], bindingType, binding.ByrefPositions.Contains(i)));
        }
        adaptedArgs[binding.CountEnd] = new JsonObject
        {
            ["k"] = "binOp",
            ["op"] = "-",
            ["lhs"] = adaptedArgs[binding.CountEnd].DeepClone(),
            ["rhs"] = adaptedArgs[binding.CountStart].DeepClone(),
        };

        var callSource = (JsonObject)node.DeepClone();
        if (instance) callSource["recv"] = recvRef;
        var lowered = Constrainify(
            ClrCallNode(callSource, clrOwner, binding.Intrinsic, member, adaptedArgs, instance,
                binding.ByrefPositions, exactArgTypes),
            node, refs, ctx, ownerToken);
        var (stmts, replacements) = CallEvalLowering.Materialise(
            bindings, new List<JsonNode> { lowered }, $"@ClrCountFromExclusiveEnd call {ownerToken}.{member}");
        var result = CallEvalLowering.Substitute(lowered, replacements);
        AssertNoPlanVocabulary(result);
        if (stmts.Count == 0) return result;
        var block = new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = stmts,
            ["result"] = result,
        };
        if (RetToken(node) is JsonNode ret) block["type"] = ret;
        return block;
    }

    // A synthetic-CharSequence (`dotkt$CharSequence`) value flowing as an ARGUMENT into a substituted BCL call has NO
    // BCL overload: `Appendable.append(CharSequence)` binds to `System.Text.StringBuilder.Append`, and ilemit — finding
    // no `Append(dotkt$CharSequence)` slot — mis-selects `Append(String)` and marshals the interface reference as a raw
    // string pointer, corrupting memory ("Destination is too short" / AccessViolationException inside joinTo/
    // joinToString). The CLR has no representation for kotc's monomorphic CharSequence interface at a BCL boundary, so any
    // CharSequence reaching one must be snapshot to System.String (its `.toString()` content). Convert the arg to a
    // null-safe `Any?.toString()` (kotlin.LibraryKt.toString) and pin the argType to `kotlin.String` (BirTypeLowering ->
    // System.String) so the overload binds cleanly. Runs in EVERY non-ref build: the rt-stdlib's OWN joinTo/joinToString
    // bodies keep the synthetic CharSequence params (CharSeqStringLowering is app-only), so this is the sole marshaling
    // point for their `buffer.append(separator/prefix/postfix/truncated)` calls.
    static void CoerceCharSeqArgsToString(JsonArray argTypes, JsonArray args)
    {
        if (argTypes == null || args == null) return;
        for (var i = 0; i < argTypes.Count && i < args.Count; i++)
            if (IsSyntheticCharSeqToken(argTypes[i]) && args[i] is JsonNode a)
            {
                args[i] = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"),
                    ["method"] = "toString",
                    ["sig"] = new JsonArray { TypeJson.Fqn("object") },
                    ["args"] = new JsonArray { a.DeepClone() },
                };
                argTypes[i] = TypeJson.Fqn("kotlin.String");
            }
    }

    // True iff an argType slot denotes kotc's synthetic monomorphic
    // `dotkt$CharSequence` interface (tolerating a `nullable`/`oblivious` decoration — a `CharSequence?`/`CharSequence!`
    // param, e.g. `StringBuilder.append(CharSequence?, start, end)`, must ALSO snapshot to String at the BCL boundary,
    // else the arg reaches a BCL call whose overloads are (Char[]|String|StringBuilder)-typed and none binds it). The
    // `dotkt$StringCharSequence` adapter deliberately does NOT match — its token has no `dotkt$CharSequence` substring.
    static bool IsSyntheticCharSeqToken(JsonNode slot)
    {
        var name = (UnwrapNullableOblivious(TypeNode.Parse(slot.ToJsonString())) as TypeNode.Fqn)?.Name;
        return name != null && name.Contains("dotkt$CharSequence", StringComparison.Ordinal);
    }

    static TypeNode UnwrapNullableOblivious(TypeNode t) => t switch
    {
        TypeNode.Nullable n => UnwrapNullableOblivious(n.Of),
        TypeNode.Oblivious o => UnwrapNullableOblivious(o.Of),
        _ => t,
    };

    // Rule-3: route to `dotkt$ClrH_<owner>.<member>(recv?, args..)`. The receiver is threaded as the helper's
    // first argument (the hoisted static's `__self`); type args are carried through when present.
    //
    // GENERIC alias owner: the hoisted helper declares the alias CLASS's type params FIRST, then the method's own
    // (HoistMethod -> MergeTypeParams order), so the call must instantiate the helper with the receiver's static-type
    // args (from the `ownerType` token, padded with `object` when erased) AHEAD of the method's own typeArgs.
    // Copying only node["typeArgs"] left the helper OPEN for a concrete generic receiver
    // (`HashMap<String,Int>().put(..)` -> an open-generic callStatic -> InvalidProgramException at run), and the bare
    // ownerFqn sig slot lowered to the degenerate NON-generic BCL type (`clr:System...Dictionary`) — carry the
    // instantiated token so the `__self` slot and the helper type args agree.
    static JsonNode Rule3HelperCall(JsonObject node, ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn, string member, JsonArray args, bool instance)
    {
        var ownerName = ownerFqn.Name;
        var hargs = new JsonArray();
        if (instance && node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());

        // The alias class's instantiation args, padded to its declared arity (a bare/partially-erased owner — a raw or
        // star-projected receiver — degrades to `object`, same as ClrOwnerType). Empty for a non-generic alias.
        var classArgs = (ownerFqn.Args ?? Array.Empty<TypeNode>()).Where(a => a != null).ToList();
        var arity = refs.OwnerArity(ownerName);
        for (var i = classArgs.Count; i < arity; i++) classArgs.Add(ObjType);

        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(ReferenceMetadataIndex.HelperTypeName(ownerName)),
            ["method"] = member,
            ["args"] = hargs,
        };
        // The helper is instantiated with the alias class's args FIRST, then the method's own typeArgs (structured).
        var typeArgs = new JsonArray();
        foreach (var ca in classArgs) typeArgs.Add(TypeJson.Write(ca));
        if (node["typeArgs"] is JsonArray ta) foreach (var t in ta) typeArgs.Add(t?.DeepClone());
        if (typeArgs.Count > 0) call["typeArgs"] = typeArgs;
        // Carry the HOISTED DECLARATION signature, not a constructed call-site projection. AliasHelperHoist moves the
        // alias class's type params ahead of the member's own params onto one static helper method, so class-scoped !i
        // becomes method-scoped !!i and the member's existing !!i is offset by the class arity. The receiver slot is
        // likewise the open helper declaration (`OrderedDictionary<!!0,!!1>`), independent of this call's concrete
        // typeArgs. This keeps CIR as the exact physical descriptor ilemit links.
        var sigParts = new JsonArray();
        if (instance && node["recv"] != null)
            sigParts.Add(TypeJson.Write(arity > 0
                ? new TypeNode.Fqn(ownerName, Enumerable.Range(0, arity)
                    .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray())
                : new TypeNode.Fqn(ownerName)));
        if (node["sig"] is JsonArray origSig)
            foreach (var p in origSig)
                sigParts.Add(TypeJson.Read(p) is TypeNode pt
                    ? TypeJson.Write(RemapHoistedTypeVars(pt, arity))
                    : p?.DeepClone());
        // `sig` may be LONGER than args (omitted defaulted params, filled downstream) — the bridge matches
        // positionally from the left; only a SHORTER sig would misalign.
        if (sigParts.Count >= hargs.Count) call["sig"] = sigParts;
        // Carry the call's statically-known return: a helper returning the alias class's BARE type param
        // (`ArrayList<Int>.removeAt` -> E) reflects as the callee's own `!!n` at the call site, and boxing that
        // out-of-scope token is invalid IL (BadImageFormat); ilemit's RetOr/CoerceReturn recover the concrete type
        // from `retType` (same channel the erased nullable-generic return conversion reads). NEVER a bare `gp:`
        // token (an open call site inside another generic body): it buys no conversion there, and when the callee's
        // return is the ERASED nullable-generic `object`, CoerceReturn would `unbox.any !!X` a possibly-null —
        // NullReferenceException for a value instantiation. The open representation of such a value stays `object`.
        if (RetToken(node) is JsonNode ret && !IsTvType(ret)) call["ret"] = ret;
        return call;
    }

    static TypeNode RemapHoistedTypeVars(TypeNode type, int classArity) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => new TypeNode.Tv("method", tv.I),
        TypeNode.Tv { Scope: "method" } tv => new TypeNode.Tv("method", classArity + tv.I),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(a => RemapHoistedTypeVars(a, classArity)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(RemapHoistedTypeVars(n.Of, classArity)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(RemapHoistedTypeVars(o.Of, classArity)),
        TypeNode.Array a => new TypeNode.Array(RemapHoistedTypeVars(a.Elem, classArity)),
        TypeNode.ByRef b => new TypeNode.ByRef(RemapHoistedTypeVars(b.Of, classArity)),
        TypeNode.Fn fn => new TypeNode.Fn(
            fn.Suspend,
            RemapHoistedTypeVars(fn.Ret, classArity),
            fn.Params.Select(p => RemapHoistedTypeVars(p, classArity)).ToArray(),
            fn.Recv == null ? null : RemapHoistedTypeVars(fn.Recv, classArity),
            fn.Clr),
        _ => type,
    };

    // The call's parameter types, used as the clr* argTypes overload key. Prefer kotc's structured `sig`;
    // else infer each arg's own type token; else empty. Left in the kotlin.* vocabulary —
    // BirTypeLowering lowers `argTypes` afterwards.
    static JsonArray InferArgTypes(JsonObject node, JsonArray args)
    {
        // Prefer kotc's `sig` (the STRUCTURED TypeNode array of param types, #37 m3b); else infer each arg's own
        // STRUCTURED type. Either form is a valid clr* argTypes overload-key entry.
        var result = new JsonArray();
        if (node["sig"] is JsonArray sig && sig.Count > 0)
        {
            foreach (var p in sig) result.Add(p?.DeepClone());
            if (result.Count == args.Count) return result;
            result = new JsonArray();
        }
        foreach (var a in args) result.Add(InferArgType(a));
        return result;
    }

    // The structured return-type slot of a call node (dynRet/retType/ret), cloned; null when absent.
    static JsonNode RetToken(JsonObject node)
    {
        foreach (var key in new[] { "dynRet", "ret" })
            if (node[key] is JsonNode n && TypeJson.Read(n) is TypeNode) return n.DeepClone();
        return null;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    // A ret slot is an UNBOUND type parameter (`Tv`) — the guard on carrying a `retType` hint (an open `gp:` token
    // buys no conversion at the call site and, when the callee return is object-erased, would unbox.any a null).
    static bool IsTvType(JsonNode slot) => TypeJson.Read(slot) is TypeNode.Tv;

    // An expression's own STRUCTURED type (its type/ret slot), cloned; Fqn("object") when none is recoverable.
    static JsonNode InferArgType(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in new[] { "type", "ret", "suspendRet", "dynRet" })
                if (obj[key] is JsonNode n && TypeJson.Read(n) is TypeNode) return n.DeepClone();
        return TypeJson.Fqn("object");
    }

}
