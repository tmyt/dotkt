using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Variance -> invariance type-argument REALIGNMENT for invariant @ClrTypeAlias collection generics.
//
// kotc's frontend approximates a use-site `in`/`out` variance projection to `kotlin.Any` (harmless on the JVM = erased).
// e.g. `val name: String by data` (data: Map<String, Any?>): the property-delegate getValue chain calls
// `getOrImplicitDefault<K,V>(this)` on a receiver the frontend views as `Map<in String, V>` — so it infers
// `K = kotlin.Any` for the call while the ACTUAL receiver is `Map<String, V>`. On the CLR `IDictionary<,>` is INVARIANT:
// an `IDictionary<string,V>` argument cannot flow into an `IDictionary<object,V>` param -> EntryPointNotFound. The fix:
// for each type param the callee places inside an INVARIANT constructed collection generic, realign the CALL's `typeArg`
// to the corresponding type-argument of the ACTUAL argument's declared type, overriding the `kotlin.Any` approximation.
//
// The selected declaration supplies the formal parameter vector. Constructed arguments are matched by
// source parameter and representation role: Map and MutableMap can have different companion layouts.
static class MapVarianceRealign
{
    // The invariant BCL collection generics (their @ClrTypeAlias Kotlin FQNs). Type params here do NOT lift via CLR
    // variance — unlike List/Collection/Iterable (`IReadOnly*<out T>` covariant), which stay untouched.
    public static readonly HashSet<string> InvariantCollections = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Map", "kotlin.collections.MutableMap",
        "kotlin.collections.HashMap", "kotlin.collections.LinkedHashMap",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.collections.HashSet", "kotlin.collections.LinkedHashSet",
    };

    // The frontend-selected declaration owns this vector. A physical generic
    // arity is not an overload identity and does not state companion roles.
    public static Dictionary<string, TypeNode[]> CollectCalleeTypeParams(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, map);
        return map;
    }

    static void CollectFrom(JsonNode root, Dictionary<string, TypeNode[]> map)
    {
        if (root is not JsonObject o) return;
        CollectMethods(o["methods"], map);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) CollectFrom(t, map);
    }

    static void CollectMethods(JsonNode methods, Dictionary<string, TypeNode[]> map)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            if (Str(mo[DeclarationIdentityBinding.Key]) is not string id) continue;
            if (mo["typeParams"] is not JsonArray tps || tps.Count == 0) continue;
            var paramTypes = (mo["params"] as JsonArray ?? new JsonArray())
                .Select(p => (p as JsonObject) is JsonObject po ? TypeJson.Read(po["type"]) : null).ToArray();
            if (map.TryGetValue(id, out var previous) && !previous.SequenceEqual(paramTypes))
                throw new InvalidOperationException("Collection realignment has conflicting selected declaration signatures");
            map[id] = paramTypes;
        }
    }

    public static void Apply(JsonNode root, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams, ReferenceMetadataIndex refs)
    {
        var typeFrames = refs == null ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
            : new Dictionary<string, NullableRepresentationFrame>(refs.NullableTypeFrames, StringComparer.Ordinal);
        foreach (var definition in SupertypeGraph.Collect(new[] { root }).Values)
        {
            var frame = KotlinSupertypesRecord.ReadNullableFrame(definition.Node);
            if (frame == null) typeFrames.Remove(definition.Name);
            else typeFrames[definition.Name] = frame;
        }
        Apply(root, calleeTypeParams, refs, typeFrames);
    }

    static void Apply(JsonNode root, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        if (root is not JsonObject o) return;
        var ownerFrame = Str(o[KotlinSupertypesRecord.PreKey]) is string facts
            && JsonNode.Parse(facts)?[NullableRepresentationFrame.MetadataKey] is JsonNode frame
            ? NullableRepresentationFrame.Read(frame) : null;
        ProcessMethods(o["methods"], calleeTypeParams, refs, ownerFrame, typeFrames);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) Apply(t, calleeTypeParams, refs, typeFrames);
    }

    static void ProcessMethods(JsonNode methods, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams,
        ReferenceMetadataIndex refs, NullableRepresentationFrame ownerFrame,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            var methodFrame = Str(mo[NullableRepresentationTypes.MethodFrameKey]) is string encoded
                ? NullableRepresentationFrame.Read(JsonNode.Parse(encoded)) : null;
            TypeNode NullableArgument(TypeNode argument)
            {
                if (argument is TypeNode.Tv variable)
                {
                    var callerFrame = variable.Scope == "type" ? ownerFrame : methodFrame;
                    if (callerFrame == null)
                        throw new System.InvalidOperationException("Collection factory requires a missing caller nullable frame");
                    var semantic = callerFrame.SemanticVariable(variable);
                    return semantic is TypeNode.Nullable ? variable : callerFrame.NullableVariable((TypeNode.Tv)semantic);
                }
                return argument is TypeNode.Nullable ? argument : new TypeNode.Nullable(argument);
            }
            // Per-method local type environment: params + local `var` declarations -> its declared structured type.
            var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            if (mo["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        env[pn] = pt;
            // The method's own type-param bounds: `M -> MutableMap<K,…>`. Recovers the PRECISE static type of a receiver
            // whose declared type is a type-param `Tv` (used by OwnerVarianceRealign to undo the `in K`->Any variance
            // approximation). Keyed by the type-param INDEX (matching a receiver Tv.I).
            var constraints = new Dictionary<int, TypeNode>();
            if (mo["typeParams"] is JsonArray tps)
                for (var i = 0; i < tps.Count; i++)
                    if (tps[i] is JsonObject to && to["constraints"] is JsonArray cs)
                        foreach (var c in cs)
                            if (TypeJson.Read(c) is TypeNode ct && InvariantGenericArgs(ct) != null) { constraints[i] = ct; break; }
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mo["body"] is JsonNode body)
            {
                GatherLocals(body, env, aliases);
                // Besides constrained invariant collections, this also keeps a MutableIterable alias on the exact
                // physical source type. The latter has no CLR value-type covariance, and its sole Kotlin operation
                // is routed through an erased receiver by MemberCallSubstitution.
                RealignVarTypes(body, env, aliases, constraints);
                Walk(body, env, calleeTypeParams, aliases, constraints, refs, NullableArgument, typeFrames);
            }
        }
    }

    static void GatherLocals(JsonNode node, Dictionary<string, TypeNode> env, Dictionary<string, string> aliases)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n)
            {
                if (TypeJson.Read(o["type"]) is TypeNode t) env.TryAdd(n, t);
                if (o["init"] is JsonObject io && Str(io["k"]) == "local" && Str(io["name"]) is string src)
                    aliases.TryAdd(n, src);
            }
            foreach (var kv in o)
                if (kv.Value != null) GatherLocals(kv.Value, env, aliases);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) GatherLocals(it, env, aliases);
    }

    // Restore the type an inlined temp had BEFORE the `in`/`out`->kotlin.Any variance approximation erased it. Recover
    // each temp's precise type from its alias-root source (a `Tv` param -> its invariant-collection bound).
    static void RealignVarTypes(JsonNode node, Dictionary<string, TypeNode> env,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode declType
                && aliases.ContainsKey(n))
            {
                var root = ResolveAliasRoot(n, aliases);
                if (env.TryGetValue(root, out var srcType) && RealignedType(declType, srcType, constraints) is TypeNode nt && nt != declType)
                { o["type"] = TypeJson.Write(nt); env[n] = nt; }
            }
            foreach (var kv in o)
                if (kv.Value != null) RealignVarTypes(kv.Value, env, aliases, constraints);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) RealignVarTypes(it, env, aliases, constraints);
    }

    // The precise physical type for a temp declared `declType` but aliased to a source of `srcType`.
    //
    // MutableIterable is covariant in Kotlin and aliases IEnumerable<T> physically. CLR variance only applies when T
    // is a reference type, so storing MutableList<Int> in a local physically declared MutableIterable<Any?> produces
    // unverifiable IL. Retain the accepted source's exact collection face in that alias local; calls keep their Kotlin
    // owner token and MutableIterable.iterator is routed through the variance-independent `Any` dispatcher parameter.
    //
    // When the source is a type-param `Tv`: a bare kotlin.Any/object temp regains that Tv; a temp declared as an
    // invariant collection generic regains the bound's concrete args wherever it holds an over-approximated
    // (kotlin.Any) position.
    static TypeNode RealignedType(TypeNode declType, TypeNode srcType, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (declType is TypeNode.Fqn { Name: "kotlin.collections.MutableIterable", Args.Length: 1 }
            && srcType is TypeNode.Fqn { Args.Length: 1 } source
            && source.Name is "kotlin.collections.MutableIterable" or "kotlin.collections.MutableCollection"
                or "kotlin.collections.MutableSet" or "kotlin.collections.MutableList")
            return srcType;
        if (srcType is not TypeNode.Tv srcTv) return declType;
        if (IsObjectish(declType)) return srcType;
        if (!constraints.TryGetValue(srcTv.I, out var bound)) return declType;
        if (InvariantGenericArgs(declType) is not TypeNode[] declArgs) return declType;
        if (InvariantGenericArgs(bound) is not TypeNode[] boundArgs || boundArgs.Length != declArgs.Length) return declType;
        return RealignArgs((TypeNode.Fqn)declType, declArgs, boundArgs);
    }

    static void Walk(JsonNode node, Dictionary<string, TypeNode> env, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints, ReferenceMetadataIndex refs,
        System.Func<TypeNode, TypeNode> nullableArgument,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "callStatic" || k == "callInstance")
                Realign(o, env, calleeTypeParams, typeFrames);
            if (k == "callInstance")
                OwnerVarianceRealign(o, env, aliases, constraints);
            if (k == "new")
                RealignFactoryCtorArgTypes(o, refs, nullableArgument);
            foreach (var kv in o)
                if (kv.Value != null) Walk(kv.Value, env, calleeTypeParams, aliases, constraints, refs, nullableArgument, typeFrames);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) Walk(it, env, calleeTypeParams, aliases, constraints, refs, nullableArgument, typeFrames);
    }

    // CONSTRUCTION-ARGUMENT covariance realign (il-bymap regression, klib migration #80): a collection-factory
    // LITERAL (`mapOf(...)`/`listOf(...)`/`setOf(...)`) passed directly as a `new` node's argument infers its OWN
    // typeArgs from the literal's element/pair VALUES (Kotlin's lower-bound inference — e.g. `mapOf("k1" to
    // "Alice", "k2" to 30)` with String/Int values infers `V = Comparable<Any>`, the tightest common supertype of
    // the two value literals), which can be NARROWER than the constructor's declared parameter type when the
    // target slot's Kotlin type is wider (`User(data: Map<String, Any?>)`). Kotlin accepts this with NO cast —
    // `Map`/`List`/`Set` are declaration-site covariant (`out V`) — but MemberCallSubstitution's `TryFactorySubst`
    // builds the literal's runtime `Dictionary<K,V>`/`List<E>`/`HashSet<E>` straight off those narrower typeArgs,
    // and the CLR's generic collection instantiations are INVARIANT: passing a `Dictionary<string,IComparable>`
    // where `IDictionary<string,object>` is expected is unverifiable (ilverify StackUnexpected — the ctor argument
    // slot never reconciles the two). Realign the factory call's `typeArgs` to the constructor's use-site
    // `argTypes` slot HERE, before MemberCallSubstitution builds the literal, so it is constructed at the WIDE
    // type Kotlin already type-checked the assignment against — the same "realign to the actually-intended type"
    // move as `Realign`/`OwnerVarianceRealign` above, just sourced from the ENCLOSING slot instead of a callee
    // constraint. BIR-space (Kotlin FQNs) — runs before type lowering + MemberCallSubstitution.
    static void RealignFactoryCtorArgTypes(JsonObject newNode, ReferenceMetadataIndex refs,
        System.Func<TypeNode, TypeNode> nullableArgument)
    {
        if (newNode["argTypes"] is not JsonArray useSiteArgTypes) return;
        if (newNode["args"] is not JsonArray args) return;
        // `new.argTypes` is already substituted into the caller's frame. The open constructor declaration
        // lives in memberSignature; substituting argTypes through the constructed owner a second time confuses
        // an enclosing class's !0 with the constructor owner's !0 (Box<List<T>> would build List<List<T>>).
        var n = Math.Min(useSiteArgTypes.Count, args.Count);
        for (var i = 0; i < n; i++)
        {
            if (args[i] is not JsonObject call || Str(call["k"]) != "callStatic") continue;
            string kind;
            if (Str(call[DeclarationIdentityBinding.Key]) is string declarationId)
            {
                // A selected declaration owns its factory annotation; the physical method name
                // need not be the Kotlin source name recorded in the name-based factory index.
                if (!refs.TryDeclarationFactory(declarationId, out kind, out _, out _)) continue;
            }
            else
                kind = Str(call["method"]) is string fn ? refs.CollectionFactoryKind(fn) : null;
            if (kind == null) continue;
            if (call["typeArgs"] is not JsonArray callTypeArgs || callTypeArgs.Count == 0) continue;
            if (TypeJson.Read(useSiteArgTypes[i]) is not TypeNode target) continue;
            if (UnwrapNullableOblivious(target) is not TypeNode.Fqn { Args: { } targetArgs }) continue;
            var expected = kind == "map" ? 2 : 1;                       // map -> [K,V]; list/set -> [E]
            var factoryFrame = Str(call[DeclarationIdentityBinding.Key]) is string id ? refs.NullableMethodFrame(id) : null;
            if (targetArgs.Length != expected || (factoryFrame?.SourceArity ?? callTypeArgs.Count) != expected) continue;
            TypeNode[] closedArgs;
            try
            {
                closedArgs = factoryFrame == null ? targetArgs
                    : factoryFrame.Close(targetArgs, argument => argument, nullableArgument);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Collection factory {call["method"]} frame closure failed: {ex.Message}", ex);
            }
            call["typeArgs"] = new JsonArray(closedArgs.Select(TypeJson.Write).ToArray());
        }
    }

    // Strip BOTH nullability wrappers (`nullable`/`oblivious`) off a declared slot type before matching it against
    // the factory call's own Fqn — a `Map<String,Any?>?` param is a `Nullable(Fqn(Map,[...]))` at this layer.
    static TypeNode UnwrapNullableOblivious(TypeNode t) => t switch
    {
        TypeNode.Nullable n => UnwrapNullableOblivious(n.Of),
        TypeNode.Oblivious ob => UnwrapNullableOblivious(ob.Of),
        _ => t,
    };

    // Undo the use-site `in`/`out` variance over-approximation baked into an INLINED Map member call: recover the
    // receiver's PRECISE static type from its type-param bound (`M : MutableMap<K,…>`) and realign each over-approximated
    // (kotlin.Any) ownerType position to the constraint's concrete arg.
    static void OwnerVarianceRealign(JsonObject call, Dictionary<string, TypeNode> env,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (TypeJson.Read(call["ownerType"]) is not TypeNode ownerType) return;
        if (InvariantGenericArgs(ownerType) is not TypeNode[] ownerArgs || !ownerArgs.Any(IsObjectish)) return;
        if (call["recv"] is not JsonObject recv || Str(recv["k"]) != "local" || Str(recv["name"]) is not string rn) return;
        var root = ResolveAliasRoot(rn, aliases);
        if (!env.TryGetValue(root, out var rootType) || rootType is not TypeNode.Tv rootTv) return;
        if (!constraints.TryGetValue(rootTv.I, out var bound)) return;
        if (InvariantGenericArgs(bound) is not TypeNode[] boundArgs || boundArgs.Length != ownerArgs.Length) return;
        var realigned = RealignArgs((TypeNode.Fqn)ownerType, ownerArgs, boundArgs);
        if (realigned != ownerType) call["ownerType"] = TypeJson.Write(realigned);
    }

    // Replace every over-approximated (kotlin.Any/object) arg with the corresponding `boundArgs` arg (when it is more
    // specific), returning the reconstructed Fqn; the original when nothing changed.
    static TypeNode RealignArgs(TypeNode.Fqn owner, TypeNode[] args, TypeNode[] boundArgs)
    {
        var realigned = new TypeNode[args.Length];
        var changed = false;
        for (var i = 0; i < args.Length; i++)
            if (IsObjectish(args[i]) && !IsObjectish(boundArgs[i])) { realigned[i] = boundArgs[i]; changed = true; }
            else realigned[i] = args[i];
        return changed ? new TypeNode.Fqn(owner.Name, realigned) : owner;
    }

    static string ResolveAliasRoot(string name, IReadOnlyDictionary<string, string> aliases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(name, out var src) && seen.Add(name)) name = src;
        return name;
    }

    static void Realign(JsonObject call, Dictionary<string, TypeNode> env, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        if (call["typeArgs"] is not JsonArray typeArgs || typeArgs.Count == 0) return;
        if (Str(call[DeclarationIdentityBinding.Key]) is not string id) return;
        if (call["args"] is not JsonArray args) return;
        if (!calleeTypeParams.TryGetValue(id, out var paramTypes)) return;

        var count = Math.Min(paramTypes.Length, args.Count);
        Dictionary<int, TypeNode> subst = null;
        for (var i = 0; i < count; i++)
        {
            if (paramTypes[i] is not TypeNode pt || InvariantGenericArgs(pt) is not TypeNode[] sigGps) continue;
            if (ActualArgType(args[i], env) is not TypeNode actual || InvariantGenericArgs(actual) is not TypeNode[] actualGps) continue;
            var formal = ArgumentRoles((TypeNode.Fqn)pt, sigGps, typeFrames);
            var concrete = ArgumentRoles((TypeNode.Fqn)actual, actualGps, typeFrames);
            foreach (var (role, parameter) in formal)
                if (parameter is TypeNode.Tv { Scope: "method" } tv
                    && concrete.TryGetValue(role, out var argument) && argument is not TypeNode.Tv
                    && tv.I >= 0 && tv.I < typeArgs.Count)
                    (subst ??= new Dictionary<int, TypeNode>())[tv.I] = argument;
        }
        if (subst == null) return;
        foreach (var (idx, concrete) in subst)
            if (!(TypeJson.Read(typeArgs[idx]) is TypeNode cur && cur == concrete))   // already aligned -> no-op
                typeArgs[idx] = TypeJson.Write(concrete);
    }

    static Dictionary<NullableRepresentationFrame.Slot, TypeNode> ArgumentRoles(TypeNode.Fqn owner, TypeNode[] arguments,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames)
    {
        frames.TryGetValue(owner.Name, out var frame);
        if (frame != null && frame.PhysicalArity != arguments.Length)
            throw new InvalidOperationException("Collection arguments disagree with their declaration-owned frame");
        return arguments.Select((argument, index) => (argument,
                slot: frame?.PhysicalSlot(index)
                    ?? new NullableRepresentationFrame.Slot(index, NullableRepresentationFrame.Role.Ordinary)))
            .ToDictionary(pair => pair.slot, pair => pair.argument);
    }

    // The generic type-argument tokens of a type whose head is an INVARIANT collection generic, e.g.
    // `kotlin.collections.Map<K,V>` -> [K,V]. Null for a non-collection / non-generic type.
    static TypeNode[] InvariantGenericArgs(TypeNode t) =>
        t is TypeNode.Fqn { Args: { } args } f && InvariantCollections.Contains(f.Name) ? args : null;

    // The declared structured type of a call argument node: a local/param reference resolved through the method's type
    // env, else the node's own `type`/`retType`. Null when not statically recoverable here.
    static TypeNode ActualArgType(JsonNode arg, Dictionary<string, TypeNode> env)
    {
        if (arg is not JsonObject o) return null;
        if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && env.TryGetValue(nm, out var t)) return t;
        return TypeJson.Read(o["type"]) ?? TypeJson.Read(o["ret"]);
    }

    // The `in`/`out` use-site variance over-approximation is `kotlin.Any` — but a projected key/value is genuinely
    // NULLABLE (`Map<in K, V>` -> the key projects to `Any?`), so post-#37/#48 kotc emits the marker as the wrapped
    // `{t:nullable,of:kotlin.Any}` rather than a bare `kotlin.Any` (pre-#48 the `?` was a retired scalar flag, leaving a
    // bare Fqn here). See through the nullability wrapper so the realignment still recognizes the approximation and
    // restores the concrete constraint arg — without this, a `MutableMap<Any?, MutableList<T>>` inlined receiver in
    // groupByTo left `clrMapPut`/`set_Item` dispatched on `IDictionary<object,…>` (value-type-invariance EntryPointNotFound).
    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null } f => f.Name is "kotlin.Any" or "object",
        _ => false,
    };

    internal static void SelfTest()
    {
        TypeNode M(int index) => new TypeNode.Tv("method", index);
        var text = new TypeNode.Fqn("kotlin.String", null);
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any", null));
        var mapFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 0, 1 });
        var mutableFrame = new NullableRepresentationFrame(2, new[] { 1 }, storageIndices: new[] { 0, 1 });
        var frames = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
        {
            ["kotlin.collections.Map"] = mapFrame,
            ["kotlin.collections.MutableMap"] = mutableFrame,
        };
        JsonObject Declaration(string id, TypeNode parameter) => new()
        {
            ["name"] = "sameName", [DeclarationIdentityBinding.Key] = id,
            ["typeParams"] = new JsonArray(new JsonObject(), new JsonObject(), new JsonObject(), new JsonObject()),
            ["params"] = new JsonArray(new JsonObject { ["name"] = "map", ["type"] = TypeJson.Write(parameter) }),
        };
        var formals = new TypeNode[] { M(0), M(1), M(2), M(3) };
        var selected = new TypeNode.Fqn("kotlin.collections.Map", formals);
        var unrelated = new TypeNode.Fqn("kotlin.collections.Map", new TypeNode[] { M(3), M(2), M(1), M(0) });
        var declarations = CollectCalleeTypeParams(new[] { new JsonObject
        {
            ["methods"] = new JsonArray(Declaration("unrelated", unrelated), Declaration("selected", selected)),
        } });
        JsonObject Call() => new()
        {
            ["k"] = "callStatic", ["method"] = "sameName", [DeclarationIdentityBinding.Key] = "selected",
            ["typeArgs"] = new JsonArray(TypeJson.Write(any), TypeJson.Write(M(0)), TypeJson.Write(any), TypeJson.Write(M(2))),
            ["args"] = new JsonArray(new JsonObject { ["k"] = "local", ["name"] = "map" }),
        };
        var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal)
        {
            ["map"] = new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { text, M(0), M(1), text, M(2) }),
        };
        void AssertCall(JsonObject call)
        {
            var actual = call["typeArgs"].AsArray().Select(TypeJson.Read).ToArray();
            if (!actual.SequenceEqual(new TypeNode[] { text, M(0), text, M(2) }))
                throw new InvalidOperationException("Collection realignment confused source or companion roles");
        }
        var call = Call();
        Realign(call, env, declarations, frames);
        AssertCall(call);

        // A nested owner may place companions before ordinary arguments. Role identity, not
        // matching vector offsets, still identifies the key and value representations.
        frames["kotlin.collections.MutableMap"] = new NullableRepresentationFrame(2, new[] { 1 },
            new[] { 4, 0, 2, 3, 1 }, new[] { 0, 1 });
        env["map"] = new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { M(2), text, M(1), text, M(0) });
        call = Call();
        Realign(call, env, declarations, frames);
        AssertCall(call);

        // An owner's !0 is not the call's !!0. It cannot bind a method argument.
        declarations["selected"] = new TypeNode[] { new TypeNode.Fqn("kotlin.collections.Map",
            new TypeNode[] { new TypeNode.Tv("type", 0), M(1), M(2), M(3) }) };
        call = Call();
        Realign(call, env, declarations, frames);
        if (TypeJson.Read(call["typeArgs"][0]) != any)
            throw new InvalidOperationException("Collection realignment rebound an owner variable as a method variable");
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
