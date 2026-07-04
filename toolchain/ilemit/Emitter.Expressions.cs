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
        foreach (var i in ifs.EnumerateArray()) { var s = i.GetString(); if (s != null && (s.StartsWith("clr:") || s.StartsWith("clrg:"))) return true; }
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
        // result: pop a dropped (void/Unit) return, else unbox/cast to the CIR-declared dynRet
        var retSpec = e.TryGetProperty("dynRet", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString()
                    : e.TryGetProperty("ret", out var rr2) && rr2.ValueKind == JsonValueKind.String ? rr2.GetString() : "void";
        if (retSpec is "void" or "unit" or "kotlin.Unit" or "System.Void") { _il.Emit(OpCodes.Pop); return typeof(void); }
        var retT = MapType(retSpec);
        _il.Emit(OpCodes.Unbox_Any, retT);   // universal: unbox a value type, cast a ref type, resolve a generic param
        return retT;
    }

    Type EmitExpr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "const": return EmitConst(e);
            case "clr.const": return EmitConst(e);
            case "this":
                _il.Emit(OpCodes.Ldarg_0); return typeof(object);
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                // Inside a cross-module inline splice, a callee param reference emits the bound arg/value instead.
                if (_inlineSubst.TryGetValue(name, out var sub)) return EmitExpr(sub);
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return _argTypes[name]; }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "field":
            {
                var fon = e.GetProperty("ownerType").GetString();
                var fnm = e.GetProperty("name").GetString();
                // `Throwable.message`/`.cause` (a Kotlin property accessed as a field) -> System.Exception property.
                if (fon == "Throwable" && (fnm == "message" || fnm == "cause"))
                {
                    EmitExpr(e.GetProperty("recv"));
                    var m = typeof(Exception).GetMethod(fnm == "message" ? "get_Message" : "get_InnerException");
                    _il.Emit(OpCodes.Callvirt, m);
                    return m.ReturnType;
                }
                // An EXTERNAL type's property read must go through the public getter (its backing field is private
                // cross-assembly -> Ldfld would throw FieldAccessException). Falls back to the field when no getter.
                if (ExternalPropAccessor(fon, "get_" + fnm) is { } getter)
                {
                    EmitExpr(e.GetProperty("recv"));
                    _il.Emit(OpCodes.Callvirt, getter);
                    return RetOr(e, getter.ReturnType);
                }
                EmitExpr(e.GetProperty("recv"));
                var fb = ResolveField(fon, fnm, out var ft);
                _il.Emit(OpCodes.Ldfld, fb);
                return RetOr(e, ft);
            }
            case "setFieldExpr":
            {
                var son = e.GetProperty("ownerType").GetString();
                var snm = e.GetProperty("name").GetString();
                if (ExternalPropAccessor(son, "set_" + snm) is { } setter)
                {
                    EmitExpr(e.GetProperty("recv"));
                    EmitStoreCoerced(e.GetProperty("value"), SetterValueType(setter));
                    _il.Emit(OpCodes.Callvirt, setter);
                    return typeof(void);
                }
                var sfefld = ResolveField(son, snm, out var sfet);
                EmitExpr(e.GetProperty("recv"));
                EmitStoreCoerced(e.GetProperty("value"), sfet);
                _il.Emit(OpCodes.Stfld, sfefld);
                return typeof(void);
            }
            case "lateinitGet":
            {
                // `lateinit var` read: load the field; if still null (uninitialized), throw.
                EmitExpr(e.GetProperty("recv"));
                var fld = ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out _);
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
                var (open, constructed) = ParseOwner(e.GetProperty("type").GetString());
                var nargs = e.GetProperty("args");
                if (!_types.TryGetValue(open, out var ti))
                {
                    // External type (e.g. `new kotlin.ranges.IntRange(1,3)` from an APP linking the rt where IntRange
                    // lives): resolve the ctor via reflection on the loaded assembly instead of indexing `_types`.
                    // Prefer a SIGNATURE match off the node's `argTypes` (kotc emits the resolved ctor's param types) so
                    // an overloaded external type resolves correctly; fall back to the first same-arity ctor.
                    var ext = constructed ?? ResolveType(open);
                    var ctorE = NewCtorBySig(ext, e, nargs.GetArrayLength())
                        ?? ext.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == nargs.GetArrayLength());
                    EmitNewArgs(e, nargs);
                    _il.Emit(OpCodes.Newobj, ctorE);
                    return ext;
                }
                var ctor = SelectCtor(ti, nargs.GetArrayLength());
                EmitNewArgs(e, nargs);
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
                var cisig = e.TryGetProperty("sig", out var ciEl) && ciEl.ValueKind == JsonValueKind.String ? ciEl.GetString() : null;
                MethodInfo m0 = null; Type rt = null;
                // A @Clr-bound member whose STATIC resolution fails -- it lives on a BCL clrg: interface that FindMethod
                // skips (e.g. AbstractMutableList.SubList calling get_Item on the IList slot) -- falls back to dynamic
                // dispatch. Gated to nodes carrying "dynRet" (the @Clr member calls), so a genuine miss elsewhere throws.
                try { m0 = ResolveMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString(), out rt, cisig); }
                catch (NotSupportedException) when (e.TryGetProperty("dynRet", out _) && OwnerHasClrInterface(e.GetProperty("ownerType").GetString())) { return EmitDynamicCall(e); }
                var m = ApplyTypeArgs(m0, e, out var mrt, out var mps);
                EmitExpr(e.GetProperty("recv"));
                if (m == m0) EmitCallArgs(e.GetProperty("args"), m); else EmitArgsTyped(e.GetProperty("args"), mps, m);
                _il.Emit(e.GetProperty("virtual").GetBoolean() ? OpCodes.Callvirt : OpCodes.Call, m);
                return CoerceReturn(e, m == m0 ? rt : mrt);
            }
            case "constrainedCall":
            case "clr.constrained.compareTo":
            {
                // General N-arg form: a CLR-aliased INTERFACE member invoked on a generic-parameter receiver
                // (`destination.add(x)` where `destination: C` and `C : MutableCollection<R>`). A plain callvirt on the
                // padded ICollection<object> owner mis-dispatches (the runtime List<R> implements ICollection<R>) and
                // throws EntryPointNotFoundException; `constrained. !!C ; callvirt ICollection<R>::Add` dispatches on
                // the receiver's actual type. Distinguished from the single-`arg` compareTo form by the `args` array.
                if (e.TryGetProperty("args", out var ccArgs) && ccArgs.ValueKind == JsonValueKind.Array)
                {
                    var rt2 = MapType(e.GetProperty("recvType").GetString());
                    var if2 = MapType(e.GetProperty("iface").GetString());
                    var mi2 = InterfaceMethodOn(if2, e.GetProperty("method").GetString());
                    EmitAddr(e.GetProperty("recv"));            // &C  (a managed pointer, required by `constrained.`)
                    EmitArgs(ccArgs, mi2.GetParameters());
                    _il.Emit(OpCodes.Constrained, rt2);
                    _il.Emit(OpCodes.Callvirt, mi2);
                    return mi2.ReturnType;
                }
                // `a.compareTo(b)` on a Comparable -> `constrained. recvType; callvirt IComparable::CompareTo`.
                // The receiver must be a managed pointer; `constrained.` then dispatches for value/ref/generic T.
                var recvType = MapType(e.GetProperty("recvType").GetString());
                var iface = MapType(e.GetProperty("iface").GetString());
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
                var csig = e.TryGetProperty("sig", out var csEl) && csEl.ValueKind == JsonValueKind.String ? csEl.GetString() : null;
                // owner present -> a static method on that named class (companion); else a file-class sibling.
                // A static on a GENERIC emitted class (a generic class's companion fun) must be anchored onto a
                // constructed owner — an open-typedef parent token is invalid IL at a foreign call site.
                var mb = ApplyTypeArgs(AnchorOpenGenericOwnerStatic(
                    (e.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String)
                        ? FindMethod(ow.GetString(), name, csig) : FindStatic(name, csig)), e, out var srt, out var sps);
                if (e.TryGetProperty("typeArgs", out _)) EmitArgsTyped(e.GetProperty("args"), sps, mb);
                else EmitCallArgs(e.GetProperty("args"), mb);
                _il.Emit(OpCodes.Call, mb);
                return CoerceReturn(e, srt);
            }
            case "staticField":
            {
                // A miss on an EXTERNAL owner returns null from FindField — surface it as a legible error
                // (an unchecked Ldsfld(null) was an opaque ArgumentNullException deep in ILGenerator).
                var f = FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString())
                    ?? throw new NotSupportedException($"static field {e.GetProperty("ownerType").GetString()}.{e.GetProperty("name").GetString()} not found");
                _il.Emit(OpCodes.Ldsfld, f);
                return f.FieldType;
            }
            case "clrStaticField":   // a static field on a .NET (reflected) type, e.g. EmptyCoroutineContext.Instance
            {
                var ct = ResolveType(e.GetProperty("type").GetString());
                var cf = ct.GetField(e.GetProperty("name").GetString(), BindingFlags.Public | BindingFlags.Static);
                _il.Emit(OpCodes.Ldsfld, cf);
                return cf.FieldType;
            }
            case "staticFieldSet":
            {
                var sfsf = FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString())
                    ?? throw new NotSupportedException($"static field {e.GetProperty("ownerType").GetString()}.{e.GetProperty("name").GetString()} not found");
                EmitStoreCoerced(e.GetProperty("value"), sfsf.FieldType);
                _il.Emit(OpCodes.Stsfld, sfsf);
                return typeof(void);
            }
            // NOTE: the `console` op (println/print -> System.Console.Write/WriteLine) was RETIRED (2026-07-02, bundle 1):
            // kotc now emits println/print as PLAIN top-level fun calls and bir2cir substitutes them to the BCL from the
            // stdlib @ClrIntrinsic (ConsoleClr.kt). This CLR-Console lowering is gone; no producer emits `k:"console"`.
            case "bin": return EmitBin(e);
            case "clr.bin": return EmitBin(e);
            case "objEq": return EmitObjEq(e);
            case "clr.obj.eq": return EmitObjEq(e);
            case "un": return EmitUn(e);
            case "clr.un": return EmitUn(e);
            case "conv": return EmitConv(e);
            case "clr.conv": return EmitConv(e);
            case "clr.isinst": return EmitNativeClrIsInst(e, resultIsBool: true);
            case "clr.castclass": return EmitNativeClrCastClass(e);
            case "clr.isinst.ref": return EmitNativeClrIsInst(e, resultIsBool: false);
            case "clr.safeCast.value": return EmitNativeClrSafeCastValue(e);
            case "clr.nullable.null": return EmitNativeClrNullableNull(e);
            case "clr.nullable.wrap": return EmitNativeClrNullableWrap(e);
            case "clr.nullable.hasValue": return EmitNativeClrNullableHasValue(e);
            case "clr.nullable.value": return EmitNativeClrNullableValue(e);
            case "clr.typeof": return EmitNativeClrTypeOf(e);
            case "clr.getType": return EmitNativeClrGetType(e);
            case "clr.enum.value": return EmitNativeClrEnumValue(e);
            case "clr.enum.ordinal": return EmitNativeClrEnumOrdinal(e);
            case "clr.enum.values": return EmitNativeClrEnumValues(e);
            case "clr.enum.parse": return EmitNativeClrEnumParse(e);
            case "valueBlock":
            {
                // Inlined scope function: run the spliced statements, then yield the result expression.
                foreach (var st in e.GetProperty("stmts").EnumerateArray()) EmitStmt(st);
                return EmitExpr(e.GetProperty("result"));
            }
            case "listNew":
            {
                // `listOf(...)` -> new List<elem> { ... } via repeated Add.
                var elem = MapType(e.GetProperty("elem").GetString());
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
                // Generic static call (LINQ): pick the exact overload by parameter shapes, MakeGenericMethod, call.
                var type = ResolveType(e.GetProperty("type").GetString());
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var shapes = e.GetProperty("shapes").EnumerateArray().Select(a => a.GetString()).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs.Length, shapes, typeArgs, instance: false);
                var ps = mi.GetParameters();
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                for (int i = argEls.Count; i < ps.Length; i++) EmitDefaultArg(ps[i]);   // fill omitted trailing default/params args
                _il.Emit(OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "clrGenericInstance":
            {
                // Generic instance call (`obj.M<T>(...)`): same overload resolution as the static path, but address
                // the constructed receiver type and `callvirt`. (Shares ResolveGenericMethod's MakeGenericMethod core.)
                var type = ClrRef(e.GetProperty("type").GetString());
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var shapes = e.GetProperty("shapes").EnumerateArray().Select(a => a.GetString()).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs.Length, shapes, typeArgs, instance: true);
                var ps = mi.GetParameters();
                EmitExpr(e.GetProperty("recv"));
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                for (int i = argEls.Count; i < ps.Length; i++) EmitDefaultArg(ps[i]);   // fill omitted trailing default/params args
                _il.Emit(mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "newArray": return EmitNewArray(e);
            case "clr.newarr": return EmitNewArray(e);
            case "newArraySized":
            {
                // `IntArray(size)` (no init) -> a zero-filled BCL array (newarr zero-initializes).
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitExpr(e.GetProperty("size")); _il.Emit(OpCodes.Newarr, elem); return elem.MakeArrayType();
            }
            case "newArrayInit":
            {
                // `IntArray(size) { init }` -> `new elem[size]` + a fill loop `for i in 0..size-1: arr[i] = init(i)`.
                // The init is a Func<int,elem> delegate; box/unbox per its actual signature (primitive vs boxed lambda).
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitExpr(e.GetProperty("size")); var size = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, size);
                var fnType = EmitExpr(e.GetProperty("init")); var fn = _il.DeclareLocal(fnType); _il.Emit(OpCodes.Stloc, fn);
                // `Func<int,elem>` over an EMITTED elem (kotlin.Any / kotlin.UInt / a user class) is a TypeBuilder
                // instantiation whose .GetMethod / .GetParameters / .ReturnType all throw -- resolve Invoke via
                // InvokeOf, and read the param/return shapes off the delegate's type ARGS (GetGenericArguments is
                // safe on an instantiation; reflecting the Invoke signature is not).
                var invoke = InvokeOf(fnType);
                var ga = fnType.IsGenericType ? fnType.GetGenericArguments() : null;
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
                _il.Emit(OpCodes.Callvirt, invoke);                                              // init(i)
                if (rType != elem) { if (elem.IsValueType || elem.IsGenericParameter) _il.Emit(OpCodes.Unbox_Any, elem); else _il.Emit(OpCodes.Castclass, elem); }
                _il.Emit(OpCodes.Stelem, elem);                                                  // arr[i] = init(i)
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Br, top);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, arr); return arr.LocalType;
            }
            case "nullableOf":
            {
                // value `v` -> `new Nullable<elem>(v)` (the implicit T -> T? wrap).
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitExpr(e.GetProperty("e"));
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
                return nt;
            }
            case "default":
            case "clr.default":
            {
                // `default(T)` -> the zero value: ldnull for a reference type, else a zero-init local (initobj).
                var dt = MapType(e.GetProperty("type").GetString());
                if (!dt.IsValueType && !dt.IsGenericParameter) { _il.Emit(OpCodes.Ldnull); return dt; }
                var loc = _il.DeclareLocal(dt);
                _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, dt);
                _il.Emit(OpCodes.Ldloc, loc);
                return dt;
            }
            case "spreadConcat":
            case "clr.array.spread":
            {
                // `f(1, *a, 2)` -> new List<elem>(); Add(literal) / AddRange(spread); ToArray().
                var elem = MapType(e.GetProperty("elem").GetString());
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
            case "clr.ldelem":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index"));
                var elem = MapType(e.GetProperty("elem").GetString());
                _il.Emit(OpCodes.Ldelem, elem); return elem;
            }
            case "arraySet":
            case "clr.stelem":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index"));
                var svt = EmitExpr(e.GetProperty("value"));
                var selem = MapType(e.GetProperty("elem").GetString());
                // Storing a value-type/generic-param value into a REFERENCE-element array (`Array<Any?>[i] = aT`) needs a
                // box -- `stelem object` with an unboxed value on the stack is invalid (garbage/NullRef). The matching
                // read side is `a[i] as T` -> unbox.any. (Reference values and value-element arrays need no box.)
                // A GENERIC-PARAM element (`T[]`, stelem !T) must NOT box: `box T` yields object, and for a value-type
                // instantiation stelem !T then stores the reference bits as the value (garbage). Same guard as the
                // local/field/coroutine box sites (Emitter.Statements 27/38, Emitter.Coroutines 187).
                if (!selem.IsValueType && !selem.IsGenericParameter && svt != null && NeedsBoxToRef(svt)) _il.Emit(OpCodes.Box, svt);
                _il.Emit(OpCodes.Stelem, selem); return typeof(void);
            }
            case "arrayLen":
            case "clr.ldlen":
                EmitExpr(e.GetProperty("array")); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "forEachInline":
            {
                // `xs.forEach { it -> body }` (inline) -> enumerate src, bind `it` to a loop local, splice body.
                // Inlining (not a delegate) lets the body read/write enclosing locals without closure Ref cells.
                var elem = MapType(e.GetProperty("elem").GetString());
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
            case "isinst":
            {
                // `x is T` -> isinst T; (ref != null) as bool. A value-type / generic-param receiver MUST be boxed
                // first: `isinst` consumes an object reference off the stack, so reading an unboxed value type (or an
                // `!!T` whose runtime T is a value type) as a reference gives an NRE. This is what C# emits for
                // `element is X` when `element` is a generic `T` (box !!T; isinst X).
                var rt0 = EmitExpr(e.GetProperty("e"));
                if (NeedsBoxToRef(rt0)) _il.Emit(OpCodes.Box, rt0);
                _il.Emit(OpCodes.Isinst, MapType(e.GetProperty("type").GetString()));
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
                // A VALUE/GENERIC source flowing into a REFERENCE target must be boxed first (castclass on an
                // unboxed !!T / struct is invalid IL) -- `(x: T) as IComparable` in compareValues.
                var castSrc = EmitExpr(e.GetProperty("e"));
                var t = MapType(e.GetProperty("type").GetString());
                var toRef = !(t.IsValueType || t.IsGenericParameter);
                if (toRef && NeedsBoxToRef(castSrc)) _il.Emit(OpCodes.Box, castSrc);
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
            case "isinstRef":
            {
                // `x as? T` for reference T -> `isinst T` (leaves the ref, or null on mismatch). The result is a
                // reference (objref or null), so report `object` — never a generic-param type that would make a
                // downstream consumer (objMethod/objEq) wrongly re-box an already-reference value.
                var rtr = EmitExpr(e.GetProperty("e"));
                if (NeedsBoxToRef(rtr)) _il.Emit(OpCodes.Box, rtr);
                var t = MapType(e.GetProperty("type").GetString());
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
            case "clr.obj.method": return EmitObjMethod(e);
            case "strRepeat":
            {
                // `s.repeat(n)` -> string.Concat(Enumerable.Repeat(s, n)).
                EmitExpr(e.GetProperty("s")); EmitExpr(e.GetProperty("n"));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethod("Repeat").MakeGenericMethod(typeof(string)));
                _il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(System.Collections.Generic.IEnumerable<string>) }));
                return typeof(string);
            }
            case "strReversed":
            {
                // `s.reversed()` -> new string(Enumerable.Reverse(s).ToArray()).
                EmitExpr(e.GetProperty("s"));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "Reverse" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(char)));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "ToArray" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(char)));
                _il.Emit(OpCodes.Newobj, typeof(string).GetConstructor(new[] { typeof(char[]) }));
                return typeof(string);
            }
            case "split":
            {
                // `s.split(seps…)` -> s.Split(string[] seps, StringSplitOptions.None) |> ToList<string>.
                EmitExpr(e.GetProperty("recv"));
                var seps = e.GetProperty("seps").EnumerateArray().ToList();
                _il.Emit(OpCodes.Ldc_I4, seps.Count);
                _il.Emit(OpCodes.Newarr, typeof(string));
                for (int i = 0; i < seps.Count; i++)
                {
                    _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpr(seps[i]); _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Ldc_I4_0); // StringSplitOptions.None
                _il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("Split", new[] { typeof(string[]), typeof(StringSplitOptions) }));
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "ToList" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(string));
                _il.Emit(OpCodes.Call, toList);
                return typeof(System.Collections.Generic.List<string>);
            }
            case "associateWith":
            case "associateBy":
            {
                // associateWith{v}: d[x]=sel(x); associateBy{k}: d[sel(x)]=x.
                bool byKey = e.GetProperty("k").GetString() == "associateBy";
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var elemT = byKey ? vt : kt;                                  // src element type
                var selFn = typeof(Func<,>).MakeGenericType(elemT, byKey ? kt : vt);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("sel")); var sel = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, sel);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, d);
                    if (byKey) { _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(selFn)); _il.Emit(OpCodes.Ldloc, x); }
                    else { _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(selFn)); }
                    _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                });
                _il.Emit(OpCodes.Ldloc, d);
                return dt;
            }
            case "groupBy":
            {
                // groupBy{k}: d=Dictionary<K,List<E>>; for x: k=sel(x); d.GetOrAdd(k).Add(x).
                var kt = MapType(e.GetProperty("keyType").GetString());
                var elemT = MapType(e.GetProperty("elemType").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, listT);
                var selFn = typeof(Func<,>).MakeGenericType(elemT, kt);
                EmitExpr(e.GetProperty("sel")); var sel = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, sel);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                var k = _il.DeclareLocal(kt);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(selFn)); _il.Emit(OpCodes.Stloc, k);
                    var have = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Callvirt, dt.GetMethod("ContainsKey")); _il.Emit(OpCodes.Brtrue, have);
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                    _il.MarkLabel(have);
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Callvirt, dt.GetMethod("get_Item")); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, listT.GetMethod("Add"));
                });
                _il.Emit(OpCodes.Ldloc, d);
                return dt;
            }
            case "linqPartition":
            {
                // `partition { pred }` -> (matched, unmatched) : ValueTuple<List<T>, List<T>>.
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var predFn = typeof(Func<,>).MakeGenericType(elemT, typeof(bool));
                EmitExpr(e.GetProperty("pred")); var p = _il.DeclareLocal(predFn); _il.Emit(OpCodes.Stloc, p);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var m = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, m);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var u = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, u);
                var add = listT.GetMethod("Add");
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, p); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(predFn));
                    _il.Emit(OpCodes.Brfalse, elseL);
                    _il.Emit(OpCodes.Ldloc, m); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, add); _il.Emit(OpCodes.Br, end);
                    _il.MarkLabel(elseL); _il.Emit(OpCodes.Ldloc, u); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, add);
                    _il.MarkLabel(end);
                });
                var vtP = ResolveType("System.ValueTuple`2").MakeGenericType(listT, listT);
                _il.Emit(OpCodes.Ldloc, m); _il.Emit(OpCodes.Ldloc, u); _il.Emit(OpCodes.Newobj, vtP.GetConstructor(new[] { listT, listT }));
                return vtP;
            }
            case "linqWithIndex":
            {
                // `withIndex()` -> List<ValueTuple<int,T>>; `for ((i,v) in …)` destructures (component1/2 -> Item1/2).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var vtW = ResolveType("System.ValueTuple`2").MakeGenericType(typeof(int), elemT);
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(vtW);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var l = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, l);
                var i = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, i);
                var add = listT.GetMethod("Add");
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, l);
                    _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Newobj, vtW.GetConstructor(new[] { typeof(int), elemT }));
                    _il.Emit(OpCodes.Callvirt, add);
                    _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                });
                _il.Emit(OpCodes.Ldloc, l); return listT;
            }
            case "linqAssociate":
            {
                // `associate { it to (k,v) }` -> Dictionary<K,V> from a selector returning a Pair (ValueTuple<K,V>).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt2 = MapType(e.GetProperty("valType").GetString());
                var pairT = ResolveType("System.ValueTuple`2").MakeGenericType(kt, vt2);
                var selFn = typeof(Func<,>).MakeGenericType(elemT, pairT);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt2);
                EmitExpr(e.GetProperty("sel")); var f = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, f);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                var pair = _il.DeclareLocal(pairT);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(selFn)); _il.Emit(OpCodes.Stloc, pair);
                    _il.Emit(OpCodes.Ldloc, d);
                    _il.Emit(OpCodes.Ldloca, pair); _il.Emit(OpCodes.Ldfld, pairT.GetField("Item1"));
                    _il.Emit(OpCodes.Ldloca, pair); _il.Emit(OpCodes.Ldfld, pairT.GetField("Item2"));
                    _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                });
                _il.Emit(OpCodes.Ldloc, d); return dt;
            }
            case "linqScan":
            {
                // `scan/runningFold(init){acc,e -> }` -> List<acc> = [init, op(init,e0), op(prev,e1), …].
                var elemT = MapType(e.GetProperty("elem").GetString());
                var accT = MapType(e.GetProperty("accType").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(accT);
                var opFn = typeof(Func<,,>).MakeGenericType(accT, elemT, accT);
                EmitExpr(e.GetProperty("op")); var f = _il.DeclareLocal(opFn); _il.Emit(OpCodes.Stloc, f);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var l = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, l);
                EmitArg(e.GetProperty("init"), accT); var acc = _il.DeclareLocal(accT); _il.Emit(OpCodes.Stloc, acc);
                var add = listT.GetMethod("Add");
                _il.Emit(OpCodes.Ldloc, l); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Callvirt, add);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, InvokeOf(opFn)); _il.Emit(OpCodes.Stloc, acc);
                    _il.Emit(OpCodes.Ldloc, l); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Callvirt, add);
                });
                _il.Emit(OpCodes.Ldloc, l); return listT;
            }
            case "linqWindowed":
            {
                // `windowed(size)` -> List<List<T>> sliding windows (step 1, no partial windows).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var outerT = typeof(System.Collections.Generic.List<>).MakeGenericType(listT);
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(mm => mm.Name == "ToList" && mm.GetParameters().Length == 1).MakeGenericMethod(elemT);
                EmitExpr(e.GetProperty("src")); _il.Emit(OpCodes.Call, toList); var arr = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, arr);
                EmitExpr(e.GetProperty("size")); var size = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, size);
                _il.Emit(OpCodes.Newobj, outerT.GetConstructor(Type.EmptyTypes)); var outl = _il.DeclareLocal(outerT); _il.Emit(OpCodes.Stloc, outl);
                var getRange = listT.GetMethod("GetRange", new[] { typeof(int), typeof(int) });
                var getCount = listT.GetMethod("get_Count");
                var iw = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, iw);
                // test-at-top loop (the back-branch target has a known stack height via the fall-through from init).
                var top = _il.DefineLabel(); var done = _il.DefineLabel();
                _il.MarkLabel(top);
                _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Callvirt, getCount);
                _il.Emit(OpCodes.Bgt, done);     // (iw + size) > count -> stop
                _il.Emit(OpCodes.Ldloc, outl); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Callvirt, getRange); _il.Emit(OpCodes.Callvirt, outerT.GetMethod("Add"));
                _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, iw);
                _il.Emit(OpCodes.Br, top);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, outl); return outerT;
            }
            case "linqGetOrElse":
            {
                // `getOrElse(index){ default(index) }` -> in-bounds ? src[index] : default(index).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var defFn = typeof(Func<,>).MakeGenericType(typeof(int), elemT);
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(mm => mm.Name == "ToList" && mm.GetParameters().Length == 1).MakeGenericMethod(elemT);
                EmitExpr(e.GetProperty("src")); _il.Emit(OpCodes.Call, toList); var arr = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, arr);
                EmitExpr(e.GetProperty("index")); var idx = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, idx);
                EmitExpr(e.GetProperty("default")); var df = _il.DeclareLocal(defFn); _il.Emit(OpCodes.Stloc, df);
                var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Blt, elseL);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Callvirt, listT.GetMethod("get_Count")); _il.Emit(OpCodes.Bge, elseL);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Callvirt, listT.GetMethod("get_Item")); _il.Emit(OpCodes.Br, end);
                _il.MarkLabel(elseL); _il.Emit(OpCodes.Ldloc, df); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Callvirt, InvokeOf(defFn));
                _il.MarkLabel(end);
                return elemT;
            }
            case "mapNew":
            {
                // `mapOf(k to v, …)` -> new Dictionary<K,V> { [k]=v, … } via set_Item.
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                _il.Emit(OpCodes.Newobj, GenericCtor(dt));
                var setItem = GenericMethod(dt, "set_Item");
                foreach (var en in e.GetProperty("entries").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(en.GetProperty("key"), kt);
                    EmitArg(en.GetProperty("val"), vt);
                    _il.Emit(OpCodes.Callvirt, setItem);
                }
                return dt;
            }
            case "listGet":
            {
                var elem = MapType(e.GetProperty("elem").GetString());
                var lt = typeof(System.Collections.Generic.List<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("list")); EmitExpr(e.GetProperty("index"));
                // GenericMethod (not .GetMethod): when `elem` is the enclosing generic FUNCTION's type parameter T,
                // List<T> is a TypeBuilderInstantiation whose .GetMethod throws — route through TypeBuilder.GetMethod.
                _il.Emit(OpCodes.Callvirt, GenericMethod(lt, "get_Item"));
                return elem;
            }
            case "listSet":
            {
                var elem = MapType(e.GetProperty("elem").GetString());
                var lt = typeof(System.Collections.Generic.List<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("list")); EmitExpr(e.GetProperty("index")); EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Callvirt, GenericMethod(lt, "set_Item"));
                return typeof(void);
            }
            case "mapGet":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                EmitArg(e.GetProperty("key"), kt);
                _il.Emit(OpCodes.Callvirt, GenericMethod(dt, "get_Item"));
                return vt;
            }
            case "mapSet":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                EmitArg(e.GetProperty("key"), kt);
                EmitArg(e.GetProperty("value"), vt);
                _il.Emit(OpCodes.Callvirt, GenericMethod(dt, "set_Item"));
                return typeof(void);
            }
            case "mapSize":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                _il.Emit(OpCodes.Callvirt, GenericMethod(dt, "get_Count"));
                return typeof(int);
            }
            case "setNew":
            {
                // `setOf(...)` -> new HashSet<elem> { ... } via repeated Add (Add returns bool -> pop).
                var elem = MapType(e.GetProperty("elem").GetString());
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
            case "linqSum":
            {
                // `sum()` -> the non-generic Enumerable.Sum(IEnumerable<elem>) overload for that numeric element.
                var elem = MapType(e.GetProperty("elem").GetString());
                var ienum = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                var mi = typeof(System.Linq.Enumerable).GetMethod("Sum", new[] { ienum });
                EmitExpr(e.GetProperty("src"));
                _il.Emit(OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "linqSumOf":
            {
                // `sumOf { selector }` -> Sum<T>(IEnumerable<T>, Func<T,selRet>); pick the overload by selector return type.
                var t = MapType(e.GetProperty("elem").GetString());
                var selRet = MapType(e.GetProperty("selRet").GetString());
                var def = typeof(System.Linq.Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "Sum" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2
                             && m.GetParameters()[1].ParameterType.IsGenericType
                             && m.GetParameters()[1].ParameterType.GetGenericArguments().Last() == selRet)
                    .MakeGenericMethod(t);
                EmitExpr(e.GetProperty("src"));
                EmitArg(e.GetProperty("sel"), def.GetParameters()[1].ParameterType);
                _il.Emit(OpCodes.Call, def);
                return def.ReturnType;
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
            case "tupleNew":
            {
                // `a to b` -> new System.ValueTuple<A,B>(a, b) (value type; newobj leaves the struct on the stack).
                var elems = e.GetProperty("elems").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var vt = ResolveType("System.ValueTuple`" + elems.Length).MakeGenericType(elems);
                var args = e.GetProperty("args").EnumerateArray().ToList();
                for (int i = 0; i < args.Count; i++) EmitArg(args[i], elems[i]);
                _il.Emit(OpCodes.Newobj, vt.GetConstructor(elems));
                return vt;
            }
            case "tupleItem":
            {
                // `.first`/`.second`/`.third` -> ValueTuple ItemN field (public field, not a property).
                var vt = MapType(e.GetProperty("tupleType").GetString());
                EmitExpr(e.GetProperty("recv"));
                var fld = vt.GetField("Item" + e.GetProperty("index").GetInt32());
                _il.Emit(OpCodes.Ldfld, fld);
                return fld.FieldType;
            }
            case "delegateNew":
            {
                // Non-capturing lambda: bind the lifted static method into a Func/Action delegate.
                var ft = MapType(e.GetProperty("funcType").GetString());
                var mb = FindStatic(e.GetProperty("method").GetString());
                // A GENERIC lifted lambda (e.g. the comparator inside a generic `sort<T>`) MUST be instantiated with its
                // typeArgs before Ldftn -- loading the open generic-method-DEFINITION's ftn throws "the method itself or
                // the containing type is not fully instantiated" at runtime.
                MethodInfo target = (e.TryGetProperty("typeArgs", out var dta) && dta.GetArrayLength() > 0 && mb.IsGenericMethodDefinition)
                    ? mb.MakeGenericMethod(dta.EnumerateArray().Select(x => MapType(x.GetString())).ToArray())
                    : mb;
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, target);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "boundDelegateNew":
            {
                // `obj::method` -> a delegate bound to the receiver. ldvirtftn needs the object twice (dup); a
                // final method uses ldftn (the target stays on the stack as the delegate's first ctor arg).
                var ft = MapType(e.GetProperty("funcType").GetString());
                var mb = FindMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString());
                EmitExpr(e.GetProperty("recv"));
                if (e.GetProperty("virtual").GetBoolean()) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mb); }
                else _il.Emit(OpCodes.Ldftn, mb);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "boundClrDelegateNew":
            {
                // `netObj::method` -> a delegate bound to a .NET instance method (resolved by reflection).
                var ft = MapType(e.GetProperty("funcType").GetString());
                var type = ClrRef(e.GetProperty("clrType").GetString());
                var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => ClrRef(a.GetString())).ToArray();
                var mi = type.GetMethod(e.GetProperty("method").GetString(),
                    BindingFlags.Public | BindingFlags.Instance, null, argTypes, null)
                    ?? type.GetMethod(e.GetProperty("method").GetString());
                EmitExpr(e.GetProperty("recv"));
                if (e.GetProperty("virtual").GetBoolean()) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mi); }
                else _il.Emit(OpCodes.Ldftn, mi);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "delegateInvoke":
            {
                // A splice's invocation of a lambda PARAM -> inline the caller's lambda body (binding its param to the
                // invoke arg) right here, so a non-local `return` in it returns from THIS (the caller's) method.
                var recv0 = e.GetProperty("recv");
                if (recv0.TryGetProperty("k", out var rk) && rk.GetString() == "local"
                    && _inlineLambdas.TryGetValue(recv0.GetProperty("name").GetString(), out var lam))
                {
                    var iargs = e.GetProperty("args").EnumerateArray().ToList();
                    var had = _inlineSubst.TryGetValue(lam.lamParam, out var prev);
                    if (iargs.Count > 0) _inlineSubst[lam.lamParam] = iargs[0];   // bind the lambda's param to the invoke arg
                    EmitSplicedStmts(lam.body);
                    if (had) _inlineSubst[lam.lamParam] = prev; else _inlineSubst.Remove(lam.lamParam);
                    return typeof(void);
                }
                var ft = MapType(e.GetProperty("funcType").GetString());
                EmitExpr(e.GetProperty("recv"));
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                _il.Emit(OpCodes.Callvirt, InvokeOf(ft));
                return FuncRetType(e.GetProperty("funcType").GetString());
            }
            case "inlineSplice": return EmitInlineSplice(e);
            case "closureNew":
            {
                // Capturing lambda: `new Closure(captures)` then bind its `invoke` instance method as a delegate.
                var ct = _types[e.GetProperty("closureType").GetString()];
                ConstructorInfo ctor = ct.Ctor;
                MethodInfo invoke = ct.Methods[e.GetProperty("method").GetString()];
                // A closure that captures an enclosing type parameter is a GENERIC class: construct it with the actual
                // type arguments (the enclosing method/class type params, live here) and re-anchor its ctor/invoke onto
                // the constructed type (TypeBuilder.GetX — a TypeBuilder instantiation can't resolve members directly).
                if (e.TryGetProperty("typeArgs", out var taProp) && taProp.GetArrayLength() > 0)
                {
                    var typeArgs = taProp.EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                    var constructed = ct.TB.MakeGenericType(typeArgs);
                    ctor = TypeBuilder.GetConstructor(constructed, ct.Ctor);
                    invoke = TypeBuilder.GetMethod(constructed, invoke);
                }
                foreach (var c in e.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ctor);              // closure instance is the delegate target
                _il.Emit(OpCodes.Ldftn, invoke);
                var ft = MapType(e.GetProperty("funcType").GetString());
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "samNew":
            {
                // SAM conversion `Comparator { … }` -> `new <Sam>(captures)` -- a synthetic class IMPLEMENTING the fun
                // interface (no delegate). The instance IS the interface value (implicit upcast at the use site).
                var ct = _types[e.GetProperty("samType").GetString()];
                ConstructorInfo ctor = ct.Ctor;
                Type result = ct.TB;
                if (e.TryGetProperty("typeArgs", out var staProp) && staProp.GetArrayLength() > 0)
                {
                    var typeArgs = staProp.EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                    result = ct.TB.MakeGenericType(typeArgs);
                    ctor = TypeBuilder.GetConstructor(result, ct.Ctor);
                }
                foreach (var c in e.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ctor);
                return result;
            }
            case "concat": return EmitConcat(e);
            case "clr.str.concat": return EmitConcat(e);
            case "cond": return EmitCond(e);
            case "clrNew": return EmitClrNew(e);
            case "clrStatic": return EmitClrCall(e, instance: false);
            case "clrInstance": return EmitClrCall(e, instance: true);
            case "clrPropGet": return EmitClrPropGet(e);
            case "clrPropSet": return EmitClrPropSet(e);
            case "clr.newobj": return EmitNativeClrNewObj(e);
            case "clr.call": return EmitNativeClrCall(e);
            case "clr.ldfld": return EmitNativeClrFieldGet(e, isStatic: false);
            case "clr.ldsfld": return EmitNativeClrFieldGet(e, isStatic: true);
            case "clr.stfld": return EmitNativeClrFieldSet(e, isStatic: false);
            case "clr.stsfld": return EmitNativeClrFieldSet(e, isStatic: true);
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
            case "clr.stackalloc":
            {
                // `localloc` a zero-initialized stack buffer of `count * sizeof(elem)` bytes, leaving its pointer.
                // (Unverifiable, like C#'s own stackalloc.)
                var elem = MapType(e.GetProperty("elem").GetString());
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
            case "clr.stack.get":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "stackSet":
            case "clr.stack.set":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "stackAsSpan":
            case "clr.stack.asSpan":
            {
                // `new System.Span<T>(void* ptr, int length)` over the stack buffer -> a real Span for .NET APIs.
                var elem = MapType(e.GetProperty("elem").GetString());
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
                var elem = MapType(e.GetProperty("elem").GetString());
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "byrefStore":
            {
                // Write through a byref local: ldloc the pointer, push the value, stobj.
                _il.Emit(OpCodes.Ldloc, _locals[e.GetProperty("local").GetString()]);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "unsupportedExpr": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + e.GetProperty("of").GetString());
            default: throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
    }
}
