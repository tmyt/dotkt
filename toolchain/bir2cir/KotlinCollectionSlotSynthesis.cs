using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using SelectedParameterHeader = KotlinOverrideSlotBridge.SelectedParameterHeader;

// KOTLIN-ONLY collection-interface slots — the mirror image of CollectionBclSlotSynthesis.
//
// That pass fills the BCL members Kotlin's collection interfaces lack (`Contains`/`CopyTo`/`IsReadOnly`/`IndexOf`).
// This one fills the other direction: `MutableCollection<E>` IS `ICollection<E>` and `MutableList<E>` IS `IList<E>`,
// and those BCL interfaces carry NO slot for several Kotlin members. Read-only Collection/List defaults have no
// corresponding member at all (`contains`, `listIterator`, `subList`, ...); mutable defaults either have no BCL slot
// (`removeAll`, `retainAll`, `addAll`) or a differently-shaped one. Without a slot there is nothing for a projected
// call to dispatch on, so a Kotlin class that OVERRIDES one of them could not be reached: the call site can only see
// an erased BCL face.
//
// Give every emitted class whose Kotlin supertypes reach such a member a real compiler-owned CLR interface slot and
// one private `dotkt$slot$…` bridge per member, wired by an exact `clrInterfaceImpls` MethodImpl descriptor. Each
// bridge forwards opaque collection arguments unchanged and restores element arguments with a cast/unbox.
// It forwards VIRTUALLY, so a further-derived override still wins and
// a subclass needs no bridge of its own.
//
// THE MEMBER IS IDENTIFIED BY THE FRONTEND'S OVERRIDE FACTS, NEVER BY NAME AND ARITY. A declaration qualifies only
// when its `overrides` chain names `kotlin.collections.MutableCollection` / `MutableList` with the slot member and
// arity. The frontend flattens that chain, so an INDIRECT implementer (`class C : I<E>` for a user
// `interface I<E> : MutableCollection<E>`) carries the same entry and needs no interface-closure walk of our own.
// Name+arity keying would have been wrong four ways: an unrelated same-name overload (`fun addAll(other: Bag<E>)`)
// produced a second bridge with a colliding physical signature, a wrong-shaped same-name member
// (`fun removeAll(n: Int)`) got an InterfaceImpl row with no matching MethodImpl (TypeLoadException), a member
// extension function's lowered receiver could satisfy the arity test, and an `@ClrName`-renamed override was missed
// entirely — silently falling back to the BCL default.
//
// The bridge's parameter typing likewise comes from the OVERRIDDEN declaration, substituted into the implementer's
// frame — never from guessing which parameter "looks like" the collection. Its collection parameter is ERASED to
// `Any` to match the non-generic slot interface and the implementation's opaque collection value ABI, so the
// capability test at the call site never has to be re-argued from the dispatcher's generic arguments (see the note
// on KotlinCollectionSlots.kt — a robustness choice, not a reproduced bug fix).
//
// Runs in the Kotlin-vocabulary phase, right after CovariantInterfaceReturnBridge: that is where `overrides` still
// exists (it does not survive to CIR) and where the supertype graph is still Kotlin's own. Non-ref builds only —
// the ref surface stays pure Kotlin and every consumer re-derives the physical form from it.
static partial class KotlinCollectionSlotSynthesis
{
    const string MutableIterable = "kotlin.collections.MutableIterable";
    const string Collection = "kotlin.collections.Collection";
    const string List = "kotlin.collections.List";
    const string MutableCollection = "kotlin.collections.MutableCollection";
    const string MutableList = "kotlin.collections.MutableList";
    const string IteratorSlots = "DotKt.Runtime.CompilerServices.KotlinMutableIteratorSlots";
    const string CollectionDefaultSlots = "DotKt.Runtime.CompilerServices.KotlinCollectionDefaultSlots";
    const string ListDefaultSlots = "DotKt.Runtime.CompilerServices.KotlinListDefaultSlots";
    const string CollectionSlots = "DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots";
    const string CollectionAddSlot = "DotKt.Runtime.CompilerServices.KotlinMutableCollectionAddSlot";
    const string ListSlots = "DotKt.Runtime.CompilerServices.KotlinMutableListSlots";
    const string ListSetSlot = "DotKt.Runtime.CompilerServices.KotlinMutableListSetSlot";
    const string ListRemoveAtSlot = "DotKt.Runtime.CompilerServices.KotlinMutableListRemoveAtSlot";
    const string Map = "kotlin.collections.Map";
    const string MutableMap = "kotlin.collections.MutableMap";
    internal const string MapSlots = "DotKt.Runtime.CompilerServices.KotlinMapDefaultSlots";
    internal const string MutableMapSlots = "DotKt.Runtime.CompilerServices.KotlinMutableMapSlots";

    enum ParameterCarrier
    {
        Exact,
        ErasedValue,
        ErasedCollection,
        ErasedInput,
        MapRemapping,
    }

    /// <summary>One Kotlin member with no BCL slot, and the compiler-owned interface slot that carries it.</summary>
    sealed class Slot
    {
        public string DeclaringInterface;   // the Kotlin interface that declares the member (override identity)
        public string Member;               // its Kotlin name
        public int Arity;                   // its parameter count
        public string Kind = "method";
        public string SlotInterface;        // the compiler-owned interface carrying the physical slot
        public string SlotMember;           // the slot's member name
        public string Bridge;               // the synthesized forwarding method's name
        public bool EraseReturn;             // the non-generic carrier returns Any, while the target stays exact
        public bool NullableReturn;
        public ParameterCarrier[] Parameters = Array.Empty<ParameterCarrier>();
    }

    static readonly Slot[] Slots = new Slot[]
    {
        new() { DeclaringInterface = MutableIterable, Member = "iterator", Arity = 0,
                SlotInterface = IteratorSlots, SlotMember = "dotktIterator", Bridge = "dotkt$slot$iterator",
                EraseReturn = true },
        new() { DeclaringInterface = Collection, Member = "isEmpty", Arity = 0,
                SlotInterface = CollectionDefaultSlots, SlotMember = "dotktIsEmpty", Bridge = "dotkt$slot$isEmpty" },
        new() { DeclaringInterface = Collection, Member = "contains", Arity = 1,
                SlotInterface = CollectionDefaultSlots, SlotMember = "dotktContains", Bridge = "dotkt$slot$contains",
                Parameters = new[] { ParameterCarrier.ErasedValue } },
        new() { DeclaringInterface = Collection, Member = "containsAll", Arity = 1,
                SlotInterface = CollectionDefaultSlots, SlotMember = "dotktContainsAll", Bridge = "dotkt$slot$containsAll",
                Parameters = new[] { ParameterCarrier.ErasedCollection } },
        new() { DeclaringInterface = List, Member = "indexOf", Arity = 1,
                SlotInterface = ListDefaultSlots, SlotMember = "dotktIndexOf", Bridge = "dotkt$slot$indexOf",
                Parameters = new[] { ParameterCarrier.ErasedValue } },
        new() { DeclaringInterface = List, Member = "lastIndexOf", Arity = 1,
                SlotInterface = ListDefaultSlots, SlotMember = "dotktLastIndexOf", Bridge = "dotkt$slot$lastIndexOf",
                Parameters = new[] { ParameterCarrier.ErasedValue } },
        new() { DeclaringInterface = List, Member = "listIterator", Arity = 0,
                SlotInterface = ListDefaultSlots, SlotMember = "dotktListIterator", Bridge = "dotkt$slot$listIterator",
                EraseReturn = true },
        new() { DeclaringInterface = List, Member = "listIterator", Arity = 1,
                SlotInterface = ListDefaultSlots, SlotMember = "dotktListIteratorAt", Bridge = "dotkt$slot$listIteratorAt",
                Parameters = new[] { ParameterCarrier.Exact }, EraseReturn = true },
        new() { DeclaringInterface = List, Member = "subList", Arity = 2,
                SlotInterface = ListDefaultSlots, SlotMember = "dotktSubList", Bridge = "dotkt$slot$subList",
                Parameters = new[] { ParameterCarrier.Exact, ParameterCarrier.Exact }, EraseReturn = true },
        new() { DeclaringInterface = MutableCollection, Member = "add", Arity = 1,
                SlotInterface = CollectionAddSlot, SlotMember = "dotktAdd", Bridge = "dotkt$slot$add",
                Parameters = new[] { ParameterCarrier.ErasedInput } },
        new() { DeclaringInterface = MutableCollection, Member = "removeAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktRemoveAll", Bridge = "dotkt$slot$removeAll",
                Parameters = new[] { ParameterCarrier.ErasedCollection } },
        new() { DeclaringInterface = MutableCollection, Member = "retainAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktRetainAll", Bridge = "dotkt$slot$retainAll",
                Parameters = new[] { ParameterCarrier.ErasedCollection } },
        new() { DeclaringInterface = MutableCollection, Member = "addAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktAddAll", Bridge = "dotkt$slot$addAll",
                Parameters = new[] { ParameterCarrier.ErasedCollection } },
        new() { DeclaringInterface = MutableList, Member = "addAll", Arity = 2,
                SlotInterface = ListSlots, SlotMember = "dotktAddAllAt", Bridge = "dotkt$slot$addAllAt",
                Parameters = new[] { ParameterCarrier.Exact, ParameterCarrier.ErasedCollection } },
        new() { DeclaringInterface = MutableList, Member = "set", Arity = 2,
                SlotInterface = ListSetSlot, SlotMember = "dotktSet", Bridge = "dotkt$slot$set",
                Parameters = new[] { ParameterCarrier.Exact, ParameterCarrier.ErasedInput },
                EraseReturn = true, NullableReturn = true },
        new() { DeclaringInterface = MutableList, Member = "removeAt", Arity = 1,
                SlotInterface = ListRemoveAtSlot, SlotMember = "dotktRemoveAt", Bridge = "dotkt$slot$removeAt",
                Parameters = new[] { ParameterCarrier.Exact }, EraseReturn = true, NullableReturn = true },
    }.Concat(MapSlotDefinitions()).ToArray();

    static readonly HashSet<string> SlotInterfaces = Slots.Select(slot => slot.SlotInterface)
        .ToHashSet(StringComparer.Ordinal);

    static IEnumerable<Slot> MapSlotDefinitions()
    {
        Slot Read(string member, string target, bool erased = false, bool nullable = false,
            string kind = "method", params ParameterCarrier[] parameters) => new() {
                DeclaringInterface = Map, Member = member, Arity = parameters.Length, Kind = kind,
                SlotInterface = MapSlots, SlotMember = target, Bridge = "dotkt$slot$" + target,
                EraseReturn = erased, NullableReturn = nullable, Parameters = parameters,
            };
        Slot Write(string member, string target, bool erased = false, bool nullable = false,
            string kind = "method", params ParameterCarrier[] parameters) => new() {
                DeclaringInterface = MutableMap, Member = member, Arity = parameters.Length, Kind = kind,
                SlotInterface = MutableMapSlots, SlotMember = target, Bridge = "dotkt$slot$" + target,
                EraseReturn = erased, NullableReturn = nullable, Parameters = parameters,
            };
        const ParameterCarrier query = ParameterCarrier.ErasedValue;
        const ParameterCarrier input = ParameterCarrier.ErasedInput;
        yield return Read("size", "dotktMapSize", kind: "getter");
        yield return Read("isEmpty", "dotktMapIsEmpty");
        yield return Read("containsKey", "dotktMapContainsKey", parameters: new[] { query });
        yield return Read("containsValue", "dotktMapContainsValue", parameters: new[] { query });
        yield return Read("get", "dotktMapGet", true, true, parameters: new[] { query });
        yield return Read("keys", "dotktMapKeys", true, kind: "getter");
        yield return Read("values", "dotktMapValues", true, kind: "getter");
        yield return Read("entries", "dotktMapEntries", true, kind: "getter");
        yield return Read("getOrDefault", "dotktMapGetOrDefault", true, true, parameters: new[] { query, input });
        yield return Write("put", "dotktMapPut", true, true, parameters: new[] { input, input });
        yield return Write("remove", "dotktMapRemove", true, true, parameters: new[] { query });
        yield return Write("putAll", "dotktMapPutAll", parameters: new[] { input });
        yield return Write("clear", "dotktMapClear");
        yield return Write("keys", "dotktMapMutableKeys", true, kind: "getter");
        yield return Write("values", "dotktMapMutableValues", true, kind: "getter");
        yield return Write("entries", "dotktMapMutableEntries", true, kind: "getter");
        yield return Write("remove", "dotktMapRemoveEntry", parameters: new[] { query, query });
        yield return Write("putIfAbsent", "dotktMapPutIfAbsent", true, true, parameters: new[] { input, input });
        yield return Write("replace", "dotktMapReplace", true, true, parameters: new[] { query, input });
        yield return Write("replace", "dotktMapReplaceEntry", parameters: new[] { query, query, input });
        yield return Write("merge", "dotktMapMerge", true, true,
            parameters: new[] { input, input, ParameterCarrier.MapRemapping });
    }

    internal static bool OwnsMapOverride(JsonObject marker) =>
        OwnsMapOverride(TypeJson.Read(marker["owner"]), Str(marker["member"]), Str(marker["kind"]),
            marker["arity"] is JsonValue value && value.TryGetValue<int>(out var arity) ? arity : null);

    internal static bool OwnsMapOverride(TypeNode type, string member, string kind, int? arity) =>
        type is TypeNode.Fqn owner && IsMapContractOwner(owner.Name)
        && Slots.Any(slot => IsMapContractOwner(slot.DeclaringInterface)
            && (owner.Name == slot.DeclaringInterface || owner.Name == MutableMap && slot.DeclaringInterface == Map)
            && member == slot.Member && kind == slot.Kind && arity == slot.Arity);

    internal static bool IsMapContractOwner(string owner) => owner is Map or MutableMap;

    internal static JsonObject ErasedQueryOverride(JsonObject operation) =>
        (operation["overrides"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(marker =>
            Slots.Any(slot => slot.Parameters.Contains(ParameterCarrier.ErasedValue) && MatchesOverride(marker, slot)));

    internal static BirOverride ErasedQueryOverride(BirCallInstance operation) =>
        ErasedQueryOverride(operation.Overrides.OrEmpty());

    internal static BirOverride ErasedQueryOverride(IEnumerable<BirOverride> overrides) =>
        overrides.FirstOrDefault(marker => Slots.Any(slot => slot.Parameters.Contains(ParameterCarrier.ErasedValue)
            && MatchesOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
                marker.Arity.HasValue ? marker.Arity.Value : null, slot)));

    internal static bool IsErasedQueryParameter(JsonObject operation, int index) =>
        ErasedQueryOverride(operation) is { } marker && Slots.Any(slot => MatchesOverride(marker, slot)
            && index < slot.Parameters.Length && slot.Parameters[index] == ParameterCarrier.ErasedValue);

    internal static bool UsesErasedQueryCarrier(TypeNode type) => type switch {
        TypeNode.Nullable nullable => UsesErasedQueryCarrier(nullable.Of),
        TypeNode.Oblivious oblivious => UsesErasedQueryCarrier(oblivious.Of),
        _ => SignatureValueTypes.Of(type) is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any", Args: null },
    };

    internal static bool OwnsSupplementalOverride(JsonObject marker) =>
        OwnsMapOverride(marker) || Slots.Any(slot => MatchesOverride(marker, slot)
            && (slot.DeclaringInterface == MutableList && slot.EraseReturn
                || slot.DeclaringInterface == MutableCollection && slot.Member == "add"));

    internal static bool OwnsSupplementalOverride(BirOverride marker) =>
        OwnsMapOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
            marker.Arity.HasValue ? marker.Arity.Value : null)
        || Slots.Any(slot => MatchesOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
            marker.Arity.HasValue ? marker.Arity.Value : null, slot)
            && (slot.DeclaringInterface == MutableList && slot.EraseReturn
                || slot.DeclaringInterface == MutableCollection && slot.Member == "add"));

    /// <summary>The exact slot members a LOCAL base class already provides, plus inherited carrier interfaces.</summary>
    ///
    /// A carrier groups several independent Kotlin members, so inheriting one bridge cannot suppress synthesis of
    /// its siblings. An EXTERNAL base needs no query: current-toolchain external interfaces carry their own DIM bridge,
    /// while a class declaring a new override receives its own exact bridge below.
    sealed class InheritedSlotState
    {
        public readonly HashSet<string> Interfaces = new(StringComparer.Ordinal);
        public readonly HashSet<string> Members = new(StringComparer.Ordinal);
    }

    static string SlotKey(Slot slot) => slot.SlotInterface + "\0" + slot.SlotMember;

    static bool MatchesOverride(JsonObject marker, Slot slot) =>
        MatchesOverride(TypeJson.Read(marker["owner"]), Str(marker["member"]), Str(marker["kind"]),
            marker["arity"] is JsonValue value && value.TryGetValue<int>(out var arity) ? arity : null, slot);

    static bool MatchesOverride(TypeNode type, string member, string kind, int? arity, Slot slot) =>
        type is TypeNode.Fqn owner && owner.Name == slot.DeclaringInterface
        && member == slot.Member && kind == slot.Kind && arity == slot.Arity;

    // The selected declaration's erased source bound is distinct from both its physical argument slot and closed K.
    // This fact belongs only to the transient inherited candidate, not to an emitted MethodDef signature.
    internal const string QueryBoundKey = "_collectionQueryBound";

    internal static TypeNode GenericQueryBound(TypeNode source, JsonObject method, JsonObject owner,
        Func<TypeNode, bool> isInterface, IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null,
        ValueTypeOracle isValue = null)
        => GenericQueryBound(source, ErasedElementValues.MethodBounds(method), ErasedElementValues.OwnerBounds(owner),
            isInterface, frames, isValue);

    internal static TypeNode GenericQueryBound(TypeNode source, ErasedElementValues.BoundDeclaration method,
        ErasedElementValues.BoundDeclaration owner, Func<TypeNode, bool> isInterface,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames = null, ValueTypeOracle isValue = null)
    {
        if ((source is TypeNode.Nullable nullable ? nullable.Of : source) is not TypeNode.Tv variable) return null;
        var mapping = new NullableRepresentationTypes(owner?.Frame, method?.Frame,
            frames ?? new Dictionary<string, NullableRepresentationFrame>(), isValue ?? (_ => false));
        var bound = ErasedElementValues.ErasedBound((TypeNode.Tv)mapping.Slot(variable), method, owner,
            isInterface, frames, isValue);
        if (bound is TypeNode.Fqn { Name: "object" }) bound = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        return source is TypeNode.Nullable && bound is not TypeNode.Nullable ? new TypeNode.Nullable(bound) : bound;
    }

    static TypeNode QueryGuardType(TypeNode physical, TypeNode selectedBound, IReadOnlyList<int> nullableFlags) =>
        selectedBound ?? (physical is not (TypeNode.Nullable or TypeNode.Oblivious or TypeNode.Tv)
            && nullableFlags is { Count: > 0 } && nullableFlags[0] == 2
                ? new TypeNode.Nullable(physical) : physical);

    static void ValidateCollectionArgument(Slot slot, TypeNode source, TypeNode declared)
    {
        if (source is not TypeNode.Fqn { Name: Collection, Args.Length: 1 }
            || declared is not TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object" })
            throw new InvalidOperationException(
                $"bir2cir: '{slot.DeclaringInterface}.{slot.Member}' collection slot disagrees with its source "
                + $"({TypeJson.Write(source)}) or physical parameter ({TypeJson.Write(declared)}).");
    }

    public static void SelfTest()
    {
        QueryParameterSelfTest();
        SelfTestSource();
        foreach (var element in new TypeNode[] {
            new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"), new TypeNode.Tv("type", 1),
            new TypeNode.Projection("out", new TypeNode.Tv("type", 1)),
            new TypeNode.Fqn(Collection, new TypeNode[] { new TypeNode.Fqn("kotlin.String") }),
        })
        {
            var source = new TypeNode.Fqn(Collection, new[] { element });
            var physical = new TypeNode.Fqn("kotlin.Any");
            var parameter = SelectedParameterHeader.FromSource(new BirParameter { Type = physical, KotlinType = source });
            var forwarded = SourceCollectionArgument(Slots.Single(slot => slot.SlotMember == "dotktContainsAll"),
                parameter, physical, "collection");
            if (forwarded.Name.OrDefault() != "collection")
                throw new InvalidOperationException("Collection slot changed an opaque argument's identity");
        }
        Console.WriteLine("[collection slot values] self-test OK (reference/value elements, owner variables, projections, nested source types)");
    }

    static void QueryParameterSelfTest()
    {
        SourceQueryParameterSelfTest();
        var variable = new TypeNode.Tv("type", 0);
        var nullableReference = new[] { 2 };
        var nonnullReference = new[] { 1 };
        var physicalObject = new TypeNode.Fqn("System.Object");
        if (QueryGuardType(physicalObject, null, nullableReference) is not TypeNode.Nullable { Of: TypeNode.Fqn { Name: "System.Object" } }
            || QueryGuardType(physicalObject, null, nonnullReference) != physicalObject
            || QueryGuardType(variable, null, nullableReference) != variable
            || UsesErasedQueryCarrier(variable) || UsesErasedQueryCarrier(new TypeNode.Fqn("Bound"))
            || !UsesErasedQueryCarrier(new TypeNode.Nullable(physicalObject)))
            throw new InvalidOperationException("Query guard lost reference nullability or changed a generic physical contract");
        Console.WriteLine("[collection query parameters] self-test OK (erased source bound, typed/native signatures)");
    }

    static TypeNode Subst(TypeNode type, TypeNode[] args) => SupertypeGraph.SubstOwnerTvs(type, args);

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
