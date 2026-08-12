// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Method/ctor body emission: control-flow prescan, arg/return coercion, array elem access, addr-of.
sealed partial class Emitter
{
    void EmitCtorBody(TypeInfo ti, ConstructorBuilder cb, JsonElement c)
    {
        _ctxType = ti.TB?.Name; _ctxMethod = ".ctor"; _ctxNode = null; _ctxPos = PosOf(c);   // #84 breadcrumb + #112 P2 source pos
        _methodRetType = Bcl("System.Void");
        _curTypeParams = EffectiveTps(ti); _curMethodParams = null;
        BeginMethod(cb.GetILGenerator(), c, isStatic: false);
        // Exactly the trees this method EMITS: `preStmts` runs before the delegation and may carry CFG labels of its
        // own (a `cond`/`try` inside a bound value), and the delegation args are emitted here too. Scanning the whole
        // declaration would also define labels for subtrees that are never emitted (`params[].default`, `attrs`), and
        // ILGenerator refuses a label that is defined and never marked.
        PrescanCfgLabels(c.GetProperty("body"));
        AddCfgLabels(c, "preStmts");
        AddCfgLabels(c, "thisArgs");
        AddCfgLabels(c, "baseArgs");

        // `preStmts` — the constructor DELEGATION's evaluation plan, lowered to `var` declarations by bir2cir's
        // CallEvalLowering. A delegation's arguments ride the declaration rather than an expression, so there is no
        // `valueBlock` to hold the values it must evaluate exactly once; they are declared HERE, ahead of the
        // `ldarg.0` that begins the `this`/`base` call, which is where Kotlin evaluates them.
        if (c.TryGetProperty("preStmts", out var pre) && pre.ValueKind == JsonValueKind.Array)
            foreach (var st in pre.EnumerateArray()) EmitStmt(st);

        _il.Emit(OpCodes.Ldarg_0);
        if (c.TryGetProperty("thisArgs", out var ta) && ta.ValueKind == JsonValueKind.Array)
        {
            // `constructor(...) : this(...)` -> delegate to a sibling ctor (it runs field inits / base call).
            foreach (var a in ta.EnumerateArray()) EmitExpr(a);
            ConstructorInfo sibling = LinkLocalCtor(ti, c);
            // Inside a GENERIC type, the sibling ctor must be referenced through the SELF-instantiation
            // `C`1<!T>` (the type over its OWN generic params), NOT the open definition `C`1` — a bare
            // `call C`1::.ctor` is "not fully instantiated" at JIT. Mirrors the base-ctor anchoring below
            // (the `: base(...)` branches ~lines 918-920 / 894-898); do not "simplify" this away.
            if (ti.TB is TypeBuilder stb && stb.IsGenericTypeDefinition)
                sibling = AnchorConstructor(ConstructedType(stb, stb.GetGenericArguments()), (ConstructorBuilder)sibling);
            EmitConstructor(_il, OpCodes.Call, sibling);
        }
        else if (ti.ClrBase != null)
        {
            // `: base(...)` on a .NET base -> the matching base constructor (resolved by reflection). A constructed
            // generic base (`Collection<int>`) needs the static helper to map the open ctor onto the instantiation.
            var ba = c.TryGetProperty("baseArgs", out var b) && b.ValueKind == JsonValueKind.Array ? b : default;
            // bir2cir has already resolved the Kotlin delegation onto its physical CLR constructor and carried the
            // declaration as baseMemberSig. Reuse the same exact-link path as newClr; this layer does not form or rank
            // a constructor candidate set from the argument expressions.
            var ctor = LinkClrCtor(ti.ClrBase, c, out var reanchorBaseCtor, "baseMemberSig", includeNonPublic: true);
            if (reanchorBaseCtor) ctor = AnchorConstructor(ti.ClrBase, ctor);
            if (ba.ValueKind == JsonValueKind.Array) EmitArgs(ba, ParametersOf(ctor));
            EmitConstructor(_il, OpCodes.Call, ctor);
        }
        else if (ti.BaseName != null && _types.ContainsKey(ti.BaseName) && c.TryGetProperty("baseArgs", out var ba2) && ba2.ValueKind == JsonValueKind.Array)
        {
            // bir2cir carries the frontend-selected local declaration signature. Link that exact declaration; choosing
            // a same-arity base constructor here would make ilemit an overload-resolution authority.
            ConstructorInfo bctor = LinkLocalCtor(_types[ti.BaseName], c);
            // A generic base instantiated over THIS type's own type params (`class D<T> : Base<T>()`) has its
            // parent set to the CONSTRUCTED base `Base<!T>` (ti.TB.BaseType); the base-ctor operand must be scoped
            // to that constructed type, not the open definition `Base<>` — a bare `call Base``1::.ctor` is "not
            // fully instantiated" (InvalidProgramException). Anchor the open ConstructorBuilder onto the constructed
            // base via the static helper (mirrors newClosure's TypeBuilder.GetConstructor over MakeGenericType).
            var baseType = ti.TB.BaseType;
            if (baseType != null && baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
                bctor = AnchorConstructor(baseType, bctor);
            foreach (var a in ba2.EnumerateArray()) EmitExpr(a);
            EmitConstructor(_il, OpCodes.Call, bctor);
        }
        else
        {
            EmitConstructor(_il, OpCodes.Call, Bcl("System.Object").GetConstructor(Type.EmptyTypes));
        }
        foreach (var s in c.GetProperty("body").EnumerateArray()) EmitStmt(s);
        _il.Emit(OpCodes.Ret);
    }

    // bir2cir resolved this same-emission-unit call to a declaration index. This is a direct token lookup: ilemit does
    // not compare argument shapes, enumerate candidates, or choose an overload.
    ConstructorBuilder LinkLocalCtor(TypeInfo ti, JsonElement node)
    {
        if (!node.TryGetProperty("localCtorIndex", out var indexNode) || indexNode.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"ilemit: local constructor {ti.TB.FullName} is missing `localCtorIndex`; bir2cir must resolve the declaration");
        var index = indexNode.GetInt32();
        if ((uint)index >= (uint)ti.Ctors.Count)
            throw new InvalidOperationException($"ilemit: local constructor index {index} is outside {ti.TB.FullName}'s {ti.Ctors.Count} declarations");
        return ti.Ctors[index];
    }

    void EmitMethodBody(TypeInfo ti, JsonElement m)
    {
        // #84 diagnostic breadcrumb — set BEFORE any read of `m`, so a malformed def (e.g. a missing `name`, the exact
        // bir2cir-bug class #84 targets) is attributed to THIS type, not the previously-emitted method. Refined to the
        // method name once resolved below.
        _ctxType = ti.TB?.Name; _ctxMethod = "?"; _ctxNode = null; _ctxPos = PosOf(m); _curTi = ti;   // #112 P2: decl source pos
        var mname = PhysicalMethodName(m);
        _ctxMethod = mname;
        // Pick THIS def's own MethodBuilder by signature (overloads share `mname`; the name-keyed map holds only the
        // last, so emitting by name alone routes a body into the wrong overload — the WinUI `text(String)` /
        // `text(()->String)` bug).
        if (!ti.MethodsBySig.TryGetValue(SigKey(mname, m), out var mb))
            throw new InvalidOperationException($"ilemit: method body {ti.TB.FullName}.{mname} has no exact declared signature match");
        // Abstract-slot body invariant (#92): a MethodBuilder DECLARED Abstract has NO IL body — GetILGenerator would
        // throw "Method body should not exist". Trust the DECLARED attribute (mb.IsAbstract, the single source of
        // truth), NOT the CIR `abstract` flag: a def that DEFEATS the declare/body pairing (an abstract slot that still
        // carries a body, or whose `abstract` flag went absent — e.g. an abstract fun-interface SAM) is SKIPPED here
        // instead of crashing emit. Pure emitter-internal consistency: skipping can never drop a body a working slot
        // needed, since ANY abstract MethodBuilder reaching GetILGenerator crashes regardless. WARN when the skip is
        // UNEXPECTED (the def looked concrete — a body present, or the abstract flag absent) so the producing-layer
        // defect stays VISIBLE: the root cause is upstream (a bir2cir/kotc pass writing a body onto an abstract slot)
        // and re-checks once R1/#90 lands. (Replaced the prior `m.abstract`-flag re-derivation, which the SAM defeated.)
        if (mb.IsAbstract)
        {
            var hasBody = m.TryGetProperty("body", out var abody) && abody.ValueKind == JsonValueKind.Array && abody.GetArrayLength() > 0;
            var flagAbstract = m.TryGetProperty("abstract", out var af) && af.GetBoolean();
            if (hasBody || !flagAbstract)
                Console.Error.WriteLine($"ilemit: WARNING: abstract-slot body invariant — '{ti.TB?.Name}.{mname}' is declared "
                    + $"abstract but its CIR def {(hasBody ? "carries a body" : "lacks the abstract flag")}; skipping body emission (upstream bir2cir/kotc defect, #92).");
            return;
        }
        _methodRetType = mb.ReturnType;
        _curTypeParams = EffectiveTps(ti);
        _curMethodParams = _methodTypeParams.TryGetValue(mb, out var mp) ? mp : null;
        if (ModFlag(m, "suspend"))
        {
            // A leftover `mods.suspend` method reaching ilemit means the real coroutine state machine (cold entry +
            // `ContinuationImpl` SM class + public `Task<T>` bridge) was NOT synthesized — that lowering is bir2cir's
            // (cold-core, bundle-6); ilemit itself is coroutine-codegen-free.
            //
            // In a STDLIB build (ref OR rt) this is EXPECTED: the coroutine PRIMITIVES — suspendCoroutine[Unintercepted
            // OrReturn], yield/yieldAll, callRecursive, and the kotlin.clr.CoroutinesKt await/delay bridge — have no
            // state-machine form; bir2cir deliberately leaves their DEFINITIONS un-lowered "for the ilemit throw-stub"
            // (SuspendColdLowering.cs), transforming only their CALL SITES. Their bodies are effectively dead (no real
            // caller survives), so a throwing stub is the correct emission. Keep it, unchanged.
            if (_stdlibStub) { EmitThrowStub(mb, "suspend (reference stub)"); return; }
            // In an APP build there are no such primitives — every suspend fn is a real coroutine that bir2cir must
            // lower. Reaching here is therefore a bir2cir transform MISS (a disqualified/un-lowered suspend shape). Fail
            // LOUD at emit time — naming the method — instead of silently emitting a throwing stub that surfaces as a
            // distant runtime throw. A NEW error here is a real bir2cir defect to fix upstream, NOT to re-silence.
            throw new NotSupportedException(
                $"ilemit: suspend method '{ti.TB?.Name}.{mname}' reached codegen un-lowered — bir2cir's cold-core suspend " +
                $"lowering must transform it into a public Task bridge + plain state-machine methods before ilemit (which " +
                $"is coroutine-codegen-free). This is a bir2cir transform MISS.");
        }
        BeginMethod(mb.GetILGenerator(), m, isStatic: mb.IsStatic);
        PrescanCfgLabels(m.GetProperty("body"));
        foreach (var s in m.GetProperty("body").EnumerateArray()) EmitStmt(s);
        // CIR may prove that a body terminates without fall-through (for example, an exact metadata throw stub).
        // Otherwise keep the verifier-safe fallback return used by ordinary emitted methods.
        if (!(m.TryGetProperty("bodyTerminates", out var bodyTerminates) && bodyTerminates.GetBoolean()))
            EmitTrailingRet();
    }

    // Append the method's fall-through terminator. For a `void` method a bare `ret` is valid. For a
    // value-returning method the fall-through is normally UNREACHABLE (every path returns) and ilverify
    // ignores dead code — but a value-returning INFINITE loop (`while(true){ … return x }`, CFG-lowered
    // to a `brfalse end` on a constant-true condition) leaves the loop-exit label STATICALLY reachable,
    // so a bare `ret` with an empty stack trips ilverify ReturnMissing (the JIT runs it fine — the exit
    // is never taken). Push `default(ret)` first so the terminator is stack-valid whether reachable or
    // not (it never actually executes). Same value-type/generic-param vs reference split as `case
    // "default"` (Emitter.Expressions.cs) and the unbox.any rule.
    void EmitTrailingRet()
    {
        var rt = _methodRetType;
        if (rt != Bcl("System.Void"))
        {
            if (IsValueType(rt) || rt.IsGenericParameter)
            { var loc = _il.DeclareLocal(rt); _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, rt); _il.Emit(OpCodes.Ldloc, loc); }
            else _il.Emit(OpCodes.Ldnull);
        }
        _il.Emit(OpCodes.Ret);
    }

    // Define an IL Label for every CFG `label` node anywhere in the body (forward refs from goto/brIf), so the
    // single emit pass can branch to not-yet-emitted blocks. Recursive: labels can sit inside nested structured
    // bodies (a CFG-lowered `while` spliced into a still-structured `if`). See docs/bir-cir-spec.md.
    // Fold one more tree of the SAME frame into the label map [PrescanCfgLabels] just built (a constructor emits its
    // `preStmts` and delegation args alongside its body).
    void AddCfgLabels(JsonElement decl, string key)
    {
        if (decl.TryGetProperty(key, out var t) && t.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            WalkCfgLabels(t);
    }

    void PrescanCfgLabels(JsonElement node)
    {
        _cfgLabels = new Dictionary<int, Label>();
        WalkCfgLabels(node);
    }

    void WalkCfgLabels(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("k", out var k) && k.GetString() == "label")
            {
                var id = e.GetProperty("id").GetInt32();
                if (!_cfgLabels.ContainsKey(id)) _cfgLabels[id] = _il.DefineLabel();
            }
            foreach (var p in e.EnumerateObject()) WalkCfgLabels(p.Value);
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) WalkCfgLabels(x);
    }

    void EmitLdcI4(int n)
    {
        if (n == -1) _il.Emit(OpCodes.Ldc_I4_M1);
        else _il.Emit(OpCodes.Ldc_I4, n);
    }

    void BeginMethod(ILGenerator il, JsonElement m, bool isStatic)
    {
        _il = il; _args.Clear(); _argTypes.Clear(); _locals.Clear();
        int i = isStatic ? 0 : 1; // arg0 = this for instance methods
        foreach (var p in m.GetProperty("params").EnumerateArray())
        {
            // A nameless param (the round-trip attribute-class ctors, #71 S2 — no Param row) is unreferenceable by
            // body IL anyway; skip its arg-map entry but still advance the arg index.
            var pn = p.TryGetProperty("name", out var nn) ? nn.GetString() : null;
            if (!string.IsNullOrEmpty(pn)) { _argTypes[pn] = MapType(p.GetProperty("type")); _args[pn] = i; }
            i++;
        }
    }

    // ---- statements ----
    // Does this statement list contain a `return` anywhere (recursing into if/while/try bodies)? Drives whether a
    // `try` needs a dedicated return label + trailing ret.
    static bool StmtsHaveReturn(JsonElement arr)
    {
        foreach (var s in arr.EnumerateArray()) if (NodeHasReturn(s)) return true;
        return false;
    }

    // Does this node's physical subtree contain a `return`/`returnExpr` anywhere? A full structural walk: after an
    // inline splice a return can hide in ANY expression slot — a `use{}`/`run{}` non-local return in
    // `setLocal.value -> valueBlock.stmts` (or a valueBlock `result`), an elvis `?: return` in an if `cond`, a call
    // arg, etc. Both statement `return` and expression-position `returnExpr` count: each stores to the try's result
    // local and `leave`s to its end label (Statements.cs:59 / Expressions.cs:610), so BOTH must drive the result-local
    // allocation + return-label marking — miss either and the leave targets an unmarked label (a bake failure). No CIR
    // node embeds ANOTHER method's body (newClosure/newDelegate reference their lifted method by NAME; the invoke body
    // travels as BIR `synthClass` and is stripped before CIR), so this walk never crosses a function boundary.
    static bool NodeHasReturn(JsonElement n)
    {
        if (n.ValueKind == JsonValueKind.Object)
        {
            if (n.TryGetProperty("k", out var k) && k.GetString() is "return" or "returnExpr") return true;
            foreach (var p in n.EnumerateObject()) if (NodeHasReturn(p.Value)) return true;
        }
        else if (n.ValueKind == JsonValueKind.Array)
            foreach (var x in n.EnumerateArray()) if (NodeHasReturn(x)) return true;
        return false;
    }

    // The CFG-`label` ids physically declared within a subtree — a `try`'s region label-set, used to decide whether
    // a `goto` leaves the protected region (→ must be `leave`, not `br`). Mirrors PrescanCfgLabels' structural walk.
    static void CollectLabelIds(JsonElement node, HashSet<int> into)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("k", out var k) && k.GetString() == "label")
                into.Add(node.GetProperty("id").GetInt32());
            foreach (var p in node.EnumerateObject()) CollectLabelIds(p.Value, into);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) CollectLabelIds(x, into);
    }

    // Does this statement list ALWAYS return/throw (no fall-through)? Used to decide if a `try`'s fall-through path
    // is reachable (and thus whether to emit a `br` over the trailing ret).
    static bool StmtsAlwaysReturn(JsonElement arr)
    {
        JsonElement last = default; bool any = false;
        foreach (var s in arr.EnumerateArray()) { last = s; any = true; }
        return any && StmtAlwaysReturns(last);
    }

    static bool StmtAlwaysReturns(JsonElement s)
    {
        switch (s.GetProperty("k").GetString())
        {
            case "return": case "throw": return true;
            case "if":
                bool hasElse = false;
                foreach (var br in s.GetProperty("branches").EnumerateArray())
                {
                    if (br.TryGetProperty("else", out _)) hasElse = true;
                    if (!StmtsAlwaysReturn(br.GetProperty("body"))) return false;
                }
                return hasElse;
            case "try":
                if (!StmtsAlwaysReturn(s.GetProperty("body"))) return false;
                foreach (var c in s.GetProperty("catches").EnumerateArray())
                    if (!StmtsAlwaysReturn(c.GetProperty("body"))) return false;
                return true;
            default: return false;
        }
    }


    // The loop a break/continue targets: the innermost, or the one whose Kotlin label matches.
    (Label cont, Label brk) TargetLoop(JsonElement s)
    {
        string label = s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        for (int i = _loops.Count - 1; i >= 0; i--)
            if (label == null || _loops[i].label == label) return (_loops[i].cont, _loops[i].brk);
        throw new NotSupportedException("break/continue with no matching loop");
    }

    static string LoopLabel(JsonElement s) => s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;

    // Enumerate an IEnumerable<elemT> `src`, binding each element to a fresh local passed to `body`.
    void EmitForEachOf(JsonElement src, Type elemT, Action<LocalBuilder> body)
    {
        var ienumT = ConstructedType(Bcl("System.Collections.Generic.IEnumerable`1"), elemT);
        var ienumrT = ConstructedType(Bcl("System.Collections.Generic.IEnumerator`1"), elemT);
        EmitExpr(src);
        EmitMethod(_il, OpCodes.Callvirt, ienumT.GetMethod("GetEnumerator"));
        var en = _il.DeclareLocal(ienumrT); _il.Emit(OpCodes.Stloc, en);
        var x = _il.DeclareLocal(elemT);
        var start = _il.DefineLabel(); var end = _il.DefineLabel();
        _il.MarkLabel(start);
        _il.Emit(OpCodes.Ldloc, en);
        EmitMethod(_il, OpCodes.Callvirt, Bcl("System.Collections.IEnumerator").GetMethod("MoveNext"));
        _il.Emit(OpCodes.Brfalse, end);
        _il.Emit(OpCodes.Ldloc, en);
        EmitMethod(_il, OpCodes.Callvirt, ienumrT.GetMethod("get_Current"));
        _il.Emit(OpCodes.Stloc, x);
        body(x);
        _il.Emit(OpCodes.Br, start);
        _il.MarkLabel(end);
    }

    // Emit `value` COERCED to the store target's type — the ONE shared RHS coercion for every store site
    // (var init, setLocal into a local/arg, setField/setFieldExpr via setter or field, staticFieldSet):
    //  - `T`/null-const stored into a `Nullable<T>` slot -> wrap / default(Nullable<T>) (EmitNullableCoerced);
    //  - a value-type / generic-param RHS stored into a REFERENCE slot -> box (the var-init rule; the other store
    //    sites used to emit the raw RHS, so `var a: Any = "x"; a = 42` stored a raw int32 into an object local ->
    //    NRE/heap corruption at use).
    // A null/unknown target emits the value as-is (no spurious boxing).
    void EmitStoreCoerced(JsonElement value, Type target)
    {
        if (target == null) { EmitExpr(value); return; }
        var got = EmitNullableCoerced(value, target);
        if (got != null && NeedsBoxToRef(got) && !IsValueType(target) && !target.IsGenericParameter)
            _il.Emit(OpCodes.Box, got);
        // A reference `object` (an ERASED value — e.g. a coroutine SM `create(object value, …)`'s receiver stored into
        // its concrete `SequenceScope<T>`/captured-field slot) stored into a NARROWER reference target needs a downcast;
        // a raw stfld of `object` into a typed field is unverifiable (ilverify StackUnexpected [found object][expected
        // ref 'T']). Scoped to a genuinely-erased `object` source and a concrete reference target (value/gp targets took
        // the box/nullable paths above; a same-type or widening store needs nothing).
        else if (got == Bcl("System.Object") && target != Bcl("System.Object") && !IsValueType(target) && !target.IsGenericParameter)
            _il.Emit(OpCodes.Castclass, target);
        // The value-type / generic-param twin: an erased `object` stored into a VALUE (Int32) or generic-param (`T`)
        // slot needs the universal `unbox.any` (a coroutine SM `.ctor(object value, …)` capturing a value/`T` field —
        // ilverify [found object][expected Int32]/[expected value 'T']). unbox.any unboxes a value type and resolves a
        // generic param; castclass would JIT-crash a value instantiation.
        else if (got == Bcl("System.Object") && (IsValueType(target) || target.IsGenericParameter))
            _il.Emit(OpCodes.Unbox_Any, target);
        // A collapsed-variance collection-interface VALUE stored into its SIBLING local/field slot (same T, either
        // direction): the same arg-position variance-collapse reconciliation as EmitArg, one store-site over — a
        // `for`/destructuring element `var it: List<Int> = iterator.next()` where next() statically yields IList<int32>
        // stored into the IReadOnlyList<int32> local (chunk/collops2, forward), or a readonly value stored into a
        // collapsed mutable slot (reverse). castclass to the closed sibling interface (see IsCollectionViewSeam).
        else if (IsCollectionViewSeam(got, target))
            _il.Emit(OpCodes.Castclass, target);
    }

    // The value-parameter type of a property setter, when retrievable: a TypeBuilder-anchored accessor
    // (a TypeBuilder.GetMethod re-anchor) throws NotSupportedException on GetParameters() — treat as unknown
    // (EmitStoreCoerced then emits the RHS as-is, the pre-helper behavior for that path).
    static Type SetterValueType(MethodInfo setter)
    {
        try { var ps = ParametersOf(setter); return ps.Length > 0 ? ps[^1].ParameterType : null; }
        catch (NotSupportedException) { return null; }
    }

    // Read an interface/base entry as a Fqn: a structured node, or a legacy STRING (a canonical synthetic like
    // `dotkt$CharSequence`, or a clr:/@-prefixed spec) wrapped as a bare Fqn (whose name routes through the string
    // resolvers). null for a non-Fqn structured node.
    static DotKt.Bir.TypeNode.Fqn ReadFqn(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? new DotKt.Bir.TypeNode.Fqn(e.GetString())
        : e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fqn f ? f
        : null;

    // An owner slot (structured Fqn or legacy string) -> (open name, constructed type).
    (string open, Type constructed) ParseOwnerSlot(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fqn f
            ? ParseOwnerT(f) : ParseOwner(e.GetString());

    (string open, Type constructed) ParseOwner(string spec)
    {
        // A bare owner-FQN IDENTITY (the legacy `clr:`/`clrg:` markers are retired — #48); a `[...]` suffix carries the
        // referenced-generic instantiation.
        var br = spec.IndexOf('[');
        if (br < 0) return (spec, null);
        var open = spec.Substring(0, br);
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapType).ToArray();
        if (_types.TryGetValue(open, out var ti)) return (open, ConstructedType(ti.TB, args));
        // Owner not emitted in THIS assembly -> a REFERENCED generic type (e.g. `kotlin.Result[int]` from
        // DotKt.Stdlib.dll): construct it by reflection so ResolveMethod/ResolveField can reflect against the
        // instantiation (its members carry substituted signatures).
        var reflectedName = open.Contains('`') ? open : open + "`" + args.Length;
        return (open, ConstructedType(ResolveType(reflectedName), args));
    }

    // The constructed type's GetX helpers return members whose declared types are still the OPEN params (`!0`);
    // substitute a type-level param by position to its concrete arg so callers box value types correctly.
    // A value type OR a generic parameter must be boxed to become an `object` — a generic param's runtime type is
    // unknown (could be a value type), and `box !!0` is legal/correct for both value and reference instantiations.
    static bool NeedsBoxToRef(Type t) => t != null && (IsValueType(t) || t.IsGenericParameter);

    // Array element STORE. ECMA-335 requires the SPECIALIZED opcode (stelem.i2/i4/…) for a BCL PRIMITIVE
    // element type; the generic token form `stelem <T>` is UNVERIFIABLE for primitives (ilverify:
    // `stelem <char>` -> [StackUnexpected][found Char]). Reference elements -> stelem.ref. A generic-param
    // (`!T`/`!!T`) OR a non-primitive struct element MUST keep the token form -- a generic-param's runtime
    // type is unknown (could be value), and specializing it would be wrong for a value instantiation.
    void EmitStelem(Type elem)
    {
        if (elem.IsGenericParameter) { _il.Emit(OpCodes.Stelem, elem); return; }
        if (!IsValueType(elem)) { _il.Emit(OpCodes.Stelem_Ref); return; }
        if (elem == Bcl("System.Boolean") || elem == Bcl("System.SByte") || elem == Bcl("System.Byte")) _il.Emit(OpCodes.Stelem_I1);
        else if (elem == Bcl("System.Char") || elem == Bcl("System.Int16") || elem == Bcl("System.UInt16")) _il.Emit(OpCodes.Stelem_I2);
        else if (elem == Bcl("System.Int32") || elem == Bcl("System.UInt32")) _il.Emit(OpCodes.Stelem_I4);
        else if (elem == Bcl("System.Int64") || elem == Bcl("System.UInt64")) _il.Emit(OpCodes.Stelem_I8);
        else if (elem == Bcl("System.Single")) _il.Emit(OpCodes.Stelem_R4);
        else if (elem == Bcl("System.Double")) _il.Emit(OpCodes.Stelem_R8);
        else if (elem == Bcl("System.IntPtr") || elem == Bcl("System.UIntPtr")) _il.Emit(OpCodes.Stelem_I);
        else _il.Emit(OpCodes.Stelem, elem); // user struct / enum / Nullable<> -> token form (verifiable)
    }

    // Array element LOAD -- specialized opcode for a BCL primitive, ldelem.ref for a reference, token form
    // (`ldelem <T>`) for a generic-param / non-primitive struct. Mirror of EmitStelem; sign-extends per type
    // (u1/u2 for unsigned+char+bool, i1/i2 for signed).
    void EmitLdelem(Type elem)
    {
        if (elem.IsGenericParameter) { _il.Emit(OpCodes.Ldelem, elem); return; }
        if (!IsValueType(elem)) { _il.Emit(OpCodes.Ldelem_Ref); return; }
        if (elem == Bcl("System.Boolean") || elem == Bcl("System.Byte")) _il.Emit(OpCodes.Ldelem_U1);
        else if (elem == Bcl("System.SByte")) _il.Emit(OpCodes.Ldelem_I1);
        else if (elem == Bcl("System.Char") || elem == Bcl("System.UInt16")) _il.Emit(OpCodes.Ldelem_U2);
        else if (elem == Bcl("System.Int16")) _il.Emit(OpCodes.Ldelem_I2);
        else if (elem == Bcl("System.Int32")) _il.Emit(OpCodes.Ldelem_I4);
        else if (elem == Bcl("System.UInt32")) _il.Emit(OpCodes.Ldelem_U4);
        else if (elem == Bcl("System.Int64") || elem == Bcl("System.UInt64")) _il.Emit(OpCodes.Ldelem_I8);
        else if (elem == Bcl("System.Single")) _il.Emit(OpCodes.Ldelem_R4);
        else if (elem == Bcl("System.Double")) _il.Emit(OpCodes.Ldelem_R8);
        else if (elem == Bcl("System.IntPtr") || elem == Bcl("System.UIntPtr")) _il.Emit(OpCodes.Ldelem_I);
        else _il.Emit(OpCodes.Ldelem, elem); // user struct / enum / Nullable<> -> token form (verifiable)
    }

    static Type Subst(Type t, Type[] typeArgs) =>
        t != null && t.IsGenericParameter && t.DeclaringMethod == null && t.GenericParameterPosition < typeArgs.Length
            ? typeArgs[t.GenericParameterPosition] : t;

    // Emit a body that just throws — stubs a method the backend can't yet emit during the stdlib build.
    void EmitThrowStub(MethodBuilder mb, string feature)
    {
        var il = mb.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "DOTKT-STDLIB stub: " + feature + " not yet supported by the .NET backend");
        EmitConstructor(il, OpCodes.Newobj, Bcl("System.NotSupportedException").GetConstructor(new[] { Bcl("System.String") }));
        il.Emit(OpCodes.Throw);
    }

    // Emit call args, boxing each value arg passed to a reference/object param (param types known explicitly).
    // CIR must already contain the complete physical argument vector. Default realization belongs to bir2cir.
    void EmitArgsTyped(JsonElement args, Type[] pt, MethodInfo mb = null)
    {
        int i = 0;
        foreach (var a in args.EnumerateArray()) { if (pt != null && i < pt.Length) EmitArg(a, pt[i]); else EmitExpr(a); i++; }
        if (pt != null) RequireArgCount(i, pt.Length, mb?.ToString() ?? "typed call");
        else if (mb != null)
        {
            try { RequireArgCount(i, ParametersOf(mb).Length, mb.ToString()); }
            catch (NotSupportedException) { }
        }
    }

    // Emit `new T(..)` ctor args honoring the node's declared ctor param types (`argTypes`): a value/generic-param
    // arg flowing into an `object`/reference ctor param must be BOXED (`Result<T>..ctor(object)` receiving a bare
    // `!!T` was InvalidProgram at a value instantiation), exactly like EmitArgsTyped does for method calls.
    // Falls back to raw emission when the node carries no (or arity-mismatched) argTypes, or a type fails to map.
    void EmitNewArgs(JsonElement e, JsonElement nargs, Type[] classArgs = null)
    {
        Type[] want = null;
        if (e.TryGetProperty("argTypes", out var at) && at.ValueKind == JsonValueKind.Array
            && at.GetArrayLength() == nargs.GetArrayLength())
            want = at.EnumerateArray().Select(x => { try { return CtorArgTarget(x, classArgs); } catch { return null; } }).ToArray();
        int i = 0;
        foreach (var a in nargs.EnumerateArray()) { if (want?[i] != null) EmitArg(a, want[i]); else EmitExpr(a); i++; }
    }

    // The target type for a ctor arg. A `new` node's `argTypes` are the ctor's DECLARED param types — for a generic
    // class those are its OWN open type-vars (`!i`). In a NON-generic caller (`main`), a type-scope tv has no generic
    // param in scope, so MapType/ResolveTv falls back to `object` and the value arg would be BOXED — yet the CONSTRUCTED
    // ctor (`Box<int>::.ctor(!0)`) wants the concrete value `int`. Substitute the declared type-var by its position with
    // the constructed instantiation's concrete arg (`classArgs`) so the target is `int`, not `object`. Inside a generic
    // caller `classArgs[i]` IS the in-scope generic param, so this is a no-op there (matches the prior ResolveTv result).
    Type CtorArgTarget(JsonElement x, Type[] classArgs)
    {
        if (classArgs != null && x.ValueKind == JsonValueKind.Object
            && DotKt.Bir.TypeNode.Read(x) is DotKt.Bir.TypeNode.Tv { Scope: "type" } tv && tv.I < classArgs.Length)
            return classArgs[tv.I];
        return MapType(x);
    }

    // Prefer a BIR-carried concrete result type (`retType`) over reflecting an un-baked builder's `!0`/`!!0`.
    Type RetOr(JsonElement e, Type fallback)
    {
        if (!e.TryGetProperty("ret", out var r)) return fallback;
        var declared = MapType(r);
        // A generic method `<T> f(): T` instantiated with T = kotlin.Unit genuinely PUSHES a kotlin.Unit value, yet a
        // Unit/statement-context call site carries retType="void" (kotc lowers Unit results to void). Trusting that
        // "void" would skip the caller's pop, stranding the kotlin.Unit on the stack (ilverify ReturnVoid — e.g. a
        // discarded `blockOn { …Unit… }`). When the RESOLVED method's actual return (`fallback`, computed by
        // ApplyTypeArgs from the reified type args) is a real non-void type, keep it so the caller pops/uses it. A
        // genuinely void method reports fallback==void here, so this only rescues the generic-Unit-erasure mismatch.
        if (declared == Bcl("System.Void") && fallback != null && fallback != Bcl("System.Void")) return fallback;
        return declared;
    }

    // Boundary conversion after a call whose ACTUAL return is `System.Object` — the erased representation of a
    // generic `T?` (NullableGenericErasure in bir2cir). The caller's statically-known type (`retType`) says
    // what to recover: a value-type nullable `Nullable<V>` via `unbox.any` (a null ref -> HasValue=false; a boxed V
    // -> HasValue=true), a reference type via `castclass` (null stays null). When the caller ALSO wants `object`
    // (an internal nullable->nullable hand-off) there is nothing to do. A non-object actual return is untouched.
    Type CoerceReturn(JsonElement e, Type actual)
    {
        if (actual == Bcl("System.Object") && e.TryGetProperty("ret", out var r))
        {
            var want = MapType(r);
            if (want != null && want != Bcl("System.Object"))
            {
                if (IsValueType(want) || want.IsGenericParameter) { _il.Emit(OpCodes.Unbox_Any, want); return want; }
                _il.Emit(OpCodes.Castclass, want); return want;
            }
        }
        var declared = RetOr(e, actual);
        // The resolved method's actual return type and bir2cir's declared call-RESULT view disagree across a collapsed-
        // variance collection seam. FORWARD: the method returns a MUTABLE interface (Pair.component1() -> IList<T>,
        // Map.Entry.get_value() -> IList<V>) but bir2cir typed the call RESULT as the READONLY sibling (ret/dynRet =
        // IReadOnlyList<T>/IReadOnlyCollection<T>). REVERSE: the nested-literal collapse (BirTypeLowering's `listOf(listOf(
        // …))` builds `List<IList<T>>`) makes an inner call's declared `ret` the collapsed IList<T> while the resolved
        // method's actual type is the readonly IReadOnlyList<T>. Either way the tracked type and the stack slot diverge;
        // without reconciliation a downstream store/receiver/arg trusts the wrong type -> ilverify StackUnexpected. Emit
        // the runtime-checked downcast so they agree at the source. Same family as EmitArg/EmitStoreCoerced (IsCollectionViewSeam).
        if (IsCollectionViewSeam(actual, declared)) _il.Emit(OpCodes.Castclass, declared);
        return declared;
    }

    // Resolve a method on a (possibly generic) interface. When the instantiation carries a TypeBuilder/generic
    // param arg (e.g. IComparable<!!0>), its own GetMethod throws on the persisted builder -> use the static helper.
    MethodInfo InterfaceMethodOn(Type iface, string name, DotKt.Bir.TypeNode[] sig = null, int methodArity = 0)
    {
        if (iface.IsGenericType && (IsTbInstantiation(iface) || iface.GetGenericArguments().Any(a => a.IsGenericParameter || a is TypeBuilder)))
            return AnchorMethod(iface, NamedMethodOn(iface.GetGenericTypeDefinition(), name, sig, methodArity));
        try { return NamedMethodOn(iface, name, sig, methodArity); }
        catch (NotSupportedException) when (iface.IsGenericType)
        {
            return AnchorMethod(iface, NamedMethodOn(iface.GetGenericTypeDefinition(), name, sig, methodArity));
        }
    }

    // The member `name` on `owner`, selected by the descriptor CIR already carries. EXACT and FAIL-CLOSED: the
    // candidate must agree on generic arity, parameter count and — when the node carries a `sig` — every parameter
    // type, and anything other than exactly one survivor is refused. `Type.GetMethod(name)` alone is neither: it
    // throws AmbiguousMatchException as soon as the owner declares an overload, and when it does NOT throw it hands
    // back a member no one checked against the call, so `describe(int)` silently became `describe(string)`. This
    // consumes the producer's answer rather than re-resolving one; a node that arrives without a usable descriptor
    // is a drop upstream, and the message says so instead of guessing an overload.
    MethodInfo NamedMethodOn(Type owner, string name, DotKt.Bir.TypeNode[] sig, int methodArity)
    {
        if (sig == null)
            throw new InvalidOperationException($"ilemit: interface call {owner}.{name} is missing its resolved `sig` descriptor");
        var byName = owner.GetMethods().Where(m => m.Name == name).ToList();
        var candidates = byName
            .Where(m => (methodArity == 0 ? !m.IsGenericMethodDefinition
                    : m.IsGenericMethodDefinition && m.GetGenericArguments().Length == methodArity)
                && m.GetParameters().Length == sig.Length)
            .ToList();
        candidates = candidates
            .Where(m => m.GetParameters().Select((p, i) => Matches(sig[i], p.ParameterType)).All(ok => ok))
            .ToList();
        if (candidates.Count == 1) return candidates[0];
        throw new NotSupportedException(
            $"cannot select '{owner}.{name}' for the call's descriptor "
            + $"({(sig == null ? "no signature" : sig.Length + " parameter(s)")}, generic arity {methodArity}): "
            + $"{candidates.Count} of {byName.Count} same-name member(s) match. "
            + (byName.Count == 0
                ? "The owner does not declare it — the CIR names a type that only INHERITS the member."
                : "The CIR node lost the signature that selects the overload."));
    }

    // Load a managed pointer (&) to an addressable lvalue (for `constrained.` / struct-member calls). Falls back
    // to materializing the value into a temp and taking its address for arbitrary expressions.
    void EmitAddr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                // A slot whose declared type is ALREADY a managed pointer (`ref T`) HOLDS the address — a `var x by
                // byref(m())` delegate local, or the local a call-evaluation plan pins a ref-returning location into.
                // Its address is its VALUE; `Ldloca` would hand out a `ref ref T` the callee cannot use.
                if (_locals.TryGetValue(name, out var l))
                { _il.Emit(l.LocalType.IsByRef ? OpCodes.Ldloc : OpCodes.Ldloca, l); return; }
                if (_args.TryGetValue(name, out var a))
                { _il.Emit(_argTypes[name].IsByRef ? OpCodes.Ldarg : OpCodes.Ldarga, a); return; }
                break;
            }
            case "byrefLoad":
            {
                // Reading through a managed pointer yields the POINTEE; the address of that pointee is the pointer
                // itself. A caller-side UnsafeAccessor field projection carries the pointer expression directly;
                // a `var x by byref(...)` delegate read carries a named byref local. In either form, materializing the
                // pointee into a temp would pass the temp's address and silently lose the callee's write.
                if (e.TryGetProperty("ptr", out var pointer)) { EmitExpr(pointer); return; }
                var bn = e.GetProperty("local").GetString();
                if (_locals.TryGetValue(bn, out var bl) && bl.LocalType.IsByRef) { _il.Emit(OpCodes.Ldloc, bl); return; }
                if (_args.TryGetValue(bn, out var ba) && _argTypes[bn].IsByRef) { _il.Emit(OpCodes.Ldarg, ba); return; }
                break;
            }
            case "this":
                _il.Emit(OpCodes.Ldarg_0);
                return;
            case "field":
                // A `field` node bir2cir resolved to an external ACCESSOR (member:"accessor") is a getter CALL, not a
                // direct backing-field lvalue — its address is the materialized rvalue (temp + Ldloca) below, NOT Ldflda on
                // the private cross-assembly field. Only a genuine direct-field node takes the Ldflda fast path here.
                if (e.TryGetProperty("member", out var fam) && fam.ValueKind == JsonValueKind.String && fam.GetString() == "accessor") break;
                EmitExpr(e.GetProperty("recv"));
                EmitField(_il, OpCodes.Ldflda, ResolveField(ParseOwnerSlot(e.GetProperty("ownerType")), e.GetProperty("name").GetString(), out _));
                return;
            case "staticField":
                EmitField(_il, OpCodes.Ldsflda, ResolveField(ParseOwnerSlot(e.GetProperty("ownerType")), e.GetProperty("name").GetString(), out _));
                return;
            case "arrayGet":
                EmitExpr(e.GetProperty("array"));
                EmitExpr(e.GetProperty("index"));
                _il.Emit(OpCodes.Ldelema, MapType(e.GetProperty("elem")));
                return;
            case "stackGet":
                // The stack-buffer slot's own address (the value path Ldobj's through exactly this).
                EmitStackCheckedAddr(e, MapType(e.GetProperty("elem")));
                return;
        }
        // THE RVALUE FALLBACK: materialize the value and hand out the temporary's address. That is the right answer for
        // an expression that designates no storage — but only when it is a VALUE. An expression that already yields a
        // managed pointer (a `ref`-returning property accessor, say) IS the address; copying it into a `ref T` local and
        // taking THAT address hands the callee a `ref ref T`.
        var t = EmitExpr(e);
        if (t != null && t.IsByRef) return;
        var tmp = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Stloc, tmp);
        _il.Emit(OpCodes.Ldloca, tmp);
    }

    /// Bounds-check a stack-buffer access and push the address of the element: `ptr + index * sizeof(elem)`.
    ///
    /// ONE helper for every stack-slot access — the read, the write and the by-reference argument — because the INDEX
    /// must be evaluated exactly ONCE and both halves need it. Emitting the check and the address as two independent
    /// pieces evaluated `e.index` twice, so `b[i++]` incremented twice and the check ran against a different element
    /// than the access: `Swap(byref(b[i++]), byref(b[i++]))` with `i == 0` left `i == 3` and threw.
    void EmitStackCheckedAddr(JsonElement e, Type elem)
    {
        // The index first and once, into a temp both halves read. (Order is unchanged: index, then len, then ptr.)
        EmitExpr(e.GetProperty("index"));
        var idx = _il.DeclareLocal(Bcl("System.Int32"));
        _il.Emit(OpCodes.Stloc, idx);

        // Throw IndexOutOfRangeException unless 0 <= index < len (unsigned compare catches negatives too).
        _il.Emit(OpCodes.Ldloc, idx);
        EmitExpr(e.GetProperty("len"));
        var ok = _il.DefineLabel();
        _il.Emit(OpCodes.Blt_Un, ok);
        _il.Emit(OpCodes.Ldstr, "StackBuffer index out of bounds");
        EmitConstructor(_il, OpCodes.Newobj, Bcl("System.IndexOutOfRangeException").GetConstructor(new[] { Bcl("System.String") }));
        _il.Emit(OpCodes.Throw);
        _il.MarkLabel(ok);

        EmitExpr(e.GetProperty("ptr"));
        _il.Emit(OpCodes.Ldloc, idx);
        _il.Emit(OpCodes.Sizeof, elem);
        _il.Emit(OpCodes.Mul);
        _il.Emit(OpCodes.Add);
    }

    void EmitArgs(JsonElement args, ParameterInfo[] ps)
    {
        int i = 0;
        foreach (var a in args.EnumerateArray()) { EmitArg(a, ps[i].ParameterType); i++; }
        RequireArgCount(i, ps.Length, "CLR call");
    }

    static void RequireArgCount(int actual, int expected, string target)
    {
        if (actual != expected)
            throw new InvalidOperationException(
                $"ilemit: CIR argument count mismatch for {target}: got {actual}, expected {expected}; " +
                "default and vararg realization must be completed by bir2cir");
    }

    void EmitArgs2(JsonElement[] args, ParameterInfo[] ps)
    {
        for (int i = 0; i < args.Length; i++) EmitArg(args[i], ps[i].ParameterType);
    }

    void EmitArg(JsonElement a, Type want)
    {
        // A by-ref parameter (`out`/`ref`, from the `byref(x)` marker) -> pass the lvalue's address.
        if (want.IsByRef) { EmitAddr(a); return; }
        // (4) A LAMBDA passed to a .NET DELEGATE parameter -> build that SPECIFIC delegate (the FIR types the param
        // as a Kotlin function type; the real delegate is `want`, resolved here from the target method's signature).
        // Mirrors the event path; covers custom delegates (ApplicationInitializationCallback, ThreadStart) and BCL
        // Func/Action alike. Scoped to literal lambdas (newDelegate/newClosure) so stored delegate/Func values keep
        // their existing pass-through path.
        // Skip the rewrap only for a `want` still mentioning an OPEN generic PARAMETER — there is no concrete ctor to
        // bind. Everything else rewraps, including a delegate whose only builder-ness is a TypeBuilder type-arg
        // (`Func<Res,int>`, Res a user class being emitted): DelegateCtor/InvokeOf bridge those via TypeBuilder.GetX.
        // (#220 removed the old assembly-local `KFunc`/`KAction` exemption: a wide delegate in a signature is now the
        // stdlib's canonical baked type, identical on both sides, so there is nothing left to exempt.)
        if (IsDelegateType(want) && want != Bcl("System.Delegate") && want != Bcl("System.MulticastDelegate")
            && !ContainsGenericParameter(want)
            && a.TryGetProperty("k", out var dk) && (dk.GetString() == "newDelegate" || dk.GetString() == "newClosure"))
        {
            EmitHandlerAsDelegate(a, want);
            return;
        }
        // `T`/null passed to a `T?` slot -> Nullable<T> wrap / default(Nullable<T>) (shared with EmitCond).
        var got = EmitNullableCoerced(a, want);
        if (got == null) return;
        // Box a value/generic-param arg passed to a reference param — but NOT when the param is itself a generic
        // param (passing `T` to a `T` slot flows the value as-is at the instantiation).
        if (NeedsBoxToRef(got) && !IsValueType(want) && !want.IsGenericParameter)
            _il.Emit(OpCodes.Box, got);
        // A collapsed-variance collection-interface VALUE flowing into its SIBLING arg SLOT (same element T, either
        // direction): the two sibling interfaces do NOT derive from each other in the BCL type lattice, so the raw flow
        // is StackUnexpected — insert the runtime-checked downcast. FORWARD (IList/ICollection -> IReadOnly*) is the
        // destructuring/for seam; REVERSE (IReadOnly* -> IList/ICollection) is the #100 H1 case: a readonly-faced value
        // (`make(): List<Int>` -> IReadOnlyList) into a collapsed MUTABLE type-arg slot (a `Pair` ctor / map-setter arg
        // whose V collapsed to IList<int>). castclass to a closed interface is always verifiable (never the value-type-
        // generic JIT hazard) and succeeds at runtime because the concrete value (stdlib List<T>/HashSet<T>, or a user
        // mutable collection that also lists the readonly face) implements all faces. Scoped to exactly this family so
        // any OTHER arg/slot mismatch still surfaces (pure CLR reconciliation of bir2cir's collapse — no Kotlin knowledge).
        else if (IsCollectionViewSeam(got, want))
            _il.Emit(OpCodes.Castclass, want);
    }

    // True exactly for the sanctioned COLLAPSED-VARIANCE collection-interface seams (EITHER direction) with an
    // identical single element type. bir2cir's Root-V collapse can put a MUTABLE face where a readonly sibling is
    // expected OR a READONLY face where the collapsed mutable sibling is expected; both are pure CLR structural
    // reconciliations of a variance collapse bir2cir already decided (no Kotlin knowledge). Sanctioned rows (got -> want):
    //   FORWARD (mutable -> readonly): IList->IReadOnlyList, IList->IReadOnlyCollection, ICollection->IReadOnlyCollection.
    //   REVERSE (readonly -> mutable): IReadOnlyList->IList, IReadOnlyList->ICollection, IReadOnlyCollection->ICollection.
    // DIRECTIONAL exclusions (each the transpose of the other; MUST return false so a genuine upstream error still
    // surfaces): ICollection->IReadOnlyList and IReadOnlyCollection->IList — a bare (readonly-)collection promises no
    // indexer, so masking it would hide a real bug. Also NOT matched (real BCL derivations / identity — no cast
    // needed): IList->ICollection, IReadOnlyList->IReadOnlyCollection, got==want. No IDictionary rows (Map collapses to
    // IDictionary at head — no seam). `got`/`want` are reference interface types by construction of the predicate, so
    // no explicit reference-type check is needed; T may be a concrete type, an emitted TypeBuilder, or a generic param.
    // RUNTIME CONTRACT: the FORWARD casts are statically guaranteed (mutable BCL/user collections list the readonly
    // faces — the interface-emit path); the REVERSE casts succeed only because the stdlib collection backing
    // (List<T>/HashSet<T>/T[]) implements every mutable face — a foreign value implementing ONLY the readonly Kotlin
    // face flowing into a collapsed mutable slot would throw InvalidCastException at the seam (fail-loud, and it is
    // bir2cir's collapse decision, not ilemit's — tracked interop follow-up, out of scope here).
    bool IsCollectionViewSeam(Type got, Type want)
    {
        if (got == null || want == null) return false;
        if (!got.IsGenericType || got.IsGenericTypeDefinition) return false;
        if (!want.IsGenericType || want.IsGenericTypeDefinition) return false;
        var ga = got.GetGenericArguments();
        var wa = want.GetGenericArguments();
        if (ga.Length != 1 || wa.Length != 1 || ga[0] != wa[0]) return false;
        var gd = got.GetGenericTypeDefinition();
        var wd = want.GetGenericTypeDefinition();
        if (wd == Bcl("System.Collections.Generic.IReadOnlyList`1"))
            return gd == Bcl("System.Collections.Generic.IList`1");
        if (wd == Bcl("System.Collections.Generic.IReadOnlyCollection`1"))
            return gd == Bcl("System.Collections.Generic.IList`1") || gd == Bcl("System.Collections.Generic.ICollection`1");
        if (wd == Bcl("System.Collections.Generic.IList`1"))
            return gd == Bcl("System.Collections.Generic.IReadOnlyList`1");
        if (wd == Bcl("System.Collections.Generic.ICollection`1"))
            return gd == Bcl("System.Collections.Generic.IReadOnlyList`1") || gd == Bcl("System.Collections.Generic.IReadOnlyCollection`1");
        return false;
    }

    // Coerce a just-emitted return VALUE (static type `got`, on the stack) to the declared method return type.
    // Shared by ALL return sites — the plain `return`, the return-inside-try store into the _methodRetType-typed
    // result local, and both `returnExpr` twins — so every path applies the identical coercion:
    //  - `T` returned where the declared type is `T?` -> wrap in Nullable<T> (e.g. a `sortedBy` selector typed
    //    `(T)->R?` whose body yields a non-null R). Mirrors EmitArg's coercion.
    //  - a value-type / generic-param value returned where the method returns `object` (an erased generic `T?` —
    //    NullableGenericErasure) must be boxed so `ldnull`/boxed-value share the object return. A null-const
    //    return already left a real null (no box). Mirrors the var-store box.
    void EmitReturnCoerced(Type got)
    {
        if (got == null) return;
        if (_methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == Bcl("System.Nullable`1")
            && _methodRetType.GetGenericArguments()[0] == got)
            EmitConstructor(_il, OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
        // A value type / `gp:T` returned where the method declares ANY reference type must BOX (C2: the
        // `compareBy { it }` selector lambda returns `it: Int` declared `kotlin.Comparable[object]` = System.IComparable
        // — the boxed Int IS an IComparable). `box` alone yields the tracked type `O`; when the return is a NON-object
        // reference (an interface / concrete ref type) add `castclass <ret>` so the boxed value verifies as that slot
        // (mirrors the `cast` emitter's box+castclass). Previously only `== object` boxed, so a value flowing into a
        // non-object reference return (`IComparable`) landed unboxed -> a value reinterpreted as a reference -> NRE.
        else if (NeedsBoxToRef(got) && !IsValueType(_methodRetType) && !_methodRetType.IsGenericParameter)
        {
            _il.Emit(OpCodes.Box, got);
            if (_methodRetType != Bcl("System.Object")) _il.Emit(OpCodes.Castclass, _methodRetType);
        }
        // A REFERENCE value (`object` — e.g. an erased generic stdlib return like `clrMapGet<K,V>:object`) returned where
        // the method declares a VALUE type or a generic PARAMETER (`V`) needs the universal cast `unbox.any <ret>` (NOT
        // castclass — `castclass !!V` JIT-crashes value-type instantiations). Without it the reference sits where a value
        // is expected -> ilverify StackUnexpected (found ref 'object', expected value 'V'). Only when it isn't already
        // the exact return type.
        else if (got != _methodRetType && !IsValueType(got) && !got.IsGenericParameter
                 && (IsValueType(_methodRetType) || _methodRetType.IsGenericParameter))
            _il.Emit(OpCodes.Unbox_Any, _methodRetType);
        // The method-return-statement twin of EmitArg/EmitStoreCoerced/CoerceReturn: a collapsed-variance collection
        // interface VALUE returned where the method declares its SIBLING (same T), distinct from CoerceReturn (which
        // reconciles a CALL RESULT, not a return statement). FORWARD: a mutable local (`val m = mutableListOf<Int>()` ->
        // IList<int32>) returned from `fun f(): List<Int>` -> IReadOnlyList<int32>. REVERSE fires only when _methodRetType
        // is a top-level IList/ICollection whose type was SUBSTITUTED from a collapsed typeArg (e.g. a synthesized
        // closure/SAM `invoke` whose result `R` collapsed) — correct-by-symmetry, and the predicate can't fire on anything else.
        else if (IsCollectionViewSeam(got, _methodRetType))
            _il.Emit(OpCodes.Castclass, _methodRetType);
    }

    // Args for a user method/ctor, boxing value types passed to reference (e.g. `object`/`Any`) params.
    // When the param type is unknown (lifted/unrecorded), emit the arg as-is (no spurious boxing).
    void EmitCallArgs(JsonElement args, MethodInfo mb)
    {
        var pt = _mparams.TryGetValue(mb, out var p) ? p : null;
        // An in-assembly method's declared params live in `_mparams`; a REFERENCED method's don't (MethodBuilder can't
        // be reflected pre-bake, but a resolved referenced MethodInfo can). Read its real ParameterInfo so a value-type
        // / Nullable<> / gp: arg still BOXES into an `object`/reference param — mirrors EmitArgsTyped and the typeArgs
        // referenced path. Without this the `pt==null` branch emitted the arg raw (no box) -> InvalidProgram for e.g.
        // `toString(object)` of an `Int?` (`box Nullable<int>` yields the boxed underlying value, or null).
        var ps = pt == null ? ParametersOf(mb) : null;
        int i = 0;
        foreach (var a in args.EnumerateArray())
        {
            if (pt != null && i < pt.Length) EmitArg(a, pt[i]);
            else if (ps != null && i < ps.Length) EmitArg(a, ps[i].ParameterType);
            else EmitExpr(a);
            i++;
        }
        RequireArgCount(i, pt?.Length ?? ps.Length, mb.ToString());
    }

}
