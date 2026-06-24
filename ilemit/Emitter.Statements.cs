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
                var declared = MapType(s.GetProperty("type").GetString());
                // In a coroutine, a `var` declaring a cpsField is a STORE into the SM field (no IL local).
                if (_coFields != null && _coFields.TryGetValue(vname, out var cf))
                {
                    if (s.TryGetProperty("init", out var cinit) && cinit.ValueKind != JsonValueKind.Null)
                    {
                        _il.Emit(OpCodes.Ldarg_0);
                        var cg = EmitExpr(cinit);
                        if (cg != null && NeedsBoxToRef(cg) && !cf.FieldType.IsValueType && !cf.FieldType.IsGenericParameter) _il.Emit(OpCodes.Box, cg);
                        _il.Emit(OpCodes.Stfld, cf);
                    }
                    break;
                }
                var local = _il.DeclareLocal(declared);
                _locals[vname] = local;
                if (s.TryGetProperty("init", out var init) && init.ValueKind != JsonValueKind.Null)
                {
                    var got = EmitExpr(init);
                    // Assigning a value type to a reference local (e.g. an `Any`/`object` temp) needs boxing.
                    if (got != null && NeedsBoxToRef(got) && !declared.IsValueType && !declared.IsGenericParameter) _il.Emit(OpCodes.Box, got);
                    _il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case "setLocal":
            {
                var sname = s.GetProperty("name").GetString();
                if (_coFields != null && _coFields.TryGetValue(sname, out var sf))
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    EmitExpr(s.GetProperty("value"));
                    _il.Emit(OpCodes.Stfld, sf);
                    break;
                }
                EmitExpr(s.GetProperty("value"));
                StoreVar(sname);
                break;
            }
            case "setField":
            {
                EmitExpr(s.GetProperty("recv"));
                EmitExpr(s.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, ResolveField(s.GetProperty("ownerType").GetString(), s.GetProperty("name").GetString(), out _));
                break;
            }
            case "return":
                if (_tryStack.Count > 0)
                {
                    // Can't `ret` inside a protected region: store the value and leave the block.
                    var ctx = _tryStack.Peek();
                    if (s.TryGetProperty("value", out var trv)) { EmitExpr(trv); if (ctx.result != null) _il.Emit(OpCodes.Stloc, ctx.result); else _il.Emit(OpCodes.Pop); }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (s.TryGetProperty("value", out var rv))
                    {
                        var got = EmitExpr(rv);
                        // `T` returned where the declared type is `T?` -> wrap in Nullable<T> (e.g. a `sortedBy`
                        // selector typed `(T)->R?` whose body yields a non-null R). Mirrors EmitArg's coercion.
                        if (got != null && _methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == typeof(Nullable<>)
                            && _methodRetType.GetGenericArguments()[0] == got)
                            _il.Emit(OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
                    }
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
                _il.BeginExceptionBlock();
                _tryStack.Push((result, retLabel));
                foreach (var b in bodyArr.EnumerateArray()) EmitStmt(b);
                foreach (var c in catchesArr.EnumerateArray())
                {
                    var ct = MapType(c.GetProperty("excType").GetString());
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
                    if (!allRet)   // a fall-through path exists -> it skips the ret and continues
                    {
                        Label cont = _il.DefineLabel();
                        _il.Emit(OpCodes.Br, cont);
                        _il.MarkLabel(retLabel);
                        if (result != null) _il.Emit(OpCodes.Ldloc, result);
                        _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(cont);
                    }
                    else           // every path returns -> the ret is the sole exit (fall-through unreachable)
                    {
                        _il.MarkLabel(retLabel);
                        if (result != null) _il.Emit(OpCodes.Ldloc, result);
                        _il.Emit(OpCodes.Ret);
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
                var elem = MapType(s.GetProperty("elem").GetString());
                var lv = _il.DeclareLocal(elem);
                _locals[s.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4);
                _il.Emit(OpCodes.Bge, end);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldelem, elem); _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, idx);
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
            case "goto": _il.Emit(OpCodes.Br, _cfgLabels[s.GetProperty("id").GetInt32()]); break;
            case "brIf":
                EmitExpr(s.GetProperty("cond"));
                _il.Emit(s.GetProperty("on").GetBoolean() ? OpCodes.Brtrue : OpCodes.Brfalse, _cfgLabels[s.GetProperty("id").GetInt32()]);
                break;
            case "unsupportedStmt": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + s.GetProperty("of").GetString());
            default: throw new NotSupportedException("stmt " + s.GetProperty("k").GetString());
        }
    }
}
