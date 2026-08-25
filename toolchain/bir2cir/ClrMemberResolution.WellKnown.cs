// #370: the FIXED BCL members ilemit expands a Kotlin operation into.
//
// `enumValues()` becomes `Enum.GetValues`, string `+` becomes `String.Concat`, a runtime-classifier read becomes
// `Object.GetType`. The source wrote none of them —
// but "did the source write it" is not the question. The question is whether ilemit encodes an EXTERNAL member as a
// CIL operand, and it does, in every one of these.
//
// None of them varies: no type arguments, no overload chosen per call site, the same member every time. So they do
// not need a carrier per node — one table per document, keyed by role, resolved here like everything else. The
// expansion stays in the emitter, which is a question about which layer owns the SHAPE; the member it emits arrives
// named, which is the question this issue is about. The two are separable, and separating them is what lets this
// land without waiting on the intrinsic-binding programme.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Toolchain;

static partial class ClrMemberResolution
{
    // role -> (owner, member, parameter types). A role names what the emitter needs it FOR, so a reader of the
    // emitter can find the entry without knowing the BCL signature by heart.
    static readonly (string Role, string Owner, string Name, string[] Params)[] WellKnownMembers =
    {
        ("String.ConcatArray",    "System.String",   "Concat",            new[] { "System.Object[]" }),
        ("Type.FromHandle",       "System.Type",     "GetTypeFromHandle", new[] { "System.RuntimeTypeHandle" }),
        ("Object.GetType",        "System.Object",   "GetType",           new string[0]),
        ("Object.ToString",       "System.Object",   "ToString",          new string[0]),
        ("Object.GetHashCode",    "System.Object",   "GetHashCode",       new string[0]),
        ("Object.Equals",         "System.Object",   "Equals",            new[] { "System.Object" }),
        ("Enum.GetValues",        "System.Enum",     "GetValues",         new[] { "System.Type" }),
        ("Enum.Parse",            "System.Enum",     "Parse",             new[] { "System.Type", "System.String" }),
        ("Array.IndexOf",         "System.Array",    "IndexOf",           new[] { "System.Array", "System.Object" }),
    };

    // Constructors with a FIXED owner. `newobj` needs a token exactly as `call` does, so these are the same
    // question — the split that matters is whether the owner varies per site, not whether the member has a name.
    static readonly (string Role, string Owner, string[] Params)[] WellKnownCtors =
    {
        ("Object.ctor",                  "System.Object",                  new string[0]),
        ("IndexOutOfRangeException.ctor","System.IndexOutOfRangeException", new[] { "System.String" }),
        // The OPEN `Nullable<T>..ctor(T)`. A coercion computes the constructed owner from the slot it is filling,
        // which no document states — but the declaration does not vary, and anchoring is mechanical.
        ("NullableT.ctor",               "System.Nullable`1",              new[] { "!0" }),
        ("SpanT.ctorPointer",            "System.Span`1",                  new[] { "System.Void*", "System.Int32" }),
    };

    /// <summary>Stamp the fixed-member table on a document root. Every entry resolves or the build stops.</summary>
    public static void ResolveWellKnown(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        if (root is not JsonObject document) return;
        var table = new JsonObject();
        foreach (var (role, owner, name, parameters) in WellKnownMembers)
        {
            var open = ResolveOwnerType(new TypeNode.Fqn(owner))
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}', which does not resolve "
                    + "to a .NET type");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == wanted.Count
                    && !m.IsGenericMethodDefinition).ToList();
            // A role that cannot be resolved is a producer defect. Skipping it quietly hands the emitter a
            // table with a hole in it, and the hole only shows up as a missing member at emit time — one layer
            // past the one that could say what went wrong.
            var win = TryPickUnique(cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}.{name}({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}'");
            table[role] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, OwnParameters(open));
        }
        foreach (var (role, owner, parameters) in WellKnownCtors)
        {
            // A name may state its arity — `System.Nullable`1` is a different type from the static `System.Nullable`
            // beside it, and the bare name resolves to the wrong one.
            var open = OwnerOfSpec(owner)
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}'");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var ownerArgs = OwnParameters(open);
            var cands = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => !c.IsStatic && c.GetParameters().Length == wanted.Count).ToList();
            var win = TryPickUniqueCtor(cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}..ctor({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}'");
            table[role] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open, ownerArgs);
        }
        document["wellKnownRefs"] = table;
    }

    // An OPEN generic declaration is named over its own parameters — `IEnumerator`1<!0>` — because that is what
    // the declaration IS. The emitter anchors it onto the owner it is building; the reference states the
    // declaration, exactly as it does everywhere else.
    static TypeNode[] OwnParameters(Type open) =>
        open.IsGenericTypeDefinition
            ? open.GetGenericArguments().Select(p => (TypeNode)new TypeNode.Tv("type", p.GenericParameterPosition)).ToArray()
            : Array.Empty<TypeNode>();

    // `Name`N` -> the arity-N definition; a bare name -> whatever that name alone resolves to.
    static Type OwnerOfSpec(string spec)
    {
        var tick = spec.IndexOf('`');
        return tick < 0
            ? ResolveOwnerType(new TypeNode.Fqn(spec))
            : RefDef(spec[..tick], int.Parse(spec[(tick + 1)..]));
    }

    static TypeNode ParseWellKnownParam(string spec) =>
        spec.StartsWith("!", StringComparison.Ordinal) ? new TypeNode.Tv("type", int.Parse(spec[1..])) :
        spec.EndsWith("[]", StringComparison.Ordinal)
            ? new TypeNode.Array(new TypeNode.Fqn(spec[..^2]))
            : new TypeNode.Fqn(spec);
}

static partial class ClrMemberResolution
{
    internal static bool TryResolveAliasedInterfaceSlot(ReferenceMetadataIndex refs,
        TypeNode.Fqn physicalOwner, string member, int methodArity, TypeNode[] wantedParams,
        JsonArray wantedTypeParams, TypeNode[] wantedOwnerArgs,
        out TypeNode.Fqn declarationOwner, out string declarationMember,
        out TypeNode[] declarationParams, out TypeNode declarationReturn)
    {
        declarationOwner = null;
        declarationMember = null;
        declarationParams = null;
        declarationReturn = null;
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var open = ResolveOwnerType(physicalOwner) ?? refs.PhysicalTypeNamed(physicalOwner.Name);
        if (open == null || !open.IsInterface) return false;
        var ownerArgs = physicalOwner.Args ?? Array.Empty<TypeNode>();
        var declarationOwners = new[] { open }.Concat(open.GetInterfaces())
            .Select(owner => owner.IsGenericType && !owner.IsGenericTypeDefinition
                ? owner.GetGenericTypeDefinition() : owner)
            .GroupBy(owner => (owner.Module, owner.MetadataToken)).Select(group => group.First());
        var candidates = new List<(MethodInfo Method, MemberRefNode Reference,
            TypeNode[] Parameters, TypeNode Return, TypeNode[] DeclaringArgs)>();
        foreach (var method in declarationOwners.SelectMany(owner => owner.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
        {
            if (!IsPublicOrProtected(method) || method.Name != member
                || method.GetGenericArguments().Length != methodArity
                || method.GetParameters().Length != wantedParams.Length) continue;
            var referenceJson = MemberRefJson(method, MemberRefNode.Kinds.Method, open, ownerArgs);
            using var referenceDocument = JsonDocument.Parse(referenceJson.ToJsonString());
            var reference = MemberRefNode.Read(referenceDocument.RootElement);
            var declaringArgs = (reference.DeclaringType as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();
            var parameters = reference.ParameterTypes
                .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, declaringArgs)).ToArray();
            if (!parameters.SequenceEqual(wantedParams, InterfaceSlotTypeComparer.Instance)) continue;
            if (wantedTypeParams != null)
            {
                var declaredTypeParams = new JsonArray(method.GetGenericArguments()
                    .Select(ReferenceMetadataIndex.GenericParamDeclaration).ToArray());
                if (!KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                        declaredTypeParams, wantedTypeParams, declaringArgs,
                        wantedOwnerArgs ?? Array.Empty<TypeNode>()))
                    continue;
            }
            candidates.Add((method, reference, parameters,
                SupertypeGraph.SubstOwnerTvs(reference.ReturnType, declaringArgs), declaringArgs));
        }
        var distinct = MostDerived(candidates
            .GroupBy(candidate => (candidate.Method.Module, candidate.Method.MetadataToken))
            .Select(group => group.First().Method).ToList());
        if (distinct.Count > 1)
            throw new InvalidOperationException(
                $"bir2cir: aliased interface slot '{physicalOwner.Name}.{member}`{methodArity}' resolves to "
                + $"{distinct.Count} declarations with the requested parameter vector");
        if (distinct.Count == 0) return false;
        var selectedMethod = distinct[0];
        var selected = candidates.Single(candidate => candidate.Method.Module == selectedMethod.Module
            && candidate.Method.MetadataToken == selectedMethod.MetadataToken);
        declarationOwner = selected.Reference.DeclaringType as TypeNode.Fqn;
        if (declarationOwner == null) return false;
        declarationMember = selected.Reference.Name;
        declarationParams = selected.Parameters;
        declarationReturn = selected.Return;
        return true;
    }

    /// <summary>
    /// The exact external declaration operands of this type's resolved interface MethodImpl table.
    /// </summary>
    /// <remarks>
    /// Exact name/signature implementations need no MethodImpl and bind under ordinary CLR rules. Every non-implicit
    /// obligation already names its body and declaration shape in `clrInterfaceImpls`; resolve precisely those rows
    /// to scalar member references here. ilemit consumes the resulting owner-scoped table and never enumerates an
    /// interface or searches the implementing type's overload set.
    /// </remarks>
    public static void ResolveInterfaceSlots(JsonNode root, IEnumerable<JsonNode> moduleRoots,
        ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var all = moduleRoots.Where(moduleRoot => moduleRoot != null).ToList();
        var defs = SupertypeGraph.Collect(all);
        WalkTypesForSlots(root, defs);
    }

    /// <summary>
    /// Name an external declaration reached through a type emitted by this compilation.
    /// </summary>
    /// <remarks>
    /// A call can quite correctly state a local receiver owner while its selected declaration lives on an
    /// inherited external interface (for example, a local interface extending one with a default method).  The
    /// early referenced-call pass must skip that owner because an older shipped copy of a local type is never an
    /// authority.  Once all physically-lowered local declarations are available together, the shared supertype
    /// graph can prove whether a local declaration answers the call and, only when none does, expose the external
    /// declaration.  This keeps the receiver local while carrying the actual declarer to ilemit.
    /// </remarks>
    public static void ResolveInheritedExternalCalls(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var all = roots.Where(root => root != null).ToList();
        var defs = SupertypeGraph.Collect(all);
        foreach (var root in all) WalkInheritedExternalCalls(root, defs, refs);
    }

    static void WalkInheritedExternalCalls(JsonNode node,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                    if (kv.Value != null) WalkInheritedExternalCalls(kv.Value, defs, refs);
                ResolveInheritedExternalCall(obj, defs, refs);
                break;
            case JsonArray arr:
                foreach (var item in arr.ToList())
                    if (item != null) WalkInheritedExternalCalls(item, defs, refs);
                break;
        }
    }

    static void ResolveInheritedExternalCall(JsonObject call,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        if ((call["k"] as JsonValue)?.GetValue<string>() != "callInstance"
            || call.ContainsKey("memberRef")
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner
            || !defs.TryGetValue(owner.Name, out var ownerDef)
            || (call["method"] as JsonValue)?.GetValue<string>() is not { } name
            || call["sig"] is not JsonArray sig)
            return;

        var callSig = sig.Select(TypeJson.Read).ToArray();
        if (callSig.Any(type => type == null)) return;
        var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var ownerArgs = owner.Args ?? (ownerDef.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, ownerDef.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        // Reachable() starts in a definition's own type-parameter frame.  Calls, however, can name a constructed
        // local owner (`Local<string>`), so project its first edges into the call's frame before walking onward.
        var projectedOwner = new SupertypeGraph.Def
        {
            Name = ownerDef.Name,
            Kind = ownerDef.Kind,
            Arity = ownerDef.Arity,
            Base = ownerDef.Base == null ? null
                : SupertypeGraph.SubstOwnerTvs(ownerDef.Base, ownerArgs) as TypeNode.Fqn,
            Interfaces = ownerDef.Interfaces.Select(iface =>
                (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(iface, ownerArgs)).ToArray(),
            Node = ownerDef.Node,
            Methods = ownerDef.Methods,
        };
        var reachable = SupertypeGraph.Reachable(projectedOwner, defs, refs).ToList();

        // A declaration in this compilation owns the identity.  Never let a reflected base with the same shape
        // displace it merely because the call's local owner does not declare the method itself.
        if (LocalDeclares(ownerDef, ownerArgs, name, methodArity, callSig)
            || reachable.Any(entry => defs.TryGetValue(entry.spec.Name, out var local)
                && LocalDeclares(local, entry.spec.Args, name, methodArity, callSig)))
            return;

        var answers = new List<(MethodInfo Declaration, JsonNode Reference, TypeNode[] OwnerArgs)>();
        foreach (var (spec, _) in reachable)
        {
            if (defs.ContainsKey(spec.Name)) continue;
            // Resolve against this exact constructed graph edge.  The reference index's ordinary call lookup is
            // receiver-oriented and may legitimately answer from an implemented interface; here the graph has
            // already identified each declaration owner, so declared-only reflection avoids counting it twice.
            var open = ResolveOwnerType(spec) ?? refs.PhysicalTypeNamed(spec.Name);
            if (open == null) continue;
            var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name
                    && method.GetGenericArguments().Length == methodArity
                    && method.GetParameters().Length == callSig.Length
                    && IsPublicOrProtected(method))
                .ToList();
            var declaration = TryPickUnique(candidates, callSig.ToList(), spec.Args ?? Array.Empty<TypeNode>());
            if (declaration == null) continue;
            var reference = MemberRefJson(declaration, MemberRefNode.Kinds.Method, open,
                spec.Args ?? Array.Empty<TypeNode>());
            answers.Add((declaration, reference, spec.Args ?? Array.Empty<TypeNode>()));
        }
        // A derived interface can redeclare a base slot with the same source signature.  That is not ambiguity:
        // ordinary member lookup selects the most-derived declaration.  Apply the same existing metadata rule here,
        // before comparing the complete scalar references; unrelated declarations remain an unresolved ambiguity.
        var mostDerived = MostDerived(answers.Select(answer => answer.Declaration)
            .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList());
        if (mostDerived.Count == 1)
        {
            var selected = mostDerived[0];
            answers = answers.Where(answer => answer.Declaration.Module == selected.Module
                && answer.Declaration.MetadataToken == selected.MetadataToken).ToList();
        }
        var distinctAnswers = answers.Select(answer => answer.Reference)
            .GroupBy(reference => reference.ToJsonString(), StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        // Multiple distinct external declarations are a semantic dispatch question, not an emitter choice.  The
        // upstream declaration identity must distinguish that case; leave it unstamped so the fail-closed audit
        // reports the unresolved site instead of picking by graph order.
        if (distinctAnswers.Count != 1) return;
        call["memberRef"] = distinctAnswers[0];
        var chosen = answers[0];
        StampResolvedMethodTypeParameters(call, chosen.Declaration);
        StampDelegateArgumentTargets(call, chosen.Declaration, chosen.OwnerArgs);
    }

    static bool LocalDeclares(SupertypeGraph.Def def, TypeNode[] ownerArgs, string name, int methodArity,
        IReadOnlyList<TypeNode> callSig)
    {
        var args = ownerArgs ?? (def.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        foreach (var method in def.Methods.OfType<JsonObject>())
        {
            if ((method["static"] as JsonValue)?.TryGetValue<bool>(out var isStatic) == true && isStatic) continue;
            if ((method["name"] as JsonValue)?.GetValue<string>() != name
                || ((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity
                || method["params"] is not JsonArray parameters
                || parameters.Count != callSig.Count)
                continue;
            var declared = parameters.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"]))
                .ToArray();
            if (declared.Length != callSig.Count || declared.Any(type => type == null)) continue;
            var wanted = callSig.Select(SupertypeGraph.TypeKey);
            if (declared.Select(SupertypeGraph.TypeKey).SequenceEqual(wanted)
                || declared.Select(type => SupertypeGraph.SubstOwnerTvs(type, args))
                    .Select(SupertypeGraph.TypeKey).SequenceEqual(callSig.Select(SupertypeGraph.TypeKey)))
                return true;
        }
        return false;
    }

    static void WalkTypesForSlots(JsonNode node,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["name"] is JsonValue)
                    StampInterfaceSlots(obj, defs);
                foreach (var kv in obj) if (kv.Value != null) WalkTypesForSlots(kv.Value, defs);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) WalkTypesForSlots(it, defs);
                break;
        }
    }

    static void StampInterfaceSlots(JsonObject type,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs)
    {
        if (type.ContainsKey("interfaceSlotRefs")) return;
        if (type["methods"] is not JsonArray methods) return;

        var descriptors = new List<(TypeNode.Fqn Owner, JsonObject Descriptor)>();
        foreach (var method in methods.OfType<JsonObject>())
            if (method["clrInterfaceImpls"] is JsonArray implementations)
                foreach (var implementation in implementations.OfType<JsonObject>())
                    if (TypeJson.Read(implementation["owner"]) is TypeNode.Fqn owner)
                        descriptors.Add((owner, implementation));
        if (descriptors.Count == 0) return;

        var slotSets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var ownerGroup in descriptors.GroupBy(entry => SupertypeGraph.TypeKey(entry.Owner),
                     StringComparer.Ordinal))
        {
            var iface = ownerGroup.First().Owner;
            if (defs.TryGetValue(iface.Name, out var localIface))
            {
                // A generated canonical interface template can be local while its physical identity is supplied by
                // the runtime assembly. Every other local owner is linked directly from the emitted MethodDef table.
                var generated = (localIface.Node?["generated"] as JsonValue)?.TryGetValue<bool>(out var value) == true
                    && value;
                if (!generated || !ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(iface.Name)
                    || _refs.PhysicalTypeNamed(iface.Name, localIface.Arity) == null)
                {
                    ResolveLocalInterfaceDescriptors(iface, localIface, ownerGroup);
                    continue;
                }
            }
            var open = ResolveOwnerType(iface) ?? _refs.PhysicalTypeNamed(iface.Name);
            if (open == null)
                throw new InvalidOperationException(
                    $"bir2cir: interface MethodImpl owner '{iface.Name}' does not resolve in the reference set");
            var args = iface.Args ?? Array.Empty<TypeNode>();
            foreach (var (_, descriptor) in ownerGroup)
            {
                if ((descriptor["member"] as JsonValue)?.GetValue<string>() is not { } member
                    || (descriptor["arity"] as JsonValue)?.TryGetValue<int>(out var arity) != true
                    || descriptor["params"] is not JsonArray parameters
                    || TypeJson.Read(descriptor["ret"]) is not TypeNode wantedRet)
                    throw new InvalidOperationException(
                        $"bir2cir: malformed interface MethodImpl descriptor on '{iface.Name}'");
                var wantedParams = parameters.Select(TypeJson.Read).ToArray();
                if (wantedParams.Any(parameter => parameter == null))
                    throw new InvalidOperationException(
                        $"bir2cir: interface MethodImpl '{iface.Name}.{member}' has an unreadable parameter type");
                var declarationOwners = new[] { open }.Concat(open.GetInterfaces())
                    .Select(owner => owner.IsGenericType && !owner.IsGenericTypeDefinition
                        ? owner.GetGenericTypeDefinition() : owner)
                    .GroupBy(owner => (owner.Module, owner.MetadataToken)).Select(group => group.First());
                var allCandidates = declarationOwners.SelectMany(owner => owner.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Where(method => IsPublicOrProtected(method)
                        && method.GetGenericArguments().Length == arity
                        && method.GetParameters().Length == wantedParams.Length)
                    .Select(method =>
                    {
                        var referenceJson = MemberRefJson(method, MemberRefNode.Kinds.Method, open, args);
                        using var referenceDocument = JsonDocument.Parse(referenceJson.ToJsonString());
                        var reference = MemberRefNode.Read(referenceDocument.RootElement);
                        var declaringArgs = (reference.DeclaringType as TypeNode.Fqn)?.Args
                            ?? Array.Empty<TypeNode>();
                        var parameters = reference.ParameterTypes
                            .Select(typeNode => SupertypeGraph.SubstOwnerTvs(typeNode, declaringArgs)).ToArray();
                        var ret = SupertypeGraph.SubstOwnerTvs(reference.ReturnType, declaringArgs);
                        return (Method: method, ReferenceJson: referenceJson, Reference: reference,
                            DeclaringArgs: declaringArgs, Parameters: parameters, Return: ret);
                    }).ToList();
                var namedCandidates = allCandidates.Where(candidate => candidate.Method.Name == member).ToList();
                bool SignatureMatches((MethodInfo Method, JsonNode ReferenceJson, MemberRefNode Reference,
                    TypeNode[] DeclaringArgs, TypeNode[] Parameters, TypeNode Return) candidate) =>
                    candidate.Parameters.SequenceEqual(wantedParams, InterfaceSlotTypeComparer.Instance)
                    && SameInterfaceSlotType(candidate.Return, wantedRet);
                var candidates = namedCandidates.Where(SignatureMatches).ToList();
                if (descriptor["typeParams"] is JsonArray wantedTypeParams)
                    candidates = candidates.Where(candidate =>
                    {
                        var declaredTypeParams = new JsonArray(candidate.Method.GetGenericArguments()
                            .Select(ReferenceMetadataIndex.GenericParamDeclaration).ToArray());
                        return KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                            declaredTypeParams, wantedTypeParams,
                            candidate.DeclaringArgs, Array.Empty<TypeNode>());
                    }).ToList();
                var mostDerived = MostDerived(candidates.Select(candidate => candidate.Method)
                    .GroupBy(method => (method.Module, method.MetadataToken))
                    .Select(group => group.First()).ToList());
                if (mostDerived.Count == 1)
                {
                    var selectedDeclaration = mostDerived[0];
                    candidates = candidates.Where(candidate =>
                        candidate.Method.Module == selectedDeclaration.Module
                        && candidate.Method.MetadataToken == selectedDeclaration.MetadataToken).ToList();
                }
                var declarations = candidates
                    .GroupBy(candidate => (candidate.Method.Module, candidate.Method.MetadataToken))
                    .Select(group => group.First()).ToList();
                if (declarations.Count != 1)
                {
                    var implementingType = (type["name"] as JsonValue)?.GetValue<string>() ?? "?";
                    var implementingMethod = methods.OfType<JsonObject>().FirstOrDefault(method =>
                        method["clrInterfaceImpls"] is JsonArray impls
                        && impls.OfType<JsonObject>().Any(impl => ReferenceEquals(impl, descriptor)));
                    var implementingMember = (implementingMethod?["name"] as JsonValue)?.GetValue<string>() ?? "?";
                    var wantedSignature = $"({string.Join(", ", wantedParams.Select(parameter =>
                            TypeJson.Write(parameter).ToJsonString()))}) -> "
                        + TypeJson.Write(wantedRet).ToJsonString();
                    var candidateSignatures = namedCandidates.Count == 0 ? "<none>" : string.Join("; ",
                        namedCandidates.Select(candidate =>
                            $"{candidate.Reference.DeclaringType}.({string.Join(", ", candidate.Parameters.Select(
                                typeNode => TypeJson.Write(typeNode).ToJsonString()))}) -> "
                            + TypeJson.Write(candidate.Return).ToJsonString()));
                    throw new InvalidOperationException(
                        $"bir2cir: interface MethodImpl '{iface.Name}.{member}`{arity}' resolves to "
                        + $"{declarations.Count} external declaration(s), expected exactly one; wanted "
                        + $"{wantedSignature}; named candidates: {candidateSignatures}; descriptor: "
                        + descriptor.ToJsonString() + $"; body: {implementingType}.{implementingMember}");
                }
                var selected = declarations[0];
                var reference = selected.ReferenceJson;
                if (selected.Reference.DeclaringType is not TypeNode.Fqn declarationOwner)
                    throw new InvalidOperationException(
                        $"bir2cir: interface MethodImpl '{iface.Name}.{member}`{arity}' resolved without a declaring type");
                descriptor["owner"] = TypeJson.Write(declarationOwner);
                descriptor["member"] = selected.Reference.Name;
                descriptor["params"] = new JsonArray(selected.Parameters
                    .Select(TypeJson.Write).ToArray());
                descriptor["ret"] = TypeJson.Write(selected.Return);
                var assembly = selected.Reference.Assembly;
                if (string.IsNullOrEmpty(assembly))
                    throw new InvalidOperationException(
                        $"bir2cir: external interface '{declarationOwner.Name}' has no assembly identity");
                var setKey = assembly + "::" + SupertypeGraph.TypeKey(declarationOwner);
                if (!slotSets.TryGetValue(setKey, out var slotSet))
                {
                    slotSet = new JsonObject
                    {
                        ["owner"] = TypeJson.Write(declarationOwner),
                        ["assembly"] = assembly,
                        ["slots"] = new JsonArray(),
                    };
                    slotSets.Add(setKey, slotSet);
                }
                var slots = (JsonArray)slotSet["slots"];
                if (!slots.Any(slot => slot?.ToJsonString() == reference.ToJsonString()))
                    slots.Add(reference);
            }
        }
        DeduplicateResolvedInterfaceDescriptors(methods);
        if (slotSets.Count > 0) type["interfaceSlotRefs"] = new JsonArray(slotSets.Values.ToArray());
    }

    // Several semantic edges can collapse onto one physical declaration (for example Collection.size and its
    // inherited alias both name IReadOnlyCollection<T>.get_Count). Once every row has been normalized to the exact
    // declaration signature above, retain one instruction on that body. Deduplicating earlier would compare mixed
    // Kotlin/CLR vocabularies (`int` versus `System.Int32`) and either miss the duplicate or guess equivalence.
    static void DeduplicateResolvedInterfaceDescriptors(JsonArray methods)
    {
        foreach (var method in methods.OfType<JsonObject>())
        {
            if (method["clrInterfaceImpls"] is not JsonArray descriptors) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < descriptors.Count;)
            {
                var key = descriptors[i]?.ToJsonString();
                if (key == null || seen.Add(key))
                {
                    i++;
                    continue;
                }
                descriptors.RemoveAt(i);
            }
        }
    }

    static void ResolveLocalInterfaceDescriptors(TypeNode.Fqn iface, SupertypeGraph.Def definition,
        IEnumerable<(TypeNode.Fqn Owner, JsonObject Descriptor)> descriptors)
    {
        var args = SupertypeGraph.EffectiveArgs(iface, definition.Arity);
        if (args == null)
            throw new InvalidOperationException(
                $"bir2cir: local interface MethodImpl owner '{SupertypeGraph.TypeKey(iface)}' has invalid arity");
        foreach (var (_, descriptor) in descriptors)
        {
            if ((descriptor["member"] as JsonValue)?.GetValue<string>() is not { } member
                || (descriptor["arity"] as JsonValue)?.TryGetValue<int>(out var arity) != true
                || descriptor["params"] is not JsonArray parameters
                || TypeJson.Read(descriptor["ret"]) is not TypeNode wantedRet)
                throw new InvalidOperationException(
                    $"bir2cir: malformed local interface MethodImpl descriptor on '{iface.Name}'");
            var wantedParams = parameters.Select(TypeJson.Read).ToArray();
            if (wantedParams.Any(parameter => parameter == null))
                throw new InvalidOperationException(
                    $"bir2cir: local interface MethodImpl '{iface.Name}.{member}' has an unreadable parameter type");
            var candidates = definition.Methods.OfType<JsonObject>().Where(method =>
            {
                if ((method["static"] as JsonValue)?.TryGetValue<bool>(out var isStatic) == true && isStatic
                    || KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                    || (method["name"] as JsonValue)?.GetValue<string>() is not { } physicalName
                    || ((method["typeParams"] as JsonArray)?.Count ?? 0) != arity
                    || method["params"] is not JsonArray declaredParameters
                    || declaredParameters.Count != wantedParams.Length
                    || TypeJson.Read(method["ret"]) is not TypeNode declaredRet)
                    return false;
                // All semantic property/explicit-name allocation is complete before this late table. Matching a
                // source name here would reconstruct meaning from a physical declaration, so require the final name.
                if (physicalName != member) return false;
                var declaredParams = declaredParameters.OfType<JsonObject>()
                    .Select(parameter => TypeJson.Read(parameter["type"]))
                    .ToArray();
                if (declaredParams.Length != wantedParams.Length || declaredParams.Any(parameter => parameter == null)
                    || !declaredParams.Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, args))
                        .SequenceEqual(wantedParams, InterfaceSlotTypeComparer.Instance)
                    || !SameInterfaceSlotType(SupertypeGraph.SubstOwnerTvs(declaredRet, args), wantedRet))
                    return false;
                return descriptor["typeParams"] is not JsonArray wantedTypeParams
                    || KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                        method["typeParams"] as JsonArray, wantedTypeParams, args, Array.Empty<TypeNode>());
            }).ToList();
            if (candidates.Count != 1)
            {
                var available = string.Join("; ", definition.Methods.OfType<JsonObject>().Select(method =>
                {
                    KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var accessorKind);
                    var name = (method["name"] as JsonValue)?.GetValue<string>() ?? "?";
                    var source = (method[DeclarationRename.SourceMemberKey] as JsonValue)?.GetValue<string>();
                    var declaredParams = method["params"] is JsonArray ps
                        ? string.Join(", ", ps.OfType<JsonObject>().Select(parameter =>
                            TypeJson.Write(TypeJson.Read(parameter["type"])).ToJsonString()))
                        : "?";
                    var declaredRet = TypeJson.Read(method["ret"]);
                    return $"{name} [source={source ?? "-"}, property={propertyName ?? "-"}:{accessorKind ?? "-"}]"
                        + $" ({declaredParams}) -> {(declaredRet == null ? "?" : TypeJson.Write(declaredRet).ToJsonString())}";
                }));
                throw new InvalidOperationException(
                    $"bir2cir: local interface MethodImpl '{iface.Name}.{member}`{arity}' resolves to "
                    + $"{candidates.Count} declaration(s), expected exactly one; candidates: {available}");
            }
            var selected = candidates[0];
            descriptor["member"] = selected["name"]?.DeepClone();
            descriptor["params"] = new JsonArray(((JsonArray)selected["params"]).OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"]))
                .Select(parameter => TypeJson.Write(SupertypeGraph.SubstOwnerTvs(parameter, args)))
                .ToArray());
            descriptor["ret"] = TypeJson.Write(SupertypeGraph.SubstOwnerTvs(
                TypeJson.Read(selected["ret"]), args));
        }
    }

    static bool SameInterfaceSlotType(TypeNode metadata, TypeNode descriptor) =>
        CanonicalInterfaceSlotType(metadata).Equals(CanonicalInterfaceSlotType(descriptor));

    static TypeNode CanonicalInterfaceSlotType(TypeNode type) =>
        CanonicalInterfaceSlotTypeCore(BirTypeLowering.CanonicalPhysicalSlotType(type));

    static TypeNode CanonicalInterfaceSlotTypeCore(TypeNode type) => type switch
    {
        // A constructed TypeNode already carries arity in Args. Reflection preserves the metadata backtick suffix;
        // CIR-authored descriptors use the semantic bare owner. They are one CLI TypeSpec, not two identities.
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(
            ReferenceMetadataIndex.BareOwnerFqn(f.Name),
            args.Select(CanonicalInterfaceSlotTypeCore).ToArray()),
        TypeNode.Array array => new TypeNode.Array(
            CanonicalInterfaceSlotTypeCore(array.Elem), array.Rank, array.SzArray),
        TypeNode.Nullable nullable => new TypeNode.Nullable(CanonicalInterfaceSlotTypeCore(nullable.Of)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(CanonicalInterfaceSlotTypeCore(oblivious.Of)),
        TypeNode.ByRef byRef => new TypeNode.ByRef(CanonicalInterfaceSlotTypeCore(byRef.Of)),
        TypeNode.Ptr pointer => new TypeNode.Ptr(CanonicalInterfaceSlotTypeCore(pointer.Of)),
        TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
            CanonicalInterfaceSlotTypeCore(modifier.M), CanonicalInterfaceSlotTypeCore(modifier.Of)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend,
            CanonicalInterfaceSlotTypeCore(function.Ret),
            function.Params.Select(CanonicalInterfaceSlotTypeCore).ToArray(),
            function.Recv == null ? null : CanonicalInterfaceSlotTypeCore(function.Recv),
            function.Clr, function.Ctx?.Select(CanonicalInterfaceSlotTypeCore).ToArray()),
        _ => type,
    };

    sealed class InterfaceSlotTypeComparer : IEqualityComparer<TypeNode>
    {
        public static readonly InterfaceSlotTypeComparer Instance = new();
        public bool Equals(TypeNode left, TypeNode right) =>
            left != null && right != null && SameInterfaceSlotType(left, right);
        public int GetHashCode(TypeNode type) =>
            CanonicalInterfaceSlotType(type).GetHashCode();
    }
}
