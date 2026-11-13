using System;
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
// PHASE 1: typed source preparation runs before join/closure/member/type lowering.
// A default is materialized before visiting its children; introduced defaults and carried helper bodies share
// the same source expansion path and hoist drain. The DOM capabilities below remain for the later inline owner.
// The shared materialization chokepoint first applies representation-entry normalization, then the payload re-lowers
// IN THIS app's context (owner attribution for a payload's own
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
static partial class DefaultArgSplice
{
    static int _counter;   // global unique id for fresh re-hoisted lifted-method names (per splice instance)
    static int FreshDefaultIdentity() => Interlocked.Increment(ref _counter);

    /// Refuse a `defaultArg` that is not the reserved `expr` of a plan binding (§2.7's standing invariant: any
    /// transform that gives a call value a second reader must go through a plan).
    internal static void AssertPlaceholdersPlanned(JsonNode node, bool inBindingExpr)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "defaultArg" && !inBindingExpr)
                throw new InvalidOperationException(UnplannedDefault());
            var isBinding = o["id"] != null && o["phase"] != null && o.ContainsKey("expr");
            foreach (var kv in o) if (kv.Value != null) AssertPlaceholdersPlanned(kv.Value, isBinding && kv.Key == "expr");
        }
        else if (node is JsonArray a) foreach (var it in a) if (it != null) AssertPlaceholdersPlanned(it, false);
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    internal static void RehomeSynthClasses(JsonNode node, string semanticOwner, int cloneId)
    {
        if (node is JsonObject obj)
        {
            // Nested expansion sites can create implementation types later. Transfer their use-site owner
            // together with already present classifiers, before either kind of payload is expanded again.
            if (Str(obj["k"]) is "callInline" or "callEval") obj["semanticOwner"] = semanticOwner;
            if (obj["synthClass"] is JsonObject synth)
            {
                TypeOwnershipLowering.TransferSyntheticOwner(synth, semanticOwner);
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

    internal static void AssertNoPlaceholder(JsonNode node, string context)
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

    internal static string DeclarationContext(JsonObject node, string fallback)
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

    static string UnplannedDefault() =>
        "bir2cir: an omitted cross-module default argument reached the splice outside a call-evaluation " +
        "plan. Its carrier binds this call's own receiver and arguments, so without bindings to read they " +
        "would each be evaluated a second time; the call site must emit an evaluation plan.";

    static string ConstructorDefaultPosition(string owner, int arguments, int parameters) =>
        $"bir2cir: cannot fill an omitted default argument of '{owner}''s constructor — the call emits " +
        $"{arguments} argument(s) for {parameters} parameter(s), so the omitted slot is not identifiable; " +
        "pass the argument explicitly";

    static string AmbiguousDefault(string label, int arity, bool constructorDelegation = false) => constructorDelegation
        ? $"bir2cir: cannot fill an omitted default argument of '{label}' constructor (arity {arity}) — " +
          "that arity is carried by several constructors whose defaults disagree; pass the argument explicitly"
        : $"bir2cir: cannot fill an omitted default argument of '{label}' (arity {arity}) — that name+arity is " +
          "carried by several referenced declarations whose defaults disagree; pass the argument explicitly";

    static string UnrepresentableDefault(string context, string label, int position, string[] names)
    {
        var parameter = names != null && position >= 0 && position < names.Length
            ? $"default of parameter '{names[position]}'" : $"default of parameter at position {position}";
        return DiagnosticPrefix(context) +
            $"cannot fill an omitted default argument for '{label}' ({parameter}) from the selected " +
            "referenced declaration: its optional value could not be obtained or represented. The reference may " +
            "be stale or the value may not be carryable. Pass the argument explicitly.";
    }

    static string MissingDefaultOwner(string method) =>
        $"default argument splice for {method} has no authored semantic use-site owner";

    static string UnavailableDefaultReceiver(string method, int slot, string kind) =>
        $"bir2cir: cannot fill the omitted default argument at slot {slot} of {method}: its default reads the " +
        $"{kind} receiver, but this call shape carries no value for that receiver kind";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
}
