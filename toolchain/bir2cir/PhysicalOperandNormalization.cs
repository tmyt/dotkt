using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// PHYSICAL ORDERED-OPERAND NORMALIZATION (#710). Kotlin statements in an operand may return, jump to a loop
// target, or enter a protected region. None may inherit earlier operands on the CLR evaluation stack: ret needs
// only its return value, loop targets need the loop's empty stack, and protected regions start empty.
//
// bir2cir authors that physical order using existing var/local/valueBlock vocabulary. A hazardous later operand
// moves as a WHOLE to a preceding typed local, with its earlier operands snapshotted before it. Moving a whole
// conditional rather than extracting one arm's statements preserves taken/untaken paths. Conditional arms, loop
// conditions and valueBlock results have site-local prefixes so work cannot escape its guard or dynamic extent.
// kotc keeps Kotlin facts; ilemit emits the authored sequence without reconstructing Kotlin control flow.
static class PhysicalOperandNormalization
{
    static int _tmp;
    static ValueTypeOracle _isValue = _ => false;
    static Func<JsonNode, BirScope, TypeNode> _typeOf;
    static JsonNode _isValueTypeRef;

    public static void Apply(JsonNode root, ValueTypeOracle isValue, Func<JsonNode, BirScope, TypeNode> typeOf,
        ReferenceMetadataIndex refs)
    {
        _isValue = isValue ?? (_ => false);
        _typeOf = typeOf;
        var typeOwner = refs.ResolveRefType("System.Type");
        _isValueTypeRef = ClrMemberResolution.MemberRefJson(typeOwner.GetProperty("IsValueType").GetMethod,
            "method", typeOwner, null);
        Walk(root, BirScope.Empty);
    }

    // Statement-list-valued keys (their elements are statements, not operand expressions).
    static readonly HashSet<string> StmtListKeys = new(StringComparer.Ordinal) { "body", "stmts", "finally", "preStmts" };
    // Keys the generic operand recurse must NOT descend into (they are statement lists, handled by Walk).
    static readonly HashSet<string> SkipGenericKeys = new(StringComparer.Ordinal) { "body", "stmts", "finally", "preStmts", "catches", "branches", "synthClass" };
    // Q4 of the four value questions (roster in bir-common/ValueStability.cs) — STACK-NEUTRAL: may this operand stay
    // in its slot when a LATER sibling hoists out of the expression? A hoist moves the sibling's evaluation to a
    // PRECEDING statement, so every operand left of it now runs after what used to run after it. That is invisible
    // only for an operand whose value cannot change and whose evaluation cannot fail — a literal or `this`.
    // A plain local read can observe a later operand's write, so it must be snapshotted too.
    // Anything else (including a load that merely dereferences, so `arrayGet`/`field` too) is spilled to a temp by
    // SpillIfNeeded, which is what preserves left-to-right order.
    //
    // Not a restatement of Q2 (droppable) or Q3 (resume-stable): this one asks about ORDER across a stack boundary,
    // not about skipping an evaluation or about surviving a resume, and the three land differently per kind.
    static readonly HashSet<string> StackNeutralKinds = new(StringComparer.Ordinal) { "const", "this" };

    // Value-list keys whose elements ilemit evaluates while a construction receiver (and, for an array, its index)
    // is already on the CLR stack. These are deliberately keyed by NODE KIND: arrays such as `sig`, `argTypes`,
    // `typeArgs`, and declaration `params` are type/signature vocabulary, not ordered value operands. A boundary in even
    // the FIRST listed element therefore starts non-empty and must move.
    static readonly Dictionary<string, string> ConstructionValueLists = new(StringComparer.Ordinal)
    {
        ["newArray"] = "elems",
        ["newList"] = "elems",
    };

    // Node kinds whose value operands are named fields rather than `recv` + `args`. Keep the physical evaluation
    // order explicit: a protected region in any slot after the first begins while its predecessors are live on the
    // CLR stack. `objMethod` is here even though it has a receiver because Equals spells its sole argument as `arg`,
    // not as an `args` array; treating every receiver-bearing node as an ordinary call silently skipped that slot.
    static readonly Dictionary<string, string[]> OrderedNamedValueSlots = new(StringComparer.Ordinal)
    {
        ["arrayGet"] = new[] { "array", "index" },
        ["arraySet"] = new[] { "array", "index", "value" },
        ["objEq"] = new[] { "lhs", "rhs" },
        ["objMethod"] = new[] { "recv", "arg" },
        ["setFieldExpr"] = new[] { "recv", "value" },
        ["clrPropSet"] = new[] { "recv", "value" },
        ["clrEventAdd"] = new[] { "recv", "handler" },
        ["clrEventRemove"] = new[] { "recv", "handler" },
        ["setField"] = new[] { "recv", "value" },
        ["stackGet"] = new[] { "index", "len", "ptr" },
        ["stackSet"] = new[] { "index", "len", "ptr", "value" },
        ["stackAsSpan"] = new[] { "ptr", "len" },
        ["byrefStore"] = new[] { "ptr", "value" },
        ["newArrayInit"] = new[] { "size", "init" },
        ["enumParse"] = new[] { "arg" },
    };

    static string K(JsonNode n) => (n as JsonObject)?["k"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Post-order whole-tree walk: normalize INNER statement lists first (so a materialized valueBlock's
    // stmts are already internally normalized), then this node's own statement lists. A declaration node
    // extends the scope with its params, and a statement sequence records each `var` for its SUBSEQUENT
    // siblings only (lexical block scoping) — the physical result oracle reads this declared environment.
    static void Walk(JsonNode node, BirScope scope)
    {
        if (node is JsonObject obj)
        {
            // A TYPE node (`{t:…}`, docs/bir-cir-spec.md §1) declares nothing and carries no statement, so the walk
            // STOPS at it. That is not tidiness: a function type spells its parameter TYPES as `params`, the same
            // key a declaration spells its parameters with, so descending asked `Extend` for a scope at every
            // `(A) -> B` in the file — 2909 of them in `_Arrays.bir` alone, each copying the whole environment to
            // harvest nothing (a type node's `params` entries are types, which carry no `name` + `type` pair, so
            // none of them could ever enter a scope). Types and value nodes are disjoint by the schema — a `{t:…}`
            // node never carries `k` — so this cannot skip a node the hoist owns.
            if (TypeJson.IsType(obj)) return;
            // `Extend` returns `this` unless the node really has parameters, so after the skip above the only nodes
            // that pay for a scope copy are the ones that declare some (a method/accessor/constructor, a lambda).
            var child = scope.Extend(obj);
            foreach (var kv in obj)
                if (kv.Value != null)
                    Walk(kv.Value, kv.Key == "body" ? BodyScope(obj, child)
                        : K(obj) == "valueBlock" && kv.Key == "result" ? BlockScope(obj, child) : child);
            NormalizeLists(obj, child);
            NormalizeDelegation(obj, "baseArgs", child);
            NormalizeDelegation(obj, "thisArgs", child);
            // Conditions remain at their evaluation site (including each loop iteration and guarded when arm).
            // Only their eager operands move, inside a valueBlock at that site, never into an enclosing statement.
            if (K(obj) is "if" or "while" or "dowhile" or "brIf" or "cond")
            {
                NormalizeAtSite(obj, "cond", child);
                if (K(obj) == "cond")
                {
                    NormalizeAtSite(obj, "then", child);
                    NormalizeAtSite(obj, "else", child);
                }
            }
            if (obj["branches"] is JsonArray branches)
                foreach (var b in branches.OfType<JsonObject>()) NormalizeAtSite(b, "cond", child);
        }
        else if (node is JsonArray arr)
        {
            var cur = scope;
            foreach (var it in arr)
            {
                if (it != null) Walk(it, cur);
                if (it is JsonObject vo && K(vo) == "var")
                {
                    if (ReferenceEquals(cur, scope)) cur = scope.Child();
                    cur.Declare(vo);
                }
            }
        }
    }

    static void NormalizeLists(JsonObject obj, BirScope scope)
    {
        foreach (var key in StmtListKeys)
            if (obj[key] is JsonArray a) obj[key] = HoistList(a, key == "body" ? BodyScope(obj, scope) : scope);
    }

    // Catch and structured-loop locals are declarations too, scoped to their bodies rather than the input
    // expression. Their exact CLR type is authored by the node; range/repeat counters have the CIR Int32 contract.
    static BirScope BodyScope(JsonObject obj, BirScope outer)
    {
        var scope = outer;
        if (obj["preStmts"] is JsonArray prefix)
        {
            scope = outer.Child();
            foreach (var statement in prefix.OfType<JsonObject>()) scope.Declare(statement);
        }
        var type = TypeJson.Read(obj["excType"]) ?? (K(obj) switch
        {
            "forArray" or "forEachInline" => TypeJson.Read(obj["elem"]),
            "forRange" or "repeatInline" => new TypeNode.Fqn("System.Int32"),
            _ => null,
        });
        if (type != null && obj["var"] is JsonValue variable && variable.TryGetValue<string>(out var name))
        {
            if (ReferenceEquals(scope, outer)) scope = outer.Child();
            scope.VarTypes[name] = type;
        }
        return scope;
    }

    // Delegation operands follow an implicit uninitialized this on the emitter's stack. Evaluate hazardous
    // arguments in the existing preStmts lane BEFORE that prefix; never materialize uninitialized this itself.
    static void NormalizeDelegation(JsonObject obj, string key, BirScope outer)
    {
        if (obj[key] is not JsonArray args) return;
        var pre = new List<JsonNode>();
        var signature = (obj["baseCtorRef"] as JsonObject)?["parameterTypes"] as JsonArray
            ?? obj["delegationSig"] as JsonArray;
        HoistOrdered(args.Count, i => args[i], (i, value) => args[i] = value, atEmpty: false, pre,
            BodyScope(obj, outer),
            isLocation: i => signature != null && i < signature.Count && TypeJson.Read(signature[i]) is TypeNode.ByRef,
            locationType: i => signature != null && i < signature.Count && TypeJson.Read(signature[i]) is TypeNode.ByRef byref
                ? byref.Of : null);
        if (pre.Count == 0) return;
        var prefix = obj["preStmts"] as JsonArray ?? new JsonArray();
        foreach (var statement in pre) prefix.Add(statement);
        obj["preStmts"] = prefix;
    }

    // Rebuild a statement list: for each statement, materialize hazardous ordered operands from its
    // single-eval expression child(ren) into preceding statements, then emit `pre + stmt`. A `var` enters
    // scope for the statements AFTER it (its own initializer is evaluated before it exists).
    static JsonArray HoistList(JsonArray list, BirScope scope)
    {
        var outList = new JsonArray();
        var cur = scope;
        foreach (var stmt in list)
        {
            var clone = stmt?.DeepClone();
            if (clone is JsonObject so)
            {
                var pre = new List<JsonNode>();
                HoistStmtExprs(so, pre, cur);
                foreach (var p in pre) outList.Add(p);
            }
            outList.Add(clone);
            if (clone is JsonObject vo && K(vo) == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
        return outList;
    }

    // Only single-evaluation statement operands move into this prefix. Guarded/repeated conditions are normalized
    // separately by NormalizeAtSite so their prefixes remain inside each guard/iteration.
    static void HoistStmtExprs(JsonObject stmt, List<JsonNode> pre, BirScope scope)
    {
        switch (K(stmt))
        {
            case "var": HoistChild(stmt, "init", pre, scope); break;
            case "setLocal": HoistChild(stmt, "value", pre, scope); break;
            case "return": HoistChild(stmt, "value", pre, scope); break;
            case "throw": HoistChild(stmt, "value", pre, scope); break;
            case "exprStmt": HoistChild(stmt, "expr", pre, scope); break;
            case "setField": HoistNamedSlots(stmt, new[] { "recv", "value" }, atEmpty: true, pre, scope); break;
            case "forArray": HoistChild(stmt, "array", pre, scope); break;
            case "forRange": HoistChild(stmt, "range", pre, scope); break;
            case "forEachInline": HoistChild(stmt, "src", pre, scope); break;
            case "repeatInline": HoistChild(stmt, "count", pre, scope); break;
        }
    }

    static void HoistChild(JsonObject o, string key, List<JsonNode> pre, BirScope scope)
    {
        if (o[key] is JsonNode c) { o[key] = null; o[key] = HoistExpr(c, pre, atEmpty: true, scope); }
    }

    static void NormalizeAtSite(JsonObject o, string key, BirScope scope)
    {
        if (o[key] is not JsonNode value) return;
        if (K(o) == "valueBlock" && key == "result") scope = BlockScope(o, scope);
        o[key] = null;
        var pre = new List<JsonNode>();
        var result = HoistExpr(value, pre, atEmpty: true, scope);
        o[key] = pre.Count == 0 ? result : new JsonObject
        {
            ["k"] = "valueBlock", ["stmts"] = new JsonArray(pre.ToArray()), ["result"] = result,
        };
    }

    static BirScope BlockScope(JsonObject block, BirScope outer)
    {
        var scope = outer.Child();
        foreach (var key in new[] { "stmts", "body" })
            if (block[key] is JsonArray statements)
                foreach (var statement in statements.OfType<JsonObject>()) scope.Declare(statement);
        return scope;
    }

    // Walk an operand expression, materializing hazardous ordered operands into `pre`. `atEmpty`
    // is true iff evaluating this node begins with an empty CLR eval stack.
    static JsonNode HoistExpr(JsonNode node, List<JsonNode> pre, bool atEmpty, BirScope scope)
    {
        if (node is not JsonObject o) return node;
        var k = K(o);

        // Every hazardous eager subtree is normalized at an empty stack before its result is stored.
        if (!atEmpty && RunsStackBoundary(o))
        {
            // Move the WHOLE operand, not the statements in one conditional arm. This keeps guarded work guarded
            // and gives every return/loop transfer/protected-region entry an empty stack on its taken path.
            var normalized = HoistExpr(o, pre, atEmpty: true, scope);
            return SpillIfNeeded(normalized, pre, scope);
        }
        if (k == "valueBlock")
        {
            // Its result runs AFTER its statements. Keep normalization of that result at the result site.
            NormalizeAtSite(o, "result", scope);
            return o;
        }
        if (k == "cond")
        {
            NormalizeAtSite(o, "cond", scope);
            NormalizeAtSite(o, "then", scope);
            NormalizeAtSite(o, "else", scope);
            return o;
        }

        if (k == "binOp" && o["lhs"] != null && o["rhs"] != null)
        {
            HoistOrdered(2, i => i == 0 ? o["lhs"] : o["rhs"], (i, v) => { if (i == 0) o["lhs"] = v; else o["rhs"] = v; }, atEmpty, pre, scope);
            return o;
        }
        if (k != null && OrderedNamedValueSlots.TryGetValue(k, out var namedValueSlots))
        {
            HoistNamedSlots(o, namedValueSlots, atEmpty, pre, scope);
            return o;
        }
        if (k == "concat" && o["parts"] is JsonArray parts)
        {
            // ilemit has already pushed the object[] accumulator and an element index before every part, including
            // part 0. A protected region in any part therefore starts with a non-empty evaluation stack.
            HoistOrdered(parts.Count, i => parts[i], (i, v) => parts[i] = v, atEmpty: false, pre, scope);
            return o;
        }
        if (k is "newClosure" or "newSam" && o["captures"] is JsonArray captures)
        {
            HoistOrdered(captures.Count, i => captures[i], (i, v) => captures[i] = v, atEmpty, pre, scope);
            return o;
        }
        if (k != null && ConstructionValueLists.TryGetValue(k, out var valueListKey)
            && o[valueListKey] is JsonArray values)
        {
            // ilemit has already pushed the new array/list (and an array index) before it evaluates an element.
            // Consequently no element inherits the enclosing expression's empty-stack position, including element 0.
            HoistOrdered(values.Count, i => values[i], (i, v) => values[i] = v,
                atEmpty: false, pre, scope, spillAllAfterHoist: true);
            return o;
        }
        if (k == "spreadConcat" && o["parts"] is JsonArray spreadParts)
        {
            HoistSpreadParts(spreadParts, pre, scope);
            return o;
        }
        if (o["args"] is JsonArray || o["recv"] != null)
        {
            HoistCallSlots(o, atEmpty, pre, scope);
            return o;
        }

        // Generic single-eval wrapper (cast, etc.): descend into object children (never a statement list).
        foreach (var kv in o.ToList())
            if (!SkipGenericKeys.Contains(kv.Key) && kv.Value is JsonObject)
            { o[kv.Key] = null; o[kv.Key] = HoistExpr(kv.Value, pre, atEmpty, scope); }
        return o;
    }

    // recv (if present) then args[..] as an ordered slot list.
    static void HoistCallSlots(JsonObject o, bool atEmpty, List<JsonNode> pre, BirScope scope)
    {
        var hasRecv = o["recv"] != null;
        var args = o["args"] as JsonArray;
        var argc = args?.Count ?? 0;
        var n = (hasRecv ? 1 : 0) + argc;
        JsonNode Get(int i) => (hasRecv && i == 0) ? o["recv"] : args[i - (hasRecv ? 1 : 0)];
        void Set(int i, JsonNode v) { if (hasRecv && i == 0) o["recv"] = v; else args[i - (hasRecv ? 1 : 0)] = v; }
        var signature = o["argTypes"] as JsonArray ?? o["shapeTypes"] as JsonArray ?? o["sig"] as JsonArray
            ?? (o["memberRef"] as JsonObject)?["parameterTypes"] as JsonArray
            ?? (o["invokeRef"] as JsonObject)?["parameterTypes"] as JsonArray;
        bool IsLocation(int i)
        {
            if (hasRecv && i == 0) return CallEvalLowering.ReceiverNeedsAddress(o, _isValue);
            var arg = i - (hasRecv ? 1 : 0);
            return signature != null && arg < signature.Count && TypeJson.Read(signature[arg]) is TypeNode.ByRef;
        }
        TypeNode LocationType(int i)
        {
            if (hasRecv && i == 0) return ReceiverType(o);
            var arg = i - (hasRecv ? 1 : 0);
            return signature != null && arg < signature.Count && TypeJson.Read(signature[arg]) is TypeNode.ByRef byref
                ? byref.Of : null;
        }
        HoistOrdered(n, Get, Set, atEmpty, pre, scope, isLocation: IsLocation, locationType: LocationType,
            isReceiver: i => hasRecv && i == 0);
    }

    static TypeNode ReceiverType(JsonObject o) =>
        TypeJson.Read(o["recvType"]) ?? TypeJson.Read(o["ownerType"]) ?? TypeJson.Read(o["type"]);

    static void HoistNamedSlots(JsonObject o, string[] keys, bool atEmpty, List<JsonNode> pre, BirScope scope)
    {
        var present = keys.Where(kk => o[kk] != null).ToArray();
        var preserveFirstLocation = present.Length > 0 && present[0] == "recv"
            && CallEvalLowering.ReceiverNeedsAddress(o, _isValue);
        HoistOrdered(present.Length, i => o[present[i]], (i, v) => o[present[i]] = v,
            atEmpty: atEmpty && !HasImplicitPrefix(o), pre, scope,
            isLocation: i => preserveFirstLocation && i == 0,
            locationType: i => preserveFirstLocation && i == 0 ? ReceiverType(o) : null,
            isReceiver: i => preserveFirstLocation && i == 0);
    }

    // These value slots follow emitter-authored operands: the enum Type token, or a named managed pointer.
    static bool HasImplicitPrefix(JsonObject o) => K(o) == "enumParse"
        || K(o) == "byrefStore" && o["ptr"] == null;

    // A spread-concatenated vararg is accumulated through `List.Add`/`AddRange`: ilemit loads that accumulator before
    // each part's expression. The value stream is `parts[i].e`; `spread` is only the already-decided Add/AddRange
    // selector and is not an expression. As with the factory constructions above, all source arguments are evaluated
    // before the call body consumes them, so a hoist materializes the whole non-neutral stream first.
    static void HoistSpreadParts(JsonArray parts, List<JsonNode> pre, BirScope scope)
    {
        var slots = parts.OfType<JsonObject>().Where(p => p["e"] != null).ToList();
        HoistOrdered(slots.Count,
            i => slots[i]["e"],
            (i, v) => slots[i]["e"] = v,
            atEmpty: false, pre, scope, spillAllAfterHoist: true);
    }

    // Core ordered-operand normalization. Evaluation is left-to-right: only the FIRST slot inherits the
    // enclosing empty-stack flag; every later slot begins with a non-empty stack. Any operand that
    // precedes a slot which hoists must itself be spilled (if side-effecting) so relative order holds.
    static void HoistOrdered(int n, Func<int, JsonNode> get, Action<int, JsonNode> set, bool atEmpty,
                             List<JsonNode> pre, BirScope scope, bool spillAllAfterHoist = false,
                             Func<int, bool> isLocation = null, Func<int, TypeNode> locationType = null,
                             Func<int, bool> isReceiver = null)
    {
        var lastHoist = -1;
        for (var i = 0; i < n; i++) if (WillHoist(get(i), i == 0 && atEmpty)) lastHoist = i;
        for (var i = 0; i < n; i++)
        {
            var node = get(i);
            var location = isLocation?.Invoke(i) == true;
            var addressedType = location ? locationType?.Invoke(i) : null;
            set(i, null);
            if (location && lastHoist >= i)
                node = MaterializeAddress(node, addressedType, pre, scope, isReceiver?.Invoke(i) == true);
            var resolved = HoistExpr(node, pre, i == 0 && atEmpty, scope);
            // Ordinary operators/calls only need the prefix: evaluation resumes in their slots after the hoisted
            // block. A construction is different — its emitter consumes each value immediately through stelem/Add/
            // set_Item, while the Kotlin factory/vararg call evaluated EVERY source argument before that body began.
            // Once any stack boundary moves, materialize the whole non-neutral stream before construction so a
            // suffix expression cannot slide behind an earlier element's user-observable hash/equality operation.
            if (!location && lastHoist >= 0 && (spillAllAfterHoist || i < lastHoist))
                resolved = SpillIfNeeded(resolved, pre, scope);
            set(i, resolved);
        }
    }

    // Preserve the evaluated LOCATION itself across a later control-transfer operand. Pinning only an array/index
    // expression is not enough when those operands are mutable locals: taking the address later would select a
    // different element. An ordinary rvalue first gets the same value temporary EmitAddr would otherwise make;
    // lvalues and ref-returning expressions go directly into a typed managed-reference local.
    static JsonNode MaterializeAddress(JsonNode location, TypeNode declaredType, List<JsonNode> pre, BirScope scope,
        bool receiver)
    {
        var resolved = HoistExpr(location, pre, atEmpty: true, scope);
        var type = _typeOf(resolved, scope) ?? declaredType
            ?? throw new InvalidOperationException("bir2cir: an ordered address operand has no declared element type");
        if (!CallEvalLowering.IsLvalueFormer(resolved) && type is not TypeNode.ByRef
            || receiver && type is TypeNode.Fqn concrete && !_isValue(concrete))
            resolved = SpillIfNeeded(resolved, pre, scope);
        var byref = type is TypeNode.ByRef ? type : new TypeNode.ByRef(type);
        JsonNode address = new JsonObject { ["k"] = "byrefOf", ["inner"] = resolved.DeepClone() };
        if (receiver && type is TypeNode.Tv)
        {
            // constrained. needs an address for both reference and value instantiations. A native value receiver
            // must keep its original storage, but a reference receiver must snapshot its VALUE before later writes.
            // Type.IsValueType is exact, including Nullable<T>; boxed default null would conflate it with a class.
            // Keep this physical decision in CIR using an authored memberRef and existing managed-reference slots.
            var snapshot = "dotkt$receiver" + System.Threading.Interlocked.Increment(ref _tmp);
            pre.Add(new JsonObject { ["k"] = "var", ["name"] = snapshot, ["type"] = TypeJson.Write(type) });
            address = new JsonObject
            {
                ["k"] = "cond", ["type"] = TypeJson.Write(byref),
                ["cond"] = new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Type"), ["method"] = "get_IsValueType",
                    ["recv"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type) },
                    ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(),
                    ["ret"] = TypeJson.Fqn("System.Boolean"), ["dispatch"] = "callvirt",
                    ["memberRef"] = _isValueTypeRef.DeepClone(),
                },
                ["then"] = address,
                ["else"] = new JsonObject
                {
                    ["k"] = "valueBlock",
                    ["stmts"] = new JsonArray(new JsonObject { ["k"] = "setLocal", ["name"] = snapshot,
                        ["value"] = resolved.DeepClone() }),
                    ["result"] = new JsonObject { ["k"] = "byrefOf", ["inner"] = new JsonObject { ["k"] = "local", ["name"] = snapshot } },
                },
            };
        }
        var name = "dotkt$address" + System.Threading.Interlocked.Increment(ref _tmp);
        pre.Add(new JsonObject
        {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(byref),
            ["init"] = address,
        });
        // The operand remains a LOCATION of the original element type. In a value slot this loads that value;
        // in an address slot ilemit's existing byrefLoad address form reads the saved pointer. Later receiver
        // binding therefore sees the original T rather than treating T& as a new receiver classifier.
        return new JsonObject
        {
            ["k"] = "byrefLoad", ["local"] = name,
            ["elem"] = TypeJson.Write(byref is TypeNode.ByRef reference ? reference.Of : type),
        };
    }

    // Snapshot a value at its physical evaluation position. Only literals and this can be reread later unchanged.
    static JsonNode SpillIfNeeded(JsonNode resolved, List<JsonNode> pre, BirScope scope)
    {
        if (IsStackNeutral(resolved)) return resolved;
        var tmp = "dotkt$hoist" + System.Threading.Interlocked.Increment(ref _tmp);
        var type = SpillType(resolved, scope);
        pre.Add(new JsonObject { ["k"] = "var", ["name"] = tmp, ["type"] = type, ["init"] = resolved.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = tmp, ["sty"] = type.DeepClone() };
    }

    // Would HoistExpr append any preceding statement for this subtree at its current stack position?
    static bool WillHoist(JsonNode node, bool atEmpty)
    {
        if (node is not JsonObject o) return false;
        var k = K(o);
        if (!atEmpty && RunsStackBoundary(o)) return true;
        if (k is "valueBlock" or "cond") return false;
        if (k == "binOp" && o["lhs"] != null && o["rhs"] != null)
            return WillHoist(o["lhs"], atEmpty) || WillHoist(o["rhs"], false);
        if (k != null && OrderedNamedValueSlots.TryGetValue(k, out var namedValueSlots))
        {
            var first = true;
            var beginsEmpty = atEmpty && !HasImplicitPrefix(o);
            foreach (var key in namedValueSlots)
                if (o[key] is JsonNode operand)
                {
                    if (WillHoist(operand, first && beginsEmpty)) return true;
                    first = false;
                }
            return false;
        }
        if (k == "concat" && o["parts"] is JsonArray parts)
        {
            foreach (var part in parts) if (WillHoist(part, atEmpty: false)) return true;
            return false;
        }
        if (k is "newClosure" or "newSam" && o["captures"] is JsonArray captures)
        {
            for (var i = 0; i < captures.Count; i++)
                if (WillHoist(captures[i], i == 0 && atEmpty)) return true;
            return false;
        }
        if (k != null && ConstructionValueLists.TryGetValue(k, out var valueListKey)
            && o[valueListKey] is JsonArray values)
        {
            foreach (var value in values) if (WillHoist(value, atEmpty: false)) return true;
            return false;
        }
        if (k == "spreadConcat" && o["parts"] is JsonArray spreadParts)
        {
            foreach (var part in spreadParts)
                if (part is JsonObject po && WillHoist(po["e"], atEmpty: false)) return true;
            return false;
        }
        if (o["args"] is JsonArray || o["recv"] != null)
        {
            var hasRecv = o["recv"] != null;
            if (hasRecv && WillHoist(o["recv"], atEmpty)) return true;
            if (o["args"] is JsonArray a)
                for (var i = 0; i < a.Count; i++) if (WillHoist(a[i], !hasRecv && i == 0 && atEmpty)) return true;
            return false;
        }
        foreach (var kv in o)
            if (!SkipGenericKeys.Contains(kv.Key) && kv.Value is JsonObject && WillHoist(kv.Value, atEmpty)) return true;
        return false;
    }

    // Does evaluating this subtree cross a stack boundary? The walk stops at
    // a nested DECLARATION (anything carrying `params` — a lambda, a local function): its body runs when it is
    // invoked, on an evaluation stack of its own, so a `try` in there is not this expression's hazard. It also stops
    // at a type node, which carries no code (and spells its parameter TYPES as `params`).
    static bool RunsStackBoundary(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                if (TypeJson.IsType(o)) return false;
                if (K(o) is "try" or "return" or "returnExpr" or "break" or "continue" or "goto") return true;
                if (o["params"] is JsonArray) return false;
                foreach (var kv in o) if (kv.Key != "synthClass" && kv.Value != null && RunsStackBoundary(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && RunsStackBoundary(it)) return true;
                return false;
            default:
                return false;
        }
    }

    static bool IsStackNeutral(JsonNode n) => n is JsonObject o && StackNeutralKinds.Contains(K(o));

    // The shared final-CLR result oracle consumes authored member/declaration contracts and lexical local types.
    // Earlier semantic sty/ret stamps are not a substitute for the physical result. An underivable spill is a
    // dropped contract, never a reason to fabricate an Any/object local or recover an older artifact's shape.
    static JsonNode SpillType(JsonNode n, BirScope scope)
        => _typeOf(n, scope) is DotKt.Bir.TypeNode t
            ? TypeJson.Write(t)
            : throw new System.NotSupportedException(
                $"bir2cir: physical operand normalization: the `{K(n) ?? "?"}` operand moved across a stack boundary "
                + "carries no static type, so the temporary that holds its value across the hoist would be untyped. "
                + "An earlier lowering dropped the operand's type, or its node kind needs an arm in "
                + $"bir-common/NodeType.cs. Operand: {n.ToJsonString()}");
}
