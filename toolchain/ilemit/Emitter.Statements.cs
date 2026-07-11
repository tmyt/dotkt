// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// EmitStmt: the BIR statement -> CIL emitter (control flow, locals, loops, returns).
sealed partial class Emitter
{
    void EmitStmt(JsonElement s)
    {
        switch (s.GetProperty("k").GetString())
        {
            case "var":
            {
                var vname = s.GetProperty("name").GetString();
                var declared = MapType(s.GetProperty("type"));
                var local = _il.DeclareLocal(declared);
                _locals[vname] = local;
                if (s.TryGetProperty("init", out var init) && init.ValueKind != JsonValueKind.Null)
                {
                    // Boxing a value type assigned to a reference local (an `Any`/`object` temp) etc. — the shared
                    // store coercion (EmitStoreCoerced).
                    EmitStoreCoerced(init, declared);
                    _il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case "setLocal":
            {
                var sname = s.GetProperty("name").GetString();
                if (_locals.TryGetValue(sname, out var slb)) { EmitStoreCoerced(s.GetProperty("value"), slb.LocalType); _il.Emit(OpCodes.Stloc, slb); }
                else if (_args.TryGetValue(sname, out var sa)) { EmitStoreCoerced(s.GetProperty("value"), _argTypes[sname]); _il.Emit(OpCodes.Starg, sa); }
                else throw new NotSupportedException("store unknown var " + sname);
                break;
            }
            case "setField":
            {
                var fon = SlotName(s.GetProperty("ownerType"));
                var fnm = s.GetProperty("name").GetString();
                // An EXTERNAL type's property write goes through the public setter (its backing field is private
                // cross-assembly -> Stfld would throw FieldAccessException). Falls back to the field when no setter.
                if (ExternalPropAccessor(fon, "set_" + fnm) is { } setter)
                {
                    EmitExpr(s.GetProperty("recv"));
                    EmitStoreCoerced(s.GetProperty("value"), SetterValueType(setter));
                    _il.Emit(OpCodes.Callvirt, setter);
                    break;
                }
                var sfld = ResolveField(fon, fnm, out var sft);
                EmitExpr(s.GetProperty("recv"));
                EmitStoreCoerced(s.GetProperty("value"), sft);
                MaybeVolatile(sfld);
                _il.Emit(OpCodes.Stfld, sfld);
                break;
            }
            case "return":
                if (_tryStack.Count > 0)
                {
                    // Can't `ret` inside a protected region: store the value and leave the block. The result local
                    // is _methodRetType-typed, so the value takes the SAME return coercion as the plain path
                    // (EmitReturnCoerced) BEFORE the store — a raw store used to skip the Nullable<T> wrap, so
                    // `fun f(): Int? { try { return 1 } finally {} }` read a default(Nullable<int>) back (printed 0).
                    var ctx = _tryStack.Peek();
                    if (s.TryGetProperty("value", out var trv))
                    {
                        var tgot = EmitExpr(trv);
                        if (ctx.result != null) { EmitReturnCoerced(tgot); _il.Emit(OpCodes.Stloc, ctx.result); }
                        else _il.Emit(OpCodes.Pop);
                    }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (s.TryGetProperty("value", out var rv)) EmitReturnCoerced(EmitExpr(rv));
                    _il.Emit(OpCodes.Ret);
                }
                break;
            case "throw":
                EmitExpr(s.GetProperty("value"));
                _il.Emit(OpCodes.Throw);
                break;
            case "try":
            {
                // `ret` is illegal inside a protected region, so a `return` in the try stores its value and
                // `leave`s to a dedicated label where the real `ret` lives. The trailing ret is emitted ONLY when
                // the try actually contains a return — otherwise control FALLS THROUGH to the following statements
                // (e.g. `try { x = f() } finally { … }; return x`). Earlier this returned unconditionally, dropping
                // the code after a fall-through try.
                var bodyArr = s.GetProperty("body");
                var catchesArr = s.GetProperty("catches");
                bool hasRet = StmtsHaveReturn(bodyArr) || catchesArr.EnumerateArray().Any(c => StmtsHaveReturn(c.GetProperty("body")));
                LocalBuilder result = (_methodRetType != typeof(void) && hasRet) ? _il.DeclareLocal(_methodRetType) : null;
                Label retLabel = _il.DefineLabel();
                // The CFG labels declared inside this region: a `goto` targeting a label NOT in this set exits the
                // protected region and MUST emit `leave`, not `br` (see the `goto` case).
                var regionLabels = new HashSet<int>();
                CollectLabelIds(s, regionLabels);
                _il.BeginExceptionBlock();
                _tryStack.Push((result, retLabel, regionLabels));
                foreach (var b in bodyArr.EnumerateArray()) EmitStmt(b);
                foreach (var c in catchesArr.EnumerateArray())
                {
                    var ct = MapType(c.GetProperty("excType"));
                    _il.BeginCatchBlock(ct);
                    // Bind the caught exception to the catch variable (a local); referenced by the handler body.
                    if (c.TryGetProperty("var", out var cv) && cv.ValueKind == JsonValueKind.String)
                    { var el = _il.DeclareLocal(ct); _locals[cv.GetString()] = el; _il.Emit(OpCodes.Stloc, el); }
                    else _il.Emit(OpCodes.Pop);
                    foreach (var b in c.GetProperty("body").EnumerateArray()) EmitStmt(b);
                }
                if (s.TryGetProperty("finally", out var fin))
                {
                    _il.BeginFinallyBlock();
                    foreach (var b in fin.EnumerateArray()) EmitStmt(b);
                }
                _il.EndExceptionBlock();
                _tryStack.Pop();
                if (hasRet)
                {
                    bool allRet = StmtsAlwaysReturn(bodyArr) && catchesArr.EnumerateArray().All(c => StmtsAlwaysReturn(c.GetProperty("body")));
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
            case "exprStmt":
            {
                var t = EmitExpr(s.GetProperty("expr"));
                if (t != typeof(void)) _il.Emit(OpCodes.Pop);
                break;
            }
            case "while":
            {
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), start, end));   // continue -> re-check, break -> end
                _il.MarkLabel(start);
                EmitExpr(s.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, end);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start); _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "if":
            {
                var end = _il.DefineLabel();
                foreach (var br in s.GetProperty("branches").EnumerateArray())
                {
                    if (br.TryGetProperty("else", out _))
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                    else
                    {
                        var next = _il.DefineLabel();
                        EmitExpr(br.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, next);
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                        _il.Emit(OpCodes.Br, end); _il.MarkLabel(next);
                    }
                }
                _il.MarkLabel(end);
                break;
            }
            case "for":
            {
                var local = _il.DeclareLocal(typeof(int));
                _locals[s.GetProperty("var").GetString()] = local;
                EmitExpr(s.GetProperty("from")); _il.Emit(OpCodes.Stloc, local);
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));   // continue -> increment, break -> end
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, local);
                EmitExpr(s.GetProperty("to"));
                switch (s.GetProperty("cmp").GetString())   // exit when the bound is crossed
                {
                    case "<=": _il.Emit(OpCodes.Bgt, end); break;
                    case "<": _il.Emit(OpCodes.Bge, end); break;
                    case ">=": _il.Emit(OpCodes.Blt, end); break;
                }
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, local);
                _il.Emit(OpCodes.Ldc_I4, s.GetProperty("step").GetInt32());
                _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, local);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "dowhile":
            {
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                EmitExpr(s.GetProperty("cond")); _il.Emit(OpCodes.Brtrue, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "forArray":
            {
                // for (x in arr): evaluate arr once, index 0..Length, bind loop var = arr[i] each iteration.
                var arrT = EmitExpr(s.GetProperty("array"));
                var arr = _il.DeclareLocal(arrT); _il.Emit(OpCodes.Stloc, arr);
                var idx = _il.DeclareLocal(typeof(int));
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, idx);
                var elem = MapType(s.GetProperty("elem"));
                var lv = _il.DeclareLocal(elem);
                _locals[s.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4);
                _il.Emit(OpCodes.Bge, end);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); EmitLdelem(elem); _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, idx);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "forRange":
            {
                // for (i in <range value>): counter-loop i over <first>..<last> step <step>. The range type + getter
                // names come from the NODE (accessOwner/firstM/lastM/stepM), so this Emitter holds NO hardcoded
                // kotlin.ranges knowledge -- it resolves the accessors generically on whatever type the CIR layer names.
                var rngT = EmitExpr(s.GetProperty("range"));
                var rngLocal = _il.DeclareLocal(rngT); _il.Emit(OpCodes.Stloc, rngLocal);
                var accessOwner = s.GetProperty("accessOwner").GetString();
                if (!_types.TryGetValue(accessOwner, out var prog))
                    throw new NotSupportedException($"forRange: {accessOwner} not emitted in this assembly");
                var i = _il.DeclareLocal(typeof(int)); _locals[s.GetProperty("var").GetString()] = i;
                var last = _il.DeclareLocal(typeof(int)); var step = _il.DeclareLocal(typeof(int));
                _il.Emit(OpCodes.Ldloc, rngLocal); _il.Emit(OpCodes.Callvirt, prog.Methods[s.GetProperty("firstM").GetString()]); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Ldloc, rngLocal); _il.Emit(OpCodes.Callvirt, prog.Methods[s.GetProperty("lastM").GetString()]); _il.Emit(OpCodes.Stloc, last);
                _il.Emit(OpCodes.Ldloc, rngLocal); _il.Emit(OpCodes.Callvirt, prog.Methods[s.GetProperty("stepM").GetString()]); _il.Emit(OpCodes.Stloc, step);
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                var neg = _il.DefineLabel(); var bodyL = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                // exit test: step >= 0 ? (i > last) : (i < last)
                _il.Emit(OpCodes.Ldloc, step); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Blt, neg);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, last); _il.Emit(OpCodes.Bgt, end); _il.Emit(OpCodes.Br, bodyL);
                _il.MarkLabel(neg);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, last); _il.Emit(OpCodes.Blt, end);
                _il.MarkLabel(bodyL);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, step); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "block":
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                break;
            // Loop-expressions used in statement position (for-in over a collection, repeat) -> emit, no value.
            case "forEachInline":
            case "repeatInline":
                EmitExpr(s);
                break;
            case "break": { var (_, brk) = TargetLoop(s); _il.Emit(OpCodes.Br, brk); break; }
            case "continue": { var (cont, _) = TargetLoop(s); _il.Emit(OpCodes.Br, cont); break; }
            // CFG block-IR (E-0.5): a basic-block boundary and (un)conditional branches. See docs/design-il-cfg.md.
            case "label": _il.MarkLabel(_cfgLabels[s.GetProperty("id").GetInt32()]); break;
            case "goto":
            {
                // A `goto` whose target label lies OUTSIDE the innermost open protected region must be `leave`, not
                // `br`: a plain branch out of a try is invalid IL (ilverify BranchOutOfTry; InvalidProgramException at
                // JIT). `leave` also runs the intervening finally on the way out. A statement-position goto has an
                // empty eval stack, so `leave` (which empties the stack) is always safe here. (bir2cir routes an
                // inline-spliced in-try non-local return to `setLocal res; goto <end-after-try>` — this is that exit.)
                var gid = s.GetProperty("id").GetInt32();
                bool exitsTry = _tryStack.Count > 0 && !_tryStack.Peek().labels.Contains(gid);
                _il.Emit(exitsTry ? OpCodes.Leave : OpCodes.Br, _cfgLabels[gid]);
                break;
            }
            case "brIf":
            {
                var bid = s.GetProperty("id").GetInt32();
                var on = s.GetProperty("on").GetBoolean();
                if (_tryStack.Count > 0 && !_tryStack.Peek().labels.Contains(bid))
                {
                    // Same protected-region rule as `goto`: a conditional branch OUT of a try is invalid IL, but there
                    // is no conditional `leave`. Invert the test to skip PAST an unconditional leave: `br<!on> skip;
                    // leave target; skip:` — fall through to the leave only when the branch condition actually holds.
                    // (SuspendColdLowering emits brIf state-machine CFG inside bodies that also carry `try` nodes.)
                    var skip = _il.DefineLabel();
                    EmitExpr(s.GetProperty("cond"));
                    _il.Emit(on ? OpCodes.Brfalse : OpCodes.Brtrue, skip);
                    _il.Emit(OpCodes.Leave, _cfgLabels[bid]);
                    _il.MarkLabel(skip);
                }
                else
                {
                    EmitExpr(s.GetProperty("cond"));
                    _il.Emit(on ? OpCodes.Brtrue : OpCodes.Brfalse, _cfgLabels[bid]);
                }
                break;
            }
            case "unsupportedStmt": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + s.GetProperty("of").GetString());
            default: throw new NotSupportedException("stmt " + s.GetProperty("k").GetString());
        }
    }
}
