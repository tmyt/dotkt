using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    // An operation selection is scalar policy, not a saved executable graph.
    // The source and physical graph owners render the same argument layout.
    internal sealed record CollectionOperationPlan(string Owner, string Method, TypeNode Element,
        TypeNode.Fqn PreferenceView = null, string NativePreference = null, int? Family = null,
        bool DefaultIndex = false, TypeNode Result = null);

    internal static TypeNode CollectionVariableType(TypeNode type) => type switch {
        TypeNode.Nullable value => CollectionVariableType(value.Of),
        TypeNode.Oblivious value => CollectionVariableType(value.Of), _ => type,
    };

    internal static CollectionOperationPlan SelectMissingCollectionIterator(string member, int count, bool declaresIterator,
        bool property, int typeArguments, TypeNode result)
    {
        if (member != "iterator" || count != 0 || declaresIterator || property || typeArguments != 0
            || result is not TypeNode.Fqn { Args: { Length: 1 } elements } protocol
            || protocol.Name is not ("kotlin.collections.Iterator" or "kotlin.collections.MutableIterator")) return null;
        var mutable = protocol.Name == "kotlin.collections.MutableIterator";
        return new(mutable ? "kotlin.collections.ClrCollectionDefaultsKt" : "kotlin.collections.ClrIteratorBridgeKt",
            mutable ? "clrMutableIterator" : "iteratorOverEnumerable", elements[0]);
    }

    internal static TypeNode CollectionReceiverType(string kind, TypeNode stamp, string local,
        TypeNode expressionType, IReadOnlyDictionary<string, TypeNode> variables, bool allowExpressionShapes = true)
    {
        if (stamp != null) return stamp switch {
            TypeNode.Nullable value => value.Of, TypeNode.Oblivious value => value.Of, _ => stamp,
        };
        if (kind == "local") return local != null && variables?.TryGetValue(local, out var type) == true ? type : null;
        return allowExpressionShapes && kind is "field" or "callInstance" or "arrayGet" or "cast" ? expressionType : null;
    }

    internal static TypeNode CollectionElement(TypeNode.Fqn owner, TypeNode receiver,
        IReadOnlyDictionary<string, List<TypeNode>> constraints, ReferenceMetadataIndex refs)
    {
        var own = OwnerElemArg(owner);
        if (!IsObjType(own) || owner.Args != null) return own;
        if (receiver is TypeNode.Tv variable && constraints?.TryGetValue(variable.Scope + ":" + variable.I, out var bounds) == true)
        {
            foreach (var bound in bounds)
                if (bound is TypeNode.Fqn { Args: { Length: >= 1 } arguments } named
                    && refs.TryResolveClrOwner(named.Name, out _, out var kind) && kind == "interface" && arguments[0] != null)
                    return arguments[0];
        }
        else if (receiver is TypeNode.Fqn named)
        {
            var element = OwnerElemArg(named);
            if (!IsObjType(element)) return element;
        }
        return ObjType;
    }

    internal static (TypeNode Key, TypeNode Value) MapTypeArguments(TypeNode.Fqn owner, TypeNode receiver,
        IReadOnlyDictionary<string, List<TypeNode>> constraints, ReferenceMetadataIndex refs)
    {
        var (key, value) = OwnerKvArgs(owner);
        if ((!IsObjType(key) && !IsObjType(value)) || refs == null) return (key, value);
        if (receiver is TypeNode.Tv variable && constraints?.TryGetValue(variable.Scope + ":" + variable.I, out var bounds) == true)
            foreach (var bound in bounds)
            {
                if (bound is not TypeNode.Fqn { Args: { Length: >= 2 } arguments } named
                    || !refs.TryResolveClrOwner(named.Name, out _, out var kind) || kind != "interface") continue;
                if (IsObjType(key) && arguments[0] != null) key = arguments[0];
                if (IsObjType(value) && arguments[1] != null) value = arguments[1];
                break;
            }
        return (key, value);
    }

    static bool ContainsCollectionProjection(TypeNode type) => type switch {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn named => named.Args?.Any(ContainsCollectionProjection) == true,
        TypeNode.Nullable value => ContainsCollectionProjection(value.Of),
        TypeNode.Oblivious value => ContainsCollectionProjection(value.Of),
        TypeNode.Array value => ContainsCollectionProjection(value.Elem),
        TypeNode.ByRef value => ContainsCollectionProjection(value.Of),
        TypeNode.Ptr value => ContainsCollectionProjection(value.Of),
        TypeNode.Mod value => ContainsCollectionProjection(value.M) || ContainsCollectionProjection(value.Of),
        TypeNode.Fn value => ContainsCollectionProjection(value.Ret) || value.Params.Any(ContainsCollectionProjection)
            || value.Recv != null && ContainsCollectionProjection(value.Recv) || value.Ctx?.Any(ContainsCollectionProjection) == true,
        _ => false,
    };

    internal static bool ProjectedCollectionReceiver(TypeNode.Fqn owner, TypeNode receiver, bool projectedLocal,
        IReadOnlyDictionary<string, List<TypeNode>> constraints, ReferenceMetadataIndex refs)
    {
        if (ContainsCollectionProjection(owner) || refs != null && AliasVarianceRepresentation.RequiresErasure(owner, refs)) return true;
        if (projectedLocal) return true;
        if (receiver == null) return false;
        if (StarProjectionLowering.IsNonGenericCollectionFacade(receiver)) return true;
        if (receiver is TypeNode.Fqn { Args: not null } named && named.Name == owner.Name && ContainsCollectionProjection(named)) return true;
        if (receiver is not TypeNode.Tv variable) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        TypeNode Core(TypeNode type) => type switch {
            TypeNode.Nullable value => Core(value.Of), TypeNode.Oblivious value => Core(value.Of), _ => type,
        };
        bool Visit(TypeNode.Tv current)
        {
            var key = current.Scope + ":" + current.I;
            if (!seen.Add(key) || constraints?.TryGetValue(key, out var bounds) != true) return false;
            foreach (var bound in bounds)
            {
                var core = Core(bound);
                if (core is TypeNode.Fqn { Args: not null } candidate
                    && candidate.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal)
                    && ContainsCollectionProjection(candidate)) return true;
                if (core is TypeNode.Tv next && Visit(next)) return true;
            }
            return false;
        }
        return Visit(variable);
    }

    internal static TypeNode.Fqn CollectionMapBound(TypeNode receiver, IReadOnlyDictionary<string, List<TypeNode>> constraints)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        TypeNode.Fqn Bound(TypeNode type)
        {
            if (type is TypeNode.Nullable nullable) return Bound(nullable.Of);
            if (type is TypeNode.Oblivious oblivious) return Bound(oblivious.Of);
            if (type is TypeNode.Fqn owner && owner.Name is "kotlin.collections.Map" or "kotlin.collections.MutableMap") return owner;
            if (type is TypeNode.Tv variable && seen.Add(variable.Scope + ":" + variable.I)
                && constraints?.TryGetValue(variable.Scope + ":" + variable.I, out var bounds) == true)
                foreach (var bound in bounds) if (Bound(bound) is { } map) return map;
            return null;
        }
        return Bound(receiver);
    }

    internal static TypeNode CollectionPreferenceType(TypeNode.Fqn owner, ReferenceMetadataIndex refs, string nativeOwner,
        CollectionOperationPhase phase)
    {
        var arguments = CollectionPreferenceArguments(owner.Args, refs?.NullableTypeFrames.GetValueOrDefault(owner.Name), phase);
        return arguments is not { Length: 1 } || HasProjection(arguments[0]) ? null
            : new TypeNode.Fqn(nativeOwner, arguments.Select(argument => phase == CollectionOperationPhase.Source ? argument
                : NullableGenericErasure.EraseArgument(argument, refs == null ? _isValue : refs.IsValueType)).ToArray());
    }

    internal static CollectionOperationPlan SelectCollectionMutation(string member, int count, TypeNode.Fqn owner,
        TypeNode element, bool projected, TypeNode.Fqn preferred, TypeNode explicitResult)
    {
        const string helpers = "kotlin.collections.ClrCollectionDefaultsKt";
        CollectionOperationPlan Plan(string method, bool preference = false, TypeNode result = null) => new(helpers, method, element,
            preference ? preferred ?? owner : null, preference ? "System.Collections.Generic.IList`1" : null, Result: result);
        if (owner.Name == "kotlin.collections.MutableList" && member is "add" or "Add" && count == 1 && projected)
            return Plan("clrProjectedListAdd", true);
        if (owner.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal) && CollectionSlotHelper(member, count, owner.Name) is { } helper)
        {
            if (projected) helper = helper switch {
                "clrCollAdd" => "clrProjectedCollAdd", "clrCollAddAll" => "clrProjectedCollAddAll",
                "clrCollRemoveAll" => "clrProjectedCollRemoveAll", "clrCollRetainAll" => "clrProjectedCollRetainAll",
                "clrListAddAllAt" => "clrProjectedListAddAllAt", _ => helper,
            };
            return Plan(helper, helper == "clrProjectedListAddAllAt");
        }
        if (owner.Name == "kotlin.collections.MutableList" && member is "add" or "Insert" && count == 2 && projected)
            return Plan("clrProjectedListAddAt", true);
        if (owner.Name == "kotlin.collections.MutableList"
            && (member is "set" or "set_Item" && count == 2 || member is "removeAt" or "RemoveAt" && count == 1))
        {
            var helper = (member is "set" or "set_Item", projected) switch {
                (true, true) => "clrProjectedListSet", (false, true) => "clrProjectedListRemoveAt",
                (true, false) => "clrListSet", _ => "clrListRemoveAt",
            };
            return Plan(helper, projected, explicitResult is TypeNode.Tv ? null : explicitResult)
                with { Element = projected ? element : OwnerElemArg(owner) };
        }
        return null;
    }

    internal static CollectionOperationPlan SelectStaticCollectionOperation(string member, string property, int count,
        bool hasReceiver, TypeNode.Fqn owner, TypeNode element, TypeNode.Fqn preferred)
    {
        var family = owner.Name switch {
            "kotlin.collections.Collection" => 0, "kotlin.collections.Set" => 1, "kotlin.collections.Iterable" => 2,
            "kotlin.collections.List" => 3, "kotlin.collections.MutableSet" => 4,
            "kotlin.collections.MutableCollection" => 5, "kotlin.collections.MutableList" => 6, _ => -1,
        };
        if (family < 0 || family >= 4 && member is not ("contains" or "containsAll" or "isEmpty")
            && !(family == 5 && member == "size")) return null;
        var helper = (member, count) switch {
            ("size", 0) when family != 2 && property == "get" => "projectedViewCountErased",
            ("isEmpty", 0) when family != 2 => "clrProjectedViewIsEmpty",
            ("contains", 1) when family != 2 => "clrProjectedViewContains",
            ("containsAll", 1) when family != 2 => "clrProjectedViewContainsAll",
            ("iterator", 0) => "clrProjectedViewIterator",
            ("get" or "get_Item", 1) when family == 3 => "clrProjectedViewGet",
            ("indexOf", 1) when family == 3 => "clrProjectedViewIndexOf",
            ("lastIndexOf", 1) when family == 3 => "clrProjectedViewLastIndexOf",
            ("listIterator", 0) when family == 3 => "clrProjectedViewListIterator",
            ("listIterator", 1) when family == 3 => "clrProjectedViewListIteratorAt",
            ("subList", 2) when family == 3 => "clrProjectedViewSubList", _ => null,
        };
        if (helper == null || !hasReceiver) return null;
        var native = family switch {
            0 => "System.Collections.Generic.IReadOnlyCollection`1", 1 => "System.Collections.Generic.IReadOnlySet`1",
            3 => "System.Collections.Generic.IReadOnlyList`1", 4 => "System.Collections.Generic.ISet`1",
            5 => "System.Collections.Generic.ICollection`1", 6 => "System.Collections.Generic.IList`1",
            _ => "System.Collections.Generic.IEnumerable`1",
        };
        var result = member switch {
            "iterator" => new TypeNode.Fqn("kotlin.collections.Iterator", new[] { element }),
            "listIterator" => new TypeNode.Fqn("kotlin.collections.ListIterator", new[] { element }),
            "subList" => new TypeNode.Fqn("kotlin.collections.List", new[] { element }),
            "get" or "get_Item" => element, "indexOf" or "lastIndexOf" or "size" => new TypeNode.Fqn("kotlin.Int"),
            _ => new TypeNode.Fqn("kotlin.Boolean"),
        };
        return new(helper == "projectedViewCountErased" ? "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"
                : "kotlin.collections.ClrCollectionDefaultsKt", helper, helper == "projectedViewCountErased" ? null : element,
            family >= 4 ? preferred ?? owner : owner, native, family, Result: result);
    }

    internal static CollectionOperationPlan SelectCollectionDefault(string member, string property, int count, bool hasReceiver,
        TypeNode.Fqn owner, TypeNode element, bool projected, TypeNode.Fqn preferred)
    {
        if (SelectStaticCollectionOperation(member, property, count, hasReceiver, owner, element, preferred) is { } operation) return operation;
        const string helpers = "kotlin.collections.ClrCollectionDefaultsKt";
        CollectionOperationPlan Plan(string method, bool preference = false, bool defaultIndex = false, string helperOwner = helpers)
            => new(helperOwner, method, projected ? element : OwnerElemArg(owner),
                preference ? preferred ?? owner : null, preference ? "System.Collections.Generic.IList`1" : null, DefaultIndex: defaultIndex);
        if (projected && owner.Name == "kotlin.collections.MutableList")
        {
            var state = (member, count) switch {
                ("listIterator", 0) => "clrProjectedMutableListIterator", ("listIterator", 1) => "clrProjectedMutableListListIterator",
                ("subList", 2) => "clrProjectedMutableListSubList", _ => null,
            };
            if (state != null) return Plan(state, true);
        }
        if (member == "iterator" && count == 0)
        {
            if (owner.Name is "kotlin.collections.MutableIterable" or "kotlin.collections.MutableCollection"
                or "kotlin.collections.MutableSet" or "kotlin.collections.MutableList") return Plan("clrMutableIterator");
            return projected ? Plan("clrProjectedIterator")
                : Plan("iteratorOverEnumerable", helperOwner: "kotlin.collections.ClrIteratorBridgeKt");
        }
        if (member == "listIterator")
        {
            if (owner.Name == "kotlin.collections.MutableList") return Plan("clrMutableListListIterator", defaultIndex: count == 0);
            if (projected) return Plan(count == 0 ? "clrProjectedListIterator" : "clrProjectedListListIterator");
            return Plan("clrListListIterator", defaultIndex: count == 0);
        }
        if (!CollectionDefaults.TryGetValue(member, out var helper)) return null;
        if (helper == "clrListSubList" && owner.Name == "kotlin.collections.MutableList") helper = "clrMutableListSubList";
        else if (projected) helper = helper switch {
            "clrCollIsEmpty" => ProjectedIsEmptyHelper(owner.Name), "clrCollContains" => "clrProjectedCollContains",
            "clrCollContainsAll" => "clrProjectedCollContainsAll", "clrListIndexOf" => "clrProjectedListIndexOf",
            "clrListLastIndexOf" => "clrProjectedListLastIndexOf", "clrListSubList" => "clrProjectedListSubList", _ => helper,
        };
        return Plan(helper);
    }

    internal static TypeNode[] CollectionOperationTypes(CollectionOperationPlan plan)
        => plan.Method == "projectedViewCountErased"
            ? new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(ObjType), new TypeNode.Fqn("kotlin.Int") }
            : CollectionHelperTypes(plan.Owner, plan.Method);

    static JsonObject RenderCollectionOperation(CollectionOperationPlan plan, JsonObject node, JsonArray args,
        ReferenceMetadataIndex refs, CollectionOperationPhase phase)
    {
        if (plan == null) return null;
        var operands = new JsonArray();
        if (node["recv"] != null) operands.Add(node["recv"].DeepClone());
        if (plan.NativePreference != null) operands.Add(StaticCollectionPreference(plan.PreferenceView, refs, plan.NativePreference, phase));
        if (plan.Family is int family) operands.Add(new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = family });
        foreach (var argument in args) operands.Add(argument?.DeepClone());
        if (plan.DefaultIndex) operands.Add(new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = 0 });
        var call = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(plan.Owner), ["method"] = plan.Method,
            ["sig"] = new JsonArray(CollectionOperationTypes(plan).Select(TypeJson.Write).ToArray()),
            ["typeArgs"] = plan.Element == null ? new JsonArray() : new JsonArray(TypeJson.Write(plan.Element)), ["args"] = operands,
        };
        if (plan.Result != null) call["ret"] = TypeJson.Write(plan.Result);
        return call;
    }
}
