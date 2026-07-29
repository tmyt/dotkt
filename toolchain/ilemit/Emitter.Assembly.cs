// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Toolchain;

// EmitAssembly passes 1-6 (DefineType/bases/signatures/bodies/.cctor/entry/bake), bridges, and Save.
sealed partial class Emitter
{
    public void EmitAssembly(List<JsonElement> files)
    {
        // #84 Phase 4: run the in-process CIR SANITY gate at the CIR boundary, BEFORE any emit — malformed CIR
        // (undeclared local, dangling goto, missing owner) fails LOUD with a precise `sanity: <invariant>` message
        // (routed through Phase 1's diagnostic) instead of a cryptic Reflection.Emit crash / silent BadImageFormat.
        // See Emitter.Sanity.cs. Pure fail-fast validation — no IL effect (a valid CIR is byte-identical after it).
        CheckCir(files);
        // NOTE (R-1, reverse-interop): the emitted assembly's core type refs point at System.Private.CoreLib (the
        // impl assembly) because BCL types resolve via runtime reflection (typeof/Type.GetType, ~176 sites). A
        // standalone exe runs fine and any .NET host can reflection-load it (samples/il-revinterop), but a C# project
        // that <Reference>s it at COMPILE time hits CS0012 (Object lives in the unreferenced System.Private.CoreLib).
        // Investigated 2026-06-21: adding a consumer <Reference> to System.Private.CoreLib does NOT work either
        // (CS0433 — attributes exist in both it and System.Runtime). The proper fix is to resolve ALL BCL types
        // through a MetadataLoadContext over the REFERENCE assemblies and pass that core to PersistedAssemblyBuilder,
        // so refs become System.Runtime — a large, contained refactor (every typeof(Bcl) -> mlc lookup). Tracked #50.
        var ab = new PersistedAssemblyBuilder(new AssemblyName(_asmName), typeof(object).Assembly);
        // Assembly provenance: the emitter owns the final assembly in BOTH SDK and direct-CLI flows, so it stamps an
        // explicit, versioned DotKt protocol marker here (not in MSBuild-only SDK plumbing). dll2klib requires this
        // signal together with compiler-generated embedded metadata carriers before applying Kotlin-only reverse maps.
        const string dotKtMarkerKey = "DotKt.Compiler";
        const string dotKtMarkerValue = "metadata-v1";
        var assemblyMetadataCtor = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) });
        ab.SetCustomAttribute(new CustomAttributeBuilder(
            assemblyMetadataCtor, new object[] { dotKtMarkerKey, dotKtMarkerValue }));
        // The frontend stdlib KLIB is the authoritative Kotlin declaration surface. Mark both CLR stdlib twins so
        // generic CLR-reference projectors can route them away from dll2klib without guessing from assembly names.
        if (_stdlibAssembly)
            ab.SetCustomAttribute(new CustomAttributeBuilder(
                assemblyMetadataCtor, new object[] { "DotKt.LibraryKind", "stdlib" }));
        _mod = ab.DefineDynamicModule(_asmName);
        // #71 S2: the DotKt.Runtime.CompilerServices.* + System.Runtime.CompilerServices.Nullable{,Context} attribute
        // CLASSES are now ordinary CIR type decls (bir2cir's synthetic `000-dotkt-roundtrip-attrs` file); pass 1 below
        // defines them like any type. No EnsureKotlinAttrs.

        // Pass 1: DefineType for every file-static-class and every user class.
        foreach (var file in files)
        {
            var fileClass = file.GetProperty("fileClass").GetString();
            // Create the file class if it has methods OR top-level static fields — a file that only declares
            // top-level `val`/`var`s (no functions) still needs its class so OTHER files can reference those
            // fields (`StateKt.counter`); otherwise cross-file top-level property access fails (item 11).
            if (file.GetProperty("methods").GetArrayLength() > 0 ||
                (file.TryGetProperty("fields", out var ffl) && ffl.GetArrayLength() > 0))
                _types[fileClass] = new TypeInfo
                {
                    TB = _mod.DefineType(fileClass, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract),
                    Def = file, IsFileClass = true, FileElem = file,
                };
            if (file.TryGetProperty("types", out var ts))
                foreach (var t in ts.EnumerateArray())
                {
                    var name = t.GetProperty("name").GetString();
                    var kind = t.GetProperty("kind").GetString();
                    // #68: `generated:true` is the STRUCTURAL compiler-generated flag (a #37-freeze win over the retired
                    // `name.StartsWith("dotkt$")` string-sniff). kotc/bir2cir stamp it on every synthetic type def
                    // (closures, ref cells, CharSequence, ClrH helpers, lifted anon/local/property-reference classes).
                    var generated = t.TryGetProperty("generated", out var genEl) && genEl.GetBoolean();
                    // Shared synthetic types (`dotkt$CharSequence`/…) are emitted
                    // identically by EVERY file that uses them; in a multi-file assembly they'd redefine the same name
                    // and collide in `_types` (orphaning a TypeBuilder -> Save crash). They're structurally identical,
                    // so the first definition serves all references — skip the duplicates. (Per-file-DISTINCT synthetics
                    // — closures, ref cells, seq SMs — are uniquely named by BirEmitter, so they never land here.)
                    if (generated && _types.ContainsKey(name)) continue;
                    // Canonicalization: a shared synthetic ALREADY defined (public) by a REFERENCED assembly (the rt
                    // stdlib dll) is REFERENCED, not re-emitted here — else the app's copy is a DISTINCT CLR type from
                    // the rt dll's, so a value crossing the app<->rt boundary (a stdlib CharSequence-extension receiving
                    // an app value) fails interface dispatch (EntryPointNotFound). Skipping the local definition routes
                    // every `@dotkt$X` reference through MapType/FindMethod/AddInterfaceImplementation -> ResolveType,
                    // which resolves it as the external canonical type in the --ref'd assembly. Scoped to the
                    // verified-safe set (CharSequence); the other shared synthetics (Result/KProperty) still
                    // re-emit per-assembly until each is verified cross-assembly. Self-correcting:
                    // only skips when the type ACTUALLY resolves externally, so a --no-stdlib build (or the stdlib's own
                    // ref/rt build, which passes ilemit no --ref) still emits the canonical copy locally.
                    if (CanonicalSynthetics.Contains(name) && ResolvesExternally(name)) continue;
                    if (kind == "enum")
                    {
                        // A real .NET enum: each entry is a literal field of the int-backed enum.
                        var eb = _mod.DefineEnum(name, TypeAttributes.Public, typeof(int));
                        var eti = new TypeInfo { EB = eb, Def = t, IsEnum = true };
                        foreach (var en in t.GetProperty("entries").EnumerateArray())
                            eti.Fields[en.GetProperty("name").GetString()] =
                                (FieldBuilder)eb.DefineLiteral(en.GetProperty("name").GetString(), en.GetProperty("ordinal").GetInt32());
                        _types[name] = eti;
                        continue;
                    }
                    var isIface = kind == "interface";
                    var visStr = t.TryGetProperty("vis", out var tv) ? tv.GetString() : "public";
                    // A nested type (`nestedIn`) is defined on the enclosing type's builder with Nested* access, so it
                    // keeps CLR access to the enclosing type's private members; otherwise a top-level Public/NotPublic.
                    var nested = t.TryGetProperty("nestedIn", out var niEl) && _types.TryGetValue(niEl.GetString(), out var parentTi);
                    var typeAccess = nested
                        ? visStr switch
                        {
                            "internal" => TypeAttributes.NestedAssembly,
                            "protected" => TypeAttributes.NestedFamily,
                            "protectedInternal" => TypeAttributes.NestedFamORAssem,
                            "private" => TypeAttributes.NestedPrivate,
                            _ => TypeAttributes.NestedPublic,
                        }
                        : (visStr == "public" ? TypeAttributes.Public : TypeAttributes.NotPublic);
                    var attrs = isIface
                        ? typeAccess | TypeAttributes.Interface | TypeAttributes.Abstract
                        : typeAccess | TypeAttributes.Class;
                    // An `abstract`/`sealed`(Kotlin) class -> a CLR abstract class (cannot be instantiated; may hold
                    // abstract members). Kotlin `sealed` is also abstract at the CLR level.
                    if (!isIface && t.TryGetProperty("abstract", out var clsAbs) && clsAbs.GetBoolean()) attrs |= TypeAttributes.Abstract;
                    // `final:true` -> TypeAttributes.Sealed (CLR-final, not Kotlin `sealed`). bir2cir sets it on the
                    // round-trip attribute-class defs (#71 S2), matching the old embedded `NotPublic | Sealed | Class`.
                    if (!isIface && t.TryGetProperty("final", out var clsFin) && clsFin.GetBoolean()) attrs |= TypeAttributes.Sealed;
                    // A generic type's CLR metadata name carries its arity (`Box`1`) — Reflection.Emit does NOT append
                    // it, and a cross-assembly consumer resolves the type by that standard name (`GetType("Box`1")`).
                    // The `_types` registry key stays the bare BIR name (`Box`), so same-assembly references are intact.
                    var arity = t.TryGetProperty("typeParams", out var tpArity) ? tpArity.GetArrayLength() : 0;
                    var simpleName = nested && name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                    var metaName = arity > 0 ? simpleName + "`" + arity : simpleName;
                    var tb = nested ? _types[niEl.GetString()].TB.DefineNestedType(metaName, attrs) : _mod.DefineType(metaName, attrs);
                    // #68: a `generated:true` type (KProperty/CharSequence/closure/ref-cell/ClrH/lifted-anon) gets
                    // [CompilerGenerated] — the STANDARD generated signal, read from the structured flag (no `dotkt$`
                    // name-sniff). dll2klib skips these purely by the attribute; the `dotkt_` name prevents source collision.
                    if (generated) StampCompilerGenerated(tb);
                    var nti = new TypeInfo
                    {
                        TB = tb,
                        Def = t,
                        IsInterface = isIface,
                        BaseFqn = t.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object
                            && DotKt.Bir.TypeNode.Read(b) is DotKt.Bir.TypeNode.Fqn bf ? bf : null,
                        BaseName = t.TryGetProperty("base", out var b2)
                            ? (b2.ValueKind == JsonValueKind.String ? b2.GetString() : SlotName(b2)) : null,
                    };
                    // Generic type `class Box<T>`: define its type parameters now so member signatures (pass 3) resolve.
                    // (Constraints are applied in pass 2, once every type — possibly referenced by a bound — exists.)
                    if (t.TryGetProperty("typeParams", out var tps) && tps.GetArrayLength() > 0)
                    {
                        var names = TpNames(tps);
                        var gps = tb.DefineGenericParameters(names);
                        for (int gi = 0; gi < names.Length; gi++) nti.TypeParams[names[gi]] = gps[gi];
                    }
                    _types[name] = nti;
                }
        }

        // Bake enums up front: their literals are fully defined in pass 1, and baking now gives a real metadata
        // token usable in other types' IL (box/castclass/ldtoken) — an un-baked EnumBuilder token breaks the PE.
        foreach (var ti in _types.Values)
            if (ti.IsEnum) ti.Created = ti.EB.CreateType();

        // Pass 2: set parents and interface implementations (DefineGenericParameters already ran in pass 1, so a
        // generic base/interface that references the type's own params resolves).
        foreach (var ti in _types.Values)
        {
            T($"pass2 parent/iface: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);
            // Bounds may reference any type (now all defined) and the type's own params (now in _curTypeParams).
            if (ti.IsGeneric && ti.Def.TryGetProperty("typeParams", out var tps2)) ApplyConstraints(tps2, ti.TypeParams, ti.IsInterface, ti.Def);
            if (ti.BaseName != null)
            {
                // A `.NET` base (`clr:System.Exception` / `clrg:...[..]`) is resolved by reflection; a Kotlin-user
                // base is another TypeBuilder in `_types`.
                if (ti.BaseName.StartsWith("clr:") || ti.BaseName.StartsWith("clrg:")) ti.TB.SetParent(ti.ClrBase = MapType(ti.BaseName));
                else
                {
                    // A constructed user base (`AbstractList[tv E]` / `AbstractCoroutineContextKey[..concrete..]`) carries its
                    // ACTUAL resolved type args in the CIR — the emitting layer (kotc `ownerSpec`) always supplies them, so
                    // ilemit INSTANTIATES it via ParseOwner (`MakeGenericType` on the carried args) and does NOT re-derive
                    // them. The BIR keeps the bare open name in `BaseName` so FindMethod still walks the base chain by bare
                    // name for inherited members (e.g. AbstractIterator.setNext).
                    var (bopen, bconstructed) = ti.BaseFqn != null ? ParseOwnerT(ti.BaseFqn) : ParseOwner(ti.BaseName);
                    if (bconstructed != null)
                    {
                        ti.TB.SetParent(bconstructed);
                        // An external referenced generic base (open name not emitted here) is a REFERENCED .NET base —
                        // record it as ClrBase so the base-ctor emission calls its ctor, not object's.
                        if (!_types.ContainsKey(bopen)) ti.ClrBase = bconstructed;
                    }
                    else if (_types.TryGetValue(bopen, out var baseTi))
                    {
                        var baseTb = baseTi.TB;
                        var bArity = baseTb.IsGenericTypeDefinition ? baseTb.GetGenericArguments().Length : 0;
                        // A generic base MUST arrive with its type args carried (bconstructed != null above). If it reaches
                        // here arg-less with arity>0, the emitting layer (bir2cir/kotc) dropped them — ilemit does NOT infer
                        // base args from the subclass's own params (that positional inference silently mis-constructed a base
                        // whose args differ from the subclass's tv, e.g. AbstractCoroutineContextKey). Fail loud so the
                        // missed producer is diagnosable, not silently mis-built into an open-generic parent (invalid: the
                        // CLR rejects an un-instantiated generic definition as a parent at type-load — TypeLoadException).
                        if (bArity > 0)
                            throw new InvalidOperationException(
                                $"generic base '{bopen}' (arity {bArity}) emitted without type args on '{ti.TB.Name}' — " +
                                "the emitting layer dropped them; ilemit does not infer base args");
                        ti.TB.SetParent(baseTb);
                    }
                    // A bare external .NET base (kotc's pure-FQN output for a non-`clr:`-marked .NET supertype, e.g.
                    // `System.Exception` via dll2klib `import`): not in `_types`, so resolve it by reflection. Record it
                    // as ClrBase — WITHOUT this the base-ctor emission has no external base and falls to `object::.ctor`,
                    // producing a `class : System.Object` (not the declared base) and an unchained base ctor (ilverify
                    // CallCtor/ThisUninitReturn). Pre-flip the `clr:`-marked base set ClrBase at the branch above.
                    else ti.TB.SetParent(ti.ClrBase = ResolveType(bopen));
                }
            }
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
            {
                var declaredIfaces = new List<Type>();
                foreach (var i in ifs.EnumerateArray())
                {
                    if (ReadFqn(i) is not DotKt.Bir.TypeNode.Fqn iFqn) continue;
                    // A REFERENCED interface (not in `_types` — a .NET Continuation<int>) is resolved by reflection; an
                    // emitted Kotlin interface (`Container<int>`) comes from `_types` (constructed via ParseOwnerT).
                    Type itype;
                    if (!_types.ContainsKey(iFqn.Name)) itype = MapType(iFqn);
                    else { var (open, constructed) = ParseOwnerT(iFqn); itype = constructed ?? (Type)_types[open].TB; }
                    ti.TB.AddInterfaceImplementation(itype);
                    declaredIfaces.Add(itype);
                }
                // (#75) A user class implementing an invariant MUTABLE collection interface (MutableList/MutableCollection/
                // MutableSet -> IList<T>/ICollection<T>/ISet<T>) must ALSO list the READONLY sibling
                // (IReadOnlyList<T>/IReadOnlyCollection<T>) so the arg-position ref->ref castclass (EmitArg) is TOTAL:
                // a user mutable-collection value can then be passed into a readonly slot. BCL List/HashSet already list
                // both faces; a user class may declare only the mutable face. The readonly members (get_Item/Count/
                // GetEnumerator) are satisfied implicitly by the mutable face's already-wired public virtual methods, so
                // no extra override wiring is needed. Skip a sibling already present in the declared interface list.
                foreach (var sib in declaredIfaces.SelectMany(ReadonlyCollectionSiblings).Distinct())
                    if (!declaredIfaces.Contains(sib)) ti.TB.AddInterfaceImplementation(sib);
            }
        }
        _curTypeParams = null;

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsEnum) continue;   // enums are fully defined (literals) in pass 1
            T($"pass3 signatures: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);   // so `gp:T` in field/ctor/method signatures resolves
            if (ti.IsFileClass)
            {
                // Top-level `val`/`var` -> static fields of the file class.
                if (ti.Def.TryGetProperty("fields", out var ffs))
                    foreach (var f in ffs.EnumerateArray())
                    {
                        var tlType = MapType(f.GetProperty("type"));
                        var tlAttrs = FieldAttributes.Public | FieldAttributes.Static;
                        // `@kotlin.concurrent.Volatile` on a top-level `var` -> a `modreq(IsVolatile)` static field.
                        var tlFb = f.TryGetProperty("volatile", out var tlVol) && tlVol.GetBoolean()
                                ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), tlType, tlAttrs)
                                : ti.TB.DefineField(f.GetProperty("name").GetString(), tlType, tlAttrs);
                        StampMemberAttrs(tlFb.SetCustomAttribute, f);   // [KotlinReadOnly]/[KotlinSuspendFunctionType]/… (bir2cir-generated)
                        ti.Fields[f.GetProperty("name").GetString()] = tlFb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: true);
            }
            else
            {
                // A class emits instance + static fields; an INTERFACE carries ONLY hoisted companion STATICS (a CLR
                // interface may hold static fields but never instance state), so skip any non-static field there (#83).
                foreach (var f in ti.Def.GetProperty("fields").EnumerateArray())
                    {
                        if (ti.IsInterface && !(f.TryGetProperty("static", out var ifst) && ifst.GetBoolean())) continue;
                        // A property's visibility maps to the field's CLR access. True CLR-private is now correct
                        // because `inner`/`nested` classes are emitted as real nested types, which retain access to the
                        // enclosing type's privates. Any lifted cross-class protected access has already been made
                        // explicit by bir2cir as protectedInternal; this layer maps the CIR visibility 1:1.
                        var fattrs = (f.TryGetProperty("vis", out var fv) ? fv.GetString() : "public") switch
                        {
                            "private" => FieldAttributes.Private,
                            "internal" => FieldAttributes.Assembly,
                            "protected" => FieldAttributes.Family,
                            "protectedInternal" => FieldAttributes.FamORAssem,
                            _ => FieldAttributes.Public,
                        };
                        if (f.TryGetProperty("static", out var st) && st.GetBoolean()) fattrs |= FieldAttributes.Static;
                        var ftype = MapType(f.GetProperty("type"));
                        // `@kotlin.concurrent.Volatile` -> a `modreq(IsVolatile)` field (the C# `volatile` encoding).
                        var fb = f.TryGetProperty("volatile", out var vol) && vol.GetBoolean()
                            ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), ftype, fattrs)
                            : ti.TB.DefineField(f.GetProperty("name").GetString(), ftype, fattrs);
                        StampMemberAttrs(fb.SetCustomAttribute, f);   // [KotlinReadOnly]/[KotlinSuspendFunctionType]/… (bir2cir-generated)
                        ti.Fields[f.GetProperty("name").GetString()] = fb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: false);
                // Real CLR properties: DefineProperty over the already-declared get_/set_ accessor methods, so a Kotlin
                // property is seen as a PROPERTY (not a bare field/methods) by C#/F#/reflection. Additive — only fires
                // when kotc emits the `properties` metadata. See docs/design-clr-property-model.md.
                if (ti.Def.TryGetProperty("properties", out var props))
                    foreach (var p in props.EnumerateArray())
                    {
                        var pb = ti.TB.DefineProperty(p.GetProperty("name").GetString(), PropertyAttributes.None, MapType(p.GetProperty("type")), null);
                        if (p.TryGetProperty("get", out var g) && g.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(g.GetString(), out var gm)) pb.SetGetMethod(gm);
                        if (p.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(s.GetString(), out var sm)) pb.SetSetMethod(sm);
                        StampMemberAttrs(pb.SetCustomAttribute, p);   // [KotlinSuspendFunctionType]/… (bir2cir-generated)
                    }
                // Synthesized field-like .NET events (§4.2, #187): DefineEvent over the already-declared add_/remove_/raise_
                // accessors, so a C#/reflection consumer sees a real `.event D E`. The accessors ALSO satisfy the interface
                // add_/remove_ slots (wired by the referenced-interface binding pass below). bir2cir stamped `clrEventDecl`.
                if (ti.Def.TryGetProperty("clrEvents", out var clrEvs) && clrEvs.ValueKind == JsonValueKind.Array)
                    foreach (var ev in clrEvs.EnumerateArray())
                    {
                        var evName = ev.GetProperty("name").GetString();
                        var eb = ti.TB.DefineEvent(evName, EventAttributes.None, MapType(ev.GetProperty("delegateType")));
                        if (ti.Methods.TryGetValue("add_" + evName, out var am)) eb.SetAddOnMethod(am);
                        if (ti.Methods.TryGetValue("remove_" + evName, out var rm)) eb.SetRemoveOnMethod(rm);
                        if (ti.Methods.TryGetValue("raise_" + evName, out var rsm)) eb.SetRaiseMethod(rsm);
                    }
                EnsureCtorsDefined(ti);
            }
        }

        // Link interface implementations: every class method that satisfies an interface method. For a constructed
        // generic interface `Container[int]`, the override target is the method on the instantiation (static helper).
        // Iterate with the registry KEY (the BIR/full name, e.g. `p.Impl` for a packaged type, `Box` for a generic):
        // FindMethod looks the type up in `_types` by that key, NOT by `ti.TB.Name` (the *simple* name, which only
        // coincides with the key for a non-generic root-package type — so namespaced/generic types broke with KeyNotFound).
        // C3b reverse bridge: now that the Kotlin Iterator interface's hasNext/next exist, emit the IEnumerator adapter
        // (once) so qualifying classes' generated GetEnumerator can reference it. Emitter.ReverseBridge.cs.
        EmitEnumeratorAdapter();
        foreach (var (typeKey, ti) in _types)
            if (!ti.IsFileClass && !ti.IsInterface && ti.Def.TryGetProperty("interfaces", out var ifs))
            {
                _curTypeParams = EffectiveTps(ti);
                // Worklist over the class's interfaces INCLUDING transitively-inherited ones (a Kotlin interface method
                // can be inherited through a chain, e.g. MonotonicTimeSource : WithComparableMarks : TimeSource — the
                // covariant markNow over TimeSource.markNow must be bridged too, or the slot stays unimplemented).
                // The interface entries are STRUCTURED Fqn nodes (birType-emitted). ilemit DERIVES the "referenced-vs-
                // emitted" decision from the name (`_types` membership), not a clr:/clrg: marker.
                // (spec, viaBaseClass): a `viaBaseClass` interface is one this class implements ONLY through its emitted
                // base-class chain (some intermediate base leaves an interface method as the default). We still wire THIS
                // class's OWN override into that inherited interface slot (#185: a grandchild overriding an interface DIM
                // that the intermediate class did not override — otherwise the grandchild's method is a fresh unlinked slot
                // and virtual/interface dispatch falls through to the DIM default), but we must NOT emit the DIM forward-
                // bridge / GetEnumerator adapter for it (the base class already did). A DIRECTLY-declared interface keeps
                // full handling. ECMA-335 II.12.2: the most-derived per-type MethodImpl wins over the DIM fallback.
                var ifWork = new Queue<(DotKt.Bir.TypeNode.Fqn spec, bool viaBaseClass)>();
                var ifSeen = new HashSet<string>();
                foreach (var i in ifs.EnumerateArray())
                    if (ReadFqn(i) is DotKt.Bir.TypeNode.Fqn iff) ifWork.Enqueue((iff, false));
                // Interfaces inherited through the EMITTED base-class chain, type args substituted into THIS class's frame
                // (a generic base `Shape<T> : I<T>` under `Square : Shape<int>` yields `I<int>`). Enqueued AFTER the direct
                // interfaces so a spec implemented BOTH ways is processed as direct (viaBaseClass=false, ifSeen dedup).
                // `chainArgs` are the current base's actual type args expressed in THIS class's frame; each descent re-
                // anchors the next base's args (stated in the current base's frame) back through `chainArgs`.
                var chainName = ti.BaseName;
                var chainArgs = ti.BaseFqn?.Args;
                while (chainName != null && _types.TryGetValue(BareTypeKey(chainName), out var bti) && !bti.IsInterface)
                {
                    if (bti.Def.ValueKind == JsonValueKind.Object && bti.Def.TryGetProperty("interfaces", out var bifs))
                        foreach (var bi in bifs.EnumerateArray())
                            if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bbi && SubstTv(bbi, chainArgs) is DotKt.Bir.TypeNode.Fqn bbiF)
                                ifWork.Enqueue((bbiF, true));
                    chainArgs = bti.BaseFqn?.Args?.Select(a => SubstTv(a, chainArgs)).ToArray();
                    chainName = bti.BaseName;
                }
                while (ifWork.Count > 0)
                {
                    var (specFqn, viaBaseClass) = ifWork.Dequeue();
                    var spec = SigCanon(specFqn);            // the canonical overload/dedup key for this interface spec
                    var specName = specFqn.Name;
                    if (!ifSeen.Add(spec)) continue;
                    // The reverse GetEnumerator bridge fires below on a `clr:`/`clrg:` collection interface (the form
                    // bir2cir lowers Kotlin Set/MutableCollection/List/... to in every runnable build). ilemit holds NO
                    // Kotlin-collection-name knowledge — the Kotlin↔CLR identity was consumed upstream.
                    // A canonicalized shared synthetic (`dotkt$CharSequence`) this app REFERENCES from the rt stdlib
                    // dll — NOT re-emitted here, so absent from `_types` — is an EXTERNAL interface: bind the class's
                    // overrides to it by reflection, exactly like a `clr:` interface, so the interface slots are wired
                    // explicitly rather than relying on an implicit name/sig match a canonicalized supertype must not
                    // depend on. (Covers both a user `class S : CharSequence` and the synthesized `dotkt$StringCharSequence`.)
                    // Checked on the RAW spec (a canonical synthetic interface spec is the bare name), so a `clr:`/`clrg:`
                    // spec is NOT ParseOwner'd here — doing so eagerly mis-strips a `clrg:` self-ref interface (crash).
                    bool externalSynthIface = CanonicalSynthetics.Contains(specName)
                        && !_types.ContainsKey(specName) && ResolvesExternally(specName);
                    // A REFERENCED interface (not emitted in THIS assembly — a .NET-mapped Continuation<int>, or an
                    // external canonical synthetic): bind each interface method to the class method of the same .NET name
                    // by reflection. An EMITTED interface (in `_types`) falls to the ParseOwner path below.
                    if (!_types.ContainsKey(specName) || externalSynthIface)
                    {
                        var itype = externalSynthIface ? ResolveType(specName) : MapType(specFqn);
                        // C3b reverse bridge: if this is a @Clr collection interface (IEnumerable<E>-derived) and the
                        // class has only a Kotlin iterator(), synthesize GetEnumerator (handles the two overloads itself).
                        // Self-guards (idempotent + only when THIS class declares its own iterator()), so it is safe for a
                        // viaBaseClass interface too: a grandchild that overrides iterator() over an abstract Iterable base
                        // (whose abstract iterator produced no base GetEnumerator) still gets its adapter; a non-overriding
                        // child no-ops and inherits the base's.
                        GenerateGetEnumeratorIfNeeded(ti, itype);
                        var have = ti.Methods.Keys.ToHashSet();
                        // A SELF-REFERENTIAL constructed generic interface (e.g. `V : IComparable<V>`, V the emitted
                        // type) is a TypeBuilderInstantiation whose .GetMethods() throws. Enumerate the OPEN
                        // definition's methods and re-anchor each to the instantiation via TypeBuilder.GetMethod
                        // (same pattern as the self-ref base-ctor below).
                        // A constructed generic interface whose OPEN def is a TypeBuilder (a self-ref `V : IComparable<V>`,
                        // OR a generic STDLIB interface instantiated even with a concrete arg) is a TypeBuilderInstantiation
                        // whose .GetMethods() throws. Try GetMethods; on failure, enumerate the OPEN definition's methods
                        // and re-anchor each to the instantiation via TypeBuilder.GetMethod.
                        MethodInfo[] ifaceMs; bool reanchor;
                        try { ifaceMs = itype.GetMethods(); reanchor = false; }
                        catch (NotSupportedException) { ifaceMs = itype.GetGenericTypeDefinition().GetMethods(); reanchor = true; }
                        foreach (var im in ifaceMs)
                        {
                            if (im.Name == "GetEnumerator" || !have.Contains(im.Name)) continue;   // GetEnumerator: handled by the reverse bridge above
                            // OVERLOADED body methods (e.g. the generic CompareTo(V) + the non-generic IComparable bridge
                            // CompareTo(object)) collide in the name-keyed ti.Methods — wiring the wrong one to the slot
                            // is a TypeLoad "signature ... do not match". Disambiguate by the interface method's
                            // (instantiation-substituted) parameter types against each overload's recorded params.
                            var body = ti.Methods[im.Name];
                            // The interface method's (instantiation-substituted) param + return types — used both to
                            // disambiguate an overloaded body AND to decide whether the body needs a return-adapting bridge.
                            var ips = im.GetParameters().Select(p => reanchor
                                ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                                : p.ParameterType).ToArray();
                            var cands = ti.MethodsBySig.Values.Where(b => b.Name == im.Name).Distinct().ToList();
                            if (cands.Count > 1)
                            {
                                var match = cands.FirstOrDefault(b => _mparams.TryGetValue(b, out var bps)
                                    && bps.Length == ips.Length
                                    && bps.Zip(ips, SlotParamMatches).All(x => x));
                                if (match == null) continue;   // no exact overload -> skip rather than mis-wire
                                body = match;
                            }
                            var ifaceM = reanchor ? TypeBuilder.GetMethod(itype, im) : im;
                            // A @ClrTypeAlias'd (referenced) interface slot whose return type the Kotlin body DROPS: the
                            // Kotlin member returns a value but the BCL slot is void (MutableCollection.add():Boolean ->
                            // ICollection.Add():void; MutableList.set/removeAt:E -> IList.set_Item/RemoveAt():void). A DIRECT
                            // methodimpl then fails the CLR's exact-signature rule ("Signature of the body and declaration in a
                            // method implementation do not match" -> the whole type, and every subclass like ArrayDeque, is
                            // UNLOADABLE, surfacing at the referencing app as "cannot resolve .NET type"). Emit a void bridge
                            // that calls the body and pops the dropped return, and carry the methodimpl on it — the referenced-
                            // interface twin of EmitCovariantBridge (which already handles this in the emitted-interface path).
                            // Guarded to the void-DROP case only (the confirmed family); a generic body directly overrides its
                            // generic slot (never a bridge), and same-return slots keep the direct, byte-identical override.
                            // `void` is substitution-invariant, so the slot's return needs no SubstituteIfaceArgs re-anchor.
                            bool bodyIsGeneric = body is MethodBuilder gmb && _methodTypeParams.ContainsKey(gmb);
                            if (!bodyIsGeneric && im.ReturnType == typeof(void) && body.ReturnType != typeof(void))
                                EmitVoidDropBridge(ti, im.Name, ips, body, ifaceM);
                            else
                                ti.TB.DefineMethodOverride(body, ifaceM);
                        }
                        continue;
                    }
                    var (open, constructed) = ParseOwnerT(specFqn);
                    if (!_types.TryGetValue(open, out var iface)) continue;
                    // The interface's instantiation args (the concrete args at this implementer): `Comparable<Self>` ->
                    // [Self]. An interface method's declared type names the INTERFACE's OWN params as `Tv{type,i}`;
                    // SubstTv re-anchors each to specArgs[i] so it matches the class's own (concrete) member signature.
                    var specArgs = specFqn.Args;
                    // Transitively process this interface's base interfaces too, substituting the type args through the
                    // chain (e.g. WithComparableMarks : TimeSource, or List<object> : Collection<object>).
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("interfaces", out var baseIfs))
                        foreach (var bi in baseIfs.EnumerateArray())
                            if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bi0 && SubstTv(bi0, specArgs) is DotKt.Bir.TypeNode.Fqn biF) ifWork.Enqueue((biF, viaBaseClass));
                    // Iterate the interface's method DEFS (not the name-keyed iface.Methods) so OVERLOADED interface
                    // methods (e.g. MutableMap.remove(K):V vs the JVM remove(K,V):Boolean) each resolve to their own
                    // builder by signature, and to the matching body overload by TYPE-ARG-SUBSTITUTED signature. A miss
                    // when the name is AMBIGUOUS (multiple body overloads) is skipped — wiring the wrong one is the bug.
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("methods", out var ifMs))
                        foreach (var imDef in ifMs.EnumerateArray())
                        {
                            if (!imDef.TryGetProperty("name", out var imn) || !imDef.TryGetProperty("params", out _)) continue;
                            var imName = imn.GetString();
                            var ifaceBuilder = iface.MethodsBySig.TryGetValue(SigKey(imName, imDef), out var ib) ? ib
                                             : (iface.Methods.TryGetValue(imName, out var ib2) ? ib2 : null);
                            if (ifaceBuilder == null) continue;
                            // The interface method's params with each Tv{type,i} re-anchored to specArgs[i], rendered to
                            // the sig-token spelling — matched against the class's own MethodsBySig (a nested value-class
                            // arg like Continuation.resumeWith(Result<T>) substitutes correctly, not just a bare gp).
                            var subSig = imName + "(" + string.Join(",", imDef.GetProperty("params").EnumerateArray()
                                .Select(p => SigCanon(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs)))) + ")";
                            var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
                            // A bir2cir-resolved exact MethodImpl bridge. The decision and exact slot signature are
                            // already CIR facts; this is mechanical consumption only. In particular, do not also wire
                            // the narrow Kotlin declaration, whose covariant return is not a byte-exact CLR MethodImpl.
                            var explicitBridge = FindExplicitInterfaceBridge(ti, specFqn, imName, subSig);
                            if (explicitBridge != null)
                            {
                                ti.TB.DefineMethodOverride(explicitBridge, ifaceMethod);
                                continue;
                            }
                            // Only wire an EXACT signature match. A miss means the class doesn't override this exact
                            // overload (e.g. it lacks the JVM remove(K,V):Boolean default) -> SKIP rather than mis-wire a
                            // different overload; for a Kotlin interface the same-name+sig method resolves implicitly anyway.
                            if (!ti.MethodsBySig.TryGetValue(subSig, out var bodyMethod))
                            {
                                // ...unless a DIRECT base interface provides this method as a DEFAULT (e.g. ValueTimeMark :
                                // ComparableTimeMark, which has compareTo(ComparableTimeMark) as a DIM): the CLR does NOT
                                // treat an interface DIM as implicitly implementing the base interface method (Comparable.
                                // compareTo), so the class slot stays unimplemented. Emit a class-level forwarding bridge
                                // that calls the inherited DIM and put the MethodImpl on it.
                                // viaBaseClass: this class only inherits the interface through its base — the base already
                                // emitted the DIM forward bridge for the not-overridden case; a second bridge here is wrong.
                                if (!ti.IsInterface && !viaBaseClass) TryEmitDimForwardBridge(ti, imDef, specArgs, subSig, constructed, ifaceBuilder);
                                continue;
                            }
                            // Covariant return: a NARROWED override return type (markNow():ValueTimeMark over the iface's
                            // :ComparableTimeMark) makes a direct MethodImpl fail the CLR's exact-return rule. Emit a bridge
                            // with the iface's (base) return type that calls the narrow body method and upcasts; put the
                            // MethodImpl on the bridge. The iface ret comes from imDef (BIR), Tv re-anchored to specArgs.
                            Type ifaceRet = null;
                            try { if (imDef.TryGetProperty("ret", out var rt)) ifaceRet = MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)); } catch { }
                            // Bridge only on a genuine type NARROWING (different type name) — not two reference-different
                            // instantiations of the SAME generic (Iterator<Object> vs Iterator<Object>), which match fine.
                            // A GENERIC body method (`fold<R>`) directly overrides the generic interface method (same
                            // arity+signature) — never a covariant bridge: the bridge is NON-generic, so a non-generic
                            // methodimpl body for a generic declaration fails the CLR's signature-match ("Signature of the
                            // body and declaration in a method implementation do not match" -> TypeLoadException). The
                            // spurious mismatch here is only because ifaceRet (a method-scope Tv `!!R`) resolves to
                            // `object` in this wiring context (ResolveTv has no method params in scope), NOT a real
                            // narrowing. Detect a generic MethodBuilder via _methodTypeParams (IsGenericMethodDefinition
                            // is unreliable on an un-baked builder).
                            var bodyIsGeneric = bodyMethod is MethodBuilder gmb && _methodTypeParams.ContainsKey(gmb);
                            if (!bodyIsGeneric && ifaceRet != null && bodyMethod.ReturnType != ifaceRet &&
                                ((bodyMethod.ReturnType.Name != ifaceRet.Name && !bodyMethod.ReturnType.IsValueType && !ifaceRet.IsValueType)   // covariant reference narrowing
                                 || (ifaceRet == typeof(void) && bodyMethod.ReturnType != typeof(void))))   // a BCL slot that DROPS the Kotlin return (MutableCollection.add():Boolean -> ICollection.Add():void, set/removeAt:E -> void)
                                EmitCovariantBridge(ti, imName, imDef, specArgs, bodyMethod, ifaceMethod, ifaceRet);
                            else
                                ti.TB.DefineMethodOverride(bodyMethod, ifaceMethod);
                        }
                }
            }

        // An INTERFACE with an EXTERNAL (clr:/clrg:) base interface — e.g. ComparableTimeMark : IComparable<CTM>
        // (via the Comparable alias) — must wire its own DEFAULT (bodied) method to the external base slot with an
        // explicit MethodImpl: unlike a class, an interface method does NOT implicitly implement a same-name+sig
        // base-interface method, so without the .override the DIM is an unrelated NewSlot and every implementing
        // class fails to LOAD ("Method 'CompareTo' in type 'ValueTimeMark' ... does not have an implementation").
        // The loader requires a MethodImpl body on an INTERFACE to be a FINAL method ("must be a final method"),
        // so the public (overridable) DIM can't carry the .override itself — emit C#'s explicit-impl shape: a
        // private final bridge that callvirts the DIM (keeping virtual dispatch for class overrides) and hangs
        // the MethodImpl on the bridge. Classes providing their own override still win ("most specific"), so
        // this only FILLS previously-unimplemented slots.
        foreach (var (_, ti) in _types)
        {
            if (!ti.IsInterface || ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var extIbs)) continue;
            _curTypeParams = EffectiveTps(ti);
            // Only a BODIED method (a DIM) can implement an external slot; an abstract redeclaration stays for the class.
            var bodied = new HashSet<string>();
            foreach (var m in ti.Def.GetProperty("methods").EnumerateArray())
                if (m.TryGetProperty("name", out var bn) && m.TryGetProperty("body", out var bb)
                    && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() > 0)
                    bodied.Add(bn.GetString());
            if (bodied.Count == 0) continue;
            // De-dup across a diamond (`I : A, B` with `A, B : C`): one methodimpl per (baseOwner :: subSig).
            var dimImplSeen = new HashSet<string>();
            foreach (var ib in extIbs.EnumerateArray())
            {
                if (ReadFqn(ib) is not DotKt.Bir.TypeNode.Fqn ibF) continue;
                // An EMITTED (same-assembly) base interface whose method THIS interface DEFAULTS (a DIM override) needs
                // the same explicit methodimpl an external base gets — the CLR does NOT implicitly wire a derived-
                // interface DIM to its base-interface slot (each `newslot` re-declaration is a DISTINCT slot), so without
                // it every IMPLEMENTER of this interface fails to LOAD ("Method 'get' ... does not have an
                // implementation"). Handled by a dedicated pass (the REFERENCED/.NET base path stays below, byte-identical).
                if (_types.ContainsKey(ibF.Name)) { EmitEmittedBaseDimImpls(ti, ibF, bodied, dimImplSeen); continue; }
                var itype = MapType(ibF);
                // A generic instantiation over an EMITTED TypeBuilder arg can't GetMethods() — enumerate the OPEN
                // definition and re-anchor each slot onto the instantiation (same pattern as the class wiring).
                MethodInfo[] ifaceMs; bool reanchor;
                try { ifaceMs = itype.GetMethods(); reanchor = false; }
                catch (NotSupportedException) { ifaceMs = itype.GetGenericTypeDefinition().GetMethods(); reanchor = true; }
                foreach (var im in ifaceMs)
                {
                    if (!bodied.Contains(im.Name) || !ti.Methods.TryGetValue(im.Name, out MethodBuilder dim)) continue;
                    var ips = im.GetParameters().Select(p => reanchor
                        ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                        : p.ParameterType).ToArray();
                    // Overload disambiguation by the slot's (substituted) param types — mirrors the class wiring.
                    var cands = ti.MethodsBySig.Values.Where(b => b.Name == im.Name).Distinct().ToList();
                    if (cands.Count > 1)
                    {
                        var match = cands.FirstOrDefault(b => _mparams.TryGetValue(b, out var bps)
                            && bps.Length == ips.Length && bps.Zip(ips, SlotParamMatches).All(x => x));
                        if (match == null) continue;   // no exact overload -> skip rather than mis-wire
                        dim = match;
                    }
                    var iret = reanchor ? SubstituteIfaceArgs(im.ReturnType, itype.GetGenericArguments()) : im.ReturnType;
                    var bridge = ti.TB.DefineMethod("dotkt$dimimpl$" + im.Name + "$" + (_covarBridge++),
                        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                        iret, ips);
                    StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
                    var bil = bridge.GetILGenerator();
                    bil.Emit(OpCodes.Ldarg_0);
                    for (int i = 0; i < ips.Length; i++) bil.Emit(OpCodes.Ldarg, i + 1);
                    var dimCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), dim) : (MethodInfo)dim;
                    bil.Emit(OpCodes.Callvirt, dimCall);
                    bil.Emit(OpCodes.Ret);
                    ti.TB.DefineMethodOverride(bridge, reanchor ? TypeBuilder.GetMethod(itype, im) : im);
                }
            }
        }
        _curTypeParams = null;

        // Pass 4: emit all bodies (every ctor/method signature already exists). Each body emit is GUARDED (#84 Phase 1):
        // a throw is re-tagged with the declaration being emitted (via CurrentDecl) so one bad method names itself in a
        // clean `ilemit: <Type>.<method>: <message>` line, and the rest are unaffected. Byte-identical on success.
        foreach (var ti in _types.Values)
            for (int ci = 0; ci < ti.Ctors.Count; ci++) { T($"pass4 ctor body: {ti.TB?.Name}#{ci}"); var cb = ti.Ctors[ci]; var cd = ti.CtorDefs[ci]; GuardBody(() => EmitCtorBody(ti, cb, cd)); }
        foreach (var ti in _types.Values)
            if (!ti.IsEnum)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray())
                {
                    // Interfaces: emit an IL body ONLY for default methods (those that carry one); abstract slots have none.
                    if (ti.IsInterface && !(m.TryGetProperty("body", out var ib) && ib.ValueKind == JsonValueKind.Array && ib.GetArrayLength() > 0)) continue;
                    T($"pass4 method body: {ti.TB?.Name}.{(m.TryGetProperty("name", out var mn) ? mn.GetString() : "?")}"); GuardBody(() => EmitMethodBody(ti, m));
                }

        // User annotations -> .NET custom attributes, applied on the type and its methods (the ctor builder of the
        // synthesized `: System.Attribute` class already exists). Args are compile-time constants.
        foreach (var ti in _types.Values)
        {
            // #71 S2: EVERY attribute here — user annotations AND the Kotlin round-trip metadata ([NullableContext]/
            // [KotlinFileClass]/[KotlinFunInterface]/[KotlinSealed] on the type; [KotlinFunction]/[KotlinInline] on
            // methods; [Nullable]/[KotlinSuspendFunctionType] in return position) — is an ordinary CIR `attrs`/`retAttrs`
            // entry that bir2cir (RoundtripMetadata) generated. ilemit only STAMPS them dumbly through BuildCab; the
            // Kotlin-semantic DECISION (which modifier -> which attribute) lives in bir2cir. A runtime-build CIR carries
            // none (the pass is skipped there), so there is nothing to strip.
            if (ti.TB != null && ti.Def.TryGetProperty("attrs", out var tattrs))
                foreach (var a in tattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) ti.TB.SetCustomAttribute(cab); }
            if (ti.Def.TryGetProperty("methods", out var ms))
                foreach (var m in ms.EnumerateArray())
                {
                    bool hasA = m.TryGetProperty("attrs", out var mattrs) && mattrs.ValueKind == JsonValueKind.Array && mattrs.GetArrayLength() > 0;
                    bool hasR = m.TryGetProperty("retAttrs", out var rattrs) && rattrs.ValueKind == JsonValueKind.Array && rattrs.GetArrayLength() > 0;
                    if (!hasA && !hasR) continue;
                    // Resolve the target MethodBuilder by SIGNATURE first (MethodsBySig), name-only second — overloaded
                    // methods (sin(Double)+sin(Float), append(...), println(...)) share a name, so a name-only lookup
                    // collides on the last-declared overload: every def's attrs land on that ONE builder while the other
                    // overloads get NONE (this dropped @ClrIntrinsic from all-but-last overloads in the ref.dll, which
                    // bir2cir reads as its binding source).
                    var mname = m.GetProperty("name").GetString();
                    if (!ti.MethodsBySig.TryGetValue(SigKey(mname, m), out var mb) && !ti.Methods.TryGetValue(mname, out mb)) continue;
                    if (hasA)
                        foreach (var a in mattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) mb.SetCustomAttribute(cab); }
                    // Return-position attrs ride the return parameter (position 0), defined once.
                    if (hasR)
                    {
                        var retPb = mb.DefineParameter(0, ParameterAttributes.None, null);
                        foreach (var a in rattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) retPb.SetCustomAttribute(cab); }
                    }
                }
        }

        // Pass 4b: static-field initializers (companion `val`s) -> a type initializer (.cctor). An INTERFACE with a
        // flattened companion (#83) also gets a .cctor for its static fields — a CLR interface legally has one.
        foreach (var ti in _types.Values)
        {
            if (!ti.Def.TryGetProperty("fields", out var fs)) continue;
            // A `lateinit` (or otherwise initializer-less) static field carries `"init": null` — the key is PRESENT
            // but its value is JSON null, so a bare TryGetProperty("init", …) sees "has init" and would feed a null
            // element to EmitStoreCoerced -> EmitExpr (crash). Such a field needs no .cctor store: a static reference
            // slot defaults to null, and a `lateinit` read goes through the `lateinitGet` not-initialized check. So
            // only fields with a NON-null init value get a type-initializer store.
            var inits = fs.EnumerateArray().Where(f => f.TryGetProperty("init", out var iv) && iv.ValueKind != JsonValueKind.Null && f.TryGetProperty("static", out var s) && s.GetBoolean()).ToList();
            if (inits.Count == 0) continue;
            _il = ti.TB.DefineTypeInitializer().GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = typeof(void);
            // A field initializer can contain CFG control flow (a `while`/`when` lowered to label/goto), so its labels
            // must be pre-defined just like a method body — otherwise MarkLabel/Br throws "key not in _cfgLabels".
            // Coerce the init value to the field's declared type (box a value-type/enum RHS stored into an
            // `object`/wider reference field) — the SAME shared store coercion the method-body sites use; without
            // it, `val X: Any = SomeEnum.ENTRY` stored the raw ordinal (int) into an object field as a null ref.
            // #84: the type initializer emits USER initializer expressions (which may carry full CFG), so guard each
            // field's emit with a `.cctor(<field>)` breadcrumb — a throw here names the failing field like a body throw.
            _ctxType = ti.TB?.Name; _ctxNode = null; _ctxPos = PosOf(ti.Def);   // #112 P2: type decl's source pos
            foreach (var f in inits)
            {
                var fname = f.GetProperty("name").GetString();
                _ctxMethod = ".cctor(" + fname + ")";
                var fb = ti.Fields[fname];
                GuardBody(() => { PrescanCfgLabels(f.GetProperty("init")); EmitStoreCoerced(f.GetProperty("init"), fb.FieldType); MaybeVolatile(fb); _il.Emit(OpCodes.Stsfld, fb); });
            }
            _il.Emit(OpCodes.Ret);
        }

        // Pass 5: synthesize entry point on the file class that has `main`.
        MethodBuilder entry = null;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.FileElem.Value.GetProperty("hasMain").GetBoolean() && ti.Methods.ContainsKey("main"))
            {
                entry = ti.TB.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(string[]) });
                var il = entry.GetILGenerator();
                var mainMb = ti.Methods["main"];
                // `fun main(args: Array<String>)` -> forward the CLR args; `fun main()` -> call with none.
                if (_mparams.TryGetValue(mainMb, out var mp) && mp.Length > 0) il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, mainMb);
                il.Emit(OpCodes.Ret);
            }

        // Pass 6: bake types (base before derived). Enums were already baked up front.
        foreach (var ti in Ordered()) { if (!ti.IsEnum) { T($"pass6 createType: {ti.TB?.Name}"); ti.TB.CreateType(); } }
        // The reverse-bridge adapter references the (now-baked) Kotlin Iterator type, so bake it after the user types.
        if (_enumAdapterTB != null && !_enumAdapterTB.IsCreated()) _enumAdapterTB.CreateType();
        foreach (var tb in _syntheticDelegates.Values)
            if (!tb.IsCreated())
                tb.CreateType();
        // The Unit-return delegate adapters forward to a void delegate type `ft` (a BCL Action or a synthetic
        // KAction), so bake them AFTER the synthetic delegates whose signatures they may reference.
        if (_unitAdapterTB != null && !_unitAdapterTB.IsCreated()) _unitAdapterTB.CreateType();
        // BCL Func/Action Invoke trampolines used when a TypeSpec contains a composite open type (Func<E[]>).
        if (_delegateInvokeAdapterTB != null && !_delegateInvokeAdapterTB.IsCreated()) _delegateInvokeAdapterTB.CreateType();
        // Safety net: any user type Ordered() somehow missed (so Save won't throw "not supported before the type is
        // created"). Repeat until stable, since creating one may be a prerequisite for another.
        for (bool again = true; again;)
        {
            again = false;
            foreach (var ti in _types.Values)
                if (!ti.IsEnum && ti.TB != null && !ti.TB.IsCreated())
                {
                    T($"pass6 createType (leftover): {ti.TB.Name}");
                    ti.TB.CreateType(); again = true;
                }
        }

        T("save: writing PE");
        Save(ab, entry);
        T("save: done");
    }

    // #84 Phase 1: run one body-emit, re-tagging any failure with the declaration being emitted (CurrentDecl). The
    // breadcrumb (_ctxType/_ctxMethod/_ctxNode) is set at the emit's head, so by the time a resolution throw
    // propagates here it names the right method. An already-tagged CirEmitException passes through unchanged.
    void GuardBody(Action emit)
    {
        try { emit(); }
        catch (CirEmitException) { throw; }
        catch (Exception ex) { throw new CirEmitException(CurrentDecl, ex.Message, ex); }
    }

    // (#75) The READONLY sibling interface(s) a mutable-collection interface must also expose so a value implementing
    // only the mutable face can be castclass'd into a readonly slot: IList<T>->IReadOnlyList<T>,
    // ICollection<T>/ISet<T>->IReadOnlyCollection<T>. (IReadOnlyList<T> derives from IReadOnlyCollection<T>, so listing
    // the former is enough for a list.) All members are satisfied implicitly by the mutable face's methods.
    static IEnumerable<Type> ReadonlyCollectionSiblings(Type itype)
    {
        if (itype == null || !itype.IsGenericType || itype.IsGenericTypeDefinition) yield break;
        var args = itype.GetGenericArguments();
        if (args.Length != 1) yield break;
        var t = args[0];
        var gd = itype.GetGenericTypeDefinition();
        if (gd == typeof(System.Collections.Generic.IList<>))
            yield return typeof(System.Collections.Generic.IReadOnlyList<>).MakeGenericType(t);
        else if (gd == typeof(System.Collections.Generic.ICollection<>) || gd == typeof(System.Collections.Generic.ISet<>))
            yield return typeof(System.Collections.Generic.IReadOnlyCollection<>).MakeGenericType(t);
    }

    IEnumerable<TypeInfo> Ordered()
    {
        // Dedup by type IDENTITY, not simple name: two distinct types can share a simple name (a top-level `State`
        // and a nested `X.State`, or same-named types in different files). Keying by name dropped the second from the
        // create order -> it was never CreateType()'d -> Save threw "not supported before the type is created".
        var done = new HashSet<TypeInfo>();
        var result = new List<TypeInfo>();
        void Visit(TypeInfo ti)
        {
            if (!done.Add(ti)) return;
            if (ti.BaseName != null && _types.TryGetValue(ti.BaseName, out var b)) Visit(b);
            // A generic interface used as a constructed parent/interface must be created before its implementers
            // (PersistedAssemblyBuilder materializes the instantiation at the implementer's CreateType).
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    if (ReadFqn(i) is DotKt.Bir.TypeNode.Fqn iF && _types.TryGetValue(iF.Name, out var inf)) Visit(inf);
                }
            // A nested type must be CreateType()'d BEFORE its enclosing type (Reflection.Emit bakes children into the
            // parent). `done` already holds `ti` (added at entry), so a child whose base IS `ti` won't recurse forever.
            var myName = ti.IsFileClass ? null : (ti.Def.TryGetProperty("name", out var nm) ? nm.GetString() : null);
            if (myName != null)
                foreach (var child in _types.Values)
                    if (!child.IsFileClass && !child.IsEnum && child.Def.TryGetProperty("nestedIn", out var cni) && cni.GetString() == myName)
                        Visit(child);
            result.Add(ti);
        }
        foreach (var ti in _types.Values) Visit(ti);
        return result;
    }

    // Kotlin visibility -> CLR method/ctor access flag (default public).
    static MethodAttributes AccessOf(JsonElement m) =>
        (m.TryGetProperty("vis", out var v) ? v.GetString() : "public") switch
        {
            "private" => MethodAttributes.Private,
            "internal" => MethodAttributes.Assembly,
            "protected" => MethodAttributes.Family,
            "protectedInternal" => MethodAttributes.FamORAssem,
            _ => MethodAttributes.Public,
        };

    // Method-level generic params, keyed by MethodInfo, so call sites can MakeGenericMethod.
    readonly Dictionary<MethodBuilder, Dictionary<string, GenericTypeParameterBuilder>> _methodTypeParams = new();

    // Body-phase occurrence counter for duplicate (name, params) defs — mirrors DeclareMethod's $dupN mangling.
    readonly Dictionary<(TypeInfo, string), int> _bodyDupSeen = new();

    void DeclareMethod(TypeInfo ti, JsonElement m, bool isStatic)
    {
        var name = m.GetProperty("name").GetString();
        // DUPLICATE (name, params) defs — Kotlin overloads distinguished ONLY by receiver types that COLLAPSE under a
        // @ClrTypeAlias (Map.iterator() vs MutableMap.iterator(): both receivers lower to IDictionary<K,V>) — would
        // otherwise share one MethodsBySig slot, concatenating BOTH bodies into a single MethodBuilder (malformed IL,
        // BadImageFormatException). Mangle the SECOND-and-later defs' emitted names (deterministic, def order — the
        // FIRST def keeps the clean name, so by-(name,params) reflection callers bind it unambiguously). EmitMethodBody
        // consumes the same #dupN keys in the same def order.
        var dupKey = SigKey(name, m);
        if (ti.MethodsBySig.ContainsKey(dupKey))
        {
            var n = 2;
            while (ti.MethodsBySig.ContainsKey(SigKey(name + "$dup" + n, m))) n++;
            name = name + "$dup" + n;
        }
        // Interface members are always public; otherwise map Kotlin visibility to a CLR access flag.
        var attrs = ti.IsInterface ? MethodAttributes.Public : AccessOf(m);
        // A method's own `static` flag (companion methods are static members of a user class).
        isStatic = isStatic || m.GetProperty("static").GetBoolean();
        var objOverride = m.TryGetProperty("objectOverride", out var oo) && oo.GetBoolean();
        // Overriding a .NET base virtual (e.g. `override val Message`) reuses the base slot, like an object-method.
        var clrOverride = m.TryGetProperty("clrOverride", out var co) ? SlotName(co) : null;
        // An interface method with a DEFAULT body -> a CLR default interface method (Virtual|NewSlot, real IL body in
        // Pass 4); a bare slot (no body) stays Virtual|Abstract|NewSlot. (A Kotlin interface default impl, e.g.
        // CoroutineContext.plus, must carry its body so non-overriding implementers inherit it instead of failing load.)
        // A flattened companion method on an interface (#83) is STATIC — it takes no slot, so it must NOT be marked
        // Virtual/NewSlot/Abstract (a static abstract interface method would demand an implementer). Only genuine
        // instance interface members become virtual slots / abstract DIMs.
        if (ti.IsInterface && !isStatic)
        {
            attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (!(m.TryGetProperty("body", out var ifb) && ifb.ValueKind == JsonValueKind.Array && ifb.GetArrayLength() > 0))
                attrs |= MethodAttributes.Abstract;
        }
        else if (ti.IsInterface && isStatic) attrs |= MethodAttributes.Static;
        else if (isStatic) attrs |= MethodAttributes.Static;
        // `ToString`/`Equals`/`GetHashCode` and .NET base overrides reuse the base slot (Virtual, no NewSlot).
        else if (objOverride || clrOverride != null) attrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
        else if (m.GetProperty("override").GetBoolean()) attrs |= MethodAttributes.Virtual;
        else if (m.GetProperty("virtual").GetBoolean()) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        // An `abstract fun` (no body) -> a CLR abstract method: Virtual|Abstract, no IL body (subclasses override).
        if (m.TryGetProperty("abstract", out var amb) && amb.GetBoolean()) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual;
        // A synthesized event accessor (add_/remove_/raise_<E>, §4.2) is `specialname` (the ECMA-335 event-accessor
        // convention) so the emitted `.event` is a clean reflectable member. bir2cir stamps the flag on the rewritten accessor.
        if (m.TryGetProperty("specialName", out var spn) && spn.GetBoolean()) attrs |= MethodAttributes.SpecialName;

        // NOTE: ilemit no longer rewrites a `suspend fun`'s signature to `Task<T>`. The cold-core coroutine lowering
        // (bir2cir, bundle-6) already arrives here as ordinary CIR: the public `Task<T>` bridge is its OWN method
        // carrying `suspendBridge:true` (from which bir2cir RoundtripMetadata generates the `[KotlinFunction(Suspend)]`
        // attr, #71 S2), and the cold entry / state-machine class are plain methods/types. A leftover `"suspend":true`
        // method falls through to the normal signature path;
        // at body time it emits a throwing stub in a STDLIB build (expected — the coroutine primitives have no SM form)
        // but is an emit-time ERROR in an app build (a bir2cir transform miss — see EmitMethodBody's suspend guard).

        MethodBuilder mb;
        Type[] ps;
        var genTps = m.TryGetProperty("typeParams", out var mtp) && mtp.GetArrayLength() > 0 ? (JsonElement?)mtp : null;
        if (genTps != null)
        {
            // Generic method `fun <T> id(x: T): T`: the signature references the method's own type params, so
            // they must be defined before SetParameters/SetReturnType (staged form, not the one-shot DefineMethod).
            var genNames = TpNames(genTps.Value);
            mb = ti.TB.DefineMethod(name, attrs);
            var gps = mb.DefineGenericParameters(genNames);
            var map = new Dictionary<string, GenericTypeParameterBuilder>();
            for (int gi = 0; gi < genNames.Length; gi++) map[genNames[gi]] = gps[gi];
            _methodTypeParams[mb] = map;
            _curMethodParams = map;
            ApplyConstraints(genTps.Value, map, false);   // `<T : Comparable<T>>` on the method (variance N/A on methods)
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            mb.SetParameters(ps);
            mb.SetReturnType(MapType(m.GetProperty("ret")));
            _curMethodParams = null;
        }
        else
        {
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            mb = ti.TB.DefineMethod(name, attrs, MapType(m.GetProperty("ret")), ps);
        }
        // A kotc-authored lifted method (`newDelegate` target) carries the same structural generated fact as
        // synthesized types. Stamp the standard marker here; dll2klib uses it to keep implementation-only helpers
        // out of the re-imported Kotlin surface. This is a direct CIR flag -> metadata mapping, not name inference.
        if (m.TryGetProperty("generated", out var generated) && generated.GetBoolean())
            StampCompilerGenerated(mb);
        ti.Methods[name] = mb; ti.MethodsBySig[SigKey(name, m)] = mb;
        // #139: record the bir2cir reverse-enumerator-bridge role marker (never a Kotlin name). A "hasNext"/"next" role
        // identifies THE Kotlin iterator interface the adapter wraps; an "iterator" role is the this.iterator() a
        // synthesized GetEnumerator calls. Read by Emitter.ReverseBridge.cs; no effect on emitted metadata.
        if (m.TryGetProperty("clrBridgeRole", out var brJson) && brJson.GetString() is { } bridgeRole)
        {
            ti.BridgeRoles[bridgeRole] = mb;
            if (bridgeRole is "hasNext" or "next") _iterBridgeIface = ti;
        }
        _mparams[mb] = ps;   // MethodBuilder.GetParameters() throws pre-bake; record param types for call-site boxing
        DefineParamNames(mb, m);
        if (objOverride)
        {
            var objM = name switch
            {
                "ToString" => typeof(object).GetMethod("ToString", Type.EmptyTypes),
                "GetHashCode" => typeof(object).GetMethod("GetHashCode", Type.EmptyTypes),
                "Equals" => typeof(object).GetMethod("Equals", new[] { typeof(object) }),
                _ => null,
            };
            if (objM != null) ti.TB.DefineMethodOverride(mb, objM);
        }
        if (clrOverride != null)
        {
            // Link the override to the EXACT .NET base virtual so virtual dispatch through the base type reaches it
            // (`callvirt System.Exception::get_Message` -> our override). bir2cir resolved the base slot off the ref.dll
            // and carried its param signature as `clrOverrideSig` (#46/#183 W1-S4) — LinkOverrideBase links the unique
            // slot (0 = hard ABI error), replacing the former name-only first-pick fallback.
            var baseT = ResolveType(clrOverride);
            ti.TB.DefineMethodOverride(mb, LinkOverrideBase(baseT, name, m, ti.TB));
        }
        // Kotlin's `@kotlin.internal.InlineOnly` says "this fn is meant to be inlined, not called as a method". The direct
        // CLR translation is a [MethodImpl(AggressiveInlining)] hint on the emitted method. kotc reads the annotation and
        // emits `mods.inlineOnly`; ilemit stamps the flag. Pure metadata, no behavior change; the JIT ignores the hint for
        // a too-large method. Skip abstract slots (no body to inline). ilemit adds no Kotlin knowledge — it stamps a flag.
        if (ModFlag(m, "inlineOnly") && (attrs & MethodAttributes.Abstract) == 0)
            mb.SetImplementationFlags(mb.GetMethodImplementationFlags() | MethodImplAttributes.AggressiveInlining);
    }

    // Define a type's constructors from its CIR (idempotent). Normally runs in pass 3, but BuildCab pulls it EARLY when
    // stamping a param/method attribute whose attribute type is emitted in THIS assembly (e.g. `@kotlin.clr.KotlinDefault
    // (index, bir)` on a defaulted stdlib parameter): pass 3 declares members type-by-type, so a `@KotlinDefault`
    // application on an EARLIER type's method would otherwise reach BuildCab before KotlinDefault's own `(int,string)`
    // ctor was defined — the old `ti.Ctors[0] ?? DefineDefaultConstructor()` then minted a bogus parameterless ctor per
    // application and every stamp failed "Parameter count does not match". Defining ctors on demand (guarded) makes the
    // real ctor available whenever it is first needed. Interfaces/enums/file classes have no CIR ctors.
    void EnsureCtorsDefined(TypeInfo ti)
    {
        if (ti.CtorsDefined) return;
        ti.CtorsDefined = true;
        if (ti.IsInterface || ti.IsEnum || ti.IsFileClass || !ti.Def.TryGetProperty("ctors", out var ctors)) return;
        var saved = _curTypeParams;
        _curTypeParams = EffectiveTps(ti);   // so a `gp:T` ctor param resolves when pulled early out of pass-3 order
        foreach (var c in ctors.EnumerateArray())
        {
            var ps = c.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            var cb = ti.TB.DefineConstructor(AccessOf(c), CallingConventions.Standard, ps);
            DefineParamNames(cb, c);   // ctor param NAMES + [Optional]/DefaultParameterValue (named-arg ctor calls)
            ti.Ctors.Add(cb);
            ti.CtorDefs.Add(c);
        }
        if (ti.Ctors.Count > 0) { ti.Ctor = ti.Ctors[0]; ti.CtorDef = ti.CtorDefs[0]; }
        _curTypeParams = saved;
    }

    // An ownerType spec is either `Name` (plain) or `Name[arg,...]` (a constructed user generic, e.g. `Box[int]`).
    // For a constructed generic, members are resolved on the OPEN definition (the Builder) and then wrapped onto
    // the constructed type via the static `TypeBuilder.GetX` helpers — the MakeGenericType result's own
    // GetMethod/GetField/GetConstructor throw NotSupportedException on the persisted builder (verified, .NET 10).
    // A typeParams entry is either a bare name string `"T"` (unconstrained) or `{"name":"T","constraints":[...]}`.
    static string TpName(JsonElement x) => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetProperty("name").GetString();

    static string[] TpNames(JsonElement tps) => tps.EnumerateArray().Select(TpName).ToArray();

    // Apply generic constraints (`<T : Comparable<T>>` -> `T : IComparable<T>`) to already-defined params. The
    // constraint context map (type or method params) must be current so a `gp:T` inside a bound resolves.
    // True if the type string mentions the type param `gp:<pname>` (token-exact, so `gp:E` doesn't match `gp:E2`).
    // Does a structured type SLOT mention the type-scope type param at position `pos` (variance-conflict check)?
    static bool MentionsTv(JsonElement e, int pos) =>
        DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode t && MentionsTv(t, pos);

    static bool MentionsTv(DotKt.Bir.TypeNode t, int pos) => t switch
    {
        DotKt.Bir.TypeNode.Tv tv => tv.Scope == "type" && tv.I == pos,
        DotKt.Bir.TypeNode.Fqn { Args: { } a } => a.Any(x => MentionsTv(x, pos)),
        DotKt.Bir.TypeNode.Nullable n => MentionsTv(n.Of, pos),
        DotKt.Bir.TypeNode.Array ar => MentionsTv(ar.Elem, pos),
        DotKt.Bir.TypeNode.ByRef b => MentionsTv(b.Of, pos),
        DotKt.Bir.TypeNode.Fn fn => MentionsTv(fn.Ret, pos) || fn.DelegateParams.Any(p => MentionsTv(p, pos)),
        _ => false,
    };

    // Whether an occurrence of an interface type parameter is illegal for its declared CLR variance. Kotlin permits
    // `out T` in a suspend return, but the CLR bridge is `Task<T>` and Task is invariant: the nested occurrence is
    // therefore both input and output from the CLR verifier's perspective. Walk the actual generic variance of each
    // containing type instead of treating every method return as automatically covariant.
    bool VarianceConflict(DotKt.Bir.TypeNode t, int pos, int context, int declared)
    {
        switch (t)
        {
            case DotKt.Bir.TypeNode.Tv tv:
                return tv.Scope == "type" && tv.I == pos && (context == 0 || context != declared);
            case DotKt.Bir.TypeNode.Fqn { Args: { } args } f:
                for (var i = 0; i < args.Length; i++)
                {
                    var variance = GenericArgVariance(f.Name, args.Length, i);
                    var nested = context == 0 || variance == 0 ? 0 : context * variance;
                    if (VarianceConflict(args[i], pos, nested, declared)) return true;
                }
                return false;
            case DotKt.Bir.TypeNode.Nullable n:
                return VarianceConflict(n.Of, pos, context, declared);
            case DotKt.Bir.TypeNode.Oblivious o:
                return VarianceConflict(o.Of, pos, context, declared);
            case DotKt.Bir.TypeNode.Array a:
                return VarianceConflict(a.Elem, pos, 0, declared); // writable CLR arrays are invariant for safety
            case DotKt.Bir.TypeNode.ByRef b:
                return VarianceConflict(b.Of, pos, 0, declared);
            case DotKt.Bir.TypeNode.Fn fn:
                if (VarianceConflict(fn.Ret, pos, context, declared)) return true;
                foreach (var p in fn.DelegateParams)
                    if (VarianceConflict(p, pos, -context, declared)) return true;
                return false;
            default:
                return false;
        }
    }

    // +1 covariant, -1 contravariant, 0 invariant/unknown. Local definitions are read from their Kotlin-neutral CIR
    // declaration; referenced CLR definitions are read from reflection. Unknown must be invariant — optimistic
    // variance here creates unloadable metadata, while invariant metadata is always sound.
    int GenericArgVariance(string name, int arity, int index)
    {
        if (_types.TryGetValue(BareTypeKey(name), out var ti)
            && ti.Def.TryGetProperty("typeParams", out var localTps)
            && index < localTps.GetArrayLength())
        {
            var tp = localTps[index];
            if (tp.ValueKind == JsonValueKind.Object && tp.TryGetProperty("variance", out var v))
                return v.GetString() switch { "out" => 1, "in" => -1, _ => 0 };
            return 0;
        }

        Type open = null;
        try
        {
            open = RuntimeReferences.ResolveType(name + "`" + arity)
                ?? RuntimeReferences.ResolveFromHostFramework(name + "`" + arity);
        }
        catch { }
        if (open == null || !open.IsGenericTypeDefinition) return 0;
        var gps = open.GetGenericArguments();
        if (index >= gps.Length) return 0;
        return (gps[index].GenericParameterAttributes & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => 1,
            GenericParameterAttributes.Contravariant => -1,
            _ => 0,
        };
    }

    void ApplyConstraints(JsonElement tps, Dictionary<string, GenericTypeParameterBuilder> map, bool isInterface, JsonElement? typeDef = null)
    {
        foreach (var x in tps.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var gp = map[x.GetProperty("name").GetString()];
            // Declaration-site variance is legal CLR metadata only on an interface type param, AND only when the param
            // is NOT used in a conflicting position: a covariant `out E` may not appear in an `in` (method-argument)
            // position, a contravariant `in E` not in an `out` (return) position. Kotlin permits the conflict via
            // @UnsafeVariance (e.g. `Collection<out E>.contains(element: E)`); the CLR has no such escape, so such a
            // param MUST be emitted invariant or the whole type fails to load. Keep clearly-valid variance, drop the rest.
            if (isInterface && x.TryGetProperty("variance", out var v))
            {
                var vs = v.GetString();
                var pname = x.GetProperty("name").GetString();
                bool conflict = false;
                if ((vs == "out" || vs == "in") && typeDef is { } td && td.TryGetProperty("methods", out var ms))
                    foreach (var m in ms.EnumerateArray())
                    {
                        var declared = vs == "out" ? 1 : -1;
                        if (m.TryGetProperty("params", out var ps))
                            foreach (var p in ps.EnumerateArray())
                                if (p.TryGetProperty("type", out var pt)
                                    && VarianceConflict(DotKt.Bir.TypeNode.Read(pt), gp.GenericParameterPosition, -1, declared))
                                { conflict = true; break; }
                        if (!conflict && m.TryGetProperty("ret", out var rt)
                            && VarianceConflict(DotKt.Bir.TypeNode.Read(rt), gp.GenericParameterPosition, 1, declared))
                            conflict = true;
                        if (conflict) break;
                    }
                var attr = conflict ? GenericParameterAttributes.None
                         : vs == "out" ? GenericParameterAttributes.Covariant
                         : vs == "in" ? GenericParameterAttributes.Contravariant
                         : GenericParameterAttributes.None;
                if (attr != GenericParameterAttributes.None) gp.SetGenericParameterAttributes(attr);
            }
            if (x.TryGetProperty("constraints", out var cs))
            {
                var types = cs.EnumerateArray().Select(c => MapType(c)).ToList();
                var ifaces = types.Where(t => t.IsInterface).ToArray();
                var baseT = types.FirstOrDefault(t => !t.IsInterface);
                if (baseT != null) gp.SetBaseTypeConstraint(baseT);
                if (ifaces.Length > 0) gp.SetInterfaceConstraints(ifaces);
            }
        }
    }

    // The OPEN type name of an owner spec, WITHOUT resolving its generic args. The type-ordering pass runs with no
    // type-param scope, so a `Foo[gp:E]` base/interface would crash MapType("gp:E"); ordering only needs the open dep.
    static string OwnerOpen(string spec) { var br = spec.IndexOf('['); return br < 0 ? spec : spec.Substring(0, br); }

    int _covarBridge = 0;

    // The referenced-interface (@ClrTypeAlias'd / clr:) twin of EmitCovariantBridge, working from a REFLECTED interface
    // method rather than a BIR imDef: the BCL slot is VOID but the Kotlin body returns a value it drops (add():Boolean ->
    // ICollection.Add():void, set/removeAt:E -> IList.set_Item/RemoveAt():void). Emit a private/final void bridge with the
    // slot's signature that callvirts the body, pops the dropped return, and carry the MethodImpl on the bridge. (Same IL
    // shape as the dimimpl bridge; `paramTypes` are the slot's substituted params, which the body's own params match.)
    void EmitVoidDropBridge(TypeInfo ti, string name, Type[] paramTypes, MethodBuilder body, MethodInfo ifaceMethod)
    {
        var bridge = ti.TB.DefineMethod("dotkt$covar$" + name + "$" + (_covarBridge++),
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            typeof(void), paramTypes);
        StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
        var il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
        var bodyCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), body) : (MethodInfo)body;
        il.Emit(OpCodes.Callvirt, bodyCall);
        il.Emit(OpCodes.Pop);   // the BCL slot drops the Kotlin return
        il.Emit(OpCodes.Ret);
        ti.TB.DefineMethodOverride(bridge, ifaceMethod);
    }

    // Emit a covariant-return bridge: a private explicit-interface-impl method with the iface's (base) return type +
    // params, calling the narrow body method on `this` and returning it (a ref upcast); the MethodImpl goes on the bridge.
    void EmitCovariantBridge(TypeInfo ti, string name, JsonElement imDef, DotKt.Bir.TypeNode[] specArgs, MethodBuilder body, MethodInfo ifaceMethod, Type ifaceRet)
    {
        var paramTypes = imDef.GetProperty("params").EnumerateArray()
            .Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
        var bridge = ti.TB.DefineMethod("dotkt$covar$" + name + "$" + (_covarBridge++),
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            ifaceRet, paramTypes);
        StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
        var il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
        var bodyCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), body) : (MethodInfo)body;
        il.Emit(OpCodes.Callvirt, bodyCall);
        // ifaceRet==void but the body returns a value (add():Boolean -> ICollection.Add():void): the BCL slot drops the
        // Kotlin return -> pop it so the void bridge leaves an empty stack. Else the (reference) narrow return upcasts.
        if (ifaceRet == typeof(void) && body.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        ti.TB.DefineMethodOverride(bridge, ifaceMethod);
    }

    // A class implements an interface method (Comparable.compareTo) for which it has no own body, but one of its
    // interfaces provides it as a DEFAULT method (ComparableTimeMark.compareTo DIM). The provider may be TRANSITIVE and
    // may live in a referenced assembly: `JobSupport : Job`, for example, must use stdlib's
    // `CoroutineContext.Element.get` to fill Job's compiler-materialized abstract `get` slot. The CLR doesn't treat that
    // inherited DIM as implementing the distinct redeclared slot, so emit a class-level forwarding bridge that calls the
    // inherited DIM and put the MethodImpl for the interface method on the bridge.
    void TryEmitDimForwardBridge(TypeInfo ti, JsonElement imDef, DotKt.Bir.TypeNode[] specArgs, string subSig, Type constructed, MethodBuilder ifaceBuilder)
    {
        if (ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var dirIfs)) return;
        // A GENERIC default-interface-method (`get<E : Element>(key: Key<E>)`) must be forwarded by a GENERIC bridge of
        // the SAME arity+constraints — an erased-to-object bridge is a NON-generic body over a generic declaration, which
        // both fails the CLR's methodimpl signature match (a generic-arity mismatch, TypeLoadException) AND collapses
        // `Key<E>` to `Key<object>`. The DIM's method type params come straight off the interface method DEF.
        var genTps = imDef.TryGetProperty("typeParams", out var mtp) && mtp.GetArrayLength() > 0 ? (JsonElement?)mtp : null;
        // Search most-specific interfaces first, then their bases. Looking only at `dirIfs` loses a DIM as soon as an
        // intermediate Kotlin interface carries a fake/abstract override of the inherited member. Keep every hop as a
        // structured spec so owner type arguments are substituted into the implementing class's frame.
        var work = new Queue<DotKt.Bir.TypeNode.Fqn>();
        var seen = new HashSet<string>();
        foreach (var di in dirIfs.EnumerateArray())
            if (ReadFqn(di) is DotKt.Bir.TypeNode.Fqn diF) work.Enqueue(diF);

        MethodInfo dimTarget = null;
        MethodBuilder dimBuilder = null;
        while (work.Count > 0 && dimTarget == null)
        {
            var diF = work.Dequeue();
            if (!seen.Add(SigCanon(diF))) continue;
            var (dopen, dconstructed) = ParseOwnerT(diF);
            if (_types.TryGetValue(dopen, out var diTi))
            {
                var diArgs = diF.Args;
                if (diTi.Def.ValueKind == JsonValueKind.Object && diTi.Def.TryGetProperty("interfaces", out var bases))
                    foreach (var bi in bases.EnumerateArray())
                        if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bi0
                            && SubstTv(bi0, diArgs) is DotKt.Bir.TypeNode.Fqn biF)
                            work.Enqueue(biF);

                if (!diTi.Def.TryGetProperty("methods", out var methods)) continue;
                foreach (var candDef in methods.EnumerateArray())
                {
                    if (!candDef.TryGetProperty("name", out var cn) || cn.GetString() != imDef.GetProperty("name").GetString()
                        || !candDef.TryGetProperty("params", out var cps)) continue;
                    var candSig = cn.GetString() + "(" + string.Join(",", cps.EnumerateArray()
                        .Select(p => SigCanon(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), diArgs)))) + ")";
                    if (candSig != subSig) continue;
                    var raw = diTi.MethodsBySig.TryGetValue(SigKey(cn.GetString(), candDef), out var bySig) ? bySig
                            : (diTi.Methods.TryGetValue(cn.GetString(), out var byName) ? byName : null);
                    if (raw == null || raw.Attributes.HasFlag(MethodAttributes.Abstract)) continue;
                    dimBuilder = raw;
                    dimTarget = dconstructed != null ? TypeBuilder.GetMethod(dconstructed, raw) : raw;
                    break;
                }
            }
            else
            {
                // Referenced Kotlin interfaces are ordinary CLR interfaces here. Resolve the overload structurally from
                // the SLOT signature (including generic method shape), and accept only a genuinely bodied DIM.
                Type ext = null;
                try { ext = MapType(diF); } catch { }
                if (ext == null) continue;
                var slotSig = imDef.GetProperty("params").EnumerateArray()
                    .Select(p => SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs)).ToArray();
                MethodInfo reflected = null;
                try { reflected = FindReflectedMethodBySig(ext, imDef.GetProperty("name").GetString(), slotSig); }
                catch (NotSupportedException)
                {
                    // A referenced generic interface instantiated with an emitted TypeBuilder argument cannot reflect
                    // members directly. Resolve on its OPEN definition and re-anchor the handle, exactly as the main
                    // interface-binding pass does.
                    try
                    {
                        var openExt = ext.GetGenericTypeDefinition();
                        var openMethod = FindReflectedMethodBySig(openExt, imDef.GetProperty("name").GetString(), slotSig);
                        if (openMethod != null) reflected = TypeBuilder.GetMethod(ext, openMethod);
                    }
                    catch (NotSupportedException) { }
                }
                if (reflected != null && !reflected.IsAbstract) dimTarget = reflected;
                // GetInterfaces is flattened, which is fine for this fallback: breadth ordering among referenced bases
                // is immaterial once an intermediate declaration is abstract, and the first bodied exact slot wins.
                if (dimTarget == null)
                    try
                    {
                        foreach (var rb in ext.GetInterfaces())
                        {
                            var rm = FindReflectedMethodBySig(rb, imDef.GetProperty("name").GetString(), slotSig);
                            if (rm != null && !rm.IsAbstract) { dimTarget = rm; break; }
                        }
                    }
                    catch (NotSupportedException) { } // constructed TypeBuilder bases are already represented in CIR
            }
        }
        if (dimTarget == null) return;

            // If the found DEFAULT is the very slot we are trying to fill (a direct interface that both DECLARES and
            // DEFAULTS this method — e.g. `Element` for its own `get`), the DIM already implements its own slot: a
            // self-forwarding override would only re-dispatch through `this` straight back into the bridge (infinite
            // recursion). The base-interface slot that genuinely needs filling (`CoroutineContext.get`) is a DIFFERENT
            // `ifaceBuilder`, so its bridge (which callvirts THIS `dim`) resolves to the inherited DIM, not to itself.
            if (ReferenceEquals(dimBuilder, ifaceBuilder)) return;
            MethodBuilder bridge; MethodInfo dimCall;
            Type ifaceRet; Type[] paramTypes;
            if (genTps != null)
            {
                // Generic arm: the bridge's params reference its OWN method type vars, so the builder + its generic
                // params must exist BEFORE MapType runs (they anchor a method-scope tv). Mirror the DIM's constraints
                // (concrete on the coroutine `get<E : Element>`), then instantiate the DIM target with the bridge's own
                // type params for the callvirt. On a resolve failure give the already-defined bridge a throwing body and
                // skip the methodimpl — a bodyless orphan would fail the whole-assembly bake.
                bridge = ti.TB.DefineMethod("dotkt$dimfwd$" + (_covarBridge++),
                    MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig);
                var genNames = TpNames(genTps.Value);
                var gps = bridge.DefineGenericParameters(genNames);
                var map = new Dictionary<string, GenericTypeParameterBuilder>();
                for (int gi = 0; gi < genNames.Length; gi++) map[genNames[gi]] = gps[gi];
                _methodTypeParams[bridge] = map;
                var savedMp = _curMethodParams; _curMethodParams = map;
                ApplyConstraints(genTps.Value, map, false);
                try
                {
                    ifaceRet = imDef.TryGetProperty("ret", out var rt) ? MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)) : typeof(void);
                    paramTypes = imDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
                }
                catch (Exception ex)
                {
                    _curMethodParams = savedMp;
                    throw new InvalidOperationException(
                        $"cannot materialize DIM forwarder {ti.TB.FullName}.{imDef.GetProperty("name").GetString()}: {ex.Message}", ex);
                }
                bridge.SetReturnType(ifaceRet);
                bridge.SetParameters(paramTypes);
                _curMethodParams = savedMp;
                dimCall = dimTarget.MakeGenericMethod(gps.Cast<Type>().ToArray());
            }
            else
            {
                // Non-generic arm: resolve the signature BEFORE defining the bridge, so a MapType failure is a clean
                // skip (no orphan bodyless method to crash the bake).
                try
                {
                    ifaceRet = imDef.TryGetProperty("ret", out var rt) ? MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)) : typeof(void);
                    paramTypes = imDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
                }
                catch { return; }
                bridge = ti.TB.DefineMethod("dotkt$dimfwd$" + (_covarBridge++),
                    MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                    ifaceRet, paramTypes);
                dimCall = dimTarget;
            }
            StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
            var il = bridge.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
            il.Emit(OpCodes.Callvirt, dimCall);   // dispatches to the DIM inherited by `this`
            il.Emit(OpCodes.Ret);
            var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
            ti.TB.DefineMethodOverride(bridge, ifaceMethod);
    }


    // A structured owner Fqn -> (open name, constructed .NET type or null for a non-generic). An emitted open type
    // (`_types`) is MakeGenericType'd; a referenced generic is arity-suffixed by reflection.
    (string open, Type constructed) ParseOwnerT(DotKt.Bir.TypeNode.Fqn f)
    {
        if (f.Args == null) return (f.Name, null);
        var args = f.Args.Select(a => { var r = MapType(a); return r == typeof(void) ? typeof(object) : r; }).ToArray();
        if (_types.TryGetValue(f.Name, out var ti)) return (f.Name, ti.TB.MakeGenericType(args));
        return (f.Name, ResolveType(f.Name + "`" + args.Length).MakeGenericType(args));
    }

    void Save(PersistedAssemblyBuilder ab, MethodBuilder entry)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            peHeader, new MetadataRootBuilder(metadata), ilStream,
            mappedFieldData: fieldData,
            entryPoint: entry != null ? MetadataTokens.MethodDefinitionHandle(entry.MetadataToken) : default);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        // #52 — write ATOMICALLY (temp + rename): FileMode.Create truncates-then-writes in place, so a concurrent
        // reader (retarget/dll2klib/bir2cir loading this same dll) can observe a partial image and fail with a
        // spurious "Format of the executable is invalid" / BadImageFormatException. A same-directory rename is atomic,
        // so a reader always sees either the whole old file or the whole new one — never a torn write.
        var dllPath = Path.Combine(_outDir, _asmName + ".dll");
        AtomicFile.Write(dllPath, fs => blob.WriteContentTo(fs));
        var v = Environment.Version;
        AtomicFile.WriteAllText(Path.Combine(_outDir, _asmName + ".runtimeconfig.json"),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" + v.Major + "." + v.Minor + ".0\" }\n  }\n}\n");
        Console.WriteLine($"emitted {_asmName}.dll");
    }

}
