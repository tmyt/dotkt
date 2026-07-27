// bir2cir — SuspendColdLowering.Normalize: pre-segmentation body normalization for the cold suspend SM.
//
// FunGen.Build cannot segment a suspension that sits in a position the straight-line state machine has no
// entry point for. Two such positions occur in the kotlinx.coroutines port; each is normalized into an
// equivalent position the SM CAN segment, BEFORE CollectVarFields / EmitStmt run:
//
//   #82/#98 FlattenSuspendingLoops — a STRUCTURED loop (`forArray` / `forEachInline` / counted `for`) whose body spans
//        a suspension is desugared to flat `label`/`brIf`/`goto` CFG with its implicit loop machinery made
//        EXPLICIT `{k:var}` (array + index; a non-generic IEnumerator; or the counted-loop variable), so
//        CollectVarFields spills those temps into SM fields (the `load unknown var __inlsN$element` root) and the
//        resume can re-enter the loop across the back-edge. `forRange`/`repeatInline` are NOT flattened (an app-build
//        range is already realized as counted `for`; the remaining stdlib-only `forRange` and repeat shapes stay
//        unsupported).
//
//   #78 HoistSuspendingCatches — a suspension inside a CATCH/FINALLY handler is impossible to resume into (branching
//        into a CLR catch clause is illegal IL). The handler is HOISTED out of the clause: the real catch only
//        records the exception into an SM-field-backed `__exc$N`, and the handler body runs as gated
//        straight-line code (`if (__exc$N != null) { … }`) AFTER the try — where the SM segments it normally.
//        A finally is kept around the hoisted catch tail when it is synchronous. A suspending finally is moved
//        after a catch-all exception capture; pending returns are routed through it, and a pending exception is
//        rethrown only after the finally completes. Thus Kotlin's body -> catch -> finally ordering is retained.
//
// Both are clone-on-change (return the input array untouched when nothing applies, else a private DeepClone —
// never mutating the shared/retained rt-stdlib original), skip nested lambda scopes, and run post-order
// (innermost first) so nested loops/hoists compose. AssertLocalsResolved is the post-Build tripwire that
// converts any residual unspilled local into a loud bir2cir error instead of a distant ilemit `load unknown var`.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class SuspendColdLowering
{
    // Genuine nested-scope kinds (their interior locals/suspensions belong to their OWN SM/closure): normalization
    // never descends into them. NOTE this is DISJOINT from the loop kinds — `forEachInline`/`repeatInline` are in
    // LambdaKinds for the subtree-skip analyses, but here they are LOOPS we DO descend into / flatten.
    static readonly HashSet<string> NestedScopeKinds = new(System.StringComparer.Ordinal)
        { "newClosure", "newDelegate", "lambda", "newSuspendLambda" };

    // Structured loop node kinds (ilemit emits each as a native IL loop with its own break/continue frame). Used to
    // scope break/continue rewriting (an unlabeled break/continue inside a still-structured nested loop belongs to it).
    // Includes `while`/`dowhile` defensively: kotc lowers Kotlin whiles to flat CFG, but a future pre-SM pass could
    // wrap statements in a structured while whose own break/continue must not be hijacked into a flattened loop.
    static readonly HashSet<string> LoopKinds = new(System.StringComparer.Ordinal)
        { "forArray", "forEachInline", "for", "forRange", "repeatInline", "while", "dowhile" };

    // Statement-list-valued keys (elements are statements, not operand expressions).
    static readonly HashSet<string> StmtListKeys = new(System.StringComparer.Ordinal)
        { "body", "stmts", "finally" };

    // A try needs handler hoisting iff at least one catch or its finally spans a suspension.
    // Shared by SuspensionRefusalReason (the gate admits the shape) and HoistSuspendingCatches (it performs the hoist).
    static bool IsHoistableTry(JsonObject o)
    {
        if (Str(o["k"]) != "try") return false;
        if (o["catches"] is JsonArray cs)
            foreach (var c in cs)
                if (c is JsonObject co && co["body"] is JsonNode cb && HasOwnSuspension(cb)) return true;
        if (o["finally"] is JsonArray fa && HasOwnSuspension(fa)) return true;
        return false;
    }

    sealed partial class FunGen
    {
        // ---- #82 loop flatten -----------------------------------------------------------------------------

        JsonArray FlattenSuspendingLoops(JsonArray body)
        {
            if (!HasFlattenableLoop(body)) return body;         // byte-identical when nothing to flatten
            return FlattenStmtList(body);
        }

        static bool HasFlattenableLoop(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k != null && NestedScopeKinds.Contains(k)) return false;
                    if ((k == "forArray" || k == "forEachInline" || k == "for")
                        && o["body"] is JsonNode lb && HasOwnSuspension(lb))
                        return true;
                    foreach (var kv in o) if (kv.Value != null && HasFlattenableLoop(kv.Value)) return true;
                    return false;
                case JsonArray a:
                    foreach (var it in a) if (it != null && HasFlattenableLoop(it)) return true;
                    return false;
                default:
                    return false;
            }
        }

        // Rewrite a statement list, expanding each flattenable suspending loop element into its flat CFG sequence.
        JsonArray FlattenStmtList(JsonArray stmts)
        {
            var result = new JsonArray();
            foreach (var s in stmts)
            {
                var fs = FlattenNode(s);                         // post-order: nested loops in this stmt already flat
                if (fs is JsonObject o && Str(o["k"]) is string k
                    && (k == "forArray" || k == "forEachInline" || k == "for")
                    && o["body"] is JsonArray lb && HasOwnSuspension(lb))
                    EmitFlatLoop(o, result);
                else
                    result.Add(fs);
            }
            return result;
        }

        // Functionally rebuild a node, flattening loops inside its statement-list children. Skips nested lambda scopes.
        JsonNode FlattenNode(JsonNode n)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k != null && NestedScopeKinds.Contains(k)) return o.DeepClone();
                var copy = new JsonObject();
                foreach (var kv in o)
                {
                    if (kv.Value is JsonArray arr && StmtListKeys.Contains(kv.Key))
                        copy[kv.Key] = FlattenStmtList(arr);
                    else if (kv.Key == "catches" && kv.Value is JsonArray cs)
                        copy[kv.Key] = RebuildCatches(cs, FlattenStmtList);
                    else
                        copy[kv.Key] = kv.Value == null ? null : FlattenNode(kv.Value);
                }
                return copy;
            }
            if (n is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : FlattenNode(it));
                return copy;
            }
            return n?.DeepClone();
        }

        // Append the flat CFG for a suspending forArray / forEachInline / counted for. `o.body` is already flattened
        // (post-order).
        void EmitFlatLoop(JsonObject o, JsonArray result)
        {
            var k = Str(o["k"]);
            var loopVar = Str(o["var"]);
            var loopLabel = Str(o["label"]);                    // may be null (unlabeled)
            var bodyArr = (JsonArray)((o["body"] as JsonArray)?.DeepClone() ?? new JsonArray());
            var contId = NextLabel();
            var endId = NextLabel();
            var startId = NextLabel();
            RewriteBreakContinue(bodyArr, loopLabel, contId, endId, insideNestedLoop: false);
            var kc = ++_loopCounter;

            if (k == "for")
            {
                var cmp = Str(o["cmp"]);
                if (cmp is not ("<=" or "<" or ">="))
                    throw new System.NotSupportedException(
                        $"suspend-lowering (#98): counted for in '{(_ownerClass ?? _fileClass)}.{_name}' carries unsupported cmp '{cmp}'.");
                if (o["step"] is not JsonValue stepValue || !stepValue.TryGetValue<int>(out var step))
                    throw new System.NotSupportedException(
                        $"suspend-lowering (#98): counted for in '{(_ownerClass ?? _fileClass)}.{_name}' carries no integer step.");

                // Match ilemit's structured counted-loop semantics exactly: evaluate `from` once, evaluate `to` at
                // each header visit, execute the body while cmp(i,to), and route continue through the increment.
                result.Add(Var(loopVar, Tw(IntTn), o["from"]?.DeepClone()));
                result.Add(Label(startId));
                result.Add(BrIf(Bin(cmp, LocalOf(loopVar), o["to"]?.DeepClone()), false, endId));
                foreach (var st in bodyArr) result.Add(st?.DeepClone());
                result.Add(Label(contId));
                result.Add(new JsonObject
                {
                    ["k"] = "setLocal", ["name"] = loopVar,
                    ["value"] = Bin("+", LocalOf(loopVar), IntConst(step)),
                });
                result.Add(Goto(startId));
                result.Add(Label(endId));
                return;
            }

            if (k == "forArray")
            {
                var elemTn = TypeJson.Read(o["elem"]);
                if (elemTn == null)
                    throw new System.NotSupportedException(
                        $"suspend-lowering (#82): forArray in '{(_ownerClass ?? _fileClass)}.{_name}' carries no `elem` — cannot flatten "
                        + "(ArrayConstructionLowering should have stamped it before the cold pass).");
                var arrName = "__arr$" + kc;
                var idxName = "__i$" + kc;
                result.Add(Var(arrName, Tw(new TypeNode.Array(elemTn)), o["array"]?.DeepClone()));
                result.Add(Var(idxName, Tw(IntTn), IntConst(0)));
                result.Add(Label(startId));
                result.Add(BrIf(Bin("<", LocalOf(idxName), new JsonObject { ["k"] = "arrayLen", ["array"] = LocalOf(arrName) }), false, endId));
                result.Add(Var(loopVar, Tw(elemTn), new JsonObject
                {
                    ["k"] = "arrayGet", ["elem"] = Tw(elemTn), ["array"] = LocalOf(arrName), ["index"] = LocalOf(idxName),
                }));
                foreach (var st in bodyArr) result.Add(st?.DeepClone());
                result.Add(Label(contId));
                result.Add(new JsonObject { ["k"] = "setLocal", ["name"] = idxName, ["value"] = Bin("+", LocalOf(idxName), IntConst(1)) });
                result.Add(Goto(startId));
                result.Add(Label(endId));
                return;
            }

            // forEachInline — a NON-GENERIC enumerator (mirrors ilemit's viaNonGeneric fallback UNCONDITIONALLY, so an
            // open generic-param `elem` never mints a broken IEnumerable<!!T> TypeBuilder token): castclass IEnumerable;
            // GetEnumerator -> IEnumerator; MoveNext; get_Current (object) -> (cast/unbox.any) elem. Codex-verified sound.
            var elemT = TypeJson.Read(o["elem"]) ?? AnyTn;
            var enName = "__en$" + kc;
            const string IEnumerable = "System.Collections.IEnumerable";
            const string IEnumerator = "System.Collections.IEnumerator";
            result.Add(Var(enName, Tn(IEnumerator), new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = Tn(IEnumerable), ["method"] = "GetEnumerator",
                ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["recv"] = new JsonObject { ["k"] = "cast", ["type"] = Tn(IEnumerable), ["e"] = o["src"]?.DeepClone() },
                ["ret"] = Tn(IEnumerator),
            }));
            result.Add(Label(startId));
            result.Add(BrIf(new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = Tn(IEnumerator), ["method"] = "MoveNext",
                ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["recv"] = LocalOf(enName), ["ret"] = Tw(BoolTn),
            }, false, endId));
            result.Add(Var(loopVar, Tw(elemT), new JsonObject
            {
                ["k"] = "cast", ["type"] = Tw(elemT),
                ["e"] = new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = Tn(IEnumerator), ["method"] = "get_Current",
                    ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                    ["recv"] = LocalOf(enName), ["ret"] = Tw(AnyTn),
                },
            }));
            foreach (var st in bodyArr) result.Add(st?.DeepClone());
            result.Add(Label(contId));                          // continue -> re-MoveNext
            result.Add(Goto(startId));
            result.Add(Label(endId));
        }

        // Rewrite each break/continue that targets the loop being flattened into a goto of its end/cont label. An
        // unlabeled break/continue targets the innermost FLATTENED loop (so stop for ones inside a still-structured
        // nested loop — they belong to it); a labeled one is rewritten wherever it matches this loop's label. Mutates
        // in place (bodyArr is a private clone). Skips nested lambda scopes.
        void RewriteBreakContinue(JsonNode n, string loopLabel, int contId, int endId, bool insideNestedLoop)
        {
            if (n is JsonArray a)
            {
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonObject o && Str(o["k"]) is "break" or "continue")
                    {
                        var tgt = Str(o["label"]);
                        var matches = tgt != null ? tgt == loopLabel : !insideNestedLoop;
                        if (matches) a[i] = Goto(Str(o["k"]) == "break" ? endId : contId);
                    }
                    else if (a[i] != null) RewriteBreakContinue(a[i], loopLabel, contId, endId, insideNestedLoop);
                }
                return;
            }
            if (n is JsonObject obj)
            {
                var k = Str(obj["k"]);
                if (k != null && NestedScopeKinds.Contains(k)) return;    // lambda scope owns its break/continue
                var nested = insideNestedLoop || (k != null && LoopKinds.Contains(k));   // a still-structured nested loop
                foreach (var kv in obj) if (kv.Value != null) RewriteBreakContinue(kv.Value, loopLabel, contId, endId, nested);
            }
        }

        // ---- #78 catch hoist ------------------------------------------------------------------------------

        JsonArray HoistSuspendingCatches(JsonArray body)
        {
            if (!HasHoistableCatch(body)) return body;          // byte-identical when nothing to hoist
            return HoistStmtList(body);
        }

        static bool HasHoistableCatch(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k != null && NestedScopeKinds.Contains(k)) return false;
                    if (k == "try" && IsHoistableTry(o)) return true;
                    foreach (var kv in o) if (kv.Value != null && HasHoistableCatch(kv.Value)) return true;
                    return false;
                case JsonArray a:
                    foreach (var it in a) if (it != null && HasHoistableCatch(it)) return true;
                    return false;
                default:
                    return false;
            }
        }

        JsonArray HoistStmtList(JsonArray stmts)
        {
            var result = new JsonArray();
            foreach (var s in stmts)
            {
                var hs = HoistNode(s);                           // post-order: nested hoistable trys already hoisted
                if (hs is JsonObject o && Str(o["k"]) == "try" && IsHoistableTry(o))
                    EmitHoistedTry(o, result);
                else
                    result.Add(hs);
            }
            return result;
        }

        JsonNode HoistNode(JsonNode n)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k != null && NestedScopeKinds.Contains(k)) return o.DeepClone();
                var copy = new JsonObject();
                foreach (var kv in o)
                {
                    if (kv.Value is JsonArray arr && StmtListKeys.Contains(kv.Key))
                        copy[kv.Key] = HoistStmtList(arr);
                    else if (kv.Key == "catches" && kv.Value is JsonArray cs)
                        copy[kv.Key] = RebuildCatches(cs, HoistStmtList);
                    else
                        copy[kv.Key] = kv.Value == null ? null : HoistNode(kv.Value);
                }
                return copy;
            }
            if (n is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : HoistNode(it));
                return copy;
            }
            return n?.DeepClone();
        }

        // Emit: [ var __exc$K = null (per suspending clause) ]  try{ BODY }catch(recording | real){…}
        //       [ if(__exc$K!=null){ HANDLER } per clause ], retaining/hoisting FINALLY as described below.
        // The try body is verbatim (its own suspensions ride EmitTry's two-level dispatch). A suspending catch becomes a
        // recording clause (`__exc$K = e`) whose SM-field-backed capture keeps the exception across the hoisted handler's
        // suspension; a non-suspending catch stays a real CLR catch. The handler's catch-var refs rebind to `__exc$K`.
        void EmitHoistedTry(JsonObject o, JsonArray result)
        {
            var newCatches = new JsonArray();
            var tail = new List<JsonNode>();
            if (o["catches"] is JsonArray cs)
                foreach (var c in cs)
                {
                    if (c is not JsonObject co) { newCatches.Add(c?.DeepClone()); continue; }
                    var handlerBody = (co["body"] as JsonArray) ?? new JsonArray();
                    if (!HasOwnSuspension(handlerBody)) { newCatches.Add(co.DeepClone()); continue; }

                    var kc = ++_excCounter;
                    var excName = "__exc$" + kc;
                    var catchVar = Str(co["var"]);
                    // The recording clause binds the exception to a FRESH `$`-infixed catch var, never the source name:
                    // a source catch var (`e`) colliding with a spilled outer SM field of the same name would make the
                    // recorder's `local(e)` rewrite to `FieldOf(e)` (this catch binding is synthesized in bir2cir,
                    // after kotc's declaration-identity slot allocation)
                    // → it would record the wrong object into the control-flow-load-bearing `__exc$K`. `__caught$K` cannot
                    // collide (no field uses that name). The hoisted handler rebinds the SOURCE name to `__exc$K` below.
                    var caughtName = "__caught$" + kc;
                    var excTn = TypeJson.Read(co["excType"]) ?? AnyTn;      // exceptions are reference types (nullable ref == ref)
                    var nullableExc = Tw(new TypeNode.Nullable(excTn));
                    // Capture var (an SM field via CollectVarFields) declared BEFORE the try.
                    result.Add(new JsonObject { ["k"] = "var", ["name"] = excName, ["type"] = nullableExc, ["init"] = NullConst(new TypeNode.Nullable(excTn)) });
                    // The real catch only records the exception (via the fresh, collision-proof catch var).
                    newCatches.Add(new JsonObject
                    {
                        ["excType"] = co["excType"]?.DeepClone(),
                        ["var"] = caughtName,
                        ["body"] = new JsonArray { new JsonObject { ["k"] = "setLocal", ["name"] = excName, ["value"] = LocalOf(caughtName) } },
                    });
                    // The hoisted handler, gated on the capture being non-null; catch-var refs -> __exc$K. Each stmt is a
                    // fresh DeepClone (a parentless node) so re-parenting it into the enclosing statement list is legal.
                    var skipL = NextLabel();
                    tail.Add(BrIf(new JsonObject { ["k"] = "objEq", ["lhs"] = LocalOf(excName), ["rhs"] = NullConst(AnyTn) }, true, skipL));
                    foreach (var st in handlerBody)
                    {
                        var cloned = st?.DeepClone();
                        if (cloned != null && catchVar != null) SubstLocalName(cloned, catchVar, excName);
                        tail.Add(cloned);
                    }
                    tail.Add(Label(skipL));
                }

            var innerTry = new JsonObject
            {
                ["k"] = "try",
                ["type"] = o["type"]?.DeepClone(),
                ["body"] = (o["body"] as JsonArray)?.DeepClone() ?? new JsonArray(),
                ["catches"] = newCatches,
            };

            if (o["finally"] is not JsonArray fin || fin.Count == 0)
            {
                result.Add(innerTry);
                foreach (var t in tail) result.Add(t);
                return;
            }

            // The catch tail must complete before finally. For a synchronous finally, wrapping the inner try and
            // hoisted tail in an outer CLR try/finally preserves precisely that order (and also runs finally when a
            // hoisted handler throws). Nested resume dispatch is supported by RegisterResume.
            // A finally-only source try has no catches to retain after the finally is hoisted. Do not leave a
            // handler-less `try` in CIR: it has no CLR representation (BeginExceptionBlock/EndExceptionBlock without
            // a catch/finally is rejected by Reflection.Emit), and ilemit must not infer that it should be unwrapped.
            var protectedBody = new JsonArray();
            if (newCatches.Count > 0)
                protectedBody.Add(innerTry);
            else if (o["body"] is JsonArray plainBody)
                foreach (var st in plainBody) protectedBody.Add(st?.DeepClone());
            foreach (var t in tail) protectedBody.Add(t);
            if (!HasOwnSuspension(fin))
            {
                result.Add(new JsonObject
                {
                    ["k"] = "try",
                    ["type"] = o["type"]?.DeepClone(),
                    ["body"] = protectedBody,
                    ["catches"] = new JsonArray(),
                    ["finally"] = fin.DeepClone(),
                });
                return;
            }

            EmitSuspendingFinally(protectedBody, fin, o["type"], result);
        }

        // A CLR finally cannot suspend. Turn
        //
        //   try { PROTECTED } finally { FIN }
        //
        // into an exception/return route:
        //
        //   var pending: Throwable? = null
        //   try { PROTECTED' } catch (t: Throwable) { pending = t }
        // route:
        //   FIN
        //   if (pending != null) throw pending
        //   if (returning) return returnValue
        //
        // PROTECTED' rewrites function returns to save their value and leave to `route`; ilemit emits that goto as
        // `leave` because the target label is outside the protected region. FIN's own return/throw is deliberately
        // untouched and therefore overrides the pending outcome, exactly as Kotlin finally does. All route state is
        // declared as ordinary BIR vars and is consequently spilled to SM fields by CollectVarFields.
        void EmitSuspendingFinally(JsonArray protectedBody, JsonArray fin, JsonNode tryType, JsonArray result)
        {
            var kc = ++_excCounter;
            var pendingName = "__pendingExc$" + kc;
            var caughtName = "__pendingCaught$" + kc;
            var routeLabel = NextLabel();
            var throwable = new TypeNode.Fqn("kotlin.Throwable");
            result.Add(Var(pendingName, Tw(new TypeNode.Nullable(throwable)), NullConst(new TypeNode.Nullable(throwable))));

            var hasReturn = HasFunctionReturn(protectedBody);
            string returningName = null;
            string returnValueName = null;
            TypeNode returnValueType = null;
            if (hasReturn)
            {
                returningName = "__returning$" + kc;
                returnValueName = "__returnValue$" + kc;
                returnValueType = IsUnitTn(_resultType) ? AnyTn : _resultType;
                result.Add(Var(returningName, Tw(BoolTn), BoolConst(false)));
                result.Add(Var(returnValueName, Tw(returnValueType), DefaultOf(returnValueType)));
                protectedBody = RouteFunctionReturns(
                    protectedBody, returningName, returnValueName, returnValueType, routeLabel);
            }

            result.Add(new JsonObject
            {
                ["k"] = "try",
                ["type"] = tryType?.DeepClone(),
                ["body"] = protectedBody,
                ["catches"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["excType"] = Tw(throwable),
                        ["var"] = caughtName,
                        ["body"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["k"] = "setLocal", ["name"] = pendingName,
                                ["value"] = LocalOf(caughtName),
                            },
                        },
                    },
                },
            });
            result.Add(Label(routeLabel));
            foreach (var st in fin) result.Add(st?.DeepClone());

            var noPending = NextLabel();
            result.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq", ["lhs"] = LocalOf(pendingName), ["rhs"] = NullConst(AnyTn),
            }, true, noPending));
            result.Add(new JsonObject { ["k"] = "throw", ["value"] = LocalOf(pendingName) });
            result.Add(Label(noPending));

            if (hasReturn)
            {
                var noReturn = NextLabel();
                result.Add(BrIf(LocalOf(returningName), false, noReturn));
                result.Add(new JsonObject { ["k"] = "return", ["value"] = LocalOf(returnValueName) });
                result.Add(Label(noReturn));
            }
        }

        static bool HasFunctionReturn(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k != null && NestedScopeKinds.Contains(k)) return false;
                    if (k == "return") return true;
                    foreach (var kv in o) if (kv.Value != null && HasFunctionReturn(kv.Value)) return true;
                    return false;
                case JsonArray a:
                    foreach (var it in a) if (it != null && HasFunctionReturn(it)) return true;
                    return false;
                default:
                    return false;
            }
        }

        JsonArray RouteFunctionReturns(
            JsonArray stmts, string returningName, string returnValueName, TypeNode returnValueType, int routeLabel)
        {
            var result = new JsonArray();
            foreach (var st in stmts)
            {
                if (st is JsonObject o && Str(o["k"]) == "return")
                {
                    result.Add(new JsonObject
                    {
                        ["k"] = "setLocal", ["name"] = returnValueName,
                        ["value"] = o["value"]?.DeepClone() ?? DefaultOf(returnValueType),
                    });
                    result.Add(new JsonObject
                    {
                        ["k"] = "setLocal", ["name"] = returningName, ["value"] = BoolConst(true),
                    });
                    result.Add(Goto(routeLabel));
                }
                else
                    result.Add(RouteReturnsNode(st, returningName, returnValueName, returnValueType, routeLabel));
            }
            return result;
        }

        JsonNode RouteReturnsNode(
            JsonNode n, string returningName, string returnValueName, TypeNode returnValueType, int routeLabel)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k != null && NestedScopeKinds.Contains(k)) return o.DeepClone();
                var copy = new JsonObject();
                foreach (var kv in o)
                {
                    if (kv.Value is JsonArray arr && StmtListKeys.Contains(kv.Key))
                        copy[kv.Key] = RouteFunctionReturns(
                            arr, returningName, returnValueName, returnValueType, routeLabel);
                    else if (kv.Key == "catches" && kv.Value is JsonArray cs)
                        copy[kv.Key] = RebuildCatches(
                            cs, a => RouteFunctionReturns(
                                a, returningName, returnValueName, returnValueType, routeLabel));
                    else
                        copy[kv.Key] = kv.Value == null ? null
                            : RouteReturnsNode(
                                kv.Value, returningName, returnValueName, returnValueType, routeLabel);
                }
                return copy;
            }
            if (n is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a)
                    copy.Add(it == null ? null
                        : RouteReturnsNode(
                            it, returningName, returnValueName, returnValueType, routeLabel));
                return copy;
            }
            return n?.DeepClone();
        }

        // Rebind every `{k:local}`/`{k:setLocal}` naming `oldName` to `newName` (the hoisted catch var -> its SM-field
        // capture). Mutates in place; skips nested lambda scopes (a same-named local there is that scope's own).
        static void SubstLocalName(JsonNode n, string oldName, string newName)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k != null && NestedScopeKinds.Contains(k)) return;
                if ((k == "local" || k == "setLocal") && Str(o["name"]) == oldName) o["name"] = newName;
                foreach (var kv in o) if (kv.Value != null) SubstLocalName(kv.Value, oldName, newName);
            }
            else if (n is JsonArray a)
                foreach (var it in a) if (it != null) SubstLocalName(it, oldName, newName);
        }

        // ---- shared helpers -------------------------------------------------------------------------------

        // Rebuild a `catches` array, applying `stmtRewrite` to each clause body (which is a statement list).
        static JsonArray RebuildCatches(JsonArray catches, System.Func<JsonArray, JsonArray> stmtRewrite)
        {
            var nc = new JsonArray();
            foreach (var c in catches)
            {
                if (c is JsonObject co)
                {
                    var cc = new JsonObject();
                    foreach (var kv in co)
                        cc[kv.Key] = kv.Key == "body" && kv.Value is JsonArray cb ? stmtRewrite(cb)
                                     : kv.Value?.DeepClone();
                    nc.Add(cc);
                }
                else nc.Add(c?.DeepClone());
            }
            return nc;
        }

        static JsonObject Var(string name, JsonNode typeJson, JsonNode init) => new()
        {
            ["k"] = "var", ["name"] = name, ["type"] = typeJson, ["init"] = init,
        };

        static JsonObject LocalOf(string name) => new() { ["k"] = "local", ["name"] = name };

        static JsonObject Bin(string op, JsonNode lhs, JsonNode rhs) => new()
        {
            ["k"] = "binOp", ["op"] = op, ["lhs"] = lhs, ["rhs"] = rhs,
        };

        // ---- #82 post-Build tripwire ----------------------------------------------------------------------

        // Every `{k:local}`/`{k:setLocal}` in the emitted invokeSuspend must resolve to a declaration the emitter can
        // bind: the `result` param, an SM `{k:var}`, or a catch/structured-loop var. An SM FIELD is `{k:field}` post-
        // Rewrite, so a residual bare `{k:local}` naming a field (or naming nothing) is exactly the #82 unspilled-local
        // bug — it would reach ilemit as `load unknown var`. Fail loud here naming the SM/fun/local instead.
        void AssertLocalsResolved(JsonArray invoke)
        {
            var declared = new HashSet<string>(System.StringComparer.Ordinal) { "result" };
            var used = new List<string>();
            void Collect(JsonNode n)
            {
                switch (n)
                {
                    case JsonObject o:
                        var k = Str(o["k"]);
                        if (k != null && NestedScopeKinds.Contains(k)) return;   // its own scope
                        if (k == "var" && Str(o["name"]) is string vn) declared.Add(vn);
                        if (k == "try" && o["catches"] is JsonArray cs)
                            foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cvn) declared.Add(cvn);
                        if (k != null && LoopKinds.Contains(k) && Str(o["var"]) is string lvn) declared.Add(lvn);
                        if ((k == "local" || k == "setLocal") && Str(o["name"]) is string un) used.Add(un);
                        foreach (var kv in o) if (kv.Value != null) Collect(kv.Value);
                        return;
                    case JsonArray a:
                        foreach (var it in a) if (it != null) Collect(it);
                        return;
                }
            }
            foreach (var st in invoke) if (st != null) Collect(st);
            foreach (var u in used)
                if (!declared.Contains(u))
                    throw new System.InvalidOperationException(
                        $"bir2cir suspend-lowering (#82): SM '{_smType}' invokeSuspend for '{(_ownerClass ?? _fileClass)}.{_name}' "
                        + $"references unspilled local '{u}' (neither the `result` param, an SM `var`, nor a catch/loop var). "
                        + "A splice-generated local crossing a resume point was not spilled into an SM field — it would reach "
                        + "ilemit as `load unknown var`.");
        }
    }
}
