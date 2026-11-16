using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
static partial class MapVarianceRealign
{
    // Invariant collection applications include native aliases and Kotlin-owned classes. Their arguments do not
    // lift via CLR variance, unlike List/Collection/Iterable (`IReadOnly*<out T>`), which stay untouched.
    public static readonly HashSet<string> InvariantCollections = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Map", "kotlin.collections.MutableMap",
        "kotlin.collections.HashMap", "kotlin.collections.LinkedHashMap",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.collections.HashSet", "kotlin.collections.LinkedHashSet",
    };

    // The frontend-selected declaration owns this vector. A physical generic
    // arity is not an overload identity and does not state companion roles.
    public static Dictionary<string, TypeNode[]> CollectCalleeTypeParams(IEnumerable<BirFileDeclaration> roots)
    {
        var map = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        void Methods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
            {
                if (!method.DeclarationId.HasValue || method.TypeParams.OrEmpty().Length == 0) continue;
                var id = method.DeclarationId.Value;
                var paramTypes = method.Params.OrEmpty().Select(parameter => parameter.Type.HasValue
                    ? BirGraphValue.CopyType(parameter.Type.Value) : null).ToArray();
                // This is the existing physical-signature conflict policy, also
                // used by the owning call realigner consuming this detached index.
                if (map.TryGetValue(id, out var previous) && !previous.SequenceEqual(paramTypes))
                    throw new InvalidOperationException("Collection realignment has conflicting selected declaration signatures");
                map[id] = paramTypes;
            }
        }
        foreach (var root in roots)
        {
            Methods(root.Methods.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Methods(type.Methods.OrEmpty());
        }
        return map;
    }

    // CLR variance does not apply to value-type arguments. Keep the exact
    // source collection face in an alias local rather than storing, for example,
    // MutableList<Int> as MutableIterable<Any?> and producing unverifiable IL.
    // A type-variable source similarly restores its precise invariant bound.
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
    // Wrappers do not change the invariant collection head selected below.
    static TypeNode UnwrapNullableOblivious(TypeNode t) => t switch
    {
        TypeNode.Nullable n => UnwrapNullableOblivious(n.Of),
        TypeNode.Oblivious ob => UnwrapNullableOblivious(ob.Of),
        _ => t,
    };
    // Replace only over-approximated arguments; retain the original owner when
    // none has a more precise bound argument.
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

}
