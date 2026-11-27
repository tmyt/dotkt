using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// A foreign CLR G<X> cannot implement a DotKt-synthesized existential after its assembly has already been emitted,
// and G<object> is a different reified invariant type. Keep a foreign G<*> value in an object slot and route only the
// operations that need its erased classifier through the stdlib runtime. bir2cir resolves every member to an exact
// declaring generic definition + exact member identity (metadata token, with a structural declaration key for a
// ref.dll/runtime twin); the runtime maps that declaration onto the receiver's constructed type and therefore
// performs no overload selection from runtime argument values or Kotlin-semantic inference.
static partial class ForeignStarProjectionBinding
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    const string DeclarationSignatureKey = "_foreignDeclarationSignature";

    internal static void DropDeclarationSignatures(BirFileDeclaration root)
    {
        root.Visit(node => {
            switch (node)
            {
                case BirConstrainedCall value: value._foreignDeclarationSignature = default; break;
                case BirGenericInstance value: value._foreignDeclarationSignature = default; break;
                case BirNativeInstance value: value._foreignDeclarationSignature = default; break;
                case BirNativeStatic value: value._foreignDeclarationSignature = default; break;
                case BirPropertyGet value: value._foreignDeclarationSignature = default; break;
                case BirPropertySet value: value._foreignDeclarationSignature = default; break;
            }
        });
    }
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode String = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    static readonly TypeNode TypeN = new TypeNode.Nullable(Type);
    static readonly TypeNode InvocationOutcome = new TypeNode.Fqn(
        "DotKt.Runtime.CompilerServices.StarProjectionInvocationOutcome");
    static HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    static Dictionary<string, TypeNode.Fqn> _closedViewHints = new(StringComparer.Ordinal);
    static Dictionary<string, TypeNode> _dependentLocalTypes = new(StringComparer.Ordinal);
    static IReadOnlyDictionary<string, string> _localExistentialOwners =
        new Dictionary<string, string>(StringComparer.Ordinal);
    static IReadOnlyDictionary<string, string> _localClrAliases =
        new Dictionary<string, string>(StringComparer.Ordinal);
    static int _nextTemp;
    static int _nextDelegateAdapter;
    static bool _bindProjectedHelpers;
    static Func<TypeNode, bool, TypeNode> _projectValueType;
    static Func<TypeNode, TypeNode> _materialMethodArgument;
    public static bool UsedRuntimeFallback { get; private set; }

    internal static void RequireRuntimeFallback() => UsedRuntimeFallback = true;

    // Late binding shares the selected policy but no longer owns source graph
    // capabilities. Replace that reader once at the actual owning frontier;
    // do not replay initial binding or reset its usage/name state.
    internal static void ContinueWithProjection(Func<TypeNode, bool, TypeNode> projectValueType, Func<TypeNode,TypeNode> materialMethodArgument)
        => (_projectValueType, _materialMethodArgument) = (projectValueType, materialMethodArgument);

    static TypeNode.Fn UnwrapFunction(TypeNode type) => type switch
    {
        TypeNode.Fn function => function,
        TypeNode.Nullable n => UnwrapFunction(n.Of),
        TypeNode.Oblivious o => UnwrapFunction(o.Of),
        _ => null,
    };

    static void CollectTypeVariables(TypeNode type, List<TypeNode.Tv> result)
    {
        switch (type)
        {
            case TypeNode.Tv tv:
                if (!result.Contains(tv)) result.Add(tv);
                break;
            case TypeNode.Fqn { Args: { } args }:
                foreach (var argument in args) CollectTypeVariables(argument, result);
                break;
            case TypeNode.Projection p: CollectTypeVariables(p.Of, result); break;
            case TypeNode.Nullable n: CollectTypeVariables(n.Of, result); break;
            case TypeNode.Oblivious o: CollectTypeVariables(o.Of, result); break;
            case TypeNode.Array a: CollectTypeVariables(a.Elem, result); break;
            case TypeNode.ByRef b: CollectTypeVariables(b.Of, result); break;
            case TypeNode.Ptr p: CollectTypeVariables(p.Of, result); break;
            case TypeNode.Mod m: CollectTypeVariables(m.M, result); CollectTypeVariables(m.Of, result); break;
            case TypeNode.Fn function:
                CollectTypeVariables(function.Ret, result);
                foreach (var parameter in function.DelegateParams) CollectTypeVariables(parameter, result);
                if (function.Ctx != null)
                    foreach (var context in function.Ctx) CollectTypeVariables(context, result);
                break;
        }
    }

    static string FreshDelegateAdapterName()
    {
        string name;
        do name = "dotkt$ForeignProjectionDelegateAdapter$" + _nextDelegateAdapter++;
        while (!_reservedNames.Add(name));
        return name;
    }

    static bool TryForeignStarOwner(TypeNode type, ReferenceMetadataIndex refs,
        out TypeNode.Fqn owner, out bool nullable)
    {
        nullable = false;
        while (type is TypeNode.Nullable n) { nullable = true; type = n.Of; }
        while (type is TypeNode.Oblivious o) type = o.Of;
        owner = type as TypeNode.Fqn;
        return owner?.Args is { Length: > 0 } args
            && (args.Any(ContainsExistential) || AliasVarianceRepresentation.RequiresErasure(owner, refs))
            && !refs.TryExistentialPhysicalOwner(owner.Name, out _)
            && refs.ResolveForeignProjectionType(owner.Name, args) != null;
    }

    public static bool IsForeignStarType(TypeNode type, ReferenceMetadataIndex refs) =>
        TryForeignStarOwner(type, refs, out _, out _);

    static bool ContainsByRefLike(TypeNode type, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => refs.IsByRefLikeFqn(f)
            || (f.Args?.Any(a => ContainsByRefLike(a, refs)) ?? false),
        TypeNode.Projection p => ContainsByRefLike(p.Of, refs),
        TypeNode.Nullable n => ContainsByRefLike(n.Of, refs),
        TypeNode.Oblivious o => ContainsByRefLike(o.Of, refs),
        TypeNode.Array a => ContainsByRefLike(a.Elem, refs),
        TypeNode.ByRef b => ContainsByRefLike(b.Of, refs),
        TypeNode.Fn fn => ContainsByRefLike(fn.Ret, refs)
            || fn.Params.Any(p => ContainsByRefLike(p, refs))
            || (fn.Recv != null && ContainsByRefLike(fn.Recv, refs)),
        _ => false,
    };

    internal static JsonObject TypeClassifierCheck(TypeNode.Fqn owner, JsonNode runtimeType,
        ReferenceMetadataIndex refs) => OpenType(owner, refs) is string openType
        ? Call("starProjectionHasView", new[] { Type, Type }, Bool,
            runtimeType.DeepClone(), ClassRef(openType)) : null;

    internal static string OpenClassifierType(TypeNode.Fqn owner, ReferenceMetadataIndex refs) =>
        IsForeignStarType(owner, refs) ? OpenType(owner, refs) : null;

    static string OpenType(TypeNode.Fqn owner, ReferenceMetadataIndex refs)
    {
        var type = refs.ResolveForeignProjectionType(owner.Name, owner.Args);
        if (type == null) return null;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        return type.IsGenericTypeDefinition ? type.FullName : null;
    }

    static JsonObject ClassRef(string openType) => new()
    {
        ["k"] = "classRef", ["type"] = TypeJson.Write(new TypeNode.Fqn(openType)),
    };

    static TypeNode ClosedViewHintType(string receiverLocal, TypeNode.Fqn owner, TypeNode declaringView,
        ReferenceMetadataIndex refs, bool preferExplicit = false)
    {
        // An ordinary variant alias states a readable preference, not a guaranteed physical closure. The runtime
        // uses it only when that exact implemented view exists; otherwise the unique actual closure remains valid.
        TypeNode.Fqn selectedHint = AliasVarianceRepresentation.RequiresErasure(owner, refs)
            && owner.Args?.Any(ContainsExistential) == false ? owner : null;
        var variantValue = AliasVarianceRepresentation.RequiresErasure(owner, refs)
            && AliasVarianceRepresentation.HasSourceVariance(owner, refs);
        if (!variantValue && (!preferExplicit || selectedHint == null)
            && receiverLocal != null && _closedViewHints.TryGetValue(receiverLocal, out var hint)
            && SameForeignProjectionOwner(hint, owner, refs)
            && hint.Args?.Length == owner.Args?.Length)
            selectedHint = hint;
        if (selectedHint is { } view)
        {
            var arguments = refs.NullableTypeFrames.GetValueOrDefault(view.Name)?.OrdinaryArguments(view.Args) ?? view.Args;
            // Translate a binding alias's ordinary source arguments before naming its physical declaring owner.
            // Once that native head exists, its arguments are exact CLR witnesses and cannot recover this role.
            // Native views keep their authored witnesses; alias arguments retain the same array role as inheritance.
            if (AliasVarianceRepresentation.RequiresErasure(view, refs))
                arguments = arguments.Select(type => _projectValueType(type, true)).ToArray();
            // MetadataLoadContext reports a method declared directly on an open generic as the bare definition.
            // In that case the selected preferred closure already is the declaring view; emitting the bare generic as a
            // classRef would be an invalid/absent CLR type token.
            if (declaringView is TypeNode.Fqn { Args: null } direct
                && refs.ResolveForeignProjectionType(view.Name, view.Args) is { } preferredOwner
                && refs.ResolveForeignProjectionType(direct.Name, arguments) is { } declaringOwner
                && (preferredOwner.IsConstructedGenericType ? preferredOwner.GetGenericTypeDefinition() : preferredOwner)
                    == (declaringOwner.IsConstructedGenericType ? declaringOwner.GetGenericTypeDefinition() : declaringOwner))
                return new TypeNode.Fqn(direct.Name, arguments);
            var translated = SubstituteOwnerSlots(declaringView, arguments);
            if (translated is TypeNode.Fqn translatedFqn && !ContainsExistential(translatedFqn))
                return translatedFqn;
        }
        return null;
    }

    static bool SameForeignProjectionOwner(TypeNode.Fqn left, TypeNode.Fqn right,
        ReferenceMetadataIndex refs)
    {
        var leftArity = left.Args?.Length ?? 0;
        var rightArity = right.Args?.Length ?? 0;
        if (leftArity != rightArity) return false;
        var leftType = refs.ResolveForeignProjectionType(left.Name, left.Args);
        var rightType = refs.ResolveForeignProjectionType(right.Name, right.Args);
        if (leftType == null || rightType == null) return left.Name == right.Name;
        if (leftType.IsConstructedGenericType) leftType = leftType.GetGenericTypeDefinition();
        if (rightType.IsConstructedGenericType) rightType = rightType.GetGenericTypeDefinition();
        return leftType == rightType;
    }

    static TypeNode SubstituteOwnerSlots(TypeNode type, IReadOnlyList<TypeNode> arguments) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < arguments.Count => arguments[tv.I],
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => SubstituteOwnerSlots(arg, arguments)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, SubstituteOwnerSlots(p.Of, arguments)),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteOwnerSlots(n.Of, arguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteOwnerSlots(o.Of, arguments)),
        TypeNode.Array a => new TypeNode.Array(SubstituteOwnerSlots(a.Elem, arguments)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteOwnerSlots(b.Of, arguments)),
        _ => type,
    };

    static TypeNode SubstituteProjectedOwnerSlots(TypeNode type, IReadOnlyList<TypeNode> arguments) => type switch
    {
        null => null,
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < arguments.Count => arguments[tv.I] switch
        {
            TypeNode.Star => AnyN,
            TypeNode.Projection projection => ProjectionBound(projection.Of),
            var argument => argument,
        },
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => SubstituteProjectedOwnerSlots(arg, arguments)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance,
            SubstituteProjectedOwnerSlots(p.Of, arguments)),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteProjectedOwnerSlots(n.Of, arguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteProjectedOwnerSlots(o.Of, arguments)),
        TypeNode.Array a => new TypeNode.Array(
            SubstituteProjectedOwnerSlots(a.Elem, arguments), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteProjectedOwnerSlots(b.Of, arguments)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteProjectedOwnerSlots(p.Of, arguments)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req,
            SubstituteProjectedOwnerSlots(m.M, arguments),
            SubstituteProjectedOwnerSlots(m.Of, arguments)),
        TypeNode.Fn function => new TypeNode.Fn(
            function.Suspend,
            SubstituteProjectedOwnerSlots(function.Ret, arguments),
            function.Params?.Select(parameter => SubstituteProjectedOwnerSlots(parameter, arguments)).ToArray(),
            function.Recv == null ? null : SubstituteProjectedOwnerSlots(function.Recv, arguments),
            function.Clr,
            function.Ctx?.Select(context => SubstituteProjectedOwnerSlots(context, arguments)).ToArray()),
        _ => type,
    };

    static TypeNode ProjectionBound(TypeNode type) => type switch
    {
        TypeNode.Projection projection => ProjectionBound(projection.Of),
        TypeNode.Oblivious oblivious => ProjectionBound(oblivious.Of),
        _ => type,
    };

    // A foreign star value remains the original object to preserve Kotlin reference identity. When an immutable local
    // is initialized from a concrete source view, carry that preferred view separately to reflection dispatch;
    // this distinguishes `I<X>` and `I<Y>` implemented by the same CLR object without wrapping the value. Mutable
    // locals are deliberately excluded: a compile-time hint would become stale after an assignment.
    internal static Dictionary<string, TypeNode.Fqn> CollectClosedViewHints(IReadOnlyList<JsonNode> roots,
        ReferenceMetadataIndex refs)
    {
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        void CollectWrites(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "setLocal" && Str(obj["name"]) is string name) assigned.Add(name);
                foreach (var child in obj.Select(p => p.Value).Where(v => v != null)) CollectWrites(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(v => v != null)) CollectWrites(child);
        }
        foreach (var root in roots) CollectWrites(root);

        var pending = new List<ClosedViewVariable>();
        void CollectVars(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "var" && Str(obj["name"]) is string name && !assigned.Contains(name)
                    && obj["init"] is JsonNode init
                    && TryForeignStarOwner(TypeJson.Read(obj["type"]), refs, out var starOwner, out _))
                    pending.Add(new(name, starOwner, NodeType.Of(init),
                        init is JsonObject local && Str(local["k"]) == "local" ? Str(local["name"]) : null));
                foreach (var child in obj.Select(p => p.Value).Where(v => v != null)) CollectVars(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(v => v != null)) CollectVars(child);
        }
        foreach (var root in roots) CollectVars(root);

        return ResolveClosedViewHints(pending, refs);
    }

    internal static Dictionary<string, TypeNode.Fqn> CollectClosedViewHints(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs)
    {
        void Visit(BirGraphValue node, Action<BirGraphValue> inspect)
        {
            if (node is IBirExecutablePayload) return;
            inspect(node);
            node.VisitChildren(child => Visit(child, inspect));
        }
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots) Visit(root, node => {
            if (node is BirSetLocal { Name.HasValue: true } write) assigned.Add(write.Name.Value);
        });
        var pending = new List<ClosedViewVariable>();
        foreach (var root in roots) Visit(root, node => {
            if (node is BirVariable { Name.HasValue: true, Init.HasValue: true } variable
                && !assigned.Contains(variable.Name.Value)
                && TryForeignStarOwner(variable.Type.OrDefault(), refs, out var owner, out _))
                pending.Add(new(variable.Name.Value, owner,
                    BirNodeType.Of(variable.Init.Value as BirGraphNode),
                    variable.Init.Value is BirLocal local ? local.Name.OrDefault() : null));
        });
        return ResolveClosedViewHints(pending, refs);
    }

    sealed record ClosedViewVariable(string Name, TypeNode.Fqn Owner, TypeNode InitialType, string Alias);

    static Dictionary<string, TypeNode.Fqn> ResolveClosedViewHints(IReadOnlyList<ClosedViewVariable> pending,
        ReferenceMetadataIndex refs)
    {
        var result = new Dictionary<string, TypeNode.Fqn>(StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, starOwner, initialType, source) in pending)
            {
                if (result.ContainsKey(name)) continue;
                TypeNode candidate = initialType;
                while (candidate is TypeNode.Nullable nullable) candidate = nullable.Of;
                while (candidate is TypeNode.Oblivious oblivious) candidate = oblivious.Of;
                TypeNode.Fqn exact = candidate as TypeNode.Fqn;
                if ((exact == null || exact.Args?.Any(ContainsExistential) == true)
                    && source != null && result.TryGetValue(source, out var inherited))
                    exact = inherited;
                if (exact?.Args is not { Length: > 0 } args || exact.Name != starOwner.Name
                    || args.Length != starOwner.Args.Length || args.Any(ContainsExistential)
                    || refs.ResolveForeignProjectionType(exact.Name, args) == null) continue;
                result[name] = exact;
                changed = true;
            }
        }
        return result;
    }

    static JsonObject Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result, params JsonNode[] args) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)),
        ["method"] = method,
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()),
        ["ret"] = TypeJson.Write(result),
        ["args"] = new JsonArray(args),
    };

    // Substitute only a DIRECT readable owner slot. If a projected slot occurs inside another reified construction,
    // replacing it recursively with object would manufacture the same invalid CLR fiction this lowering exists to
    // avoid (`Holder<T>` -> `Holder<object>`, `T[]` -> `object[]`, ...). A trusted DotKt existential can retain that
    // nested classifier; every other dependent construction crosses the reflection boundary as one opaque object.
    static TypeNode ProjectResult(TypeNode type, TypeNode[] ownerArgs, ReferenceMetadataIndex refs,
        IReadOnlyList<TypeNode> methodArgs = null)
    {
        if (type == null) return AnyN;
        if (type is TypeNode.Tv { Scope: "method" } methodVariable
            && methodArgs != null && methodVariable.I >= 0 && methodVariable.I < methodArgs.Count)
            return methodArgs[methodVariable.I];
        if (type is TypeNode.Tv { Scope: "type" } tv && tv.I >= 0 && tv.I < ownerArgs.Length)
            return ownerArgs[tv.I] switch
            {
                TypeNode.Star => AnyN,
                TypeNode.Projection { Variance: "in" } => AnyN,
                // The selected argument is written in the CALLER's generic frame. A caller `type:0` is not the
                // foreign declaration's `type:0`; substituting it again recursively either changes its meaning or,
                // for `Foreign<out T>` returning T, recurses forever. Continue projection with no owner frame.
                TypeNode.Projection projectedArgument => ProjectResult(projectedArgument.Of, Array.Empty<TypeNode>(), refs),
                var argument => ProjectResult(argument, Array.Empty<TypeNode>(), refs),
            };
        if (type is TypeNode.Star) return AnyN;
        if (type is TypeNode.Projection projection)
            return projection.Variance == "in" ? AnyN : ProjectResult(projection.Of, ownerArgs, refs, methodArgs);

        if (DependsOnProjectedOwnerSlot(type, ownerArgs))
        {
            if (type is TypeNode.Fqn f)
            {
                if (_localExistentialOwners.TryGetValue(f.Name, out var localExistential))
                    return new TypeNode.Fqn(localExistential);
                if (refs.TryExistentialPhysicalOwner(f.Name, out var referencedExistential))
                    return new TypeNode.Fqn(referencedExistential);
            }
            if (type is TypeNode.Fqn dependentFqn && dependentFqn.Args is { Length: > 0 } dependentArgs
                && refs.ResolveNetType(dependentFqn.Name, dependentArgs.Length) != null)
                return new TypeNode.Fqn(dependentFqn.Name, dependentArgs.Select(a =>
                    DependsOnProjectedOwnerSlot(a, ownerArgs)
                        ? (TypeNode)new TypeNode.Star()
                        : ProjectResult(a, ownerArgs, refs, methodArgs)).ToArray());
            if (type is TypeNode.Nullable dependentNullable)
                return new TypeNode.Nullable(ProjectResult(dependentNullable.Of, ownerArgs, refs, methodArgs));
            if (type is TypeNode.Oblivious dependentOblivious)
                return new TypeNode.Oblivious(ProjectResult(dependentOblivious.Of, ownerArgs, refs, methodArgs));
            return AnyN;
        }

        return type switch
        {
            TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
                args.Select(a => ProjectResult(a, ownerArgs, refs, methodArgs)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(ProjectResult(n.Of, ownerArgs, refs, methodArgs)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(ProjectResult(o.Of, ownerArgs, refs, methodArgs)),
            TypeNode.Array a => new TypeNode.Array(ProjectResult(a.Elem, ownerArgs, refs, methodArgs)),
            TypeNode.ByRef b => new TypeNode.ByRef(ProjectResult(b.Of, ownerArgs, refs, methodArgs)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, ProjectResult(fn.Ret, ownerArgs, refs, methodArgs),
                fn.Params.Select(p => ProjectResult(p, ownerArgs, refs, methodArgs)).ToArray(),
                fn.Recv == null ? null : ProjectResult(fn.Recv, ownerArgs, refs, methodArgs), fn.Clr,
                fn.Ctx?.Select(c => ProjectResult(c, ownerArgs, refs, methodArgs)).ToArray()),
            _ => type,
        };
    }

    static bool DependsOnProjectedOwnerSlot(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length
            // One simultaneous substitution: the selected argument belongs to the caller's frame. Its TVs must
            // never be rebound against this declaration's unrelated owner argument vector.
            => ContainsExistential(ownerArgs[tv.I]),
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(a => DependsOnProjectedOwnerSlot(a, ownerArgs)),
        TypeNode.Nullable n => DependsOnProjectedOwnerSlot(n.Of, ownerArgs),
        TypeNode.Oblivious o => DependsOnProjectedOwnerSlot(o.Of, ownerArgs),
        TypeNode.Array a => DependsOnProjectedOwnerSlot(a.Elem, ownerArgs),
        TypeNode.ByRef b => DependsOnProjectedOwnerSlot(b.Of, ownerArgs),
        TypeNode.Ptr p => DependsOnProjectedOwnerSlot(p.Of, ownerArgs),
        TypeNode.Mod m => DependsOnProjectedOwnerSlot(m.M, ownerArgs)
            || DependsOnProjectedOwnerSlot(m.Of, ownerArgs),
        TypeNode.Fn fn => DependsOnProjectedOwnerSlot(fn.Ret, ownerArgs)
            || fn.Params.Any(p => DependsOnProjectedOwnerSlot(p, ownerArgs))
            || (fn.Recv != null && DependsOnProjectedOwnerSlot(fn.Recv, ownerArgs))
            || (fn.Ctx?.Any(c => DependsOnProjectedOwnerSlot(c, ownerArgs)) ?? false),
        _ => false,
    };

    static bool ContainsExistential(TypeNode type) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExistential),
        TypeNode.Nullable n => ContainsExistential(n.Of),
        TypeNode.Oblivious o => ContainsExistential(o.Of),
        TypeNode.Array a => ContainsExistential(a.Elem),
        TypeNode.ByRef b => ContainsExistential(b.Of),
        TypeNode.Ptr p => ContainsExistential(p.Of),
        TypeNode.Mod m => ContainsExistential(m.M) || ContainsExistential(m.Of),
        TypeNode.Fn fn => ContainsExistential(fn.Ret) || fn.Params.Any(ContainsExistential)
            || (fn.Recv != null && ContainsExistential(fn.Recv))
            || (fn.Ctx?.Any(ContainsExistential) ?? false),
        _ => false,
    };

    internal static void SelfTest()
    {
        ClrMemberResolution.SelfTestForeignStamps();
        var slot = new TypeNode.Tv("type", 0);
        var callerArgs = new TypeNode[] { slot };
        if (DependsOnProjectedOwnerSlot(slot, callerArgs))
            throw new InvalidOperationException("Caller owner variable was rebound in the declaration frame");
        var permuted = new TypeNode[] { new TypeNode.Tv("type", 1), new TypeNode.Projection("out", String) };
        var nested = new TypeNode.Fqn("Probe.Native", new TypeNode[] { slot });
        if (DependsOnProjectedOwnerSlot(nested, permuted)
            || !DependsOnProjectedOwnerSlot(new TypeNode.Tv("type", 1), permuted)
            || DependsOnProjectedOwnerSlot(new TypeNode.Fqn("Probe.Native", new[] { String }), permuted))
            throw new InvalidOperationException("Projected result dependency crossed caller/declaration frames");
        Console.WriteLine("[foreign projected results] self-test OK (simultaneous substitution, nested dependency, exact independent result)");
    }

    static bool IsObjectish(TypeNode type) => type switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };

    static string FreshTemp()
    {
        string candidate;
        do candidate = "dotkt$foreignStar$value$" + _nextTemp++;
        while (!_reservedNames.Add(candidate));
        return candidate;
    }

    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
