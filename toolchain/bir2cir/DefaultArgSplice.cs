using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CROSS-MODULE DEFAULT-ARGUMENT SPLICE (#134/#146).
//
// A call that OMITS a defaulted argument reaches bir2cir in one of three forms:
// - a POSITIONAL `{"k":"defaultArg"}` placeholder when a later argument keeps the omitted slot observable; or
// - a shorter `args` vector for a purely trailing Kotlin omission; or
// - a shorter owner-qualified CLR-static call whose optional parameter exists only in CLR metadata (Kotlin IR marks
//   the call legal but does not synthesize an IrErrorExpression/defaultArg placeholder).
// The call still carries the frontend-selected declaration's complete `sig`/`shapeTypes` vector. For that exact
// declaration this pass reads the default value from the referenced DLL (`KotlinDefault` carrier first, ECMA-335
// parameter constant otherwise) and materializes a complete physical argument vector for CIR.
//
// PHASE 1 (#146): runs immediately AFTER InlineSplice — BEFORE ObjectSlotRename/ClosureSynthesis/MemberCallSubstitution/
// BirTypeLowering — so the spliced RAW payload re-lowers IN THIS app's context (owner attribution for a payload's own
// `callStatic owner:null`, @ClrIntrinsic binding, generic resolution), exactly like InlineSplice's body splice.
// Reference-KLIB cross-module calls already carry the callee's exact file-facade `ownerType`; use it. Only a truly
// ownerless Kotlin call falls back to `method name | emitted-arity`, where conflicting owners are refused loudly.
//
// CLOSED CARRIER (#146): a NON-CONSTANT default that lifts a helper — a non-capturing lambda `= {}` (the Avalonia
// `configure: Panel.() -> Unit = {}` idiom) whose `newDelegate` points at a library-local generated method — is carried as a
// `{"k":"defaultCarrier","expr":<newDelegate>,"lifted":[<method decls>]}` envelope (kotc BirEmitterDeclarations
// .defaultCarrierBir). At the splice we RE-HOIST each carried method into THIS file's file-class methods under a fresh
// per-splice name and rewrite both `newDelegate.method` and its mandatory `calleeOwner` to this consuming file class —
// no cross-assembly `ldftn`. A constant / simple-call default (`= emptyList()`) carries its BIR verbatim (no envelope).
// Capturing closures, SAMs and suspend lambdas already self-carry their synthesis facts on the construction node.
//
// A receiver read rides a `{k:defaultArgReceiver,kind}` token: `dispatch` -> callInstance.recv, `extension` -> arg[0],
// and an inner constructor's `enclosing` -> its hidden arg[0]. A default reading an EARLIER parameter rides
// `{k:defaultArgParam,idx}` -> the call's arg[idx] (Kotlin defaults reference only earlier params; lower indices fill
// first). Ordinary `{k:this}` inside a carried closure/SAM/suspend-lambda is that synthesized object's OWN receiver and
// is never rewritten. Unconditional across builds (a ref/rt stdlib self-build carries no cross-module omission).
//
// MATERIALISE AND REFERENCE, nothing else (docs/bir-cir-spec.md §2.7). kotc reserves a default-phase BINDING for every
// cross-module omission — the placeholder is that binding's `expr` — and every value of the call is a binding too, so
// each arg slot the carrier reads is already a `bindRef`, a pure READ. This pass therefore only materialises the
// carrier and drops it into the reserved binding: it discovers no prefix, creates no temp, wraps nothing, and clones
// only reads. Which of those bindings needs a local, and in what order, is CallEvalLowering's single decision
// immediately afterwards. The temp machinery this pass used to carry — a read-position scan, an all-or-nothing
// `bindable` prefix and a second storage-legality oracle — is what made a legal fill jump ahead of an unbindable
// supplied value; there is no longer a place for that choice to be made.
//
// CONSTRUCTORS (#235) come through the same machinery with two differences: a `{"k":"new"}` names its callee by TYPE, so
// it is always OWNERFUL (`<type>|.ctor|<declared param count>`, the count read off `argTypes`) and never falls back to the
// name+arity index; and its hidden leading argument is explicitly the `enclosing` receiver of an inner constructor,
// never an inferred dispatch/extension receiver. Two same-arity ctor overloads carrying DIFFERENT defaults make the key
// ambiguous and are refused (ReferenceMetadataIndex.KotlinDefaultsConflicted).
static class DefaultArgSplice
{
    static int _counter;   // global unique id for fresh re-hoisted lifted-method names (per splice instance)

    // `root` is the CIR file object (has `methods` = the file-class methods a carrier's lifted decls re-hoist into).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        var hoist = new JsonArray();
        // Every `defaultArg` kotc emits is the `expr` of a reserved plan binding. One anywhere else means a value of
        // that call has a second reader with no binding to hold it — the exact shape that duplicated an evaluation
        // before the plan existed — so refuse it here rather than fill it into a call nothing bound.
        AssertPlaceholdersPlanned(root, inBindingExpr: false);
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
        // CHOKEPOINT: kotc uses `defaultArg` only to preserve an omitted positional slot. CIR and ilemit require the
        // complete physical argument vector, so a survivor is a bir2cir fill failure.
        AssertNoPlaceholder(root, null);
    }

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, refs, hoist, localOwner);
            // A placeholder only ever lives in a plan binding, so the PLAN is the splice site: it names both the call
            // whose callee identifies the carriers and the bindings the fill is written into and reads from.
            if (Str(obj["k"]) == "callEval" && obj["expr"] is JsonObject call && obj["bindings"] is JsonArray bindings)
                TrySplice(call, bindings, refs, hoist, localOwner, obj["semanticOwner"]);
            // A genuine static class member restored with Kotlin's IS_STATIC_FUNCTION flag has no synthetic companion
            // receiver and can expose an ECMA-335 optional parameter directly. The frontend accepts the omission but
            // its IR stub carries no default expression, so kotc can only emit the shorter argument vector. Resolve the
            // selected owner+declaration signature here and append only metadata-representable constants. Anything
            // needing a receiver/earlier-parameter binding still requires the ordinary planned placeholder path.
            TrySpliceTrailingConstants(obj, refs);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it, refs, hoist, localOwner);
    }

    static void TrySpliceTrailingConstants(JsonObject node, ReferenceMetadataIndex refs)
    {
        var k = Str(node["k"]);
        if (k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args) return;
        var sig = node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray;
        if (sig == null || args.Count >= sig.Count) return;
        var ownerNode = node["ownerType"] ?? node["calleeOwner"] ?? node["owner"];
        var owner = TypeJson.OwnerName(ownerNode);
        var method = Str(node["method"]);
        if (owner == null || method == null) return;
        var defaults = DefaultsForCall(node, ownerNode, owner, method, sig, refs);
        if (defaults == null) return;

        // Parse the complete tail before mutating the call. A non-constant KotlinDefault carrier may read the receiver
        // or an earlier parameter and therefore needs callEval bindings; accept only leaf metadata constants. An enum
        // constant is the same leaf with an explicit declared enum slot plus underlying bits, not an entry-name lookup.
        var fills = new List<JsonNode>();
        for (var pos = args.Count; pos < sig.Count; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;
            JsonNode parsed;
            try { parsed = JsonNode.Parse(bir, documentOptions: BirJson.DocOptions); }
            catch { return; }
            if (parsed is not JsonObject o || !IsMetadataConstant(o)) return;
            fills.Add(parsed);
        }
        foreach (var fill in fills) args.Add(fill);
    }

    static bool IsMetadataConstant(JsonObject value)
    {
        var kind = Str(value["k"]);
        if (kind == "const") return true;
        if (kind == "default") return value["type"] != null;
        if (kind == "enumValue")
            return value["type"] != null && Str(value["underlying"]) != null
                && Str(value["physicalValue"]) != null;

        // CLR decimal and DateTime metadata constants have no literal opcode. ReferenceMetadataIndex materializes
        // their exact public value-type constructors. Admit only those closed, all-constant shapes here so a shorter
        // static call can receive them; an arbitrary KotlinDefault `new` must still use a call-evaluation plan.
        if (kind != "new" || value["argTypes"] is not JsonArray argTypes || value["args"] is not JsonArray args
            || argTypes.Count != args.Count) return false;
        var owner = TypeJson.OwnerName(value["type"]);
        string[] expected = owner switch
        {
            "System.Decimal" => new[]
            {
                "System.Int32", "System.Int32", "System.Int32", "System.Boolean", "System.Byte",
            },
            "System.DateTime" => new[] { "System.Int64" },
            _ => null,
        };
        if (expected == null || expected.Length != args.Count) return false;
        for (var i = 0; i < expected.Length; i++)
            if (TypeJson.OwnerName(argTypes[i]) != expected[i]
                || args[i] is not JsonObject arg || Str(arg["k"]) != "const"
                || TypeJson.OwnerName(arg["type"]) != expected[i]) return false;
        return true;
    }

    // The binding an argument slot READS, or null when the slot is not a plan read.
    static JsonObject BindingOf(JsonNode slot, JsonArray bindings)
    {
        if (slot is not JsonObject o || Str(o["k"]) != "bindRef" || Str(o["id"]) is not string id) return null;
        foreach (var b in bindings) if (b is JsonObject bo && Str(bo["id"]) == id) return bo;
        return null;
    }

    /// Refuse a `defaultArg` that is not the reserved `expr` of a plan binding (§2.7's standing invariant: any
    /// transform that gives a call value a second reader must go through a plan).
    static void AssertPlaceholdersPlanned(JsonNode node, bool inBindingExpr)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "defaultArg" && !inBindingExpr)
                throw new InvalidOperationException(
                    "bir2cir: an omitted cross-module default argument reached the splice outside a call-evaluation " +
                    "plan. Its carrier binds this call's own receiver and arguments, so without bindings to read they " +
                    "would each be evaluated a second time; the call site must emit an evaluation plan.");
            var isBinding = o["id"] != null && o["phase"] != null && o.ContainsKey("expr");
            foreach (var kv in o) if (kv.Value != null) AssertPlaceholdersPlanned(kv.Value, isBinding && kv.Key == "expr");
        }
        else if (node is JsonArray a) foreach (var it in a) if (it != null) AssertPlaceholdersPlanned(it, false);
    }

    static void TrySplice(JsonObject node, JsonArray bindings, ReferenceMetadataIndex refs, JsonArray hoist,
        JsonNode localOwner, JsonNode semanticOwner)
    {
        var k = Str(node["k"]);
        var isNew = k == "new";
        if (!isNew && k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args) return;
        // The reserved binding behind each argument slot, and whether it is still awaiting a cross-module fill. kotc
        // emits a placeholder for EVERY omitted arg of a @KotlinDefault-carrying callee (never a bare trailing DROP),
        // so `args.Count == sig.Count` here. A shorter owner-qualified call may have had CLR optional constants appended
        // by TrySpliceTrailingConstants; arbitrary short vectors remain untouched because an ownerless/name-only match
        // would not identify a safe fill.
        var slotBinding = new JsonObject[args.Count];
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++)
        {
            slotBinding[j] = BindingOf(args[j], bindings);
            if (IsPlaceholder(slotBinding[j]?["expr"])) hasPlaceholder = true;
        }
        if (!hasPlaceholder) return;
        string method, owner;
        ReferenceMetadataIndex.SignatureKey sigKey = null, relaxedSigKey = null;
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
            sigKey = ReferenceMetadataIndex.SignatureKeyOf(ctorParamTypes);
            relaxedSigKey = ReferenceMetadataIndex.SignatureKeyOf(ctorParamTypes, relaxed: true);
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
            method = Str(node["method"]);
            if (method == null) return;
            owner = TypeJson.OwnerName(node["ownerType"] ?? node["calleeOwner"] ?? node["owner"]);
        }
        // The callee as a DIAGNOSTIC names itself: `.ctor` is a key component, not something to show a reader.
        var label = isNew ? owner + " constructor" : method;
        var defaults = isNew
            ? refs.KotlinDefaultsFor(owner, method, sigCount, sigKey, relaxedSigKey)
            : DefaultsForCall(
                node, node["ownerType"] ?? node["calleeOwner"] ?? node["owner"], owner, method,
                node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray, refs);
        if (defaults == null)
        {
            if (refs.KotlinDefaultsAmbiguous(owner, method, sigCount))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{label}' (arity {sigCount}) — that name+arity is " +
                    "carried by several referenced declarations whose defaults disagree; pass the argument explicitly");
            return;
        }
        // The call site's TYPE arguments, for closing the carrier's own frame below: a generic callee's default is
        // carried as the callee wrote it, with its type parameters as positional `tv`s.
        var methodTypeArgs = node["typeArgs"] as JsonArray;
        var ownerTypeArgs = TypeArgsOf(isNew ? node["type"] : node["ownerType"]);
        // Receiver identity comes from the CALL SHAPE, never from an argument-position guess. A member extension has
        // both: dispatch is callInstance.recv, extension is physical arg[0]. A constructor has no dispatch receiver,
        // but an inner constructor's hidden enclosing instance is physical arg[0].
        var dispatchReceiver = k == "callInstance" ? node["recv"] : null;
        var extensionReceiver = args.Count > 0 ? args[0] : null;
        var enclosingReceiver = isNew && args.Count > 0 ? args[0] : null;
        Fill(args, slotBinding, sigCount, defaults, label, refs, hoist, localOwner, semanticOwner,
            methodTypeArgs, ownerTypeArgs, dispatchReceiver, extensionReceiver, enclosingReceiver);
    }

    // The type ARGUMENTS of a `{t:fqn}` type reference, or null.
    static JsonArray TypeArgsOf(JsonNode type) => (type as JsonObject)?["args"] as JsonArray;

    // Resolve defaults from the exact MethodDef the scalar member binder selects. The declaration-identity path is
    // already exact for compiler-produced methods; plain CLR calls use the shared structural resolver. The existing
    // owner/signature indexes remain only for call shapes that resolver cannot yet represent, never after it selected
    // a MethodDef with no default (which would incorrectly fall through to a sibling declaration).
    static Dictionary<int, string> DefaultsForCall(JsonObject node, JsonNode ownerNode, string owner, string method,
        JsonArray signature, ReferenceMetadataIndex refs)
    {
        if (Str(node[DeclarationIdentityBinding.Key]) is string declarationId)
            return refs.KotlinDefaultsForDeclarationIdentity(declarationId);
        if (signature == null) return null;
        var callSignature = signature.Select(TypeJson.Read).ToArray();
        if (TypeJson.Read(ownerNode) is TypeNode.Fqn ownerType
            && callSignature.All(type => type != null)
            && refs.TryKotlinDefaultsForSelectedMethod(
                ownerType, method, (node["typeArgs"] as JsonArray)?.Count ?? 0, Str(node["k"]) == "callStatic",
                callSignature, out var selectedDefaults))
            return selectedDefaults;

        var sigKey = ReferenceMetadataIndex.SignatureKeyOf(signature);
        var relaxedSigKey = ReferenceMetadataIndex.SignatureKeyOf(signature, relaxed: true);
        var defaults = refs.KotlinDefaultsFor(owner, method, signature.Count, sigKey, relaxedSigKey);
        return defaults ?? refs.KotlinDefaultsForImplementedInterface(
            owner, TypeArgsOf(ownerNode)?.Count ?? 0, method, signature.Count, sigKey, relaxedSigKey);
    }

    /// Materialise every omitted default of `args` from `defaults` and write it into the binding the slot READS.
    /// Nothing is hoisted and nothing is wrapped: the values a carrier binds are already bindings, so a
    /// `{defaultArgParam n}` token becomes a clone of that slot's `bindRef` — a duplicated READ, never a duplicated
    /// evaluation. Receiver tokens are supplied separately from the call shape so dispatch/extension/enclosing values
    /// can coexist without sharing a positional meaning.
    static void Fill(JsonArray args, JsonObject[] slotBinding, int sigCount, Dictionary<int, string> defaults,
        string label, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner, JsonNode semanticOwner,
        JsonArray methodTypeArgs, JsonArray ownerTypeArgs, JsonNode dispatchReceiver, JsonNode extensionReceiver,
        JsonNode enclosingReceiver)
    {
        // ASCENDING, mirroring the declaration order Kotlin evaluates defaults in — though the fills no longer depend
        // on it: a later default referencing an earlier slot gets that slot's binding read, whatever has been written
        // into it yet.
        for (var j = 0; j < args.Count; j++)
        {
            var binding = slotBinding[j];
            if (!IsPlaceholder(binding?["expr"]) || !defaults.TryGetValue(j, out var bir)) continue;
            if (SpliceOne(bir, dispatchReceiver, extensionReceiver, enclosingReceiver, args, hoist, refs, label, j,
                    localOwner, semanticOwner, methodTypeArgs, ownerTypeArgs) is not JsonNode fill) continue;
            binding["expr"] = fill;
            // kotc reserved this binding conservatively (it could not know what would fill it). Now that the value is
            // known, answer Q1 (re-readable) for it — a constant default then costs no local at all.
            //
            // The answer is COARSER than the one kotc writes on the bindings it fills itself, and that is deliberate:
            // kotc judges Q1 over Kotlin IR, where `val` is distinguishable from `var`, so it accepts an immutable
            // local read; BIR spells both as `local`, so this refuses the kind outright. A "no" costs one local; a
            // wrong "yes" duplicates an evaluation. See bir-common/ValueStability.cs.
            binding["stable"] = ValueStability.IsReReadable(fill);
            Walk(fill, refs, hoist, localOwner);
        }
        // Any purely-TRAILING omitted arg (the callee carries @KotlinDefault but kotc dropped the tail) is APPENDED as
        // a plain value: it has one reader, its own new slot, and it is evaluated last, which is where Kotlin
        // evaluates it. Unreachable for a `new` (its `args.Count == sigCount` is asserted by the caller). A gap leaves
        // the call PARTIALLY filled — the chokepoint then reports the unfilled placeholder.
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) break;
            if (SpliceOne(bir, dispatchReceiver, extensionReceiver, enclosingReceiver, args, hoist, refs, label, pos,
                    localOwner, semanticOwner, methodTypeArgs, ownerTypeArgs) is JsonNode fill)
            { args.Add(fill); Walk(fill, refs, hoist, localOwner); }
            else break;
        }
    }

    /// A constructor DELEGATION (`: super(…)` / `: this(…)`) is an omitting call site like any other, but its arguments
    /// ride the constructor DECLARATION — there is no call node for [Walk] to see, and the callee is named by the
    /// enclosing type's `base` (or the type itself). Its evaluation plan rides the declaration too, as
    /// `delegationBindings`; the fills go into those bindings exactly as at an expression call site, and
    /// CallEvalLowering turns them into the ctor's `preStmts`. Without this a `class Sub : RefBase(3)` against a
    /// referenced `RefBase(w: Int, h: Int = w * 2)` reaches the chokepoint with an unfilled placeholder.
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
            var bindings = ctor["delegationBindings"] as JsonArray;
            FillDelegation(ctor["baseArgs"] as JsonArray, bindings, baseName);
            FillDelegation(ctor["thisArgs"] as JsonArray, bindings, selfName);
        }

        void FillDelegation(JsonArray args, JsonArray bindings, string owner)
        {
            if (args == null || owner == null || bindings == null) return;
            var slotBinding = new JsonObject[args.Count];
            var has = false;
            for (var j = 0; j < args.Count; j++)
            {
                slotBinding[j] = BindingOf(args[j], bindings);
                if (IsPlaceholder(slotBinding[j]?["expr"])) has = true;
            }
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
            // A delegation names its target by TYPE, so the base's own type arguments are the frame to close against;
            // a constructor declares no type parameters of its own.
            // A constructor delegation has no dispatch/extension receiver. For an inner target its hidden leading
            // argument is the enclosing instance and is already part of this positional vector.
            Fill(args, slotBinding, args.Count, defaults, owner + " constructor", refs, hoist,
                localOwner, JsonValue.Create(selfName), null, TypeArgsOf(type["base"]), null, null,
                args.Count > 0 ? args[0] : null);
        }
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string, unwrap a `defaultCarrier` (re-hoisting its lifted methods app-local), and
    // bind the callee's explicit receiver/parameter tokens to THIS call's own bound values. A deep-fresh subtree per
    // occurrence.
    static JsonNode SpliceOne(string bir, JsonNode dispatchReceiver, JsonNode extensionReceiver,
        JsonNode enclosingReceiver, JsonArray args, JsonArray hoist, ReferenceMetadataIndex refs, string method,
        int slot, JsonNode localOwner, JsonNode semanticOwner,
        JsonArray methodTypeArgs = null, JsonArray ownerTypeArgs = null)
    {
        var parsed = MaterializeDefault(bir, hoist, refs, method, slot, localOwner);
        if (parsed == null) return null;
        if (Str(semanticOwner) is not string owner)
            throw new InvalidOperationException(
                $"default argument splice for {method} has no authored semantic use-site owner");
        RehomeSynthClasses(parsed, owner, Interlocked.Increment(ref _counter));
        // CLOSE THE CARRIER'S OWN TYPE FRAME, before its tokens are bound. The carrier is the default as the CALLEE
        // wrote it, so a generic callee's type parameters ride it as positional `tv`s — `fun <T> f(xs: MutableList<T> =
        // mutableListOf())` carries `mutableListOf<tv{method,0}>()`. Nothing downstream resolves those in the
        // CONSUMER's frame: they erase to `object`, so the consumer built a `List<Object>` for a `MutableList<String>`
        // slot — right values, wrong runtime type, and unverifiable IL. This is the last sibling of the same rule kotc
        // applies to same-module and external defaults (every open type variable closes against the call site); here
        // the substitution is positional because the carrier is JSON, and it runs BEFORE token substitution so the
        // consumer's own `bindRef`s, inserted afterwards, are never re-substituted.
        InlineSplice.SubstTvIn(parsed, methodTypeArgs ?? new JsonArray(), methodTypeArgs?.Count ?? 0, ownerTypeArgs);
        var result = SubstituteTokens(parsed, dispatchReceiver, extensionReceiver, enclosingReceiver, args);
        if (FirstUnboundReceiver(result) is string kind)
            throw new InvalidOperationException(
                $"bir2cir: cannot fill the omitted default argument at slot {slot} of {method}: its default reads the " +
                $"{kind} receiver, but this call shape carries no value for that receiver kind");
        return result;
    }

    internal static void RehomeSynthClasses(JsonNode node, string semanticOwner, int cloneId)
    {
        if (node is JsonObject obj)
        {
            if (obj["synthClass"] is JsonObject synth)
            {
                synth["semanticOwner"] = semanticOwner;
                if (Str(synth["name"]) is string oldName)
                    RenameFqnRefs(obj, oldName, oldName + "$dflt" + cloneId);
            }
            foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                RehomeSynthClasses(child, semanticOwner, cloneId);
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value != null).ToList())
                RehomeSynthClasses(child, semanticOwner, cloneId);
    }

    static void RenameFqnRefs(JsonNode node, string oldName, string newName)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["t"]) == "fqn" && Str(obj["name"]) == oldName) obj["name"] = newName;
            else if (Str(obj["name"]) == oldName && (obj.ContainsKey("fields") || obj.ContainsKey("methods")))
                obj["name"] = newName;
            foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                RenameFqnRefs(child, oldName, newName);
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value != null).ToList())
                RenameFqnRefs(child, oldName, newName);
    }

    // SHARED with InlineSplice (#34 — omitted defaulted param of an inline callee): parse a @KotlinDefault BIR string,
    // unwrap a `defaultCarrier` (re-hoisting its lifted methods into `hoist`),
    // and return the raw default EXPR (still carrying `{defaultArgReceiver kind}` / `{defaultArgParam idx}` tokens — the
    // caller binds them to its own receiver/param frame via SubstituteTokens). Returns null on an unparseable string.
    internal static JsonNode MaterializeDefault(string bir, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot, JsonNode localOwner)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir, documentOptions: BirJson.DocOptions); } catch { return null; }
        var lexicalIdsFreshened = false;
        if (parsed is JsonObject env)
        {
            var envK = Str(env["k"]);
            if (envK == "defaultCarrier")
            {
                parsed = UnwrapCarrier(env, hoist, refs, localOwner);
                lexicalIdsFreshened = true; // UnwrapCarrier freshens the expression and carried declarations jointly.
            }
        }
        // The carrier is the PRODUCER's BIR, so any evaluation plan inside it carries the producer's binding ids. It is
        // about to be substituted with the CONSUMER's own `bindRef`s and dropped into the consumer's frame, possibly
        // once per omitting call site — re-mint the ids so neither copy can collide with the other or with a
        // consumer-side id (§2.7).
        if (parsed != null)
        {
            if (!lexicalIdsFreshened) LexicalDeclarationIds.Freshen(parsed);
            CallEvalLowering.FreshenPlanIds(parsed);
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
        var exprClone = expr.DeepClone();
        RewriteDelegateNames(exprClone, rename, localOwner);
        foreach (var c in clones)
        {
            if (Str(c["name"]) is string on && rename.TryGetValue(on, out var nn)) c["name"] = nn;
            RewriteDelegateNames(c, rename, localOwner);
        }
        // A default expression and its carried methods are one serialized declaration graph. Freshen lexical ids
        // jointly before recursively materializing nested defaults so declaration/use edges survive every clone.
        LexicalDeclarationIds.Freshen(new[] { exprClone }.Concat(clones).ToArray());
        foreach (var c in clones)
        {
            if (TypeJson.OwnerName(localOwner) is not string owner)
                throw new InvalidOperationException("default carrier has no authored consumer file owner");
            // Lifted default helpers move to the consumer file facade. Any implementation classifier carried inside
            // such a helper moves with it; keeping the producer member owner would create a dangling semantic owner in
            // the consumer module. The file owner is explicit at this splice boundary, not recovered from its name.
			c.Remove("semanticOwner");
            RehomeSynthClasses(c, owner, Interlocked.Increment(ref _counter));
            // The carried lifted body may ITSELF contain a `defaultArg` placeholder (a default `= { crossModuleFn() }`
            // whose call omits a non-const default) — fill it before the method is hoisted (the clone is unparented here).
            Walk(c, refs, hoist, localOwner);
            hoist.Add(c);
        }
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

    // Rebuild `node`, replacing each DISCRIMINATED receiver token with the corresponding call value and every
    // `{defaultArgParam,idx:N}` with `args[N]`. Ordinary `{k:this}` is deliberately NOT a token: inside a carried
    // closure/SAM/suspend-lambda it denotes that synthesized object's own receiver. Rebuilds fresh so no node is
    // double-parented. Shared with InlineSplice (#34).
    internal static JsonNode SubstituteTokens(JsonNode node, JsonNode dispatchReceiver, JsonNode extensionReceiver,
        JsonNode enclosingReceiver, JsonArray args)
    {
        switch (node)
        {
            case JsonObject obj when Str(obj["k"]) == "defaultArgReceiver":
            {
                var receiver = Str(obj["kind"]) switch
                {
                    "dispatch" => dispatchReceiver,
                    "extension" => extensionReceiver,
                    "enclosing" => enclosingReceiver,
                    _ => null,
                };
                return receiver == null ? obj.DeepClone() : receiver.DeepClone();
            }
            case JsonObject obj when Str(obj["k"]) == "defaultArgParam":
            {
                var idx = (obj["idx"] as JsonValue)?.GetValue<int>() ?? -1;
                return idx >= 0 && idx < args.Count && args[idx] is JsonNode a ? a.DeepClone() : obj.DeepClone();
            }
            case JsonObject obj:
            {
                var res = new JsonObject();
                foreach (var kv in obj) res[kv.Key] = kv.Value == null ? null
                    : SubstituteTokens(kv.Value, dispatchReceiver, extensionReceiver, enclosingReceiver, args);
                return res;
            }
            case JsonArray arr:
            {
                var res = new JsonArray();
                foreach (var it in arr) res.Add(it == null ? null
                    : SubstituteTokens(it, dispatchReceiver, extensionReceiver, enclosingReceiver, args));
                return res;
            }
            default: return node.DeepClone();
        }
    }

    static string FirstUnboundReceiver(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "defaultArgReceiver") return Str(obj["kind"]) ?? "unknown";
            foreach (var kv in obj)
                if (kv.Value != null && FirstUnboundReceiver(kv.Value) is string found) return found;
        }
        else if (node is JsonArray arr)
            foreach (var it in arr)
                if (it != null && FirstUnboundReceiver(it) is string found) return found;
        return null;
    }

    static void AssertNoPlaceholder(JsonNode node, string context)
    {
        if (node is JsonObject obj)
        {
            var here = DeclarationContext(obj, context);
            // A constructor delegation has no call node: its plan rides the constructor declaration. Its containing
            // type nevertheless supplies the exact base/self target and the source declaration context, while the
            // binding retains the omitted parameter role.
            if (obj["ctors"] is JsonArray ctors && Str(obj["name"]) is string self)
            {
                foreach (var item in ctors)
                {
                    if (item is not JsonObject ctor || ctor["delegationBindings"] is not JsonArray delegationBindings) continue;
                    for (var index = 0; index < delegationBindings.Count; index++)
                    {
                        if (delegationBindings[index] is not JsonObject binding || !IsPlaceholder(binding["expr"])) continue;
                        var role = Str(binding["role"]) ?? $"argument binding {index}";
                        var target = ctor["baseArgs"] is JsonArray
                            ? TypeJson.OwnerName(obj["base"]) ?? "unknown"
                            : self;
                        throw new InvalidOperationException(
                            DiagnosticPrefix(here) +
                            $"cannot fill an omitted default argument for '{target} constructor' ({role}) from the " +
                            "selected referenced declaration: its optional value could not be obtained or represented. " +
                            "The reference may be stale or the value may not be carryable. Pass the argument explicitly.");
                    }
                }
            }
            // A planned omission keeps both sides of the diagnostic together: the binding names the source parameter,
            // while the call names the exact selected declaration. Report it here before descending into the binding's
            // placeholder so an unrepresentable metadata value is not misreported as a missing KotlinDefault carrier.
            if (Str(obj["k"]) == "callEval" && obj["expr"] is JsonObject call
                && obj["bindings"] is JsonArray bindings)
            {
                for (var index = 0; index < bindings.Count; index++)
                {
                    if (bindings[index] is not JsonObject binding || !IsPlaceholder(binding["expr"])) continue;
                    var role = Str(binding["role"]) ?? $"argument binding {index}";
                    var callee = CallLabel(call);
                    throw new InvalidOperationException(
                        DiagnosticPrefix(here) +
                        $"cannot fill an omitted default argument for '{callee}' ({role}) from the selected referenced " +
                        "declaration: its optional value could not be obtained or represented. The reference may be " +
                        "stale or the value may not be carryable. Pass the argument explicitly.");
                }
            }
            if (Str(obj["k"]) == "defaultArg")
                throw new InvalidOperationException(
                    DiagnosticPrefix(here) +
                    "an omitted cross-module default argument was not filled because its optional value could not be " +
                    "obtained or represented from the selected referenced declaration. The reference may be stale or " +
                    "the value may not be carryable. Pass the argument explicitly.");
            foreach (var kv in obj) if (kv.Value != null) AssertNoPlaceholder(kv.Value, here);
        }
        else if (node is JsonArray arr) foreach (var it in arr) if (it != null) AssertNoPlaceholder(it, context);
    }

    static string DeclarationContext(JsonObject node, string fallback)
    {
        if ((node["body"] is not JsonArray && node["ctors"] is not JsonArray)
            || Str(node["name"]) is not string name) return fallback;
        if (node["pos"] is JsonObject pos && Str(pos["f"]) is string source)
        {
            var location = Path.GetFileName(source);
            if ((pos["l"] as JsonValue)?.TryGetValue<int>(out var line) == true)
            {
                location += ":" + line;
                if ((pos["c"] as JsonValue)?.TryGetValue<int>(out var column) == true)
                    location += ":" + column;
            }
            return $"{location}: {name}";
        }
        // Nested synthesized shapes such as a closure class also have name/body but no source position. Keep the
        // enclosing source declaration instead of replacing it with an implementation-only name.
        return fallback ?? name;
    }

    static string CallLabel(JsonObject call)
    {
        var kind = Str(call["k"]);
        if (kind == "new")
            return (TypeJson.OwnerName(call["type"]) ?? "unknown") + " constructor";
        var owner = TypeJson.OwnerName(call["ownerType"] ?? call["calleeOwner"] ?? call["owner"]);
        var method = Str(call["method"]) ?? kind ?? "unknown call";
        return owner == null ? method : owner + "." + method;
    }

    static string DiagnosticPrefix(string context) => context == null ? "bir2cir: " : context + ": bir2cir: ";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
}
