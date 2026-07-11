using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed class ReferenceMetadataIndex
{
    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    const string JvmInlineAttr = "kotlin.jvm.JvmInline";
    const string RestrictsSuspensionAttr = "kotlin.coroutines.RestrictsSuspension";
    // [KotlinFunction(flags)] flag word (mirrors ilemit Program.cs pass 4 / facadegen): Infix=1, Operator=2, Suspend=4.
    const int KotlinFunctionSuspendFlag = 4;

    readonly List<ReferenceAssembly> _assemblies;

    // Aggregate CALL-SUBSTITUTION index across all reference assemblies.
    readonly Dictionary<string, string> _ownerAlias = new(StringComparer.Ordinal);   // Kotlin FQN -> BCL alias
    readonly Dictionary<string, string> _ownerKind = new(StringComparer.Ordinal);    // Kotlin FQN -> class/struct/...
    readonly Dictionary<string, int> _ownerArity = new(StringComparer.Ordinal);      // Kotlin FQN -> generic arity
    readonly Dictionary<string, string[]> _ownerTypeParams = new(StringComparer.Ordinal); // Kotlin FQN -> generic param names
    // Per owner-FQN, the declared param type names of its (first/sole) constructor — used to adapt a static-String arg
    // flowing into a CharSequence ctor param of a SPLICED anonymous stdlib object (`dotkt$obj*`, e.g. the anonymous
    // Grouping from `CharSequence.groupingBy` whose ctor captures the receiver as `kotlin.CharSequence`). The spliced
    // `new dotkt$obj*(...)` node carries no argTypes, so the CharSequence-slot knowledge comes only from here.
    readonly Dictionary<string, string[]> _ownerCtorParams = new(StringComparer.Ordinal);
    // Per owner-FQN, the CLR generic-parameter CONSTRAINT class of each flattened type-param position:
    // "struct" (NotNullableValueTypeConstraint), "class" (ReferenceTypeConstraint), or "unconstrained". Drives the
    // struct-ness ORACLE for a type variable (#37/#48 nullability fold): a struct-constrained `T?` is `Nullable<T>`,
    // a class/unconstrained `T?` is a bare reference (nullability rides an NRT byte).
    readonly Dictionary<string, string[]> _ownerTypeParamConstraints = new(StringComparer.Ordinal);
    readonly HashSet<string> _helperTypes = new(StringComparer.Ordinal);             // emitted "dotkt$ClrH_*"
    readonly HashSet<string> _restrictsSuspension = new(StringComparer.Ordinal);     // @RestrictsSuspension owners
    readonly Dictionary<string, List<MemberBinding>> _membersByOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, TypeNode> _staticFieldTypes = new(StringComparer.Ordinal); // "owner|field" -> declared type (#73-2b-A: cross-file array-const reads)
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static
    readonly Dictionary<string, string> _topLevelIntrinsicsBySig = new(StringComparer.Ordinal); // "name|paramKeys" -> FQ static (overload-disambiguated)
    readonly HashSet<string> _ambiguousTopLevelIntrinsics = new(StringComparer.Ordinal); // names whose overloads bind to DIFFERENT statics (Math vs MathF)
    readonly Dictionary<string, int[]> _topLevelIntrinsicByref = new(StringComparer.Ordinal); // top-level fun name -> byref param positions
    readonly Dictionary<string, string> _extMemberIntrinsics = new(StringComparer.Ordinal); // "name|recvKey|paramCount" -> bare member
    readonly Dictionary<string, (string Getter, string Conv)> _inlineBacking = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, string RecvKey)>> _topLevelStatics = new(StringComparer.Ordinal); // non-intrinsic top-level fun name -> [(file-class, recvKey)]
    readonly Dictionary<string, string> _collectionFactories = new(StringComparer.Ordinal); // @ClrCollectionFactory fun name -> "list"/"set"/"map"
    readonly Dictionary<string, string> _arrayFactories = new(StringComparer.Ordinal);       // @ClrArrayFactory fun name -> "vararg"/"sized"
    readonly Dictionary<string, string> _arrayFactoryElemHints = new(StringComparer.Ordinal);// array factory name -> concrete elem FQN (empty-call fallback)
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaults = new(StringComparer.Ordinal); // "owner|name|paramCount" -> (argPos -> default BIR)
    // [KotlinInline] raw-BIR payloads (#71/#75): "owner|name|pc|ga" -> the CANDIDATE decoded carrier JSONs (one per overload
    // sharing that key; the raw pre-lowering decl facts InlineBirStash stashed). Read cross-module by InlineSplice, which
    // picks the UNIQUE candidate matching the call's `paramSig` (§4.2), then splices its body at the call site (so it
    // re-lowers in THIS app's context). owner = the .NET type FullName (file-facade class); pc/ga = the reflected
    // GetParameters/GetGenericArguments counts (parity with InlineBirStash's params.Count / typeParams.Count).
    readonly Dictionary<string, List<string>> _inlinePayloads = new(StringComparer.Ordinal);
    // OWNER-LESS callInline index (S3, §4.2 #75 S4b): "name|pc|ga" -> the CANDIDATE payload JSONs across EVERY `kotlin.*`
    // file-class hosting that shape. kotc cannot name the stdlib file class (facadegen supplies no `kotlin.*` metadata — the
    // whole stdlib rides the klib), so a scope-fn/@InlineOnly callInline carries owner=null. Since the bare `name|pc|ga`
    // collides across owners (Iterable/Array/IntArray/CharSequence `filter`/`map`/`forEach` etc.), the owner canNOT be picked
    // by the key alone — InlineSplice gathers ALL candidates here and picks the UNIQUE one whose declared params match the
    // call's `paramSig`; the winning payload's own `owner` field names the host. Restricted to `kotlin.*` so a user-lib
    // inline fn sharing a name|pc|ga cannot leak in.
    readonly Dictionary<string, List<string>> _ownerlessInlineCandidates = new(StringComparer.Ordinal);

    // ---- .NET-interop resolution (A2 / #61): the LONG-LIVED metadata universe over the loaded .NET references +
    // the running framework's reference dir. NetInteropBinding resolves a facadegen-injected owner FQN
    // ("System.Console", "Kfc.App") to a metadata-only System.Reflection.Type here and reflects its member SHAPE
    // (static/instance/property/field/indexer/generic) to bind the plain callStatic/callInstance kotc emitted by
    // identity into the CLR-codegen `clr*` vocabulary. kotc no longer decides the .NET call shape (layer purity —
    // this is the SAME "emit the identity, bind in bir2cir" pattern as the stdlib ref.dll, one axis over). The MLC is
    // kept ALIVE for the whole run (Type handles are per-MLC; disposed in Driver.Run) and populated lazily.
    MetadataLoadContext _netMlc;
    List<Assembly> _netRefAsms;       // the explicit --ref .NET assemblies (user libs, e.g. Kfc)
    List<Assembly> _netRuntimeAsms;   // the running framework's reference dir (System.* et al.)
    readonly Dictionary<string, Type> _netTypeCache = new(StringComparer.Ordinal);
    bool _netInit;

    // Foundational REFERENCE-type aliases known to bir2cir directly (the same principle as the foundational
    // kotlin.* -> CLR type map already hardcoded in this file). Listed here so member-call / construction
    // substitution works even before kotc preserves the class @ClrTypeAlias attribute on the ref.dll. Only the
    // reference primitives (Any/String) — value primitives keep their identity and are handled by type lowering.
    static readonly IReadOnlyDictionary<string, string> FoundationalRefAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Any"] = "System.Object",
        ["kotlin.String"] = "System.String",
        ["kotlin.Nothing"] = "System.Object",
    };

    // The foundational VALUE-type identities (seed for the struct-ness ORACLE): the numeric/bool/char primitives and
    // the unsigned set, in BOTH their kotlin.* spelling and the CLR shorthand a lowered/synthesized node may carry.
    // A nullable value type is the structural `System.Nullable<T>` (a DISTINCT type), so it keeps its `{t:nullable}`
    // wrapper through lowering — unlike a reference type, whose `?` is stripped to a bare type + an NRT byte. The
    // authoritative source for a concrete NON-primitive is the ref.dll `_ownerKind` (struct/enum); this seed makes the
    // primitives resolve even with no ref.dll loaded and shadows any ref-scan miss.
    static readonly HashSet<string> ValueTypePrimitiveFqns = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Double", "kotlin.Float",
        "kotlin.Boolean", "kotlin.Char", "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        "int", "long", "short", "sbyte", "double", "float", "bool", "char", "uint", "ulong", "ushort", "byte",
    };

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies)
    {
        _assemblies = assemblies;
        foreach (var asm in assemblies)
        {
            foreach (var kv in asm.DotKt.Aliases) _ownerAlias[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeKinds) _ownerKind[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeArity) _ownerArity[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamNames) _ownerTypeParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.CtorParamTypes) _ownerCtorParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamConstraints) _ownerTypeParamConstraints[kv.Key] = kv.Value;
            foreach (var h in asm.DotKt.HelperTypes) _helperTypes.Add(h);
            foreach (var s in asm.DotKt.RestrictsSuspensionTypes) _restrictsSuspension.Add(s);
            foreach (var m in asm.DotKt.MemberBindings)
            {
                if (!_membersByOwner.TryGetValue(m.Owner, out var list))
                    _membersByOwner[m.Owner] = list = new List<MemberBinding>();
                list.Add(m);
            }
            foreach (var kv in asm.DotKt.StaticFieldTypes) _staticFieldTypes.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelIntrinsics) _topLevelIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicsBySig) _topLevelIntrinsicsBySig.TryAdd(kv.Key, kv.Value);
            foreach (var n in asm.DotKt.AmbiguousTopLevelIntrinsics) _ambiguousTopLevelIntrinsics.Add(n);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicByref) _topLevelIntrinsicByref.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ExtMemberIntrinsics) _extMemberIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.InlineBacking) _inlineBacking.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelStatics)
            {
                if (!_topLevelStatics.TryGetValue(kv.Key, out var lst))
                    _topLevelStatics[kv.Key] = lst = new List<(string, string)>();
                lst.AddRange(kv.Value);
            }
            foreach (var kv in asm.DotKt.KotlinDefaults) _kotlinDefaults.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.CollectionFactories) _collectionFactories.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ArrayFactories) _arrayFactories.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ArrayFactoryElemHints) _arrayFactoryElemHints.TryAdd(kv.Key, kv.Value);
            // §4.2 (#75 S4b): merge candidate lists across assemblies — every overload sharing owner|name|pc|ga is kept; the
            // call site disambiguates by paramSig. (No poisoning: structural selection replaces the recv0-key collision.)
            foreach (var kv in asm.DotKt.InlinePayloads)
            {
                if (!_inlinePayloads.TryGetValue(kv.Key, out var lst)) _inlinePayloads[kv.Key] = lst = new List<string>();
                lst.AddRange(kv.Value);
            }
        }
        // Build the owner-less candidate index (S3, §4.2): "name|pc|ga" -> the candidate payload JSONs across every `kotlin.*`
        // file-class hosting that shape. The owner is NOT resolvable from the bare key (it collides across owners) — the call
        // site selects by paramSig and reads the winner's own `owner` field. Restricted to `kotlin.*` (kotc emits owner-less
        // ONLY for the klib stdlib; a user-lib inline fn is always owner-ful) so a user `T.use(block)` cannot leak in.
        foreach (var (key, jsons) in _inlinePayloads)
        {
            var bar = key.IndexOf('|');
            if (bar < 0) continue;
            if (!key.AsSpan(0, bar).StartsWith("kotlin.")) continue;
            var npg = key[(bar + 1)..];   // name|pc|ga
            if (!_ownerlessInlineCandidates.TryGetValue(npg, out var lst)) _ownerlessInlineCandidates[npg] = lst = new List<string>();
            lst.AddRange(jsons);
        }
    }

    // The CANDIDATE raw-BIR [KotlinInline] payloads for an OWNER-FUL cross-module inline fn (owner|name|pc|ga), each decoded to
    // its JSON object — the overloads sharing that key. Empty/null when the referenced assembly carries no (or only unreadable
    // / pre-S1-shaped) [KotlinInline] for that shape. InlineSplice picks the UNIQUE one matching the call's paramSig.
    public List<JsonObject> InlineCandidates(string owner, string name, int pc, int ga)
    {
        if (owner == null || name == null) return null;
        return ParseCandidates(_inlinePayloads.GetValueOrDefault($"{owner}|{name}|{pc}|{ga}"));
    }

    // The CANDIDATE payloads for an OWNER-LESS callInline (S3): every `kotlin.*` overload hosting name|pc|ga, across owners.
    // InlineSplice selects the unique paramSig match; the winner's own `owner` field names the host file class.
    public List<JsonObject> OwnerlessInlineCandidates(string name, int pc, int ga) =>
        name == null ? null : ParseCandidates(_ownerlessInlineCandidates.GetValueOrDefault($"{name}|{pc}|{ga}"));

    static List<JsonObject> ParseCandidates(List<string> jsons)
    {
        if (jsons == null || jsons.Count == 0) return null;
        var list = new List<JsonObject>(jsons.Count);
        foreach (var j in jsons)
            try { if (JsonNode.Parse(j) is JsonObject jo) list.Add(jo); } catch { /* unreadable payload — skip candidate */ }
        return list.Count > 0 ? list : null;
    }

    // The @ClrCollectionFactory kind ("list"/"set"/"map") for a top-level fun NAME, or null when the fun is not a
    // collection factory. MemberCallSubstitution consults this on a `callStatic owner=null` to re-emit newList/newSet/newMap.
    public string CollectionFactoryKind(string funName) => _collectionFactories.GetValueOrDefault(funName);
    // The @ClrArrayFactory kind ("vararg"/"sized") for a top-level fun NAME, or null when not an array factory.
    public string ArrayFactoryKind(string funName) => _arrayFactories.GetValueOrDefault(funName);
    // The concrete element FQN for an array factory (empty-call fallback for `intArrayOf()`), or null.
    public string ArrayFactoryElemHint(string funName) => _arrayFactoryElemHints.GetValueOrDefault(funName);

    // The @KotlinDefault BIR splice map for a call's callee — (argPosition -> default-expression BIR-json). Matched by
    // owner FQN + method name + total parameter count (the emitted-call arity, extension receiver included). Null when
    // the callee carries no @KotlinDefault (a function with only metadata-representable defaults).
    public Dictionary<int, string> KotlinDefaultsFor(string owner, string method, int paramCount) =>
        _kotlinDefaults.TryGetValue(owner + "|" + method + "|" + paramCount, out var m) ? m : null;

    // Cross-assembly suspend-call resolution (bundle-6 P3 wave 2a): does the referenced owner declare a suspend
    // member of this name? The cold entry is the naming-convention linkage (`<name>$dotkt_suspend` on the same
    // owner type), keyed off the [KotlinFunction(Suspend)] flag scanned into MemberBinding.Suspend. Used by
    // SuspendColdLowering to rewrite a cross-assembly `x.g()` suspend call to `x.g$dotkt_suspend(…, completion)`.
    public bool HasSuspendMember(string owner, string name) =>
        owner != null && _membersByOwner.TryGetValue(owner, out var list)
        && list.Any(m => m.Suspend && string.Equals(m.Name, name, StringComparison.Ordinal));

    // Does this owner type carry @kotlin.coroutines.RestrictsSuspension (a restricted-suspension scope, e.g.
    // SequenceScope)? A suspend lambda with such a receiver gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public bool HasRestrictsSuspension(string ownerToken) =>
        ownerToken != null && _restrictsSuspension.Contains(BareOwnerFqn(ownerToken));

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    // Every ref.dll scan diagnostic (a swallowed MetadataLoadContext load failure / partial-type-load / per-type skip).
    // Surfaced to stderr in the driver so a silent ref-scan miss (which becomes a DISTANT EntryPointNotFound/NRE at
    // ilemit or run time) is visible at the layer that produced it. See the driver's `Run` for the fail-loud print.
    public IEnumerable<string> Diagnostics => _assemblies.SelectMany(a => a.DotKt.Diagnostics);

    // The ref.dll @ClrTypeAlias index (Kotlin FQN -> BCL), the SINGLE source of truth shared by both the member-call
    // substitution (owner identity) and the TYPE-TOKEN lowering (supertypes/interfaces/type-args/fields). Keyed on the
    // stripped FQN (no generic-arity backtick), matching a BIR type token's bare owner.
    public IReadOnlyDictionary<string, string> Aliases => _ownerAlias;

    // ---- Call-substitution lookups (consumed by MemberCallSubstitution) ----

    // A BIR owner token ("@kotlin.text.StringBuilder", "kotlin.collections.ArrayList[gp:E]", "clr:System.X") ->
    // its bare Kotlin FQN ("kotlin.text.StringBuilder"). Strips decoration, the clr:/clrg: marker, and type args.
    public static string BareOwnerFqn(string token)
    {
        var t = token.Trim().TrimStart('@');
        foreach (var p in new[] { "clrg:", "clr:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) t = t[p.Length..];
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        return StripGenericArity(t);
    }

    // Resolve a member-call/construction OWNER to its BCL type. True for a @ClrTypeAlias / class-@ClrIntrinsic owner
    // (or a foundational reference primitive). `kind` is the ref.dll type kind (class/struct/interface/enum).
    public bool TryResolveClrOwner(string ownerToken, out string bcl, out string kind)
    {
        var fqn = BareOwnerFqn(ownerToken);
        if (FoundationalRefAliases.TryGetValue(fqn, out bcl)) { kind = "class"; return true; }
        if (_ownerAlias.TryGetValue(fqn, out bcl)) { kind = _ownerKind.GetValueOrDefault(fqn, "class"); return true; }
        bcl = null; kind = null; return false;
    }

    // Resolve a facadegen-injected .NET owner FQN to its metadata-only reflection Type (A2 / #61), or null when the
    // owner is NOT a reachable .NET type — i.e. a `kotlin.*`/`kotlinx.*`/`dotkt*` stdlib owner (bound by
    // MemberCallSubstitution off the ref.dll, NOT here), a local app-emitted type, or anything the loaded refs +
    // framework dir don't contain. `genericArity` lets a constructed generic owner ("System.Collections.Generic.List"
    // + args) resolve its open definition (`List`1`). Consumed by NetInteropBinding to shape the call. Cached.
    public Type ResolveNetType(string fqn, int genericArity = 0)
    {
        if (string.IsNullOrEmpty(fqn)) return null;
        // The stdlib's own vocabulary is bound off the ref.dll (@ClrTypeAlias/@ClrIntrinsic) by MemberCallSubstitution,
        // never reflected as a raw .NET type here — skip it so the two binders never collide. This ALSO skips the three
        // CLR-only-vocabulary SYNTHETICS facadegen injects purely to make the frontend typecheck — `kotlin.clr.ClrEvent`,
        // `kotlin.clr.ClrRef`, the `kotlin.clr.byref` marker — which have NO definition in any reference assembly and are
        // fully lowered by kotc itself (kotc's own dialect extension). They must never be resolved here (they don't
        // exist); their pre-lowered nodes (an event `clrPropGet`, a ref-passing form) flow through this pass opaquely.
        if (fqn == "kotlin" || fqn.StartsWith("kotlin.", StringComparison.Ordinal)
            || fqn.StartsWith("kotlinx.", StringComparison.Ordinal)
            || fqn.StartsWith("dotkt", StringComparison.Ordinal)) return null;
        if (_netTypeCache.TryGetValue(fqn, out var cached)) return cached;
        EnsureNetMlc();
        Type found = null;
        if (_netMlc != null)
        {
            foreach (var candidate in NetTypeCandidates(fqn, genericArity))
            {
                foreach (var asm in _netRefAsms) { found = SafeGetType(asm, candidate); if (found != null) break; }
                if (found == null)
                    foreach (var asm in _netRuntimeAsms) { found = SafeGetType(asm, candidate); if (found != null) break; }
                if (found != null) break;
            }
        }
        _netTypeCache[fqn] = found;
        return found;
    }

    // The FQN spellings to probe: the plain name, then the generic-arity backtick form (`List`1`). The exact arity
    // (from the owner token's type-arg count) is tried first; a small fallback range covers a token that dropped its args.
    static IEnumerable<string> NetTypeCandidates(string fqn, int genericArity)
    {
        yield return fqn;
        if (genericArity > 0) yield return fqn + "`" + genericArity;
        for (var k = 1; k <= 8; k++) if (k != genericArity) yield return fqn + "`" + k;
    }

    static Type SafeGetType(Assembly asm, string fqn) { try { return asm.GetType(fqn, throwOnError: false); } catch { return null; } }

    void EnsureNetMlc()
    {
        if (_netInit) return;
        _netInit = true;
        try
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var paths = new List<string>(Directory.GetFiles(runtimeDir, "*.dll"));
            foreach (var a in _assemblies)
            {
                var full = Path.GetFullPath(a.Path);
                paths.Add(full);
                var dir = Path.GetDirectoryName(full);
                if (dir != null) paths.AddRange(Directory.GetFiles(dir, "*.dll"));
            }
            _netMlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct(StringComparer.Ordinal)));
            _netRefAsms = new List<Assembly>();
            foreach (var a in _assemblies)
            {
                try { _netRefAsms.Add(_netMlc.LoadFromAssemblyPath(Path.GetFullPath(a.Path))); } catch { }
            }
            _netRuntimeAsms = new List<Assembly>();
            foreach (var p in Directory.GetFiles(runtimeDir, "*.dll"))
            {
                try { _netRuntimeAsms.Add(_netMlc.LoadFromAssemblyPath(p)); } catch { }
            }
        }
        catch { _netMlc = null; }
    }

    public void DisposeNet() { try { _netMlc?.Dispose(); } catch { } _netMlc = null; }

    public int OwnerArity(string ownerFqn) => _ownerArity.GetValueOrDefault(ownerFqn, 0);
    public string[] OwnerTypeParamNames(string ownerFqn) => _ownerTypeParams.GetValueOrDefault(ownerFqn);
    // The declared param type names of the owner's (sole/first) constructor, or null. Keyed by the arity-stripped
    // Kotlin FQN (`dotkt$obj90`, not `dotkt$obj90``1`), matching the CIR `new` node's bare type token.
    public string[] OwnerCtorParamTypeNames(string ownerFqn) => _ownerCtorParams.GetValueOrDefault(ownerFqn);

    // The struct-ness ORACLE (#37/#48 nullability fold). True iff a CONCRETE Kotlin/CLR type FQN is a VALUE type
    // (a foundational primitive, or a ref.dll struct/enum). A value `T?` is `System.Nullable<T>` (keeps its wrapper);
    // a reference `T?` is a bare type + an NRT byte. Consulted by BirTypeLowering (the Nullable strip) and the decl
    // NRT-byte walk. Not for type VARIABLES — use TvConstraint for those. Foundational value primitives resolve from
    // the hardcoded seed even with no ref.dll; a ref.dll struct/enum resolves from the scanned `_ownerKind`.
    public bool IsValueTypeFqn(string fqn)
    {
        if (fqn == null) return false;
        if (ValueTypePrimitiveFqns.Contains(fqn)) return true;
        var bare = StripGenericArity(fqn);
        var kind = _ownerKind.GetValueOrDefault(bare);
        return kind == "struct" || kind == "enum";
    }

    // The CLR generic-parameter constraint class of a type variable declared on `ownerFqn` at flattened index `i`:
    // "struct" (a value-type constraint -> a `T?` is `Nullable<T>`), "class" (a reference constraint -> bare + NRT),
    // or "unconstrained"/null (unknown -> treated as reference by the caller's sound fallback). Recorded from the
    // ref.dll's GenericParameterAttributes during the scan; empty when the owner is a local type / not on the ref.dll.
    public string TvConstraint(string ownerFqn, int i)
    {
        if (ownerFqn == null) return null;
        var arr = _ownerTypeParamConstraints.GetValueOrDefault(StripGenericArity(ownerFqn));
        return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
    }

    // The @ClrProperty accessor binding for owner.member: its READ/WRITE access flags + the .NET property name. Routes the
    // call EXPLICITLY to clrPropGet/clrPropSet (no get_/set_ string-prefix sniff). Overload-disambiguated by arg count.
    public bool TryMemberProperty(string ownerFqn, string memberName, int argCount, out int access, out string name)
    {
        access = 0; name = null;
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.PropertyName != null).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous (use it); MULTIPLE
            // candidates that DISAGREE on the bound property are a genuine routing ambiguity — refuse rather than
            // pick an arbitrary overload (which would bind the wrong .NET property).
            if (cands.Select(c => (c.PropertyAccess, c.PropertyName)).Distinct().Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrProperty overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different properties — no exact-arity match");
            pick = cands[0];
        }
        access = pick.PropertyAccess; name = pick.PropertyName;
        return true;
    }

    // The @ClrConv numeric-conversion binding for owner.member: its conv TARGET (the callee's own return-type token, a
    // pre-lowering Kotlin FQN like `kotlin.Long`). Returns true when owner.member (arg count matched when possible) is a
    // @ClrConv-marked conversion — MemberCallSubstitution then emits `{k:conv, to:<convTo>, e:<recv>}`. A conversion is
    // nullary, so arg count is always 0; the arity match is kept for symmetry with the other member lookups.
    public bool TryMemberConv(string ownerFqn, string memberName, int argCount, out string convTo)
    {
        convTo = null;
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.Conv).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount) ?? cands[0];
        convTo = pick.ConvTo;
        return convTo != null;
    }

    // The @ClrIntrinsic BCL member name for owner.member (overload-disambiguated by arg count when possible).
    public bool TryMemberIntrinsic(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = null;
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.Intrinsic != null).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // binding DIFFERENT BCL members are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => c.Intrinsic).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrIntrinsic overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different BCL members — no exact-arity match");
            pick = cands[0];
        }
        intrinsic = pick.Intrinsic;
        return true;
    }

    // STRICT overload-exact @ClrIntrinsic lookup for the DECLARATION rename: the marker's arity is precise (Kotlin
    // override resolution), so `add(element)` (arity 1, ->Add) must NOT fall through to `add(index,element)` (arity 2,
    // ->Insert). Unlike TryMemberIntrinsic there is no `?? cands[0]` arity fallback — no exact-arity match = no rename.
    public bool TryMemberIntrinsicExact(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = _membersByOwner.TryGetValue(ownerFqn, out var list)
            ? list.FirstOrDefault(m => m.Name == memberName && m.Intrinsic != null && m.ParamCount == argCount)?.Intrinsic
            : null;
        return intrinsic != null;
    }

    // FULL-SIGNATURE @ClrIntrinsic lookup for the member-STRIP: is owner.name(paramKeys) a bound stub? Matches the
    // @ClrIntrinsic member whose canonicalized param types equal the emitted method's — so `StringBuilder.append(Char)`
    // (@ClrIntrinsic, dropped) is distinguished from `append(CharSequence?)` (rule-3, kept), which share name+arity.
    public bool IsBoundStub(string ownerFqn, string memberName, IReadOnlyList<string> birParamKeys)
    {
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        return list.Any(m => m.Name == memberName && m.Intrinsic != null && m.ParamTypes != null
            && m.ParamTypes.Length == birParamKeys.Count
            && m.ParamTypes.Select(ParamKey).SequenceEqual(birParamKeys));
    }

    // Canonicalize a type token (a kotc birType or a ref.dll reflected TypeName) to a comparable identity for signature
    // matching: unwrap byref/array/nullable, drop the clr/@ marker + generic args, collapse a type param, fold primitives.
    // Deliberately shallow (top-level identity) — enough to separate the real overloads without full structural matching.
    public static string ParamKey(string t)
    {
        t = t.Trim();
        if (t.EndsWith("?", StringComparison.Ordinal)) t = t[..^1];
        foreach (var w in new[] { "byref:", "array:", "nullable:" })
            if (t.StartsWith(w, StringComparison.Ordinal)) return w + ParamKey(t[w.Length..]);
        foreach (var p in new[] { "clrg:", "clr:", "@" })
            if (t.StartsWith(p, StringComparison.Ordinal)) { t = t[p.Length..]; break; }
        // `sfunc:` (suspend fn TYPE) erases to `object`: a suspend-lambda VALUE is a SuspendLambda state-machine
        // OBJECT (a Continuation-based object), NOT a Func delegate — so it keys as `obj`, matching an intrinsic's
        // object-erased suspend param/receiver. A plain `func:` still keys as the delegate bucket.
        if (t.StartsWith("sfunc:", StringComparison.Ordinal)) return "obj";
        if (t.StartsWith("func:", StringComparison.Ordinal)) return "func";
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        if (t.StartsWith("gp:", StringComparison.Ordinal)) return "gp";
        return t switch
        {
            "kotlin.Byte" or "System.SByte" or "sbyte" => "i8",             // signed 8-bit; token "sbyte" IS kotlin.Byte (System.SByte)
            "kotlin.Short" or "System.Int16" or "short" => "i16",
            "kotlin.Int" or "System.Int32" or "int" => "i32",
            "kotlin.Long" or "System.Int64" or "long" => "i64",
            "kotlin.Float" or "System.Single" or "float" => "f32",
            "kotlin.Double" or "System.Double" or "double" => "f64",
            "kotlin.Boolean" or "System.Boolean" or "bool" => "bool",
            "kotlin.Char" or "System.Char" or "char" => "char",
            "kotlin.String" or "System.String" or "string" => "str",
            "kotlin.Unit" or "System.Void" or "void" => "void",
            "kotlin.Any" or "System.Object" or "object" => "obj",
            // Primitive-array class spellings (kotc lowers to `array:int`, but the ref.dll may reflect the kotlin.IntArray
            // class) -> the same array key so a top-level `sort(IntArray)`@ClrIntrinsic matches by signature.
            "kotlin.IntArray" => "array:i32",
            "kotlin.LongArray" => "array:i64",
            "kotlin.ByteArray" => "array:i8",
            "kotlin.ShortArray" => "array:i16",
            "kotlin.FloatArray" => "array:f32",
            "kotlin.DoubleArray" => "array:f64",
            "kotlin.BooleanArray" => "array:bool",
            "kotlin.CharArray" => "array:char",
            // Unsigned specialized arrays (#53): native System.Byte[]/UInt16[]/UInt32[]/UInt64[]. Same array key as
            // their element token so an @ClrIntrinsic signature over the ref.dll spelling matches.
            "kotlin.UByteArray" => "array:byte",
            "kotlin.UShortArray" => "array:ushort",
            "kotlin.UIntArray" => "array:uint",
            "kotlin.ULongArray" => "array:ulong",
            _ => StripGenericArity(t),
        };
    }

    // ParamKey over a STRUCTURED Type node (a birType-emitted param slot) — walks the TypeNode natively (never
    // re-renders a legacy token), matching the string ParamKey's top-level-identity canonicalization exactly:
    // byref/array/nullable unwrap-with-marker, a fn -> obj (suspend) / func, a type-var -> gp, an Fqn leaf folded via
    // the shared primitive switch (delegating to ParamKey(f.Name) — a bare FQN the switch already handles).
    public static string ParamKey(TypeNode t) => t switch
    {
        TypeNode.ByRef b => "byref:" + ParamKey(b.Of),
        TypeNode.Array a => "array:" + ParamKey(a.Elem),
        TypeNode.Nullable n => "nullable:" + ParamKey(n.Of),
        TypeNode.Fn fn => fn.Suspend ? "obj" : "func",
        TypeNode.Tv => "gp",
        TypeNode.Fqn f => ParamKey(f.Name),
        _ => "obj",
    };

    // ParamKey off a JSON type slot: a structured `{t:…}` node walks natively; a legacy string slot (sig-side token)
    // keeps the string path.
    public static string ParamKey(JsonNode typeSlot)
    {
        if (TypeJson.Read(typeSlot) is TypeNode tn) return ParamKey(tn);
        if (typeSlot is JsonValue v && v.TryGetValue<string>(out var s)) return ParamKey(s);
        return ParamKey("");
    }

    // A top-level fun (file-class static, called as `callStatic owner=null`) bound by @ClrIntrinsic to a
    // fully-qualified BCL static (e.g. clrTimestamp -> "System.Diagnostics.Stopwatch.GetTimestamp").
    public bool TryTopLevelIntrinsic(string funName, out string fqStatic) =>
        _topLevelIntrinsics.TryGetValue(funName, out fqStatic);

    // Overload-disambiguated variant: a top-level @ClrIntrinsic name that binds to DIFFERENT BCL statics per overload
    // — kotlin.math `sqrt`/`abs`/`pow`/... -> System.Math.* for Double/Int/Long but System.MathF.* for Float. Keyed by
    // name|<ParamKey-joined signature> so a call resolves the EXACT intrinsic overload (and a non-intrinsic sibling
    // overload, e.g. `Double.pow(Int)`, correctly MISSES here and falls through to its real Kotlin body). `sigKey` is
    // the call's ParamKey-normalized signature. This is what lets the by-name-first-wins map stop shadowing MathF.
    public bool TryTopLevelIntrinsicBySig(string funName, string sigKey, out string fqStatic) =>
        _topLevelIntrinsicsBySig.TryGetValue(funName + "|" + sigKey, out fqStatic);

    // Whether a top-level intrinsic NAME binds to more than one distinct BCL static across its overloads (sqrt/abs/
    // pow -> Math vs MathF). For such names the name-only fallback is UNSAFE (it would pick an arbitrary overload), so
    // the caller must require an exact signature match; single-static names still fall back by name.
    public bool IsAmbiguousTopLevelIntrinsic(string funName) => _ambiguousTopLevelIntrinsics.Contains(funName);

    // Whether the ref.dll ALSO has a NON-intrinsic (real-Kotlin-body) top-level fun of this name. Such a name is
    // unsafe for the NAME-ONLY intrinsic fallback even when every intrinsic overload agrees on one BCL static:
    // `sort` binds all 8 primitive-array overloads to "System.Array.Sort" (so it is NOT "ambiguous"), but
    // `MutableList<T>.sort()` / `Array<out T>.sort()` are real bodies — the name fallback rewrote the real-bodied
    // call inside the compiled `sorted()` to an open-generic `Array.Sort` ("not fully instantiated" at runtime).
    // With a real-bodied sibling present, only the sig-EXACT intrinsic match may substitute.
    public bool HasNonIntrinsicTopLevel(string funName) => _topLevelStatics.ContainsKey(funName);

    // The 0-based parameter positions a top-level @ClrIntrinsic fun's bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). Empty when none — the substituted call then wraps no argTypes.
    public int[] TopLevelByrefPositions(string funName) =>
        _topLevelIntrinsicByref.TryGetValue(funName, out var pos) ? pos : Array.Empty<int>();

    // The 0-based parameter positions a bound MEMBER (owner.member, overload-matched by arg count) takes BY REFERENCE
    // (@ClrRefArgument). Empty when none.
    public int[] MemberByrefPositions(string ownerFqn, string memberName, int argCount)
    {
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return Array.Empty<int>();
        var cands = list.Where(m => m.Name == memberName && m.ByrefPositions != null && m.ByrefPositions.Length > 0).ToList();
        if (cands.Count == 0) return Array.Empty<int>();
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // with DIFFERENT byref positions are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => string.Join(",", c.ByrefPositions)).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrRefArgument byref overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate " +
                    $"arities [{string.Join(",", cands.Select(c => c.ParamCount))}] disagree on byref positions — no exact-arity match");
            pick = cands[0];
        }
        return pick.ByrefPositions;
    }

    // A NON-intrinsic top-level fun (real Kotlin body) resolved to the file-class it lives in, so an APP's
    // `callStatic owner=null` gets an explicit owner ilemit reflects against the referenced runtime stdlib. When the
    // name is defined in multiple file-classes (getOrElse in CollectionsKt/ArraysKt/MapsKt/...), the call's receiver
    // type (recvKey = its first sig param's bare owner) disambiguates. A single candidate needs no receiver match.
    public bool TryResolveTopLevelStatic(string funName, string recvKey, out string owner)
    {
        owner = null;
        if (!_topLevelStatics.TryGetValue(funName, out var cands) || cands.Count == 0) return false;
        if (cands.Count == 1) { owner = cands[0].Owner; return true; }
        // The candidate RecvKey is the ref.dll's Kotlin receiver type (`kotlin.collections.List`); the call site's
        // recvKey may already be that type's @ClrTypeAlias CLR form (`System.Collections.Generic.IReadOnlyList`), when
        // kotc rendered the receiver local as its CLR alias (e.g. `val xs = listOf(...)` used only via an extension).
        // Match through the alias so the overload disambiguates in either representation. (The forward alias map is
        // unambiguous; a bare-Kotlin recvKey still matches the plain `c.RecvKey == recvKey` arm.)
        foreach (var c in cands)
            if (c.RecvKey == recvKey || (_ownerAlias.TryGetValue(c.RecvKey, out var aliased) && aliased == recvKey))
            { owner = c.Owner; return true; }
        // The receiver key didn't disambiguate the OVERLOAD, but if every candidate lives in the SAME file-class the
        // OWNER is still unambiguous (e.g. both `runCatching(Func)` and `T.runCatching(Func)` are in kotlin.ResultKt).
        // Emit the shared owner; ilemit's FindMethod then selects the exact overload by signature.
        var owners = cands.Select(c => c.Owner).Distinct().ToList();
        if (owners.Count == 1) { owner = owners[0]; return true; }
        return false;
    }

    // The declared RETURN type of a bound member (owner.name, matched by arg count then by name), from the ref.dll —
    // used by StaticType (#59) to recover a call / field read whose BIR node carries NO `ret` (kotc emits `ret` only for
    // a GENERIC call). null when the owner/member is unknown or its return type was not structurable (a delegate/gp).
    public TypeNode TryMemberReturn(string ownerFqn, string name, int argCount)
    {
        if (ownerFqn == null || !_membersByOwner.TryGetValue(ownerFqn, out var list)) return null;
        return (list.FirstOrDefault(b => b.Name == name && b.ParamCount == argCount && b.ReturnType != null)
                ?? list.FirstOrDefault(b => b.Name == name && b.ReturnType != null))?.ReturnType;
    }

    // The declared type of a STATIC field on the ref.dll (a top-level `val` / companion constant, e.g. a cross-file
    // `charArrayOf(…)` array constant). Used by StaticType (#73-2b-A) to derive an array-const read's element.
    public TypeNode TryFieldType(string ownerFqn, string name) =>
        ownerFqn != null && _staticFieldTypes.TryGetValue(ownerFqn + "|" + name, out var t) ? t : null;

    // The declared RETURN type of a top-level fun (a `callStatic owner=null`), resolved via its file-class owner then the
    // member's return type. `recvKey` = the call's first sig-param bare owner (disambiguates overloads across file-classes);
    // `argCount` = the sig's total param count (receiver + args), matching the ref.dll static's ParamCount. null if unresolved.
    public TypeNode TryTopLevelReturn(string funName, string recvKey, int argCount) =>
        TryResolveTopLevelStatic(funName, recvKey, out var owner) ? TryMemberReturn(owner, funName, argCount) : null;

    // A bare-@ClrIntrinsic extension fun resolved by name + the receiver-type key (the call's first-arg type) + the
    // FULL parameter count (receiver + args), so `set` on a MutableMap receiver -> set_Item (not StringBuilder's
    // set_Chars) AND a same-name/same-receiver overload of a DIFFERENT arity does not collide: `substring(String,Int)`
    // @ClrIntrinsic("Substring") must NOT capture the 3-param `substring(String,Int,Int)` real-body call (which would
    // wrongly emit Substring(start,end) with end read as a LENGTH). The paramCount disambiguates them; the real-bodied
    // overload misses here and falls through to its stdlib file-class attribution.
    public bool TryExtMemberIntrinsic(string funName, string recvKey, int paramCount, out string member) =>
        _extMemberIntrinsics.TryGetValue(funName + "|" + recvKey + "|" + paramCount, out member);

    // An @JvmInline value class's backing-field getter call (`x.get_data()`): the inline UNBOX. Returns the CLR conv
    // token for the field's declared type so the call collapses to `conv(recv)` (the erased primitive IS the value).
    public bool TryInlineFieldGetter(string ownerFqn, string member, out string conv)
    {
        conv = null;
        return _inlineBacking.TryGetValue(ownerFqn, out var info) && member == info.Getter && (conv = info.Conv) != null;
    }

    // Whether the owner is an @JvmInline value class erased to a primitive CLR form (so `new T(arg)` is the inline BOX).
    public bool IsInlineValueClass(string ownerFqn) => _inlineBacking.ContainsKey(ownerFqn);

    // A rule-3 hoist candidate: owner.member exists, is concrete (non-abstract) and carries NEITHER @ClrIntrinsic NOR
    // @ClrProperty, so its real Kotlin body is hoisted by bir2cir's AliasHelperHoist to the static helper `dotkt$ClrH_<owner>`. A @ClrProperty
    // accessor (setLength/capacity/nativeSetCapacity/ticks) is a BOUND stub — its call substitutes to clrPropGet/clrPropSet
    // (Rule 2p) — so it must NOT hoist its throwing TODO body into the helper (the same exclusion @ClrIntrinsic gets).
    public bool IsRule3Member(string ownerFqn, string memberName) =>
        _membersByOwner.TryGetValue(ownerFqn, out var list) &&
        list.Any(m => m.Name == memberName && m.Intrinsic == null && m.PropertyName == null && !m.Conv && !m.IsAbstract);

    public static string HelperTypeName(string ownerFqn) =>
        "dotkt$ClrH_" + System.Text.RegularExpressions.Regex.Replace(ownerFqn, "[^A-Za-z0-9]", "_");


    public static ReferenceMetadataIndex Build(IReadOnlyList<string> refs)
    {
        var assemblies = new List<ReferenceAssembly>();
        foreach (var reference in refs)
        {
            if (!File.Exists(reference))
                throw new UsageException($"bir2cir: reference not found: {reference}");

            var identity = AssemblyName.GetAssemblyName(reference);
            assemblies.Add(new ReferenceAssembly(
                reference,
                identity.Name ?? Path.GetFileNameWithoutExtension(reference),
                identity.Version?.ToString() ?? "",
                ReadDotKtMetadata(reference)));
        }

        return new ReferenceMetadataIndex(assemblies);
    }

    static ReferenceDotKtMetadata ReadDotKtMetadata(string reference)
    {
        var metadata = new ReferenceDotKtMetadata();
        // The substitution index via MetadataLoadContext (a metadata-only reflection read) is the SOLE scan. A former
        // runtime `Assembly.LoadFrom` scan (populating Members/Types/Functions/FileClasses) was REMOVED: it always
        // threw TypeLoadException on the metadata-only ref stdlib (throw-stub bodies + kotlin.* signatures) — logging a
        // spurious "metadata scan failed: TypeLoadException Type: 'kotlin.String'" on every build — and aborted early,
        // and its output fed ONLY dead resolution paths (the unreferenced Resolve(CallSite)/Resolve(TypeSite)/
        // ResolveClrProperty). The live @ClrTypeAlias/@ClrIntrinsic/rule-3 substitution reads exclusively from here.
        ScanSubstitutionMetadata(reference, metadata);
        return metadata;
    }

    // Populate the substitution index (Aliases / TypeKinds / HelperTypes / MemberBindings) from the ref.dll using a
    // MetadataLoadContext so the metadata-only assembly reads cleanly. Per-type try/catch: one malformed type is
    // skipped, never aborting the whole scan (the failure mode that left Assembly.LoadFrom's index empty).
    static void ScanSubstitutionMetadata(string reference, ReferenceDotKtMetadata metadata)
    {
        try
        {
            var full = Path.GetFullPath(reference);
            var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            var dir = Path.GetDirectoryName(full);
            if (dir != null) paths.AddRange(Directory.GetFiles(dir, "*.dll"));
            paths.Add(full);
            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct(StringComparer.Ordinal)));
            var asm = mlc.LoadFromAssemblyPath(full);

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var type in types)
            {
                try
                {
                    // Index by the REAL Kotlin FQN (kotc emits "kotlin.String" etc. as the type name) so a BIR
                    // member-call owner token matches. A CLR-bound owner carries @ClrTypeAlias (the type-identity
                    // binding) or, for any not-yet-renamed bound class, a class-level @ClrIntrinsic.
                    var ownerFqn = StripGenericArity(type.FullName ?? type.Name);
                    metadata.TypeKinds[ownerFqn] = TypeKind(type);
                    if (type.IsGenericType)
                    {
                        var gargs = type.GetGenericArguments();
                        metadata.TypeArity[ownerFqn] = gargs.Length;
                        metadata.TypeParamNames[ownerFqn] = gargs.Select(g => g.Name).ToArray();
                        // The struct-ness ORACLE for a TYPE VARIABLE (#37/#48): record each type-param's CLR constraint
                        // class from GenericParameterAttributes so a `T?` on a struct-constrained param stays Nullable<T>.
                        metadata.TypeParamConstraints[ownerFqn] = gargs.Select(GenericParamConstraintClass).ToArray();
                    }
                    var classAlias = ClrAliasOf(type.GetCustomAttributesData());
                    if (classAlias != null) metadata.Aliases[ownerFqn] = classAlias;
                    // A SPLICED anonymous object (`dotkt$obj*`) captures its enclosing inline fn's receiver/free vars as
                    // ctor params. Record that (sole) ctor's param types so the StringCharSequenceBridge can adapter-wrap
                    // a static-String arg flowing into a `kotlin.CharSequence` capture slot (the spliced `new` carries no
                    // argTypes). Scoped to `dotkt$obj*` — the exact single-ctor case — so no multi-overload BCL/stdlib
                    // type is indexed (where "first ctor" would be ambiguous).
                    if (ownerFqn.StartsWith("dotkt$obj", StringComparison.Ordinal))
                    {
                        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
                        if (ctor != null)
                            metadata.CtorParamTypes[ownerFqn] = ctor.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray();
                    }
                    if (ownerFqn.StartsWith("dotkt$ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    if (HasAttribute(type.GetCustomAttributesData(), RestrictsSuspensionAttr)) metadata.RestrictsSuspensionTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);

                    // @JvmInline value class: its single instance backing field IS the erased value. Record the field
                    // getter + the field's CLR conv token so a `get_<field>()` call collapses to `conv(<recv>)`.
                    if (HasAttribute(type.GetCustomAttributesData(), JvmInlineAttr))
                    {
                        var backing = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault();
                        if (backing != null && InlineFieldConv(backing.FieldType) is string conv)
                            metadata.InlineBacking[ownerFqn] = ("get_" + backing.Name, conv);
                    }

                    // STATIC FIELD types (#73-2b-A): a top-level `val` / companion constant (e.g. a cross-file
                    // `charArrayOf(…)` array constant) so StaticType can derive an `array-const[i]` read's element
                    // when the field is defined in ANOTHER file (not in this file's LocalTypes).
                    foreach (var fi in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (TypeNodeOf(fi.FieldType) is TypeNode ft)
                            metadata.StaticFieldTypes.TryAdd(ownerFqn + "|" + fi.Name, ft);

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        var prop = ClrPropertyOf(method.GetCustomAttributesData());
                        var byrefPositions = ByrefPositionsOf(method);
                        // @ClrConv (numeric primitive conversion): the call lowers to a CIL `conv` to the callee's OWN
                        // declared return type (toLong -> the emitted `kotlin.Long` type, ...). Read the marker + capture
                        // the return-type token here (the pre-lowering Kotlin FQN, from THIS reference/metadata dll), so
                        // MemberCallSubstitution can emit `{k:conv, to:<convTo>, e:<recv>}` — the target BirTypeLowering
                        // then lowers to System.Int64/etc. and ilemit picks the conv opcode.
                        var isConv = HasAttribute(method.GetCustomAttributesData(), "kotlin.clr.ClrConv");
                        var convTo = isConv ? TypeName(method.ReturnType) : null;
                        // @KotlinDefault(index, bir) on the method's params -> the cross-module default-arg splice source.
                        var kdefaults = KotlinDefaultsOf(method);
                        if (kdefaults != null)
                            metadata.KotlinDefaults[ownerFqn + "|" + method.Name + "|" + method.GetParameters().Length] = kdefaults;
                        // The `suspend` bit from the DotKt round-trip [KotlinFunction(flags)] attribute (Suspend = 4,
                        // the flag word ilemit stamps; the dead Assembly.LoadFrom scan read it, this live scan didn't).
                        // Channelled into MemberBinding.Suspend for the coroutine bundle (bundle 6) — no consumer yet.
                        var suspend = (KotlinFunctionFlags(method.GetCustomAttributesData()) & KotlinFunctionSuspendFlag) != 0;
                        if (suspend && Environment.GetEnvironmentVariable("DOTKT_BIR2CIR_DEBUG_SUSPEND") == "1")
                            Console.Error.WriteLine($"bir2cir: ref-scan suspend member {ownerFqn}.{method.Name}/{method.GetParameters().Length} (Suspend=true)");
                        metadata.MemberBindings.Add(new MemberBinding(
                            ownerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic,
                            method.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray(),
                            prop?.Access ?? 0,
                            prop?.Name,
                            byrefPositions,
                            suspend,
                            isConv,
                            convTo,
                            TypeNodeOf(method.ReturnType)));
                        // [KotlinInline] raw-BIR carrier (#71/#75 S1): decode the versioned carrier now (the codec is
                        // BirCarrier, shared) and key it owner|name|pc|ga so InlineSplice can splice this injected inline
                        // fn's body at a cross-module call site. A malformed / pre-S1-shaped payload is swallowed (no
                        // cross-module splice for it -> the splicer's plain-call fallback). ga = generic arity.
                        var inlineCad = method.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == KotlinInlineAttr);
                        if (inlineCad != null && inlineCad.ConstructorArguments.Count == 2)
                        {
                            try
                            {
                                var ver = (string)inlineCad.ConstructorArguments[0].Value!;
                                var content = ReadByteArrayArg(inlineCad.ConstructorArguments[1]);
                                var decoded = DotKt.Bir.BirCarrier.DecodeBody(ver, content);
                                var json = decoded.ToJsonString();
                                // §4.2 (#75 S4b): key `owner|name|pc|ga` -> a LIST of candidate overload payloads. pc/ga come
                                // from the .NET method (the payload counts them identically: an extension receiver rides as a
                                // leading `__self` param). InlineSplice picks the UNIQUE candidate whose decoded `params[i].type`
                                // structurally matches the call's paramSig — the decoded params are kotc-emitted decl nodes, so
                                // they equal the callInline's paramSig exactly (both from `birType(param.type)`).
                                var ikey = ownerFqn + "|" + method.Name + "|" + method.GetParameters().Length + "|" + method.GetGenericArguments().Length;
                                if (!metadata.InlinePayloads.TryGetValue(ikey, out var ilst)) metadata.InlinePayloads[ikey] = ilst = new List<string>();
                                ilst.Add(json);
                            }
                            catch { /* unreadable / pre-S1 payload — no cross-module splice; the engine fails loud at splice time */ }
                        }
                        // A top-level fun (file-class static) with @ClrIntrinsic. TWO shapes:
                        //   FQ "System.X.Y"  -> a fully-qualified BCL static (isNaN, clrTimestamp); keyed by NAME.
                        //   bare "Name"      -> a member on an EXTENSION receiver (`Array<T>.nativeClone()` ->
                        //                       @ClrIntrinsic("Clone")). Keyed by NAME|recvKey (the first param's type),
                        //                       because the name alone collides across receivers (MutableMap.set->set_Item
                        //                       vs StringBuilder.set->set_Chars). recvKey of the call site is its first arg.
                        if (isFileClass && method.IsStatic && intrinsic != null)
                        {
                            var ps = method.GetParameters();
                            if (intrinsic.Contains('.'))
                            {
                                // Name-only map (first-wins) is retained for single-static intrinsics (isNaN,
                                // clrTimestamp); when a name is seen binding to a DIFFERENT static, mark it ambiguous so
                                // the caller requires an exact-signature match instead (sqrt/abs/pow -> Math vs MathF).
                                if (metadata.TopLevelIntrinsics.TryGetValue(method.Name, out var prior))
                                {
                                    if (prior != intrinsic) metadata.AmbiguousTopLevelIntrinsics.Add(method.Name);
                                }
                                else metadata.TopLevelIntrinsics[method.Name] = intrinsic;
                                // ALSO key by name|<full ParamKey signature> so a call resolves the EXACT overload
                                // (sqrt(Double)->System.Math.Sqrt, sqrt(Float)->System.MathF.Sqrt) and a non-intrinsic
                                // sibling (Double.pow(Int)) misses -> falls through to its real Kotlin body.
                                metadata.TopLevelIntrinsicsBySig.TryAdd(method.Name + "|" + SigKeyOf(ps), intrinsic);
                                if (byrefPositions.Length > 0) metadata.TopLevelIntrinsicByref.TryAdd(method.Name, byrefPositions);
                            }
                            else if (ps.Length >= 1)
                                metadata.ExtMemberIntrinsics.TryAdd(method.Name + "|" + RecvKey(ps[0].ParameterType) + "|" + ps.Length, intrinsic);
                        }
                        // A NON-intrinsic top-level fun (a real Kotlin body in a file-class) -> index it by name so an APP
                        // build can attribute a referenced `callStatic owner=null` to this file-class (disambiguated by the
                        // first-param receiver type when overloaded across file-classes). The stdlib self-build never reads it.
                        if (isFileClass && method.IsStatic && intrinsic == null)
                        {
                            var ps = method.GetParameters();
                            var rk = ps.Length >= 1 ? RecvKey(ps[0].ParameterType) : "";
                            if (!metadata.TopLevelStatics.TryGetValue(method.Name, out var lst))
                                metadata.TopLevelStatics[method.Name] = lst = new List<(string, string)>();
                            lst.Add((ownerFqn, rk));
                        }
                        // Collection/array FACTORY markers on a [KotlinFileClass] static (listOf/setOf/mapOf/arrayOf/…):
                        // record name -> kind so MemberCallSubstitution re-emits the newList/newSet/newMap/newArray node
                        // (the recognition kotc used to do via its LIST/SET/MAP/ARRAY_FACTORY tables). Every overload of a
                        // factory name agrees on the kind, so a name key is enough.
                        if (isFileClass && method.IsStatic)
                        {
                            if (AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrCollectionFactory") is string cf)
                                metadata.CollectionFactories[method.Name] = cf;
                            if (AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrArrayFactory") is string af)
                            {
                                metadata.ArrayFactories[method.Name] = af;
                                // Element hint for an EMPTY concrete primitive factory (`intArrayOf()`): kotc drops the
                                // empty vararg (args=[]) and these funs carry NO type argument, so neither typeArgs nor
                                // the vararg wrapper yields the element. Capture it from the factory's array return type
                                // (`kotlin.IntArray` -> element `kotlin.Int`); null for the generic `arrayOf<T>` (whose
                                // element is a type variable — typeArgs[0] covers it there).
                                if (ArrayElemHint(method.ReturnType) is string ah)
                                    metadata.ArrayFactoryElemHints[method.Name] = ah;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    metadata.Diagnostics.Add($"subst scan skip {type?.FullName}: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: subst scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A reflected byte[] ctor argument materializes under MetadataLoadContext as an IReadOnlyList<CustomAttributeTypedArgument>
    // (each element's .Value a boxed byte), not a byte[] — reify it (mirrors ilemit Emitter.CompilerServices.ReadByteArrayArg).
    static byte[] ReadByteArrayArg(CustomAttributeTypedArgument a)
    {
        if (a.Value is byte[] b) return b;
        if (a.Value is IReadOnlyList<CustomAttributeTypedArgument> arr)
        {
            var r = new byte[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = (byte)arr[i].Value!;
            return r;
        }
        throw new FormatException("carrier content is not a byte[]");
    }

    static bool HasAttribute(IList<CustomAttributeData> attrs, string fullName) =>
        attrs.Any(a => a.AttributeType.FullName == fullName);

    // The first constructor string argument of the attribute `fullName` (e.g. @ClrCollectionFactory("list") -> "list"),
    // or null when the attribute is absent / carries no string arg. Used for the factory-kind markers.
    static string AttrStringArg(IList<CustomAttributeData> attrs, string fullName)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == fullName);
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The element type FQN of an array factory's return type (`kotlin.IntArray` -> "kotlin.Int"), or null when the return
    // is not a concrete array (the generic `arrayOf<T>` returns `Array<T>` whose element is a type variable). Used only as
    // a last-resort element source for an EMPTY concrete primitive factory call, where args + typeArgs are both empty.
    static string ArrayElemHint(Type retType)
    {
        try
        {
            if (retType != null && retType.IsArray)
            {
                var el = retType.GetElementType();
                if (el != null && !el.IsGenericParameter) return TypeName(el);
            }
        }
        catch { }
        return null;
    }

    // The class-level CLR binding: @ClrTypeAlias (the type-identity binding); a class-level @ClrIntrinsic is also
    // accepted for any not-yet-renamed bound class. Returns the single ctor-arg (the .NET FQN), or null if not CLR-bound.
    static string ClrAliasOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrTypeAlias" or "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The member-level CLR binding: @ClrIntrinsic("Name") (or AsDynamic). Returns the BCL member name (the call is
    // rewritten to owner.Name), or null when the member carries no intrinsic (a rule-3 candidate).
    static string ClrIntrinsicOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrIntrinsic" or "kotlin.clr.ClrIntrinsicAsDynamic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The PARAMETER positions (0-based, over the method's declared params) marked @ClrRefArgument — a plain-typed
    // parameter the bound BCL member takes BY REFERENCE (`ref`/`out`). The substituted call wraps these argTypes
    // positions `byref:` so ilemit resolves the ref/out overload + emits the address-load. Empty when none.
    static int[] ByrefPositionsOf(MethodBase method)
    {
        var ps = method.GetParameters();
        List<int> hits = null;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].GetCustomAttributesData().Any(a => a.AttributeType.FullName == "kotlin.clr.ClrRefArgument"))
                (hits ??= new List<int>()).Add(i);
        return hits?.ToArray() ?? Array.Empty<int>();
    }

    // @KotlinDefault(index, bir) on the method's parameters -> (argPosition -> default-expression BIR-json). Returns null
    // when no parameter carries it. `index` is the parameter's position in the emitted call (extension receiver first);
    // `bir` is the default expression as a raw BIR-json string (opaque here — spliced pre-lowering by DefaultArgSplice).
    static Dictionary<int, string> KotlinDefaultsOf(MethodBase method)
    {
        Dictionary<int, string> map = null;
        foreach (var p in method.GetParameters())
        {
            var a = p.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.KotlinDefault");
            if (a == null || a.ConstructorArguments.Count < 2) continue;
            if (a.ConstructorArguments[0].Value is null || a.ConstructorArguments[1].Value is not string bir) continue;
            (map ??= new Dictionary<int, string>())[Convert.ToInt32(a.ConstructorArguments[0].Value)] = bir;
        }
        return map;
    }

    // The member-level PROPERTY-accessor binding: @ClrProperty(access, name). `access` is the READ(1)/WRITE(2) flag word;
    // `name` is the .NET property. Returns (access, name) or null when the member carries no @ClrProperty.
    static (int Access, string Name)? ClrPropertyOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.ClrProperty");
        if (a == null || a.ConstructorArguments.Count < 2) return null;
        if (a.ConstructorArguments[1].Value is not string name) return null;
        var access = a.ConstructorArguments[0].Value is null ? 0 : Convert.ToInt32(a.ConstructorArguments[0].Value);
        return (access, name);
    }

    // A receiver-type key for an extension fun's first param, matched against a call's first-arg type. Arrays collapse
    // to "[]", generic params to "gp", a generic type to its open def's stripped FQN. A NESTED type's reflection name
    // ("kotlin.collections.Map`2+Map$Entry`2") is normalized to the BIR token convention the call side uses
    // ("kotlin.collections.Map$Entry" = namespace + innermost simple name) — e.g. the Map.Entry.component1/2 extensions.
    static string RecvKey(Type t)
    {
        if (t.IsByRef && t.GetElementType() is Type e) t = e;
        if (t.IsArray) return "[]";
        if (t.IsGenericParameter) return "gp";
        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        var full = def.IsNested
            ? (string.IsNullOrEmpty(def.Namespace) ? "" : def.Namespace + ".") + def.Name
            : def.FullName ?? def.Name;
        return StripGenericArity(full);
    }

    // A method's full ParamKey-normalized signature ("f64", "f64,f64", "i32", ...), used to overload-disambiguate a
    // top-level @ClrIntrinsic (sqrt(Double) vs sqrt(Float); pow(Double,Double) intrinsic vs pow(Double,Int) real-body).
    // Runs each param's TypeName through ParamKey so the ref.dll declaration and the call's kotc `sig` agree.
    static string SigKeyOf(ParameterInfo[] ps) => string.Join(",", ps.Select(p => ParamKey(TypeName(p.ParameterType))));

    // An @JvmInline backing-field's CLR `conv` target — the ilemit conv opcode token for the field's primitive type
    // (kotlin.Int -> "int", kotlin.Byte -> "sbyte", ...). Null if the field is not a primitive ilemit conv'able.
    static string InlineFieldConv(Type fieldType) => fieldType.FullName switch
    {
        "kotlin.Int" => "int", "kotlin.Long" => "long", "kotlin.Short" => "short", "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char", "kotlin.Double" => "double", "kotlin.Float" => "float",
        "System.Int32" => "int", "System.Int64" => "long", "System.Int16" => "short", "System.SByte" => "sbyte",
        "System.Char" => "char", "System.Double" => "double", "System.Single" => "float",
        _ => null,
    };

    static int KotlinFunctionFlags(IList<CustomAttributeData> attrs)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == KotlinFunctionAttr);
        if (attr == null || attr.ConstructorArguments.Count == 0) return 0;
        var value = attr.ConstructorArguments[0].Value;
        return value is int i ? i : 0;
    }

    static string TypeName(Type type)
    {
        if (type.IsByRef)
            return "byref:" + TypeName(type.GetElementType()!);
        if (type.IsArray)
            return "array:" + TypeName(type.GetElementType()!);
        if (type.IsGenericParameter)
            return "gp:" + type.Name;
        if (IsDelegate(type))
            return DelegateTypeName(type);
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeName).ToList();
            if (def == typeof(Nullable<>))
                return "nullable:" + args[0];
            if (IsFunc(def))
                return "func:" + args[^1] + ":" + string.Join(",", args.Take(args.Count - 1));
            if (IsAction(def))
                return "func:void:" + string.Join(",", args);
            return "clrg:" + StripGenericArity(def.FullName ?? def.Name) + "[" + string.Join(",", args) + "]";
        }

        return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
    }

    // A STRUCTURED TypeNode from a reflected ref.dll type — the pure-Kotlin identity kotc would have emitted (the ref
    // surface's types ARE named kotlin.* — kotlin.collections.List<kotlin.String>, kotlin.Int, …). Used to carry a
    // top-level fn / member RETURN type so bir2cir StaticType (#59) can recover a `callStatic`/`callInstance` whose
    // node lacks a `ret` (a non-generic call — kotc emits `ret` only for a generic call). Covers the shapes StaticType
    // needs (Fqn+args for collection detect, nullable, array, primitive, tv); a delegate/func return is left null.
    static TypeNode TypeNodeOf(Type type)
    {
        if (type.IsByRef) return TypeNodeOf(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsArray) return TypeNodeOf(type.GetElementType()!) is TypeNode e1 ? new TypeNode.Array(e1) : null;
        if (type.IsGenericParameter) return null;   // an unresolved fn type-param: no useful static identity
        if (IsDelegate(type)) return null;
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeNodeOf).ToArray();
            if (def == typeof(Nullable<>)) return args[0] is TypeNode nv ? new TypeNode.Nullable(nv) : null;
            if (IsFunc(def) || IsAction(def)) return null;
            if (args.Any(a => a == null)) return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name));
            return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name), args);
        }
        var prim = PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? StripGenericArity(type.FullName ?? type.Name));
    }

    static bool IsFunc(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Func`", StringComparison.Ordinal);

    static bool IsAction(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Action`", StringComparison.Ordinal);

    static bool IsDelegate(Type type)
    {
        for (var cur = type; cur != null; cur = cur.BaseType)
            if (cur.FullName == "System.MulticastDelegate")
                return true;
        return false;
    }

    static string DelegateTypeName(Type type)
    {
        var invoke = type.GetMethod("Invoke");
        if (invoke == null) return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
        return "func:" + TypeName(invoke.ReturnType) + ":" + string.Join(",", invoke.GetParameters().Select(p => TypeName(p.ParameterType)));
    }

    static string PrimitiveBirName(Type type)
    {
        if (type == typeof(bool)) return "bool";
        // .NET-aligned 8-bit tokens (#54): "sbyte" is SIGNED = kotlin.Byte (System.SByte); "byte" is UNSIGNED =
        // kotlin.UByte (System.Byte). This matches int/short/long, whose token names already agree with .NET.
        // The unsigned family (ushort/uint/ulong) is here for the same reason.
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(char)) return "char";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(object)) return "object";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(string)) return "string";
        if (type == typeof(void)) return "void";
        // The REFERENCE stdlib emits the pure-Kotlin primitives as real types whose FullName is literally
        // "kotlin.Int" / "kotlin.String" / ... When such a ref dll is read back, converge those onto the SAME
        // CLR-shorthand token as their BCL twin so a member signature speaks one vocabulary for TypeMatches.
        return PrimitiveBirNameByFullName(type.FullName);
    }

    static string PrimitiveBirNameByFullName(string fullName) => fullName switch
    {
        "kotlin.Boolean" => "bool",
        "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Any" => "object",
        "kotlin.Short" => "short",
        "kotlin.String" => "string",
        "kotlin.UByte" => "byte",
        "kotlin.UInt" => "uint",
        "kotlin.ULong" => "ulong",
        "kotlin.UShort" => "ushort",
        "kotlin.Unit" => "void",
        _ => null,
    };

    static string TypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    // The constraint class of a generic parameter (a `GetGenericArguments()` element): "struct" when it carries the
    // value-type constraint (`where T : struct`), "class" when it carries the reference constraint (`where T : class`),
    // else "unconstrained". Drives the tv struct-ness oracle for the nullability fold.
    static string GenericParamConstraintClass(Type gp)
    {
        var a = gp.GenericParameterAttributes;
        if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return "struct";
        if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return "class";
        return "unconstrained";
    }

    static string StripGenericArity(string value)
    {
        var idx = value.IndexOf('`');
        return idx >= 0 ? value[..idx] : value;
    }
}

sealed record ReferenceAssembly(string Path, string Name, string Version, ReferenceDotKtMetadata DotKt);

sealed class ReferenceDotKtMetadata
{
    public readonly List<string> Diagnostics = new();

    // CALL-SUBSTITUTION metadata (sourced from the ref.dll, consumed by MemberCallSubstitution; NOT serialized).
    // ownerFqn (the Kotlin FQN, e.g. "kotlin.String") -> the BCL alias it binds to ("System.String"), from a
    // class-level @ClrTypeAlias (the type-identity binding) or, for a not-yet-renamed bound class, a class-level @ClrIntrinsic.
    public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TypeKinds = new(StringComparer.Ordinal);   // ownerFqn -> class/struct/interface/enum
    public readonly Dictionary<string, int> TypeArity = new(StringComparer.Ordinal);       // ownerFqn -> generic arity
    public readonly Dictionary<string, string[]> TypeParamNames = new(StringComparer.Ordinal); // ownerFqn -> generic param names
    public readonly Dictionary<string, string[]> CtorParamTypes = new(StringComparer.Ordinal); // ownerFqn -> (first) ctor param type names
    public readonly Dictionary<string, string[]> TypeParamConstraints = new(StringComparer.Ordinal); // ownerFqn -> per-param "struct"/"class"/"unconstrained"
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "dotkt$ClrH_*" rule-3 helpers
    // Types carrying @kotlin.coroutines.RestrictsSuspension (BINARY-retained, so present on the ref.dll). A suspend
    // lambda whose RECEIVER is such a scope (e.g. SequenceScope) gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public readonly HashSet<string> RestrictsSuspensionTypes = new(StringComparer.Ordinal);
    public readonly List<MemberBinding> MemberBindings = new();                           // per-member @ClrIntrinsic + shape
    // [KotlinInline] raw-BIR payloads (#71/#75): "owner|name|pc|ga" -> the candidate decoded carrier JSONs (one per overload).
    public readonly Dictionary<string, List<string>> InlinePayloads = new(StringComparer.Ordinal);
    public readonly Dictionary<string, TypeNode> StaticFieldTypes = new(StringComparer.Ordinal); // "owner|field" -> declared type
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TopLevelIntrinsicsBySig = new(StringComparer.Ordinal);
    public readonly HashSet<string> AmbiguousTopLevelIntrinsics = new(StringComparer.Ordinal);
    // Top-level @ClrIntrinsic fun name -> the 0-based parameter positions its bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). The substituted clrStatic wraps these argTypes positions `byref:` (tryParseInt32's `out result`,
    // Interlocked's `ref location`, Math.DivRem's `out remainder`). Absent when the fun has no byref parameter.
    public readonly Dictionary<string, int[]> TopLevelIntrinsicByref = new(StringComparer.Ordinal);
    // Bare-@ClrIntrinsic extension fun, keyed "funName|recvKey" (recvKey = the receiver/first-param type) -> the BCL
    // member name. Receiver-keyed because the bare name collides across receivers (set->set_Item vs set->set_Chars).
    public readonly Dictionary<string, string> ExtMemberIntrinsics = new(StringComparer.Ordinal);
    // @JvmInline value-class owner FQN -> (its single backing-field getter "get_data", the field's CLR conv token).
    // The class is ERASED to its primitive CLR form, so `get_data()` is the inline unbox: it collapses to the receiver
    // value conv'd to the field's declared type (a `conv`, never a `ldfld data` — the erased primitive has no field).
    public readonly Dictionary<string, (string Getter, string Conv)> InlineBacking = new(StringComparer.Ordinal);
    // NON-intrinsic top-level funs (real Kotlin bodies in a [KotlinFileClass]) -> their (file-class owner FQN, first-
    // param recvKey). Keyed by fun name. Lets an APP build resolve a referenced `callStatic owner=null` to the file-
    // class it actually lives in (getOrElse -> kotlin.collections._CollectionsKt), disambiguated by the call's receiver
    // type when the name is defined across multiple file-classes (CollectionsKt vs ArraysKt vs MapsKt). NOT consulted in
    // a stdlib self-build (the fun is local there; owner=null + FindStatic finds the sibling).
    public readonly Dictionary<string, List<(string Owner, string RecvKey)>> TopLevelStatics = new(StringComparer.Ordinal);
    // Collection/array FACTORY top-level funs, keyed by fun NAME -> the factory kind. A @kotlin.clr.ClrCollectionFactory
    // ("list"/"set"/"map") or @kotlin.clr.ClrArrayFactory ("vararg"/"sized") marker on a [KotlinFileClass] static.
    // MemberCallSubstitution reads these on a `callStatic owner=null` (listOf/setOf/mapOf/arrayOf/intArrayOf/arrayOfNulls
    // -> the `{k:newList/newSet/newMap/newArray/newArraySized}` construction node kotc used to synthesize). Keyed by name
    // alone: every overload of a factory name shares the kind, so no receiver disambiguation is needed.
    public readonly Dictionary<string, string> CollectionFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactoryElemHints = new(StringComparer.Ordinal); // concrete-primitive elem (empty call)
    // A defaulted parameter's default-value expression as BIR (from @KotlinDefault), for CROSS-MODULE splice of an
    // omitted argument. Keyed "ownerFqn|methodName|paramCount" -> (argPosition -> BIR-json string). The DefaultArgSplice
    // pass reads this to fill trailing omitted args BEFORE the CharSequence bridge + type lowering (so a String default
    // is coerced exactly like an explicit arg). Rides the ref.dll only (param attrs stripped in the rt build).
    public readonly Dictionary<string, Dictionary<int, string>> KotlinDefaults = new(StringComparer.Ordinal);
}

// A single ref.dll member's call-substitution shape. Owner is the Kotlin FQN ("kotlin.String"); Intrinsic is the
// @ClrIntrinsic BCL name or null (null + no @ClrProperty + !IsAbstract = a rule-3 hoist candidate). PropertyName (+ the
// READ/WRITE access flags) is set when the member carries @ClrProperty — an EXPLICIT .NET property accessor binding.
// Suspend = the Kotlin `suspend` modifier, read from the DotKt round-trip [KotlinFunction(flags)] attribute
// (Suspend bit = 4) in the LIVE MetadataLoadContext scan. Populated for the Task-based coroutine bundle (bundle 6):
// a cross-module call site must know "is this referenced callee suspend?" (its CLR shape is the Task<T> kickoff).
// NO consumer reads it yet — bundle 6 wires it.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, string[] ParamTypes = null, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null, bool Suspend = false, bool Conv = false, string ConvTo = null, TypeNode ReturnType = null);

