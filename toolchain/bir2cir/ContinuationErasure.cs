using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The cold coroutine protocol uses monomorphic Result/Continuation value surfaces where the earlier
// existential projection has not already supplied its carrier. CLR variance cannot relate value-type
// instantiations, so ordinary values, construction sites and inherited protocol edges share that ABI.
// An exact lexical receiver is different: its own member operand must retain the actual declaring frame,
// not name G<object> while loading this of G<T>. Source signatures remain in the existing metadata channel.
// Runs in ref/runtime/app before BirTypeLowering; ilemit receives only the resulting physical contracts.
static partial class ContinuationErasure
{
    const string Cont = "kotlin.coroutines.Continuation";
    const string ResultFqn = "kotlin.Result";
    static readonly string[] MethodImplDescriptorKeys = { "clrInterfaceImpls", "clrBaseImpls" };
    static IReadOnlySet<string> _continuationTypeNames = new HashSet<string>(StringComparer.Ordinal) { Cont };
    static IReadOnlySet<string> _resultTypeNames = new HashSet<string>(StringComparer.Ordinal) { ResultFqn };
    static IReadOnlyDictionary<string, string> _physicalTypeNames = new Dictionary<string, string>();

    // Supertype edges are source declaration facts too. Freeze them before nullable-frame allocation and
    // other representation passes; recording only member slots leaves Continuation<T>'s inherited contract
    // paired with a physical Continuation<object> edge when a DLL is imported again.
    public static BirFileDeclaration[] PreserveSourceSupertypes(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        SetTypeNames(refs.PhysicalTypeNames);
        return roots.Select(root => (BirFileDeclaration)root.Rewrite(CaptureSupertypes)).ToArray();
    }

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Apply(root, refs.PhysicalTypeNames);

    static void Apply(JsonNode root, IReadOnlyDictionary<string, string> physicalTypeNames)
    {
        SetTypeNames(physicalTypeNames);
        RecordDeclarationSurfaces(root);
        Walk(root, inResumeWith: false);
    }

    static void SetTypeNames(IReadOnlyDictionary<string, string> physicalTypeNames)
    {
        _physicalTypeNames = physicalTypeNames;
        var continuationNames = new HashSet<string>(StringComparer.Ordinal) { Cont };
        if (physicalTypeNames.TryGetValue(Cont, out var physicalContinuation))
            continuationNames.Add(physicalContinuation);
        _continuationTypeNames = continuationNames;
        var resultNames = new HashSet<string>(StringComparer.Ordinal) { ResultFqn };
        if (physicalTypeNames.TryGetValue(ResultFqn, out var physicalResult))
            resultNames.Add(physicalResult);
        _resultTypeNames = resultNames;
    }

    static BirGraphValue CaptureSupertypes(BirGraphValue node)
    {
        bool Moves(TypeNode edge) => edge != null && !EraseType(edge).Equals(edge);
        OptionalField<SourceTypeFacts> Capture(OptionalField<TypeNode> baseType, OptionalField<TypeNode[]> interfaces,
            OptionalField<BirTypeParameter[]> parameters, OptionalField<SourceTypeFacts> previous)
        {
            var original = previous.OrDefault() ?? SourceTypeFacts.Empty;
            var bounds = parameters.OrEmpty().Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Constraints.OrEmpty().Any(Moves))
                .ToDictionary(entry => entry.index, entry => entry.parameter.Constraints.Value);
            var merged = original.Merge(Moves(baseType.OrDefault()) ? baseType.Value : null,
                interfaces.OrEmpty().Where(Moves).ToArray(), bounds);
            return ReferenceEquals(original, merged) ? previous : merged;
        }
        return node switch {
            BirClassDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            BirInterfaceDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            BirEnumDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            // An inherited implementation descriptor also owns its declared generic bounds.
            BirInheritedImplementation owner => owner with {
                KotlinSupertypesPre = Capture(default, default, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            _ => node,
        };
    }

    internal static void SelfTest()
    {
        FunctionRewriteTests.Check("continuation erasure", EraseType);
        LexicalOwnerSelfTest();
        SourceOwnerSelfTest();
        var names = new Dictionary<string, string>
        {
            [Cont] = "kotlin.coroutines.Continuation`1",
            [ResultFqn] = "kotlin.Result`1",
        };
        var integer = new TypeNode.Fqn("kotlin.Int");
        var any = new TypeNode.Fqn("kotlin.Any");
        foreach (var sourceName in new[] { Cont, ResultFqn })
        foreach (var typeName in new[] { sourceName, names[sourceName] })
        {
            TypeNode Nested(TypeNode argument) => new TypeNode.Fqn("probe.Container",
                new TypeNode[] { new TypeNode.Fqn(typeName, new TypeNode[] { argument }) });
            var original = Nested(integer);
            var expected = Nested(any);
            var root = new JsonObject
            {
                ["kind"] = "class",
                ["name"] = Cont,
                ["base"] = TypeJson.Write(original),
                ["interfaces"] = new JsonArray(TypeJson.Write(original)),
                ["typeParams"] = new JsonArray(new JsonObject
                {
                    ["name"] = "T",
                    ["constraints"] = new JsonArray(TypeJson.Write(original), TypeJson.Fqn("probe.Unchanged")),
                }),
                ["overrides"] = new JsonArray(new JsonObject { ["owner"] = TypeJson.Write(original) }),
                ["call"] = new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(Cont) },
            };
            foreach (var key in MethodImplDescriptorKeys)
                root[key] = new JsonArray(new JsonObject
                {
                    ["owner"] = TypeJson.Write(original),
                    ["params"] = new JsonArray(TypeJson.Write(original)),
                    ["ret"] = TypeJson.Write(original),
                });
            SetTypeNames(names);
            var typed = new BirClassDefinition { Name = Cont, Kind = "class", Base = original,
                Interfaces = new[] { original }, TypeParams = new[] { new BirTypeParameter {
                    Name = "T", Constraints = new TypeNode[] { original, new TypeNode.Fqn("probe.Unchanged") },
                } } };
            var captured = (BirClassDefinition)CaptureSupertypes(typed);
            root[KotlinSupertypesRecord.PreKey] = SourceTypeFacts.ToDom(captured.KotlinSupertypesPre.Value);
            var source = JsonNode.Parse(root[KotlinSupertypesRecord.PreKey].GetValue<string>());
            if (!JsonNode.DeepEquals(source["base"], TypeJson.Write(original))
                || !JsonNode.DeepEquals(source["interfaces"][0], TypeJson.Write(original))
                || !JsonNode.DeepEquals(source["bounds"]["0"], root["typeParams"][0]["constraints"]))
                throw new InvalidOperationException($"ContinuationErasure self-test: {typeName} source edges/bounds");
            var snapshot = source.ToJsonString();
            Apply(root, names);
            var competing = Nested(new TypeNode.Tv("method", 3));
            var later = (BirClassDefinition)CaptureSupertypes(captured with {
                Base = competing, Interfaces = new[] { competing }, TypeParams = new[] { new BirTypeParameter {
                    Name = "T", Constraints = new TypeNode[] { competing, new TypeNode.Fqn("probe.Unchanged") },
                } },
            });
            if (SourceTypeFacts.ToDom(later.KotlinSupertypesPre.Value).GetValue<string>() != snapshot
                || typed.KotlinSupertypesPre.IsPresent)
                throw new InvalidOperationException($"ContinuationErasure self-test: {typeName} typed source ownership");
            if (root[KotlinSupertypesRecord.PreKey].GetValue<string>() != snapshot)
                throw new InvalidOperationException($"ContinuationErasure self-test: {typeName} earlier source precedence");
            void Equal(JsonNode actual, TypeNode wanted, string role)
            {
                if (!JsonNode.DeepEquals(actual, TypeJson.Write(wanted)))
                    throw new InvalidOperationException($"ContinuationErasure self-test: {typeName} {role}");
            }
            Equal(root["interfaces"][0], expected, "implemented interface");
            Equal(root["overrides"][0]["owner"], original, "semantic override owner");
            Equal(root["call"]["owner"], new TypeNode.Fqn(Cont), "static declaration container");
            foreach (var key in MethodImplDescriptorKeys)
            {
                var descriptor = root[key][0];
                Equal(descriptor["owner"], expected, key + " owner");
                Equal(descriptor["params"][0], expected, key + " parameter");
                Equal(descriptor["ret"], expected, key + " return");
            }
        }
    }

    static void SourceOwnerSelfTest()
    {
        SetTypeNames(new Dictionary<string, string>());
        var source = new TypeNode.Fqn(Cont, new TypeNode[] { new TypeNode.Tv("type", 2) });
        var sibling = new TypeNode.Fqn("probe.Sibling");
        var bounds = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { source, sibling } } };
        var descriptor = new BirInheritedImplementation { Kind = "function", TypeParams = bounds };
        var method = new BirFunctionDeclaration { Name = "member", TypeParams = bounds,
            InheritedImplementation = descriptor, Body = new IBirStatement[] { new BirExpressionStatement {
                Expr = new BirNewSam { SynthClass = new BirClassDefinition {
                    Kind = "class", Name = "probe.Synthetic", Base = source,
                    InheritedDefaultAccessors = new[] { new BirInheritedDefaultAccessor { Implementation = descriptor } },
                    InheritedDefaultMethods = new[] { new BirInheritedDefaultMethod { Implementation = descriptor } },
                } },
            } } };
        var input = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Kind = "class", Name = "probe.Class", Base = source,
                Interfaces = new TypeNode[] { source, sibling }, TypeParams = bounds },
            new BirInterfaceDefinition { Kind = "interface", Name = "probe.Interface",
                Interfaces = new TypeNode[] { source }, TypeParams = bounds },
            new BirEnumDefinition { Kind = "enum", Name = "probe.Enum", Base = source, TypeParams = bounds },
        } };
        var result = (BirFileDeclaration)input.Rewrite(CaptureSupertypes);
        var classOwner = (BirClassDefinition)result.Types.Value[0];
        var interfaceOwner = (BirInterfaceDefinition)result.Types.Value[1];
        var enumOwner = (BirEnumDefinition)result.Types.Value[2];
        var inherited = result.Methods.Value[0].InheritedImplementation.Value;
        var synthetic = ((BirNewSam)((BirExpressionStatement)result.Methods.Value[0].Body.Value[0]).Expr.Value).SynthClass.Value;
        foreach (var facts in new[] { classOwner.KotlinSupertypesPre.Value, interfaceOwner.KotlinSupertypesPre.Value,
            enumOwner.KotlinSupertypesPre.Value, inherited.KotlinSupertypesPre.Value,
            synthetic.InheritedDefaultAccessors.Value[0].Implementation.Value.KotlinSupertypesPre.Value,
            synthetic.InheritedDefaultMethods.Value[0].Implementation.Value.KotlinSupertypesPre.Value })
            if (facts.Bounds[0].Length != 2 || facts.Bounds[0][0] != source || facts.Bounds[0][1] != sibling)
                throw new InvalidOperationException("Continuation source bounds lost a generic frame or unchanged sibling");
        if (classOwner.KotlinSupertypesPre.Value.Base != source
            || classOwner.KotlinSupertypesPre.Value.Interfaces.Single() != source
            || interfaceOwner.KotlinSupertypesPre.Value.Interfaces.Single() != source
            || enumOwner.KotlinSupertypesPre.Value.Base != source
            || inherited.KotlinSupertypesPre.Value.Base != null
            || synthetic.KotlinSupertypesPre.Value.Base != source
            || ((BirClassDefinition)input.Types.Value[0]).KotlinSupertypesPre.IsPresent
            || descriptor.KotlinSupertypesPre.IsPresent
            || classOwner.Base.Value != source || classOwner.Interfaces.Value[1] != sibling)
            throw new InvalidOperationException("Continuation source capture changed declaration ownership or physical types");
        var existing = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { sibling } });
        var recorded = (BirClassDefinition)CaptureSupertypes(classOwner with { KotlinSupertypesPre = existing });
        if (recorded.KotlinSupertypesPre.Value.Bounds[0].Single() != sibling)
            throw new InvalidOperationException("Continuation source capture replaced earlier source bounds");
        var unchanged = (BirClassDefinition)CaptureSupertypes(new BirClassDefinition {
            Kind = "class", Name = "probe.Plain", Base = sibling,
        });
        if (unchanged.KotlinSupertypesPre.IsPresent)
            throw new InvalidOperationException("Continuation source capture invented an absent source fact");
    }

    // The CLR coroutine ABI is deliberately monomorphic, but a referenced DLL must still re-expose the source-level
    // Kotlin signature. Preserve every declaration position changed by this pass as a pure TypeNode fact. Later,
    // RoundtripMetadata turns the fact into [KotlinType]; dll2klib restores it before FIR sees the declaration.
    static void RecordDeclarationSurfaces(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                // Method declaration (call/expression nodes carry `k`).
                if (obj["k"] == null && obj["name"] is JsonValue && obj["params"] is JsonArray ps)
                {
                    foreach (var p in ps.OfType<JsonObject>())
                        RecordSlot(p, "type", "kotlinType");
                    RecordSlot(obj, "ret", "retKotlinType");
                }
                // Fields and properties use the same `type` slot. Recording an unchanged slot is avoided by RecordSlot.
                if (obj["k"] == null && obj["type"] != null)
                    RecordSlot(obj, "type", "kotlinType");
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RecordDeclarationSurfaces(child);
                break;
            }
            case JsonArray arr:
                foreach (var child in arr.Where(v => v != null).ToList())
                    RecordDeclarationSurfaces(child);
                break;
        }
    }

    static void RecordSlot(JsonObject owner, string slot, string fact)
    {
        // An earlier representation pass may already have moved this declaration onto a CLR slot and captured its
        // source type in the nullable-generic channel. In that case `owner[slot]` is physical input to this pass, not
        // Kotlin meaning. Recording it again as [KotlinType] would mask the earlier carrier in dll2klib (which
        // correctly prefers the exact KotlinType carrier) and publish a hybrid signature. The existing source carrier
        // remains authoritative; this pass only changes the physical type below.
        if (HasCapturedSourceSurface(owner, fact)) return;
        if (TypeJson.Read(owner[slot]) is not TypeNode original) return;
        var erased = ExactOuter(owner) && slot == "type" ? original : EraseType(original);
        if (TypeNode.ToJson(original) != TypeNode.ToJson(erased))
            owner[fact] = TypeNode.ToJson(original);
    }

    static bool HasCapturedSourceSurface(JsonObject owner, string fact) =>
        owner[fact] != null || (fact == "retKotlinType"
            ? owner["nullableGenericRet"] != null
            : owner["nullableGeneric"] != null);

    static bool ExactOuter(JsonObject node) => node[FBoundStarProjectionErasure.ExactOuterKey]?.GetValue<bool>() == true;

    static TypeNode.Fqn Head(TypeNode type) => type switch
    {
        TypeNode.Fqn owner => owner,
        TypeNode.Nullable nullable => Head(nullable.Of),
        TypeNode.Oblivious oblivious => Head(oblivious.Of),
        _ => null,
    };

    static bool SameDeclaration(string left, string right) => left == right
        || right != null && _physicalTypeNames.TryGetValue(right, out var physicalRight) && left == physicalRight
        || left != null && _physicalTypeNames.TryGetValue(left, out var physicalLeft) && right == physicalLeft;

    static bool IsExactReceiverSlot(JsonObject node, string key, string lexicalOwner)
    {
        var kind = (node["k"] as JsonValue)?.GetValue<string>();
        if (key == "ownerType" && node["recv"] is JsonObject receiver)
        {
            var selected = Head(TypeJson.Read(node[key]));
            if (selected != null && (receiver["k"] as JsonValue)?.GetValue<string>() == "this"
                && SameDeclaration(selected.Name, lexicalOwner)) return true;
            // An enclosing receiver's own declaration, not an inherited/interface edge on that receiver.
            if (selected != null && ExactOuter(receiver) && Head(NodeType.Of(receiver)) is { } enclosing
                && SameDeclaration(selected.Name, enclosing.Name)
                && (selected.Args ?? Array.Empty<TypeNode>()).SequenceEqual(enclosing.Args ?? Array.Empty<TypeNode>())) return true;
        }
        return key is "type" or "sty" or "memberType" or "ret" or "ownerType"
            && (ExactOuter(node) || kind == "this" && Head(TypeJson.Read(node[key])) is { } self
                && SameDeclaration(self.Name, lexicalOwner));
    }

    static void Walk(JsonNode node, bool inResumeWith, string lexicalOwner = null)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj["k"] == null && obj["kind"] is JsonValue kind
                    && kind.GetValue<string>() is "class" or "interface" or "struct" or "enum" or "delegate")
                    lexicalOwner = (obj["name"] as JsonValue)?.GetValue<string>();
                var here = inResumeWith || IsResumeWithMethod(obj);
                var exactKeys = obj.Select(pair => pair.Key)
                    .Where(key => IsExactReceiverSlot(obj, key, lexicalOwner)).ToHashSet(StringComparer.Ordinal);
                // Inside the erased resumeWith boundary, the `result` local is now Result<object> (invariant
                // reference class). A generic Result-accessor whose EXTENSION RECEIVER (first arg) is that erased
                // `result` — e.g. `getOrThrow<Unit>(result)` from the source `result.getOrThrow()` — is instantiated
                // at the SOURCE element type (Unit), so it expects a Result<Unit> receiver and mismatches the
                // Result<object> we pass (invalid IL / InvalidProgramException). Re-instantiate its receiver-dependent
                // T as object so `getOrThrow<object>(result: Result<object>)` type-checks. The Unit prologue value is
                // discarded (exprStmt), so no unbox/cast at the use site is needed.
                if (here) EraseResultReceiverTypeArgs(obj);
                // Result is monomorphic Result<object> globally — every `Result.success/failure<X>` construction
                // must yield Result<object>, so its type-arg is erased at EVERY call site (not just resumeWith args).
                EraseResultFactoryTypeArgs(obj);
                // MethodImpl owners are constructed physical types, unlike semantic override edges or a
                // static call's declaration container. Their instantiation must match the implemented supertype.
                foreach (var descriptorsKey in MethodImplDescriptorKeys)
                    if (obj[descriptorsKey] is JsonArray descriptors)
                        foreach (var descriptor in descriptors.OfType<JsonObject>())
                            if (TypeJson.Read(descriptor["owner"]) is TypeNode owner)
                                descriptor["owner"] = TypeJson.Write(EraseType(owner));
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var val = obj[key];
                    if (val == null) continue;
                    // `name` is a declaration identity (the Continuation INTERFACE's own FQN) and `owner` is a
                    // callStatic's method container (a file-class, not a generic instantiation) — a bare Continuation
                    // there must NOT gain a [kotlin.Any] arg. Every actual type-reference slot (type/ownerType/ret/base/
                    // interfaces/funcType/typeArgs/…) is rewritten.
                    if (key == "name" || key == "owner") continue;
                    // A call's `sig` is a STRUCTURED TypeNode array (#37 m3b), so its `Continuation<T>`/`Result<T>`
                    // elements erase to `Continuation<object>`/`Result<object>` for free via the array-recursion below
                    // (EraseType) — DEF and CALL sigs stay in agreement structurally, no sig-string special case needed.
                    if (TypeJson.Read(val) is TypeNode tn)
                        obj[key] = TypeJson.Write(exactKeys.Contains(key) ? tn : EraseType(tn));
                    else
                        Walk(val, here, lexicalOwner);
                }
                break;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var val = arr[i];
                    if (val == null) continue;
                    if (TypeJson.Read(val) is TypeNode tn)
                        arr[i] = TypeJson.Write(EraseType(tn));
                    else
                        Walk(val, inResumeWith, lexicalOwner);
                }
                break;
            }
        }
    }

    // A method DECLARATION named resumeWith (no `k` — a call carries `k`). The whole subtree is the
    // resume protocol operating on the (now Result<object>) result, so every Result token erases.
    static bool IsResumeWithMethod(JsonObject obj) =>
        obj["k"] == null &&
        (obj["name"] as JsonValue)?.TryGetValue<string>(out var n) == true && n == "resumeWith";

    // A generic call (getOrThrow/getOrNull/exceptionOrNull/…) whose extension receiver — the first `args` entry —
    // is the erased `result` local. Its type argument parametrizes the receiver's Result<T>, so with the receiver
    // now Result<object> the call must be instantiated at object. Rewrites every `typeArgs` element to kotlin.Any.
    static void EraseResultReceiverTypeArgs(JsonObject obj)
    {
        var k = (obj["k"] as JsonValue)?.TryGetValue<string>(out var kk) == true ? kk : null;
        if (k != "callStatic" && k != "callInstance") return;
        if (obj["typeArgs"] is not JsonArray ta || ta.Count == 0) return;
        if (obj["args"] is not JsonArray args || args.Count == 0) return;
        if (args[0] is not JsonObject a0
            || (a0["k"] as JsonValue)?.TryGetValue<string>(out var ak) != true || ak != "local"
            || (a0["name"] as JsonValue)?.TryGetValue<string>(out var an) != true || an != "result")
            return;
        var repl = new JsonArray();
        foreach (var _ in ta) repl.Add(TypeJson.Fqn("kotlin.Any"));
        obj["typeArgs"] = repl;

        // A T-returning accessor (getOrThrow/getOrNull/getOrDefault/getOrElse) instantiated at the erased element
        // type now returns `object`, but kotc left its retType at the SOURCE element (`void`/Unit for `Result<Unit>`).
        // ilemit decides whether to POP a discarded exprStmt from this retType hint (Emitter.Statements exprStmt:
        // `if (t != void) Pop`), so a stale `void` leaks the pushed value onto the stack -> ReturnVoid / invalid IL.
        // Promote the hint to kotlin.Any so the discarded getOrThrow is popped. (exceptionOrNull returns Throwable
        // regardless of T, so it is excluded — its retType is already correct.)
        var method = (obj["method"] as JsonValue)?.TryGetValue<string>(out var me) == true ? me : null;
        if (method is "getOrThrow" or "getOrNull" or "getOrDefault" or "getOrElse")
        {
            var rk = obj.ContainsKey("ret") ? "ret" : null;
            // Pre-lowering the hint is still the source `kotlin.Unit` (void folds later) — promote a void/Unit hint to
            // kotlin.Any so ilemit pops the discarded getOrThrow value.
            if (rk != null && TypeJson.Read(obj[rk]) is TypeNode.Fqn { Args: null } rf
                && rf.Name is "void" or "kotlin.Unit")
            {
                obj[rk] = TypeJson.Fqn("kotlin.Any");
                // Spec §2.7: a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`. The frontend
                // stamped this call site at the SOURCE element (`kotlin.Unit` for a `Result<Unit>`), which every
                // deriver reads FIRST — leaving it behind restores the stale `void` hint the promotion above exists
                // to remove. (The #305 chokepoint is what found it.)
                if (obj["sty"] != null) obj["sty"] = TypeJson.Fqn("kotlin.Any");
            }
        }
    }

    // Reset a `kotlin.Result.success/failure<X>` construction's type-arg to kotlin.Any so it yields
    // Result<object> (monomorphic). The value/exception arg keeps its own `sig` (gp:T / System.Exception);
    // only the Result type-parameter is erased. A `new kotlin.Result[X]` is handled by the global type-token
    // erasure (its `type` key). No-op on any other node.
    static void EraseResultFactoryTypeArgs(JsonObject obj)
    {
        var owner = TypeJson.OwnerName(obj["owner"]);
        var method = (obj["method"] as JsonValue)?.TryGetValue<string>(out var me) == true ? me : null;
        if (owner == ResultFqn && (method == "success" || method == "failure") &&
            obj["typeArgs"] is JsonArray ta)
        {
            var repl = new JsonArray();
            foreach (var _ in ta) repl.Add(TypeJson.Fqn("kotlin.Any"));
            obj["typeArgs"] = repl;
        }
    }

    // Erase Continuation + constructed Result to their monomorphic `<kotlin.Any>` form, recursively through a
    // structured Type. `kotlin.coroutines.Continuation[X]` AND a bare `Continuation` (the star-projection) ->
    // Continuation[kotlin.Any]; a CONSTRUCTED `kotlin.Result[X]` -> Result[kotlin.Any] (a BARE Result — a raw-type
    // value — is left untouched). A leading-substring false match (ContinuationImpl / SafeContinuation / ResultKt) is
    // impossible on a structured Fqn (the Name is an exact FQN, not a substring). Nested args/nullable/array/byRef/fn
    // are recursed so a Continuation/Result buried in a generic arg or delegate signature erases too.
    static readonly TypeNode[] AnyArg = { new TypeNode.Fqn("kotlin.Any") };

    static TypeNode EraseType(TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Fqn f:
                if (_continuationTypeNames.Contains(f.Name)) return new TypeNode.Fqn(f.Name, AnyArg);
                // A trusted inline/default payload can already carry the referenced TypeDef's exact metadata name
                // (`kotlin.Result`1`) while source-authored slots still use `kotlin.Result`. Both identities come from
                // the explicit reference index; recognizing the physical twin here avoids splitting one monomorphic
                // Result value between Result<object> and Result<T> locals without reconstructing a name convention.
                if (_resultTypeNames.Contains(f.Name) && f.Args != null)
                    return new TypeNode.Fqn(f.Name, AnyArg);
                return f.Args == null ? f : new TypeNode.Fqn(f.Name, f.Args.Select(EraseType).ToArray());
            case TypeNode.Nullable n: return new TypeNode.Nullable(EraseType(n.Of));
            case TypeNode.Array a: return new TypeNode.Array(EraseType(a.Elem));
            case TypeNode.ByRef b: return new TypeNode.ByRef(EraseType(b.Of));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, EraseType(fn.Ret),
                fn.Params.Select(EraseType).ToArray(), fn.Recv == null ? null : EraseType(fn.Recv), fn.Clr,
                fn.Ctx?.Select(EraseType).ToArray());
            default: return t;
        }
    }
}
