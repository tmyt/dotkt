// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Cir;

// EmitStmt: the BIR statement -> CIL emitter (control flow, locals, loops, returns).
sealed partial class Emitter
{
    void EmitStmt(ICirStatement statement)
    {
        _ctxNode = statement.Tag;
        switch (statement)
        {
            case CirSourcePoint s: RecordSourcePoint(s); break;
            case CirEventAccessorBody s: EmitClrEventAccessorImpl(s); break;
            case CirVariable s:
            {
                var vname = s.Name;
                var declared = MapType(s.Type);
                var local = _il.DeclareLocal(declared);
                _locals[vname] = local;
                if (s.Init.HasValue)
                {
                    // Boxing a value type assigned to a reference local (an `Any`/`object` temp) etc. — the shared
                    // store coercion (EmitStoreCoerced).
                    EmitStoreCoerced(s.Init.Value, declared);
                    _il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case CirSetLocal s:
            {
                var sname = s.Name;
                if (_locals.TryGetValue(sname, out var slb)) { EmitStoreCoerced(s.Value, slb.LocalType); _il.Emit(OpCodes.Stloc, slb); }
                else if (_args.TryGetValue(sname, out var sa)) { EmitStoreCoerced(s.Value, _argTypes[sname]); _il.Emit(OpCodes.Starg, sa); }
                else throw new NotSupportedException("store unknown var " + sname);
                break;
            }
            case CirFieldStore s:
            {
                var fnm = s.Name;
                // W1-S3 (#46 / #121) CONSUME-ONLY: an EXTERNAL owner's write goes through the public setter (its backing
                // field is private cross-assembly) — bir2cir decided that KIND and stamped the accessor memberRef +
                // dispatch. Absent = a LOCAL owner or a public @ClrField -> the direct Stfld path below.
                if (s.Member.OrDefault() == "accessor")
                {
                    var stype = ClrRef(s.OwnerType);
                    var setter = LinkClrMethod(stype, s.Accessor.Value,
                        new ClrMethodTarget(s.MemberRef.OrDefault(), s.LocalAccessor.OrDefault(),
                            s.AccessorOwner.IsPresent ? s.AccessorOwner : s.Type, CallSignature(s.CalleeParams, s.Sig)), instance: true);
                    if (IsValueType(stype)) EmitAddr(s.Recv); else EmitExpr(s.Recv);
                    EmitStoreCoerced(s.Value, SetterValueType(setter));
                    EmitClrDispatch(setter, RequireDispatch(s.Dispatch, stype, "setField"), stype);
                    break;
                }
                // Consume the owner AS RESOLVED: `SlotName` collapses a constructed-generic owner to its open name,
                // dropping the instantiation bir2cir already fixed, so the write bound the field on the type's own
                // implied self-instantiation. Writing a `Cell<T>` field from a generic STATIC method then emitted
                // `Cell<!0>::v` — a TYPE generic parameter, in a method whose frame has only `!!0` — which the JIT
                // rejects as a bad image. `ParseOwnerSlot` preserves the instantiation, exactly as the field READ and
                // `Ldflda` paths already do (Emitter.Expressions/Bodies).
                // The reference first, exactly as the READ path does. A store by name can hit a derived type's
                // static field where the base instance field was resolved, and write to the wrong one silently.
                FieldInfo sfld; Type sft;
                if (PrimaryFromRef(s.MemberRef.OrDefault()) is FieldInfo referencedStore) { sfld = referencedStore; sft = FieldTypeOf(sfld); }
                else sfld = ResolveLocalField(ParseOwnerSlot(s.OwnerType), fnm, out sft, "field write");
                if (IsValueType(ClrRef(s.OwnerType))) EmitAddr(s.Recv);
                else EmitExpr(s.Recv);
                EmitStoreCoerced(s.Value, sft);
                MaybeVolatile(sfld, s.Volatile.OrDefault());
                EmitField(_il, OpCodes.Stfld, sfld);
                break;
            }
            case CirReturn s:
                if (_tryStack.Count > 0)
                {
                    // Can't `ret` inside a protected region: store the value and leave the block. The result local
                    // is _methodRetType-typed, so the value takes the SAME return coercion as the plain path
                    // (EmitReturnCoerced) BEFORE the store — a raw store used to skip the Nullable<T> wrap, so
                    // `fun f(): Int? { try { return 1 } finally {} }` read a default(Nullable<int>) back (printed 0).
                    var ctx = _tryStack.Peek();
                    if (s.Value.IsPresent)
                    {
                        var tgot = EmitExpr(s.Value.Value);
                        if (ctx.result != null) { EmitReturnCoerced(tgot); _il.Emit(OpCodes.Stloc, ctx.result); }
                        else _il.Emit(OpCodes.Pop);
                    }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (s.Value.IsPresent) EmitReturnCoerced(EmitExpr(s.Value.Value));
                    _il.Emit(OpCodes.Ret);
                }
                break;
            case CirThrow s:
                EmitExpr(s.Value);
                _il.Emit(OpCodes.Throw);
                break;
            case CirTry s:
            {
                // `ret` is illegal inside a protected region, so a `return` in the try stores its value and
                // `leave`s to a dedicated label where the real `ret` lives. The trailing ret is emitted ONLY when
                // the try actually contains a return — otherwise control FALLS THROUGH to the following statements
                // (e.g. `try { x = f() } finally { … }; return x`). Earlier this returned unconditionally, dropping
                // the code after a fall-through try.
                var bodyArr = s.Body;
                var catchesArr = s.Catches;
                bool hasRet = StmtsHaveReturn(bodyArr) || catchesArr.Any(c => StmtsHaveReturn(c.Body));
                LocalBuilder result = (_methodRetType != Bcl("System.Void") && hasRet) ? _il.DeclareLocal(_methodRetType) : null;
                Label retLabel = _il.DefineLabel();
                _il.BeginExceptionBlock();
                _tryStack.Push((result, retLabel));
                foreach (var b in bodyArr) EmitStmt(b);
                foreach (var c in catchesArr)
                {
                    var ct = MapType(c.ExcType);
                    _il.BeginCatchBlock(ct);
                    // Bind the caught exception to the catch variable (a local); referenced by the handler body.
                    if (c.Var.HasValue)
                    { var el = _il.DeclareLocal(ct); _locals[c.Var.Value] = el; _il.Emit(OpCodes.Stloc, el); }
                    else _il.Emit(OpCodes.Pop);
                    foreach (var b in c.Body) EmitStmt(b);
                }
                if (s.Finally.IsPresent)
                {
                    _il.BeginFinallyBlock();
                    foreach (var b in s.Finally.Value) EmitStmt(b);
                }
                _il.EndExceptionBlock();
                _tryStack.Pop();
                if (hasRet)
                {
                    bool allRet = StmtsAlwaysReturn(bodyArr) && catchesArr.All(c => StmtsAlwaysReturn(c.Body));
                    // The pending return at retLabel: a real `ret` is legal only OUTSIDE every protected region.
                    // When this try is NESTED inside another try, retLabel still sits inside the OUTER protected
                    // region — a `ret` there is invalid IL (ilverify ReturnFromTry; InvalidProgramException at JIT).
                    // Propagate one level instead: copy the result into the OUTER frame's result local and `leave`
                    // to its retLabel (each level's finally runs on its own leave); only the outermost level rets.
                    void EmitPendingReturn()
                    {
                        if (_tryStack.Count > 0)
                        {
                            var outer = _tryStack.Peek();
                            if (result != null && outer.result != null)
                            { _il.Emit(OpCodes.Ldloc, result); _il.Emit(OpCodes.Stloc, outer.result); }
                            _il.Emit(OpCodes.Leave, outer.end);
                        }
                        else
                        {
                            if (result != null) _il.Emit(OpCodes.Ldloc, result);
                            _il.Emit(OpCodes.Ret);
                        }
                    }
                    if (!allRet)   // a fall-through path exists -> it skips the pending return and continues
                    {
                        Label cont = _il.DefineLabel();
                        _il.Emit(OpCodes.Br, cont);
                        _il.MarkLabel(retLabel);
                        EmitPendingReturn();
                        _il.MarkLabel(cont);
                    }
                    else           // every path returns -> the pending return is the sole exit
                    {
                        _il.MarkLabel(retLabel);
                        EmitPendingReturn();
                    }
                }
                break;
            }
            case CirExpressionStatement s:
            {
                var t = EmitExpr(s.Expr);
                if (t != Bcl("System.Void")) _il.Emit(OpCodes.Pop);
                break;
            }
            case CirWhile s:
            {
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((s.LoopId, start, end));   // continue -> re-check, break -> end
                _il.MarkLabel(start);
                EmitExpr(s.Cond); _il.Emit(OpCodes.Brfalse, end);
                foreach (var b in s.Body) EmitStmt(b);
                _il.Emit(OpCodes.Br, start); _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case CirIf s:
            {
                var end = _il.DefineLabel();
                foreach (var br in s.Branches)
                {
                    if (br.Else.IsPresent)
                        foreach (var b in br.Body) EmitStmt(b);
                    else
                    {
                        var next = _il.DefineLabel();
                        EmitExpr(br.Cond.Value); _il.Emit(OpCodes.Brfalse, next);
                        foreach (var b in br.Body) EmitStmt(b);
                        _il.Emit(OpCodes.Br, end); _il.MarkLabel(next);
                    }
                }
                _il.MarkLabel(end);
                break;
            }
            case CirFor s:
            {
                var local = _il.DeclareLocal(Bcl("System.Int32"));
                _locals[s.Var] = local;
                EmitExpr(s.From); _il.Emit(OpCodes.Stloc, local);
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((s.LoopId, cont, end));   // continue -> increment, break -> end
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, local);
                EmitExpr(s.To);
                switch (s.Cmp)   // exit when the bound is crossed
                {
                    case "<=": _il.Emit(OpCodes.Bgt, end); break;
                    case "<": _il.Emit(OpCodes.Bge, end); break;
                    case ">=": _il.Emit(OpCodes.Blt, end); break;
                }
                foreach (var b in s.Body) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, local);
                _il.Emit(OpCodes.Ldc_I4, s.Step);
                _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, local);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case CirForArray s:
            {
                // for (x in arr): evaluate arr once, index 0..Length, bind loop var = arr[i] each iteration.
                var arrT = EmitExpr(s.Array);
                var arr = _il.DeclareLocal(arrT); _il.Emit(OpCodes.Stloc, arr);
                var idx = _il.DeclareLocal(Bcl("System.Int32"));
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, idx);
                var elem = MapType(s.Elem);
                var lv = _il.DeclareLocal(elem);
                _locals[s.Var] = lv;
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((s.LoopId, cont, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4);
                _il.Emit(OpCodes.Bge, end);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); EmitLdelem(elem); _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in s.Body) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, idx);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case CirForRange s:
            {
                // for (i in <range value>): counter-loop i over <first>..<last> step <step>. The range type + getter
                // names come from the NODE (accessOwner/firstM/lastM/stepM), so this Emitter holds NO hardcoded
                // kotlin.ranges knowledge -- it resolves the accessors generically on whatever type the CIR layer names.
                var rngT = EmitExpr(s.Range);
                var rngLocal = _il.DeclareLocal(rngT); _il.Emit(OpCodes.Stloc, rngLocal);
                var accessOwner = SlotName(s.AccessOwner);
                if (!_types.TryGetValue(accessOwner, out var prog))
                    throw new NotSupportedException($"forRange: {accessOwner} not emitted in this assembly");
                var i = _il.DeclareLocal(Bcl("System.Int32")); _locals[s.Var] = i;
                var last = _il.DeclareLocal(Bcl("System.Int32")); var step = _il.DeclareLocal(Bcl("System.Int32"));
                _il.Emit(OpCodes.Ldloc, rngLocal); EmitMethod(_il, OpCodes.Callvirt, prog.Methods[s.FirstM]); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Ldloc, rngLocal); EmitMethod(_il, OpCodes.Callvirt, prog.Methods[s.LastM]); _il.Emit(OpCodes.Stloc, last);
                _il.Emit(OpCodes.Ldloc, rngLocal); EmitMethod(_il, OpCodes.Callvirt, prog.Methods[s.StepM]); _il.Emit(OpCodes.Stloc, step);
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                var neg = _il.DefineLabel(); var bodyL = _il.DefineLabel();
                _loops.Add((s.LoopId, cont, end));
                _il.MarkLabel(start);
                // exit test: step >= 0 ? (i > last) : (i < last)
                _il.Emit(OpCodes.Ldloc, step); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Blt, neg);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, last); _il.Emit(OpCodes.Bgt, end); _il.Emit(OpCodes.Br, bodyL);
                _il.MarkLabel(neg);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, last); _il.Emit(OpCodes.Blt, end);
                _il.MarkLabel(bodyL);
                foreach (var b in s.Body) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, step); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case CirBlock s:
                foreach (var b in s.Body) EmitStmt(b);
                break;
            // Loop-expressions used in statement position (for-in over a collection, repeat) -> emit, no value.
            case CirForEach s:
                EmitExpr(s);
                break;
            case CirRepeat s:
                EmitExpr(s);
                break;
            case CirBreak s: { var (_, brk) = TargetLoop(s.LoopId); _il.Emit(s.Leave ? OpCodes.Leave : OpCodes.Br, brk); break; }
            case CirContinue s: { var (cont, _) = TargetLoop(s.LoopId); _il.Emit(s.Leave ? OpCodes.Leave : OpCodes.Br, cont); break; }
            // CFG block-IR (E-0.5): a basic-block boundary and (un)conditional branches. See docs/bir-cir-spec.md.
            case CirLabel s: _il.MarkLabel(_cfgLabels[s.Id]); break;
            case CirGoto s:
            {
                var gid = s.Id;
                _il.Emit(s.Leave ? OpCodes.Leave : OpCodes.Br, _cfgLabels[gid]);
                break;
            }
            case CirBranchIf s:
            {
                var bid = s.Id;
                var on = s.On;
                if (s.Leave)
                {
                    // CIR explicitly requests a conditional leave. CIL has no such opcode, so emit its mechanical
                    // expansion: invert the conditional branch over the unconditional leave.
                    var skip = _il.DefineLabel();
                    EmitExpr(s.Cond);
                    _il.Emit(on ? OpCodes.Brfalse : OpCodes.Brtrue, skip);
                    _il.Emit(OpCodes.Leave, _cfgLabels[bid]);
                    _il.MarkLabel(skip);
                }
                else
                {
                    EmitExpr(s.Cond);
                    _il.Emit(on ? OpCodes.Brtrue : OpCodes.Brfalse, _cfgLabels[bid]);
                }
                break;
            }
            default: throw new NotSupportedException("stmt " + statement.Tag);
        }
    }
}
