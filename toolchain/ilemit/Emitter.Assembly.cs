// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Toolchain;
using DotKt.Cir;

// EmitAssembly passes 1-6 (DefineType/bases/signatures/bodies/.cctor/entry/bake), bridges, and Save.
sealed partial class Emitter
{
    public void EmitAssembly(List<CirFileDeclaration> files)
    {
        // #84 Phase 4: run the in-process CIR SANITY gate at the CIR boundary, BEFORE any emit — malformed CIR
        // (undeclared local, dangling goto, missing owner) fails LOUD with a precise `sanity: <invariant>` message
        // (routed through Phase 1's diagnostic) instead of a cryptic Reflection.Emit crash / silent BadImageFormat.
        // See Emitter.Sanity.cs. Pure fail-fast validation: a valid CIR reaches emission unchanged.
        CheckCir(files);
        // An empty source set has no body/signature operation that can consume a fixed member. Keep empty-library
        // emission independent of the stdlib binding table so deleting the final source can replace the previous
        // assembly with a valid metadata-only shell instead of retaining stale declarations.
        if (files.Count != 0) LoadWellKnown(files);
        // #370: how many references these documents carry, so the parity check below can be held to all of them.
        // #336: PersistedAssemblyBuilder and every external type/member below share one target
        // MetadataLoadContext. The compiler host still supplies Reflection.Emit's implementation, never an emitted
        // identity. Mixing a host Type with this graph is invalid even when its FullName matches a target Type.
        var ab = new PersistedAssemblyBuilder(new AssemblyName(_asmName) { Version = _versions?.Identity }, _target.CoreAssembly);
        if (_referenceAssemblyPath != null)
        {
            // member-lookup-residual: CLR reference image marker, not a Kotlin program operation
            var marker = Bcl("System.Runtime.CompilerServices.ReferenceAssemblyAttribute").GetConstructor(Type.EmptyTypes);
            SetAttribute(ab.SetCustomAttribute, marker, Type.EmptyTypes);
        }
        if (_versions != null)
        {
            foreach (var attribute in _versions.Attributes)
            {
                // member-lookup-residual: project-selected assembly boilerplate, not a Kotlin program operation
                var ctor = Bcl(attribute.TypeName).GetConstructor(new[] { Bcl("System.String") });
                ab.SetCustomAttribute(ctor, attribute.Blob);
            }
        }
        // The driver supplies physical JIT policy, independently of symbols or project configuration names.
        // This constructor sets only Default/DisableOptimizations, never Edit and Continue or PDB suppression.
        if (_jitDebugging is bool jitDebugging)
        {
            var parameters = new[] { Bcl("System.Boolean"), Bcl("System.Boolean") };
            // member-lookup-residual: driver-selected physical assembly metadata, not a Kotlin program call
            var constructor = Bcl("System.Diagnostics.DebuggableAttribute").GetConstructor(parameters);
            SetAttribute(ab.SetCustomAttribute, constructor, parameters, jitDebugging, jitDebugging);
        }
        // Assembly provenance: the emitter owns the final assembly in BOTH SDK and direct-CLI flows, so it stamps an
        // explicit, versioned DotKt protocol marker here (not in MSBuild-only SDK plumbing). dll2klib requires this
        // signal together with compiler-generated embedded metadata carriers before applying Kotlin-only reverse maps.
        const string dotKtMarkerKey = "DotKt.Compiler";
        const string dotKtMarkerValue = "metadata-v1";
        // member-lookup-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
        var assemblyMetadataCtor = Bcl("System.Reflection.AssemblyMetadataAttribute").GetConstructor(new[] { Bcl("System.String"), Bcl("System.String") });
        SetAttribute(ab.SetCustomAttribute, assemblyMetadataCtor,
            new[] { Bcl("System.String"), Bcl("System.String") }, dotKtMarkerKey, dotKtMarkerValue);
        // The project/direct driver owns the target framework fact. Stamp it only when explicitly supplied; deriving
        // it from ilemit's net10 host would make cross-target output lie about its contract.
        if (_targetFrameworkMoniker != null)
        {
            var targetFrameworkCtor = Bcl("System.Runtime.Versioning.TargetFrameworkAttribute")
                // member-lookup-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
                .GetConstructor(new[] { Bcl("System.String") });
            SetAttribute(ab.SetCustomAttribute, targetFrameworkCtor,
                new[] { Bcl("System.String") }, _targetFrameworkMoniker);
        }
        // Platform-qualified TFMs carry two independent SDK-owned facts. MSBuild supplies the exact strings it would
        // emit as attributes; ilemit preserves them on the final assembly without parsing a TFM name or deriving
        // target capabilities from its host.
        if (_targetPlatform != null)
        {
            var targetPlatformCtor = Bcl("System.Runtime.Versioning.TargetPlatformAttribute")
                // member-lookup-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the target platform, not a call any program makes
                .GetConstructor(new[] { Bcl("System.String") });
            SetAttribute(ab.SetCustomAttribute, targetPlatformCtor,
                new[] { Bcl("System.String") }, _targetPlatform);
        }
        if (_supportedOsPlatform != null)
        {
            var supportedOsPlatformCtor = Bcl("System.Runtime.Versioning.SupportedOSPlatformAttribute")
                // member-lookup-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the supported platform floor, not a call any program makes
                .GetConstructor(new[] { Bcl("System.String") });
            SetAttribute(ab.SetCustomAttribute, supportedOsPlatformCtor,
                new[] { Bcl("System.String") }, _supportedOsPlatform);
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
        var pendingTypes = files.SelectMany(file => file.Types).ToList();
        var nestedOwners = pendingTypes.Where(type => type.NestedIn.HasValue)
            .Select(type => type.NestedIn.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Methods.Length > 0 || file.Fields.Length > 0 || nestedOwners.Contains(file.FileClass))
                _types[file.FileClass] = new TypeInfo {
                    TB = _mod.DefineType(file.FileClass, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract),
                    Def = file, IsFileClass = true,
                };
        }

        // CIR states the enclosing declaration explicitly; a child may precede that declaration in its input file.
        while (pendingTypes.Count > 0)
        {
            var progressed = false;
            foreach (var type in pendingTypes.ToArray())
            {
                if (type.NestedIn.HasValue && !_types.ContainsKey(type.NestedIn.Value)) continue;
                pendingTypes.Remove(type);
                progressed = true;
                var name = type.Name;
                var generated = type.Generated.OrDefault();
                if (generated && _types.ContainsKey(name)) continue;
                if (ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(name) && ResolvesExternally(name)) continue;

                if (type.Kind == CirTypeKind.Delegate)
                {
                    var builder = _mod.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                        Bcl("System.MulticastDelegate"));
                    if (generated) StampCompilerGenerated(builder);
                    var declaration = new TypeInfo { TB = builder, Def = type, IsDelegate = true };
                    var parameters = type.TypeParams.OrEmpty();
                    if (parameters.Length > 0)
                        declaration.TypeParams.AddRange(builder.DefineGenericParameters(parameters.Select(p => p.Name).ToArray()));
                    _types[name] = declaration;
                    continue;
                }

                var visibility = type.Vis.OrDefault("public");
                TypeInfo parent = null;
                var nested = type.NestedIn.HasValue && _types.TryGetValue(type.NestedIn.Value, out parent);
                var simpleName = nested && name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                if (type.Kind == CirTypeKind.Enum)
                {
                    // Spell the target ECMA enum shape without host Reflection.Emit enum classification.
                    var builder = nested
                        ? parent.TB.DefineNestedType(simpleName, TypeAccess(visibility, true) | TypeAttributes.Sealed, Bcl("System.Enum"))
                        : _mod.DefineType(name, TypeAccess(visibility, false) | TypeAttributes.Sealed, Bcl("System.Enum"));
                    var declaration = new TypeInfo { TB = builder, Def = type, IsEnum = true };
                    var underlying = type.Underlying.HasValue ? MapType(type.Underlying.Value) : Bcl("System.Int32");
                    declaration.Fields["value__"] = builder.DefineField("value__", underlying,
                        FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName);
                    foreach (var literalEntry in type.Entries.OrEmpty())
                    {
                        var field = builder.DefineField(literalEntry.Name, builder,
                            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
                        field.SetConstant(literalEntry.PhysicalValue.HasValue && literalEntry.Underlying.HasValue
                            ? EnumConstantValue(literalEntry.Underlying.Value, literalEntry.PhysicalValue.Value) : literalEntry.Ordinal.Value);
                        StampMemberAttrs(field.SetCustomAttribute, literalEntry.Attrs);
                        declaration.Fields[literalEntry.Name] = field;
                    }
                    _types[name] = declaration;
                    continue;
                }

                var isInterface = type.Kind == CirTypeKind.Interface;
                var attributes = TypeAccess(visibility, nested) | (isInterface
                    ? TypeAttributes.Interface | TypeAttributes.Abstract : TypeAttributes.Class);
                if (!isInterface && type.Abstract.OrDefault()) attributes |= TypeAttributes.Abstract;
                if (!isInterface && type.Final.OrDefault()) attributes |= TypeAttributes.Sealed;
                if (type.SpecialName.OrDefault()) attributes |= TypeAttributes.SpecialName;
                if (type.BeforeFieldInit.OrDefault()) attributes |= TypeAttributes.BeforeFieldInit;
                // Only the type's own declarations contribute to the metadata-name suffix, not nested captures.
                var arity = type.TypeParams.OrEmpty().Length;
                var metadataName = arity > 0 ? simpleName + "`" + arity : simpleName;
                var typeBuilder = nested ? parent.TB.DefineNestedType(metadataName, attributes) : _mod.DefineType(metadataName, attributes);
                if (generated) StampCompilerGenerated(typeBuilder);
                var baseType = type.Base.OrDefault() as DotKt.Bir.TypeNode.Fqn;
                if (type.Base.HasValue && baseType == null) throw new FormatException("CIR base must be an fqn Type node");
                var info = new TypeInfo {
                    TB = typeBuilder, Def = type, IsInterface = isInterface, BaseFqn = baseType, BaseName = baseType?.Name,
                };
                var names = type.CapturedTypeParams.OrEmpty().Concat(type.TypeParams.OrEmpty()).Select(p => p.Name).ToArray();
                if (names.Length > 0) info.TypeParams.AddRange(typeBuilder.DefineGenericParameters(names));
                _types[name] = info;
            }
            if (!progressed)
                throw new InvalidOperationException("Nested CIR types have missing or cyclic owners: "
                    + string.Join(", ", pendingTypes.Select(type => type.Name + " -> " + type.NestedIn.OrDefault())));
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
            if (ti.IsGeneric && ti.TypeDeclaration is { } genericDeclaration)
            {
                var captured = genericDeclaration.CapturedTypeParams.OrEmpty();
                ApplyConstraints(captured, ti.TypeParams, false);
                ApplyConstraints(genericDeclaration.TypeParams.OrEmpty(), ti.TypeParams, ti.IsInterface || ti.IsDelegate,
                    captured.Length);
            }
            if (ti.BaseName != null)
            {
                // A constructed base carries its actual type arguments in the structured Fqn. Resolve local
                // TypeBuilders before the declared reference universe; the structured Fqn is the only owner input.
                var (bopen, bconstructed) = ParseOwnerT(ti.BaseFqn!);
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
                    // A generic base MUST arrive with its type args carried. If it reaches here arg-less with arity>0,
                    // the emitting layer dropped them; ilemit must not infer them from the subclass frame.
                    if (bArity > 0)
                        throw new InvalidOperationException(
                            $"generic base '{bopen}' (arity {bArity}) emitted without type args on '{ti.TB.Name}' — " +
                            "the emitting layer dropped them; ilemit does not infer base args");
                    ti.TB.SetParent(baseTb);
                }
                // A bare external .NET base is not in `_types`; resolve it from the declared reference universe.
                else ti.TB.SetParent(ti.ClrBase = ResolveType(bopen));
            }
            if (ti.TypeDeclaration is { } implementingDeclaration)
            {
                // One InterfaceImpl row per stated interface, in the stated order. The set is complete as it arrives:
                // every physical face a type owes — including the read-only sibling of a mutable collection face, which
                // bir2cir's ReadOnlyCollectionViewInterfaces states — is already in this array.
                foreach (var i in implementingDeclaration.Interfaces.OrEmpty())
                {
                    if (i is not DotKt.Bir.TypeNode.Fqn iFqn) continue;
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
            foreach (var field in ti.Def.DeclaredFields)
            {
                if (ti.IsInterface && !field.Static.OrDefault()) continue;
                DeclareField(ti, field, ti.IsFileClass);
            }
            foreach (var method in ti.Def.DeclaredMethods) DeclareMethod(ti, method, ti.IsFileClass);
            DeclareProperties(ti);
            if (!ti.IsFileClass)
            {
                foreach (var declaration in ti.TypeDeclaration.ClrEvents.OrEmpty())
                {
                    var @event = ti.TB.DefineEvent(declaration.Name, EventAttributes.None, MapType(declaration.DelegateType));
                    if (ti.Methods.TryGetValue("add_" + declaration.Name, out var add)) @event.SetAddOnMethod(add);
                    if (ti.Methods.TryGetValue("remove_" + declaration.Name, out var remove)) @event.SetRemoveOnMethod(remove);
                    if (ti.Methods.TryGetValue("raise_" + declaration.Name, out var raise)) @event.SetRaiseMethod(raise);
                }
                EnsureCtorsDefined(ti);
            }
        }

        // Emit only the explicit interface MethodImpl rows bir2cir resolved. Exact public name/signature members bind
        // implicitly under CLR rules; every mismatch, rename, selected DIM, event/accessor, suspend, or erasure seam
        // arrives as one descriptor on its exact MethodDef body.
        foreach (var (typeKey, ti) in _types)
            if (!ti.IsFileClass && !ti.IsInterface)
            {
                _curTypeParams = EffectiveTps(ti);
                var ifWork = new Queue<DotKt.Bir.TypeNode.Fqn>();
                var ifSeen = new HashSet<string>();
                foreach (var method in ti.Def.DeclaredMethods)
                        foreach (var directive in method.ClrInterfaceImpls.OrEmpty())
                            if (directive.Owner is DotKt.Bir.TypeNode.Fqn owner)
                                ifWork.Enqueue(owner);
                while (ifWork.Count > 0)
                {
                    var specFqn = ifWork.Dequeue();
                    var spec = SigCanon(specFqn);            // the canonical overload/dedup key for this interface spec
                    var specName = specFqn.Name;
                    if (!ifSeen.Add(spec)) continue;
                    // A canonicalized shared synthetic (`dotkt$CharSequence`) this app REFERENCES from the rt stdlib
                    // dll — NOT re-emitted here, so absent from `_types` — is an EXTERNAL interface: bind the class's
                    // overrides to it by reflection, so the interface slots are wired
                    // explicitly rather than relying on an implicit name/sig match a canonicalized supertype must not
                    // depend on. (Covers both a user `class S : CharSequence` and the synthesized `dotkt$StringCharSequence`.)
                    // Checked on the structured Fqn's bare name so constructed arguments cannot affect this identity test.
                    bool externalSynthIface = ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(specName)
                        && !_types.ContainsKey(specName) && ResolvesExternally(specName);
                    // A REFERENCED interface (not emitted in THIS assembly — a .NET-mapped Continuation<int>, or an
                    // external canonical synthetic): bind each interface method to the class method of the same .NET name
                    // by reflection. An EMITTED interface (in `_types`) follows the structured local-owner path below.
                    if (!_types.ContainsKey(specName) || externalSynthIface)
                    {
                        var itype = externalSynthIface ? ResolveType(specName) : MapType(specFqn);
                        var reanchor = ContainsTypeBuilder(itype);
                        var slotOwner = reanchor ? itype.GetGenericTypeDefinition() : itype;
                        var ifaceMs = NamedInterfaceSlots(ti, specFqn, slotOwner);
                        foreach (var im in ifaceMs)
                        {
                            var ips = ParametersOf(im).Select(p => reanchor
                                ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                                : p.ParameterType).ToArray();
                            var methodArity = im.GetGenericArguments().Length;
                            var interfaceRet = reanchor
                                ? SubstituteIfaceArgs(ReturnTypeOf(im), itype.GetGenericArguments())
                                : ReturnTypeOf(im);
                            if (FindExternalInterfaceBridge(ti, im, itype, im.Name, methodArity, ips, interfaceRet, specFqn)
                                is MethodBuilder directiveBridge)
                            {
                                WireMethodOverride(ti.TB, directiveBridge, reanchor ? AnchorOn(itype, im) : im);
                                continue;
                            }
                        }
                        continue;
                    }
                    var (open, constructed) = ParseOwnerT(specFqn);
                    if (!_types.TryGetValue(open, out var iface)) continue;
                    // The interface's instantiation args (the concrete args at this implementer): `Comparable<Self>` ->
                    // [Self]. An interface method's declared type names the INTERFACE's OWN params as `Tv{type,i}`;
                    // SubstTv re-anchors each to specArgs[i] so it matches the class's own (concrete) member signature.
                    var specArgs = specFqn.Args;
                    // Iterate the interface's method DEFS (not the name-keyed iface.Methods) so OVERLOADED interface
                    // methods (e.g. MutableMap.remove(K):V vs the JVM remove(K,V):Boolean) each resolve to their own
                    // builder by signature, and to the matching body overload by TYPE-ARG-SUBSTITUTED signature. A miss
                    // when the name is AMBIGUOUS (multiple body overloads) is skipped — wiring the wrong one is the bug.
                        foreach (var imDef in iface.Def.DeclaredMethods)
                        {
                            // A private/final explicit MethodImpl BODY declared on an interface is not another
                            // declaration slot. In particular, a derived class must never name it as the declaration
                            // half of its own MethodImpl row: ECMA-335 rejects a final declaration there. bir2cir marks
                            // this physical role explicitly, so excluding it is mechanical CIR consumption rather than
                            // reconstructing the bridge's purpose from visibility, finality, name, or body shape.
                            if (imDef.ClrInterfaceSlotBridge.OrDefault())
                                continue;
                            var physicalInterfaceName = PhysicalMethodName(imDef);
                            var ifaceBuilder = iface.MethodsBySig.TryGetValue(
                                                   DefinitionSigKey(physicalInterfaceName, imDef), out var ib) ? ib : null;
                            if (ifaceBuilder == null) continue;
                            // The interface method's params with each Tv{type,i} re-anchored to specArgs[i], rendered to
                            // the sig-token spelling — matched against the class's own MethodsBySig (a nested value-class
                            // arg like Continuation.resumeWith(Result<T>) substitutes correctly, not just a bare gp).
                            var subSig = SigKey(physicalInterfaceName, DeclaredMethodArity(imDef),
                                imDef.Params.Select(p => SubstTv(p.Type, specArgs)));
                            var ifaceMethod = constructed != null ? AnchorMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
                            // A bir2cir-resolved exact MethodImpl bridge. The decision and exact slot signature are
                            // already CIR facts; this is mechanical consumption only. In particular, do not also wire
                            // the narrow Kotlin declaration, whose covariant return is not a byte-exact CLR MethodImpl.
                            var subRet = SubstTv(imDef.Ret, specArgs);
                            var explicitBridge = FindExplicitInterfaceBridge(
                                ti, specFqn, physicalInterfaceName, subSig, subRet, imDef);
                            if (explicitBridge != null)
                            {
                                WireMethodOverride(ti.TB, explicitBridge, ifaceMethod);
                                continue;
                            }
                            // As on the referenced axis, an exact declaration binds implicitly. A covariant, renamed,
                            // erased, default, event, suspend, or return-dropping obligation must arrive as a resolved
                            // descriptor and is consumed by the branch above.
                        }
                }
            }

        // Consume bir2cir-authored MethodImpl descriptors on interfaces. The descriptor already identifies the private
        // final bridge body and exact external declaration slot; reflection is used only to obtain that declaration's
        // MethodInfo. Selection of a Kotlin default implementation and synthesis of its forwarding body are complete
        // before CIR reaches ilemit.
        foreach (var (_, ti) in _types)
        {
            if (!ti.IsInterface) continue;
            _curTypeParams = EffectiveTps(ti);
            // De-dup across a diamond (`I : A, B` with `A, B : C`): one methodimpl per (baseOwner :: subSig).
            var dimImplSeen = new HashSet<(string Owner, MethodBuilder Declaration)>();
            var externalBaseSpecs = new List<DotKt.Bir.TypeNode.Fqn>();
            // As with class implementations above, an explicit interface MethodImpl is driven by its resolved CIR
            // owner even when a representation carrier replaced the source-level direct edge. The existing exact
            // descriptor lookup remains the only operation that can select its body.
            foreach (var method in ti.Def.DeclaredMethods)
                    foreach (var directive in method.ClrInterfaceImpls.OrEmpty())
                        if (directive.Owner is DotKt.Bir.TypeNode.Fqn owner)
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
                var reanchor = ContainsTypeBuilder(itype);
                var slotOwner = reanchor ? itype.GetGenericTypeDefinition() : itype;
                var ifaceMs = NamedInterfaceSlots(ti, ibF, slotOwner);
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
                    if (FindExternalInterfaceBridge(ti, im, itype, im.Name, methodArity, ips, interfaceRet, ibF)
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

        // Every bir2cir-authored interface MethodImpl is an obligation, not a hint. A descriptor that no exact
        // declaration loop consumed is malformed CIR/reference state; never fall back to implicit name binding or
        // silently leave the slot for a later type-load failure.
        ValidateInterfaceImplsConsumed();

        // A bir2cir-resolved MethodImpl against a BASE-CLASS slot (`clrBaseImpls`, #86 D3). The interface twin of this
        // instruction (`clrInterfaceImpls`) is consumed in the interface wiring above; a base-CLASS slot has no such
        // loop because a class override normally binds by name+signature alone. An erasure bridge does NOT: it carries
        // a synthesized name so it cannot shadow the declaration it forwards to, so the slot it fills must be named.
        // Mechanical consumption only — the decision and the exact slot signature are already CIR facts.
        foreach (var (_, ti) in _types)
        {
            if (ti.IsInterface) continue;
            _curTypeParams = EffectiveTps(ti);
            foreach (var m in ti.Def.DeclaredMethods)
            {
                if (!m.ClrBaseImpls.HasValue) continue;
                var bridgeName = PhysicalMethodName(m);
                if (!ti.MethodsBySig.TryGetValue(DefinitionSigKey(bridgeName, m), out var bridge))
                    throw new InvalidOperationException(
                        $"ilemit: resolved base MethodImpl body {ti.TB.Name}.{SigKey(bridgeName, m)} is absent");
                // A GENERIC slot's descriptor states its parameter vector in the BRIDGE's own vocabulary, so a
                // method-scope `tv` in it names one of the bridge's own type parameters. Install that exact method
                // frame while consuming the descriptor; resolving it against the enclosing type would cross ownership.
                _curMethodParams = _methodTypeParams.TryGetValue(bridge, out var bridgeTps) ? bridgeTps : null;
                foreach (var impl in m.ClrBaseImpls.Value)
                {
                    var ownerFqn = impl.Owner as DotKt.Bir.TypeNode.Fqn
                        ?? throw new InvalidOperationException("Base MethodImpl owner must be an FQN");
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
                        var externalSlot = PrimaryFromRef(impl.MemberRef.OrDefault()) as MethodInfo;
                        if (externalSlot is null)
                            throw new InvalidOperationException(
                                $"ilemit: {ti.TB.Name}.{bridgeName}: clrBaseImpls names "
                                + $"'{impl.Member}' on the referenced base '{open}', which does not resolve "
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
                    var member = impl.Member;
                    var describedArity = DescribedArity(impl);
                    var slotSig = SigKey(member, describedArity, impl.Params);
                    var describedRet = impl.Ret;
                    MethodBuilder slot = null;
                        foreach (var bm in baseTi.Def.DeclaredMethods)
                        {
                            var physicalDeclarationName = PhysicalMethodName(bm);
                            if (physicalDeclarationName != member || DeclaredMethodArity(bm) != describedArity
                                || !DescriptorTypeParamsMatch(impl, bm, ownerFqn.Args)) continue;
                            var declared = DefinitionSigKey(physicalDeclarationName, bm);
                            var substituted = SigKey(member, DeclaredMethodArity(bm),
                                bm.Params.Select(p => SubstTv(p.Type, ownerFqn.Args)));
                            if (substituted != slotSig) continue;
                            if (!SubstTv(bm.Ret, ownerFqn.Args).Equals(describedRet))
                                continue;
                            if (baseTi.MethodsBySig.TryGetValue(declared, out var found)) { slot = found; break; }
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
        // clean `ilemit: <Type>.<method>: <message>` line; successful body emission is unaffected by the guard.
        foreach (var ti in _types.Values)
            if (!ti.IsDelegate)
                for (int ci = 0; ci < ti.Ctors.Count; ci++) { T($"pass4 ctor body: {ti.TB?.Name}#{ci}"); var cb = ti.Ctors[ci]; var cd = ti.CtorDefs[ci]; GuardBody(() => EmitCtorBody(ti, cb, cd)); }
        foreach (var ti in _types.Values)
            if (!ti.IsEnum && !ti.IsDelegate)
                foreach (var m in ti.Def.DeclaredMethods)
                {
                    // A CIR extern is deliberately bodyless (currently caller-side UnsafeAccessor). Its signature and
                    // custom attributes were defined above; the CLR supplies the implementation.
                    if (m.Extern.OrDefault()) continue;
                    // Interfaces: the CIR `abstract` fact distinguishes an abstract slot from a default method.
                    // A valid void default implementation may have an empty statement list (`set(value) {}`), so
                    // body presence is not a semantic discriminator. EmitMethodBody will append its required `ret`.
                    if (ti.IsInterface
                        && !m.Static && m.Abstract.Value) continue;
                    T($"pass4 method body: {ti.TB?.Name}.{m.Name}"); GuardBody(() => EmitMethodBody(ti, m));
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
            if (ti.TB != null)
                StampMemberAttrs(ti.TB.SetCustomAttribute, ti.Def.Attrs);
                foreach (var m in ti.Def.DeclaredMethods)
                {
                    var mattrs = m.Attrs.OrEmpty();
                    var rattrs = m.RetAttrs.OrEmpty();
                    bool hasA = mattrs.Length > 0;
                    bool hasR = rattrs.Length > 0;
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
                        StampMemberAttrs(mb.SetCustomAttribute, m.Attrs);
                    // Return-position attrs ride the return parameter (position 0), defined once.
                    if (hasR)
                    {
                        var retPb = mb.DefineParameter(0, ParameterAttributes.None, null);
                        StampMemberAttrs(retPb.SetCustomAttribute, m.RetAttrs);
                    }
                }
        }

        // Pass 4b: static-field initializers -> a type initializer (.cctor). A CLR interface may legally have one.
        foreach (var ti in _types.Values)
        {
            // Absent/null initializers both leave the CLR default; only an expression needs a .cctor store.
            var inits = ti.Def.DeclaredFields.Where(f => f.Init.HasValue && f.Static.OrDefault()).ToList();
            if (inits.Count == 0) continue;
            var initializer = ti.TB.DefineTypeInitializer();
            var il = initializer.GetILGenerator();
            if (EmitReferenceStub(il)) continue;
            // A synthesized .cctor is a type method with no method-generic scope. Carry the declaring type's generic
            // parameters and clear whatever method scope pass 4 last visited; otherwise a generic nested singleton
            // initializer can encode `new ...<!!0>` and produce a BadImageFormatException at type initialization.
            _curTypeParams = EffectiveTps(ti);
            _curMethodParams = null;
            _sourceMethod = initializer;
            _il = il;
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = Bcl("System.Void");
            // A field initializer can contain CFG control flow (a `while`/`when` lowered to label/goto), so its labels
            // must be pre-defined just like a method body — otherwise MarkLabel/Br throws "key not in _cfgLabels".
            // Coerce the init value to the field's declared type (box a value-type/enum RHS stored into an
            // `object`/wider reference field) — the SAME shared store coercion the method-body sites use; without
            // it, `val X: Any = SomeEnum.ENTRY` stored the raw ordinal (int) into an object field as a null ref.
            // #84: the type initializer emits USER initializer expressions (which may carry full CFG), so guard each
            // field's emit with a `.cctor(<field>)` breadcrumb — a throw here names the failing field like a body throw.
            _ctxType = ti.TB?.Name; _ctxNode = null; _ctxPos = PosOf(ti.Def.Pos.OrDefault());
            foreach (var f in inits)
            {
                var fname = f.Name;
                _ctxMethod = ".cctor(" + fname + ")";
                var fb = ti.Fields[fname];
                GuardBody(() => { PrescanCfgLabels(f.Init.Value.Element); EmitStoreCoerced(f.Init.Value.Element, fb.FieldType); MaybeVolatile(fb); EmitField(_il, OpCodes.Stsfld, fb); });
            }
            _il.Emit(OpCodes.Ret);
        }
        _curTypeParams = null;

        // Pass 5: synthesize entry point on the file class that has `main`.
        MethodBuilder entry = null;
        foreach (var ti in _types.Values)
            if (ti.Def is CirFileDeclaration { HasMain: true } && ti.Methods.ContainsKey("main"))
            {
                entry = ti.TB.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, Bcl("System.Void"), new[] { Bcl("System.String").MakeArrayType() });
                var il = entry.GetILGenerator();
                if (EmitReferenceStub(il)) continue;
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

        // Assembly attributes are literal CIR descriptions. PersistedAssemblyBuilder captures a local builder's
        // still-unassigned MethodDef token when SetCustomAttribute is called, producing token 0x06000000. Defer
        // those rows until GenerateMetadata assigns the local tokens; external constructors use the ordinary API.
        var localAssemblyAttrs = new List<EncodedAttribute>();
        foreach (var file in files)
                foreach (var attribute in file.AssemblyAttrs.OrEmpty())
                {
                    var encoded = BuildAttribute(attribute);
                    if (encoded == null) continue;
                    if (encoded.Constructor is ConstructorBuilder) localAssemblyAttrs.Add(encoded);
                    else ab.SetCustomAttribute(encoded.Constructor, encoded.Blob);
                }
        T("save: writing PE");
        Save(ab, entry, localAssemblyAttrs);
        T("save: done");
    }

    static TypeAttributes TypeAccess(string visibility, bool nested) => nested ? visibility switch
    {
        "private" => TypeAttributes.NestedPrivate,
        "internal" => TypeAttributes.NestedAssembly,
        "protected" => TypeAttributes.NestedFamily,
        "protectedInternal" => TypeAttributes.NestedFamORAssem,
        _ => TypeAttributes.NestedPublic,
    } : visibility == "public" ? TypeAttributes.Public : TypeAttributes.NotPublic;

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
            if (ti.TypeDeclaration is { } type)
                foreach (var i in type.Interfaces.OrEmpty())
                {
                    if (i is DotKt.Bir.TypeNode.Fqn iF && _types.TryGetValue(iF.Name, out var inf)) Visit(inf);
                }
            // A nested type must be CreateType()'d BEFORE its enclosing type (Reflection.Emit bakes children into the
            // parent). `done` already holds `ti` (added at entry), so a child whose base IS `ti` won't recurse forever.
            var myName = ti.TypeDeclaration?.Name;
            if (myName != null)
                foreach (var child in _types.Values)
                    if (child.TypeDeclaration?.NestedIn.OrDefault() == myName)
                        Visit(child);
            result.Add(ti);
        }
        foreach (var ti in _types.Values) Visit(ti);
        return result;
    }

    // Kotlin visibility -> CLR method/ctor access flag (default public).
    static MethodAttributes AccessOf(OptionalField<string> visibility) =>
        visibility.OrDefault("public") switch
        {
            "private" => MethodAttributes.Private,
            "internal" => MethodAttributes.Assembly,
            "protected" => MethodAttributes.Family,
            "protectedInternal" => MethodAttributes.FamORAssem,
            _ => MethodAttributes.Public,
        };

    void DeclareField(TypeInfo owner, CirField field, bool fileStatic)
    {
        var slot = MapSignatureSlot(field.Type);
        var attributes = field.Vis.OrDefault("public") switch
        {
            "private" => FieldAttributes.Private,
            "internal" => FieldAttributes.Assembly,
            "protected" => FieldAttributes.Family,
            "protectedInternal" => FieldAttributes.FamORAssem,
            _ => FieldAttributes.Public,
        };
        if (fileStatic || field.Static.OrDefault()) attributes |= FieldAttributes.Static;
        if (field.InitOnly.OrDefault()) attributes |= FieldAttributes.InitOnly;
        if (field.Constant.IsPresent) attributes |= FieldAttributes.Static | FieldAttributes.Literal;
        var builder = field.Volatile.OrDefault()
            ? DefineVolatileField(owner.TB, field.Name, slot.Type, attributes, slot.Required, slot.Optional)
            : owner.TB.DefineField(field.Name, slot.Type, slot.Required, slot.Optional, attributes);
        StampMemberAttrs(builder.SetCustomAttribute, field.Attrs);
        if (field.Constant.IsPresent) builder.SetConstant(ConstArgValue(field.Constant.Value));
        owner.Fields[field.Name] = builder;
    }

    // Real CLR properties over already-declared accessor methods. Both ordinary types and file facades use the same
    // CIR declaration record; a top-level delegated property especially needs this explicit link because its physical
    // storage is `<name>$delegate`, not a field from which dll2klib could reconstruct the source property name.
    void DeclareProperties(TypeInfo ti)
    {
        foreach (var p in ti.Def.DeclaredProperties)
        {
            MethodBuilder gm = null;
            MethodBuilder sm = null;
            if (p.Get.HasValue)
                gm = ResolvePropertyAccessor(ti, p.Get.Value, p.GetSig, p.GetRet, p.GetMethodArity.OrDefault());
            if (p.Set.HasValue)
                sm = ResolvePropertyAccessor(ti, p.Set.Value, p.SetSig, p.SetRet, p.SetMethodArity.OrDefault());
            // ECMA-335 requires the Property signature to describe the accessor's index parameters. Most Kotlin
            // properties have none, but a context/extension property's getter physically receives those arguments.
            // A setter's final parameter is the value, not an index parameter.
            var propertySlots = gm != null
                ? p.GetSig.Value.Select(MapSignatureSlot).ToArray()
                : sm != null ? p.SetSig.Value.SkipLast(1).Select(MapSignatureSlot).ToArray()
                : Array.Empty<DeclaredSignatureSlot>();
            var propertyType = MapSignatureSlot(p.Type);
            // The Property signature must carry the same instance/static convention as its linked accessor.
            // Omitting HASTHIS makes an instance override's signature differ from its inherited Property row.
            var convention = (gm ?? sm).IsStatic
                ? CallingConventions.Standard
                : CallingConventions.Standard | CallingConventions.HasThis;
            var pb = ti.TB.DefineProperty(
                p.Name,
                PropertyAttributes.None, convention,
                propertyType.Type, propertyType.Required, propertyType.Optional,
                propertySlots.Select(slot => slot.Type).ToArray(),
                propertySlots.Select(slot => slot.Required).ToArray(),
                propertySlots.Select(slot => slot.Optional).ToArray());
            if (gm != null) pb.SetGetMethod(gm);
            if (sm != null) pb.SetSetMethod(sm);
            StampMemberAttrs(pb.SetCustomAttribute, p.Attrs);   // [KotlinSuspendFunctionType]/… (bir2cir-generated)
        }
    }

    MethodBuilder ResolvePropertyAccessor(TypeInfo ti, string name, OptionalField<DotKt.Bir.TypeNode[]> signature,
        OptionalField<DotKt.Bir.TypeNode> returnType, int methodArity)
    {
        if (!signature.HasValue)
            throw new InvalidOperationException(
                $"ilemit: Property accessor {ti.TB.FullName}.{name} has no resolved signature descriptor");
        var parameterTypes = signature.Value;
        if (ti.MethodsBySig.TryGetValue(DefinitionSigKey(name, methodArity, parameterTypes, returnType.Value), out var exact))
            return exact;
        throw new InvalidOperationException(
            $"ilemit: resolved Property accessor descriptor {ti.TB.FullName}.{DefinitionSigKey(name, methodArity, parameterTypes)} does not link exactly");
    }

    // Method-level generic params, keyed by MethodInfo, so call sites can MakeGenericMethod.
    readonly Dictionary<MethodBuilder, GenericTypeParameterBuilder[]> _methodTypeParams = new();

    // Every generic parameter belonging to an EMITTED METHOD, by identity. `GenericTypeParameterBuilder` reports
    // neither `DeclaringMethod` nor `DeclaringType` — measured, and identically so for a TYPE's parameter — so
    // nothing on the object itself says which scope it is in, and `!!i` vs `!i` is a signature difference. Every
    // method that declares generic parameters records them here, through `RecordMethodTps`.
    readonly HashSet<Type> _emittedMethodTps = new(ReferenceEqualityComparer.Instance);

    void RecordMethodTps(MethodBuilder mb, GenericTypeParameterBuilder[] parameters)
    {
        _methodTypeParams[mb] = parameters;
        foreach (var g in parameters) _emittedMethodTps.Add(g);
    }

    static void AddMethodAlias(TypeInfo ti, MethodSigKey key, MethodBuilder method)
    {
        if (ti.AmbiguousMethodAliases.Contains(key)) return;
        if (ti.MethodsBySig.TryGetValue(key, out var previous) && previous != method)
        {
            ti.MethodsBySig.Remove(key);
            ti.AmbiguousMethodAliases.Add(key);
            return;
        }
        ti.MethodsBySig[key] = method;
    }

    void DeclareMethod(TypeInfo ti, CirMethod m, bool isStatic)
    {
        var logicalName = m.Name;
        var name = PhysicalMethodName(m);
        // bir2cir owns physical method allocation (#395). A duplicate CIR signature is therefore malformed input;
        // ilemit must not invent an order-dependent `$dupN` name that declarations and use sites cannot share.
        var dupKey = DefinitionSigKey(name, m);
        if (ti.MethodsBySig.ContainsKey(dupKey))
            throw new InvalidOperationException(
                $"ilemit: duplicate physical method signature {ti.TB?.FullName}.{name}; bir2cir must allocate a unique MethodDef name");
        var interfaceAbstract = false;
        var interfaceSlot = ti.IsInterface && !(isStatic || m.Static);
        if (interfaceSlot)
        {
            if (!m.Abstract.HasValue)
                throw new InvalidOperationException(
                    $"ilemit: interface method '{ti.TB?.FullName}.{name}' has no explicit CIR abstract modality fact");
            interfaceAbstract = m.Abstract.Value;
        }
        var clrInterfaceSlotBridge = ti.IsInterface && m.ClrInterfaceSlotBridge.OrDefault();
        // Source interface members are public. A bir2cir-authored explicit MethodImpl body retains its stated private
        // visibility and finality; this is direct consumption of the CIR slot instruction.
        var attrs = ti.IsInterface && !clrInterfaceSlotBridge ? MethodAttributes.Public : AccessOf(m.Vis);
        // A method's own `static` flag (companion methods are static members of a user class).
        isStatic = isStatic || m.Static;
        var objOverride = m.ObjectOverride.OrDefault();
        // Overriding a .NET base virtual (e.g. `override val Message`) reuses the base slot, like an object-method.
        // `requiresClrOverride` is the one-to-one CIR instruction to emit a MethodImpl; `clrOverrideRef` is its
        // already-selected operand. Keeping the trigger separate from the operand makes either missing half a
        // malformed document instead of silently changing virtual dispatch.
        var hasClrOverride = m.RequiresClrOverride.OrDefault();
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
        if (m.Extern.OrDefault())
            attrs |= MethodAttributes.HideBySig;
        // `ToString`/`Equals`/`GetHashCode` and .NET base overrides reuse the base slot (Virtual, no NewSlot).
        else if (objOverride || hasClrOverride) attrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
        else if (m.Override) attrs |= MethodAttributes.Virtual;
        else if (m.Virtual) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        // An `abstract fun` (no body) -> a CLR abstract method: Virtual|Abstract, no IL body (subclasses override).
        if (m.Abstract.OrDefault()) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual;
        if (m.ClrFinal.OrDefault()) attrs |= MethodAttributes.Final;
        // A synthesized event accessor (add_/remove_/raise_<E>, §4.2) is `specialname` (the ECMA-335 event-accessor
        // convention) so the emitted `.event` is a clean reflectable member. bir2cir stamps the flag on the rewritten accessor.
        if (m.SpecialName.OrDefault()) attrs |= MethodAttributes.SpecialName;

        // NOTE: a coroutine arrives here as ordinary CIR and nothing on this path knows it is one. The cold-core
        // lowering (bir2cir) has already made the public `Task<T>` bridge its OWN method carrying `suspendBridge:true`
        // (from which bir2cir RoundtripMetadata generates the `[KotlinFunction(Suspend)]` attr, #71 S2), and the cold
        // entry / state-machine class are plain methods/types. The Kotlin `suspend` modifier itself does not reach CIR.

        MethodBuilder mb;
        Type[] ps;
        var genTps = m.TypeParams.OrEmpty();
        if (genTps.Length > 0)
        {
            // Generic method `fun <T> id(x: T): T`: the signature references the method's own type params, so
            // they must be defined before SetParameters/SetReturnType (staged form, not the one-shot DefineMethod).
            var genNames = genTps.Select(p => p.Name).ToArray();
            mb = ti.TB.DefineMethod(name, attrs);
            var gps = mb.DefineGenericParameters(genNames);
            RecordMethodTps(mb, gps);
            _curMethodParams = gps;
            ApplyConstraints(genTps, gps, false);   // `<T : Comparable<T>>` on the method (variance N/A on methods)
        }
        else
        {
            mb = ti.TB.DefineMethod(name, attrs);
        }
        var parameterSlots = m.Params.Select(p => MapSignatureSlot(p.Type)).ToArray();
        var returnSlot = MapSignatureSlot(m.Ret);
        ps = parameterSlots.Select(slot => slot.Type).ToArray();
        mb.SetSignature(returnSlot.Type, returnSlot.Required, returnSlot.Optional, ps,
            parameterSlots.Select(slot => slot.Required).ToArray(),
            parameterSlots.Select(slot => slot.Optional).ToArray());
        if (returnSlot.HasModifiers || parameterSlots.Any(slot => slot.HasModifiers))
            _declaredMethodSignatures[mb] = new(returnSlot, parameterSlots);
        if (genTps.Length > 0) _curMethodParams = null;
        // A kotc-authored lifted method (`newDelegate` target) carries the same structural generated fact as
        // synthesized types. Stamp the standard marker here; dll2klib uses it to keep implementation-only helpers
        // out of the re-imported Kotlin surface. This is a direct CIR flag -> metadata mapping, not name inference.
        if (m.Generated.OrDefault())
            StampCompilerGenerated(mb);
        ti.Methods[name] = mb;
        ti.MethodsBySig[DefinitionSigKey(name, m)] = mb;
        ti.MethodDeclarationReturns[mb] = DefinitionSigKey(name, m).Return;
        // Keep a partial call-side alias only while it is unique. Return-distinct definitions must never
        // acquire an order-dependent first-wins binding.
        AddMethodAlias(ti, DefinitionSigKey(name, m) with { Return = null }, mb);
        AddMethodAlias(ti, SigKey(name, m), mb);
        // Calls whose frontend declaration identity is not yet transported through an erased overload set remain #395.
        // Preserve the first declaration's logical lookup without changing the emitted MethodDef name; exact bir2cir-
        // authored calls and all MethodImpl descriptors use the physical key above.
        ti.Methods.TryAdd(logicalName, mb);
        ti.MethodsBySig.TryAdd(DefinitionSigKey(logicalName, m), mb);
        AddMethodAlias(ti, DefinitionSigKey(logicalName, m) with { Return = null }, mb);
        AddMethodAlias(ti, SigKey(logicalName, m), mb);
        ti.MethodNameCounts[name] = ti.MethodNameCounts.TryGetValue(name, out var nameCount) ? nameCount + 1 : 1;
        _mparams[mb] = ps;   // MethodBuilder.GetParameters() throws pre-bake; record param types for call-site boxing
        DefineParamNames(mb, m);
        if (m.Pinvoke.HasValue) ApplyPInvokeDescriptor(mb, m.Pinvoke.Value);
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
            WireMethodOverride(ti.TB, mb, RequiredRef<MethodInfo>(m.ClrOverrideRef.OrDefault(), $"the override {name}"));
        }
        // Kotlin's `@kotlin.internal.InlineOnly` says "this fn is meant to be inlined, not called as a method". The direct
        // CLR translation is a [MethodImpl(AggressiveInlining)] hint on the emitted method. kotc reads the annotation and
        // emits `mods.inlineOnly`; ilemit stamps the flag. Pure metadata, no behavior change; the JIT ignores the hint for
        // a too-large method. Skip abstract slots (no body to inline). ilemit adds no Kotlin knowledge — it stamps a flag.
        if (m.Mods.OrDefault()?.InlineOnly.OrDefault() == true && (attrs & MethodAttributes.Abstract) == 0)
            mb.SetImplementationFlags(mb.GetMethodImplementationFlags() | MethodImplAttributes.AggressiveInlining);
    }

    // Define a type's constructors from its CIR (idempotent). Normally runs in pass 3, but BuildAttribute pulls it EARLY when
    // stamping a param/method attribute whose attribute type is emitted in THIS assembly (e.g. the generated
    // `KotlinDefaultAttribute(int,string,byte[])` on a defaulted parameter). Pass 3 declares members type-by-type,
    // so an attribute on an earlier type may need a constructor declared by a later type. Defining constructors
    // on demand makes that exact constructor available at its first use. Interfaces/enums/file classes have no CIR ctors.
    void EnsureCtorsDefined(TypeInfo ti)
    {
        if (ti.CtorsDefined) return;
        ti.CtorsDefined = true;
        if (ti.IsInterface || ti.IsEnum || ti.IsFileClass) return;
        var saved = _curTypeParams;
        _curTypeParams = EffectiveTps(ti);   // so a `gp:T` ctor param resolves when pulled early out of pass-3 order
        foreach (var c in ti.Def.DeclaredConstructors)
        {
            var slots = c.Params.Select(p => MapSignatureSlot(p.Type)).ToArray();
            var ps = slots.Select(slot => slot.Type).ToArray();
            var cb = ti.TB.DefineConstructor(AccessOf(c.Vis), CallingConventions.Standard, ps,
                slots.Select(slot => slot.Required).ToArray(), slots.Select(slot => slot.Optional).ToArray());
            if (slots.Any(slot => slot.HasModifiers)) _declaredConstructorSignatures[cb] = slots;
            DefineParamNames(cb, c);   // ctor param NAMES + [Optional]/DefaultParameterValue (named-arg ctor calls)
            StampMemberAttrs(cb.SetCustomAttribute, c.Attrs); // declaration carriers/annotations belong to the ctor row
            ti.Ctors.Add(cb);
            ti.CtorDefs.Add(c);
        }
        if (ti.Ctors.Count > 0) { ti.Ctor = ti.Ctors[0]; ti.CtorDef = ti.CtorDefs[0]; }
        _curTypeParams = saved;
    }

    // Apply generic constraints (`<T : Comparable<T>>` -> `T : IComparable<T>`) to already-defined params. The
    // constraint context map (type or method params) must be current so a `gp:T` inside a bound resolves.
    void ApplyConstraints(CirTypeParameter[] tps, IReadOnlyList<GenericTypeParameterBuilder> parameters, bool isInterface,
        int offset = 0)
    {
        foreach (var x in tps)
        {
            var gp = parameters[offset++];
            var parameterAttributes = GenericParameterAttributes.None;
            // bir2cir has resolved the physical flags over signatures and inheritance. Emit that CIR fact directly.
            if (isInterface && x.Variance.HasValue)
            {
                var vs = x.Variance.Value;
                var attr = vs == "out" ? GenericParameterAttributes.Covariant
                         : vs == "in" ? GenericParameterAttributes.Contravariant
                         : GenericParameterAttributes.None;
                parameterAttributes |= attr;
            }
            if (x.SpecialConstraints.HasValue)
            {
                foreach (var special in x.SpecialConstraints.Value)
                    parameterAttributes |= special switch
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
            if (x.Constraints.HasValue)
            {
                var types = x.Constraints.Value.Select(MapType).ToList();
                var baseT = types.FirstOrDefault(t => !t.IsGenericParameter && !IsInterfaceType(t));
                // Reflection.Emit stores additional constraint rows through this API, including type parameters.
                // A parameter constraint is not an interface, but must not disappear behind a nominal base bound.
                var additional = types.Where(t => t != baseT).ToArray();
                if (baseT != null) gp.SetBaseTypeConstraint(baseT);
                if (additional.Length > 0) gp.SetInterfaceConstraints(additional);
            }
        }
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

    void Save(PersistedAssemblyBuilder ab, MethodBuilder entry, IReadOnlyList<EncodedAttribute> localAssemblyAttrs)
    {
        // How much of what these documents carry this build put through the parity check. A measurement, not
        // the gate — the enforcement is at the call sites, which compare before every legacy resolution.
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        foreach (var attribute in localAssemblyAttrs)
            metadata.AddCustomAttribute(MetadataTokens.EntityHandle(0x20000001),
                MetadataTokens.EntityHandle(attribute.Constructor.MetadataToken), metadata.GetOrAddBlob(attribute.Blob));
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var entryPoint = entry != null && _referenceAssemblyPath == null
            ? MetadataTokens.MethodDefinitionHandle(entry.MetadataToken) : default;
        var symbolsId = _portablePdbPath == null ? default
            : _deterministic ? DeterministicSymbolsSeed()
            : BlobContentId.GetTimeBasedProvider()(Array.Empty<Blob>());
        var debugDirectory = _portablePdbPath == null ? null : new DebugDirectoryBuilder();
        debugDirectory?.AddCodeViewEntry(Path.GetFileName(_portablePdbPath), symbolsId, PortablePdbFormatVersion);
        if (_deterministic) debugDirectory?.AddReproducibleEntry();
        var peBuilder = new ManagedPEBuilder(
            peHeader, new MetadataRootBuilder(metadata), ilStream,
            mappedFieldData: fieldData,
            nativeResources: _nativeResources,
            debugDirectoryBuilder: debugDirectory,
            entryPoint: entryPoint,
            deterministicIdProvider: _deterministic ? DeterministicPeId : null);
        var blob = new BlobBuilder();
        var imageId = peBuilder.Serialize(blob);
        var imageBytes = blob.ToArray();
        if (_deterministic)
        {
            imageId.Guid.TryWriteBytes(imageBytes.AsSpan(ModuleVersionIdOffset(imageBytes), 16));
            if (_portablePdbPath != null)
            {
                symbolsId = imageId;
                SetSymbolsId(imageBytes, symbolsId);
            }
        }
        if (_portablePdbPath != null)
            WritePortablePdb(imageBytes, metadata.GetRowCounts(), entryPoint, symbolsId);
        // #52 — write ATOMICALLY (temp + rename): FileMode.Create truncates-then-writes in place, so a concurrent
        // reader (dll2klib/bir2cir loading this same dll) can observe a partial image and fail with a
        // spurious "Format of the executable is invalid" / BadImageFormatException. A same-directory rename is atomic,
        // so a reader always sees either the whole old file or the whole new one — never a torn write.
        var dllPath = _referenceAssemblyPath ?? Path.Combine(_outDir, _asmName + ".dll");
        AtomicFile.Write(dllPath, fs => fs.Write(imageBytes));
        Console.WriteLine(_referenceAssemblyPath == null ? $"emitted {_asmName}.dll" : $"emitted reference assembly {dllPath}");
    }

}
