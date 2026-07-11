using System;
using System.Collections.Generic;
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
        ["kotlin.UByteArray"] = "kotlin.Byte", ["kotlin.UShortArray"] = "kotlin.Short",
        ["kotlin.UIntArray"] = "kotlin.Int", ["kotlin.ULongArray"] = "kotlin.Long",
    };

    // A star-projection/erased type-arg token: `object`/`kotlin.Any`, possibly nullable/oblivious-wrapped (a star K/V
    // projects to `Any?`, i.e. `{t:nullable,of:kotlin.Any}` post-#48). Used by the Map<*,*> extension guard (#74a).
    static bool IsErasedAny(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsErasedAny(n.Of),
        TypeNode.Oblivious o => IsErasedAny(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true,
        _ => false,
    };

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTopLevelFns, bool attributeTopLevelOwner)
    {
        _localTopLevelFns = localTopLevelFns;
        _attributeTopLevelOwner = attributeTopLevelOwner;
        return Rewrite(root, refs, new SubstCtx());
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
        // DisambiguateShadowedVars intent (a same-name local of a different type is a distinct binding).
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
        return (node["k"] as JsonValue)?.GetValue<string>() switch
        {
            "new" => TransformNew(node, refs) ?? node,
            "callInstance" => TransformCall(node, refs, instance: true, ctx) ?? node,
            "callStatic" => TransformCall(node, refs, instance: false, ctx) ?? node,
            "staticField" => TransformStaticField(node, refs) ?? node,
            "field" => TransformStorageField(node) ?? node,
            _ => node,
        };
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
            // FACADEGEN-INJECTED .NET owner (A2 tail / #73 M4 newClr): kotc emits a plain `new` by the .NET-FQN
            // identity (it no longer decides the ctor SHAPE); the newClr decision moves HERE, resolved off the loaded
            // refs — the exact axis NetInteropBinding uses for an injected .NET CALL. Keep the .NET-FQN name verbatim
            // (an arity-qualified `Task`1`/nested `Outer+Inner` injected name diverges from its Kotlin ClassId name, so
            // it must ride through unchanged — do NOT re-derive it from a Kotlin type token). No struct/enum skip: a
            // .NET struct ctor is a valid `newobj`, and kotc emitted newClr for an injected struct too (parity). Also
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
        // at run. Drop the trailing loadFactor arg (and its declared argType) so the overload key becomes a bare (int).
        // Gated on a @ClrTypeAlias owner whose declared 2nd ctor param is a Float — the loadFactor idiom is unique to
        // the stdlib collection aliases (no BCL type reaching here has a genuine (int, float) ctor).
        if (args.Count == 2 && refs.Aliases.ContainsKey(ownerFqn.Name)
            && node["argTypes"] is JsonArray dat && dat.Count == 2 && IsFloatArg(dat[1]))
        {
            args = new JsonArray { args[0].DeepClone() };
            node["argTypes"] = new JsonArray { dat[0].DeepClone() };
        }

        var newClrArgTypes = CtorArgTypes(node, args, refs, ownerFqn.Name);
        var newClrArgs = (JsonArray)args.DeepClone();
        // M10 coercion applies ONLY to an @ClrTypeAlias owner (the alias route). A BCL type can never declare a
        // `kotlin.CharSequence`/`dotkt$CharSequence` ctor param, and a REFERENCED KOTLIN library class reached through
        // the injected-owner fallback (`new mylib.W(cs: CharSequence)`) DOES — coercing there would corrupt its real
        // `dotkt$CharSequence` param (its compiled ctor takes the adapter, not String). The M10 target
        // `kotlin.text.StringBuilder` is a @ClrTypeAlias, so it always resolves via the alias route.
        if (viaAlias) CoerceCharSequenceCtorArgs(newClrArgs, newClrArgTypes);
        return new JsonObject
        {
            ["k"] = "newClr",
            ["type"] = TypeJson.Write(typeNode),
            ["argTypes"] = newClrArgTypes,
            ["args"] = newClrArgs,
        };
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
    // capacity ctor (a bare `object`/unbound-`gp:E` argType matches neither, so ilemit mis-picked `List(int)` ->
    // InvalidProgramException). Falls back to InferArgTypes when the node has no declared argTypes (older shape).
    // The 2nd ctor arg is a Float (the JVM loadFactor idiom) — read the structured argType (with a legacy-string fallback).
    static bool IsFloatArg(JsonNode n)
    {
        if (TypeJson.Read(n) is TypeNode.Fqn { Args: null } f) return f.Name is "kotlin.Float" or "float";
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s is "kotlin.Float" or "float";
        return false;
    }

    static JsonArray CtorArgTypes(JsonObject node, JsonArray args, ReferenceMetadataIndex refs, string ownerToken)
    {
        if (node["argTypes"] is not JsonArray declared || declared.Count != args.Count)
            return InferArgTypes(node, args);
        var map = ClassTypeParamMap(refs, ownerToken);
        var result = new JsonArray();
        foreach (var a in declared)
        {
            var s = (a as JsonValue)?.GetValue<string>();
            result.Add(s == null ? a?.DeepClone() : SubstituteGenericParams(s, map));
        }
        return result;
    }

    // Positional map from a generic owner token's class type-param NAMES (from the ref.dll) to its instantiation args:
    // `kotlin.collections.ArrayList[kotlin.Int]` + names [E] => { "E" -> "kotlin.Int" }. Empty when the owner is
    // non-generic, unbound, or the ref.dll has no param names for it.
    static Dictionary<string, string> ClassTypeParamMap(ReferenceMetadataIndex refs, string ownerToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var br = ownerToken.IndexOf('[');
        if (br < 0 || !ownerToken.EndsWith("]", StringComparison.Ordinal)) return map;
        var names = refs.OwnerTypeParamNames(ReferenceMetadataIndex.BareOwnerFqn(ownerToken));
        if (names == null || names.Length == 0) return map;
        var targs = SplitTopLevel(ownerToken[(br + 1)..^1]).ToList();
        for (var i = 0; i < names.Length && i < targs.Count; i++) map[names[i]] = targs[i];
        return map;
    }

    // Replace each `gp:<name>` type token (a class type parameter) with its instantiation type, leaving unrelated
    // generic params (a METHOD's own gp:T/gp:R, absent from the class map) untouched. Word-boundary-safe: a gp name is
    // an identifier terminated by `[`, `]`, `,`, or end.
    static string SubstituteGenericParams(string type, Dictionary<string, string> map)
    {
        if (map.Count == 0 || !type.Contains("gp:", StringComparison.Ordinal)) return type;
        var sb = new System.Text.StringBuilder(type.Length);
        for (var i = 0; i < type.Length;)
        {
            if (i + 3 <= type.Length && type[i] == 'g' && type[i + 1] == 'p' && type[i + 2] == ':')
            {
                var j = i + 3;
                while (j < type.Length && (char.IsLetterOrDigit(type[j]) || type[j] == '_')) j++;
                var name = type[(i + 3)..j];
                if (map.TryGetValue(name, out var repl)) { sb.Append(repl); i = j; continue; }
            }
            sb.Append(type[i]); i++;
        }
        return sb.ToString();
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

        if (refs.CollectionFactoryKind(fn) is string collKind)
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
                return new JsonObject { ["k"] = "newMap", ["keyType"] = kt.DeepClone(), ["valType"] = vt.DeepClone(), ["entries"] = entries };
            }
            var elemT = TypeArgAt(typeArgs, 0);
            if (elemT == null) return null;                                     // can't reconstruct elem -> plain call
            var elems = new JsonArray();
            foreach (var el in FactoryElems(args, elemT)) elems.Add(el.DeepClone());
            return new JsonObject { ["k"] = collKind == "set" ? "newSet" : "newList", ["elem"] = elemT.DeepClone(), ["elems"] = elems };
        }

        if (refs.ArrayFactoryKind(fn) is string arrKind)
        {
            if (arrKind == "sized")                                             // arrayOfNulls<T>(size) -> newArraySized
            {
                var elemT = TypeArgAt(typeArgs, 0);
                if (elemT == null || args.Count < 1) return null;
                return new JsonObject { ["k"] = "newArraySized", ["elem"] = elemT.DeepClone(), ["size"] = args[0].DeepClone() };
            }
            // "vararg": arrayOf<T>(...) / intArrayOf(...) -> newArray. kotc emits the vararg as a single `newArray` arg
            // (an EMPTY vararg is dropped -> args=[]). The elem source, in precedence: typeArgs[0] (the generic
            // arrayOf<T>, reliable even when empty) -> the vararg wrapper's own elem (concrete primitive intArrayOf/…
            // NON-empty) -> the ref.dll return-type hint (concrete primitive, EMPTY call). The elements come from the
            // wrapper, or none when the vararg was dropped.
            var wrapper = args.Count == 1 && args[0] is JsonObject w && (w["k"] as JsonValue)?.GetValue<string>() == "newArray" ? w : null;
            var arrElem = TypeArgAt(typeArgs, 0) ?? wrapper?["elem"]
                ?? (refs.ArrayFactoryElemHint(fn) is string hint ? TypeJson.Fqn(hint) : null);
            if (arrElem == null) return null;                                   // no element source -> plain call
            var arrElems = new JsonArray();
            foreach (var el in (wrapper?["elems"] as JsonArray) ?? new JsonArray()) arrElems.Add(el.DeepClone());
            return new JsonObject { ["k"] = "newArray", ["elem"] = arrElem.DeepClone(), ["elems"] = arrElems };
        }
        return null;
    }

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
            // #81: a top-level EXTENSION-property accessor (C7: `val List<T>.lastIndex`, `val Int.absoluteValue`)
            // carries the bare property IDENTITY + a `"prop":"get"/"set"` marker instead of a baked `get_`/`set_`
            // slot name (extending the #78 static-axis convention to the owner-null extension axis). There is no
            // bare-name ext-property binding index, so reconstruct kotc's OWN `get_`/`set_<name>` accessor
            // convention BEFORE every owner-null resolver below (`TryExtMemberIntrinsic` keyed `get_lastIndex|recv|
            // count`, `TryResolveTopLevelStatic` keyed `get_lastIndex`) — byte-identical to the pre-#81 baked emission.
            if ((node["prop"] as JsonValue)?.GetValue<string>() is ("get" or "set") and var tlProp)
            {
                node.Remove("prop");
                fn = (tlProp == "set" ? "set_" : "get_") + fn;
                node["method"] = fn;
            }
            // Collection/array FACTORY (`listOf`/`setOf`/`mapOf`/`arrayOf`/`intArrayOf`/`arrayOfNulls`): a
            // @ClrCollectionFactory/@ClrArrayFactory marker on the ref.dll top-level fun -> re-emit the
            // newList/newSet/newMap/newArray/newArraySized CONSTRUCTION node (the recognition kotc used to do via its
            // LIST/SET/MAP/ARRAY_FACTORY tables). Handled first so a factory never falls through to the plain top-level
            // owner-attribution below. A non-decomposable form (`mapOf(pairVariable)` — not a `to`-Pair literal) returns
            // null here and stays a plain call to the real factory body.
            if (TryFactorySubst(node, refs, fn) is JsonNode factoryNode) return factoryNode;
            var args0 = node["args"] as JsonArray ?? new JsonArray();
            var sigParts0 = SplitSig(node);
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
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"),
                    ["method"] = fn == "get" ? "get_Item" : "Contains",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") },
                    ["ret"] = TypeJson.Fqn(fn == "get" ? "System.Object" : "System.Boolean"),
                    ["recv"] = args0[0]?.DeepClone(), ["args"] = new JsonArray { args0[1]?.DeepClone() },
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
            // bare-intrinsic extension: resolve by name + the first-arg's receiver key + full param count (disambiguates
            // `set`, and keeps `substring(String,Int)`@ClrIntrinsic from capturing the 3-arg `substring(String,Int,Int)`).
            if (sigParts0.Count >= 1 && refs.TryExtMemberIntrinsic(fn, RecvKeyOf(sigParts0[0]), sigParts0.Count, out var extMember))
                return TopLevelExtensionInstance(node, refs, extMember, args0, sigParts0, ctx);
            // A NON-intrinsic referenced top-level stdlib fun (getOrElse/first/...): kotc emits owner=null (it cannot
            // know the file-class — that is CLR/ref knowledge). In an APP build, attribute it to the file-class the
            // ref.dll says it lives in, so ilemit's owner-present FindMethod reflects it against the runtime stdlib —
            // exactly how the iterator bridge `callStatic kotlin.collections.ClrIteratorBridgeKt.*` already resolves.
            // Skipped when the fun is locally defined (the sibling wins) or in the stdlib self-build (flag off).
            if (_attributeTopLevelOwner && !_localTopLevelFns.Contains(fn))
            {
                var recvKey = sigParts0.Count >= 1 ? RecvKeyOf(sigParts0[0]) : "";
                if (refs.TryResolveTopLevelStatic(fn, recvKey, out var fileClassOwner))
                {
                    node["owner"] = TypeJson.Fqn(fileClassOwner);   // owner is a birType-emitted (structured Fqn) slot
                    return node;
                }
            }
            return null;
        }

        // #76 EDIT 2 (defensive) — a `get_storage()` accessor call on an unsigned-array value class, should kotc emit
        // the backing-field read as a property getter callInstance rather than a raw `{k:field}`. Same erasure as
        // TransformStorageField: reinterpret the receiver to the SIGNED array. Handled BEFORE the CLR-owner gate below
        // (kotlin.U*Array is not @ClrTypeAlias-bound, so it would otherwise return null unresolved).
        if (instance && (node["method"] as JsonValue)?.GetValue<string>() == "get_storage"
            && UnsignedArraySignedElem.TryGetValue(ownerToken, out var storageSignedElem))
            return new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(new TypeNode.Array(new TypeNode.Fqn(storageSignedElem))),
                ["e"] = node["recv"]?.DeepClone(),
            };

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
            var directHasProp = instance && !string.IsNullOrEmpty(pmember)
                && refs.TryResolveClrOwner(ownerToken, out _, out _)
                && refs.TryMemberProperty(ReferenceMetadataIndex.BareOwnerFqn(ownerToken), pmember, pargs.Count, out _, out _);
            if (instance && !directHasProp && !string.IsNullOrEmpty(pmember) && node["overrides"] is JsonArray povChain)
                foreach (var o in povChain)
                    if (o is JsonObject oo && TypeJson.OwnerName(oo["owner"]) is string ovOwner
                        && refs.TryResolveClrOwner(ovOwner, out var ovBcl, out _)
                        && refs.TryMemberProperty(ovOwner, pmember, pargs.Count, out var povAccess, out var povName))
                        return ClrPropNode(node, ClrOwnerType(refs, new TypeNode.Fqn(ovOwner)) ?? new TypeNode.Fqn(ovBcl), povName, povAccess, pmember, pargs);
        }

        // A Kotlin-collection `iterator()` on an EMITTED (non-@ClrTypeAlias) collection type — a `kotlin.collections.
        // AbstractMutable*` self-call: its abstract iterator() slot vanished when its collection supertype substituted
        // to the BCL IEnumerable face, so `this.iterator()` finds no slot. Route it to the ClrIteratorBridge over the
        // receiver (the exact target the @ClrTypeAlias-interface path — Rule 5 — uses; here the owner is a CLASS not in
        // the alias table, so that rule never reaches it). Element type = the owner's first type-arg.
        if (instance && ownerToken.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && (node["method"] as JsonValue)?.GetValue<string>() == "iterator"
            && node["args"] is JsonArray itArgs && itArgs.Count == 0
            && ownerFqnNode != null && !refs.TryResolveClrOwner(ownerToken, out _, out _))
            return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable",
                OwnerElemArg(ownerFqnNode), itArgs);

        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var kind))
        {
            // #78: a companion STATIC property-accessor call (the "owner"-keyed stdlib axis) whose enclosing type
            // carries NO @ClrTypeAlias binding at all — the overwhelmingly common case (an ordinary user or stdlib
            // companion computed property with no CLR binding). kotc emits the bare property IDENTITY + a
            // `"prop":"get"/"set"` marker instead of baking the accessor slot name (mirrors the instance-axis A2
            // convention kotc already uses elsewhere); reconstruct kotc's OWN get_/set_<name> declaration-side
            // convention (the CLR property model — every property's accessor is CIL-named that way regardless of
            // CLR-boundness) so the call still resolves to the real emitted accessor, byte-identical to the
            // pre-#78 baked emission. The marker itself is stripped either way — it is not BIR/CIR vocabulary.
            if (!instance && (node["prop"] as JsonValue)?.GetValue<string>() is ("get" or "set") and var uProp
                && (node["method"] as JsonValue)?.GetValue<string>() is string uMember)
            {
                node.Remove("prop");
                node["method"] = (uProp == "set" ? "set_" : "get_") + uMember;
            }
            return null;
        }

        var member = (node["method"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(member)) return null;
        var ownerFqn = ReferenceMetadataIndex.BareOwnerFqn(ownerToken);
        var args = node["args"] as JsonArray ?? new JsonArray();
        // #78: the STATIC property-accessor marker for a call whose owner IS CLR-bound — carried down to Rule 2p
        // (below) so the explicit @ClrProperty binding is tried on the static axis too, not just instance.
        var staticPropMarker = !instance ? (node["prop"] as JsonValue)?.GetValue<string>() : null;
        if (staticPropMarker != null) node.Remove("prop");   // the marker is not BIR/CIR vocabulary — consumed here

        // Rule Conv (numeric primitive CONVERSION): the member carries @ClrConv on the ref.dll (`kotlin.Int.toLong`,
        // `kotlin.Double.toInt`, `kotlin.Char.toInt`, ...) -> emit `{k:conv, to:<callee return type>, e:<receiver>}`, the
        // SAME node kotc used to synthesize from the retired NUMBER_CONV name-heuristic. The `to` is the callee's own
        // declared return token (a pre-lowering Kotlin FQN, e.g. `kotlin.Long`); BirTypeLowering later lowers it to the
        // CLR primitive and ilemit selects conv.i4/conv.i8/conv.r8/char. A conversion is nullary (no args). Handled first
        // so it never falls through to Rule 2/3 (the conversion members are intrinsic-less, so IsRule3Member excludes them).
        if (instance && args.Count == 0 && refs.TryMemberConv(ownerFqn, member, 0, out var convTo))
            return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(convTo), ["e"] = node["recv"]?.DeepClone() };

        // Rule 0 (inline-class ERASURE / unbox): the backing-field getter of an @JvmInline value class erased to its
        // primitive CLR form (`uint.get_data()`) is the unbox — the receiver value IS the field. Collapse it to a
        // `conv` of the receiver to the field's declared type (never a `ldfld data` — System.UInt32 has no `data`). This
        // is the GENERAL inline-erasure rule, not a UInt.toInt special-case; it fixes both the inlined `x.data` and the
        // rule-3 helper body's `self.data`, after which all the unsigned conversions fold to a plain cast.
        if (instance && refs.TryInlineFieldGetter(ownerFqn, member, out var inlineConv))
            return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(inlineConv), ["e"] = node["recv"]?.DeepClone() };

        // The CLR owner TYPE the call addresses (a ClrRef-resolvable BCL token; see ClrOwnerType).
        TypeNode clrOwner = ClrOwnerType(refs, ownerFqnNode) ?? new TypeNode.Fqn(bcl);

        // Rule 2p (explicit PROPERTY accessor): the member carries @ClrProperty(access, name) -> route EXPLICITLY to
        // clrPropGet(name) [READ] / clrPropSet(name) [WRITE] on the BCL owner, from the stated access role — NOT the old
        // get_/set_ intrinsic-string-prefix sniff. Handled before Rule 2/3 so a @ClrProperty stub (setLength/capacity/
        // ticks) is neither routed as a plain method nor hoisted as a rule-3 body. #78: also tried on the STATIC axis
        // (a companion computed property carrying the `"prop":"get"/"set"` marker) — a @ClrProperty binding is keyed
        // purely by owner+bare-name+argcount, with no instance/static distinction of its own.
        if ((instance || staticPropMarker is "get" or "set") && refs.TryMemberProperty(ownerFqn, member, args.Count, out var pAccess, out var pName))
            return ClrPropNode(node, clrOwner, pName, pAccess, member, args, staticPropMarker);
        // #78: the static-axis marker found no @ClrProperty binding — probe a bare @ClrIntrinsic under the SAME bare
        // name (Rule 2, reached again unconditionally below) before Rule 3/4 ever see this bare name; when NEITHER
        // binds, reconstruct kotc's own get_/set_<name> declaration-side convention (byte-identical to the pre-#78
        // baked emission) so every rule below proceeds exactly as it did before this call carried a marker at all.
        if (staticPropMarker is "get" or "set" && !refs.TryMemberIntrinsic(ownerFqn, member, args.Count, out _))
            node["method"] = member = (staticPropMarker == "set" ? "set_" : "get_") + member;

        // PRE-Rule-2 semantic override: MutableCollection.add is @ClrIntrinsic("Add") (the binding drives the
        // implementor-side DeclarationRename), but the CALL semantics diverge — Kotlin `add` returns the
        // changed-Boolean while `ICollection<T>.Add` is VOID (a brIf on the phantom result was a stack underflow),
        // and 1-arg `addAll` has no ICollection slot at all. Route these calls to the ClrCollectionDefaults
        // helpers BEFORE the intrinsic rule; the 2-arg add(index, e)/addAll(index, c) Insert forms fall through.
        if (instance && kind == "interface" && ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && args.Count == 1 && member is "add" or "Add" or "addAll")
            return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt",
                member == "addAll" ? "clrCollAddAll" : "clrCollAdd", CollElemArg(node, refs, ctx, ownerFqnNode), args);

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
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn(primBcl), ["method"] = "CompareTo",
                ["argTypes"] = new JsonArray { TypeJson.Fqn(primBcl) }, ["ret"] = TypeJson.Fqn("System.Int32"),
                ["recv"] = node["recv"]?.DeepClone(), ["args"] = args.DeepClone(),
            };

        // Rule 2: the member carries @ClrIntrinsic -> a direct BCL call.
        if (refs.TryMemberIntrinsic(ownerFqn, member, args.Count, out var intrinsic))
            return Constrainify(ClrCallNode(node, clrOwner, intrinsic, member, args, instance, refs.MemberByrefPositions(ownerFqn, member, args.Count)), node, refs, ctx, ownerToken);

        // Rule 3: a concrete member of a CLR-bound CLASS with NO @ClrIntrinsic carries a real Kotlin body, which
        // AliasHelperHoist lifts to the static helper `dotkt$ClrH_<owner>` (driven by the SAME class binding that brought us here).
        // `IsRule3Member` (ref.dll: the member is concrete + intrinsic-less) is the signal to hoist it; the helper
        // is emitted into the same runtime assembly. NEVER for an INTERFACE owner: an @ClrTypeAlias interface's members
        // are abstract in source (no helper is emitted for it — confirmed: every emitted dotkt$ClrH_* is a class), so
        // its abstract collection members (isEmpty/contains/iterator/...) need the ClrCollectionDefaults routing (Rule 5), not
        // a non-existent helper. (The ref.dll mis-reports these as non-abstract, so IsRule3Member alone false-positives.)
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
                    && refs.TryResolveClrOwner(ovOwner, out _, out var ovKind) && ovKind != "interface"
                    && refs.IsRule3Member(ovOwner, ovMember))
                    return Rule3HelperCall(node, refs, new TypeNode.Fqn(ovOwner), ovMember, args, instance);

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
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"),
                    ["method"] = member == "get" ? "get_Item" : "Contains",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") },
                    ["ret"] = TypeJson.Fqn(member == "get" ? "System.Object" : "System.Boolean"),
                    ["recv"] = node["recv"]?.DeepClone(), ["args"] = new JsonArray { args[0].DeepClone() },
                };
            var mutable = ownerFqn == "kotlin.collections.MutableMap";
            var helper = (member, args.Count, mutable) switch
            {
                ("get", 1, _) => "clrMapGet",
                // size / containsKey are UNBOUND (no @ClrIntrinsic) — a direct Count/ContainsKey reads through the
                // INVARIANT generic IDictionary<K,V> and throws EntryPointNotFound on a value-type-mismatched map (a
                // groupBy result). Route to the covariance-safe non-generic helpers (ICollection.Count / IDictionary
                // .Contains). This also makes mapValues' transitive `mapCapacity(this.size)` covariance-safe.
                ("get_size", 0, _) => "clrMapSize",
                ("containsKey", 1, _) => "clrMapContainsKey",
                ("isEmpty", 0, _) => "clrMapIsEmpty",
                ("containsValue", 1, _) => "clrMapContainsValue",
                ("getOrDefault", 2, _) => "clrMapGetOrDefault",
                ("get_keys", 0, false) => "clrMapKeys",
                ("get_values", 0, false) => "clrMapValues",
                ("get_entries", 0, false) => "clrMapEntries",
                ("get_entries", 0, true) => "clrMapMutableEntries",
                ("put", 2, true) => "clrMapPut",
                ("remove", 1, true) => "clrMapRemove",
                ("remove", 2, true) => "clrMapRemoveKV",
                ("putAll", 1, true) => "clrMapPutAll",
                ("putIfAbsent", 2, true) => "clrMapPutIfAbsent",
                ("replace", 2, true) => "clrMapReplace",
                ("replace", 3, true) => "clrMapReplaceKVV",
                ("merge", 3, true) => "clrMapMerge",
                _ => null,
            };
            if (helper != null)
                return MapDefaultCall(node, helper, ownerFqnNode, args, refs, ctx);
            if (mutable && args.Count == 0 && member is "get_keys" or "get_values")
                return ClrPropNode(node, clrOwner, member == "get_keys" ? "Keys" : "Values", ClrPropRead, member, args);
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
                return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable", elem, args);
            if (member == "listIterator")
            {
                var idx = args.Count >= 1 ? args : new JsonArray { new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = 0 } };
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", "clrListListIterator", elem, idx);
            }
            if (CollectionDefaults.TryGetValue(member, out var helperMethod))
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", helperMethod, elem, args);
        }

        // Rule 4 (already-BCL member name): kotc emits the BCL member NAME for a member it knows is CLR-bound — both the
        // universal object/comparable renames (compareTo/equals/hashCode/toString -> CompareTo/Equals/GetHashCode/
        // ToString) and the collection accessors/methods (get_Item/get_Count/Add/set_Item/RemoveAt/Insert/Remove/Clear/
        // GetEnumerator/...). The ref.dll member is kept under its Kotlin name (`get`/`compareTo`), so rules 2/3 miss by
        // name; but the emitted name is already the BCL member, which exists on the alias's BCL type. A BCL name is
        // PascalCase or a get_/set_ accessor (Kotlin members are lowercase camelCase) -> route to clrInstance/clrPropGet
        // on the BCL type. A lowercase-camelCase name that reaches here is an UNBOUND Kotlin member with no BCL
        // equivalent by that name (MutableCollection.addAll/removeAll/retainAll on ICollection) -> still route it to a
        // clrInstance on the BCL owner: ilemit resolves the BCL member when one matches, and falls to dynamic dispatch
        // (recv.GetType().GetMethod(name)) when none does. EITHER WAY this is correct AND it rescues the call from the
        // clrg:/shorthand owner that plain `callInstance` resolution (ilemit ParseOwner / ResolveMethod) cannot handle.
        //
        // MAKE-IT-LOUD gate (H1): the "falls to dynamic dispatch" escape is ONLY legitimate for an INTERFACE owner —
        // the intended `MutableCollection.addAll/removeAll/retainAll` on `ICollection<T>`, where the runtime value
        // implements the interface under a concrete type so reflection finds the slot. A lowercase-camelCase member on a
        // CLR-bound NON-interface owner (a concrete BCL class) is an UNBOUND Kotlin member with no BCL equivalent by that
        // name AND no @ClrIntrinsic/@ClrProperty/rule-3 binding: it is a genuine routing MISS. Left unrefused it would
        // emit a clrInstance that ilemit can neither resolve statically nor (post-gate) dispatch dynamically → an opaque
        // runtime NRE. Refuse it here, at compile time, naming `owner.member`. Allow only a BCL-shaped name (PascalCase
        // or a get_/set_ accessor) or an interface owner (the legit dynamic-dispatch case). Instance-only: a static
        // lowercase miss already throws loudly at ilemit (no dynamic-dispatch path is instance-gated there).
        if (instance && kind != "interface" && !string.IsNullOrEmpty(member)
            && !char.IsUpper(member[0])
            && !member.StartsWith("get_", StringComparison.Ordinal)
            && !member.StartsWith("set_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"bir2cir: unresolved CLR member '{ownerFqn}.{member}' — a lowercase-camelCase member on the CLR-bound "
                + $"{kind} owner '{ownerToken}' has no @ClrIntrinsic/@ClrProperty/rule-3 binding and is not a BCL member "
                + "name (BCL members are PascalCase). This is a routing MISS: fix the stdlib binding or the owner alias, "
                + "do not let it fall to a silent runtime dynamic-dispatch NRE.");
        return Constrainify(ClrCallNode(node, clrOwner, member, member, args, instance), node, refs, ctx, ownerToken);
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
        var rk = (recv["k"] as JsonValue)?.GetValue<string>();
        if (rk == "local")
            return (recv["name"] as JsonValue)?.GetValue<string>() is string vn
                && ctx.VarTypes.TryGetValue(vn, out var vt) ? vt : null;
        if (!allowExprShapes) return null;
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
        if (ctx != null && node["recv"] is JsonObject recv && (recv["k"] as JsonValue)?.GetValue<string>() == "local"
            && (recv["name"] as JsonValue)?.GetValue<string>() is string vn
            && ctx.VarTypes.TryGetValue(vn, out var vt))
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
        if (ctx != null && refs != null && node["recv"] is JsonObject recv && (recv["k"] as JsonValue)?.GetValue<string>() == "local"
            && (recv["name"] as JsonValue)?.GetValue<string>() is string vn && ctx.VarTypes.TryGetValue(vn, out var vt) && vt is TypeNode.Tv tvR
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

    // The receiver-type key of a call's first-arg type (mirrors ReferenceMetadataIndex.RecvKey on the ref.dll side).
    static string RecvKeyOf(TypeNode sig0) => sig0 switch
    {
        TypeNode.Array => "[]",
        TypeNode.Tv => "gp",
        TypeNode.Nullable n => RecvKeyOf(n.Of),
        TypeNode.ByRef b => RecvKeyOf(b.Of),
        TypeNode.Fqn f => ReferenceMetadataIndex.BareOwnerFqn(f.Name),
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
    // (both bits) is a get+set property whose specific call is disambiguated by the accessor member prefix / arg count.
    const int ClrPropRead = 1, ClrPropWrite = 2;

    // Build a clrPropGet/clrPropSet node for a .NET property `prop` on the BCL owner `bcl`. Used by BOTH the explicit
    // @ClrProperty accessor (Rule 2p; `prop` is the bare BCL property "Length") and the genuine `val X` member-prefix
    // accessor (trigger ①), where `prop` may arrive as the full BCL accessor name kotc emits for a CLR-bound property
    // (Rule 4: `get_Count`) — strip a leading get_/set_ so the clrProp `name` is the bare property. `access` = READ/WRITE
    // flags; when BOTH are set (a var property) the accessor member prefix (`set_` -> write) or arg count (1 = write)
    // picks the direction. WRITE takes the single value arg; READ carries the return type. On the STATIC axis a non-null
    // `propMarker` ("get"/"set", #78/#81) OVERRIDES the arg-count heuristic (it encodes the accessor kind explicitly),
    // and a leading `__self` extension-receiver arg makes the accessor an INSTANCE property on __self (WRITE value = args[^1]).
    static JsonNode ClrPropNode(JsonObject node, TypeNode clrOwner, string prop, int access, string member, JsonArray args, string propMarker = null)
    {
        if (prop.StartsWith("get_", StringComparison.Ordinal) || prop.StartsWith("set_", StringComparison.Ordinal))
            prop = prop[4..];
        var wantRead = (access & ClrPropRead) != 0;
        var wantWrite = (access & ClrPropWrite) != 0;
        // #81: the STATIC-axis `"prop":"get"/"set"` marker encodes the accessor KIND explicitly — trust it over the
        // `args.Count == 1` heuristic, which mis-reads an EXTENSION getter's lone `__self` arg (count 1) as a WRITE.
        // The heuristic stays for the instance axis (no marker), where args are pure value args.
        var write = propMarker is "get" or "set"
            ? propMarker == "set"
            : wantRead && wantWrite
                ? (member.StartsWith("set_", StringComparison.Ordinal) || args.Count == 1)
                : wantWrite;
        // #81: a STATIC EXTENSION property accessor prepends its extension receiver as the LEADING arg (getter
        // `[__self]`; setter `[__self, value]`) rather than in node["recv"]. Detect it by arg count past the
        // direction the marker fixed (getter with 1 arg / setter with 2 args carries a `__self`) — it becomes the
        // .NET receiver, so the accessor is an INSTANCE property on `__self`, not a static.
        var extRecv = propMarker is "get" or "set" && args.Count > (write ? 1 : 0) ? args[0] : null;
        var pg = new JsonObject
        {
            ["k"] = write ? "clrPropSet" : "clrPropGet",
            ["type"] = TypeJson.Write(clrOwner),
            ["name"] = prop,
            // A marker-bound static computed property (no __self) is a genuine STATIC accessor; an extension binds
            // on __self (instance); the instance axis (no marker) stays instance.
            ["static"] = propMarker != null && extRecv == null,
            ["recv"] = (extRecv ?? node["recv"])?.DeepClone(),
        };
        if (!write && RetToken(node) is JsonNode ret) pg["ret"] = ret;
        // WRITE value = the LAST arg (past a leading `__self` on the extension axis); args[0] on the instance/plain axis.
        if (write && args.Count >= 1) pg["value"] = (extRecv != null ? args[^1] : args[0]).DeepClone();
        return pg;
    }

    // A clrInstance / clrStatic node. A property-accessor call whose MEMBER carries the `get_`/`set_` prefix (kotc's
    // property convention: a `val length` -> the accessor call `get_length`, intrinsic bare "Length") emits clrPropGet/
    // clrPropSet on the bare intrinsic; otherwise a plain method call. A standalone accessor FUN bound to a property is
    // routed EXPLICITLY by @ClrProperty (Rule 2p) BEFORE this node is built, so there is no intrinsic-prefix sniff here.
    // Prefix `byref:` onto the argTypes at each @ClrRefArgument position (idempotent), so ilemit resolves the `ref`/`out`
    // BCL overload and emits the address-load for that arg (the byref shape a `ref`/`out` parameter needs).
    static void WrapByref(JsonArray argTypes, int[] byrefPositions)
    {
        if (byrefPositions == null) return;
        foreach (var i in byrefPositions)
        {
            if (i < 0 || i >= argTypes.Count) continue;
            // A structured arg type -> ByRef(inner); a legacy sig-string token -> "byref:"+s. Idempotent either way.
            if (TypeJson.Read(argTypes[i]) is TypeNode tn)
            {
                if (tn is not TypeNode.ByRef) argTypes[i] = TypeJson.Write(new TypeNode.ByRef(tn));
            }
            else if (argTypes[i] is JsonValue v && v.TryGetValue<string>(out var s) && !s.StartsWith("byref:", StringComparison.Ordinal))
                argTypes[i] = "byref:" + s;
        }
    }

    static JsonNode ClrCallNode(JsonObject node, TypeNode clrOwner, string intrinsic, string member, JsonArray args, bool instance, int[] byrefPositions = null)
    {
        var argTypes = InferArgTypes(node, args);
        WrapByref(argTypes, byrefPositions);
        var ret = RetToken(node);

        // Trigger ①: a genuine `val X` accessor — kotc emits the call on the MEMBER as `get_x`/`set_x`. The intrinsic is
        // the bare property name (convention: property @ClrIntrinsic values are bare, e.g. "Length"), so it becomes the
        // clrProp `name` verbatim. (Indexers reaching here have member "get"/"set" with an index arg -> args.Count != 0/1,
        // so they fall through to the method call below, not this branch.)
        var isGet = member.StartsWith("get_", StringComparison.Ordinal) && args.Count == 0;
        var isSet = member.StartsWith("set_", StringComparison.Ordinal) && args.Count == 1;
        if (instance && (isGet || isSet))
            return ClrPropNode(node, clrOwner, intrinsic, isSet ? ClrPropWrite : ClrPropRead, member, args);

        var call = new JsonObject
        {
            ["k"] = instance ? "clrInstance" : "clrStatic",
            ["type"] = TypeJson.Write(clrOwner),
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
        };
        if (ret != null) call["ret"] = ret;
        if (instance) call["recv"] = node["recv"]?.DeepClone();
        call["args"] = args.DeepClone();
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
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"), ["method"] = "toString",
                    ["sig"] = new JsonArray { TypeJson.Fqn("object") }, ["args"] = new JsonArray { a.DeepClone() },
                };
                argTypes[i] = TypeJson.Fqn("kotlin.String");
            }
    }

    // True iff an argType slot (a legacy sig STRING or a structured Fqn) denotes kotc's synthetic monomorphic
    // `dotkt$CharSequence` interface (tolerating a `nullable:`/`@` decoration). The `dotkt$StringCharSequence`
    // adapter deliberately does NOT match — its token has no `dotkt$CharSequence` substring.
    static bool IsSyntheticCharSeqToken(JsonNode slot)
    {
        var name = slot switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => (TypeJson.Read(slot) as TypeNode.Fqn)?.Name,
        };
        return name != null && name.Contains("dotkt$CharSequence", StringComparison.Ordinal);
    }

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
        // Carry the callee's param-type list (receiver-first, mirroring the hoisted helper's __self) so the
        // String->CharSequence bridge sees the synthetic-CharSequence slots (il-regex). `sig` is a STRUCTURED
        // TypeNode array (#37 m3b): the receiver type prepends the original sig's structured elements verbatim.
        var sigParts = new JsonArray();
        if (instance && node["recv"] != null)
            sigParts.Add(TypeJson.Write(classArgs.Count > 0 ? new TypeNode.Fqn(ownerName, classArgs.ToArray()) : ownerFqn));
        if (node["sig"] is JsonArray origSig)
            foreach (var p in origSig) sigParts.Add(p?.DeepClone());
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

    // The call's parameter types, used as the clr* argTypes overload key. Prefer kotc's `sig` (a comma-joined
    // param-type list); else infer each arg's own type token; else empty. Left in the kotlin.* vocabulary —
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
            else if (value[i] == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
        }
        result.Add(value[start..].Trim());
        return result;
    }
}

