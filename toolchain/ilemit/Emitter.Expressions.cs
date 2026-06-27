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
    Type EmitExpr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "const": return EmitConst(e);
            case "clr.const": return EmitConst(e);
            case "this":
                if (_coThis != null) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, _coThis); return _coThis.FieldType; }   // instance coroutine: captured receiver
                _il.Emit(OpCodes.Ldarg_0); return typeof(object);
            case "coSuspendedSentinel":   // kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
                { var f = ResolveType("DotKt.Coroutines.Intrinsics").GetField("COROUTINE_SUSPENDED"); _il.Emit(OpCodes.Ldsfld, f); return typeof(object); }
            case "sequenceNew": return EmitSequenceSm(e);
            case "coSelfCont":   // the coroutine's own continuation (the SM), as a typed Continuation<T>: new TypedCont<T>(this)
                {
                    var tk = MapType(e.GetProperty("resultType").GetString());
                    var typed = ResolveType("DotKt.Coroutines.TypedCont`1").MakeGenericType(tk);
                    var contObj = ResolveType("DotKt.Coroutines.Continuation`1").MakeGenericType(typeof(object));
                    _il.Emit(OpCodes.Ldarg_0);   // the SM (Continuation<object>)
                    _il.Emit(OpCodes.Newobj, CtorOf(typed));
                    return typed;
                }
            case "coContext":   // kotlin.coroutines.coroutineContext -> the SM's own Context (the SM is Continuation<object>)
                {
                    var contObj = ResolveType("DotKt.Coroutines.Continuation`1").MakeGenericType(typeof(object));
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Callvirt, contObj.GetMethod("get_Context"));
                    return ResolveType("DotKt.Coroutines.CoroutineContext");
                }
            case "coSelfCancellable":   // the SM as a CancellableContinuation<T>: new CancellableCont<T>(new TypedCont<T>(this))
                {
                    var tk = MapType(e.GetProperty("resultType").GetString());
                    var typed = ResolveType("DotKt.Coroutines.TypedCont`1").MakeGenericType(tk);
                    var cancel = ResolveType("DotKtx.Coroutines.CancellableCont`1").MakeGenericType(tk);
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Newobj, CtorOf(typed));
                    _il.Emit(OpCodes.Newobj, CtorOf(cancel));
                    return cancel;
                }
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                // Inside a cross-module inline splice, a callee param reference emits the bound arg/value instead.
                if (_inlineSubst.TryGetValue(name, out var sub)) return EmitExpr(sub);
                // In a coroutine, a param/live-local reference is a load of the SM struct field.
                if (_coFields != null && _coFields.TryGetValue(name, out var cf)) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, cf); return cf.FieldType; }
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
                EmitExpr(e.GetProperty("recv"));
                var fb = ResolveField(fon, fnm, out var ft);
                _il.Emit(OpCodes.Ldfld, fb);
                return RetOr(e, ft);
            }
            case "setFieldExpr":
            {
                EmitExpr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out _));
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
                var ti = _types[open];
                var nargs = e.GetProperty("args");
                var ctor = SelectCtor(ti, nargs.GetArrayLength());
                foreach (var a in nargs.EnumerateArray()) EmitExpr(a);
                // Constructed user generic `Box<int>` -> resolve the ctor onto the instantiation (static helper).
                _il.Emit(OpCodes.Newobj, constructed != null ? TypeBuilder.GetConstructor(constructed, ctor) : (ConstructorInfo)ctor);
                return constructed ?? (Type)ti.TB;
            }
            case "callInstance":
            {
                var cisig = e.TryGetProperty("sig", out var ciEl) && ciEl.ValueKind == JsonValueKind.String ? ciEl.GetString() : null;
                var m0 = ResolveMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString(), out var rt, cisig);
                var m = ApplyTypeArgs(m0, e, out var mrt, out var mps);
                EmitExpr(e.GetProperty("recv"));
                if (m == m0) EmitCallArgs(e.GetProperty("args"), m); else EmitArgsTyped(e.GetProperty("args"), mps);
                _il.Emit(e.GetProperty("virtual").GetBoolean() ? OpCodes.Callvirt : OpCodes.Call, m);
                return RetOr(e, m == m0 ? rt : mrt);
            }
            case "constrainedCall":
            {
                // `a.compareTo(b)` on a Comparable -> `constrained. recvType; callvirt IComparable<T>::CompareTo`.
                // The receiver must be a managed pointer; `constrained.` then dispatches for value/ref/generic T.
                var recvType = MapType(e.GetProperty("recvType").GetString());
                var iface = MapType(e.GetProperty("iface").GetString());
                var mi = InterfaceMethodOn(iface, e.GetProperty("method").GetString());
                EmitAddr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("arg"));
                _il.Emit(OpCodes.Constrained, recvType);
                _il.Emit(OpCodes.Callvirt, mi);
                return mi.ReturnType;
            }
            case "callStatic":
            {
                var name = e.GetProperty("method").GetString();
                var csig = e.TryGetProperty("sig", out var csEl) && csEl.ValueKind == JsonValueKind.String ? csEl.GetString() : null;
                // owner present -> a static method on that named class (companion); else a file-class sibling.
                var mb = ApplyTypeArgs((e.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String)
                    ? FindMethod(ow.GetString(), name, csig) : FindStatic(name, csig), e, out var srt, out var sps);
                if (e.TryGetProperty("typeArgs", out _)) EmitArgsTyped(e.GetProperty("args"), sps);
                else EmitCallArgs(e.GetProperty("args"), mb);
                _il.Emit(OpCodes.Call, mb);
                return RetOr(e, srt);
            }
            case "staticField":
            {
                var f = FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString());
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
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stsfld, FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString()));
                return typeof(void);
            }
            case "console":
            {
                var cargs = e.GetProperty("args").EnumerateArray().ToList();
                if (cargs.Count == 0)   // bare `println()` -> Console.WriteLine() (blank line)
                {
                    _il.Emit(OpCodes.Call, typeof(Console).GetMethod(e.GetProperty("method").GetString(), Type.EmptyTypes));
                    return typeof(void);
                }
                var t = EmitExpr(cargs[0]);
                if (NeedsBoxToRef(t)) _il.Emit(OpCodes.Box, t);
                _il.Emit(OpCodes.Call, typeof(Console).GetMethod(e.GetProperty("method").GetString(), new[] { typeof(object) }));
                return typeof(void);
            }
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
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index")); EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stelem, MapType(e.GetProperty("elem").GetString())); return typeof(void);
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
                var ienumrT = typeof(System.Collections.Generic.IEnumerator<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("src"));
                // GenericMethod (not .GetMethod): when `elem` is the enclosing generic FUNCTION's type parameter T,
                // IEnumerable<T>/IEnumerator<T> are TypeBuilderInstantiations whose .GetMethod throws — route those
                // through TypeBuilder.GetMethod. (Concrete elem types resolve normally.)
                _il.Emit(OpCodes.Callvirt, GenericMethod(ienumT, "GetEnumerator"));
                var en = _il.DeclareLocal(ienumrT); _il.Emit(OpCodes.Stloc, en);
                var lv = _il.DeclareLocal(elem); _locals[e.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(e), start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, en);
                _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext"));
                _il.Emit(OpCodes.Brfalse, end);
                _il.Emit(OpCodes.Ldloc, en);
                _il.Emit(OpCodes.Callvirt, GenericMethod(ienumrT, "get_Current"));
                _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in e.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return typeof(void);
            }
            case "isinst":
            {
                // `x is T` -> isinst T; (ref != null) as bool.
                EmitExpr(e.GetProperty("e"));
                _il.Emit(OpCodes.Isinst, MapType(e.GetProperty("type").GetString()));
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Cgt_Un);
                return typeof(bool);
            }
            case "cast":
            {
                // `x as T` / smart-cast downcast -> castclass (reference) or unbox.any (value type).
                EmitExpr(e.GetProperty("e"));
                var t = MapType(e.GetProperty("type").GetString());
                _il.Emit(t.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, t);
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
                EmitExpr(e.GetProperty("e"));
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
                    if (byKey) { _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Ldloc, x); }
                    else { _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); }
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
                    _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, k);
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
                    _il.Emit(OpCodes.Ldloc, p); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, predFn.GetMethod("Invoke"));
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
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, pair);
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
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, opFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, acc);
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
                _il.MarkLabel(elseL); _il.Emit(OpCodes.Ldloc, df); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Callvirt, defFn.GetMethod("Invoke"));
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
                // (mirrors the "return" statement, incl. the protected-region leave).
                if (_tryStack.Count > 0)
                {
                    var ctx = _tryStack.Peek();
                    if (e.TryGetProperty("value", out var trv)) { EmitExpr(trv); if (ctx.result != null) _il.Emit(OpCodes.Stloc, ctx.result); else _il.Emit(OpCodes.Pop); }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (e.TryGetProperty("value", out var rv))
                    {
                        var got = EmitExpr(rv);
                        if (got != null && _methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == typeof(Nullable<>)
                            && _methodRetType.GetGenericArguments()[0] == got)
                            _il.Emit(OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
                    }
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
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, mb);
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
            case "concat": return EmitConcat(e);
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
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "stackSet":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "stackAsSpan":
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
