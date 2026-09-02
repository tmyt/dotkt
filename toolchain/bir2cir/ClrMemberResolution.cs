using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// W1-S2 (#46) — RESOLVED-CLR-IR carry for PLAIN calls (clrStatic/clrInstance) + CONSTRUCTORS (newClr). Runs AFTER
// BirTypeLowering.Lower (Program.cs), so every clr*/newClr node's `type` (owner) + `argTypes` are already in the
// CLR-lowered vocabulary. bir2cir HOLDS the winning member (its MetadataLoadContext) but until now serialized only the
// lossy `argTypes` (declared param types WITHOUT member identity), forcing ilemit to re-run overload resolution
// (arity probes, name+arity first-picks, assignability scoring, ctor-by-arg-count, the interface-owner dynamic-dispatch
// downgrade). This pass makes CIR the RESOLVED IR: it structurally matches the callee's DECLARED param types (kotc's
// FIR-resolved `sig`, carried as `argTypes`) against the owner's same-name members in the MLC, requires a UNIQUE winner,
// and stamps the winner's canonical DECLARED params as `resolvedMemberParams` (+ `dispatch` on clrInstance), deleting `argTypes`.
// ilemit then LINKS exactly one handle (0 = hard ABI error / >1 = malformed), never picking.
//
// Mirrors the S1 generic-CALL matcher (Emitter.Resolve.cs ResolveGenericMethod) — its DUAL for the CONSTRUCTION case:
// the owner is resolved on its OPEN definition, and `resolvedMemberParams` keeps a class type-var as a positional `tv(type,i)`.
// So `List<E>` (generic stdlib) / `HashSet<EmittedType>` need NO MakeGenericType over an open/local type-arg; the
// tv-vs-owner-instantiation bridge (the `ownerArgs` TypeNodes) resolves the concrete/open owner args positionally,
// exactly as ilemit's GenericParamMatches `ownerArgs` branch does with reflected Types.
static partial class ClrMemberResolution
{
    const string KotlinSigSnapshotId = "dotktKotlinSigId";
    static readonly Dictionary<int, JsonArray> KotlinSigSnapshots = new();
    static int _nextKotlinSigSnapshotId;
    static ReferenceMetadataIndex _refs;
    static IReadOnlySet<string> _localTypes = new HashSet<string>();
    static IReadOnlySet<string> _externalCanonicalTypes = new HashSet<string>();
    static IReadOnlySet<string> _localDeclarationIds = new HashSet<string>();

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes)
    {
        _refs = refs;
        _localTypes = localTypes ?? new HashSet<string>();
        ResolveExternalClassOverrides(root);
        ResolveExternalBaseMethodImpls(root);
        ResolveBaseConstructors(root);
        Walk(root);
    }

    // Resolve every same-emission-unit constructor call to the declaration's stable index before CIR reaches ilemit.
    // The emitter then performs a direct table lookup; all signature/arity reasoning remains in bir2cir.
    public static void ResolveLocalConstructors(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var defs = rootList.OfType<JsonObject>()
            .SelectMany(file => file["types"] is JsonArray types ? types.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
            .Where(t => (t["name"] as JsonValue)?.TryGetValue<string>(out _) == true)
            // A shared generated declaration may occur in every BIR file that uses it. Every copy has the same generated
            // definition, including constructor list and order, so any one copy defines the assembly-level type index.
            .GroupBy(t => t["name"].GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        void Bind(JsonObject call, TypeNode.Fqn owner, JsonArray args, string signatureName, string context)
        {
            if (!defs.TryGetValue(owner.Name, out var target) || target["ctors"] is not JsonArray ctors) return;
            var sameArity = ctors.Select((n, i) => (ctor: n as JsonObject, index: i))
                .Where(x => x.ctor?["params"] is JsonArray ps && ps.Count == args.Count).ToList();
            var useSiteSig = call[signatureName] as JsonArray;
            if (signatureName == "argTypes")
            {
                if (useSiteSig == null)
                    throw new InvalidDataException(
                        $"bir2cir: malformed current `new` node for {context}: required `argTypes` is absent or is not an array");
                if (useSiteSig.Count != args.Count)
                    throw new InvalidDataException(
                        $"bir2cir: malformed current `new` node for {context}: `argTypes` count {useSiteSig.Count} does not match `args` count {args.Count}");
                for (var i = 0; i < useSiteSig.Count; i++)
                    if (!TypeJson.IsType(useSiteSig[i]))
                        throw new InvalidDataException(
                            $"bir2cir: malformed current `new` node for {context}: `argTypes[{i}]` is not a structured Type node");
            }
            // kotc carries the frontend-selected constructor's OPEN declaration signature independently of the
            // substituted use-site argument vector.  This distinction is load-bearing when physical lowering changes
            // a constructed owner's invariant storage face while the value at the call remains on its read-only head
            // face.  Select from the declaration fact; `argTypes` is rewritten below to the selected physical target.
            var declarationSig = call["memberSignature"] as JsonArray;
            var sig = declarationSig ?? useSiteSig;
            var exact = new List<(JsonObject ctor, int index)>();
            // `new.argTypes` is a use-site vector and therefore closes the constructor owner's type frame.
            // `delegationSig` is authored on the delegating declaration but names the selected target's declaration
            // frame; its `type#0` must remain target `type#0` even when a derived owner reaches that target as
            // `Base<type#1>`.
            var closeOwnerFrame = declarationSig == null && signatureName == "argTypes";
            if (sig != null && sig.Count == args.Count && sig.All(n => n != null))
            {
                var wanted = sig.Select(TypeJson.Read).ToArray();
                foreach (var candidate in sameArity)
                {
                    var ps = (JsonArray)candidate.ctor["params"];
                    var declared = ps.Select(p => p?["type"] is JsonNode pt ? TypeJson.Read(pt) : null).ToArray();
                    if (declared.Any(t => t == null)) continue;
                    var matches = declared.Select((raw, i) =>
                    {
                        var wantedKey = SupertypeGraph.TypeKey(wanted[i]);
                        // A declaration fact normally stays in the target's own open frame.  A lifted/local target
                        // can instead be serialized through the caller's lexical frame; in that case it is exactly
                        // the target declaration closed by the constructed owner.  Accept those two equivalent
                        // spellings, while a plain use-site argTypes lookup remains closed-only.
                        if (declarationSig != null && SupertypeGraph.TypeKey(raw) == wantedKey) return true;
                        if ((closeOwnerFrame || declarationSig != null) && owner.Args is { Length: > 0 })
                            return SupertypeGraph.TypeKey(SupertypeGraph.SubstOwnerTvs(raw, owner.Args)) == wantedKey;
                        return declarationSig == null && SupertypeGraph.TypeKey(raw) == wantedKey;
                    }).All(x => x);
                    if (matches) exact.Add(candidate);
                }
            }
            // A lifted local class can re-home a lexical type parameter into a NEW owner slot.  kotc's open
            // declaration vector still speaks the original lexical frame there, while the constructed-owner plus
            // use-site vector states the same selection in its final physical frame.  If the open comparison names
            // no declaration, normalize through that closed pair.  This remains exact equality and still rejects
            // both zero and multiple matches; it is not assignability or overload scoring.
            if (exact.Count == 0 && declarationSig != null && useSiteSig != null
                && useSiteSig.Count == args.Count && useSiteSig.All(n => n != null))
            {
                var wanted = useSiteSig.Select(TypeJson.Read).ToArray();
                foreach (var candidate in sameArity)
                {
                    var ps = (JsonArray)candidate.ctor["params"];
                    var declared = ps.Select(p => p?["type"] is JsonNode pt ? TypeJson.Read(pt) : null).ToArray();
                    if (declared.Any(t => t == null)) continue;
                    var matches = declared.Select((raw, i) =>
                    {
                        var closed = owner.Args is { Length: > 0 }
                            ? SupertypeGraph.SubstOwnerTvs(raw, owner.Args)
                            : raw;
                        return SupertypeGraph.TypeKey(closed) == SupertypeGraph.TypeKey(wanted[i]);
                    }).All(x => x);
                    if (matches) exact.Add(candidate);
                }
            }
            var winner = exact.Count == 1 ? exact[0]
                : throw new InvalidOperationException($"bir2cir: {context} resolves to {exact.Count} exact local constructors on '{owner.Name}'; wanted={sig?.ToJsonString()}; declarations={string.Join(" | ", sameArity.Select(c => c.ctor["params"]?.ToJsonString()))}; call={call.ToJsonString()}");
            call["localCtorIndex"] = winner.index;
            var declaredParameters = ((JsonArray)winner.ctor["params"]).OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
            if (declaredParameters.Length == args.Count && declaredParameters.All(type => type != null))
            {
                var ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
                var targets = declaredParameters.Select(type => ownerArgs.Length == 0
                    ? type
                    : SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray();
                if (useSiteSig != null && useSiteSig.Count == args.Count)
                    for (var i = 0; i < args.Count; i++)
                    {
                        var flowed = TypeJson.Read(useSiteSig[i]);
                        if (flowed != null && CollectionViewFaces.IsViewSeam(flowed, targets[i])
                            && args[i] is JsonNode argument)
                            args[i] = new JsonObject
                            {
                                ["k"] = "cast",
                                ["type"] = TypeJson.Write(targets[i]),
                                ["e"] = argument.DeepClone(),
                            };
                    }
                call[signatureName] = new JsonArray(targets.Select(TypeJson.Write).ToArray());
                StampDelegateArgumentTargets(call, declaredParameters,
                    ownerArgs, Array.Empty<TypeNode>());
            }
            call.Remove("memberSignature");
        }

        void WalkLocalNews(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var value in obj.Select(kv => kv.Value).ToList()) if (value != null) WalkLocalNews(value);
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "new"
                    && ReadOwnerNode(obj["type"]) is TypeNode.Fqn owner && defs.ContainsKey(owner.Name)
                    && obj["args"] is JsonArray args)
                    Bind(obj, owner, args, "argTypes", $"new {owner.Name}");
            }
            else if (node is JsonArray array)
                foreach (var item in array.ToList()) if (item != null) WalkLocalNews(item);
        }

        foreach (var type in defs.Values)
        {
            var own = new TypeNode.Fqn(type["name"].GetValue<string>());
            var baseType = ReadOwnerNode(type["base"]) as TypeNode.Fqn;
            if (type["ctors"] is not JsonArray ctors) continue;
            foreach (var ctor in ctors.OfType<JsonObject>())
            {
                if (ctor["thisArgs"] is JsonArray thisArgs)
                    Bind(ctor, own, thisArgs, "delegationSig", $"this-delegation in {own.Name}");
                else if (baseType != null && defs.ContainsKey(baseType.Name) && ctor["baseArgs"] is JsonArray baseArgs)
                    Bind(ctor, baseType, baseArgs, "delegationSig", $"base-delegation in {own.Name}");
            }
        }
        foreach (var root in rootList) WalkLocalNews(root);
    }

    // Resolve same-emission-unit delegate targets against the module-wide declaration table. A generic lifted
    // lambda is referenced through its closed delegate shape (`String -> String`) while the actual method declaration
    // is open (`!!0 -> !!0`). Confirm that closing the declaration with the carried typeArgs produces the call-site
    // descriptor, then serialize the declaration's OPEN parameter vector. ilemit subsequently performs only an exact
    // table lookup; it does not reconstruct a generic method signature from the delegate type.
    public static void ResolveLocalDelegateTargets(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var rootList = roots.ToList();
        var owners = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void AddDeclarations(string owner, JsonArray methods)
        {
            if (!owners.TryGetValue(owner, out var declarations))
                owners[owner] = declarations = new List<JsonObject>();
            declarations.AddRange(methods.OfType<JsonObject>());
        }
        foreach (var root in rootList.OfType<JsonObject>())
        {
            if ((root["fileClass"] as JsonValue)?.TryGetValue<string>(out var fileClass) == true
                && root["methods"] is JsonArray topMethods)
                AddDeclarations(fileClass, topMethods);
            if (root["types"] is not JsonArray types) continue;
            foreach (var type in types.OfType<JsonObject>())
                if ((type["name"] as JsonValue)?.TryGetValue<string>(out var name) == true
                    && type["methods"] is JsonArray methods)
                    AddDeclarations(name, methods);
        }

        static int GenericArity(JsonObject method) => method["typeParams"] is JsonArray tps ? tps.Count : 0;
        static TypeNode SubstMethodTvs(TypeNode type, TypeNode[] args) => type switch
        {
            TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
            TypeNode.Nullable n => new TypeNode.Nullable(SubstMethodTvs(n.Of, args)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(SubstMethodTvs(o.Of, args)),
            TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
                nested.Select(a => SubstMethodTvs(a, args)).ToArray()),
            TypeNode.Array a => new TypeNode.Array(SubstMethodTvs(a.Elem, args)),
            TypeNode.ByRef b => new TypeNode.ByRef(SubstMethodTvs(b.Of, args)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
                SubstMethodTvs(fn.Ret, args),
                fn.Params.Select(p => SubstMethodTvs(p, args)).ToArray(),
                fn.Recv == null ? null : SubstMethodTvs(fn.Recv, args), fn.Clr,
                fn.Ctx?.Select(c => SubstMethodTvs(c, args)).ToArray()),
            _ => type,
        };
        static string[] Keys(IEnumerable<TypeNode> types) => types.Select(SupertypeGraph.TypeKey).ToArray();

        void Bind(JsonObject call)
        {
            var kind = (call["k"] as JsonValue)?.GetValue<string>();
            if (kind is not ("newDelegate" or "newBoundDelegate" or "callStatic" or "callInstance" or "constrainedCall")) return;
            // A constrained call names its declaration owner in `iface`; its `recvType` is the type parameter whose
            // dispatch mechanics the emitter consumes.  Treating only ordinary owner slots as local left this one
            // call shape outside delegate-target stamping whenever the interface is emitted in this module.
            var ownerNode = kind == "constrainedCall"
                ? call["iface"]
                : call["calleeOwner"] ?? call["owner"] ?? call["ownerType"];
            if (TypeJson.Read(ownerNode) is not TypeNode.Fqn owner || !owners.TryGetValue(owner.Name, out var methods))
                return;
            if ((call["method"] as JsonValue)?.TryGetValue<string>(out var name) != true
                || call["sig"] is not JsonArray sig) return;
            var wanted = sig.Select(TypeJson.Read).ToArray();
            if (wanted.Any(t => t == null)) return;
            var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
                ?? Array.Empty<TypeNode>();
            if (methodArgs.Any(t => t == null)) return;
            var ownerArgs = owner.Args ?? Array.Empty<TypeNode>();
            var matches = new List<(JsonObject Method, TypeNode[] Params)>();
            foreach (var candidate in methods.Where(m =>
                         (m["name"] as JsonValue)?.TryGetValue<string>(out var candidateName) == true
                         && candidateName == name && GenericArity(m) == methodArgs.Length))
            {
                if (candidate["params"] is not JsonArray parameters || parameters.Count != wanted.Length) continue;
                var declared = parameters.OfType<JsonObject>()
                    .Select(p => TypeJson.Read(p["type"])).ToArray();
                if (declared.Length != wanted.Length || declared.Any(t => t == null)) continue;
                var closed = declared.Select(t => SupertypeGraph.SubstOwnerTvs(t, ownerArgs))
                    .Select(t => SubstMethodTvs(t, methodArgs)).ToArray();
                if (Keys(declared).SequenceEqual(Keys(wanted)) || Keys(closed).SequenceEqual(Keys(wanted)))
                    matches.Add((candidate, declared));
            }
            if (matches.Count != 1 && kind is "newDelegate" or "newBoundDelegate")
                throw new InvalidOperationException(
                    $"bir2cir: {kind} target '{owner.Name}.{name}' resolves to {matches.Count} exact local methods; call={call.ToJsonString()}");
            if (matches.Count != 1) return;
            // Delegate-construction nodes identify the target method itself, so their descriptor must be the
            // declaration's open parameter vector. Ordinary calls already carry the receiver/method-substituted
            // call-site descriptor; replacing it with declaration-relative type variables would reinterpret those
            // variables in the caller's generic frame (notably for a constrained call through I<Int> from T : I<Int>).
            if (kind is "newDelegate" or "newBoundDelegate")
                call["sig"] = new JsonArray(matches[0].Params.Select(TypeJson.Write).ToArray());
            try { StampDelegateArgumentTargets(call, matches[0].Params, ownerArgs, methodArgs); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"bir2cir: failed to stamp a local call's delegate target for {owner.Name}.{name}: {ex.Message}; "
                    + $"call={call.ToJsonString()}", ex);
            }
        }

        void WalkDelegates(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var value in obj.Select(kv => kv.Value).ToList())
                    if (value != null) WalkDelegates(value);
                Bind(obj);
            }
            else if (node is JsonArray array)
                foreach (var item in array.ToList()) if (item != null) WalkDelegates(item);
        }
        foreach (var root in rootList) WalkDelegates(root);
    }

    // A constructor declaration's `baseArgs` is a call site too. Once its base type lowers to an external CLR owner,
    // resolve that delegation here and carry the PHYSICAL constructor declaration to CIR. This closes the one ctor
    // path that used to reach ilemit as only (owner, arity), making target reflection enumeration order observable.
    static void ResolveBaseConstructors(JsonNode root)
    {
        if (root is not JsonObject file || file["types"] is not JsonArray types) return;
        foreach (var item in types)
        {
            if (item is not JsonObject type || ReadOwnerNode(type["base"]) is not TypeNode.Fqn baseFqn) continue;
            if (_localTypes.Contains(baseFqn.Name)) continue;
            var open = ResolveOwnerType(baseFqn);
            if (open == null || type["ctors"] is not JsonArray ctors) continue;
            foreach (var ctorNode in ctors)
            {
                if (ctorNode is not JsonObject ctor || ctor["thisArgs"] is JsonArray
                    || ctor["baseArgs"] is not JsonArray baseArgs) continue;
                var semanticSig = (ctor["delegationSig"] as JsonArray)?.Where(x => x != null)
                    .Select(TypeJson.Read).ToList() ?? new List<TypeNode>();
                if (semanticSig.Count != baseArgs.Count)
                    throw new InvalidOperationException($"bir2cir: constructor delegation to '{baseFqn.Name}' carries "
                        + $"{baseArgs.Count} arguments but {semanticSig.Count} signature slots");
                var arity = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(c => c.GetParameters().Length == semanticSig.Count).ToList();
                var winner = PickUnique(arity, c => c.GetParameters(), semanticSig, baseFqn.Args,
                    $"base constructor owner={TypeNode.ToJson(baseFqn)} ({DescArgs(semanticSig)})");
                ctor["baseCtorRef"] = MemberRefJson(winner, MemberRefNode.Kinds.Ctor, open, baseFqn.Args);
                StampDelegateArgumentTargets(ctor, winner.GetParameters(),
                    baseFqn.Args ?? Array.Empty<TypeNode>(), Array.Empty<TypeNode>(), "baseArgs");
            }
        }
    }

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);
            Resolve(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList()) if (item != null) Walk(item);
        }
    }

    static void Resolve(JsonObject node)
    {
        // W1-S4 (#46/#183): a method DECLARATION overriding a .NET base-CLASS virtual (accessor) carries `pendingOverrideOwner`
        // (the base owner FQN, from DeclarationRename) but no `k` — resolve its base virtual off the ref.dll and carry
        // the complete `clrOverrideRef` so ilemit links the exact base slot (no name-only first-pick).
        if (node["pendingOverrideOwner"] != null) ResolveOverrideBase(node);
        switch ((node["k"] as JsonValue)?.GetValue<string>())
        {
            case "newClr": ResolveCtor(node); break;
            case "new": ResolveReferencedCtor(node); break;
            case "clrStatic": ResolveCall(node, instance: false); break;
            case "clrInstance": ResolveCall(node, instance: true); break;
            case "newBoundClrDelegate": ResolveBoundClrDelegate(node); ResolveDelegateCtor(node, "type"); break;
            case "newClrStaticDelegate": ResolveClrStaticDelegate(node); ResolveDelegateCtor(node, "type"); break;
            // A GENERIC .NET method gets its `resolvedMemberParams` from NetInteropBinding (kotc's FIR-resolved
            // `shapeTypes`). Resolve that input to one declaration here, author its scalar memberRef, and establish the
            // declared return for the crossing refusal. The descriptor is consumed and never reaches CIR.
            case "clrGenericStatic": ResolveGenericCallRet(node, instance: false); break;
            case "clrGenericInstance": ResolveGenericCallRet(node, instance: true); break;
            case "clrPropGet": ResolveProp(node, write: false); break;
            case "clrPropSet": ResolveProp(node, write: true); break;
            case "clrEventAdd": ResolveEvent(node); break;
            case "clrEventRemove": ResolveEvent(node); break;
            case "spreadConcat": ResolveSpreadConcat(node); break;
            case "forEachInline": ResolveForEachInline(node); break;
            // A CONSTRUCTION names the constructor it runs, and nothing else: its value is called through the
            // Invoke the CALL states, so no invoke identity belongs on the construction itself.
            case "newDelegate": case "newClosure": ResolveDelegateCtor(node, "funcType"); break;
            case "clrEventAccessorImpl": ResolveEventCas(node); break;
            // A static field is the same member whether it is read or written, and whether it arrived through the
            // Kotlin or the @Clr spelling. Found by enumerating the emitter's node kinds against this dispatch
            // rather than one gate failure at a time.
            case "staticField": case "staticFieldSet": case "clrStaticField": ResolveStaticField(node); break;
            case "delegateInvoke": ResolveDelegateInvoke(node, "funcType"); break;
            // The initializer may be an arbitrary stored function value, not only a newDelegate/newClosure node.
            // Name the Invoke on the operation that performs the call; an expression-local carrier would disappear
            // for `val f = { ... }; IntArray(n, f)` and force ilemit to rediscover the delegate member.
            case "newArrayInit": ResolveDelegateInvoke(node, "init"); break;
            case "constrainedCall": ResolveConstrainedCall(node); break;
            case "newBoundDelegate": ResolveDelegateCtor(node, "funcType"); ResolveDelegateInvoke(node, "funcType"); break;
            case "nullableNull": ResolveNullableConversion(node, "nullableNull"); break;
            case "nullableWrap": ResolveNullableConversion(node, "nullableWrap"); break;
            case "nullableHasValue": ResolveNullableConversion(node, "nullableHasValue"); break;
            case "nullableValue": ResolveNullableConversion(node, "nullableValue"); break;
            // `x as? T` for a value T builds the same Nullable<T> as an ordinary wrap does, so it needs the same
            // constructor named. It reads `elem` like its siblings; only the surrounding isinst differs.
            case "safeCastValue": ResolveNullableConversion(node, "nullableWrap"); break;
            case "newList": ResolveCollectionTemplate(node, "newList"); break;
            case "newSet": ResolveCollectionTemplate(node, "newSet"); break;
            case "newMap": ResolveCollectionTemplate(node, "newMap"); break;
            case "field": ResolveFieldAccess(node, write: false); break;
            case "setFieldExpr": ResolveFieldAccess(node, write: true); break;
            case "setField": ResolveFieldAccess(node, write: true); break;
            case "lateinitGet": ResolveLateinitField(node); break;
        }
    }

    // A plain top-level/static Kotlin call into a referenced assembly is already attributed to its file class by
    // MemberCallSubstitution (`owner`) or the frontend provenance carry (`calleeOwner`). Replace the frontend call-site
    // `sig` with the referenced declaration's physical signature, then resolve the scalar memberRef. This is the plain-call
    // counterpart of bir2cir's internal `resolvedMemberParams` on clr* nodes. Local same-assembly calls are absent from the
    // reference index and remain unchanged.
    public static void ResolveReferencedStaticCalls(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes, IReadOnlySet<string> externalCanonicalTypes,
        IReadOnlySet<string> localDeclarationIds, string file = null)
    {
        _refs = refs;
        // This pass runs before Apply, which normally initializes the local/external boundary. Set it here as an
        // explicit input too: retaining a prior file's static value (or the initial empty set) can bind a synthetic
        // type emitted by this compilation to a stale copy from its compile references.
        _localTypes = localTypes ?? new HashSet<string>();
        _externalCanonicalTypes = externalCanonicalTypes ?? new HashSet<string>();
        _localDeclarationIds = localDeclarationIds ?? new HashSet<string>();
        WalkReferencedStaticCalls(root, file ?? "<unknown>");
        DropKotlinSigSnapshots(root);
    }

    // Normalize the frontend/synthetic call dialect to an explicit declaration signature. Empty accessors commonly
    // omitted `sig`; CLR emission must see `[]`, not absence. For non-empty calls, only an already-carried declaration
    // vector (`shapeTypes` for a generic declaration, otherwise `argTypes`) may supply it — expression arity/types are
    // never consulted here. `shapeTypes` is consumed before type lowering so its open method TVs follow the ordinary
    // `sig` lowering path and do not leak as a second descriptor dialect into CIR.
    public static void EnsurePlainCallDescriptors(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var value in obj.Select(kv => kv.Value).ToList()) if (value != null) EnsurePlainCallDescriptors(value);
            var kind = (obj["k"] as JsonValue)?.GetValue<string>();
            if (kind is "newDelegate" or "newBoundDelegate")
            {
                if (obj["sig"] is not JsonArray && TypeJson.Read(obj["funcType"]) is TypeNode.Fn fn)
                    // A function type's extension receiver is a real leading CLR delegate/target-method parameter.
                    // `Params` excludes it; `DelegateParams` is the complete physical signature.
                    obj["sig"] = new JsonArray(fn.DelegateParams.Select(TypeJson.Write).ToArray());
                return;
            }
            if (kind is not ("callStatic" or "callInstance" or "constrainedCall")) return;
            if (obj["sig"] is JsonArray) return;
            if (obj["shapeTypes"] is JsonArray shapeTypes)
            {
                obj["sig"] = shapeTypes.DeepClone();
                obj.Remove("shapeTypes");
            }
            else if (obj["argTypes"] is JsonArray argTypes) obj["sig"] = argTypes.DeepClone();
            else if (obj["args"] is JsonArray args && args.Count == 0) obj["sig"] = new JsonArray();
        }
        else if (node is JsonArray array)
            foreach (var item in array.ToList()) if (item != null) EnsurePlainCallDescriptors(item);
    }

    // Nullable-generic/function erasure runs before top-level owner attribution. Preserve the frontend-resolved
    // descriptor out-of-band across those transforms. A scalar identity token rides the call while transforms clone
    // it; the token is removed after resolution and never enters CIR.
    public static void CaptureReferencedStaticCallSignatures(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if ((obj["k"] as JsonValue)?.GetValue<string>() == "callStatic"
                && obj["sig"] is JsonArray sig && obj[KotlinSigSnapshotId] == null)
            {
                var id = ++_nextKotlinSigSnapshotId;
                KotlinSigSnapshots.Add(id, (JsonArray)sig.DeepClone());
                obj[KotlinSigSnapshotId] = id;
            }
            foreach (var kv in obj.ToList())
                if (kv.Value != null) CaptureReferencedStaticCallSignatures(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) CaptureReferencedStaticCallSignatures(item);
        }
    }

    // Some structural lowerings replace a callStatic with a newly-built callStatic instead of cloning the node.
    // Carry only the scalar lookup token; the Kotlin descriptor stays in this pass's side table and is still removed
    // before CIR emission.
    public static void CarryReferencedStaticCallSignatureSnapshot(JsonObject source, JsonObject target)
    {
        if ((source[KotlinSigSnapshotId] as JsonValue)?.TryGetValue<int>(out var snapshotId) == true
            && KotlinSigSnapshots.ContainsKey(snapshotId))
            target[KotlinSigSnapshotId] = snapshotId;
    }

    static void WalkReferencedStaticCalls(JsonNode node, string context)
    {
        if (node is JsonObject obj)
        {
            var childContext = DeclarationContext(obj, context);
            foreach (var kv in obj.ToList())
                if (kv.Value != null) WalkReferencedStaticCalls(kv.Value, childContext);
            if ((obj["k"] as JsonValue)?.GetValue<string>() is "callStatic" or "callInstance" or "newDelegate" or "newBoundDelegate")
                ResolveReferencedStaticCall(obj, context);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) WalkReferencedStaticCalls(item, context);
        }
    }

    static string DeclarationContext(JsonObject node, string fallback)
    {
        if (node["body"] is not JsonArray
            || (node["name"] as JsonValue)?.TryGetValue<string>(out var name) != true) return fallback;
        if (node["pos"] is JsonObject pos
            && (pos["f"] as JsonValue)?.TryGetValue<string>(out var source) == true
            && (pos["l"] as JsonValue)?.TryGetValue<int>(out var line) == true)
        {
            var column = (pos["c"] as JsonValue)?.TryGetValue<int>(out var parsedColumn) == true
                ? $":{parsedColumn}" : "";
            return $"{source}:{line}{column}: {name}";
        }
        return $"{fallback}: {name}";
    }

    static void ResolveReferencedStaticCall(JsonObject node, string context)
    {
        // An instance call states its owner as `ownerType`; a static one as `owner`, or `calleeOwner` when a
        // lowering rebuilt the node. All three name the same thing — the type that declares the member.
        var ownerNode = node["owner"] is JsonNode owner && owner.GetValueKind() != System.Text.Json.JsonValueKind.Null
            ? owner
            : node["calleeOwner"] ?? node["ownerType"];
        if (ReadOwnerNode(ownerNode) is not TypeNode.Fqn ownerFqn
            || (node["method"] as JsonValue)?.TryGetValue<string>(out var name) != true
            || node["sig"] is not JsonArray sig)
            return;
        // A type this compilation emits stays on the local axis (#395). The search can otherwise answer from the
        // shipped twin, which for a stdlib self-build is the PREVIOUS build of the assembly being produced.
        // A canonical runtime synthetic is present in the input only as a representation template.  App CIR omits
        // that duplicate declaration and ilemit links the shipped TypeDef, so its calls belong to the external axis.
        // Every other locally-authored type keeps source-wins precedence, generated or not.
        if (_localTypes.Contains(ownerFqn.Name) && !_externalCanonicalTypes.Contains(ownerFqn.Name))
        {
            // An inline/member-extension reshape can temporarily put a referenced declaration identity on a call
            // through a local wrapper owner. That call remains on the established local substitution path; retaining
            // the external identity past this boundary would make ApplyLocal treat it as an unallocated local
            // MethodDef. Preserve only identities that actually belong to this emission unit.
            if ((node[DeclarationIdentityBinding.Key] as JsonValue)?.TryGetValue<string>(out var localAxisIdentity) == true
                && !_localDeclarationIds.Contains(localAxisIdentity))
                node.Remove(DeclarationIdentityBinding.Key);
            return;
        }
        if ((node[DeclarationIdentityBinding.Key] as JsonValue)?.TryGetValue<string>(out var carriedIdentity) == true
            && _localDeclarationIds.Contains(carriedIdentity))
            return;
        var selectionSig = sig;
        if ((node[KotlinSigSnapshotId] as JsonValue)?.TryGetValue<int>(out var snapshotId) == true
            && KotlinSigSnapshots.TryGetValue(snapshotId, out var snapshot))
            selectionSig = snapshot;
        var callSig = selectionSig.Select(TypeJson.Read).Where(t => t != null).ToArray();
        if (callSig.Length != selectionSig.Count) return;
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        // A referenced extension-property accessor can still arrive here with its preserved source-property identity
        // (or, before preservation, the Kotlin name plus `prop` role). Ask the reference index for the accessor's
        // physical name, exactly as the reshape does; without it the search is for a member that does not exist under
        // that spelling.
        KotlinPropertyAccessors.TryCallIdentity(node, out var sourcePropertyName, out var accessorKind);
        var propertyAccessorResolved = false;
        if (accessorKind is "get" or "set")
        {
            if (!_refs.TryKotlinPropertyAccessor(ownerFqn.Name, sourcePropertyName, accessorKind, callSig.Length, methodArity,
                    callSig, ownerFqn.Args ?? Array.Empty<TypeNode>(), out var physicalAccessor, out var accessorVirtual))
                return;
            KotlinPropertyAccessors.PreserveCallIdentity(node, sourcePropertyName, accessorKind);
            node.Remove("prop");
            node["method"] = physicalAccessor;
            // A virtual declaration remains a non-virtual call operand when Kotlin selected `super`. The declaration
            // flag describes its slot; it must not overwrite the dispatch decision already carried by the call.
            if (accessorVirtual && (node["super"] as JsonValue)?.GetValue<bool>() != true)
                node["virtual"] = true;
            name = physicalAccessor;
            propertyAccessorResolved = true;
        }
        var isStatic = node["k"]?.GetValue<string>() is "callStatic" or "newDelegate";
        // Property accessors already crossed their dedicated PropertyInfo/MethodSemantics association above. Keep
        // their established accessor-signature path: it also owns compiler-added parameters such as reified
        // nullability witnesses, which are not part of the Kotlin property-call vector at this point.
        if (!propertyAccessorResolved
            && (node[DeclarationIdentityBinding.Key] as JsonValue)?.TryGetValue<string>(out var declarationId) == true)
        {
            if (!_refs.TryDeclarationIdentityMethod(
                    declarationId, methodArity, isStatic, callSig, out var selectedSignature,
                    out var selectedDeclaration, out var selectedOwner, out var failure))
                throw new InvalidOperationException(
                    $"bir2cir: {context} [{node["k"]?.GetValue<string>()} {ownerFqn.Name}.{name}]: "
                    + $"frontend declaration identity '{declarationId}' {failure}");
            node["sig"] = new JsonArray(selectedSignature.Select(TypeJson.Write).ToArray());
            node["memberRef"] = MemberRefJson(selectedDeclaration, MemberRefNode.Kinds.Method,
                selectedOwner, ownerFqn.Args);
            StampResolvedMethodTypeParameters(node, selectedDeclaration);
            StampDelegateArgumentTargets(node, selectedDeclaration, ownerFqn.Args ?? Array.Empty<TypeNode>());
            node.Remove(DeclarationIdentityBinding.Key);
            return;
        }
        if (propertyAccessorResolved)
            node.Remove(DeclarationIdentityBinding.Key);
        if (!_refs.TryResolveStaticMemberSignature(
                ownerFqn.Name, name, methodArity, isStatic, callSig,
                ownerFqn.Args ?? Array.Empty<TypeNode>(), out var declarationSig,
                out var declaration, out var declaringOwner))
            return;
        node["sig"] = new JsonArray(declarationSig.Select(TypeJson.Write).ToArray());
        // A previously-compiled DotKt assembly is another assembly: its members are external, and the reason
        // this call kept a parameter vector rather than an identity was only that the vector was all this
        // resolution used to return.
        node["memberRef"] = MemberRefJson(declaration, MemberRefNode.Kinds.Method, declaringOwner, ownerFqn.Args);
        StampResolvedMethodTypeParameters(node, declaration);
        StampDelegateArgumentTargets(node, declaration, ownerFqn.Args ?? Array.Empty<TypeNode>());
    }

    static void DropKotlinSigSnapshots(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if ((obj[KotlinSigSnapshotId] as JsonValue)?.TryGetValue<int>(out var snapshotId) == true)
                KotlinSigSnapshots.Remove(snapshotId);
            obj.Remove(KotlinSigSnapshotId);
            foreach (var kv in obj.ToList())
                if (kv.Value != null) DropKotlinSigSnapshots(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) DropKotlinSigSnapshots(item);
        }
    }

    // ---- constructors --------------------------------------------------------------------------

    // A plain Kotlin `new` can target a declaration restored from a referenced DotKt assembly. It is just as external
    // to the current emission unit as `newClr`: resolve its physical constructor here, while declarations in this CIR
    // remain direct local links.
    static void ResolveReferencedCtor(JsonObject node)
    {
        if (ReadOwnerNode(node["type"]) is not TypeNode.Fqn ownerFqn || _localTypes.Contains(ownerFqn.Name)) return;
        if (ResolveOwnerType(ownerFqn) == null) return;
        ResolveCtor(node);
    }

    static void ResolveCtor(JsonObject node)
    {
        if (ReadOwnerNode(node["type"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: newClr owner '{ownerFqn.Name}' does not resolve to a .NET type");
        // A physical constructor descriptor may use CIR primitive shorthand or the equivalent BCL FQN, including
        // below arrays and other constructed slots. Reflection exposes the BCL spelling, so compare in the one
        // canonical physical vocabulary instead of making nested shorthand depend on which pass authored the node.
        var argNodes = ReadArgTypes(node)
            .Select(BirTypeLowering.CanonicalPhysicalSlotType)
            .ToList();
        var ownerArgs = ownerFqn.Args?
            .Select(BirTypeLowering.CanonicalPhysicalSlotType)
            .ToArray();
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == argNodes.Count).ToList();
        var win = PickUnique(ctors, c => c.GetParameters(), argNodes, ownerArgs,
            $"newClr owner={TypeNode.ToJson(ownerFqn)} ({DescArgs(argNodes)})");
        CoerceCtorCollectionViews(node, win.GetParameters(), argNodes, ownerArgs);
        node["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open, ownerArgs);
        StampDelegateArgumentTargets(node, win.GetParameters(), ownerArgs ?? Array.Empty<TypeNode>(),
            Array.Empty<TypeNode>());
        // A constructor has no declared return; its result is the node's own `type`. Stamped as `void` so the
        // chokepoint can tell "no return" from "nobody stamped one".
        StampResolvedMemberReturn(node, typeof(void));
        node.Remove("argTypes");
        node.Remove("memberSignature");
    }

    // Root-V lowers a readonly Kotlin collection nested in a constructed generic to its invariant CLR sibling.
    // A constructor descriptor remains a head-position slot, so the same source type can arrive here as
    // `IReadOnlyList<T>` while the selected closed constructor parameter is `IList<T>`. Materialize that already-
    // sanctioned collection-view conversion in CIR; ilemit then emits the stated cast and links the memberRef 1:1.
    // This is structural over the reflected constructor signature, not tied to Pair/Triple or any source name.
    static void CoerceCtorCollectionViews(JsonObject node, ParameterInfo[] parameters,
        IReadOnlyList<TypeNode> argNodes, TypeNode[] ownerArgs)
    {
        if (node["args"] is not JsonArray args || args.Count != parameters.Length) return;
        for (var i = 0; i < parameters.Length; i++)
        {
            var declared = MemberSigOf(parameters[i].ParameterType);
            var closed = ownerArgs is { Length: > 0 }
                ? SupertypeGraph.SubstOwnerTvs(declared, ownerArgs)
                : declared;
            if (!CollectionViewFaces.IsViewSeam(argNodes[i], closed)
                || args[i] is not JsonNode arg)
                continue;
            args[i] = new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(closed),
                ["e"] = arg.DeepClone(),
            };
        }
    }

    // ---- plain static / instance calls ---------------------------------------------------------

    static void ResolveCall(JsonObject node, bool instance)
    {
        var ownerNode = ReadOwnerNode(node["type"]);
        TypeNode.Fqn ownerFqn;
        Type open;
        // An ARRAY-owner instance method (`Array<T>.clone()` -> `System.Array.Clone`): every CLR array IS a
        // `System.Array`, so retarget the owner to it (the receiver `T[]` is assignable, no cast needed). Its `Clone`
        // etc. live on System.Array, not on the erased element type.
        if (instance && ownerNode is TypeNode.Array)
        {
            open = SystemArrayMlc();
            if (open == null) throw new InvalidOperationException("bir2cir: clrInstance array-owner method could not resolve System.Array");
            ownerFqn = new TypeNode.Fqn("System.Array");
            node["type"] = TypeJson.Write(ownerFqn);
        }
        else if (ownerNode is TypeNode.Fqn f)
        {
            ownerFqn = f;
            open = ResolveOwnerType(f);
            if (open == null)
                throw new InvalidOperationException($"bir2cir: {(instance ? "clrInstance" : "clrStatic")} owner '{ownerFqn.Name}' does not resolve to a .NET type");
        }
        else return;
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var argNodes = ReadArgTypes(node);
        // Protected declarations are part of the referenced Kotlin/CLR surface and the frontend has already enforced
        // their source access rule.
        // Reflection's Public-only lookup drops those legal slots; enumerate nonpublic methods but retain only CLR
        // Family/FamORAssem, never private or assembly-only declarations.
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
            (instance ? BindingFlags.Instance : BindingFlags.Static);
        var cands = Candidates(open, name, argNodes, ownerFqn.Args, flags)
            .Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)
            .ToList();
        // WITHOUT `typeArgs`, exclude generic-method DEFINITIONS so `Task.fromException` binds the non-generic
        // `Task FromException(Exception)`, not `Task<T> FromException<T>(Exception)` (no inferable T). WITH `typeArgs`
        // keep BOTH kinds: a generic Kotlin @ClrIntrinsic (`arrayCopy<T>`) can bind a NON-generic BCL method
        // (`Array.Copy(Array,…)`) OR a generic one (`Array.Fill<T>`) — the structural param match then disambiguates.
        bool hasTypeArgs = node["typeArgs"] is JsonArray ta && ta.Count > 0;
        if (!hasTypeArgs) cands = cands.Where(m => !m.IsGenericMethodDefinition).ToList();
        // An INTERFACE-owner miss with no candidate at all used to become a `clrDynInstance` node, which ilemit
        // emitted as `recv.GetType().GetMethod(name).Invoke(recv, args)`. That runtime name-only lookup preserved no
        // overload, declaring slot, explicit-interface implementation or generic arity, and it returned null — an
        // opaque NullReferenceException — whenever the receiver was a plain BCL collection, which is the common case.
        // The Kotlin members that motivated it (`removeAll`/`retainAll`/`addAll`) now have a physical representation:
        // MemberCallSubstitution routes them to the `kotlin.collections.ClrCollectionDefaults` dispatchers, and every
        // Kotlin implementer carries a real `DotKt.Runtime.CompilerServices.Kotlin*Slots` interface slot so its
        // override is reached by ordinary virtual dispatch. A member arriving here unresolved is therefore an upstream
        // routing gap, not something to resolve at run time; PickUnique's hard error propagates.
        var win = PickUnique(cands, m => m.GetParameters(), argNodes, ownerFqn.Args,
            $"{(instance ? "clrInstance" : "clrStatic")} owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        var dispatch = instance
            ? Dispatch(win, open, (node["super"] as JsonValue)?.GetValue<bool>() ?? false)
            : null;
        // `constrained. V; callvirt Slot` names the virtual SLOT, not V's override body. For example, the valid
        // unboxed TimeSpan.ToString form names System.Object.ToString; pairing `constrained. TimeSpan` with a
        // TimeSpan.ToString token makes the runtime consume the receiver incorrectly. Preserve the selected override
        // for signature/crossing facts below, but carry its base-definition token as the physical dispatch target.
        MethodInfo dispatchTarget = win;
        if (dispatch == "constrained")
            dispatchTarget = ConstrainedSlot(win, open);
        node["memberRef"] = MemberRefJson(dispatchTarget, MemberRefNode.Kinds.Method, open, ownerFqn.Args);
        StampResolvedMethodTypeParameters(node, win);
        StampDelegateArgumentTargets(node, win, ownerFqn.Args ?? Array.Empty<TypeNode>());
        StampResolvedMemberReturn(node, win.ReturnType);
        node.Remove("argTypes");
        if (instance) node["dispatch"] = dispatch;
    }

    // THE DECLARED RETURN OF A GENERIC .NET METHOD. Everything else about these nodes was already resolved
    // upstream, so this establishes one fact and touches nothing: the member's own return type, open (a method
    // type-variable stays positional), for the crossing refusal to read. Without it a foreign `List<int?> Make<T>()`
    // reached that refusal with no declared return at all and its caller-view `ret` — already erased to Kotlin's
    // `List<object>` — said nothing.
    //
    // UNKNOWN IS NOT SPELLED `void`. Stamping a fake `void` for an overload set this could not narrow satisfied the
    // chokepoint — a stamp WAS made — while telling the crossing refusal there was no declared return to object to,
    // so a C# `List<int?> Make<T>(int)` beside a `string Make<T>(string)` passed both and its
    // `List<Nullable<int32>>` was consumed as a `List<object>`. The node already carries the FIR-resolved internal
    // `resolvedMemberParams`, which is the exact matching input used to author the memberRef, so the return is resolved
    // through it by the same unique-match discipline every other member here uses — INCLUDING lookup through the
    // implemented interfaces, since a class may satisfy the Kotlin-surfaced member through a private explicit
    // MethodImpl body whose public name lives only on the interface. If that input resolves no declaration, the
    // reference ABI is incomplete and lowering stops before CIR is written.
    //
    // There is no "unresolved but emit later" state. A declaration that can become an external CIL operand — including
    // a synthesized suspend cold entry — is reference ABI and must be present with its full signature in the reference
    // assembly. Letting ilemit recover it from the runtime twin would make emission resolve meaning again and would also
    // produce CIR that the scalar-reference contract cannot represent.
    static void ResolveGenericCallRet(JsonObject node, bool instance)
    {
        if (node["memberRef"] is JsonObject && node.ContainsKey(ResolvedMemberReturnKey))
        {
            node.Remove("resolvedMemberParams");
            return;
        }
        if (node.ContainsKey(ResolvedMemberReturnKey))
            throw new InvalidOperationException(
                "bir2cir: generic external call carries a declared-return stamp without its resolved memberRef");
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var ownerFqn = ReadOwnerNode(node["type"]) as TypeNode.Fqn;
        var open = ownerFqn != null ? ResolveOwnerType(ownerFqn) : null;
        if (open == null || name == null)
            throw new InvalidOperationException(
                $"bir2cir: generic external call '{ownerFqn?.Name ?? "<unknown>"}.{name ?? "<unknown>"}' "
                + "does not resolve to a declaration in the reference set");
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var arity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        var argCount = (node["args"] as JsonArray)?.Count ?? 0;
        List<MethodInfo> Candidates(Type owner) => owner.GetMethods(flags)
            .Where(m => m.Name == name && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == arity
                        && m.GetParameters().Length == argCount)
            .ToList();
        var cands = Candidates(open);
        List<MethodInfo> InterfaceCandidates() => !instance
            ? new List<MethodInfo>()
            : SafeInterfaces(open).SelectMany(Candidates)
                .GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList();
        var sig = (node["resolvedMemberParams"] as JsonArray)?.Select(TypeJson.Read).ToList();
        // THE DESCRIPTOR DECIDES, and it decides FIRST. Taking a lone same-name candidate without asking whether the
        // descriptor selects it is how a `string Make<T>(string)` on the class answered for an `I.Make<T>(int)`
        // reached through an interface — a return read off a member the emitter does not link.
        if (sig != null && sig.Count == argCount && !sig.Any(t => t == null))
        {
            var win = TryPickUnique(cands, sig, ownerFqn.Args)
                      ?? TryPickUnique(InterfaceCandidates(), sig, ownerFqn.Args);
            if (win != null)
            {
                // The generic method DEFINITION is the identity; the call's own `typeArgs` instantiate it, exactly
                // as an ECMA MethodSpec wraps a MemberRef to the uninstantiated signature.
                node["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, ownerFqn.Args);
                StampResolvedMethodTypeParameters(node, win);
                StampDelegateArgumentTargets(node, win, ownerFqn.Args ?? Array.Empty<TypeNode>());
                StampResolvedMemberReturn(node, win.ReturnType);
            }
            else
                throw new InvalidOperationException(
                    $"bir2cir: generic external call '{ownerFqn.Name}.{name}<{arity}>' has no unique declaration "
                    + "matching its frontend-resolved parameter vector in the reference set");
            // The descriptor is this resolution's INPUT — it selects among the candidates above — and inputs do
            // not belong in the output. Leaving it made the reference travel beside the thing it replaced, which
            // is the arrangement every other node just stopped carrying.
            node.Remove("resolvedMemberParams");
            return;
        }
        // No usable descriptor means there is no resolved identity to consume. A unique name/arity candidate is not
        // an identity and must not become one merely because today's target happens to expose only one overload.
        throw new InvalidOperationException(
            $"bir2cir: generic external call '{ownerFqn.Name}.{name}<{arity}>' carries no usable "
            + "frontend-resolved parameter vector");
    }

    // `PickUnique`'s first two tiers without its diagnostics: the exact structural match, then the single applicable
    // one. Null where the set is empty or leaves more than one standing — the caller has another set to ask before it
    // may call that a hard error. The third tier (fewest `object` parameters) is deliberately absent: a DECLARED
    // parameter vector either is the member's or is not, and there is nothing to be more-specific about.
    static MethodInfo TryPickUnique(List<MethodInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var applicable = MostDerived(scored.Select(x => x.c).ToList());
        return applicable.Count == 1 ? applicable[0] : null;
    }

    // The same two tiers for a CONSTRUCTOR set. Constructors are never inherited, so there is no most-derived
    // question to ask: a set of them either narrows to one declaration or it does not.
    static ConstructorInfo TryPickUniqueCtor(List<ConstructorInfo> cands, List<TypeNode> sig, TypeNode[] ownerArgs)
    {
        var scored = cands.Select(c => (c, m: Match(c.GetParameters(), sig, ownerArgs)))
            .Where(x => x.m != MatchKind.No).ToList();
        var exact = scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList();
        if (exact.Count > 0) return exact.Count == 1 ? exact[0] : null;
        return scored.Count == 1 ? scored[0].c : null;
    }

    // ---- bound .NET method-reference (newBoundClrDelegate) --------------------------------------

    // W1-S5 (#46/#183) — RESOLVED-CLR-IR carry for a BOUND .NET method-reference (`netObj::method`, produced by
    // NetInteropBinding.ReshapeBoundDelegate). The target is a source-visible public/protected INSTANCE method on the owner `clrType`
    // (Codex-confirmed: the bound receiver comes from an IR dispatch receiver — statics have none, extensions are
    // excluded). Until now ilemit resolved it with `type.GetMethod(name, argTypes) ?? type.GetMethod(name)` — a
    // name+params match with a NAME-ONLY first-pick fallback (exactly the class #46 removes). The internal
    // `resolvedMemberParams` selects the unique target (0 = hard ABI error, >1 = malformed), then this pass authors its
    // complete memberRef. The ldftn-vs-ldvirtftn choice stays driven by the node's existing `virtual` field.
    static void ResolveBoundClrDelegate(JsonObject node)
    {
        if (ReadOwnerNode(node["clrType"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: newBoundClrDelegate owner '{ownerFqn.Name}' does not resolve to a .NET type");
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var argNodes = ReadArgTypes(node);
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        var cands = Candidates(open, name, argNodes, ownerFqn.Args,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(IsPublicOrProtected)
            .Where(m => m.IsGenericMethodDefinition
                ? m.GetGenericArguments().Length == methodArity
                : methodArity == 0)
            .ToList();
        var win = PickUnique(cands, m => m.GetParameters(), argNodes, ownerFqn.Args,
            $"newBoundClrDelegate owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        node["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, ownerFqn.Args);
        StampResolvedMethodTypeParameters(node, win);
        StampResolvedMemberReturn(node, win.ReturnType);
        node.Remove("argTypes");
    }

    static void ResolveClrStaticDelegate(JsonObject node)
    {
        if (ReadOwnerNode(node["clrType"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException(
                $"bir2cir: newClrStaticDelegate owner '{ownerFqn.Name}' does not resolve to a .NET type");
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var argNodes = ReadArgTypes(node);
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        var cands = Candidates(open, name, argNodes, ownerFqn.Args,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(IsPublicOrProtected)
            .Where(m => m.IsGenericMethodDefinition
                ? m.GetGenericArguments().Length == methodArity
                : methodArity == 0)
            .ToList();
        var win = PickUnique(cands, m => m.GetParameters(), argNodes, ownerFqn.Args,
            $"newClrStaticDelegate owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        node["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, ownerFqn.Args);
        StampResolvedMethodTypeParameters(node, win);
        StampResolvedMemberReturn(node, win.ReturnType);
        node.Remove("argTypes");
    }

    // ---- shared resolution ---------------------------------------------------------------------

    // Resolve the owner's OPEN definition off the ref.dll — via ResolveRefType, which (unlike ResolveNetType) does NOT
    // skip `kotlin.*`, so a stdlib-owner clr* node (`kotlin.collections.Iterator.next()`, kept by IteratorConsumer-
    // Normalization for the rt-stdlib link) resolves its declared member sig here too. RESPECTS generic arity: a generic
    // owner (args present) binds the arity-suffixed def (`TaskCompletionSource`1`), never a same-named NON-generic sibling.
    internal static Type ResolveOwnerType(TypeNode.Fqn ownerFqn)
    {
        // A generated nested companion's CIR token deliberately omits CLR generic-arity punctuation, including the
        // outer owner's backtick. Its validated [KotlinCompanion] association is the authority for the exact reflected
        // TypeDef spelling; use that association before the ordinary flat-name arity probe.
        if (_refs.TryCompanionMetadataCarrier(ownerFqn.Name, out _))
            return _refs.ResolveCompanionMetadataCarrier(ownerFqn.Name, ownerFqn.Args?.Length ?? 0);
        // A NESTED-generic reflection name already carries backtick arity + `+` separators (`Outer`1+Nested`, the
        // ConfigureAwait awaiter) — resolve it VERBATIM; BareOwnerFqn/StripGenericArity would truncate at the first
        // backtick and lose the nested type. (Its `args` instantiate the OUTER; a member whose sig has no outer type-var
        // — OnCompleted(Action) — matches on the open nested def regardless, so no MakeGenericType is needed.)
        if (ownerFqn.Name.Contains('`')) return _refs.ResolveRefType(ownerFqn.Name, 0);
        return RefDef(ownerFqn.Name, ownerFqn.Args?.Length ?? 0);
    }

    // The structured TypeNode carried by an owner slot.
    static TypeNode ReadOwnerNode(JsonNode typeSlot) => TypeJson.Read(typeSlot);

    static List<TypeNode> ReadArgTypes(JsonObject node) =>
        (node["argTypes"] as JsonArray)?.Where(x => x != null).Select(TypeJson.Read).ToList() ?? new List<TypeNode>();

    // DefaultArgSplice runs before NetInteropBinding, but it must read defaults from the exact MethodDef this pass will
    // later put in memberRef. Share this resolver, including constructed-owner substitution, interface traversal,
    // accessibility and overload ranking; a second approximation here can materialize a sibling's value irreversibly.
    internal static bool TryResolveExternalMethodForDefaults(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn,
        string name, int methodArity, bool isStatic, IReadOnlyList<TypeNode> callSignature,
        out MethodInfo declaration)
    {
        declaration = null;
        if (refs == null || ownerFqn == null || name == null || callSignature == null) return false;
        _refs = refs;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null) return false;
        TypeNode Physical(TypeNode type, bool typeArg) => BirTypeLowering.CanonicalPhysicalSlotType(
            BirTypeLowering.LowerPhysicalType(
                type, refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames, typeArg));
        var argNodes = callSignature.Select(type => Physical(type, typeArg: false)).ToList();
        var ownerArgs = ownerFqn.Args?.Select(type => Physical(type, typeArg: true)).ToArray();
        var flags = BindingFlags.Public | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = Candidates(open, name, argNodes, ownerArgs, flags)
            .Where(IsPublicOrProtected)
            .Where(method => method.IsGenericMethodDefinition
                ? method.GetGenericArguments().Length == methodArity
                : methodArity == 0)
            .ToList();
        declaration = PickUnique(candidates, method => method.GetParameters(), argNodes, ownerArgs,
            $"default argument owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        return true;
    }

    // Pick the UNIQUE member whose declared params match `argNodes`: Tier 1 all-exact; Tier 2 all-applicable (def-level
    // assignability, shallow — mirrors ilemit PickOpenCtor.ParamAccepts); Tier 3 fewest-`object` strict-min-unique.
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    static T PickUnique<T>(List<T> cands, Func<T, ParameterInfo[]> paramsOf, List<TypeNode> argNodes,
                           TypeNode[] ownerArgs, string desc) where T : MethodBase
    {
        var scored = cands.Select(c => (c, m: Match(paramsOf(c), argNodes, ownerArgs))).Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw Malformed(desc, exact);
        var appl = MostDerived(scored.Select(x => x.c).ToList());
        if (appl.Count == 1) return appl[0];
        if (appl.Count == 0) throw NoMatch(desc, cands);
        // Tier 3 — most-specific: fewest `object` params, strict-min-unique (parity with C#'s "better function member"
        // falling out of specificity: a non-`object` param beats `object`). A tie is a HARD malformed error, never a pick.
        var ranked = appl.Select(c => (c, obj: paramsOf(c).Count(p => IsObjectMlc(p.ParameterType)))).ToList();
        var min = ranked.Min(x => x.obj);
        var best = ranked.Where(x => x.obj == min).Select(x => x.c).ToList();
        if (best.Count == 1) return best[0];
        throw Malformed(desc, best);
    }

    // C#'s "most-derived declaring type wins" (§12.8.10.2): discard a candidate whose declaring type is a STRICT BASE of
    // another candidate's — a base CLASS (`Task<T>.GetAwaiter()` on Task`1 beats the inherited `Task.GetAwaiter()` on the
    // base Task) OR a base INTERFACE (`IEnumerable<T>.GetEnumerator()` beats `IEnumerable.GetEnumerator()`). resolvedMemberParams
    // (params) can't distinguish the return-only difference, so this shadowing rule is what makes the winner unique.
    static List<T> MostDerived<T>(List<T> hits) where T : MethodBase
    {
        if (hits.Count > 1) hits = hits.GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList();   // dedupe reflection duplicates
        if (hits.Count <= 1) return hits;
        return hits.Where(h => !hits.Any(o => !ReferenceEquals(o, h) && !SameDeclType(h.DeclaringType, o.DeclaringType)
            && (IsStrictBase(h.DeclaringType, o.DeclaringType) || IfaceImplementedBy(h.DeclaringType, o.DeclaringType)))).ToList();
    }

    static bool SameDeclType(Type a, Type b) => ReferenceEquals(a, b) || (a != null && b != null && SafeDef(a) == SafeDef(b));
    // True iff `baseT` is a STRICT base class of `derived` (walk `derived`'s base-CLASS chain; generic-def aware).
    static bool IsStrictBase(Type baseT, Type derived)
    {
        if (baseT == null || derived == null) return false;
        try { for (var t = derived.BaseType; t != null; t = t.BaseType) if (SafeDef(t) == SafeDef(baseT)) return true; } catch { }
        return false;
    }
    // True iff `baseIface` is a base INTERFACE of `derived` (derived implements/extends it) — the interface twin of IsStrictBase.
    static bool IfaceImplementedBy(Type baseIface, Type derived)
    {
        if (baseIface == null || derived == null || !baseIface.IsInterface) return false;
        try { return derived.GetInterfaces().Any(i => SafeDef(i) == SafeDef(baseIface)); } catch { return false; }
    }

    enum MatchKind { No, Assignable, Exact }

    static MatchKind Match(ParameterInfo[] ps, List<TypeNode> argNodes, TypeNode[] ownerArgs)
    {
        if (ps.Length != argNodes.Count) return MatchKind.No;
        var acc = MatchKind.Exact;
        for (int i = 0; i < ps.Length; i++)
        {
            var m = Applies(argNodes[i], ps[i].ParameterType, ownerArgs);
            if (m == MatchKind.No) return MatchKind.No;
            if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
        }
        return acc;
    }

    // Applicability of a DECLARED arg TypeNode `a` (from kotc's FIR-resolved `sig`) to a candidate OPEN-def param `p`.
    // `p` is ALIAS-RESOLVED first (a ref.dll member param typed through a stdlib @ClrTypeAlias is compared/emitted as
    // its BCL twin, matching the lowered arg).
    //   CONCRETE param -> the C#-binder LEAF rule: resolve the arg to an MLC Type, exact-identity or IsAssignableFrom
    //     (so `sbyte[]` binds `Sort(System.Array)`); an UNRESOLVABLE arg (a local/synthetic ref such as
    //     `dotkt$CharSequence`, or a function-type) binds only `object` (+ an arity-matching delegate param for a function
    //     arg) — the deterministic form of ilemit's former object-steering, never a first-pick.
    //   OPEN param (a type-var, or a constructed generic mentioning one) -> STRUCTURAL match under positional-tv
    //     equality: a class-var param at position i is satisfied by the DECLARED `tv(type,i)` (a method call carries the
    //     callee-owner's own class var) OR by the arg matching `ownerArgs[i]` (a ctor carries the SUBSTITUTED concrete
    //     arg — `Box<Int>(value: Int)`); a method tv by `tv(method,i)`; a constructed generic
    //     recurses, with a shallow def-derivation assignability (IReadOnlyCollection<E> -> IEnumerable<E>).
    static MatchKind Applies(TypeNode a, Type p, TypeNode[] ownerArgs)
    {
        if (a is TypeNode.Oblivious ob) return Applies(ob.Of, p, ownerArgs);
        p = AliasResolve(p);
        // An unmanaged pointer has its own recursive signature identity. Handle it before the concrete-type fast
        // path: `MapMlc` can materialize ordinary `int*`, but the CIR spelling `void*` deliberately uses the
        // primitive token `void`, which is not a resolvable nominal TypeRef. The semantic KLIB marker and the
        // already-lowered CIR pointer must answer identically at both resolution stages.
        if (p.IsPointer)
        {
            var pointerNode = a switch
            {
                TypeNode.Nullable nullable => nullable.Of,
                _ => a,
            };
            var pointee = pointerNode switch
            {
                TypeNode.Ptr ptr => ptr.Of,
                TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn, Args: { Length: 1 } } marker => marker.Args[0],
                _ => null,
            };
            if (pointee == null) return MatchKind.No;
            var physicalPointee = p.GetElementType();
            if (physicalPointee.FullName == "System.Void"
                && pointee is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void", Args: null })
                return MatchKind.Exact;
            return Applies(pointee, physicalPointee, ownerArgs);
        }
        if (!p.IsGenericParameter && !p.ContainsGenericParameters)
        {
            var aT = MapMlc(a);
            if (aT != null)
            {
                if (aT == p) return MatchKind.Exact;
                try { if (p.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
                // The DECLARED arg is the erased top type `object` (kotlin.Any) — the runtime value is really a subtype,
                // so a REFERENCE param accepts it via an implicit downcast (emitted as a castclass). Mirrors the old
                // object-arg acceptance (`fun createArray(cls: Any, ...)` binding to `Array.CreateInstance(Type,...)`).
                if (IsObjectMlc(aT) && !p.IsValueType) return MatchKind.Assignable;
                // Kotlin's primitive DUAL-REPRESENTATION: a signed integer and its SAME-WIDTH unsigned twin share the
                // bit pattern (Long==ULong, Int==UInt, …), so a @ClrIntrinsic bit-op binds `Long.countLeadingZeroBits()`
                // to `BitOperations.LeadingZeroCount(UInt64)` — the arg reinterprets. Same-width only (never Int64->UInt32).
                if (SameWidthIntegral(aT, p)) return MatchKind.Assignable;
                return MatchKind.No;
            }
            // Some compiler-owned physical types are intentionally not general-purpose ref-resolver inputs
            // (`dotkt$CharSequence` is the canonical example), but they can still occur verbatim in a referenced
            // declaration signature.  Their unresolved TypeNode is exact when its canonical metadata identity is
            // exactly the reflected parameter identity; this is identity comparison, not assignability inference.
            if (a is TypeNode.Fqn unresolved
                && string.Equals(StripArity(Dotted(unresolved.Name)),
                    StripArity(Dotted(p.FullName ?? p.Name)), StringComparison.Ordinal))
                return MatchKind.Exact;
            if (a is TypeNode.Fn fn) return MatchFnToDelegate(fn, p, ownerArgs);
            // An array with an UNRESOLVABLE element (`Array<T>`, T a type-var) still IS a System.Array/object and
            // implements the non-generic array interfaces — `System.Array` assignable to `p` means `T[]` is too (e.g.
            // the generic `arrayCopy(Array<T>,...)` binding to `Array.Copy(System.Array,...)`).
            if (a is TypeNode.Array)
            {
                var sysArr = SystemArrayMlc();
                if (sysArr != null) { try { if (p.IsAssignableFrom(sysArr)) return MatchKind.Assignable; } catch { } }
            }
            return IsObjectMlc(p) ? MatchKind.Assignable : MatchKind.No;
        }
        // p is OPEN (a generic parameter or a constructed generic mentioning one).
        if (p.IsGenericParameter)
        {
            int i = p.GenericParameterPosition;
            if (p.DeclaringMethod != null)
                return a is TypeNode.Tv { Scope: "method" } mtv && mtv.I == i ? MatchKind.Exact : MatchKind.No;
            // class type-var: (a) DECLARED — the arg is the owner's own class var at position i (a method call); OR
            // (b) SUBSTITUTED — the arg matches the owner's instantiation arg at position i (a ctor's concrete param).
            if (a is TypeNode.Tv { Scope: "type" } ttv && ttv.I == i) return MatchKind.Exact;
            if (ownerArgs != null && i >= 0 && i < ownerArgs.Length && ownerArgs[i] != null) return NodeEq(a, ownerArgs[i]);
            return MatchKind.No;
        }
        if (p.IsByRef) return a is TypeNode.ByRef b ? Applies(b.Of, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsArray) return a is TypeNode.Array ar ? Applies(ar.Elem, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsGenericType && SafeDef(p) == NullableDef())
            return a is TypeNode.Nullable nv ? Applies(nv.Of, p.GetGenericArguments()[0], ownerArgs) : MatchKind.No;
        if (a is TypeNode.Nullable nn) return Applies(nn.Of, p, ownerArgs);   // nullable ref arg = same .NET type
        if (a is TypeNode.Fn fnOpen) return MatchFnToDelegate(fnOpen, p, ownerArgs);     // a lambda arg binding an OPEN delegate param (`ThreadLocal<T>(Func<T>)`)
        if (a is not TypeNode.Fqn f) return MatchKind.No;
        var pdef = SafeDef(p);
        var adef = RefDef(f.Name, f.Args?.Length ?? 0);
        if (adef == null) return MatchKind.No;
        var adefDef = SafeDef(adef);
        if (adefDef == pdef)
        {
            if (f.Args == null) return MatchKind.Exact;
            var pa = p.GetGenericArguments();
            if (pa.Length != f.Args.Length) return MatchKind.No;
            var acc = MatchKind.Exact;
            for (int i = 0; i < pa.Length; i++)
            {
                var m = Applies(f.Args[i], pa[i], ownerArgs);
                if (m == MatchKind.No) return MatchKind.No;
                if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
            }
            return acc;
        }
        try { if (adefDef.GetInterfaces().Any(i => SafeDef(AliasResolve(i)) == pdef)) return MatchKind.Assignable; } catch { }
        return MatchKind.No;
    }

    // Structural equality of an arg TypeNode against the owner's instantiation-arg TypeNode (the ctor SUBSTITUTED-param
    // bridge): both resolve to the same MLC Type (exact), or the arg is assignable to the instantiation arg.
    static MatchKind NodeEq(TypeNode a, TypeNode ownerArg)
    {
        if (a == ownerArg) return MatchKind.Exact;
        if (CollectionViewFaces.IsViewSeam(a, ownerArg)) return MatchKind.Assignable;
        // A constructed owner slot closed over Object accepts every source value through the CLR's ordinary
        // reference conversion / boxing path. `Pair<Any, …>(nullableInt, …)` is the constructor form of the same
        // generic object-erasure seam already accepted for method parameters below.
        if (ownerArg is TypeNode.Fqn { Name: "object" or "System.Object", Args: null })
            return MatchKind.Assignable;
        // A `Nullable<value>` arg (`Int?`) binds the underlying value owner-arg (`T`=Int): the value-nullable GENERIC
        // erasure (#128) — `IComparer<Int>.Compare(x: Int?, y: Int?)` binds the constructed `Compare(Int,Int)`.
        if (a is TypeNode.Nullable an && NodeEq(an.Of, ownerArg) != MatchKind.No) return MatchKind.Assignable;
        var aT = MapMlc(a); var oT = MapMlc(ownerArg);
        if (aT != null && oT != null)
        {
            if (aT == oT) return MatchKind.Exact;
            try { if (oT.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
        }
        // The arg is the erased top type `object` (kotlin.Any) — in a GENERIC erasure it IS the owner-arg boxed/as-is
        // (a reference class, an unresolvable LOCAL class, OR a boxed value type unboxed at emit): `Comparable<Ver>.
        // compareTo(other:object)` binds `CompareTo(Ver)`, `IComparer<Int>.Compare(object,object)` binds `Compare(Int,Int)`.
        if (a is TypeNode.Fqn { Name: "object" or "System.Object", Args: null }) return MatchKind.Assignable;
        return MatchKind.No;
    }

    // Resolve a ref.dll-reflected type through the @ClrTypeAlias index to its BCL-MLC twin, recursively over generic
    // args / element types; a generic parameter and a non-aliased type are returned unchanged.
    static Type AliasResolve(Type t)
    {
        if (t == null || t.IsGenericParameter) return t;
        if (t.IsArray)
        {
            var e = AliasResolve(t.GetElementType());
            if (ReferenceEquals(e, t.GetElementType())) return t;
            return t.IsSZArray ? e.MakeArrayType() : e.MakeArrayType(t.GetArrayRank());
        }
        if (t.IsByRef) { var e = AliasResolve(t.GetElementType()); return ReferenceEquals(e, t.GetElementType()) ? t : e.MakeByRefType(); }
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var rdef = AliasDef(def);
            var args = t.GetGenericArguments().Select(AliasResolve).ToArray();
            if (rdef == null && args.Zip(t.GetGenericArguments(), ReferenceEquals).All(x => x)) return t;
            try { return (rdef ?? def).MakeGenericType(args); } catch { return t; }
        }
        return AliasDef(t) ?? t;
    }

    // The BCL-MLC type an @ClrTypeAlias def maps to, or null when the def is not aliased.
    static Type AliasDef(Type def)
    {
        return _refs.Aliases.TryGetValue(AliasKey(def), out var bcl)
            ? RefDef(bcl, def.IsGenericType ? def.GetGenericArguments().Length : 0)
            : null;
    }

    /// <summary>
    /// The @ClrTypeAlias index key for a reflected type: dotted, with the arity backtick dropped from EVERY
    /// nesting segment.
    /// </summary>
    /// <remarks>
    /// Truncating at the FIRST backtick instead keys `Map`2+Entry`2` as `kotlin.collections.Map`, so a nested
    /// type inherits its OUTER type's alias — `Map.Entry` resolved to `IDictionary`. Nothing caught that while
    /// the only consumer matched on parameters, because the mis-aliased type sat in a return.
    /// </remarks>
    static string AliasKey(Type def) =>
        string.Join('.', (def.FullName ?? def.Name).Split('+').Select(StripArity));

    // Resolve a .NET type by name off the ref.dll, RESPECTING generic arity: probe the arity-suffixed def (`Foo`1`)
    // FIRST when arity>0, so a same-named NON-generic sibling (`TaskCompletionSource`/the `System.Nullable` static class)
    // never shadows the generic def (ResolveRefType/ResolveNetType probe the bare name first).
    static Type RefDef(string owner, int arity)
    {
        var physical = owner.Contains('`') || owner.Contains('+');
        if (physical)
            return _refs.ResolveRefType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner), 0);
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner);
        // A flattened nested identity needs each declaring segment's own metadata arity
        // (`Outer`1+Leaf`1`), not one suffix made from the flattened total (`Outer+Leaf`2`).
        if (_refs.TryExactPhysicalTypeName(bare, arity, out var exact))
        {
            if (exact == null)
                throw new InvalidOperationException(
                    $"ambiguous CLR metadata identity for nested type '{bare}' with flattened arity {arity}");
            // The exact spelling is authoritative. If local-source precedence rejects it or its declaring reference
            // is unavailable, do not fall back to a different aggregate-arity TypeDef.
            return _refs.ResolveRefType(exact, 0);
        }
        if (arity > 0 && !bare.Contains('`') && _refs.ResolveRefType(bare + "`" + arity, arity) is { } g) return g;
        return _refs.ResolveRefType(bare, arity);
    }

    // A concrete arg TypeNode -> its MLC Type (null when it embeds an open tv / a local-emitted / a delegate / can't construct).
    static Type MapMlc(TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Oblivious o: return MapMlc(o.Of);
            case TypeNode.ByRef b: { var e = MapMlc(b.Of); return e?.MakeByRefType(); }
            case TypeNode.Ptr p: { var e = MapMlc(p.Of); return e?.MakePointerType(); }
            case TypeNode.Array a:
            {
                var e = MapMlc(a.Elem);
                return e == null ? null : a.SzArray ? e.MakeArrayType() : e.MakeArrayType(a.Rank);
            }
            case TypeNode.Nullable n:
            {
                var inner = MapMlc(n.Of);
                if (inner == null) return null;
                if (!inner.IsValueType) return inner;
                try { return NullableDef()?.MakeGenericType(inner); } catch { return null; }
            }
            case TypeNode.Tv: return null;
            case TypeNode.Fn: return null;
            case TypeNode.Fqn f:
            {
                var baseT = RefDef(f.Name, f.Args?.Length ?? 0);
                if (baseT == null) return null;
                if (f.Args == null || f.Args.Length == 0) return baseT;
                if (!baseT.IsGenericTypeDefinition) return baseT;
                var margs = f.Args.Select(MapMlc).ToArray();
                if (margs.Any(x => x == null)) return null;
                try { return baseT.MakeGenericType(margs); } catch { return null; }
            }
        }
        return null;
    }

    static Type _nullableDef;
    static Type NullableDef() => _nullableDef ??= RefDef("System.Nullable", 1);
    static Type _sysArr;
    // This is an internal ABI-resolution probe, not a NetInterop ownership decision. Use ResolveRefType so DotKt
    // declaration ownership filters cannot hide the CLR root array type while matching `T[]` to Array.Copy(Array,...).
    static Type SystemArrayMlc() => _sysArr ??= _refs.ResolveRefType("System.Array");

    // The candidate set for `name`, PREFERRING the owner's OWN declared members over interface members
    // (C#'s "most-derived declaring type wins" — §12.8.10.2): reflection's GetMethods surfaces inherited CLASS
    // members, but not an interface slot implemented by a private explicit MethodImpl body. GetInterfaces is therefore
    // the fallback for class and interface owners alike. Adding those slots unconditionally would make
    // `IEnumerable<T>.GetEnumerator()` ambiguous with the inherited non-generic `IEnumerable.GetEnumerator()`
    // (resolvedMemberParams = [] cannot distinguish return-type-only overloads), so consult them only when no own member is
    // applicable. Static calls never bind an implemented-interface slot.
    static List<MethodInfo> Candidates(Type open, string name, List<TypeNode> argNodes, TypeNode[] ownerArgs, BindingFlags flags)
    {
        var own = new List<MethodInfo>();
        try { own.AddRange(open.GetMethods(flags).Where(m => m.Name == name &&
            m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        if ((flags & BindingFlags.Instance) == 0 ||
            own.Any(m => Match(m.GetParameters(), argNodes, ownerArgs) != MatchKind.No))
            return own;
        var withInterfaces = new List<MethodInfo>(own);
        foreach (var bi in SafeInterfaces(open))
            try { withInterfaces.AddRange(bi.GetMethods(flags).Where(m => m.Name == name &&
                m.GetParameters().Length == argNodes.Count && IsPublicOrProtected(m))); } catch { }
        return withInterfaces;
    }

    static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

    // Kotlin's signed<->unsigned SAME-WIDTH integral TWIN (identical bit pattern; a @ClrIntrinsic bit-op reinterprets,
    // `Long.countLeadingZeroBits()` -> `BitOperations.LeadingZeroCount(UInt64)`). `Char` is DELIBERATELY excluded (it is
    // a distinct Kotlin type with explicit `.code`/`.toChar()` conversions — reinterpreting a Short into a `char` param
    // would silently print "A" for 65). Only the exact signed/unsigned pairs qualify.
    static bool SameWidthIntegral(Type a, Type b)
    {
        static string W(Type t) => (t.FullName ?? t.Name) switch
        {
            "System.SByte" or "System.Byte" => "8",
            "System.Int16" or "System.UInt16" => "16",
            "System.Int32" or "System.UInt32" => "32",
            "System.Int64" or "System.UInt64" => "64",
            "System.IntPtr" or "System.UIntPtr" => "n",
            _ => null,
        };
        var wa = W(a); return wa != null && wa == W(b);
    }
    static Type SafeDef(Type t) { try { return t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; } catch { return t; } }
    internal static string DeclaringTypeIdentity(MethodBase member)
    {
        var declaring = member?.DeclaringType
            ?? throw new InvalidOperationException($"bir2cir: resolved member '{member}' has no declaring type");
        declaring = SafeDef(AliasResolve(declaring));
        return declaring.FullName ?? declaring.Name;
    }
    internal static JsonNode DeclaringTypeDescriptor(MethodBase member)
        => TypeJson.Write(new TypeNode.Fqn(DeclaringTypeIdentity(member)));
    static bool IsObjectMlc(Type t) { try { return t.FullName == "System.Object"; } catch { return false; } }
    static bool IsVoidNode(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" or "kotlin.Unit" };

    // A function-type arg binds an `object` param OR a delegate param (BCL Func/Action or a stdlib delegate, possibly
    // OPEN — `Func<T>`). Match arity + void-ness, and each param/return STRUCTURALLY — but ONLY reject on a genuine
    // mismatch of RESOLVABLE types (`(Int)->Unit` must NOT bind `Action<string>`). An UNRESOLVABLE lambda side (a local/
    // kotlin.* type — `(MatchResult)->CharSequence` binding a dll2klib `MatchEvaluator(System.Text..Match)`) is a
    // WILDCARD (the delegate mapping bridges the Kotlin↔BCL types). A lambda->delegate is a CONVERSION -> Assignable.
    static MatchKind MatchFnToDelegate(TypeNode.Fn fn, Type p, TypeNode[] ownerArgs)
    {
        if (IsObjectMlc(p)) return MatchKind.Assignable;
        if (!IsDelegateType(p)) return MatchKind.No;
        var targetFamily = DelegateFamily(p);
        if (targetFamily != null && fn.Clr != targetFamily) return MatchKind.No;
        MethodInfo invoke; try { invoke = p.GetMethod("Invoke"); } catch { return MatchKind.No; }
        if (invoke == null) return MatchKind.No;
        var ips = invoke.GetParameters();
        var dp = fn.DelegateParams;
        if (ips.Length != dp.Length) return MatchKind.No;
        bool retVoid; try { retVoid = invoke.ReturnType.FullName is "System.Void" or "kotlin.Unit"; } catch { retVoid = false; }
        if (retVoid != IsVoidNode(fn.Ret)) return MatchKind.No;
        for (int i = 0; i < dp.Length; i++) if (Incompatible(dp[i], ips[i].ParameterType, ownerArgs)) return MatchKind.No;
        if (!retVoid && Incompatible(fn.Ret, invoke.ReturnType, ownerArgs)) return MatchKind.No;
        return MatchKind.Assignable;
    }

    // A RESOLVABLE lambda side that structurally fails against the delegate's Invoke type — a genuine element mismatch.
    // An unresolvable side (MapMlc null) is a wildcard (No verdict), so the dll2klib Kotlin↔BCL delegate bridge passes.
    static bool Incompatible(TypeNode side, Type invokeType, TypeNode[] ownerArgs) =>
        MapMlc(side) != null && Applies(side, invokeType, ownerArgs) == MatchKind.No;
    static bool IsDelegateType(Type t)
    {
        try { for (var c = t; c != null; c = c.BaseType) if (c.FullName == "System.MulticastDelegate") return true; } catch { }
        return false;
    }

    // dispatch (clrInstance): mirrors ilemit EmitInstanceCall (Bodies.cs). All facts are MLC-readable off the resolved
    // MethodInfo + the owner value-type-ness. constraintType is redundant (== the owner `type` slot), so NOT carried.
    // INVARIANT: IsVirtual/IsFinal are read off the REF.dll member while ilemit links the RT member (same-source builds
    // keep them identical); an rt-side `sealed`/virtuality change therefore requires a matching ref rebuild.
    //   super (non-virtual base slot, ref receiver) -> call ; non-virtual -> call ; virtual ref receiver -> callvirt ;
    //   virtual FINAL on a value type -> call ; virtual non-final inherited/overridden by a value type ->
    //   constrained.callvirt through the method's base-definition slot.
    static string Dispatch(MethodInfo mi, Type owner, bool superCall)
    {
        bool valueOwner; try { valueOwner = owner.IsValueType; } catch { valueOwner = false; }
        bool isVirtual; try { isVirtual = mi.IsVirtual; } catch { isVirtual = false; }
        bool isFinal; try { isFinal = mi.IsFinal; } catch { isFinal = false; }
        if (superCall && !valueOwner) return "call";
        if (!isVirtual) return "call";
        if (!valueOwner) return "callvirt";
        if (isFinal) return "call";
        return "constrained";
    }

    // MetadataLoadContext does not reliably connect an override in a reference assembly to GetBaseDefinition().
    // Recover the root virtual slot from the value type's base chain using the CLR method-signature identity. A
    // constrained callvirt must name that slot (Object.ToString, for example), while the constraint selects the
    // concrete value-type implementation without boxing.
    static MethodInfo ConstrainedSlot(MethodInfo method, Type owner)
    {
        MethodInfo result = method;
        try
        {
            var reflected = method.GetBaseDefinition();
            if (reflected != null) result = reflected;
        }
        catch { }
        try
        {
            var wanted = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var genericArity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
            for (var type = owner.BaseType; type != null; type = type.BaseType)
            {
                const BindingFlags declaredInstance = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var matches = type.GetMethods(declaredInstance).Where(candidate =>
                    candidate.Name == method.Name && candidate.IsVirtual &&
                    (candidate.IsGenericMethod ? candidate.GetGenericArguments().Length : 0) == genericArity &&
                    candidate.GetParameters().Select(p => p.ParameterType).SequenceEqual(wanted)).ToList();
                if (matches.Count == 1) result = matches[0];
            }
        }
        catch { }
        return result;
    }

    // ---- resolvedMemberParams (winning member params -> lowered TypeNode array) ---------------------------


    // THE FOREIGN DECLARED RETURN, stamped beside `resolvedMemberParams` for the crossing refusal to read (#86).
    //
    // A node's own `ret` is the CALLER's Kotlin view of the result and is erased as a Kotlin slot — correctly, since
    // it is what the value's Kotlin type is. What no key stated is what the MEMBER declares, so a C# `List<int?>
    // Make()` was seen as returning Kotlin's `List<object>`, was not refused, and left a `List<Nullable<int32>>` on a
    // stack typed as the unrelated Kotlin form. `resolvedMemberParams` is that channel for parameters; this is its return twin.
    //
    // A pass-to-pass fact, NOT a CIR key: ForeignNullableGenericCrossing reads it and strips it, so nothing reaches
    // the emitter that the emitter does not consume.
    internal const string ResolvedMemberReturnKey = "resolvedMemberReturn";

    // ALWAYS stamped where a foreign declaration is established, INCLUDING for `void`. A missing stamp and a
    // genuinely void member were otherwise the same observation, so nothing could tell an omission from a fact —
    // and an omission is exactly what let a generic method's and a field's declared type go unchecked. With `void`
    // written explicitly, `CheckForeignDeclStamped` below can assert the invariant mechanically.
    static void StampResolvedMemberReturn(JsonObject node, Type declaredReturn)
        => node[ResolvedMemberReturnKey] = TypeJson.Write(
            declaredReturn == null || declaredReturn == typeof(void)
                ? new TypeNode.Fqn("void")
                : MemberSigOf(declaredReturn));

    static JsonArray MemberSig(ParameterInfo[] ps)
    {
        var arr = new JsonArray();
        foreach (var p in ps) arr.Add(TypeJson.Write(MemberSigOf(p.ParameterType)));
        return arr;
    }

    // A resolved OPEN-def member's param Type -> its declared-param TypeNode in the CLR-lowered vocabulary (BCL FullName
    // spellings, matching S1's lowered resolvedMemberParams). A class/method generic param -> a positional tv; a delegate keeps its
    // concrete Fqn (unlike TypeNodeOf, which drops delegates) so ilemit can link the exact slot.
    internal static TypeNode MemberSigOf(Type t)
    {
        t = AliasResolve(t);   // a ref.dll @ClrTypeAlias param -> its BCL twin, so ilemit's MapType links the rt-stdlib slot
        if (t.IsByRef) return new TypeNode.ByRef(MemberSigOf(t.GetElementType()));
        if (t.IsPointer) return new TypeNode.Ptr(MemberSigOf(t.GetElementType()));
        if (t.IsArray) return ArrayOf(t, MemberSigOf(t.GetElementType()));
        if (t.IsGenericParameter)
            return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // Kotlin function delegate families stay `fn`, carrying their exact nominal family. Unknown/custom CLR delegates
        // remain FQNs below.
        if (IsShapeDelegate(t)) return DelegateFn(t);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(MemberSigOf).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static bool IsShapeDelegate(Type t) => IsDelegateType(t) && DelegateFamily(t) != null;

    static string DelegateFamily(Type t)
    {
        Type def;
        try { def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; }
        catch { return null; }
        if (def.Namespace == "System")
        {
            if (def.Name == "Action" || def.Name.StartsWith("Action`", StringComparison.Ordinal)) return "System.Action";
            if (def.Name.StartsWith("Func`", StringComparison.Ordinal)) return "System.Func";
        }
        if (def.Namespace == "DotKt.Runtime.CompilerServices")
        {
            if (def.Name.StartsWith("KAction`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KAction";
            if (def.Name.StartsWith("KFunc`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KFunc";
        }
        return null;
    }

    static TypeNode DelegateFn(Type t)
    {
        var invoke = t.GetMethod("Invoke");
        if (invoke == null) return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));   // defensive: not a real delegate
        var ps = invoke.GetParameters().Select(p => MemberSigOf(p.ParameterType)).ToArray();
        var ret = invoke.ReturnType.FullName is "System.Void" or "kotlin.Unit" ? new TypeNode.Fqn("void") : MemberSigOf(invoke.ReturnType);
        return new TypeNode.Fn(false, ret, ps, null, DelegateFamily(t));
    }

    static string Dotted(string s) => s.Replace('+', '.');
    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    // ---- diagnostics ---------------------------------------------------------------------------

    static string DescArgs(List<TypeNode> a) => string.Join(",", a.Select(x => TypeNode.ToJson(x)));
    static InvalidOperationException NoMatch<T>(string desc, List<T> cands) where T : MethodBase =>
        new($"bir2cir: no .NET member matches the resolved descriptor {desc} (ABI mismatch; {cands.Count} same-name/arity candidate(s): {string.Join("; ", cands.Select(c => c.ToString()))})");
    static InvalidOperationException Malformed<T>(string desc, List<T> hits) where T : MethodBase =>
        new($"bir2cir: resolved descriptor {desc} is AMBIGUOUS — {hits.Count} members match (malformed): {string.Join("; ", hits.Select(c => c.ToString()))}");

    // THE CHOKEPOINT: a node this pass resolved against a .NET member CARRIES that member's declared return.
    //
    // Two omissions of exactly this shape shipped before it existed — a generic method, which never entered the
    // switch, and a genuine public field, which returned early — and each one silently removed a whole family from
    // the crossing refusal. Review caught them; the build did not. It does now.
    //
    // WHAT IT ASSERTS, precisely, so nobody reads more into it than it holds: every node carrying a resolved
    // parameter vector (`resolvedMemberParams`), and every node whose KIND only this pass produces, also carries `resolvedMemberReturn`.
    // It cannot speak for a `field`/`setField` node, whose kind Kotlin uses too and whose owner may be local — those
    // stamp in their own branches, and the assertion below covers them only once they carry a `resolvedMemberParams`.
    static readonly string[] ResolvedOnlyKinds =
    {
        "newClr", "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance", "newBoundClrDelegate",
        "newClrStaticDelegate",
        "clrPropGet", "clrPropSet",
    };

    static void CheckForeignDeclStamped(JsonNode node, string file)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var k = (obj["k"] as JsonValue)?.TryGetValue<string>(out var ks) == true ? ks : null;
                // Event nodes have both a local and a referenced form.  Only the referenced form carries a memberRef;
                // the local form names an emitted synthesized accessor and must not masquerade as a foreign declaration.
                var resolvedEvent = k is "clrEventAdd" or "clrEventRemove" && obj["memberRef"] != null;
                var resolved = obj["resolvedMemberParams"] != null || resolvedEvent
                    || (k != null && Array.IndexOf(ResolvedOnlyKinds, k) >= 0);
                if (resolved && obj[ResolvedMemberReturnKey] == null)
                    throw new InvalidOperationException(
                        $"bir2cir: {file}: a '{k ?? "?"}' node resolved against a .NET member carries no declared "
                        + "return (resolvedMemberReturn). Every site that establishes a foreign declaration must stamp one — "
                        + "the crossing refusal reads it, and an unstamped node silently leaves its family unchecked.");
                foreach (var kv in obj) if (kv.Value != null) CheckForeignDeclStamped(kv.Value, file);
                break;
            }
            case JsonArray arr:
                foreach (var it in arr) if (it != null) CheckForeignDeclStamped(it, file);
                break;
        }
    }

    public static void CheckStamped(JsonNode root, string file) => CheckForeignDeclStamped(root, file);
}
