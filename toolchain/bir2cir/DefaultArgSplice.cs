using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CROSS-MODULE DEFAULT-ARGUMENT SPLICE (#134/#146).
//
// A call that OMITS a defaulted argument reaches bir2cir with a POSITIONAL `{"k":"defaultArg"}` placeholder (kotc emits
// one for every omitted default whose VALUE the frontend dropped to an IrErrorExpression — the cross-module case — so a
// later provided arg keeps its slot). For a callee whose defaulted params carry `[kotlin.clr.KotlinDefault(index, bir)]`
// on the referenced .dll, this pass reads the default-expression BIR and SPLICES it into each placeholder / trailing
// omitted slot.
//
// PHASE 1 (#146): runs immediately AFTER InlineSplice — BEFORE ObjectSlotRename/ClosureSynthesis/MemberCallSubstitution/
// BirTypeLowering — so the spliced RAW payload re-lowers IN THIS app's context (owner attribution for a payload's own
// `callStatic owner:null`, @ClrIntrinsic binding, generic resolution), exactly like InlineSplice's body splice.
// facadegen-injected cross-module calls already carry the callee's exact file-facade `ownerType`; use it. Only a truly
// ownerless Kotlin call falls back to `method name | emitted-arity`, where conflicting owners are refused loudly.
//
// CLOSED CARRIER (#146): a NON-CONSTANT default that lifts a helper — a non-capturing lambda `= {}` (the Avalonia
// `configure: Panel.() -> Unit = {}` idiom) whose `newDelegate` points at a library-local `__lambdaN` — is carried as a
// `{"k":"defaultCarrier","expr":<newDelegate>,"lifted":[<method decls>]}` envelope (kotc BirEmitterDeclarations
// .defaultCarrierBir). At the splice we RE-HOIST each carried method into THIS file's file-class methods under a fresh
// per-splice name and rewrite both `newDelegate.method` and its mandatory `calleeOwner` to this consuming file class —
// no cross-assembly `ldftn`. A constant / simple-call default (`= emptyList()`) carries its BIR
// verbatim (no envelope). A `{"k":"defaultUnsupported"}` poison carrier (a capturing/SAM/suspend lambda default kotc
// could not close) is refused loudly here.
//
// A `= this` (an extension receiver) default rides a `{k:this}` token -> the call's arg[0]; a default reading an EARLIER
// param rides `{k:defaultArgParam, idx}` -> the call's arg[idx] (Kotlin defaults reference only earlier params; lower
// indices fill first). Unconditional across builds (a ref/rt stdlib self-build simply carries no cross-module omission).
//
// CONSTRUCTORS (#235) come through the same machinery with two differences: a `{"k":"new"}` names its callee by TYPE, so
// it is always OWNERFUL (`<type>|.ctor|<declared param count>`, the count read off `argTypes`) and never falls back to the
// name+arity index; and it has no receiver, so a carrier that reads one is refused rather than bound to `args[0]` (which
// is a plain constructor argument). Two same-arity ctor overloads carrying DIFFERENT defaults make the key ambiguous and
// are refused (ReferenceMetadataIndex.KotlinDefaultsConflicted).
static class DefaultArgSplice
{
    static int _counter;   // global unique id for fresh re-hoisted lifted-method names (per splice instance)

    // `root` is the CIR file object (has `methods` = the file-class methods a carrier's lifted decls re-hoist into).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        var hoist = new JsonArray();
        var localOwner = root is JsonObject ro && Str(ro["fileClass"]) is string fc ? TypeJson.Fqn(fc) : null;
        Walk(root, refs, hoist, localOwner);
        // A constructor delegation's args are not a call node, so `Walk` never reaches them as one.
        if (root is JsonObject rt) SpliceCtorDelegations(rt["types"], refs, hoist, localOwner);
        if (hoist.Count > 0)
        {
            // A carrier re-hoisted a lifted default lambda but this file has no file-class `methods` array to place it in
            // (kotc ALWAYS emits `methods` on a file root, so this is an internal invariant break, never a silent drop).
            if (root is not JsonObject fo || fo["methods"] is not JsonArray methods)
                throw new InvalidOperationException("bir2cir: DefaultArgSplice re-hoisted a carried default lambda but the file root has no `methods` array");
            foreach (var h in hoist.ToList()) { hoist.Remove(h); methods.Add(h); }
        }
        // CHOKEPOINT: kotc emits a `defaultArg` only for a cross-module omission it expects a @KotlinDefault to fill, and
        // ilemit cannot emit a raw placeholder — a survivor is a fill failure. Fail here with the callee, not opaquely there.
        AssertNoPlaceholder(root);
    }

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, refs, hoist, localOwner);
            TrySplice(obj, refs, hoist, localOwner);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it, refs, hoist, localOwner);
    }

    static void TrySplice(JsonObject node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        var k = Str(node["k"]);
        var isNew = k == "new";
        if (!isNew && k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args) return;
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { hasPlaceholder = true; break; }
        // ONLY a POSITIONAL `defaultArg` placeholder marks a cross-module omission to fill. kotc emits a placeholder for
        // EVERY omitted arg of a @KotlinDefault-carrying callee (never a bare trailing DROP), so `args.Count == sig.Count`
        // here — a call merely SHORT of `sig` (a same-module trailing omit ilemit backfills, or any non-defaulted call
        // carrying `sig`) must NOT be touched: appending a default off an ownerless name match would corrupt it. This is
        // essential now the splice runs at phase 1 on EVERY build (a stdlib self-build call short of `sig` is not an omission).
        if (!hasPlaceholder) return;
        string method, owner, sigKey = null;
        int sigCount;
        if (isNew)
        {
            // A `new` names its callee by TYPE — there is no `method`/`sig` to read. The ctor's splice identity is
            // `<type>|.ctor|<declared parameter count>` (#235), and `argTypes` IS that declared vector (kotc emits the
            // resolved ctor's own parameter types). The args array must line up with it one-for-one: kotc fills every
            // omitted slot with a placeholder, so each stamped @KotlinDefault index indexes that same array. A mismatch
            // means an arg was dropped rather than placeheld — refuse instead of filling the wrong slot.
            owner = TypeJson.OwnerName(node["type"]);
            if (owner == null) return;
            method = ReferenceMetadataIndex.CtorKeyName;
            if (node["argTypes"] is not JsonArray ctorParamTypes) return;
            sigCount = ctorParamTypes.Count;
            // `argTypes` IS the resolved ctor's declared parameter vector, in the same ParamKey space the reference scan
            // keys by — so same-arity ctor overloads resolve to the RIGHT one instead of being refused as ambiguous.
            sigKey = string.Join(",", ctorParamTypes.Select(t => ReferenceMetadataIndex.ParamKey(t)));
            if (args.Count != sigCount)
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{owner}''s constructor — the call emits " +
                    $"{args.Count} argument(s) for {sigCount} parameter(s), so the omitted slot is not identifiable; " +
                    "pass the argument explicitly");
        }
        else
        {
            // A non-generic call carries `sig`; a generic facade call carries the same declared parameter vector as
            // `shapeTypes` so later CLR binding can close the method. Both are structural callee signatures and therefore
            // equally authoritative for the KotlinDefault owner+name+arity lookup.
            var sig = node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray;
            if (sig == null) return;
            sigCount = sig.Count;
            // `sig`/`shapeTypes` IS the callee's declared parameter vector, so it identifies the exact OVERLOAD — two
            // same-arity declarations of one name (an extension `String.tagged(t = this)` beside a
            // `tagged(name, items = emptyList())`) carry different defaults and must not be told apart by arity alone.
            sigKey = string.Join(",", sig.Select(t => ReferenceMetadataIndex.ParamKey(t)));
            method = Str(node["method"]);
            if (method == null) return;
            owner = TypeJson.OwnerName(node["ownerType"] ?? node["calleeOwner"] ?? node["owner"]);
        }
        // The callee as a DIAGNOSTIC names itself: `.ctor` is a key component, not something to show a reader.
        var label = isNew ? owner + " constructor" : method;
        var defaults = refs.KotlinDefaultsFor(owner, method, sigCount, sigKey);
        if (defaults == null)
        {
            if (hasPlaceholder && refs.KotlinDefaultsAmbiguous(owner, method, sigCount))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{label}' (arity {sigCount}) — that name+arity is " +
                    "carried by several referenced declarations whose defaults disagree; pass the argument explicitly");
            return;
        }
        // The declared parameter vector — the primary type for a hoisted temp (see [TempType]).
        var declared = isNew ? node["argTypes"] as JsonArray : (node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray);
        var temps = FillAndBind(args, sigCount, defaults, declared, ctorNoReceiver: isNew,
            recvHost: isNew ? null : node, label, refs, hoist, localOwner);
        // The temps are declared by a `valueBlock` wrapping this very call. Rewritten IN PLACE (the node is parented, and
        // its parent slot is what must now hold the block): the original content becomes the block's `result`.
        if (temps.Count > 0)
        {
            var inner = node.DeepClone();
            node.Clear();
            node["k"] = "valueBlock";
            node["stmts"] = temps;
            node["result"] = inner;
        }
    }

    /// Fill every positional (and trailing) `defaultArg` in `args` from `defaults`, binding each value a fill SPLICES to
    /// a temp so it is evaluated exactly once (#235). Returns the `var` statements the caller must declare — an
    /// expression call site wraps itself in a `valueBlock`; a constructor DELEGATION, whose args ride a declaration,
    /// puts them on its first argument. `recvHost` is the node whose `recv` slot binds with them (null when the call
    /// site has no receiver, i.e. a `new` or a delegation).
    static JsonArray FillAndBind(JsonArray args, int sigCount, Dictionary<int, string> defaults, JsonArray declared,
        bool ctorNoReceiver, JsonObject recvHost, string label,
        ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        // A fill SPLICES this call's receiver / argument into the default expression, while the call keeps using it too —
        // so a non-trivial value would run twice, where Kotlin evaluates a receiver and each argument exactly once. The
        // positions a fill reads come from the carriers themselves, so collect them before filling anything — from the
        // TRAILING carriers too (they bind the same tokens against the same args array).
        var read = new HashSet<int>();
        for (var pos = 0; pos < sigCount; pos++)
            if ((pos >= args.Count || IsPlaceholder(args[pos])) && defaults.TryGetValue(pos, out var peek))
                CollectReadPositions(peek, read);
        var lastRead = read.Count == 0 ? -1 : read.Max();
        // Binding a value moves its evaluation ahead of the call, so EVERY non-stable value up to `lastRead` binds with it
        // or it would slide after the temps — the receiver included, since it is evaluated before any argument. If even
        // one of them cannot be typed, bind NOTHING: a partial hoist reorders the call, which is worse than the double
        // evaluation it would have removed. A PLACEHOLDER is checked against the declared vector, which is what its fill
        // will be typed by.
        var bindable = lastRead >= 0;
        for (var j = 0; j <= lastRead && j < args.Count && bindable; j++)
        {
            if (IsPlaceholder(args[j])) { if (TempType(declared, j, null) == null) bindable = false; }
            else if (args[j] is JsonNode v && !IsStableValue(v) && TempType(declared, j, v) == null) bindable = false;
        }
        if (bindable && recvHost?["recv"] is JsonNode recvCheck && !IsStableValue(recvCheck)
            && TempType(AsVector(recvHost["ownerType"]), 0, recvCheck) == null)
            bindable = false;
        var temps = new JsonArray();
        // 1) Replace POSITIONAL placeholders in place (a later provided arg keeps its slot). Fill by array index = the
        //    @KotlinDefault index (extension receiver counted first, matching kotc's stamp). ASCENDING, so a lower slot a
        //    later default reads is already filled — and already bound, so reading it again costs nothing.
        for (var j = 0; j < args.Count; j++)
        {
            if (IsPlaceholder(args[j]) && defaults.TryGetValue(j, out var bir))
            {
                // An extension receiver rides args[0] (the emitted extension fun's `__self`). A `= this` default binds to
                // it — re-read per fill because a bind may have replaced it with the temp. A `new` / delegation has NO
                // receiver: args[0] is the first ARGUMENT, so a `{k:this}` in such a carrier binds to nothing (kotc
                // refuses to carry one — `defaultReadsDispatch` poisons it — and `SpliceOne` asserts it here).
                var receiver = ctorNoReceiver ? null : (args.Count > 0 ? args[0] : null);
                if (SpliceOne(bir, receiver, args, hoist, refs, label, j, localOwner, ctorNoReceiver) is JsonNode fill) { args[j] = fill; Walk(fill, refs, hoist, localOwner); }
            }
            // Bind every non-stable value up to the last one a fill reads — including the fill just spliced in (a later
            // default may read THIS slot, and a filled default must not be evaluated twice either), and including values
            // no fill reads, whose evaluation would otherwise slide after the temps.
            if (bindable && j <= lastRead && !IsPlaceholder(args[j]) && args[j] is JsonNode value && !IsStableValue(value)
                && TempType(declared, j, value) is JsonNode argType)
                args[j] = Hoist(temps, value, argType);
        }
        // 2) Append any purely-TRAILING omitted args (callee carries @KotlinDefault but kotc dropped the tail). Unreachable
        //    for a `new`: its `args.Count == sigCount` is asserted by the caller. A gap leaves the call PARTIALLY filled —
        //    the chokepoint then reports the unfilled placeholder, and the temps still declare what the fills reference.
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) break;
            var receiver = ctorNoReceiver ? null : (args.Count > 0 ? args[0] : null);
            if (SpliceOne(bir, receiver, args, hoist, refs, label, pos, localOwner, ctorNoReceiver) is JsonNode fill) { args.Add(fill); Walk(fill, refs, hoist, localOwner); } else break;
        }
        // The RECEIVER is evaluated before any argument, so it binds only when an argument did — and then FIRST, ahead of
        // them. (`bindable` above already refused the whole call if it could not be typed.) Its declared type is the
        // member's OWNER: a temp of the owner type is what the call slot expects, and a `byref` owner is refused by
        // [TempType] rather than copied, since for an addressable receiver a temp is a copy and not the address the call
        // would take.
        if (temps.Count > 0 && recvHost?["recv"] is JsonNode recvNode && !IsStableValue(recvNode)
            && TempType(AsVector(recvHost["ownerType"]), 0, recvNode) is JsonNode recvType)
            recvHost["recv"] = HoistFirst(temps, recvNode, recvType);
        return temps;
    }

    // The owner type as a one-element "declared vector", so a receiver goes through [TempType]'s rules unchanged.
    static JsonArray AsVector(JsonNode type) => type == null ? null : new JsonArray(type.DeepClone());

    /// A constructor DELEGATION (`: super(…)` / `: this(…)`) is an omitting call site like any other, but its arguments
    /// ride the constructor DECLARATION — there is no call node for [TrySplice] to see, and the callee is named by the
    /// enclosing type's `base` (or the type itself). Fills them from the target ctor's `@KotlinDefault` carriers, with
    /// the single-evaluation temps declared by the first argument (a `var` declares an ordinary method-body local, and
    /// the first argument is evaluated before every later one — the same placement kotc uses for a same-module
    /// delegation). Without this a `class Sub : RefBase(3)` against a referenced `RefBase(w: Int, h: Int = w * 2)`
    /// reaches the chokepoint with an unfilled placeholder.
    static void SpliceCtorDelegations(JsonNode node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        if (node is JsonArray arr) { foreach (var it in arr) if (it != null) SpliceCtorDelegations(it, refs, hoist, localOwner); return; }
        if (node is not JsonObject type) return;
        foreach (var kv in type) if (kv.Value != null && (kv.Key == "types" || kv.Key == "methods")) SpliceCtorDelegations(kv.Value, refs, hoist, localOwner);
        if (type["ctors"] is not JsonArray ctors) return;
        var selfName = Str(type["name"]);
        var baseName = TypeJson.OwnerName(type["base"]);
        foreach (var c in ctors)
        {
            if (c is not JsonObject ctor) continue;
            Fill(ctor["baseArgs"] as JsonArray, baseName);
            Fill(ctor["thisArgs"] as JsonArray, selfName);
        }

        void Fill(JsonArray args, string owner)
        {
            if (args == null || owner == null) return;
            var has = false;
            for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { has = true; break; }
            if (!has) return;
            // No `argTypes` rides a delegation, so the target ctor is identified by arity alone — which refuses when two
            // same-arity ctors carry disagreeing defaults, rather than serving one of them.
            var defaults = refs.KotlinDefaultsFor(owner, ReferenceMetadataIndex.CtorKeyName, args.Count);
            if (defaults == null)
            {
                if (refs.KotlinDefaultsAmbiguous(owner, ReferenceMetadataIndex.CtorKeyName, args.Count))
                    throw new InvalidOperationException(
                        $"bir2cir: cannot fill an omitted default argument of '{owner}' constructor (arity {args.Count}) — " +
                        "that arity is carried by several constructors whose defaults disagree; pass the argument explicitly");
                return;
            }
            // The target ctor's DECLARED parameter types: a delegation carries no signature vector of its own, and
            // without them a placeholder slot inside the bound range would be untypable and the whole call would decline
            // to bind — silently reverting to per-reader evaluation.
            var declared = refs.KotlinDefaultParamTypesFor(owner, ReferenceMetadataIndex.CtorKeyName, args.Count);
            var temps = FillAndBind(args, args.Count, defaults, declared, ctorNoReceiver: true,
                recvHost: null, owner + " constructor", refs, hoist, localOwner);
            if (temps.Count == 0) return;
            if (args.Count == 0)
                throw new InvalidOperationException("bir2cir: a constructor delegation bound a single-evaluation temp but has no argument to declare it in");
            var first = args[0].DeepClone();
            args[0] = new JsonObject { ["k"] = "valueBlock", ["stmts"] = temps, ["result"] = first };
        }
    }

    // The positions of THIS call's values that a carrier reads: `{k:this}` -> args[0] (an extension receiver), and
    // `{k:defaultArgParam,idx}` -> args[idx]. Parsed WITHOUT materializing (no lifted-method hoisting): this only needs
    // the token positions, and MaterializeDefault runs later for the real fill.
    static void CollectReadPositions(string bir, HashSet<int> into)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir, documentOptions: BirJson.DocOptions); } catch { return; }
        // Only the carrier's EXPRESSION is token-substituted; a `defaultCarrier`'s `lifted` method bodies are re-hoisted
        // verbatim, so a token inside one binds nothing and must not force a hoist.
        if (parsed is JsonObject env && Str(env["k"]) == "defaultCarrier") parsed = env["expr"];
        void Scan(JsonNode n)
        {
            if (n is JsonObject o)
            {
                switch (Str(o["k"]))
                {
                    case "this": into.Add(0); break;
                    case "defaultArgParam" when (o["idx"] as JsonValue)?.GetValue<int>() is int i && i >= 0: into.Add(i); break;
                }
                foreach (var kv in o) if (kv.Value != null) Scan(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Scan(it);
        }
        if (parsed != null) Scan(parsed);
    }

    // A value that is free to RE-READ: a literal or `this` (immutable, no side effect). Everything else — including a
    // plain local read, which another argument's evaluation could write between the two reads — is bound to a temp.
    static bool IsStableValue(JsonNode n) =>
        n is not JsonObject o || Str(o["k"]) is "const" or "this";

    // The type slot for a hoisted temp: the callee's DECLARED type for that position — what the call slot and the
    // carrier's parameter read both expect, so a widening conversion still happens at the `var` store rather than at
    // every use. It is unusable when OPEN: `sig`/`shapeTypes`/`argTypes` render a generic callee's parameter as the
    // CALLEE's positional type variable, which would resolve in the CALLER's frame (to the wrong parameter, or to
    // `object`) — then the VALUE's own static type, concrete at this call site, is used instead. `value` may be null for
    // a placeholder that is about to be FILLED: its fill takes the declared type, so only that slot is checked.
    // Null -> not bindable: a byref slot is an addressable lvalue rather than a value a temp can hold, and a synthesized
    // operand may carry no type at all.
    static JsonNode TempType(JsonArray declared, int position, JsonNode value)
    {
        var slot = declared != null && declared.Count > position ? declared[position] : null;
        if (slot != null && TypeJson.Read(slot) is TypeNode d)
        {
            if (d is TypeNode.ByRef) return null;
            if (!IsOpen(d)) return slot.DeepClone();
        }
        var own = (value as JsonObject)?["sty"] ?? (value as JsonObject)?["type"];
        if (own != null && TypeJson.Read(own) is TypeNode o && o is not TypeNode.ByRef && !IsOpen(o)) return own.DeepClone();
        return null;
    }

    // A type mentioning a positional type VARIABLE: it names a slot in the declaring generic's frame, so it cannot be
    // written into a local of THIS body without re-resolving it there.
    static bool IsOpen(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.ByRef b => IsOpen(b.Of),
        TypeNode.Array a => IsOpen(a.Elem),
        TypeNode.Nullable n => IsOpen(n.Of),
        TypeNode.Oblivious ob => IsOpen(ob.Of),
        TypeNode.Fqn f => f.Args != null && f.Args.Any(IsOpen),
        TypeNode.Fn fn => fn.Params.Any(IsOpen) || IsOpen(fn.Ret),
        _ => false,
    };

    // Move `value` into a fresh `var` on `temps` and return the local read that replaces it.
    static JsonNode Hoist(JsonArray temps, JsonNode value, JsonNode type) => HoistAt(temps, temps.Count, value, type);

    // The RECEIVER binds ahead of the arguments already bound (Kotlin evaluates it first).
    static JsonNode HoistFirst(JsonArray temps, JsonNode value, JsonNode type) => HoistAt(temps, 0, value, type);

    static JsonNode HoistAt(JsonArray temps, int at, JsonNode value, JsonNode type)
    {
        var name = "__dflt$tmp$" + Interlocked.Increment(ref _counter);
        temps.Insert(at, new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = type, ["init"] = value.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = name, ["sty"] = type.DeepClone() };
    }

    // A `{"k":"this"}` in the CARRIER — the callee's own default expression, before this call's args are substituted in.
    // (The substituted result may legitimately contain `this`: an argument the CONSUMER wrote reading its own instance.)
    static bool CarrierReadsReceiver(JsonNode node) => node switch
    {
        JsonObject obj => Str(obj["k"]) == "this" || obj.Any(kv => kv.Value != null && CarrierReadsReceiver(kv.Value)),
        JsonArray arr => arr.Any(it => it != null && CarrierReadsReceiver(it)),
        _ => false,
    };

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string, unwrap a `defaultCarrier` (re-hoisting its lifted methods app-local), and
    // bind the callee's default-expression tokens (`{this}` / `{defaultArgParam idx}`) to THIS call's args. A deep-fresh
    // subtree per occurrence.
    static JsonNode SpliceOne(string bir, JsonNode receiver, JsonArray args, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot, JsonNode localOwner, bool ctorNoReceiver = false)
    {
        var parsed = MaterializeDefault(bir, hoist, refs, method, slot, localOwner);
        if (parsed == null) return null;
        // A ctor call site has no receiver to bind a `{k:this}` to. Checked on the CARRIER, never on the substituted
        // result — that legitimately carries `this` whenever the CONSUMER passed an argument reading its own instance
        // (`Rect(this.w)`), which is the shape the same-module path already binds by symbol rather than by token.
        if (ctorNoReceiver && CarrierReadsReceiver(parsed))
            throw new InvalidOperationException(
                $"bir2cir: cannot fill the omitted default argument at slot {slot} of {method}: its default reads an " +
                "enclosing instance, which a constructor call site cannot bind; pass the argument explicitly");
        return SubstituteTokens(parsed, receiver, args);
    }

    // SHARED with InlineSplice (#34 — omitted defaulted param of an inline callee): parse a @KotlinDefault BIR string,
    // refuse a `defaultUnsupported` poison loudly, unwrap a `defaultCarrier` (re-hoisting its lifted methods into `hoist`),
    // and return the raw default EXPR (still carrying `{this}` / `{defaultArgParam idx}` tokens — the caller binds them to
    // its own arg/param frame via SubstituteTokens). Returns null on an unparseable string.
    internal static JsonNode MaterializeDefault(string bir, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot, JsonNode localOwner)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir, documentOptions: BirJson.DocOptions); } catch { return null; }
        if (parsed is JsonObject env)
        {
            var envK = Str(env["k"]);
            if (envK == "defaultUnsupported")
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill the omitted default argument at slot {slot} of '{method}': " +
                    (Str(env["reason"]) ?? "the default is not representable at a cross-module call site"));
            if (envK == "defaultCarrier")
                parsed = UnwrapCarrier(env, hoist, refs, localOwner);
        }
        return parsed;
    }

    // A `defaultCarrier` envelope: RE-HOIST each carried lifted method into this file (fresh per-splice name), rewrite the
    // `newDelegate.method` references (in the expr AND across the lifted bodies) to the fresh names, and return the expr.
    static JsonNode UnwrapCarrier(JsonObject env, JsonArray hoist, ReferenceMetadataIndex refs, JsonNode localOwner)
    {
        var expr = env["expr"];
        var lifted = env["lifted"] as JsonArray;
        if (expr == null || lifted == null) return expr;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        var clones = new List<JsonObject>();
        foreach (var m in lifted)
        {
            if (m is not JsonObject mo) continue;
            var old = Str(mo["name"]); if (old == null) continue;
            rename[old] = "__dflt$lambda$" + Interlocked.Increment(ref _counter);
            clones.Add((JsonObject)mo.DeepClone());
        }
        foreach (var c in clones)
        {
            if (Str(c["name"]) is string on && rename.TryGetValue(on, out var nn)) c["name"] = nn;
            RewriteDelegateNames(c, rename, localOwner);
            // The carried lifted body may ITSELF contain a `defaultArg` placeholder (a default `= { crossModuleFn() }`
            // whose call omits a non-const default) — fill it before the method is hoisted (the clone is unparented here).
            Walk(c, refs, hoist, localOwner);
            hoist.Add(c);
        }
        var exprClone = expr.DeepClone();
        RewriteDelegateNames(exprClone, rename, localOwner);
        return exprClone;
    }

    // Rewrite every `{"k":"newDelegate","method":<old>}` whose method is in `map` to the renamed method.
    static void RewriteDelegateNames(JsonNode node, Dictionary<string, string> map, JsonNode localOwner)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "newDelegate" && Str(obj["method"]) is string m && map.TryGetValue(m, out var nn))
            {
                obj["method"] = nn;
                // The target method was moved out of the referenced carrier into THIS consuming file class.
                // Retarget #204's mandatory dispatch identity together with the fresh method name.
                obj["calleeOwner"] = localOwner?.DeepClone();
            }
            foreach (var kv in obj.ToList()) if (kv.Value != null) RewriteDelegateNames(kv.Value, map, localOwner);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) RewriteDelegateNames(it, map, localOwner);
    }

    // Rebuild `node`, replacing every `{"k":"this"}` with a deep clone of `receiver` and every
    // `{"k":"defaultArgParam","idx":N}` with a deep clone of `args[N]`. Rebuilds fresh so no node is double-parented.
    // Shared with InlineSplice (#34): there `args[N]` = the bound temp for the emitted-position-N param, `receiver` = the
    // bound extension/dispatch temp.
    internal static JsonNode SubstituteTokens(JsonNode node, JsonNode receiver, JsonArray args)
    {
        switch (node)
        {
            case JsonObject obj when Str(obj["k"]) == "this":
                return receiver == null ? obj.DeepClone() : receiver.DeepClone();
            case JsonObject obj when Str(obj["k"]) == "defaultArgParam":
            {
                var idx = (obj["idx"] as JsonValue)?.GetValue<int>() ?? -1;
                return idx >= 0 && idx < args.Count && args[idx] is JsonNode a ? a.DeepClone() : obj.DeepClone();
            }
            case JsonObject obj:
            {
                var res = new JsonObject();
                foreach (var kv in obj) res[kv.Key] = kv.Value == null ? null : SubstituteTokens(kv.Value, receiver, args);
                return res;
            }
            case JsonArray arr:
            {
                var res = new JsonArray();
                foreach (var it in arr) res.Add(it == null ? null : SubstituteTokens(it, receiver, args));
                return res;
            }
            default: return node.DeepClone();
        }
    }

    static void AssertNoPlaceholder(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "defaultArg")
                throw new InvalidOperationException(
                    "bir2cir: an omitted cross-module default argument was not filled — no [kotlin.clr.KotlinDefault] " +
                    "carrier was found for its callee on the referenced assembly; the reference may be stale or the " +
                    "default not carryable. Pass the argument explicitly.");
            foreach (var kv in obj) if (kv.Value != null) AssertNoPlaceholder(kv.Value);
        }
        else if (node is JsonArray arr) foreach (var it in arr) if (it != null) AssertNoPlaceholder(it);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
}
