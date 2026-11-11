// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Cir;

// EmitExpr: the BIR expression -> CIL evaluator (returns the .NET Type left on the stack).
sealed partial class Emitter
{
    // ---- expressions: push one value, return its CLR type ----
    // (#400) There is NO runtime-reflection dispatch here any more. ilemit used to emit
    // `recv.GetType().GetMethod(name).Invoke(recv, [args...])` for a call it could not link statically — a name-only
    // binder that preserved no overload, declaring slot, explicit-interface implementation or generic arity, and that
    // produced an opaque NullReferenceException whenever the runtime type had no method of that name (the ordinary
    // case for a BCL-backed collection). Every producer of that path is gone: the Kotlin collection members with no
    // BCL slot now have a physical representation authored in bir2cir — the ClrCollectionDefaults dispatchers plus a
    // real DotKt.Runtime.CompilerServices.Kotlin*Slots interface slot on each implementer — so a member ilemit cannot
    // link is an upstream drop and must fail loudly.

    Type EmitExpr(ICirExpression expression)
    {
        _ctxNode = expression.Tag;
        switch (expression)
        {
            case CirConstant e: return EmitConst(e);
            case CirThis:
                _il.Emit(OpCodes.Ldarg_0); return Bcl("System.Object");
            case CirLocal e:
            {
                var name = e.Name;
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return _argTypes[name]; }
                throw new NotSupportedException("load unknown var " + name);
            }
            case CirFieldRead e:
            {
                var fnm = e.Name;
                // W1-S3 (#46 / #121) CONSUME-ONLY: an EXTERNAL owner's backing field is PRIVATE cross-assembly, so the
                // read goes through the public getter — bir2cir (ClrMemberResolution) decided that KIND and stamped
                // `member:"accessor"` + the resolved accessor memberRef + dispatch. ilemit no longer reinterprets a
                // `field` into a `get_` accessor (the ExternalPropAccessor probe is gone). Absent = a LOCAL owner (its
                // backing field is directly accessible) or a genuine public @ClrField -> the direct Ldfld path below.
                // (No Throwable.message/cause correction here either: bir2cir substitutes those to clrPropGet upstream.)
                if (e.Member.OrDefault() == "accessor")
                {
                    var ftype = ClrRef(e.OwnerType);
                    var getter = LinkClrMethod(ftype, e.Accessor.Value,
                        new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: true);
                    if (IsValueType(ftype)) EmitAddr(e.Recv); else EmitExpr(e.Recv);
                    EmitClrDispatch(getter, RequireDispatch(e.Dispatch, ftype, "field"), ftype);
                    var getterReturn = ReturnTypeOf(getter);
                    return RetOr(e.Ret, getterReturn);
                }
                var fon = ParseOwnerSlot(e.OwnerType);
                FieldInfo fb; Type ft;
                if (PrimaryFromRef(e.MemberRef.OrDefault()) is FieldInfo referencedField) { fb = referencedField; ft = FieldTypeOf(fb); }
                else fb = ResolveLocalField(fon, fnm, out ft, "field read");
                // ECMA-335 ldfld consumes a managed pointer for a value-type receiver.  Property access already takes
                // this path through EmitAddr above; a genuine public CLR field must do the same.  The field token is the
                // physical source of truth here (including a referenced constructed generic owner), so this is direct
                // CIR -> CIL emission rather than reconstruction of Kotlin member semantics.
                if (IsValueType(ClrRef(e.OwnerType))) EmitAddr(e.Recv);
                else EmitExpr(e.Recv);
                MaybeVolatile(fb, e.Volatile.OrDefault());
                EmitField(_il, OpCodes.Ldfld, fb);
                return RetOr(e.Ret, ft);
            }
            case CirFieldStoreExpression e:
            {
                var snm = e.Name;
                if (e.Member.OrDefault() == "accessor")
                {
                    var stype = ClrRef(e.OwnerType);
                    var setter = LinkClrMethod(stype, e.Accessor.Value,
                        new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: true);
                    if (IsValueType(stype)) EmitAddr(e.Recv); else EmitExpr(e.Recv);
                    EmitStoreCoerced(e.Value, SetterValueType(setter));
                    EmitClrDispatch(setter, RequireDispatch(e.Dispatch, stype, "setFieldExpr"), stype);
                    return Bcl("System.Void");
                }
                var son = ParseOwnerSlot(e.OwnerType);
                FieldInfo sfefld; Type sfet;
                if (PrimaryFromRef(e.MemberRef.OrDefault()) is FieldInfo referencedField)
                {
                    sfefld = referencedField;
                    sfet = FieldTypeOf(sfefld);
                }
                else sfefld = ResolveLocalField(son, snm, out sfet, "field write");
                if (IsValueType(ClrRef(e.OwnerType))) EmitAddr(e.Recv);
                else EmitExpr(e.Recv);
                EmitStoreCoerced(e.Value, sfet);
                MaybeVolatile(sfefld, e.Volatile.OrDefault());
                EmitField(_il, OpCodes.Stfld, sfefld);
                return Bcl("System.Void");
            }
            case CirLateinitGet e:
            {
                // `lateinit var` read: load the field; if still null (uninitialized), throw.
                Type lateinitType;
                if (e.Value.IsPresent)
                    lateinitType = EmitExpr(e.Value.Value);
                else
                {
                    FieldInfo fld;
                    if (e.FieldRef.IsPresent)
                        fld = RequiredRef<FieldInfo>(e.FieldRef.Value, "lateinitGet");
                    else
                        fld = ResolveLocalField(ParseOwnerSlot(e.OwnerType.Value), e.Name.Value, out _, "lateinit field read");
                    if (e.Static.OrDefault())
                    {
                        MaybeVolatile(fld, e.Volatile.OrDefault());
                        EmitField(_il, OpCodes.Ldsfld, fld);
                    }
                    else
                    {
                        EmitExpr(e.Recv.Value);
                        MaybeVolatile(fld, e.Volatile.OrDefault());
                        EmitField(_il, OpCodes.Ldfld, fld);
                    }
                    lateinitType = FieldTypeOf(fld);
                }
                _il.Emit(OpCodes.Dup);
                var ok = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, ok);
                _il.Emit(OpCodes.Pop);
                // bir2cir has resolved Kotlin's failure semantics to an ordinary, exactly-bound constructor expression.
                // Emitting it here is the same one-to-one CIR path as any other nested expression.
                EmitExpr(e.Exception);
                _il.Emit(OpCodes.Throw);
                _il.MarkLabel(ok);
                return lateinitType;
            }
            case CirNew e:
            {
                var (open, constructed) = ParseOwnerSlot(e.Type);
                var nargs = e.Args;
                if (!_types.TryGetValue(open, out var ti))
                {
                    // External type (e.g. `new kotlin.ranges.IntRange(1,3)` from an APP linking the rt where IntRange
                    // lives): bir2cir resolved its physical declaration and stamped `memberRef`. Link that identity
                    // exactly; this path must not choose a constructor from the argument expressions.
                    var ext = constructed ?? ResolveType(open);
                    var ctorE = LinkClrCtor(ext, e.MemberRef.OrDefault());
                    EmitArgs(nargs, ParametersOf(ctorE));
                    EmitConstructor(_il, OpCodes.Newobj, ctorE);
                    return ext;
                }
                var ctor = LinkLocalCtor(ti, e.LocalCtorIndex);
                EmitNewArgs(e.ArgTypes, nargs);
                // Constructed user generic `Box<int>` -> resolve the ctor onto the instantiation (static helper).
                EmitConstructor(_il, OpCodes.Newobj, constructed != null ? AnchorConstructor(constructed, ctor) : (ConstructorInfo)ctor);
                return constructed ?? (Type)ti.TB;
            }
            case CirCallInstance e:
            {
                var cisig = CallSignature(e.CalleeParams, e.Sig);
                MethodInfo m0; Type rt;
                var ciOwner = ParseOwnerSlot(e.OwnerType);
                // An external owner names the member (#370); only a method this compilation emits is still found
                // by signature here, which is the local axis (#395). A resolution failure is now a HARD error: the
                // former `catch (NotSupportedException) … EmitDynamicCall` fallback (#400) turned an unresolvable
                // member into a runtime name lookup, which is not a call identity at all.
                if (e.MemberRef.IsPresent)
                {
                    m0 = RequiredRef<MethodInfo>(e.MemberRef.Value, "method");
                    rt = m0.ReturnType;
                }
                else m0 = ResolveMethod(ciOwner, e.Method, out rt, cisig, MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault());
                var m = ApplyTypeArgs(m0, MethodTypeArguments(e.TypeArgs), out var mrt, out var mps);
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
                EmitExpr(e.Recv);
                if (m == m0) EmitCallArgs(e.Args, m); else EmitArgsTyped(e.Args, mps, m);
                EmitMethod(_il, e.Virtual.OrDefault() ? OpCodes.Callvirt : OpCodes.Call, m);
                return CoerceReturn(e.Ret, m == m0 ? rt : mrt);
            }
            case CirConstrainedCall e:
            {
                // An interface member invoked on a generic-parameter receiver: collection operations, CompareTo and
                // property accessors all use this physical dispatch. A plain callvirt on a padded interface owner can
                // mis-dispatch; `constrained. !!T; callvirt Interface::Member` dispatches on the receiver's actual
                // type. `args` is the sole current CIR operand vector.
                var ccArgs = e.Args;
                var recvType = MapType(e.RecvType);
                // CIR already carries the exact constructed constraint owner. The only distinction here is token
                // mechanics: a type emitted in this module must be linked through its TypeBuilder registry, while
                // a referenced interface uses its reflected constructed MethodInfo. This does not re-select an
                // owner or overload; bir2cir has already made both decisions.
                var ifaceNode = e.Iface;
                var ifaceSpec = ReadFqn(ifaceNode);
                var ccSig = CallSignature(e.CalleeParams, e.Sig);
                var ccArity = MethodTypeArguments(e.TypeArgs).Length;
                Type declaredReturn;
                MethodInfo mi0;
                if (ifaceSpec != null && _types.ContainsKey(ifaceSpec.Name))
                    mi0 = ResolveMethod(ParseOwnerSlot(ifaceNode), e.Method,
                        out declaredReturn, ccSig, ccArity, e.CalleeRet.OrDefault());
                else
                {
                    mi0 = RequiredRef<MethodInfo>(e.MemberRef.OrDefault(), "an external constrained-call interface slot");
                    declaredReturn = ReturnTypeOf(mi0);
                }
                // The receiver being a type variable changes the DISPATCH, not the member: a generic member still
                // needs its `typeArgs` instantiation, exactly as the callInstance arm applies it. Without this a
                // `fun <R> pick(a: R, b: R): R` called on a `!!T` receiver emitted a callvirt on the generic
                // method DEFINITION.
                var mi = ApplyTypeArgs(mi0, MethodTypeArguments(e.TypeArgs), out var ccRet, out var ccPs);
                EmitAddr(e.Recv);
                // The RECORDED parameter vector wins whenever there is one: `GetParameters()` on a MethodBuilder
                // whose declaring TypeBuilder is not baked yet is not answerable, and a constrained call whose
                // constraint is an EMITTED Kotlin interface resolves to exactly such a builder. Reflection is the
                // fallback, for a referenced owner that has no recorded vector.
                if (ccPs != null) EmitArgsTyped(ccArgs, ccPs, mi); else EmitArgs(ccArgs, ParametersOf(mi));
                _il.Emit(OpCodes.Constrained, recvType);
                EmitMethod(_il, OpCodes.Callvirt, mi);
                // …and the object-erasure conversion is a property of the call, not of how its receiver was addressed.
                // Any other physical/result-view conversion is already an explicit enclosing CIR node.
                // ResolveMethod's stack type already includes the constructed owner arguments. The anchored
                // MethodInfo retains the open declaration type for its metadata token; that !N is not a caller slot.
                return CoerceReturn(e.Ret, mi == mi0 ? declaredReturn : ccRet);
            }
            case CirCallStatic e:
            {
                var name = e.Method;
                var csig = CallSignature(e.CalleeParams, e.Sig);
                // owner present -> a static method on that complete physical owner; else a file-class sibling.
                // #199/#204 — `owner:null` remains the substitution/recognition axis, while mandatory `calleeOwner`
                // is the exact dispatch axis. Never global-search by method name: a missing/scoped miss is malformed
                // CIR and must fail loud instead of silently binding another file class's same-simple-name function.
                // A call into a previously-compiled DotKt assembly is an external member like any other, and the
                // reference is consulted FIRST: running the search anyway would let a missing or ambiguous
                // candidate set abort before the answer that was already resolved could be read. A member of THIS
                // compilation carries no reference, and the search below still answers for it.
                bool exactConstructedOwner = false;
                var resolved = PrimaryFromRef(e.MemberRef.OrDefault()) as MethodInfo;
                if (resolved == null)
                {
                    // The owner selected in CIR is complete. A bare generic TypeDef here is malformed CIR; ilemit
                    // must not invent a representative instantiation or reconstruct a retired compiler ABI.
                    if (e.Owner.HasValue && SlotName(e.Owner.Value) is string ownm)
                    {
                        var ow = e.Owner.Value;
                        exactConstructedOwner = ow is DotKt.Bir.TypeNode.Fqn { Args: not null };
                        resolved = exactConstructedOwner
                            ? ResolveMethod(ParseOwnerSlot(ow), name, out _, csig, MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault())
                            : FindMethod(ownm, name, csig, MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault());
                    }
                    else
                        resolved = FindCalleeOwnedStatic(e.CalleeOwner.OrDefault(), "callStatic", name, csig, MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault());
                }
                var mb = ApplyTypeArgs(resolved, MethodTypeArguments(e.TypeArgs), out var srt, out var sps);
                if (e.TypeArgs.IsPresent) EmitArgsTyped(e.Args, sps, mb);
                else EmitCallArgs(e.Args, mb);
                EmitMethod(_il, OpCodes.Call, mb);
                return CoerceReturn(e.Ret, srt);
            }
            case CirStaticField e:
            {
                // ownerType is already the final CIR TypeSpec. Preserve it exactly: generic-owner companion statics
                // have already moved to their explicit non-generic carrier, and arbitrary CIR must not be reinterpreted.
                // An external owner names its field (#370); only a field this compilation emits is still found by
                // name, which is the local axis (#395). The field's own type is what the read is typed by either way.
                Type ft;
                FieldInfo f;
                if (e.FieldRef.IsPresent)
                {
                    f = RequiredRef<FieldInfo>(e.FieldRef.Value, "field");
                    ft = f.FieldType;
                }
                else f = ResolveLocalField(ParseOwnerSlot(e.OwnerType), e.Name, out ft, "static field read");
                MaybeVolatile(f, e.Volatile.OrDefault());
                EmitField(_il, OpCodes.Ldsfld, f);
                return ft;
            }
            case CirClrStaticField e:
            {
                var cf = RequiredRef<FieldInfo>(e.FieldRef, "clrStaticField");
                EmitField(_il, OpCodes.Ldsfld, cf);
                return FieldTypeOf(cf);
            }
            case CirStaticFieldSet e:
            {
                // Preserve the exact CIR owner, for the same reason as the `staticField` read above.
                FieldInfo sfsf;
                Type sfsft;
                if (e.FieldRef.IsPresent)
                {
                    sfsf = RequiredRef<FieldInfo>(e.FieldRef.Value, "staticFieldSet");
                    sfsft = FieldTypeOf(sfsf);
                }
                else
                    sfsf = ResolveLocalField(ParseOwnerSlot(e.OwnerType), e.Name, out sfsft, "static field write");
                EmitStoreCoerced(e.Value, sfsft);
                MaybeVolatile(sfsf, e.Volatile.OrDefault());
                EmitField(_il, OpCodes.Stsfld, sfsf);
                return Bcl("System.Void");
            }
            case CirBinary e: return EmitBin(e);
            case CirObjectEqual e: return EmitObjEq(e);
            case CirUnary e: return EmitUn(e);
            case CirConvert e: return EmitConv(e);
            case CirValueBlock e:
            {
                // Inlined scope function: run the spliced statements, then yield the result expression.
                foreach (var st in e.Stmts) EmitStmt(st);
                return EmitExpr(e.Result);
            }
            case CirNewList e:
            {
                // `listOf(...)` -> new List<elem> { ... } via repeated Add.
                var elem = MapType(e.Elem);
                // The members a collection literal builds through are stated by the pass that minted the node,
                // so the emitter stops deriving them from the constructed type.
                var listCtor = RequiredRef<ConstructorInfo>(e.CtorRef, "newList");
                var add = RequiredRef<MethodInfo>(e.AddRef, "newList");
                // …and so is the type it constructs: the constructor's OWN declaring type, anchored on the
                // instantiation its reference states. Naming `List`1` here would be this layer deciding which BCL
                // type a Kotlin list literal becomes, which is the producer's decision and is already recorded.
                var listT = listCtor.DeclaringType;
                EmitConstructor(_il, OpCodes.Newobj, listCtor);
                foreach (var item in e.Elems)
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(item, elem);
                    EmitMethod(_il, OpCodes.Callvirt, add);
                }
                return listT;
            }
            case CirGenericStatic e:
            {
                // Generic static call (LINQ): resolve the scalar memberRef, apply MethodSpec arguments, and call.
                var type = ClrRef(e.Type);
                var typeArgs = e.TypeArgs.Select(MapType).ToArray();
                var argEls = e.Args;
                var mi = ResolveGenericMethod(type, e.Method, typeArgs, e.MemberRef, instance: false);
                var ps = ParametersOf(mi);
                for (int i = 0; i < argEls.Length; i++) EmitArg(argEls[i], ps[i].ParameterType);
                RequireArgCount(argEls.Length, ps.Length, mi.ToString());
                EmitMethod(_il, OpCodes.Call, mi);
                return ReturnTypeOf(mi);
            }
            case CirGenericInstance e:
            {
                // Generic instance call: consume the receiver and dispatch authored with the selected MethodDef.
                var type = ClrRef(e.Type);
                var typeArgs = e.TypeArgs.Select(MapType).ToArray();
                var argEls = e.Args;
                var mi = ResolveGenericMethod(type, e.Method, typeArgs, e.MemberRef, instance: true);
                var ps = ParametersOf(mi);
                switch (e.ReceiverMode)
                {
                    case "address": EmitAddr(e.Recv); break;
                    case "value": EmitExpr(e.Recv); break;
                    default: throw new InvalidOperationException("clrGenericInstance has no physical receiver mode");
                }
                for (int i = 0; i < argEls.Length; i++) EmitArg(argEls[i], ps[i].ParameterType);
                RequireArgCount(argEls.Length, ps.Length, mi.ToString());
                EmitClrDispatch(mi, e.Dispatch, type);
                return ReturnTypeOf(mi);
            }
            case CirNewArray e: return EmitNewArray(e);
            case CirNewArraySized e:
            {
                // `IntArray(size)` (no init) -> a zero-filled BCL array (newarr zero-initializes).
                var elem = MapType(e.Elem);
                EmitExpr(e.Size); _il.Emit(OpCodes.Newarr, elem); return elem.MakeArrayType();
            }
            case CirNewArrayInitialized e:
            {
                // `IntArray(size) { init }` -> `new elem[size]` + a fill loop `for i in 0..size-1: arr[i] = init(i)`.
                // The init is a Func<int,elem> delegate; box/unbox per its actual signature (primitive vs boxed lambda).
                var elem = MapType(e.Elem);
                EmitExpr(e.Size); var size = _il.DeclareLocal(Bcl("System.Int32")); _il.Emit(OpCodes.Stloc, size);
                var init = e.Init;
                var fnType = EmitExpr(init); var fn = _il.DeclareLocal(fnType); _il.Emit(OpCodes.Stloc, fn);
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
                var i = _il.DeclareLocal(Bcl("System.Int32")); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, i);
                var top = _il.DefineLabel(); var done = _il.DefineLabel();
                _il.MarkLabel(top);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Bge, done);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, i);                       // arr, i (for stelem)
                _il.Emit(OpCodes.Ldloc, fn); _il.Emit(OpCodes.Ldloc, i);                         // fn, i
                if (!IsValueType(pType)) _il.Emit(OpCodes.Box, Bcl("System.Int32"));
                EmitDelegateInvoke(_il, fnType,
                    RequiredRef<MethodInfo>(e.InvokeRef, "an array initializer delegate"));                  // init(i)
                if (rType != elem) { if (IsValueType(elem) || elem.IsGenericParameter) _il.Emit(OpCodes.Unbox_Any, elem); else _il.Emit(OpCodes.Castclass, elem); }
                EmitStelem(elem);                                                                // arr[i] = init(i)
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Br, top);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, arr); return arr.LocalType;
            }
            case CirDefault e:
            {
                // `default(T)` -> the zero value: ldnull for a reference type, else a zero-init local (initobj).
                var dt = MapType(e.Type);
                if (!IsValueType(dt) && !dt.IsGenericParameter) { _il.Emit(OpCodes.Ldnull); return dt; }
                var loc = _il.DeclareLocal(dt);
                _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, dt);
                _il.Emit(OpCodes.Ldloc, loc);
                return dt;
            }
            case CirSpreadConcat e:
            {
                // `f(1, *a, 2)` -> new List<elem>(); Add(literal) / AddRange(spread); ToArray().
                var elem = MapType(e.Elem);
                // The four members this builds through — and, with them, the accumulator type — are named by the
                // pass that minted the node; the accumulator local is that constructor's declaring type.
                var spreadCtor = RequiredRef<ConstructorInfo>(e.CtorRef, "spreadConcat");
                var spreadAdd = RequiredRef<MethodInfo>(e.AddRef, "spreadConcat");
                var spreadAddRange = RequiredRef<MethodInfo>(e.AddRangeRef, "spreadConcat");
                var spreadToArray = RequiredRef<MethodInfo>(e.ToArrayRef, "spreadConcat");
                var loc = _il.DeclareLocal(spreadCtor.DeclaringType);
                EmitConstructor(_il, OpCodes.Newobj, spreadCtor);
                _il.Emit(OpCodes.Stloc, loc);
                foreach (var p in e.Parts)
                {
                    _il.Emit(OpCodes.Ldloc, loc);
                    EmitExpr(p.E);
                    EmitMethod(_il, OpCodes.Callvirt, p.Spread ? spreadAddRange : spreadAdd);
                }
                _il.Emit(OpCodes.Ldloc, loc);
                EmitMethod(_il, OpCodes.Callvirt, spreadToArray);
                return elem.MakeArrayType();
            }
            case CirArrayGet e:
            {
                EmitExpr(e.Array); EmitExpr(e.Index);
                var elem = MapType(e.Elem);
                EmitLdelem(elem); return elem;
            }
            case CirArraySet e:
            {
                EmitExpr(e.Array); EmitExpr(e.Index);
                var selem = MapType(e.Elem);
                // Coerce the value to the element type before stelem: a value-type/generic-param value into a
                // REFERENCE-element array (`Array<Any?>[i] = aT`) boxes; a bare `T` / null into a `Nullable<T>` element
                // (`Array<Int?>[i] = 5`) wraps to `Nullable<T>` / `default(Nullable<T>)` — else `stelem Nullable<int>`
                // with a raw int on the stack corrupts the struct (SIGSEGV). A GENERIC-PARAM element (`T[]`, stelem !T)
                // must NOT box. Shared with EmitNewArray via EmitArrayElemCoerced.
                EmitArrayElemCoerced(e.Value, selem);
                EmitStelem(selem); return Bcl("System.Void");
            }
            case CirArrayLength e:
                EmitExpr(e.Array); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4); return Bcl("System.Int32");
            case CirForEach e:
            {
                // `xs.forEach { it -> body }` (inline) -> enumerate src, bind `it` to a loop local, splice body.
                // Inlining (not a delegate) lets the body read/write enclosing locals without closure Ref cells.
                var elem = MapType(e.Elem);
                // BOTH enumerator surfaces arrive named by the pass that minted the node; neither is looked up here.
                // Which of the two this method can SPEAK is the one thing the producer cannot state, and the reason is
                // narrower than "elem is generic": it is whether `elem` MAPS, in this frame, to a builder of the module
                // under construction. IEnumerable<builder>/IEnumerator<builder> is an instantiation Reflection.Emit
                // cannot carry a usable member token for (runtime EntryPointNotFound in a non-inline method), so those
                // take the non-generic IEnumerable/IEnumerator and Unbox_Any the object Current to elem; everything
                // else keeps the typed enumerator (faster, no box).
                //
                // bir2cir cannot make that call, in either direction. A `tv` does not imply a builder — ResolveTv
                // answers System.Object for a type-scope tv with no parameter in scope (the flat lifted anon-object),
                // where the typed enumerator is both speakable and better. Conversely this emitter builds types bir2cir
                // never sees — closures, per-arity delegate adapters, dotkt$ synthetics — so an "external, therefore
                // typed" rule computed from CIR would name the arm that cannot be encoded. The mapped element type is
                // the whole predicate, and it exists only here. Choosing between two members already named is not
                // member selection, and no BCL type is named to make the choice.
                bool viaNonGeneric = IsBuilderTypeArgument(elem);
                var enumerableGet = RequiredRef<MethodInfo>(
                    viaNonGeneric ? e.EnumerableGetErasedRef : e.EnumerableGetRef, "forEachInline");
                EmitExpr(e.Src);
                EmitMethod(_il, OpCodes.Callvirt, enumerableGet);
                // The enumerator the walk holds is whatever the selected GetEnumerator returns — a mechanical read of
                // the reference just emitted, so the local can never disagree with the member that filled it.
                var en = _il.DeclareLocal(ReturnTypeOf(enumerableGet)); _il.Emit(OpCodes.Stloc, en);
                var lv = _il.DeclareLocal(elem); _locals[e.Var] = lv;
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((e.LoopId, start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, en);
                EmitMethod(_il, OpCodes.Callvirt, RequiredRef<MethodInfo>(e.MoveNextRef, "forEachInline"));
                _il.Emit(OpCodes.Brfalse, end);
                _il.Emit(OpCodes.Ldloc, en);
                if (viaNonGeneric)
                {
                    EmitMethod(_il, OpCodes.Callvirt, RequiredRef<MethodInfo>(e.CurrentErasedRef, "forEachInline"));
                    _il.Emit(OpCodes.Unbox_Any, elem);
                }
                else
                {
                    EmitMethod(_il, OpCodes.Callvirt, RequiredRef<MethodInfo>(e.CurrentRef, "forEachInline"));
                }
                _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in e.Body) EmitStmt(b);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return Bcl("System.Void");
            }
            case CirIsInstance e:
            {
                // `x is T` -> isinst T; (ref != null) as bool. A value-type / generic-param receiver MUST be boxed
                // first: `isinst` consumes an object reference off the stack, so reading an unboxed value type (or an
                // `!!T` whose runtime T is a value type) as a reference gives an NRE. This is what C# emits for
                // `element is X` when `element` is a generic `T` (box !!T; isinst X).
                var rt0 = EmitExpr(e.E);
                if (NeedsBoxToRef(rt0)) _il.Emit(OpCodes.Box, rt0);
                // `nullMatches` (bir2cir NullableIsInstMatch): the Kotlin type operand was NULLABLE (`x is T?`), whose
                // instances include null — but `isinst` never matches a null reference. Answer true for null before the
                // isinst, keeping the operand's single evaluation: dup the reference, and on null pop it and push 1.
                if (e.NullMatches.OrDefault())
                {
                    var notNull = _il.DefineLabel(); var done = _il.DefineLabel();
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Brtrue, notNull);
                    _il.Emit(OpCodes.Pop);
                    _il.Emit(OpCodes.Ldc_I4_1);
                    _il.Emit(OpCodes.Br, done);
                    _il.MarkLabel(notNull);
                    _il.Emit(OpCodes.Isinst, MapType(e.Type));
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Cgt_Un);
                    _il.MarkLabel(done);
                    return Bcl("System.Boolean");
                }
                if (e.NullWitness.IsPresent)
                {
                    var notNull = _il.DefineLabel(); var done = _il.DefineLabel();
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Brtrue, notNull);
                    _il.Emit(OpCodes.Pop);
                    var witnessType = EmitExpr(e.NullWitness.Value);
                    if (witnessType != Bcl("System.Boolean"))
                        throw new InvalidOperationException("isInst nullWitness must be System.Boolean");
                    _il.Emit(OpCodes.Br, done);
                    _il.MarkLabel(notNull);
                    _il.Emit(OpCodes.Isinst, MapType(e.Type));
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Cgt_Un);
                    _il.MarkLabel(done);
                    return Bcl("System.Boolean");
                }
                _il.Emit(OpCodes.Isinst, MapType(e.Type));
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Cgt_Un);
                return Bcl("System.Boolean");
            }
            case CirCast e:
            {
                // `x as T` / smart-cast downcast. A generic type parameter (`!!T`) is NOT IsValueType at emit time, but
                // `castclass` is INVALID for a VALUE-type instantiation (the JIT rejects `castclass int` ->
                // InvalidProgram). `unbox.any` is the universal cast: unbox for value types, castclass for reference
                // types, and resolves a generic param correctly at JIT -- exactly what C# emits for `(T)objExpr`.
                var castSrc = EmitExpr(e.E);
                var t = MapType(e.Type);
                // IDENTITY cast (source already IS the target, e.g. `(element: T) as T`): the stack already holds `t`.
                // Emitting `unbox.any !!T` here would read an ALREADY-UNBOXED `!!T` value as a boxed-object pointer ->
                // NullReferenceException at runtime. Nothing to do -- just report the type.
                if (castSrc == t) return t;
                var toRef = !(IsValueType(t) || t.IsGenericParameter);
                // A VALUE/GENERIC source flowing into ANY target that isn't already itself must be boxed first: an
                // unboxed !!T / struct feeding `castclass` (ref target) is invalid IL, and feeding `unbox.any` (value /
                // generic target) reads a raw value as a boxed-obj pointer. Box independently of the target kind --
                // e.g. `(x: T) as System.IComparable`, or a value flowing into a differing generic slot.
                if (NeedsBoxToRef(castSrc)) _il.Emit(OpCodes.Box, castSrc);
                _il.Emit(toRef ? OpCodes.Castclass : OpCodes.Unbox_Any, t);
                return t;
            }
            case CirClassReference e:
            {
                return EmitNativeClrTypeOf(e);
            }
            case CirGetType e:
            {
                return EmitNativeClrGetType(e);
            }
            case CirIsInstanceReference e:
            {
                // `x as? T` for reference T -> `isinst T` (leaves the ref, or null on mismatch). The result is a
                // reference (objref or null), so report `object` — never a generic-param type that would make a
                // downstream consumer (objMethod/objEq) wrongly re-box an already-reference value.
                var rtr = EmitExpr(e.E);
                if (NeedsBoxToRef(rtr)) _il.Emit(OpCodes.Box, rtr);
                var t = MapType(e.Type);
                _il.Emit(OpCodes.Isinst, t);
                return Bcl("System.Object");
            }
            case CirSafeCastValue e:
            {
                return EmitNativeClrSafeCastValue(e);
            }
            case CirNullableNull e:
            {
                return EmitNativeClrNullableNull(e);
            }
            case CirNullableWrap e:
            {
                return EmitNativeClrNullableWrap(e);
            }
            case CirNullableHasValue e:
            {
                return EmitNativeClrNullableHasValue(e);
            }
            case CirNullableValue e:
            {
                return EmitNativeClrNullableValue(e);
            }
            case CirRepeat e:
            {
                // `repeat(n) { i -> body }` -> for (i = 0; i < n; i++) { body } (i bound to a loop local).
                var lv = _il.DeclareLocal(Bcl("System.Int32")); _locals[e.Var] = lv;
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, lv);
                var cnt = _il.DeclareLocal(Bcl("System.Int32")); EmitExpr(e.Count); _il.Emit(OpCodes.Stloc, cnt);
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((e.LoopId, start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldloc, cnt); _il.Emit(OpCodes.Bge, end);
                foreach (var b in e.Body) EmitStmt(b);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, lv);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return Bcl("System.Void");
            }
            case CirEnumValue e:
            {
                return EmitNativeClrEnumValue(e);
            }
            case CirEnumBits e:
            {
                var enumType = MapType(e.Type);
                var underlying = MapType(e.Underlying);
                var actual = EmitExpr(e.E);
                if (actual != underlying)
                    throw new InvalidOperationException(
                        $"enumBits expression produced '{actual}', expected explicit underlying '{underlying}'");
                return enumType;
            }
            case CirEnumOrdinal e:
                return EmitNativeClrEnumOrdinal(e);
            case CirEnumValues e:
            {
                return EmitNativeClrEnumValues(e);
            }
            case CirEnumParse e:
            {
                return EmitNativeClrEnumParse(e);
            }
            case CirObjectMethod e: return EmitObjMethod(e);
            case CirThrowExpression e:
            {
                // A throwing expression (error()/TODO()/exhaustive-when else): construct + throw; no value reaches a merge.
                EmitExpr(e.Value);
                _il.Emit(OpCodes.Throw);
                return Bcl("System.Object");
            }
            case CirReturnExpression e:
            {
                // `return` in expression position: emit the method return; no value reaches the surrounding merge
                // (mirrors the "return" statement, incl. the protected-region leave and the return coercion).
                if (_tryStack.Count > 0)
                {
                    var ctx = _tryStack.Peek();
                    if (e.Value.IsPresent)
                    {
                        var tgot = EmitExpr(e.Value.Value);
                        if (ctx.result != null) { EmitReturnCoerced(tgot); _il.Emit(OpCodes.Stloc, ctx.result); }
                        else _il.Emit(OpCodes.Pop);
                    }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (e.Value.IsPresent) EmitReturnCoerced(EmitExpr(e.Value.Value));
                    _il.Emit(OpCodes.Ret);
                }
                return Bcl("System.Object");
            }
            case CirDelegate e:
            {
                // Non-capturing lambda: bind the lifted static method into a Func/Action delegate.
                var ft = MapType(e.FuncType);
                // #199/#203/#204: calleeOwner selects the exact file class and sig selects its overload. Locally
                // lifted __lambda/__ctorref/__mref targets carry their synthesizing file class too; no global fallback.
                var dname = e.Method;
                var dsig = CallSignature(e.CalleeParams, e.Sig);
                MethodInfo mb = PrimaryFromRef(e.MemberRef.OrDefault()) as MethodInfo
                    ?? FindCalleeOwnedStatic(e.CalleeOwner, "newDelegate", dname, dsig, MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault());
                // A GENERIC lifted lambda (e.g. the comparator inside a generic `sort<T>`) MUST be instantiated with its
                // typeArgs before Ldftn -- loading the open generic-method-DEFINITION's ftn throws "the method itself or
                // the containing type is not fully instantiated" at runtime.
                MethodInfo target = (e.TypeArgs.IsPresent && e.TypeArgs.Value.Length > 0 && mb.IsGenericMethodDefinition)
                    ? ConstructedMethod(mb, e.TypeArgs.Value.Select(MapType).ToArray())
                    : mb;
                _il.Emit(OpCodes.Ldnull);
                EmitMethod(_il, OpCodes.Ldftn, target);
                EmitDelegateCtor(_il, e.DelegateCtorRef);
                return ft;
            }
            case CirBoundDelegate e:
            {
                // `obj::method` -> a delegate bound to the receiver. The carried declaration `sig` selects the exact
                // overload within ownerType (#203). ldvirtftn needs the object twice (dup); a
                // final method uses ldftn (the target stays on the stack as the delegate's first ctor arg).
                var ft = MapType(e.FuncType);
                var boundName = e.Method;
                var boundOwnerNode = e.OwnerType;
                var boundOwner = SlotName(boundOwnerNode);
                if (SlotName(e.CalleeOwner) is not string bcoName || bcoName != boundOwner)
                    throw new NotSupportedException($"newBoundDelegate target '{boundOwner}.{boundName}' is missing or mismatches required calleeOwner");
                var mb = PrimaryFromRef(e.MemberRef.OrDefault()) as MethodInfo
                    ?? (boundOwnerNode is DotKt.Bir.TypeNode.Fqn { Args: not null }
                        ? ResolveMethod(ParseOwnerSlot(boundOwnerNode), boundName, out _, CallSignature(e.CalleeParams, e.Sig), MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault())
                        : FindMethod(boundOwner, boundName, CallSignature(e.CalleeParams, e.Sig), MethodTypeArguments(e.TypeArgs).Length, e.CalleeRet.OrDefault()));
                if (mb == null)
                    throw new NotSupportedException($"newBoundDelegate target '{boundOwner}.{boundName}' was not found");
                MethodInfo boundTarget = e.TypeArgs.IsPresent
                    && e.TypeArgs.Value.Length > 0 && mb.IsGenericMethodDefinition
                        ? ConstructedMethod(mb, e.TypeArgs.Value.Select(MapType).ToArray())
                        : mb;
                var recvT = EmitExpr(e.Recv);
                // A value-type (or `gp:T`) receiver must be BOXED before it can back the delegate: the delegate ctor's
                // first arg is `object` and `ldvirtftn` dispatches on an object reference, but EmitExpr pushed the raw
                // struct value. Box gives a valid object target; the CLR delegate machinery routes it through the value
                // type's unboxing stub for a non-virtual `ldftn` target and virtual dispatch for `ldvirtftn`.
                if (NeedsBoxToRef(recvT)) _il.Emit(OpCodes.Box, recvT);
                if (e.Virtual.OrDefault()) { _il.Emit(OpCodes.Dup); EmitMethod(_il, OpCodes.Ldvirtftn, boundTarget); }
                else EmitMethod(_il, OpCodes.Ldftn, boundTarget);
                EmitDelegateCtor(_il, e.DelegateCtorRef);
                return ft;
            }
            case CirBoundClrDelegate e:
            {
                // `netObj::method` -> a delegate bound to a .NET instance method. W1-S5 (#46/#183): CONSUME the FIR-
                // resolved `memberRef` bir2cir carried (ClrMemberResolution.ResolveBoundClrDelegate) — LINK
                // the UNIQUE instance target (0 = hard ABI error, >1 = malformed), never a name-only first-pick.
                var ft = MapType(e.FuncType);
                var type = ClrRef(e.ClrType);
                var mi = LinkClrMethod(type, e.Method, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: true);
                if (e.TypeArgs.IsPresent &&
                    e.TypeArgs.Value.Length > 0 && mi.IsGenericMethodDefinition)
                    mi = ConstructedMethod(mi,
                        e.TypeArgs.Value.Select(MapType).ToArray());
                var recvTc = EmitExpr(e.Recv);
                // Same value-type-receiver rule as newBoundDelegate: a struct .NET receiver (e.g. `kvp::method`) must be
                // boxed so the delegate ctor's `object` target and `ldvirtftn` see an object reference, not a raw struct.
                if (NeedsBoxToRef(recvTc)) _il.Emit(OpCodes.Box, recvTc);
                if (e.Virtual.OrDefault()) { _il.Emit(OpCodes.Dup); EmitMethod(_il, OpCodes.Ldvirtftn, mi); }
                else EmitMethod(_il, OpCodes.Ldftn, mi);
                EmitDelegateCtor(_il, e.DelegateCtorRef);
                return ft;
            }
            case CirClrStaticDelegate e:
            {
                var ft = MapType(e.FuncType);
                var type = ClrRef(e.ClrType);
                var mi = LinkClrMethod(type, e.Method, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: false);
                if (e.TypeArgs.IsPresent &&
                    e.TypeArgs.Value.Length > 0 && mi.IsGenericMethodDefinition)
                    mi = ConstructedMethod(mi,
                        e.TypeArgs.Value.Select(MapType).ToArray());
                _il.Emit(OpCodes.Ldnull);
                EmitMethod(_il, OpCodes.Ldftn, mi);
                EmitDelegateCtor(_il, e.DelegateCtorRef);
                return ft;
            }
            case CirDelegateInvoke e:
            {
                var ftNode = e.FuncType;
                var ft = MapType(ftNode);
                EmitExpr(e.Recv);
                // bir2cir has made every argument conversion explicit against the resolved Invoke contract.
                foreach (var argument in e.Args) EmitExpr(argument);
                // The node names the DECLARATION it calls through; the delegate value on the stack is what it
                // gets anchored onto. Emitting the declaration unanchored is emitting a member of the open
                // definition, which the constructed type has no token for.
                EmitDelegateInvoke(_il, ft, RequiredRef<MethodInfo>(e.InvokeRef, "a function-type call"));
                return FuncRetType(ftNode);
            }
            case CirClosure e:
            {
                // Capturing lambda: `new Closure(captures)` then bind its `invoke` instance method as a delegate.
                // ResolveClosure instantiates the closure generic when it captures an enclosing type param (a generic
                // closure left open -> a TypeLoadException at the newobj); shared with the delegate-arg binding path.
                var (ctor, invoke) = ResolveClosure(e);
                foreach (var c in e.Captures) EmitExpr(c);
                EmitConstructor(_il, OpCodes.Newobj, ctor);  // closure instance is the delegate target
                EmitMethod(_il, OpCodes.Ldftn, invoke);
                var ft = MapType(e.FuncType);
                EmitDelegateCtor(_il, e.DelegateCtorRef);
                return ft;
            }
            case CirSam e:
            {
                // SAM conversion `Comparator { … }` -> `new <Sam>(captures)` -- a synthetic class IMPLEMENTING the fun
                // interface (no delegate). The instance IS the interface value (implicit upcast at the use site).
                var ct = _types[SlotName(e.SamType)];
                ConstructorInfo ctor = ct.Ctor;
                Type result = ct.TB;
                if (e.TypeArgs.IsPresent && e.TypeArgs.Value.Length > 0)
                {
                    var typeArgs = e.TypeArgs.Value.Select(MapType).ToArray();
                    result = ConstructedType(ct.TB, typeArgs);
                    ctor = AnchorConstructor(result, ct.Ctor);
                }
                foreach (var c in e.Captures) EmitExpr(c);
                EmitConstructor(_il, OpCodes.Newobj, ctor);
                return result;
            }
            case CirConcat e: return EmitConcat(e);
            case CirConditional e: return EmitCond(e);
            case CirNewClr e: return EmitClrNew(e);
            case CirNativeStatic e: return EmitClrCall(e);
            case CirNativeInstance e: return EmitClrCall(e);
            case CirPropertyGet e: return EmitClrPropGet(e);
            case CirPropertySet e: return EmitClrPropSet(e);
            case CirEventAdd e: return EmitClrEvent(e.Type, e.Static, e.Accessor, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), e.Dispatch, e.Recv, e.HandlerExact, e.Handler, add: true);
            case CirEventRemove e: return EmitClrEvent(e.Type, e.Static, e.Accessor, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), e.Dispatch, e.Recv, e.HandlerExact, e.Handler, add: false);
            case CirByRefOf e:
            {
                // The live managed pointer behind `byref(...)` in a `var x by` delegate: keep a ref return's pointer
                // (deref:false), or take the address of a local/field lvalue.
                var inner = e.Inner;
                if (inner is CirNativeInstance instance) return EmitClrCall(instance, deref: false);
                if (inner is CirNativeStatic statik) return EmitClrCall(statik, deref: false);
                EmitAddr(inner);
                return null;
            }
            case CirStackAllocate e:
            {
                // `localloc` a zero-initialized stack buffer of `count * sizeof(elem)` bytes, leaving its pointer.
                // (Unverifiable, like C#'s own stackalloc.)
                var elem = MapType(e.Elem);
                var bc = _il.DeclareLocal(Bcl("System.Int32"));
                EmitExpr(e.Count);
                _il.Emit(OpCodes.Sizeof, elem);
                _il.Emit(OpCodes.Mul);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Stloc, bc);   // keep byteCount for initblk
                _il.Emit(OpCodes.Conv_U);
                _il.Emit(OpCodes.Localloc);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ldloc, bc); _il.Emit(OpCodes.Initblk);
                return Bcl("System.Byte").MakePointerType();
            }
            case CirStackGet e:
            {
                var elem = MapType(e.Elem);
                EmitStackCheckedAddr(e.Index, e.Len, e.Ptr, elem);
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case CirStackSet e:
            {
                var elem = MapType(e.Elem);
                EmitStackCheckedAddr(e.Index, e.Len, e.Ptr, elem);
                EmitArg(e.Value, elem);
                _il.Emit(OpCodes.Stobj, elem);
                return Bcl("System.Void");
            }
            case CirStackSpan e:
            {
                // `new System.Span<T>(void* ptr, int length)` over the stack buffer -> a real Span for .NET APIs.
                var elem = MapType(e.Elem);
                var spanT = ConstructedType(Bcl("System.Span`1"), elem);
                // The declaration is fixed; the element this site computed is what it anchors onto.
                var ctor = AnchorOn(spanT, WellKnown<ConstructorInfo>("SpanT.ctorPointer"));
                EmitExpr(e.Ptr);
                EmitExpr(e.Len);
                EmitConstructor(_il, OpCodes.Newobj, ctor);
                return spanT;
            }
            case CirByRefLoad e:
            {
                // Read through either a named byref local (ClrRef) or an explicit managed-pointer expression such as
                // a caller-side UnsafeAccessor field declaration.
                if (e.Ptr.IsPresent) EmitExpr(e.Ptr.Value);
                else _il.Emit(OpCodes.Ldloc, _locals[e.Local.Value]);
                var elem = MapType(e.Elem);
                MaybeVolatile(null, e.Volatile.OrDefault());
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case CirByRefStore e:
            {
                // Write through either a named byref local or an explicit managed-pointer expression.
                if (e.Ptr.IsPresent) EmitExpr(e.Ptr.Value);
                else _il.Emit(OpCodes.Ldloc, _locals[e.Local.Value]);
                var elem = MapType(e.Elem);
                EmitArg(e.Value, elem);
                MaybeVolatile(null, e.Volatile.OrDefault());
                _il.Emit(OpCodes.Stobj, elem);
                return Bcl("System.Void");
            }
            case CirUnsupportedExpression e: throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + e.Of);
            default: throw new NotSupportedException("expr " + expression.Tag);
        }
    }
}
