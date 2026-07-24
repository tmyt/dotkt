// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// EmitExpr: the BIR expression -> CIL evaluator (returns the .NET Type left on the stack).
sealed partial class Emitter
{
    // Null-tolerant read of the `virtual` flag on a callInstance / newBoundDelegate node. Defaults to FALSE (a plain
    // `call`) when the key is absent: a facadegen-reinjected .NET-interop callInstance (e.g. a DotKt library consumed
    // AS KOTLIN) is emitted by kotc's clrType path WITHOUT `virtual` when bir2cir leaves it un-reshaped, so an
    // unconditional GetProperty("virtual") would throw KeyNotFoundException (#139). A missing flag => non-virtual.
    static bool IsVirtual(JsonElement e) => e.TryGetProperty("virtual", out var v) && v.GetBoolean();

    // ---- expressions: push one value, return its CLR type ----
    // @ClrIntrinsicAsDynamic dispatch: `recv.GetType().GetMethod(name).Invoke(recv, [args...])`, emitted inline (no
    // helper assembly). Resolves the bound member at RUNTIME, so ilemit needs NO static resolution -- this sidesteps the
    // BCL-`clrg:`-interface skip in FindMethod (e.g. AbstractMutableList.SubList calling get_Item on the IList slot) and
    // the IReadOnlyList/IList dual get_Item. Slower (reflection + boxing) but correct; used only where static fails.
    // True if the emitted type implements a BCL `clr:`/`clrg:` interface -- i.e. a substituted Kotlin collection whose
    // Kotlin members (get_Item/iterator/addAll) may live on the BCL interface that static FindMethod skips. Gates the
    // dynamic-dispatch fallback to these, so a genuine missing-method on a non-collection type still throws.
    bool OwnerHasClrInterface(string ownerType)
    {
        var (open, _) = ParseOwner(ownerType);
        if (!_types.TryGetValue(open, out var ti) || ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var ifs)) return false;
        // A CLR/BCL interface is a facadegen-injected .NET type: NOT emitted in THIS assembly AND not a Kotlin `kotlin.*`
        // interface — the structured successor of the retired `clr:`/`clrg:` interface-token check (#48), kept narrow so
        // a referenced *Kotlin* interface does not spuriously widen the dynamic-dispatch fallback.
        foreach (var i in ifs.EnumerateArray())
            if (DotKt.Bir.TypeNode.Read(i) is DotKt.Bir.TypeNode.Fqn f
                && !_types.ContainsKey(BareTypeKey(f.Name))
                && !f.Name.StartsWith("kotlin.", StringComparison.Ordinal)) return true;
        return false;
    }

    Type EmitDynamicCall(JsonElement e)
    {
        var name = e.GetProperty("method").GetString();
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        var recvT = EmitExpr(e.GetProperty("recv"));
        if (NeedsBoxToRef(recvT)) _il.Emit(OpCodes.Box, recvT);   // box a value-type OR a `gp:T` receiver to object
        var recvLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, recvLocal);
        // mi = recv.GetType().GetMethod(name)   (this for Invoke)
        _il.Emit(OpCodes.Ldloc, recvLocal);
        _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType"));
        _il.Emit(OpCodes.Ldstr, name);
        _il.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("GetMethod", new[] { typeof(string) }));
        // Invoke(target=recv, object[] args)
        _il.Emit(OpCodes.Ldloc, recvLocal);
        _il.Emit(OpCodes.Ldc_I4, args.Length);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < args.Length; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var at = EmitExpr(args[i]);
            if (NeedsBoxToRef(at)) _il.Emit(OpCodes.Box, at);   // box a value-type OR a `gp:T` arg before stelem_ref into object[]
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Callvirt, typeof(MethodInfo).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) }));
        // result: pop a dropped void return, else unbox/cast to the CIR-declared dynRet. The spec is a CLR spelling —
        // bir2cir derives Unit->void upstream, so ilemit never sees a Kotlin `unit`/`kotlin.Unit` here (if it did, that
        // would be a bir2cir lowering defect, not something ilemit should silently absorb). The slot is a structured
        // TypeNode (post type-flip) OR a legacy string — MapType(JsonElement) dispatches both; only the bare-string
        // "void"/"System.Void" legacy spelling needed the special-case. (Regression guard: before the flip this read the
        // slot ONLY as a string, so a structured `dynRet` fell through to "void" and POPPED a live bool — e.g. a
        // dynamic-dispatched `it.MoveNext()` loop condition -> `brfalse` on an empty stack -> InvalidProgram.)
        JsonElement retEl = default; bool hasRet = false;
        if (e.TryGetProperty("dynRet", out var rr) && rr.ValueKind != JsonValueKind.Null) { retEl = rr; hasRet = true; }
        else if (e.TryGetProperty("ret", out var rr2) && rr2.ValueKind != JsonValueKind.Null) { retEl = rr2; hasRet = true; }
        var retT = hasRet ? MapType(retEl) : typeof(void);
        if (retT == typeof(void)) { _il.Emit(OpCodes.Pop); return typeof(void); }
        _il.Emit(OpCodes.Unbox_Any, retT);   // universal: unbox a value type, cast a ref type, resolve a generic param
        return retT;
    }

    Type EmitExpr(JsonElement e)
    {
        switch (_ctxNode = e.GetProperty("k").GetString())   // #84: refine the diagnostic breadcrumb to the node kind
        {
            case "const": return EmitConst(e);
            case "this":
                _il.Emit(OpCodes.Ldarg_0); return typeof(object);
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return _argTypes[name]; }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "field":
            {
                var fnm = e.GetProperty("name").GetString();
                // W1-S3 (#46 / #121) CONSUME-ONLY: an EXTERNAL owner's backing field is PRIVATE cross-assembly, so the
                // read goes through the public getter — bir2cir (ClrMemberResolution) decided that KIND and stamped
                // `member:"accessor"` + the resolved accessor name + memberSig + dispatch. ilemit no longer reinterprets a
                // `field` into a `get_` accessor (the ExternalPropAccessor probe is gone). Absent = a LOCAL owner (its
                // backing field is directly accessible) or a genuine public @ClrField -> the direct Ldfld path below.
                // (No Throwable.message/cause correction here either: bir2cir substitutes those to clrPropGet upstream.)
                if (e.TryGetProperty("member", out var fmk) && fmk.ValueKind == JsonValueKind.String && fmk.GetString() == "accessor")
                {
                    var ftype = ClrRef(e.GetProperty("ownerType"));
                    var getter = LinkClrMethod(ftype, e.GetProperty("accessor").GetString(), e, instance: true);
                    if (ftype.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
                    EmitClrDispatch(getter, RequireDispatch(e, ftype, "field"), ftype);
                    // Property-read twin of the CoerceReturn seam (`pair.first` vs the destructuring `component1()`),
                    // reconciling a collapsed-variance collection seam between the getter's REAL return type and bir2cir's
                    // declared `ret` view. FORWARD: the getter returns the mutable interface (IList<T>) while `ret` declares
                    // the readonly view (IReadOnlyList<T>). REVERSE: an external property genuinely typed readonly while
                    // `ret` declares the collapsed mutable view. Reconcile the stack to the declared view with a castclass.
                    var prDeclared = RetOr(e, getter.ReturnType);
                    if (IsCollectionViewSeam(getter.ReturnType, prDeclared)) _il.Emit(OpCodes.Castclass, prDeclared);
                    return prDeclared;
                }
                var fon = ParseOwnerSlot(e.GetProperty("ownerType"));
                EmitExpr(e.GetProperty("recv"));
                var fb = ResolveField(fon, fnm, out var ft);
                MaybeVolatile(fb);                       // `volatile.` prefix on a @Volatile field (pairs with modreq)
                _il.Emit(OpCodes.Ldfld, fb);
                return RetOr(e, ft);
            }
            case "setFieldExpr":
            {
                var snm = e.GetProperty("name").GetString();
                if (e.TryGetProperty("member", out var smk) && smk.ValueKind == JsonValueKind.String && smk.GetString() == "accessor")
                {
                    var stype = ClrRef(e.GetProperty("ownerType"));
                    var setter = LinkClrMethod(stype, e.GetProperty("accessor").GetString(), e, instance: true);
                    if (stype.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
                    EmitStoreCoerced(e.GetProperty("value"), SetterValueType(setter));
                    EmitClrDispatch(setter, RequireDispatch(e, stype, "setFieldExpr"), stype);
                    return typeof(void);
                }
                var son = ParseOwnerSlot(e.GetProperty("ownerType"));
                var sfefld = ResolveField(son, snm, out var sfet);
                EmitExpr(e.GetProperty("recv"));
                EmitStoreCoerced(e.GetProperty("value"), sfet);
                MaybeVolatile(sfefld);
                _il.Emit(OpCodes.Stfld, sfefld);
                return typeof(void);
            }
            case "lateinitGet":
            {
                // `lateinit var` read: load the field; if still null (uninitialized), throw.
                EmitExpr(e.GetProperty("recv"));
                var fld = ResolveField(ParseOwnerSlot(e.GetProperty("ownerType")), e.GetProperty("name").GetString(), out _);
                _il.Emit(OpCodes.Ldfld, fld);
                _il.Emit(OpCodes.Dup);
                var ok = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, ok);
                _il.Emit(OpCodes.Pop);
                _il.Emit(OpCodes.Ldstr, "lateinit property " + e.GetProperty("name").GetString() + " has not been initialized");
                _il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }));
                _il.Emit(OpCodes.Throw);
                _il.MarkLabel(ok);
                return fld.FieldType;
            }
            case "new":
            {
                var (open, constructed) = ParseOwnerSlot(e.GetProperty("type"));
                var nargs = e.GetProperty("args");
                if (!_types.TryGetValue(open, out var ti))
                {
                    // External type (e.g. `new kotlin.ranges.IntRange(1,3)` from an APP linking the rt where IntRange
                    // lives): resolve the ctor via reflection on the loaded assembly instead of indexing `_types`.
                    // Prefer a SIGNATURE match off the node's `argTypes` (kotc emits the resolved ctor's param types) so
                    // an overloaded external type resolves correctly; fall back to the first same-arity ctor.
                    var ext = constructed ?? ResolveType(open);
                    // An external generic instantiated over a FREE emitted type param (`AtomicReference<T>` where T is
                    // the enclosing method's type var) is a TypeBuilderInstantiation: its `.GetConstructor(s)` throw
                    // "does not support resolving members". Resolve the ctor on the OPEN definition (by declared-arg
                    // signature, else arity) and re-anchor onto the instantiation via the static TypeBuilder.GetConstructor
                    // — the exact mirror of EmitClrNew's IsTbInstantiation branch (which only fires for facadegen `clrNew`
                    // System.* types; a stdlib `kotlin.*` generic arrives here as a plain `new`).
                    if (IsTbInstantiation(ext))
                    {
                        var openDef = ext.GetGenericTypeDefinition();
                        var newArgc = nargs.GetArrayLength();
                        // argTypes describes the ctor's OWN params (kotc omits it, or prepends enclosing/capture args, for
                        // some `new` shapes) — use it only when its cardinality lines up (mirrors NewCtorBySig), else a
                        // null-filled array so PickOpenCtor never indexes out of range and degrades to the first same-arity ctor.
                        var rawArgTypes = e.TryGetProperty("argTypes", out var atEl) && atEl.ValueKind == JsonValueKind.Array
                            ? atEl.EnumerateArray().Select(a => { try { return ClrRef(a); } catch { return (Type)null; } }).ToArray()
                            : System.Array.Empty<Type>();
                        var newArgTypes = rawArgTypes.Length == newArgc ? rawArgTypes : new Type[newArgc];
                        ConstructorInfo openCtor = null;
                        if (newArgTypes.All(t => t != null))
                            try { openCtor = openDef.GetConstructor(newArgTypes); } catch (ArgumentException) { }
                        openCtor ??= PickOpenCtor(openDef, newArgTypes, newArgc)
                            ?? throw new NotSupportedException($"no matching ctor on the open def of {ext.FullName} with {newArgc} arg(s)");
                        EmitArgs(nargs, openCtor.GetParameters());
                        _il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(ext, openCtor));
                        return ext;
                    }
                    var ctorE = NewCtorBySig(ext, e, nargs.GetArrayLength())
                        ?? ext.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == nargs.GetArrayLength());
                    // Emit the ctor args against the RESOLVED ctor's SUBSTITUTED param types (a constructed-generic
                    // `Triple<int,string,int>` ctor wants value `int`, not object) — NOT the node's `argTypes`, which are
                    // the ctor's DECLARED params (the open class type-vars `!0/!1/!2`); those resolve to `object` in a
                    // non-generic caller, so EmitNewArgs would box the value args -> a `newobj` arg-type mismatch
                    // (ilverify StackUnexpected [found ref int32][expected Int32] -> runtime InvalidProgram).
                    if (ctorE != null) EmitArgs(nargs, ctorE.GetParameters()); else EmitNewArgs(e, nargs);
                    _il.Emit(OpCodes.Newobj, ctorE);
                    return ext;
                }
                var ctor = SelectCtor(ti, nargs.GetArrayLength(), e);
                // Pass the constructed instantiation's generic args so a value ctor arg is targeted at its CONCRETE
                // type (`Box<int>::.ctor(int)`), not boxed to the ResolveTv `object` fallback in a non-generic caller.
                EmitNewArgs(e, nargs, constructed is { IsGenericType: true } ? constructed.GetGenericArguments() : null);
                // Constructed user generic `Box<int>` -> resolve the ctor onto the instantiation (static helper).
                _il.Emit(OpCodes.Newobj, constructed != null ? TypeBuilder.GetConstructor(constructed, ctor) : (ConstructorInfo)ctor);
                return constructed ?? (Type)ti.TB;
            }
            case "callInstance":
            {
                // @ClrIntrinsicAsDynamic member: dispatch by RUNTIME reflection (recv.GetType().GetMethod(name).Invoke),
                // sidestepping static resolution that cascades (a member on a BCL `clrg:` interface FindMethod skips).
                if (e.TryGetProperty("dyn", out var dynF) && dynF.ValueKind == JsonValueKind.True)
                    return EmitDynamicCall(e);
                var cisig = SigNodes(e);
                MethodInfo m0 = null; Type rt = null;
                // A @Clr-bound member whose STATIC resolution fails -- it lives on a BCL clrg: interface that FindMethod
                // skips (e.g. AbstractMutableList.SubList calling get_Item on the IList slot) -- falls back to dynamic
                // dispatch. Gated to nodes carrying "dynRet" (the @Clr member calls), so a genuine miss elsewhere throws.
                var ciOwner = ParseOwnerSlot(e.GetProperty("ownerType"));   // keeps a constructed-generic owner's args
                try { m0 = ResolveMethod(ciOwner, e.GetProperty("method").GetString(), out rt, cisig); }
                catch (NotSupportedException) when (e.TryGetProperty("dynRet", out _) && OwnerHasClrInterface(ciOwner.open)) { return EmitDynamicCall(e); }
                var m = ApplyTypeArgs(m0, e, out var mrt, out var mps);
                // #108 GUARD (defensive, contract-violation only — never fires on valid CIR). This path pushes the
                // receiver as a plain value/reference (EmitExpr(recv)) then emits call/callvirt on `m` DIRECTLY. Per
                // ECMA-335 that is verifiable IL ONLY when `m`'s declaring type is a reference type: a value-type
                // (struct) receiver's `this` is a managed pointer, so it needs an address / unbox + `constrained.` —
                // the separate `constrainedCall` path, not this one. bir2cir lowers every value-type instance call to
                // `constrainedCall`, so a `callInstance` resolving to a value-type declaring type is a bir2cir contract
                // violation. Fail LOUD with a precise breadcrumb (the #84 CirEmitException style) rather than emit an
                // unverifiable bare receiver. (A generic-parameter *receiver* is also constrainedCall's job, but it is
                // not detectable from `m` here — its method resolves onto the constraint/interface, a reference type —
                // so this token-level guard covers only the value-type declaring type it CAN prove.)
                if (m.DeclaringType is { IsValueType: true })
                    throw new CirEmitException(CurrentDecl,
                        $"callInstance receiver is a value-type declaring type '{m.DeclaringType}' (method '{m.Name}'): this emit path pushes a plain receiver with no address/unbox/constrained., which is unverifiable IL — such an instance call must be lowered to 'constrainedCall' in bir2cir", null);
                EmitExpr(e.GetProperty("recv"));
                if (m == m0) EmitCallArgs(e.GetProperty("args"), m); else EmitArgsTyped(e.GetProperty("args"), mps, m);
                _il.Emit(IsVirtual(e) ? OpCodes.Callvirt : OpCodes.Call, m);
                return CoerceReturn(e, m == m0 ? rt : mrt);
            }
            case "constrainedCall":
            {
                // General N-arg form: a CLR-aliased INTERFACE member invoked on a generic-parameter receiver
                // (`destination.add(x)` where `destination: C` and `C : MutableCollection<R>`). A plain callvirt on the
                // padded ICollection<object> owner mis-dispatches (the runtime List<R> implements ICollection<R>) and
                // throws EntryPointNotFoundException; `constrained. !!C ; callvirt ICollection<R>::Add` dispatches on
                // the receiver's actual type. Distinguished from the single-`arg` compareTo form by the `args` array.
                if (e.TryGetProperty("args", out var ccArgs) && ccArgs.ValueKind == JsonValueKind.Array)
                {
                    var rt2 = MapType(e.GetProperty("recvType"));
                    var if2 = MapType(e.GetProperty("iface"));
                    // CIR already carries the exact constructed constraint owner. The only distinction here is token
                    // mechanics: a type emitted in this module must be linked through its TypeBuilder registry, while
                    // a referenced interface uses its reflected constructed MethodInfo. This does not re-select an
                    // owner or overload; bir2cir has already made both decisions.
                    var ifaceNode = e.GetProperty("iface");
                    var ifaceSpec = ReadFqn(ifaceNode);
                    var mi2 = ifaceSpec != null && _types.ContainsKey(ifaceSpec.Name)
                        ? ResolveMethod(ParseOwnerSlot(ifaceNode), e.GetProperty("method").GetString(), out _, SigNodes(e))
                        : InterfaceMethodOn(if2, e.GetProperty("method").GetString());
                    EmitAddr(e.GetProperty("recv"));            // &C  (a managed pointer, required by `constrained.`)
                    EmitArgs(ccArgs, mi2.GetParameters());
                    _il.Emit(OpCodes.Constrained, rt2);
                    _il.Emit(OpCodes.Callvirt, mi2);
                    return mi2.ReturnType;
                }
                // `a.compareTo(b)` on a Comparable -> `constrained. recvType; callvirt IComparable::CompareTo`.
                // The receiver must be a managed pointer; `constrained.` then dispatches for value/ref/generic T.
                var recvType = MapType(e.GetProperty("recvType"));
                var iface = MapType(e.GetProperty("iface"));
                // IComparable`1<T> instantiated over an EMITTED value type (e.g. a SAM-shim's class type param bound to a
                // Kotlin value class): re-anchoring CompareTo via TypeBuilder.GetMethod yields a metadata token the JIT
                // REJECTS for that value-type instantiation (InvalidProgramException) -- the same family as the generic-
                // enumerator fallback. Use the NON-generic System.IComparable.CompareTo(object) + box the arg; `constrained.`
                // still dispatches to T's own impl (value types implement both IComparable and IComparable<T>).
                //
                // BUT when the receiver is a generic PARAMETER (`!!T` with `T : Comparable<T>` — gen3's maxOf2 / SortedPair),
                // the instantiation `IComparable`1<!!T>` is over a type param, not an emitted value type: its token is a
                // plain MethodSpec that is BOTH JIT-safe AND ilverify-clean (the exact `constrained. !!T; callvirt
                // IComparable`1<!!T>::CompareTo(!0)` C# emits). The non-generic-IComparable workaround is UNVERIFIABLE there
                // because the constraint only proves `IComparable<T>`, not the non-generic `IComparable` -> keep the generic
                // path for a generic-parameter receiver; scope the workaround to genuinely-emitted value-type instantiations.
                bool brokenGeneric = iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IComparable<>)
                    && IsTbInstantiation(iface) && !recvType.IsGenericParameter;
                var mi = brokenGeneric ? typeof(IComparable).GetMethod("CompareTo")! : InterfaceMethodOn(iface, e.GetProperty("method").GetString());
                EmitAddr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("arg"));
                if (brokenGeneric) _il.Emit(OpCodes.Box, recvType);   // arg (type T) -> object for CompareTo(object)
                _il.Emit(OpCodes.Constrained, recvType);
                _il.Emit(OpCodes.Callvirt, mi);
                return mi.ReturnType;
            }
            case "callStatic":
            {
                var name = e.GetProperty("method").GetString();
                var csig = SigNodes(e);
                // owner present -> a static method on that named class (companion); else a file-class sibling.
                // A static on a GENERIC emitted class (a generic class's companion fun) must be anchored onto a
                // constructed owner — an open-typedef parent token is invalid IL at a foreign call site.
                // #199/#204 — `owner:null` remains the substitution/recognition axis, while mandatory `calleeOwner`
                // is the exact dispatch axis. Never global-search by method name: a missing/scoped miss is malformed
                // CIR and must fail loud instead of silently binding another file class's same-simple-name function.
                MethodInfo resolved;
                if (e.TryGetProperty("owner", out var ow) && ow.ValueKind != JsonValueKind.Null && SlotName(ow) is string ownm)
                    resolved = FindMethod(ownm, name, csig);
                else
                    resolved = FindCalleeOwnedStatic(e, "callStatic", name, csig);
                var mb = ApplyTypeArgs(AnchorOpenGenericOwnerStatic(resolved), e, out var srt, out var sps);
                if (e.TryGetProperty("typeArgs", out _)) EmitArgsTyped(e.GetProperty("args"), sps, mb);
                else EmitCallArgs(e.GetProperty("args"), mb);
                _il.Emit(OpCodes.Call, mb);
                return CoerceReturn(e, srt);
            }
            case "staticField":
            {
                // ownerType is already the final CIR TypeSpec (including the canonical instantiation for a static on a
                // generic owner). Preserve it exactly; collapsing through SlotName/FindField would silently replace
                // `G<object>` with the open `G<!0>` and manufacture invalid IL in a non-generic caller.
                var f = ResolveField(ParseOwnerSlot(e.GetProperty("ownerType")), e.GetProperty("name").GetString(), out var ft);
                MaybeVolatile(f);
                _il.Emit(OpCodes.Ldsfld, f);
                return ft;
            }
            case "clrStaticField":   // a static field on a .NET (reflected) type, e.g. EmptyCoroutineContext.Instance
            {
                var ct = ClrRef(e.GetProperty("type"));
                var cf = ct.GetField(e.GetProperty("name").GetString(), BindingFlags.Public | BindingFlags.Static);
                _il.Emit(OpCodes.Ldsfld, cf);
                return cf.FieldType;
            }
            case "staticFieldSet":
            {
                var sfsf = FindField(SlotName(e.GetProperty("ownerType")), e.GetProperty("name").GetString())
                    ?? throw new NotSupportedException($"static field {SlotName(e.GetProperty("ownerType"))}.{e.GetProperty("name").GetString()} not found");
                EmitStoreCoerced(e.GetProperty("value"), sfsf.FieldType);
                MaybeVolatile(sfsf);
                _il.Emit(OpCodes.Stsfld, sfsf);
                return typeof(void);
            }
            // NOTE: the `console` op (println/print -> System.Console.Write/WriteLine) was RETIRED (2026-07-02, bundle 1):
            // kotc now emits println/print as PLAIN top-level fun calls and bir2cir substitutes them to the BCL from the
            // stdlib @ClrIntrinsic (ConsoleClr.kt). This CLR-Console lowering is gone; no producer emits `k:"console"`.
            case "binOp": return EmitBin(e);
            case "objEq": return EmitObjEq(e);
            case "unaryOp": return EmitUn(e);
            case "conv": return EmitConv(e);
            case "valueBlock":
            {
                // Inlined scope function: run the spliced statements, then yield the result expression.
                foreach (var st in e.GetProperty("stmts").EnumerateArray()) EmitStmt(st);
                return EmitExpr(e.GetProperty("result"));
            }
            case "newList":
            {
                // `listOf(...)` -> new List<elem> { ... } via repeated Add.
                var elem = MapType(e.GetProperty("elem"));
                var listT = typeof(List<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, GenericCtor(listT));
                var add = GenericMethod(listT, "Add");
                foreach (var item in e.GetProperty("elems").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(item, elem);
                    _il.Emit(OpCodes.Callvirt, add);
                }
                return listT;
            }
            case "clrGenericStatic":
            {
                // Generic static call (LINQ): CONSUME the FIR-resolved `memberSig` descriptor — exact structural match,
                // MakeGenericMethod, call. ilemit picks NO overload (0 or >1 = hard link error; see ResolveGenericMethod).
                var type = ClrRef(e.GetProperty("type"));
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a)).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs, e, instance: false);
                var ps = mi.GetParameters();
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                for (int i = argEls.Count; i < ps.Length; i++) EmitDefaultArg(ps[i]);   // fill omitted trailing default/params args
                _il.Emit(OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "clrGenericInstance":
            {
                // Generic instance call (`obj.M<T>(...)`): same CONSUME-ONLY memberSig match as the static path, but
                // address the constructed receiver type and `callvirt`. (Shares ResolveGenericMethod's MakeGenericMethod core.)
                var type = ClrRef(e.GetProperty("type"));
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a)).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs, e, instance: true);
                var ps = mi.GetParameters();
                EmitExpr(e.GetProperty("recv"));
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                for (int i = argEls.Count; i < ps.Length; i++) EmitDefaultArg(ps[i]);   // fill omitted trailing default/params args
                // A `super.M<T>(...)` to a CLR-bound base (issue #14) forces a non-virtual `call` to the base slot on the
                // (reference) `this` receiver — else the callvirt re-dispatches to THIS class's override -> recursion.
                var genSuper = e.TryGetProperty("super", out var supGi) && supGi.GetBoolean() && !type.IsValueType;
                _il.Emit(mi.IsVirtual && !genSuper ? OpCodes.Callvirt : OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "newArray": return EmitNewArray(e);
            case "newArraySized":
            {
                // `IntArray(size)` (no init) -> a zero-filled BCL array (newarr zero-initializes).
                var elem = MapType(e.GetProperty("elem"));
                EmitExpr(e.GetProperty("size")); _il.Emit(OpCodes.Newarr, elem); return elem.MakeArrayType();
            }
            case "newArrayInit":
            {
                // `IntArray(size) { init }` -> `new elem[size]` + a fill loop `for i in 0..size-1: arr[i] = init(i)`.
                // The init is a Func<int,elem> delegate; box/unbox per its actual signature (primitive vs boxed lambda).
                var elem = MapType(e.GetProperty("elem"));
                EmitExpr(e.GetProperty("size")); var size = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, size);
                var fnType = EmitExpr(e.GetProperty("init")); var fn = _il.DeclareLocal(fnType); _il.Emit(OpCodes.Stloc, fn);
                // `Func<int,elem>` over an EMITTED elem (kotlin.Any / kotlin.UInt / a user class) is a TypeBuilder
                // instantiation whose .GetMethod / .GetParameters / .ReturnType all throw -- resolve Invoke via
                // InvokeOf, and read the param/return shapes off the delegate's type ARGS (GetGenericArguments is
                // safe on an instantiation; reflecting the Invoke signature is not).
                var ga = fnType.IsGenericType ? fnType.GetGenericArguments() : null;
                var invoke = ga == null ? InvokeOf(fnType) : null;
                var pType = ga != null ? ga[0] : invoke.GetParameters()[0].ParameterType;
                var rType = ga != null ? ga[^1] : invoke.ReturnType;
                _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Newarr, elem);
                var arr = _il.DeclareLocal(elem.MakeArrayType()); _il.Emit(OpCodes.Stloc, arr);
                var i = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, i);
                var top = _il.DefineLabel(); var done = _il.DefineLabel();
                _il.MarkLabel(top);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Bge, done);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, i);                       // arr, i (for stelem)
                _il.Emit(OpCodes.Ldloc, fn); _il.Emit(OpCodes.Ldloc, i);                         // fn, i
                if (!pType.IsValueType) _il.Emit(OpCodes.Box, typeof(int));
                EmitDelegateInvoke(_il, fnType);                                                 // init(i)
                if (rType != elem) { if (elem.IsValueType || elem.IsGenericParameter) _il.Emit(OpCodes.Unbox_Any, elem); else _il.Emit(OpCodes.Castclass, elem); }
                EmitStelem(elem);                                                                // arr[i] = init(i)
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Br, top);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, arr); return arr.LocalType;
            }
            case "default":
            {
                // `default(T)` -> the zero value: ldnull for a reference type, else a zero-init local (initobj).
                var dt = MapType(e.GetProperty("type"));
                if (!dt.IsValueType && !dt.IsGenericParameter) { _il.Emit(OpCodes.Ldnull); return dt; }
                var loc = _il.DeclareLocal(dt);
                _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, dt);
                _il.Emit(OpCodes.Ldloc, loc);
                return dt;
            }
            case "spreadConcat":
            {
                // `f(1, *a, 2)` -> new List<elem>(); Add(literal) / AddRange(spread); ToArray().
                var elem = MapType(e.GetProperty("elem"));
                var listT = typeof(List<>).MakeGenericType(elem);
                var ienumT = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                var loc = _il.DeclareLocal(listT);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes));
                _il.Emit(OpCodes.Stloc, loc);
                foreach (var p in e.GetProperty("parts").EnumerateArray())
                {
                    _il.Emit(OpCodes.Ldloc, loc);
                    EmitExpr(p.GetProperty("e"));
                    _il.Emit(OpCodes.Callvirt, p.GetProperty("spread").GetBoolean()
                        ? listT.GetMethod("AddRange", new[] { ienumT })
                        : listT.GetMethod("Add", new[] { elem }));
                }
                _il.Emit(OpCodes.Ldloc, loc);
                _il.Emit(OpCodes.Callvirt, listT.GetMethod("ToArray", Type.EmptyTypes));
                return elem.MakeArrayType();
            }
            case "arrayGet":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index"));
                var elem = MapType(e.GetProperty("elem"));
                EmitLdelem(elem); return elem;
            }
            case "arraySet":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index"));
                var selem = MapType(e.GetProperty("elem"));
                // Coerce the value to the element type before stelem: a value-type/generic-param value into a
                // REFERENCE-element array (`Array<Any?>[i] = aT`) boxes; a bare `T` / null into a `Nullable<T>` element
                // (`Array<Int?>[i] = 5`) wraps to `Nullable<T>` / `default(Nullable<T>)` — else `stelem Nullable<int>`
                // with a raw int on the stack corrupts the struct (SIGSEGV). A GENERIC-PARAM element (`T[]`, stelem !T)
                // must NOT box. Shared with EmitNewArray via EmitArrayElemCoerced.
                EmitArrayElemCoerced(e.GetProperty("value"), selem);
                EmitStelem(selem); return typeof(void);
            }
            case "arrayLen":
                EmitExpr(e.GetProperty("array")); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "forEachInline":
            {
                // `xs.forEach { it -> body }` (inline) -> enumerate src, bind `it` to a loop local, splice body.
                // Inlining (not a delegate) lets the body read/write enclosing locals without closure Ref cells.
                var elem = MapType(e.GetProperty("elem"));
                var ienumT = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                // When `elem` is a TYPE PARAMETER (method/class), IEnumerable<!!T>/IEnumerator<!!T> are TypeBuilder
                // instantiations of a BCL generic; TypeBuilder.GetMethod re-anchoring them yields a BROKEN metadata
                // token (runtime EntryPointNotFound) in a non-inline method. Fall back to the NON-GENERIC IEnumerable/
                // IEnumerator (no <!!T> -> no bad token) + Unbox_Any the object Current to elem. Concrete elem types
                // keep the typed enumerator (faster, no box).
                bool viaNonGeneric = IsTbInstantiation(ienumT);
                EmitExpr(e.GetProperty("src"));
                Type enT;
                if (viaNonGeneric)
                {
                    _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator"));
                    enT = typeof(System.Collections.IEnumerator);
                }
                else
                {
                    _il.Emit(OpCodes.Callvirt, GenericMethod(ienumT, "GetEnumerator"));
                    enT = typeof(System.Collections.Generic.IEnumerator<>).MakeGenericType(elem);
                }
                var en = _il.DeclareLocal(enT); _il.Emit(OpCodes.Stloc, en);
                var lv = _il.DeclareLocal(elem); _locals[e.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(e), start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, en);
                _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext"));
                _il.Emit(OpCodes.Brfalse, end);
                _il.Emit(OpCodes.Ldloc, en);
                if (viaNonGeneric)
                {
                    _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("get_Current"));
                    _il.Emit(OpCodes.Unbox_Any, elem);
                }
                else
                {
                    _il.Emit(OpCodes.Callvirt, GenericMethod(enT, "get_Current"));
                }
                _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in e.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return typeof(void);
            }
            case "isInst":
            {
                // `x is T` -> isinst T; (ref != null) as bool. A value-type / generic-param receiver MUST be boxed
                // first: `isinst` consumes an object reference off the stack, so reading an unboxed value type (or an
                // `!!T` whose runtime T is a value type) as a reference gives an NRE. This is what C# emits for
                // `element is X` when `element` is a generic `T` (box !!T; isinst X).
                var rt0 = EmitExpr(e.GetProperty("e"));
                if (NeedsBoxToRef(rt0)) _il.Emit(OpCodes.Box, rt0);
                _il.Emit(OpCodes.Isinst, MapType(e.GetProperty("type")));
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Cgt_Un);
                return typeof(bool);
            }
            case "cast":
            {
                // `x as T` / smart-cast downcast. A generic type parameter (`!!T`) is NOT IsValueType at emit time, but
                // `castclass` is INVALID for a VALUE-type instantiation (the JIT rejects `castclass int` ->
                // InvalidProgram). `unbox.any` is the universal cast: unbox for value types, castclass for reference
                // types, and resolves a generic param correctly at JIT -- exactly what C# emits for `(T)objExpr`.
                var castSrc = EmitExpr(e.GetProperty("e"));
                var t = MapType(e.GetProperty("type"));
                // IDENTITY cast (source already IS the target, e.g. `(element: T) as T`): the stack already holds `t`.
                // Emitting `unbox.any !!T` here would read an ALREADY-UNBOXED `!!T` value as a boxed-object pointer ->
                // NullReferenceException at runtime. Nothing to do -- just report the type.
                if (castSrc == t) return t;
                var toRef = !(t.IsValueType || t.IsGenericParameter);
                // A VALUE/GENERIC source flowing into ANY target that isn't already itself must be boxed first: an
                // unboxed !!T / struct feeding `castclass` (ref target) is invalid IL, and feeding `unbox.any` (value /
                // generic target) reads a raw value as a boxed-obj pointer. Box independently of the target kind --
                // e.g. `(x: T) as IComparable` in compareValues, or a value flowing into a differing generic slot.
                if (NeedsBoxToRef(castSrc)) _il.Emit(OpCodes.Box, castSrc);
                _il.Emit(toRef ? OpCodes.Castclass : OpCodes.Unbox_Any, t);
                return t;
            }
            case "classRef":
            {
                return EmitNativeClrTypeOf(e);
            }
            case "getType":
            {
                return EmitNativeClrGetType(e);
            }
            case "isInstRef":
            {
                // `x as? T` for reference T -> `isinst T` (leaves the ref, or null on mismatch). The result is a
                // reference (objref or null), so report `object` — never a generic-param type that would make a
                // downstream consumer (objMethod/objEq) wrongly re-box an already-reference value.
                var rtr = EmitExpr(e.GetProperty("e"));
                if (NeedsBoxToRef(rtr)) _il.Emit(OpCodes.Box, rtr);
                var t = MapType(e.GetProperty("type"));
                _il.Emit(OpCodes.Isinst, t);
                return typeof(object);
            }
            case "safeCastValue":
            {
                return EmitNativeClrSafeCastValue(e);
            }
            case "nullableNull":
            {
                return EmitNativeClrNullableNull(e);
            }
            case "nullableWrap":
            {
                return EmitNativeClrNullableWrap(e);
            }
            case "nullableHasValue":
            {
                return EmitNativeClrNullableHasValue(e);
            }
            case "nullableValue":
            {
                return EmitNativeClrNullableValue(e);
            }
            case "repeatInline":
            {
                // `repeat(n) { i -> body }` -> for (i = 0; i < n; i++) { body } (i bound to a loop local).
                var lv = _il.DeclareLocal(typeof(int)); _locals[e.GetProperty("var").GetString()] = lv;
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, lv);
                var cnt = _il.DeclareLocal(typeof(int)); EmitExpr(e.GetProperty("count")); _il.Emit(OpCodes.Stloc, cnt);
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(e), start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldloc, cnt); _il.Emit(OpCodes.Bge, end);
                foreach (var b in e.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, lv);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return typeof(void);
            }
            case "enumValue":
            {
                return EmitNativeClrEnumValue(e);
            }
            case "enumOrdinal":
                return EmitNativeClrEnumOrdinal(e);
            case "enumValues":
            {
                return EmitNativeClrEnumValues(e);
            }
            case "enumParse":
            {
                return EmitNativeClrEnumParse(e);
            }
            case "objMethod": return EmitObjMethod(e);
            case "newMap":
            {
                // `mapOf(k to v, …)` -> new Dictionary<K,V> { [k]=v, … } via set_Item.
                var kt = MapType(e.GetProperty("keyType"));
                var vt = MapType(e.GetProperty("valType"));
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                _il.Emit(OpCodes.Newobj, GenericCtor(dt));
                var setItem = GenericMethod(dt, "set_Item");
                foreach (var en in e.GetProperty("entries").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(en.GetProperty("key"), kt);
                    EmitArg(en.GetProperty("value"), vt);
                    _il.Emit(OpCodes.Callvirt, setItem);
                }
                return dt;
            }
            case "newSet":
            {
                // `setOf(...)` -> new HashSet<elem> { ... } via repeated Add (Add returns bool -> pop).
                var elem = MapType(e.GetProperty("elem"));
                var setT = typeof(System.Collections.Generic.HashSet<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, GenericCtor(setT));
                var add = GenericMethod(setT, "Add");
                foreach (var item in e.GetProperty("elems").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(item, elem);
                    _il.Emit(OpCodes.Callvirt, add);
                    _il.Emit(OpCodes.Pop);
                }
                return setT;
            }
            case "throwExpr":
            {
                // A throwing expression (error()/TODO()/exhaustive-when else): construct + throw; no value reaches a merge.
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Throw);
                return typeof(object);
            }
            case "returnExpr":
            {
                // `return` in expression position: emit the method return; no value reaches the surrounding merge
                // (mirrors the "return" statement, incl. the protected-region leave and the return coercion).
                if (_tryStack.Count > 0)
                {
                    var ctx = _tryStack.Peek();
                    if (e.TryGetProperty("value", out var trv))
                    {
                        var tgot = EmitExpr(trv);
                        if (ctx.result != null) { EmitReturnCoerced(tgot); _il.Emit(OpCodes.Stloc, ctx.result); }
                        else _il.Emit(OpCodes.Pop);
                    }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (e.TryGetProperty("value", out var rv)) EmitReturnCoerced(EmitExpr(rv));
                    _il.Emit(OpCodes.Ret);
                }
                return typeof(object);
            }
            case "newDelegate":
            {
                // Non-capturing lambda: bind the lifted static method into a Func/Action delegate.
                var ft = MapType(e.GetProperty("funcType"));
                // #199/#203/#204: calleeOwner selects the exact file class and sig selects its overload. Locally
                // lifted __lambda/__ctorref/__mref targets carry their synthesizing file class too; no global fallback.
                var dname = e.GetProperty("method").GetString();
                var dsig = SigNodes(e);
                MethodInfo mb = FindCalleeOwnedStatic(e, "newDelegate", dname, dsig);
                // A GENERIC lifted lambda (e.g. the comparator inside a generic `sort<T>`) MUST be instantiated with its
                // typeArgs before Ldftn -- loading the open generic-method-DEFINITION's ftn throws "the method itself or
                // the containing type is not fully instantiated" at runtime.
                MethodInfo target = (e.TryGetProperty("typeArgs", out var dta) && dta.GetArrayLength() > 0 && mb.IsGenericMethodDefinition)
                    ? mb.MakeGenericMethod(dta.EnumerateArray().Select(x => MapType(x)).ToArray())
                    : mb;
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, target);
                EmitDelegateCtor(_il, ft);
                return ft;
            }
            case "newBoundDelegate":
            {
                // `obj::method` -> a delegate bound to the receiver. The carried declaration `sig` selects the exact
                // overload within ownerType (#203). ldvirtftn needs the object twice (dup); a
                // final method uses ldftn (the target stays on the stack as the delegate's first ctor arg).
                var ft = MapType(e.GetProperty("funcType"));
                var boundName = e.GetProperty("method").GetString();
                var boundOwner = SlotName(e.GetProperty("ownerType"));
                if (!e.TryGetProperty("calleeOwner", out var bco) || bco.ValueKind == JsonValueKind.Null
                    || SlotName(bco) is not string bcoName || bcoName != boundOwner)
                    throw new NotSupportedException($"newBoundDelegate target '{boundOwner}.{boundName}' is missing or mismatches required calleeOwner");
                var mb = FindMethod(boundOwner, boundName, SigNodes(e))
                    ?? throw new NotSupportedException($"newBoundDelegate target '{boundOwner}.{boundName}' was not found");
                var recvT = EmitExpr(e.GetProperty("recv"));
                // A value-type (or `gp:T`) receiver must be BOXED before it can back the delegate: the delegate ctor's
                // first arg is `object` and `ldvirtftn` dispatches on an object reference, but EmitExpr pushed the raw
                // struct value. Box gives a valid object target; the CLR delegate machinery routes it through the value
                // type's unboxing stub for a non-virtual `ldftn` target and virtual dispatch for `ldvirtftn`.
                if (NeedsBoxToRef(recvT)) _il.Emit(OpCodes.Box, recvT);
                if (IsVirtual(e)) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mb); }
                else _il.Emit(OpCodes.Ldftn, mb);
                EmitDelegateCtor(_il, ft);
                return ft;
            }
            case "newBoundClrDelegate":
            {
                // `netObj::method` -> a delegate bound to a .NET instance method. W1-S5 (#46/#183): CONSUME the FIR-
                // resolved `memberSig` descriptor bir2cir carried (ClrMemberResolution.ResolveBoundClrDelegate) — LINK
                // the UNIQUE instance target (0 = hard ABI error, >1 = malformed), never a name-only first-pick.
                var ft = MapType(e.GetProperty("funcType"));
                // `clrType` is a STRUCTURED TypeNode post type-flip (was a bare string); ClrRef(JsonElement) dispatches both.
                var type = ClrRef(e.GetProperty("clrType"));
                var mi = LinkClrMethod(type, e.GetProperty("method").GetString(), e, instance: true);
                var recvTc = EmitExpr(e.GetProperty("recv"));
                // Same value-type-receiver rule as newBoundDelegate: a struct .NET receiver (e.g. `kvp::method`) must be
                // boxed so the delegate ctor's `object` target and `ldvirtftn` see an object reference, not a raw struct.
                if (NeedsBoxToRef(recvTc)) _il.Emit(OpCodes.Box, recvTc);
                if (IsVirtual(e)) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mi); }
                else _il.Emit(OpCodes.Ldftn, mi);
                EmitDelegateCtor(_il, ft);
                return ft;
            }
            case "delegateInvoke":
            {
                var ftNode = e.GetProperty("funcType");
                var ft = MapType(ftNode);
                EmitExpr(e.GetProperty("recv"));
                // Coerce each invoke arg to the delegate param type declared in the funcType. The delegate's Invoke
                // param is the FUNCTION type parameter (`Func<T,R>::Invoke(!0)`), so at a VALUE-type instantiation
                // (`Func<int,object>`) it expects the raw `int` on the stack — but a `T?`-erased arg (a `nextItem:
                // object` field read passed as `nextItem!!`) pushes a BOXED object. A reference-type instantiation
                // tolerates the object (it IS a valid reference), which is why only value-typed elements crashed
                // (generateSequence(1){…} -> InvalidProgramException in the GeneratorSequence iterator's calcNext).
                // `unbox.any <param>` is the universal fix: unbox a value-type param, castclass a reference one.
                var invArgSpecs = FuncArgTypes(ftNode);
                var invArgs = e.GetProperty("args").EnumerateArray().ToArray();
                for (int ia = 0; ia < invArgs.Length; ia++)
                {
                    var got = EmitExpr(invArgs[ia]);
                    if (ia < invArgSpecs.Count && invArgSpecs[ia] is { } want && got != null
                        && (want.IsValueType || want.IsGenericParameter)
                        && !got.IsValueType && !got.IsGenericParameter && got != want)
                        _il.Emit(OpCodes.Unbox_Any, want);
                }
                EmitDelegateInvoke(_il, ft);
                return FuncRetType(ftNode);
            }
            case "newClosure":
            {
                // Capturing lambda: `new Closure(captures)` then bind its `invoke` instance method as a delegate.
                // ResolveClosure instantiates the closure generic when it captures an enclosing type param (a generic
                // closure left open -> a TypeLoadException at the newobj); shared with the delegate-arg binding path.
                var (ctor, invoke) = ResolveClosure(e);
                foreach (var c in e.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ctor);              // closure instance is the delegate target
                _il.Emit(OpCodes.Ldftn, invoke);
                var ft = MapType(e.GetProperty("funcType"));
                EmitDelegateCtor(_il, ft);
                return ft;
            }
            case "newSam":
            {
                // SAM conversion `Comparator { … }` -> `new <Sam>(captures)` -- a synthetic class IMPLEMENTING the fun
                // interface (no delegate). The instance IS the interface value (implicit upcast at the use site).
                var ct = _types[SlotName(e.GetProperty("samType"))];
                ConstructorInfo ctor = ct.Ctor;
                Type result = ct.TB;
                if (e.TryGetProperty("typeArgs", out var staProp) && staProp.GetArrayLength() > 0)
                {
                    var typeArgs = staProp.EnumerateArray().Select(a => MapType(a)).ToArray();
                    result = ct.TB.MakeGenericType(typeArgs);
                    ctor = TypeBuilder.GetConstructor(result, ct.Ctor);
                }
                foreach (var c in e.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ctor);
                return result;
            }
            case "concat": return EmitConcat(e);
            case "cond": return EmitCond(e);
            case "newClr": return EmitClrNew(e);
            case "clrStatic": return EmitClrCall(e, instance: false);
            case "clrInstance": return EmitClrCall(e, instance: true);
            // W1-S2 (#46): a clrInstance whose interface owner has NO statically-matching BCL slot (the runtime value
            // implements it under a different concrete type) is emitted by bir2cir as a DELIBERATE dynamic-dispatch node
            // — replacing ilemit's former SILENT EmitClrCall->EmitDynamicCall downgrade, so the fallback is greppable.
            case "clrDynInstance": return EmitDynamicCall(e);
            case "clrPropGet": return EmitClrPropGet(e);
            case "clrPropSet": return EmitClrPropSet(e);
            case "clrEventAdd": return EmitClrEvent(e, add: true);
            case "clrEventRemove": return EmitClrEvent(e, add: false);
            case "byrefOf":
            {
                // The live managed pointer behind `byref(...)` in a `var x by` delegate: keep a ref return's pointer
                // (deref:false), or take the address of a local/field lvalue.
                var inner = e.GetProperty("inner");
                var ik = inner.GetProperty("k").GetString();
                if (ik == "clrInstance") return EmitClrCall(inner, instance: true, deref: false);
                if (ik == "clrStatic") return EmitClrCall(inner, instance: false, deref: false);
                EmitAddr(inner);
                return null;
            }
            case "stackAlloc":
            {
                // `localloc` a zero-initialized stack buffer of `count * sizeof(elem)` bytes, leaving its pointer.
                // (Unverifiable, like C#'s own stackalloc.)
                var elem = MapType(e.GetProperty("elem"));
                var bc = _il.DeclareLocal(typeof(int));
                EmitExpr(e.GetProperty("count"));
                _il.Emit(OpCodes.Sizeof, elem);
                _il.Emit(OpCodes.Mul);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Stloc, bc);   // keep byteCount for initblk
                _il.Emit(OpCodes.Conv_U);
                _il.Emit(OpCodes.Localloc);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ldloc, bc); _il.Emit(OpCodes.Initblk);
                return typeof(byte).MakePointerType();
            }
            case "stackGet":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem"));
                EmitStackAddr(e, elem);
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "stackSet":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem"));
                EmitStackAddr(e, elem);
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "stackAsSpan":
            {
                // `new System.Span<T>(void* ptr, int length)` over the stack buffer -> a real Span for .NET APIs.
                var elem = MapType(e.GetProperty("elem"));
                var spanT = typeof(System.Span<>).MakeGenericType(elem);
                var ctor = spanT.GetConstructor(new[] { typeof(void*), typeof(int) });
                EmitExpr(e.GetProperty("ptr"));
                EmitExpr(e.GetProperty("len"));
                _il.Emit(OpCodes.Newobj, ctor);
                return spanT;
            }
            case "byrefLoad":
            {
                // Read through a byref local (the ClrRef delegate): ldloc the pointer, ldobj to dereference.
                _il.Emit(OpCodes.Ldloc, _locals[e.GetProperty("local").GetString()]);
                var elem = MapType(e.GetProperty("elem"));
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "byrefStore":
            {
                // Write through a byref local: ldloc the pointer, push the value, stobj.
                _il.Emit(OpCodes.Ldloc, _locals[e.GetProperty("local").GetString()]);
                var elem = MapType(e.GetProperty("elem"));
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "unsupportedExpr": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + e.GetProperty("of").GetString());
            default: throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
    }
}
