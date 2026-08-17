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
        LoadWellKnown(files);
        // #370: how many references these documents carry, so the parity check below can be held to all of them.
        // #336: PersistedAssemblyBuilder and every external type/member below share one target
        // MetadataLoadContext. The compiler host still supplies Reflection.Emit's implementation, never an emitted
        // identity. Mixing a host Type with this graph is invalid even when its FullName matches a target Type.
        var ab = new PersistedAssemblyBuilder(new AssemblyName(_asmName), _target.CoreAssembly);
        // Assembly provenance: the emitter owns the final assembly in BOTH SDK and direct-CLI flows, so it stamps an
        // explicit, versioned DotKt protocol marker here (not in MSBuild-only SDK plumbing). dll2klib requires this
        // signal together with compiler-generated embedded metadata carriers before applying Kotlin-only reverse maps.
        const string dotKtMarkerKey = "DotKt.Compiler";
        const string dotKtMarkerValue = "metadata-v1";
        // #370-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
        var assemblyMetadataCtor = Bcl("System.Reflection.AssemblyMetadataAttribute").GetConstructor(new[] { Bcl("System.String"), Bcl("System.String") });
        SetAttribute(ab.SetCustomAttribute, assemblyMetadataCtor,
            new[] { Bcl("System.String"), Bcl("System.String") }, dotKtMarkerKey, dotKtMarkerValue);
        // The project/direct driver owns the target framework fact. Stamp it only when explicitly supplied; deriving
        // it from ilemit's net10 host would make cross-target output lie about its contract.
        if (_targetFrameworkMoniker != null)
        {
            var targetFrameworkCtor = Bcl("System.Runtime.Versioning.TargetFrameworkAttribute")
                // #370-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
                .GetConstructor(new[] { Bcl("System.String") });
            SetAttribute(ab.SetCustomAttribute, targetFrameworkCtor,
                new[] { Bcl("System.String") }, _targetFrameworkMoniker);
        }
        // The frontend stdlib KLIB is the authoritative Kotlin declaration surface. Mark both CLR stdlib twins so
        // generic CLR-reference projectors can route them away from dll2klib without guessing from assembly names.
        if (_stdlibAssembly)
            SetAttribute(ab.SetCustomAttribute, assemblyMetadataCtor,
                new[] { Bcl("System.String"), Bcl("System.String") }, "DotKt.LibraryKind", "stdlib");
        _mod = ab.DefineDynamicModule(_asmName);
        // #71 S2: DotKt.Runtime.CompilerServices.* carrier classes are ordinary CIR type declarations. Standard
        // nullable metadata attributes resolve from the target BCL and are never redefined in user assemblies.

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
        }

        // CIR nesting is declarative: a child may precede its enclosing declaration (for example when kotc emits a
        // rich-enum companion before the rich enum class). Define enclosing builders first so `nestedIn` is honored
        // independently of source/file order instead of silently degrading the child to a dotted top-level type.
        var pendingTypes = files
            .Where(file => file.TryGetProperty("types", out _))
            .SelectMany(file => file.GetProperty("types").EnumerateArray())
            .ToList();
        while (pendingTypes.Count > 0)
        {
            var progressed = false;
            foreach (var t in pendingTypes.ToArray())
            {
                if (t.TryGetProperty("nestedIn", out var pendingParent) &&
                    !_types.ContainsKey(pendingParent.GetString()) &&
                    // The runtime stdlib substitutes several Kotlin owners with BCL types, so their declarations are
                    // intentionally absent from this emission unit. Preserve that pre-#275 stdlib-only dotted TypeDef
                    // shape explicitly; application/reference-library emission must always realize declared nesting.
                    !_stdlibRuntime)
                    continue;
                pendingTypes.Remove(t);
                progressed = true;
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
                    if (ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(name) && ResolvesExternally(name)) continue;
                    if (kind == "delegate")
                    {
                        // A CIR delegate declaration already carries its exact metadata name (including `arity),
                        // generic parameters/variance, Invoke parameters and return. Realize that CLR shape directly;
                        // no family/range/name decision is made in this layer.
                        var delegateTb = _mod.DefineType(name,
                            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                            Bcl("System.MulticastDelegate"));
                        if (generated) StampCompilerGenerated(delegateTb);
                        var dti = new TypeInfo { TB = delegateTb, Def = t, IsDelegate = true };
                        if (t.TryGetProperty("typeParams", out var dtps) && dtps.GetArrayLength() > 0)
                        {
                            var names = TpNames(dtps);
                            var gps = delegateTb.DefineGenericParameters(names);
                            for (var gi = 0; gi < names.Length; gi++) dti.TypeParams[names[gi]] = gps[gi];
                        }
                        _types[name] = dti;
                        continue;
                    }
                    if (kind == "enum")
                    {
                        // A real .NET enum: each entry is a literal field of the int-backed enum.
                        // DefineEnum asks host Reflection helpers to classify its underlying Type. A target-MLC
                        // System.Int32 is metadata-only, so that helper misclassifies it and PAB synthesizes an illegal
                        // enum .ctor. Spell the ECMA enum shape directly from target types: sealed : System.Enum, one
                        // special instance value__ field, and static literal fields. No semantic decision is involved.
                        var enumVisibility = t.TryGetProperty("vis", out var enumVis)
                            ? enumVis.GetString()
                            : "public";
                        TypeInfo enumParent = null;
                        var nestedEnum = t.TryGetProperty("nestedIn", out var enumParentName) &&
                            _types.TryGetValue(enumParentName.GetString(), out enumParent);
                        var enumAccess = nestedEnum
                            ? enumVisibility switch
                            {
                                "internal" => TypeAttributes.NestedAssembly,
                                "protected" => TypeAttributes.NestedFamily,
                                "protectedInternal" => TypeAttributes.NestedFamORAssem,
                                "private" => TypeAttributes.NestedPrivate,
                                _ => TypeAttributes.NestedPublic,
                            }
                            : (enumVisibility == "public" ? TypeAttributes.Public : TypeAttributes.NotPublic);
                        var enumSimpleName = nestedEnum && name.Contains('.')
                            ? name[(name.LastIndexOf('.') + 1)..]
                            : name;
                        var enumTb = nestedEnum
                            ? enumParent.TB.DefineNestedType(enumSimpleName,
                                enumAccess | TypeAttributes.Sealed, Bcl("System.Enum"))
                            : _mod.DefineType(name, enumAccess | TypeAttributes.Sealed, Bcl("System.Enum"));
                        var eti = new TypeInfo { TB = enumTb, Def = t, IsEnum = true };
                        eti.Fields["value__"] = enumTb.DefineField("value__", Bcl("System.Int32"),
                            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName);
                        foreach (var en in t.GetProperty("entries").EnumerateArray())
                        {
                            var entryName = en.GetProperty("name").GetString();
                            var field = enumTb.DefineField(entryName, enumTb,
                                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
                            field.SetConstant(en.GetProperty("ordinal").GetInt32());
                            eti.Fields[entryName] = field;
                        }
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
                    if (t.TryGetProperty("specialName", out var typeSpecialName) && typeSpecialName.GetBoolean())
                        attrs |= TypeAttributes.SpecialName;
                    if (t.TryGetProperty("beforeFieldInit", out var beforeFieldInit) && beforeFieldInit.GetBoolean())
                        attrs |= TypeAttributes.BeforeFieldInit;
                    // Only SOURCE-declared parameters contribute to the metadata-name suffix. A nested companion can
                    // carry the enclosing type's flattened CLR generic slots in `capturedTypeParams` without declaring
                    // Kotlin parameters of its own: `Foo`1+$Companion`, not `...$Companion`1`.
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
                    // A physical nested capture is deliberately distinct from this type's own declared parameters.
                    // A declaration-form capturedTypeParams entry preserves the outer Kotlin constraints; a bare
                    // name intentionally declares an unconstrained representation slot (for example a companion
                    // carrier). ApplyConstraints below emits exactly the CIR-authored distinction.
                    var capturedNames = t.TryGetProperty("capturedTypeParams", out var capturedTps)
                        ? TpNames(capturedTps) : [];
                    var declaredNames = t.TryGetProperty("typeParams", out var tps)
                        ? TpNames(tps) : [];
                    var allTypeParamNames = capturedNames.Concat(declaredNames).ToArray();
                    if (allTypeParamNames.Length > 0)
                    {
                        var gps = tb.DefineGenericParameters(allTypeParamNames);
                        for (int gi = 0; gi < allTypeParamNames.Length; gi++)
                            nti.TypeParams[allTypeParamNames[gi]] = gps[gi];
                    }
                    _types[name] = nti;
            }
            if (!progressed)
            {
                var unresolved = string.Join(", ", pendingTypes.Select(t =>
                    $"{t.GetProperty("name").GetString()} -> {t.GetProperty("nestedIn").GetString()}"));
                throw new InvalidOperationException($"nested CIR types have missing or cyclic owners: {unresolved}");
            }
        }

        // Bake enums up front: their literals are fully defined in pass 1, and baking now gives a real metadata
        // token usable in other types' IL (box/castclass/ldtoken) — an un-baked EnumBuilder token breaks the PE.
        foreach (var ti in _types.Values)
            if (ti.IsEnum) ti.Created = ti.TB.CreateType();

        // Pass 2: set parents and interface implementations (DefineGenericParameters already ran in pass 1, so a
        // generic base/interface that references the type's own params resolves).
        foreach (var ti in _types.Values)
        {
            T($"pass2 parent/iface: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);
            // Bounds may reference any type (now all defined) and the type's own params (now in _curTypeParams).
            if (ti.IsGeneric && ti.Def.TryGetProperty("capturedTypeParams", out var capturedTps2))
                ApplyConstraints(capturedTps2, ti.TypeParams, false);
            if (ti.IsGeneric && ti.Def.TryGetProperty("typeParams", out var tps2))
                ApplyConstraints(tps2, ti.TypeParams, ti.IsInterface || ti.IsDelegate, ti.Def);
            if (ti.BaseName != null)
            {
                // A `.NET` base (`clr:System.Exception` / `clrg:...[..]`) is resolved by reflection; a Kotlin-user
                // base is another TypeBuilder in `_types`.
                if (ti.BaseName.StartsWith("clr:") || ti.BaseName.StartsWith("clrg:"))
                {
                    ti.ClrBase = MapType(ti.BaseName);
                    ti.TB.SetParent(ParentType(ti.ClrBase));
                }
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
                        ti.TB.SetParent(ParentType(bconstructed));
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
                // One InterfaceImpl row per stated interface, in the stated order. The set is complete as it arrives:
                // every physical face a type owes — including the read-only sibling of a mutable collection face, which
                // bir2cir's ReadOnlyCollectionViewInterfaces states — is already in this array.
                foreach (var i in ifs.EnumerateArray())
                {
                    if (ReadFqn(i) is not DotKt.Bir.TypeNode.Fqn iFqn) continue;
                    // A REFERENCED interface (not in `_types` — a .NET Continuation<int>) is resolved by reflection; an
                    // emitted Kotlin interface (`Container<int>`) comes from `_types` (constructed via ParseOwnerT).
                    Type itype;
                    if (!_types.ContainsKey(iFqn.Name)) itype = MapType(iFqn);
                    else { var (open, constructed) = ParseOwnerT(iFqn); itype = constructed ?? (Type)_types[open].TB; }
                    ti.TB.AddInterfaceImplementation(itype);
                }
            }
        }
        _curTypeParams = null;

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsEnum) continue;   // enums are fully defined (literals) in pass 1
            T($"pass3 signatures: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);   // so `gp:T` in field/ctor/method signatures resolves
            if (ti.IsDelegate)
            {
                DefineDelegateMembers(ti);
                continue;
            }
            if (ti.IsFileClass)
            {
                // Top-level `val`/`var` -> static fields of the file class.
                if (ti.Def.TryGetProperty("fields", out var ffs))
                    foreach (var f in ffs.EnumerateArray())
                    {
                        var tlType = MapType(f.GetProperty("type"));
                        var tlAttrs = (f.TryGetProperty("vis", out var tlVis) ? tlVis.GetString() : "public") switch
                        {
                            "private" => FieldAttributes.Private,
                            "internal" => FieldAttributes.Assembly,
                            "protected" => FieldAttributes.Family,
                            "protectedInternal" => FieldAttributes.FamORAssem,
                            _ => FieldAttributes.Public,
                        };
                        tlAttrs |= FieldAttributes.Static;
                        if (f.TryGetProperty("initOnly", out var tlInitOnly) && tlInitOnly.GetBoolean())
                            tlAttrs |= FieldAttributes.InitOnly;
                        var tlLiteral = f.TryGetProperty("constant", out var tlLiteralValue);
                        if (tlLiteral) tlAttrs |= FieldAttributes.Literal;
                        // `@kotlin.concurrent.Volatile` on a top-level `var` -> a `modreq(IsVolatile)` static field.
                        var tlFb = f.TryGetProperty("volatile", out var tlVol) && tlVol.GetBoolean()
                                ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), tlType, tlAttrs)
                                : ti.TB.DefineField(f.GetProperty("name").GetString(), tlType, tlAttrs);
                        StampMemberAttrs(tlFb.SetCustomAttribute, f);   // [KotlinReadOnly]/[KotlinSuspendFunctionType]/… (bir2cir-generated)
                        if (tlLiteral)
                        {
                            var constant = LiteralConstant(tlLiteralValue, tlType);
                            tlFb.SetConstant(constant);
                        }
                        ti.Fields[f.GetProperty("name").GetString()] = tlFb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: true);
                DeclareProperties(ti);
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
                        if (f.TryGetProperty("initOnly", out var initOnly) && initOnly.GetBoolean())
                            fattrs |= FieldAttributes.InitOnly;
                        var ftype = MapType(f.GetProperty("type"));
                        var literal = f.TryGetProperty("constant", out var literalValue);
                        if (literal) fattrs |= FieldAttributes.Static | FieldAttributes.Literal;
                        // `@kotlin.concurrent.Volatile` -> a `modreq(IsVolatile)` field (the C# `volatile` encoding).
                        var fb = f.TryGetProperty("volatile", out var vol) && vol.GetBoolean()
                            ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), ftype, fattrs)
                            : ti.TB.DefineField(f.GetProperty("name").GetString(), ftype, fattrs);
                        StampMemberAttrs(fb.SetCustomAttribute, f);   // [KotlinReadOnly]/[KotlinSuspendFunctionType]/… (bir2cir-generated)
                        if (literal)
                        {
                            var constant = LiteralConstant(literalValue, ftype);
                            fb.SetConstant(constant);
                        }
                        ti.Fields[f.GetProperty("name").GetString()] = fb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: false);
                DeclareProperties(ti);
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
        foreach (var (typeKey, ti) in _types)
            if (!ti.IsFileClass && !ti.IsInterface && ti.Def.TryGetProperty("interfaces", out var ifs))
            {
                _curTypeParams = EffectiveTps(ti);
                // Worklist over the class's interfaces INCLUDING transitively-inherited ones (a Kotlin interface method
                // can be inherited through a chain, e.g. MonotonicTimeSource : WithComparableMarks : TimeSource — the
                // covariant markNow over TimeSource.markNow must be bridged too, or the slot stays unimplemented).
                // The interface entries are STRUCTURED Fqn nodes (birType-emitted). ilemit DERIVES the "referenced-vs-
                // emitted" decision from the name (`_types` membership), not a clr:/clrg: marker.
                // Include interfaces inherited through an emitted base class as well: a grandchild's own override may
                // carry a resolved MethodImpl for such a slot. Any synthesis decision is already a CIR fact; this
                // worklist only visits possible descriptor owners and ordinary implicit CLR bindings.
                var ifWork = new Queue<DotKt.Bir.TypeNode.Fqn>();
                var ifSeen = new HashSet<string>();
                foreach (var i in ifs.EnumerateArray())
                    if (ReadFqn(i) is DotKt.Bir.TypeNode.Fqn iff) ifWork.Enqueue(iff);
                // A resolved MethodImpl descriptor is an independent CIR obligation. Its declaring interface may no
                // longer be present as a direct edge after an earlier representation pass has introduced an
                // existential carrier, so seed that exact owner into the mechanical wiring worklist as well. This is
                // not a hierarchy reconstruction: bir2cir already stated owner/member/signature and ilemit merely
                // visits the named metadata declaration. The descriptor-first lookup below still selects the body.
                if (ti.Def.TryGetProperty("methods", out var directiveMethods))
                    foreach (var method in directiveMethods.EnumerateArray())
                        if (method.TryGetProperty("clrInterfaceImpls", out var directives)
                            && directives.ValueKind == JsonValueKind.Array)
                            foreach (var directive in directives.EnumerateArray())
                                if (directive.TryGetProperty("owner", out var ownerNode)
                                    && ReadFqn(ownerNode) is DotKt.Bir.TypeNode.Fqn owner)
                                    ifWork.Enqueue(owner);
                // Interfaces inherited through the EMITTED base-class chain, type args substituted into THIS class's frame
                // (a generic base `Shape<T> : I<T>` under `Square : Shape<int>` yields `I<int>`).
                // `chainArgs` are the current base's actual type args expressed in THIS class's frame; each descent re-
                // anchors the next base's args (stated in the current base's frame) back through `chainArgs`.
                var chainName = ti.BaseName;
                var chainArgs = ti.BaseFqn?.Args;
                while (chainName != null && _types.TryGetValue(BareTypeKey(chainName), out var bti) && !bti.IsInterface)
                {
                    if (bti.Def.ValueKind == JsonValueKind.Object && bti.Def.TryGetProperty("interfaces", out var bifs))
                        foreach (var bi in bifs.EnumerateArray())
                            if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bbi && SubstTv(bbi, chainArgs) is DotKt.Bir.TypeNode.Fqn bbiF)
                                ifWork.Enqueue(bbiF);
                    chainArgs = bti.BaseFqn?.Args?.Select(a => SubstTv(a, chainArgs)).ToArray();
                    chainName = bti.BaseName;
                }
                while (ifWork.Count > 0)
                {
                    var specFqn = ifWork.Dequeue();
                    var spec = SigCanon(specFqn);            // the canonical overload/dedup key for this interface spec
                    var specName = specFqn.Name;
                    if (!ifSeen.Add(spec)) continue;
                    // A canonicalized shared synthetic (`dotkt$CharSequence`) this app REFERENCES from the rt stdlib
                    // dll — NOT re-emitted here, so absent from `_types` — is an EXTERNAL interface: bind the class's
                    // overrides to it by reflection, exactly like a `clr:` interface, so the interface slots are wired
                    // explicitly rather than relying on an implicit name/sig match a canonicalized supertype must not
                    // depend on. (Covers both a user `class S : CharSequence` and the synthesized `dotkt$StringCharSequence`.)
                    // Checked on the RAW spec (a canonical synthetic interface spec is the bare name), so a `clr:`/`clrg:`
                    // spec is NOT ParseOwner'd here — doing so eagerly mis-strips a `clrg:` self-ref interface (crash).
                    bool externalSynthIface = ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(specName)
                        && !_types.ContainsKey(specName) && ResolvesExternally(specName);
                    // A REFERENCED interface (not emitted in THIS assembly — a .NET-mapped Continuation<int>, or an
                    // external canonical synthetic): bind each interface method to the class method of the same .NET name
                    // by reflection. An EMITTED interface (in `_types`) falls to the ParseOwner path below.
                    if (!_types.ContainsKey(specName) || externalSynthIface)
                    {
                        var itype = externalSynthIface ? ResolveType(specName) : MapType(specFqn);
                        // A SELF-REFERENTIAL constructed generic interface (e.g. `V : IComparable<V>`, V the emitted
                        // type) is a TypeBuilderInstantiation whose .GetMethods() throws. Enumerate the OPEN
                        // definition's methods and re-anchor each to the instantiation via TypeBuilder.GetMethod
                        // (same pattern as the self-ref base-ctor below).
                        // A constructed generic interface whose OPEN def is a TypeBuilder (a self-ref `V : IComparable<V>`,
                        // OR a generic STDLIB interface instantiated even with a concrete arg) is a TypeBuilderInstantiation
                        // whose .GetMethods() throws. Try GetMethods; on failure, enumerate the OPEN definition's methods
                        // and re-anchor each to the instantiation via TypeBuilder.GetMethod.
                        // #370-residual: enumerate only to choose the TypeBuilder anchoring face; NamedInterfaceSlots
                        // replaces the external set with bir2cir's resolved references before any operand is emitted.
                        MethodInfo[] ifaceMs; bool reanchor; Type slotOwner; // #370-residual: anchoring-face probe
                        try { ifaceMs = itype.GetMethods(); reanchor = false; slotOwner = itype; }
                        catch (NotSupportedException)
                        {
                            slotOwner = itype.GetGenericTypeDefinition();
                            // #370-residual: anchoring-face probe; carried slots replace this external set.
                            ifaceMs = slotOwner.GetMethods(); reanchor = true;
                        }
                        // The slots are named on the type that implements them. Only the member SET is replaced:
                        // which face to anchor against was decided just above, by the reflection that either
                        // worked or did not, and that is not a decision a reference can make.
                        if (NamedInterfaceSlots(ti, specFqn, slotOwner) is { } named) ifaceMs = named;
                        // Reflection's GetMethods() omits a referenced interface's inherited slots. bir2cir's resolved
                        // MethodImpl descriptors name each exact DECLARING interface, and those owners were seeded into
                        // this worklist above. Consume the descriptor when that owner is dequeued; probing base interfaces
                        // here as well would wire the same MethodImpl twice when the declaring owner is later visited.
                        foreach (var im in ifaceMs)
                        {
                            // #370-residual: the local axis: wiring a MethodImpl on a type being built (#395)
                            // OVERLOADED body methods (e.g. the generic CompareTo(V) + the non-generic IComparable bridge
                            // CompareTo(object)) collide in the name-keyed ti.Methods — wiring the wrong one to the slot
                            // is a TypeLoad "signature ... do not match". Disambiguate by the interface method's
                            // (instantiation-substituted) parameter types against each overload's recorded params.
                            // The interface method's (instantiation-substituted) param + return types — used both to
                            // disambiguate an overloaded body AND to decide whether the body needs a return-adapting bridge.
                            var ips = ParametersOf(im).Select(p => reanchor
                                ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                                : p.ParameterType).ToArray();
                            var methodArity = im.GetGenericArguments().Length;
                            // A bir2cir-resolved MethodImpl comes first: it names the slot and the member that fills
                            // it, so there is nothing to disambiguate. The name-based search below cannot find such a
                            // bridge — it is deliberately not named after the slot.
                            var interfaceRet = reanchor
                                ? SubstituteIfaceArgs(ReturnTypeOf(im), itype.GetGenericArguments())
                                : ReturnTypeOf(im);
                            if (FindExternalInterfaceBridge(ti, itype, im.Name, methodArity, ips, interfaceRet, specFqn)
                                is MethodBuilder directiveBridge)
                            {
                                WireMethodOverride(ti.TB, directiveBridge, reanchor ? AnchorOn(itype, im) : im);
                                continue;
                            }
                            // #370-residual: local axis — match MethodBuilders emitted in this assembly to a named slot.
                            var cands = ti.MethodsBySig
                                .Where(kv => kv.Key.Name == im.Name && kv.Key.GenericArity == methodArity)
                                .Select(kv => kv.Value)
                                .Where(b => _mparams.TryGetValue(b, out var bps)
                                    && bps.Length == ips.Length
                                    && bps.Zip(ips, SlotParamMatches).All(x => x))
                                .Distinct()
                                .ToList();
                            if (cands.Count != 1) continue;   // no unique exact CLR identity -> skip, never mis-wire
                            var body = cands[0];
                            var ifaceM = reanchor ? AnchorOn(itype, im) : im;
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
                            if (!bodyIsGeneric && ReturnTypeOf(im) == Bcl("System.Void") && body.ReturnType != Bcl("System.Void"))
                                EmitVoidDropBridge(ti, im.Name, ips, body, ifaceM);
                            else
                                WireMethodOverride(ti.TB, body, ifaceM);
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
                            if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bi0 && SubstTv(bi0, specArgs) is DotKt.Bir.TypeNode.Fqn biF) ifWork.Enqueue(biF);
                    // Iterate the interface's method DEFS (not the name-keyed iface.Methods) so OVERLOADED interface
                    // methods (e.g. MutableMap.remove(K):V vs the JVM remove(K,V):Boolean) each resolve to their own
                    // builder by signature, and to the matching body overload by TYPE-ARG-SUBSTITUTED signature. A miss
                    // when the name is AMBIGUOUS (multiple body overloads) is skipped — wiring the wrong one is the bug.
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("methods", out var ifMs))
                    {
                        foreach (var imDef in ifMs.EnumerateArray())
                        {
                            if (!imDef.TryGetProperty("params", out _)) continue;
                            // A private/final explicit MethodImpl BODY declared on an interface is not another
                            // declaration slot. In particular, a derived class must never name it as the declaration
                            // half of its own MethodImpl row: ECMA-335 rejects a final declaration there. bir2cir marks
                            // this physical role explicitly, so excluding it is mechanical CIR consumption rather than
                            // reconstructing the bridge's purpose from visibility, finality, name, or body shape.
                            if (imDef.TryGetProperty("clrInterfaceSlotBridge", out var interfaceSlotBridge)
                                && interfaceSlotBridge.GetBoolean())
                                continue;
                            var physicalInterfaceName = PhysicalMethodName(imDef);
                            var ifaceBuilder = iface.MethodsBySig.TryGetValue(
                                                   DefinitionSigKey(physicalInterfaceName, imDef), out var ib) ? ib : null;
                            if (ifaceBuilder == null) continue;
                            // The interface method's params with each Tv{type,i} re-anchored to specArgs[i], rendered to
                            // the sig-token spelling — matched against the class's own MethodsBySig (a nested value-class
                            // arg like Continuation.resumeWith(Result<T>) substitutes correctly, not just a bare gp).
                            var subSig = SigKey(physicalInterfaceName, DeclaredMethodArity(imDef),
                                imDef.GetProperty("params").EnumerateArray()
                                    .Select(p => SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs)));
                            var ifaceMethod = constructed != null ? AnchorMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
                            // A bir2cir-resolved exact MethodImpl bridge. The decision and exact slot signature are
                            // already CIR facts; this is mechanical consumption only. In particular, do not also wire
                            // the narrow Kotlin declaration, whose covariant return is not a byte-exact CLR MethodImpl.
                            var subRet = imDef.TryGetProperty("ret", out var subRetNode)
                                ? SubstTv(DotKt.Bir.TypeNode.Read(subRetNode), specArgs)
                                : null;
                            var explicitBridge = FindExplicitInterfaceBridge(
                                ti, specFqn, physicalInterfaceName, subSig, subRet, imDef);
                            if (explicitBridge != null)
                            {
                                WireMethodOverride(ti.TB, explicitBridge, ifaceMethod);
                                continue;
                            }
                            // Only wire an EXACT signature match. A miss means the class doesn't override this exact
                            // overload (e.g. it lacks the JVM remove(K,V):Boolean default) -> SKIP rather than mis-wire a
                            // different overload; for a Kotlin interface the same-name+sig method resolves implicitly anyway.
                            if (!ti.MethodsBySig.TryGetValue(subSig, out var bodyMethod))
                            {
                                // No CIR declaration or exact MethodImpl descriptor answers this slot. Inherited Kotlin
                                // default selection is a frontend fact and must not be reconstructed here by walking
                                // interface bodies/names. The CLR's ordinary DIM rules remain in force; a distinct
                                // redeclared slot that needs a bridge must be materialized by bir2cir from explicit BIR.
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
                            // A generic body has its own return frame and can never use this non-generic bridge shape.
                            // Detect it from _methodTypeParams (IsGenericMethodDefinition is unreliable on an un-baked
                            // builder) before interpreting the non-generic return comparison.
                            var bodyIsGeneric = bodyMethod is MethodBuilder gmb && _methodTypeParams.ContainsKey(gmb);
                            // #370-residual: TYPE-shape comparison deciding whether a local adapter is required.
                            if (!bodyIsGeneric && ifaceRet != null && bodyMethod.ReturnType != ifaceRet &&
                                ((bodyMethod.ReturnType.Name != ifaceRet.Name && !IsValueType(bodyMethod.ReturnType) && !IsValueType(ifaceRet))   // covariant reference narrowing
                                 || (ifaceRet == Bcl("System.Void") && bodyMethod.ReturnType != Bcl("System.Void"))))   // a BCL slot that DROPS the Kotlin return (MutableCollection.add():Boolean -> ICollection.Add():void, set/removeAt:E -> void)
                                EmitCovariantBridge(ti, physicalInterfaceName, imDef, specArgs, bodyMethod, ifaceMethod, ifaceRet);
                            else
                                WireMethodOverride(ti.TB, bodyMethod, ifaceMethod);
                        }
                    }
                }
            }

        // Consume bir2cir-authored MethodImpl descriptors on interfaces. The descriptor already identifies the private
        // final bridge body and exact external declaration slot; reflection is used only to obtain that declaration's
        // MethodInfo. Selection of a Kotlin default implementation and synthesis of its forwarding body are complete
        // before CIR reaches ilemit.
        foreach (var (_, ti) in _types)
        {
            if (!ti.IsInterface || ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var extIbs)) continue;
            _curTypeParams = EffectiveTps(ti);
            // De-dup across a diamond (`I : A, B` with `A, B : C`): one methodimpl per (baseOwner :: subSig).
            var dimImplSeen = new HashSet<string>();
            var externalBaseSpecs = extIbs.EnumerateArray().Select(ReadFqn)
                .OfType<DotKt.Bir.TypeNode.Fqn>().ToList();
            // As with class implementations above, an explicit interface MethodImpl is driven by its resolved CIR
            // owner even when a representation carrier replaced the source-level direct edge. The existing exact
            // descriptor lookup remains the only operation that can select its body.
            foreach (var method in ti.Def.GetProperty("methods").EnumerateArray())
                if (method.TryGetProperty("clrInterfaceImpls", out var directives)
                    && directives.ValueKind == JsonValueKind.Array)
                    foreach (var directive in directives.EnumerateArray())
                        if (directive.TryGetProperty("owner", out var ownerNode)
                            && ReadFqn(ownerNode) is DotKt.Bir.TypeNode.Fqn owner)
                            externalBaseSpecs.Add(owner);
            var externalBaseSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ibF in externalBaseSpecs)
            {
                if (!externalBaseSeen.Add(SigCanon(ibF))) continue;
                // A same-assembly interface slot is already named by bir2cir's exact MethodImpl descriptor. Consume
                // that instruction directly; selecting a DIM from concrete methods, names, or hierarchy order would
                // re-resolve frontend override semantics in the emitter.
                if (_types.ContainsKey(ibF.Name))
                {
                    WireResolvedEmittedInterfaceImpls(ti, ibF, dimImplSeen);
                    continue;
                }
                var itype = MapType(ibF);
                // A generic instantiation over an EMITTED TypeBuilder arg can't GetMethods() — enumerate the OPEN
                // definition and re-anchor each slot onto the instantiation (same pattern as the class wiring).
                // #370-residual: enumerate only to choose the TypeBuilder anchoring face; NamedInterfaceSlots
                // replaces the external set with bir2cir's resolved references before any operand is emitted.
                MethodInfo[] ifaceMs; bool reanchor; // #370-residual: anchoring-face probe
                try { ifaceMs = itype.GetMethods(); reanchor = false; }
                catch (NotSupportedException)
                {
                    // #370-residual: anchoring-face probe; carried slots replace this external set.
                    ifaceMs = itype.GetGenericTypeDefinition().GetMethods(); reanchor = true;
                }
                // The same resolved slot set drives MethodImpls authored ON an emitted interface as drives class
                // implementations above. Omitting it here left this second wiring loop selecting declarations by
                // reflection even though the producer had already named every one (notably star-projection carriers).
                var slotOwner = reanchor ? itype.GetGenericTypeDefinition() : itype;
                if (NamedInterfaceSlots(ti, ibF, slotOwner) is { } named) ifaceMs = named;
                foreach (var im in ifaceMs)
                {
                    var ips = ParametersOf(im).Select(p => reanchor
                        ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                        : p.ParameterType).ToArray();
                    // Match the complete CLR method identity, including method generic arity. A same-name/same-param
                    // generic/non-generic pair is two distinct slots, never an overload ambiguity (#86 Phase 0).
                    var methodArity = im.GetGenericArguments().Length;
                    var interfaceRet = reanchor
                        ? SubstituteIfaceArgs(ReturnTypeOf(im), itype.GetGenericArguments())
                        : ReturnTypeOf(im);
                    if (FindExternalInterfaceBridge(ti, itype, im.Name, methodArity, ips, interfaceRet, ibF)
                        is MethodBuilder resolvedBridge)
                    {
                        WireMethodOverride(ti.TB, resolvedBridge, reanchor ? AnchorOn(itype, im) : im);
                    }
                    // No exact CIR descriptor means no MethodImpl. Which Kotlin declaration (if any) implements an
                    // external slot is a Frontend/bir2cir decision; ilemit must not rediscover it from names, concrete
                    // methods, or interface hierarchy order.
                }
            }
        }

        // A bir2cir-resolved MethodImpl against a BASE-CLASS slot (`clrBaseImpls`, #86 D3). The interface twin of this
        // instruction (`clrInterfaceImpls`) is consumed in the interface wiring above; a base-CLASS slot has no such
        // loop because a class override normally binds by name+signature alone. An erasure bridge does NOT: it carries
        // a synthesized name so it cannot shadow the declaration it forwards to, so the slot it fills must be named.
        // Mechanical consumption only — the decision and the exact slot signature are already CIR facts.
        foreach (var (_, ti) in _types)
        {
            if (ti.IsInterface || ti.Def.ValueKind != JsonValueKind.Object
                || !ti.Def.TryGetProperty("methods", out var baseImplMethods)) continue;
            _curTypeParams = EffectiveTps(ti);
            foreach (var m in baseImplMethods.EnumerateArray())
            {
                if (!m.TryGetProperty("clrBaseImpls", out var impls) || impls.ValueKind != JsonValueKind.Array)
                    continue;
                var bridgeName = PhysicalMethodName(m);
                if (!ti.MethodsBySig.TryGetValue(DefinitionSigKey(bridgeName, m), out var bridge))
                    throw new InvalidOperationException(
                        $"ilemit: resolved base MethodImpl body {ti.TB.Name}.{SigKey(bridgeName, m)} is absent");
                // A GENERIC slot's descriptor states its parameter vector in the BRIDGE's own vocabulary, so a
                // method-scope `tv` in it names one of the bridge's own type parameters. Install that exact method
                // frame while consuming the descriptor; resolving it against the enclosing type would cross ownership.
                _curMethodParams = _methodTypeParams.TryGetValue(bridge, out var bridgeTps) ? bridgeTps : null;
                foreach (var impl in impls.EnumerateArray())
                {
                    if (!impl.TryGetProperty("owner", out var ownerNode) || ReadFqn(ownerNode) is not { } ownerFqn
                        || !impl.TryGetProperty("member", out var memberNode)
                        || !impl.TryGetProperty("params", out var ps)
                        || !impl.TryGetProperty("ret", out var ret)) continue;
                    var (open, constructed) = ParseOwnerT(ownerFqn);
                    // A base class declared in a REFERENCED assembly is resolved through reflection, exactly as the
                    // referenced-INTERFACE wiring resolves its slot: the descriptor states the constructed owner, the
                    // member and the parameter vector, and there is a real MethodInfo to point the MethodImpl at.
                    // A `class C : Base<Int>()` over a referenced generic base reaches this and used to abort the
                    // emit; refusing it is not an option either, since the abstract slot would go unimplemented.
                    if (!_types.ContainsKey(open))
                    {
                        // A RESOLVED MethodImpl THAT CANNOT BE LINKED IS AN EARLIER-LAYER DROP, and silence here is
                        // the worst outcome available: an abstract base slot becomes a type-load failure with nothing
                        // naming the producer, and a concrete virtual one keeps dispatching to the base body — the
                        // override simply never runs. Same contract as the emitted-base miss below.
                        // The slot is a lookup: the reference travelling with this descriptor names it.
                        var externalSlot = PrimaryFromRef(impl, "memberRef") as MethodInfo;
                        if (externalSlot is null)
                            throw new InvalidOperationException(
                                $"ilemit: {ti.TB.Name}.{bridgeName}: clrBaseImpls names "
                                + $"'{memberNode.GetString()}' on the referenced base '{open}', which does not resolve "
                                + "to exactly one method of that signature — bir2cir resolved a base-class MethodImpl "
                                + "this layer cannot link");
                        WireMethodOverride(ti.TB, bridge, externalSlot);
                        continue;
                    }
                    // A RESOLVED DESCRIPTOR THIS LAYER CANNOT BIND IS AN EARLIER-LAYER DROP, and dropping it here
                    // would leave the base slot unimplemented — a TypeLoadException at run time with nothing pointing
                    // back at the producer.
                    if (!_types.TryGetValue(open, out var baseTi))
                        throw new InvalidOperationException(
                            $"ilemit: {ti.TB.Name}.{bridgeName}: clrBaseImpls names '{open}', which is not "
                            + "emitted in this assembly — bir2cir resolved a base-class MethodImpl against a type that is not here");
                    // THE DESCRIPTOR IS THE CONSTRUCTED SLOT; THE BUILDER IS KEYED BY THE DECLARATION. `Base<T>`'s
                    // `take(T, T?)` is emitted once, keyed `(gp:T, object)`, while the descriptor for `Base<Int>`
                    // states `(int32, object)` — so the constructed signature never finds it directly, and matching on
                    // it would refuse a program whose only sin is a generic base with a non-erased parameter. Find the
                    // DECLARATION whose own parameters, substituted at this base's type arguments, ARE the descriptor,
                    // then re-anchor its builder onto the constructed base — the same two steps the emitted-interface
                    // wiring takes (`subSig` there, `TypeBuilder.GetMethod` here).
                    var member = memberNode.GetString();
                    var describedArity = DescribedArity(impl);
                    var slotSig = SigKey(member, describedArity,
                        ps.EnumerateArray().Select(DotKt.Bir.TypeNode.Read));
                    var describedRet = DotKt.Bir.TypeNode.Read(ret);
                    MethodBuilder slot = null;
                    if (baseTi.Def.TryGetProperty("methods", out var baseDecls))
                    {
                        foreach (var bm in baseDecls.EnumerateArray())
                        {
                            if (!bm.TryGetProperty("name", out var bn)) continue;
                            var declarationName = bn.GetString();
                            var physicalDeclarationName = PhysicalMethodName(bm);
                            if (physicalDeclarationName != member || DeclaredMethodArity(bm) != describedArity
                                || !DescriptorTypeParamsMatch(impl, bm, ownerFqn.Args)) continue;
                            if (!bm.TryGetProperty("params", out var bps)) continue;
                            var declared = DefinitionSigKey(physicalDeclarationName, DeclaredMethodArity(bm),
                                bps.EnumerateArray().Select(p => DotKt.Bir.TypeNode.Read(p.GetProperty("type"))));
                            var substituted = SigKey(member, DeclaredMethodArity(bm),
                                bps.EnumerateArray()
                                    .Select(p => SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), ownerFqn.Args)));
                            if (substituted != slotSig) continue;
                            if (!bm.TryGetProperty("ret", out var baseRet)
                                || !SubstTv(DotKt.Bir.TypeNode.Read(baseRet), ownerFqn.Args).Equals(describedRet))
                                continue;
                            if (baseTi.MethodsBySig.TryGetValue(declared, out var found)) { slot = found; break; }
                        }
                    }
                    if (slot == null)
                        throw new InvalidOperationException(
                            $"ilemit: {ti.TB.Name}.{bridgeName}: clrBaseImpls names {open}.{slotSig}, "
                            + "which that type declares no member for at any instantiation — bir2cir resolved a base-class "
                            + "MethodImpl against a missing slot");
                    WireMethodOverride(ti.TB, bridge,
                        constructed != null ? AnchorMethod(constructed, slot) : (MethodInfo)slot);
                }
                _curMethodParams = null;
            }
        }
        _curTypeParams = null;

        // Pass 4: emit all bodies (every ctor/method signature already exists). Each body emit is GUARDED (#84 Phase 1):
        // a throw is re-tagged with the declaration being emitted (via CurrentDecl) so one bad method names itself in a
        // clean `ilemit: <Type>.<method>: <message>` line, and the rest are unaffected. Byte-identical on success.
        foreach (var ti in _types.Values)
            if (!ti.IsDelegate)
                for (int ci = 0; ci < ti.Ctors.Count; ci++) { T($"pass4 ctor body: {ti.TB?.Name}#{ci}"); var cb = ti.Ctors[ci]; var cd = ti.CtorDefs[ci]; GuardBody(() => EmitCtorBody(ti, cb, cd)); }
        foreach (var ti in _types.Values)
            if (!ti.IsEnum && !ti.IsDelegate)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray())
                {
                    // A CIR extern is deliberately bodyless (currently caller-side UnsafeAccessor). Its signature and
                    // custom attributes were defined above; the CLR supplies the implementation.
                    if (m.TryGetProperty("extern", out var externFlag) && externFlag.GetBoolean()) continue;
                    // Interfaces: the CIR `abstract` fact distinguishes an abstract slot from a default method.
                    // A valid void default implementation may have an empty statement list (`set(value) {}`), so
                    // body presence is not a semantic discriminator. EmitMethodBody will append its required `ret`.
                    if (ti.IsInterface
                        && !(m.TryGetProperty("static", out var interfaceStatic) && interfaceStatic.GetBoolean())
                        && m.GetProperty("abstract").GetBoolean()) continue;
                    T($"pass4 method body: {ti.TB?.Name}.{(m.TryGetProperty("name", out var mn) ? mn.GetString() : "?")}"); GuardBody(() => EmitMethodBody(ti, m));
                }

        // User annotations -> .NET custom attributes, applied on the type and its methods (the ctor builder of the
        // synthesized `: System.Attribute` class already exists). Args are compile-time constants.
        foreach (var ti in _types.Values)
        {
            // #71 S2: EVERY attribute here — user annotations AND the Kotlin round-trip metadata ([NullableContext]/
            // [KotlinFileClass]/[KotlinFunInterface]/[KotlinSealed] on the type; [KotlinFunction]/[KotlinInline] on
            // methods; [Nullable]/[KotlinSuspendFunctionType] in return position) — is an ordinary CIR `attrs`/`retAttrs`
            // entry that bir2cir (RoundtripMetadata) generated. ilemit only STAMPS them dumbly through BuildAttribute; the
            // Kotlin-semantic DECISION (which modifier -> which attribute) lives in bir2cir. A runtime-build CIR carries
            // none (the pass is skipped there), so there is nothing to strip.
            if (ti.TB != null && ti.Def.TryGetProperty("attrs", out var tattrs))
                foreach (var a in tattrs.EnumerateArray()) { var encoded = BuildAttribute(a); if (encoded != null) ti.TB.SetCustomAttribute(encoded.Constructor, encoded.Blob); }
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
                    var mname = PhysicalMethodName(m);
                    if (!ti.MethodsBySig.TryGetValue(DefinitionSigKey(mname, m), out var mb))
                        throw new InvalidOperationException(
                            $"ilemit: attributed MethodDef {ti.TB.FullName}.{SigKey(mname, m)} is absent");
                    if (hasA)
                        foreach (var a in mattrs.EnumerateArray()) { var encoded = BuildAttribute(a); if (encoded != null) mb.SetCustomAttribute(encoded.Constructor, encoded.Blob); }
                    // Return-position attrs ride the return parameter (position 0), defined once.
                    if (hasR)
                    {
                        var retPb = mb.DefineParameter(0, ParameterAttributes.None, null);
                        foreach (var a in rattrs.EnumerateArray()) { var encoded = BuildAttribute(a); if (encoded != null) retPb.SetCustomAttribute(encoded.Constructor, encoded.Blob); }
                    }
                }
        }

        // Pass 4b: static-field initializers -> a type initializer (.cctor). A CLR interface may legally have one.
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
            // A synthesized .cctor is a type method with no method-generic scope. Carry the declaring type's generic
            // parameters and clear whatever method scope pass 4 last visited; otherwise a generic nested singleton
            // initializer can encode `new ...<!!0>` and produce a BadImageFormatException at type initialization.
            _curTypeParams = EffectiveTps(ti);
            _curMethodParams = null;
            _il = ti.TB.DefineTypeInitializer().GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = Bcl("System.Void");
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
                GuardBody(() => { PrescanCfgLabels(f.GetProperty("init")); EmitStoreCoerced(f.GetProperty("init"), fb.FieldType); MaybeVolatile(fb); EmitField(_il, OpCodes.Stsfld, fb); });
            }
            _il.Emit(OpCodes.Ret);
        }
        _curTypeParams = null;

        // Pass 5: synthesize entry point on the file class that has `main`.
        MethodBuilder entry = null;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.FileElem.Value.GetProperty("hasMain").GetBoolean() && ti.Methods.ContainsKey("main"))
            {
                entry = ti.TB.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, Bcl("System.Void"), new[] { Bcl("System.String").MakeArrayType() });
                var il = entry.GetILGenerator();
                var mainMb = ti.Methods["main"];
                // `fun main(args: Array<String>)` -> forward the CLR args; `fun main()` -> call with none.
                if (_mparams.TryGetValue(mainMb, out var mp) && mp.Length > 0) il.Emit(OpCodes.Ldarg_0);
                EmitMethod(il, OpCodes.Call, mainMb);
                il.Emit(OpCodes.Ret);
            }

        // Pass 6: bake types (base before derived). Enums were already baked up front.
        foreach (var ti in Ordered()) { if (!ti.IsEnum) { T($"pass6 createType: {ti.TB?.Name}"); ti.TB.CreateType(); } }
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
                    if (!child.IsFileClass && child.Def.TryGetProperty("nestedIn", out var cni) && cni.GetString() == myName)
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

    // Real CLR properties over already-declared accessor methods. Both ordinary types and file facades use the same
    // CIR declaration record; a top-level delegated property especially needs this explicit link because its physical
    // storage is `<name>$delegate`, not a field from which dll2klib could reconstruct the source property name.
    void DeclareProperties(TypeInfo ti)
    {
        if (!ti.Def.TryGetProperty("properties", out var props)) return;
        foreach (var p in props.EnumerateArray())
        {
            MethodBuilder gm = null;
            MethodBuilder sm = null;
            if (p.TryGetProperty("get", out var g) && g.ValueKind == JsonValueKind.String)
                gm = ResolvePropertyAccessor(ti, p, g.GetString(), "get");
            if (p.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.String)
                sm = ResolvePropertyAccessor(ti, p, s.GetString(), "set");
            // ECMA-335 requires the Property signature to describe the accessor's index parameters. Most Kotlin
            // properties have none, but a context/extension property's getter physically receives those arguments.
            // A setter's final parameter is the value, not an index parameter.
            var propertyParams = gm != null
                ? _mparams[gm]
                : sm != null ? _mparams[sm].SkipLast(1).ToArray() : Type.EmptyTypes;
            var pb = ti.TB.DefineProperty(
                p.GetProperty("name").GetString(),
                PropertyAttributes.None,
                MapType(p.GetProperty("type")),
                propertyParams);
            if (gm != null) pb.SetGetMethod(gm);
            if (sm != null) pb.SetSetMethod(sm);
            StampMemberAttrs(pb.SetCustomAttribute, p);   // [KotlinSuspendFunctionType]/… (bir2cir-generated)
        }
    }

    MethodBuilder ResolvePropertyAccessor(TypeInfo ti, JsonElement property, string name, string role)
    {
        if (!property.TryGetProperty(role + "Sig", out var signature))
            throw new InvalidOperationException(
                $"ilemit: Property accessor {ti.TB.FullName}.{name} has no resolved {role}Sig descriptor");
        var methodArity = property.TryGetProperty(role + "MethodArity", out var arity)
            ? arity.GetInt32()
            : 0;
        var parameterTypes = signature.EnumerateArray().Select(DotKt.Bir.TypeNode.Read).ToArray();
        if (ti.MethodsBySig.TryGetValue(DefinitionSigKey(name, methodArity, parameterTypes), out var exact))
            return exact;
        throw new InvalidOperationException(
            $"ilemit: resolved Property accessor descriptor {ti.TB.FullName}.{DefinitionSigKey(name, methodArity, parameterTypes)} does not link exactly");
    }

    // Method-level generic params, keyed by MethodInfo, so call sites can MakeGenericMethod.
    readonly Dictionary<MethodBuilder, Dictionary<string, GenericTypeParameterBuilder>> _methodTypeParams = new();

    // Every generic parameter belonging to an EMITTED METHOD, by identity. `GenericTypeParameterBuilder` reports
    // neither `DeclaringMethod` nor `DeclaringType` — measured, and identically so for a TYPE's parameter — so
    // nothing on the object itself says which scope it is in, and `!!i` vs `!i` is a signature difference. Every
    // method that declares generic parameters records them here, through `RecordMethodTps`.
    readonly HashSet<Type> _emittedMethodTps = new(ReferenceEqualityComparer.Instance);

    void RecordMethodTps(MethodBuilder mb, Dictionary<string, GenericTypeParameterBuilder> map)
    {
        _methodTypeParams[mb] = map;
        foreach (var g in map.Values) _emittedMethodTps.Add(g);
    }

    void DeclareMethod(TypeInfo ti, JsonElement m, bool isStatic)
    {
        var logicalName = m.GetProperty("name").GetString();
        var name = PhysicalMethodName(m);
        // bir2cir owns physical method allocation (#395). A duplicate CIR signature is therefore malformed input;
        // ilemit must not invent an order-dependent `$dupN` name that declarations and use sites cannot share.
        var dupKey = DefinitionSigKey(name, m);
        if (ti.MethodsBySig.ContainsKey(dupKey))
            throw new InvalidOperationException(
                $"ilemit: duplicate physical method signature {ti.TB?.FullName}.{name}; bir2cir must allocate a unique MethodDef name");
        var interfaceAbstract = false;
        var interfaceSlot = ti.IsInterface
            && !(isStatic || (m.TryGetProperty("static", out var declaredStatic) && declaredStatic.GetBoolean()));
        if (interfaceSlot)
        {
            if (!m.TryGetProperty("abstract", out var abstractFact)
                || abstractFact.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException(
                    $"ilemit: interface method '{ti.TB?.FullName}.{name}' has no explicit CIR abstract modality fact");
            interfaceAbstract = abstractFact.GetBoolean();
        }
        var clrInterfaceSlotBridge = ti.IsInterface
            && m.TryGetProperty("clrInterfaceSlotBridge", out var cis)
            && cis.GetBoolean();
        // Source interface members are public. A bir2cir-authored explicit MethodImpl body retains its stated private
        // visibility and finality; this is direct consumption of the CIR slot instruction.
        var attrs = ti.IsInterface && !clrInterfaceSlotBridge ? MethodAttributes.Public : AccessOf(m);
        // A method's own `static` flag (companion methods are static members of a user class).
        isStatic = isStatic || m.GetProperty("static").GetBoolean();
        var objOverride = m.TryGetProperty("objectOverride", out var oo) && oo.GetBoolean();
        // Overriding a .NET base virtual (e.g. `override val Message`) reuses the base slot, like an object-method.
        // `requiresClrOverride` is the one-to-one CIR instruction to emit a MethodImpl; `clrOverrideRef` is its
        // already-selected operand. Keeping the trigger separate from the operand makes either missing half a
        // malformed document instead of silently changing virtual dispatch.
        var hasClrOverride = m.TryGetProperty("requiresClrOverride", out var clrOverrideRequired)
            && clrOverrideRequired.GetBoolean();
        // The frontend-stated abstract modality decides whether an interface member is a CLR abstract slot. A concrete
        // Kotlin DIM may have an empty Unit body, so body length is not a declaration-semantics oracle.
        // A compiler-authored static interface helper takes no slot, so it must NOT be marked Virtual/NewSlot/Abstract
        // (a static abstract interface method would demand an implementer). Only genuine instance interface members
        // become virtual slots / abstract DIMs.
        if (ti.IsInterface && !isStatic)
        {
            attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (clrInterfaceSlotBridge)
                attrs |= MethodAttributes.Final | MethodAttributes.HideBySig;
            else if (interfaceAbstract)
                attrs |= MethodAttributes.Abstract;
        }
        else if (ti.IsInterface && isStatic) attrs |= MethodAttributes.Static;
        else if (isStatic) attrs |= MethodAttributes.Static;
        if (m.TryGetProperty("extern", out var externFlag) && externFlag.GetBoolean())
            attrs |= MethodAttributes.HideBySig;
        // `ToString`/`Equals`/`GetHashCode` and .NET base overrides reuse the base slot (Virtual, no NewSlot).
        else if (objOverride || hasClrOverride) attrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
        else if (m.GetProperty("override").GetBoolean()) attrs |= MethodAttributes.Virtual;
        else if (m.GetProperty("virtual").GetBoolean()) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        // An `abstract fun` (no body) -> a CLR abstract method: Virtual|Abstract, no IL body (subclasses override).
        if (m.TryGetProperty("abstract", out var amb) && amb.GetBoolean()) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual;
        // A synthesized event accessor (add_/remove_/raise_<E>, §4.2) is `specialname` (the ECMA-335 event-accessor
        // convention) so the emitted `.event` is a clean reflectable member. bir2cir stamps the flag on the rewritten accessor.
        if (m.TryGetProperty("specialName", out var spn) && spn.GetBoolean()) attrs |= MethodAttributes.SpecialName;

        // NOTE: a coroutine arrives here as ordinary CIR and nothing on this path knows it is one. The cold-core
        // lowering (bir2cir) has already made the public `Task<T>` bridge its OWN method carrying `suspendBridge:true`
        // (from which bir2cir RoundtripMetadata generates the `[KotlinFunction(Suspend)]` attr, #71 S2), and the cold
        // entry / state-machine class are plain methods/types. The Kotlin `suspend` modifier itself does not reach CIR.

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
            RecordMethodTps(mb, map);
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
        ti.Methods[name] = mb;
        ti.MethodsBySig[DefinitionSigKey(name, m)] = mb;
        // Temporary call-side alias: distinct definitions can intentionally share this wildcard key. Keep the first;
        // constructed-owner structural matching enumerates the exact definitions and selects after substitution.
        ti.MethodsBySig.TryAdd(SigKey(name, m), mb);
        // Calls whose frontend declaration identity is not yet transported through an erased overload set remain #395.
        // Preserve the first declaration's logical lookup without changing the emitted MethodDef name; exact bir2cir-
        // authored calls and all MethodImpl descriptors use the physical key above.
        ti.Methods.TryAdd(logicalName, mb);
        ti.MethodsBySig.TryAdd(DefinitionSigKey(logicalName, m), mb);
        ti.MethodsBySig.TryAdd(SigKey(logicalName, m), mb);
        ti.MethodNameCounts[name] = ti.MethodNameCounts.TryGetValue(name, out var nameCount) ? nameCount + 1 : 1;
        _mparams[mb] = ps;   // MethodBuilder.GetParameters() throws pre-bake; record param types for call-site boxing
        DefineParamNames(mb, m);
        if (objOverride)
        {
            var objM = name switch
            {
                "ToString" => WellKnown<MethodInfo>("Object.ToString"),
                "GetHashCode" => WellKnown<MethodInfo>("Object.GetHashCode"),
                "Equals" => WellKnown<MethodInfo>("Object.Equals"),
                _ => null,
            };
            if (objM != null) WireMethodOverride(ti.TB, mb, objM);
        }
        if (hasClrOverride)
        {
            // Link the override to the EXACT .NET base virtual so virtual dispatch through the base type reaches
            // it (`callvirt System.Exception::get_Message` -> our override). The slot is the one scalar reference
            // bir2cir resolved; the independent boolean above is only the emission instruction, not another identity.
            WireMethodOverride(ti.TB, mb, RequiredRef<MethodInfo>(m, "clrOverrideRef", $"the override {name}"));
        }
        // Kotlin's `@kotlin.internal.InlineOnly` says "this fn is meant to be inlined, not called as a method". The direct
        // CLR translation is a [MethodImpl(AggressiveInlining)] hint on the emitted method. kotc reads the annotation and
        // emits `mods.inlineOnly`; ilemit stamps the flag. Pure metadata, no behavior change; the JIT ignores the hint for
        // a too-large method. Skip abstract slots (no body to inline). ilemit adds no Kotlin knowledge — it stamps a flag.
        if (ModFlag(m, "inlineOnly") && (attrs & MethodAttributes.Abstract) == 0)
            mb.SetImplementationFlags(mb.GetMethodImplementationFlags() | MethodImplAttributes.AggressiveInlining);
    }

    // Define a type's constructors from its CIR (idempotent). Normally runs in pass 3, but BuildAttribute pulls it EARLY when
    // stamping a param/method attribute whose attribute type is emitted in THIS assembly (e.g. `@kotlin.clr.KotlinDefault
    // (index, bir)` on a defaulted stdlib parameter): pass 3 declares members type-by-type, so a `@KotlinDefault`
    // application on an EARLIER type's method would otherwise reach BuildAttribute before KotlinDefault's own `(int,string)`
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
            StampMemberAttrs(cb.SetCustomAttribute, c); // declaration carriers/annotations belong to the ctor row
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
        try { open = ResolveType(name + "`" + arity); }
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
            var parameterAttributes = GenericParameterAttributes.None;
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
                parameterAttributes |= attr;
            }
            if (x.TryGetProperty("specialConstraints", out var specials))
            {
                foreach (var special in specials.EnumerateArray())
                    parameterAttributes |= special.GetString() switch
                    {
                        "class" => GenericParameterAttributes.ReferenceTypeConstraint,
                        "struct" => GenericParameterAttributes.NotNullableValueTypeConstraint,
                        "new" => GenericParameterAttributes.DefaultConstructorConstraint,
                        "allowsRefStruct" => GenericParameterAttributes.AllowByRefLike,
                        var value => throw new InvalidDataException(
                            $"unknown generic-parameter special constraint '{value}'"),
                    };
            }
            if (parameterAttributes != GenericParameterAttributes.None)
                gp.SetGenericParameterAttributes(parameterAttributes);
            if (x.TryGetProperty("constraints", out var cs))
            {
                var types = cs.EnumerateArray().Select(c => MapType(c)).ToList();
                var ifaces = types.Where(IsInterfaceType).ToArray();
                var baseT = types.FirstOrDefault(t => !IsInterfaceType(t));
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
            Bcl("System.Void"), paramTypes);
        StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
        var il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
        var bodyCall = ti.IsGeneric ? AnchorMethod(ConstructedType(ti.TB, ti.TB.GetGenericArguments()), body) : (MethodInfo)body;
        EmitMethod(il, OpCodes.Callvirt, bodyCall);
        il.Emit(OpCodes.Pop);   // the BCL slot drops the Kotlin return
        il.Emit(OpCodes.Ret);
        WireMethodOverride(ti.TB, bridge, ifaceMethod);
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
        var bodyCall = ti.IsGeneric ? AnchorMethod(ConstructedType(ti.TB, ti.TB.GetGenericArguments()), body) : (MethodInfo)body;
        EmitMethod(il, OpCodes.Callvirt, bodyCall);
        // ifaceRet==void but the body returns a value (add():Boolean -> ICollection.Add():void): the BCL slot drops the
        // Kotlin return -> pop it so the void bridge leaves an empty stack. Else the (reference) narrow return upcasts.
        if (ifaceRet == Bcl("System.Void") && body.ReturnType != Bcl("System.Void")) il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        WireMethodOverride(ti.TB, bridge, ifaceMethod);
    }

    // A structured owner Fqn -> (open name, constructed .NET type or null for a non-generic). An emitted open type
    // (`_types`) is MakeGenericType'd; a referenced generic is arity-suffixed by reflection.
    (string open, Type constructed) ParseOwnerT(DotKt.Bir.TypeNode.Fqn f)
    {
        if (f.Args == null) return (f.Name, null);
        var args = f.Args.Select(a => RequireGenericArgument(MapType(a), a)).ToArray();
        if (_types.TryGetValue(f.Name, out var ti)) return (f.Name, ConstructedType(ti.TB, args));
        // bir2cir may carry an exact nested generic TypeDef token whose outer segment already owns the CLR arity
        // (`Outer`1+Nested`). Appending another suffix would name a different, absent TypeDef.
        var reflectedName = f.Name.Contains('`') ? f.Name : f.Name + "`" + args.Length;
        return (f.Name, ConstructedType(ResolveType(reflectedName), args));
    }

    void Save(PersistedAssemblyBuilder ab, MethodBuilder entry)
    {
        // How much of what these documents carry this build put through the parity check. A measurement, not
        // the gate — the enforcement is at the call sites, which compare before every legacy resolution.
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            peHeader, new MetadataRootBuilder(metadata), ilStream,
            mappedFieldData: fieldData,
            entryPoint: entry != null ? MetadataTokens.MethodDefinitionHandle(entry.MetadataToken) : default);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        // #52 — write ATOMICALLY (temp + rename): FileMode.Create truncates-then-writes in place, so a concurrent
        // reader (dll2klib/bir2cir loading this same dll) can observe a partial image and fail with a
        // spurious "Format of the executable is invalid" / BadImageFormatException. A same-directory rename is atomic,
        // so a reader always sees either the whole old file or the whole new one — never a torn write.
        var dllPath = Path.Combine(_outDir, _asmName + ".dll");
        AtomicFile.Write(dllPath, fs => blob.WriteContentTo(fs));
        Console.WriteLine($"emitted {_asmName}.dll");
    }

}
