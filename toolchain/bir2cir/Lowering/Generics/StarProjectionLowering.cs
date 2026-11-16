using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static KotlinRuntimeTypeWitness;

// Classifier tests and existential collection operations preserve the original
// Kotlin object while selecting only the physical face required by the operation.
// Native collection faces alone do not prove Kotlin membership: dictionaries
// and arrays expose them too, HashSet has no nongeneric collection face, and a
// Kotlin List implementation need not implement nongeneric IList. Use composite
// classifiers while keeping independent List and Set contracts intact.
static partial class StarProjectionLowering
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    public static bool UsedRuntimeFallback { get; private set; }

    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal) {
        ["kotlin.collections.Iterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.MutableIterable"] = "System.Collections.IEnumerable",
    };
    static readonly Dictionary<string, int> IdentityKind = new(StringComparer.Ordinal) {
        ["kotlin.collections.Collection"] = 0, ["kotlin.collections.Set"] = 1,
        ["kotlin.collections.MutableSet"] = 2, ["kotlin.collections.List"] = 3,
        ["kotlin.collections.MutableList"] = 4, ["kotlin.collections.MutableCollection"] = 5,
        ["kotlin.collections.Map"] = 6, ["kotlin.collections.MutableMap"] = 7,
    };

    internal static bool IsNonGenericCollectionFacade(TypeNode type) =>
        type is TypeNode.Fqn { Args: null } named && NonGenericIface.Values.Contains(named.Name);

    static bool IsStarCollection(TypeNode type, out string physical)
    {
        physical = null;
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        return type is TypeNode.Fqn named && NonGenericIface.TryGetValue(named.Name, out physical)
            && (named.Args == null || named.Args.All(IsObjectArg));
    }
    static bool IsIdentityCollection(TypeNode type, out int kind, out bool nullable)
    {
        kind = -1; nullable = false;
        while (true)
        {
            if (type is TypeNode.Nullable n) { nullable = true; type = n.Of; continue; }
            if (type is TypeNode.Oblivious o) { type = o.Of; continue; }
            break;
        }
        return type is TypeNode.Fqn named && IdentityKind.TryGetValue(named.Name, out kind)
            && (named.Args == null || named.Args.All(IsObjectArg));
    }
    static bool IsObjectArg(TypeNode type) => type switch {
        TypeNode.Star => true, TypeNode.Nullable n => IsObjectArg(n.Of), TypeNode.Oblivious o => IsObjectArg(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true, _ => false,
    };
    static TypeNode StripOuterWrappers(TypeNode type) => type switch {
        TypeNode.Nullable n => StripOuterWrappers(n.Of), TypeNode.Oblivious o => StripOuterWrappers(o.Of), _ => type,
    };
    static bool ContainsProjection(TypeNode type) => type switch {
        TypeNode.Star or TypeNode.Projection => true, TypeNode.Fqn f => f.Args?.Any(ContainsProjection) == true,
        TypeNode.Nullable n => ContainsProjection(n.Of), TypeNode.Oblivious o => ContainsProjection(o.Of),
        TypeNode.Array a => ContainsProjection(a.Elem), _ => false,
    };
    static bool HasConcreteTypeArguments(TypeNode type) => StripOuterWrappers(type) is TypeNode.Fqn { Args.Length: > 0 } named
        && !ContainsProjection(named);
    static bool IsMutableStarCollection(TypeNode type)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type is TypeNode.Fqn { Name: "kotlin.collections.MutableIterable" };
    }

    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        var views = ForeignStarProjectionBinding.CollectClosedViewHints(new[] { root }, refs);
        root.VisitChildSlots(slot => Walk(slot, new CollectionHelperBinding.CollectionSourceContext()));

        void Walk(BirGraphSlot slot, CollectionHelperBinding.CollectionSourceContext context)
        {
            var node = slot.Value;
            if (node == null || node is IBirExecutablePayload) return;
            context = context.Extend(node);
            if (node is BirCallInstance call)
            {
                if (call.OwnerType.OrDefault() is TypeNode.Fqn owner)
                {
                    var element = MemberCallSubstitution.CollectionElement(owner, context.Receiver(call.Recv.OrDefault()), context.Constraints, refs);
                    var plan = MemberCallSubstitution.SelectStaticCollectionOperation(call.Method.OrDefault(), call.Prop.OrDefault(),
                        call.Args.OrEmpty().Length, call.Recv.HasValue, owner, element, null);
                    if (plan != null)
                    {
                        var operation = CollectionHelperBinding.RenderCollectionOperation(plan, call, refs, MemberCallSubstitution.CollectionOperationPhase.Physical);
                        MemberCallSubstitution.BindAuthoredHelper(operation, refs, context, refs.IsValueType);
                        slot.Replace(operation); Children(operation, context); return;
                    }
                }
                if (IsIdentityCollection(call.OwnerType.OrDefault(), out var kind, out _))
                {
                    if (kind is 0 or 1 or 2 or 5 && call.Method.OrDefault() == "size" && call.Prop.OrDefault() == "get"
                        && LowerCollectionCount(call, kind, views) is { } count)
                    { UsedRuntimeFallback = true; slot.Replace(count); Children(count, context); return; }
                    if (kind is 3 or 4 && LowerListMember(call, kind, views) is { } member)
                    { UsedRuntimeFallback = true; slot.Replace(member); Children(member, context); return; }
                    if (call.Recv.OrDefault() is BirCast cast && IsIdentityCollection(cast.Type.OrDefault(), out var castKind, out _)
                        && !HasConcreteTypeArguments(cast.Type.OrDefault()) && LowerIdentityMember(call, cast, castKind, refs, context) is { } identity)
                    { UsedRuntimeFallback = true; slot.Replace(identity); Children(identity, context); return; }
                }
                if (IsStarCollection(call.OwnerType.OrDefault(), out _) && call.Recv.OrDefault() is BirCast receiver
                    && IsStarCollection(receiver.Type.OrDefault(), out var physical)
                    && LowerIterableMember(call, receiver, physical, IsMutableStarCollection(call.OwnerType.OrDefault())) is { } iterable)
                {
                    slot.Replace(iterable);
                    // This branch's authored operands are final, as on the DOM path.
                    iterable.VisitOwnChildSlots((field, _, child) => { if (field is "recv" or "args") Walk(child, context); });
                    return;
                }
            }
            if (ClassifierOperation.Admits(node))
            {
                var operation = new ClassifierOperation(node);
                if (IsStarCollection(operation.Type.OrDefault(), out var physical) && (operation.IsTest || node is BirInstanceOrNull && physical == "System.Collections.IEnumerable"))
                    operation.Type = new TypeNode.Fqn(physical);
                if (node is BirCast cast && IsStarCollection(cast.Type.OrDefault(), out var castPhysical))
                {
                    cast.Type = new TypeNode.Fqn(castPhysical);
                    // Preserve semantic provenance after the raw facade loses
                    // its element closure; the later member context consumes it.
                    cast.DotktProjectedCollection = true;
                }
                if (operation.Operand.HasValue && IsIdentityCollection(operation.Type.OrDefault(), out var kind, out var nullable))
                {
                    UsedRuntimeFallback = true;
                    node = LowerIdentityClassifier(operation.IsTest ? "isInst" : operation.IsCast ? "cast" : "isInstRef",
                        operation.Operand.Value, kind, nullable || node is BirIsInstance test && test.NullMatches.OrDefault(), operation.Type.Value);
                    slot.Replace(node);
                }
            }
            Children(node, context);
        }
        void Children(BirGraphValue node, CollectionHelperBinding.CollectionSourceContext context) => node.VisitChildSlots(child => Walk(child, context));
    }

    static BirGraphValue LowerIdentityClassifier(string kind, IBirExpression operand, int classifier, bool nullable, TypeNode target)
    {
        var method = kind switch {
            "isInst" when nullable => "starProjectionKotlinNullableCollectionIsInstance", "isInst" => "starProjectionKotlinCollectionIsInstance",
            "isInstRef" => "starProjectionKotlinCollectionSafeCast", "cast" when nullable => "starProjectionKotlinNullableCollectionCast",
            _ => "starProjectionKotlinCollectionCast",
        };
        var first = classifier switch {
            0 => "System.Collections.Generic.IReadOnlyCollection`1", 1 => "System.Collections.Generic.IReadOnlySet`1",
            2 => "System.Collections.Generic.ISet`1", 3 => "System.Collections.Generic.IReadOnlyList`1",
            4 => "System.Collections.Generic.IList`1", 5 => "System.Collections.Generic.ICollection`1",
            6 or 7 => "System.Collections.Generic.IDictionary`2", _ => throw new InvalidOperationException("Unknown collection classifier"),
        };
        var second = classifier switch {
            0 or 5 => "System.Collections.Generic.ICollection`1", 3 or 4 => "System.Collections.Generic.IList`1",
            6 or 7 => "System.Collections.Generic.IDictionary`2", _ => "System.Collections.Generic.ISet`1",
        };
        var call = Call(method, new[] { AnyN, Int, Type, Type }, kind == "isInst" ? Bool : kind == "isInstRef" || nullable ? AnyN : Any,
            CopyExpression(operand), ConstInt(classifier), ClassRef(first), ClassRef(second));
        // Any is a concrete closure, not a star. Keep its physical value cast
        // after the membership guard instead of discarding that exact interface.
        if (kind != "isInst" && HasConcreteTypeArguments(target))
            return kind == "cast" ? new BirCast { Type = BirGraphValue.CopyType(target), E = call }
                : new BirInstanceOrNull { Type = BirGraphValue.CopyType(target), E = call };
        if (kind != "isInst") call.DotktProjectedCollection = true;
        return call;
    }

    static BirGraphValue LowerIdentityMember(BirCallInstance call, BirCast cast, int kind, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context)
    {
        // Maps use semantic map dispatchers, not the collection iterator protocol.
        if (kind is 6 or 7) return null;
        var receiver = (IBirExpression)LowerIdentityClassifier("cast", cast.E.Value, kind, false, cast.Type.Value);
        return call.Method.OrDefault() switch {
            "size" when call.Prop.OrDefault() == "get" => Call(kind switch {
                0 => "projectedReadOnlyCollectionCountErased", 3 => "projectedListCountErased", 4 => "projectedMutableListCountErased",
                5 => "projectedMutableCollectionCountErased", 1 => "projectedSetCountErased", 2 => "projectedMutableSetCountErased",
                _ => throw new InvalidOperationException("Unknown collection classifier"),
            }, new[] { Any }, Int, CopyExpression(receiver)),
            "isEmpty" => CollectionHelper(MemberCallSubstitution.ProjectedIsEmptyHelper(IdentityKind.Single(pair => pair.Value == kind).Key), Bool, refs, context, receiver),
            "iterator" when kind is 2 or 4 or 5 => IteratorCall(true, receiver),
            "iterator" => IteratorCall(false, Cast("System.Collections.IEnumerable", receiver)), _ => null,
        };
    }
    static BirCallStatic LowerCollectionCount(BirCallInstance call, int kind, IReadOnlyDictionary<string, TypeNode.Fqn> views)
    {
        if (!CanUseErasedReceiver(call, views)) return null;
        return Call(kind switch { 1 => "projectedSetCountErased", 2 => "projectedMutableSetCountErased",
            5 => "projectedMutableCollectionCountErased", _ => "projectedCollectionCountErased" }, new[] { Any }, Int, CopyExpression(call.Recv.Value));
    }
    static bool CanUseErasedReceiver(BirCallInstance call, IReadOnlyDictionary<string, TypeNode.Fqn> views)
        // A source-authored closed view belongs to exact foreign binding. A
        // runtime-only erased helper would discard its selected interface.
        => call.Recv.HasValue && !HasConcreteTypeArguments(call.OwnerType.OrDefault())
            && !(call.Recv.Value is BirCast cast && HasConcreteTypeArguments(cast.Type.OrDefault()))
            && !(call.Recv.Value is BirLocal { Name.HasValue: true } local && views.ContainsKey(local.Name.Value));
    static BirCallStatic LowerListMember(BirCallInstance call, int kind, IReadOnlyDictionary<string, TypeNode.Fqn> views)
    {
        var count = call.Method.OrDefault() == "size" && call.Prop.OrDefault() == "get";
        if (!count && !(call.Method.OrDefault() is "get" or "get_Item" && call.Args.OrEmpty().Length == 1) || !CanUseErasedReceiver(call, views)) return null;
        var receiver = call.Recv.Value is BirCast cast && IsIdentityCollection(cast.Type.OrDefault(), out var receiverKind, out _)
            ? (IBirExpression)LowerIdentityClassifier("cast", cast.E.Value, receiverKind, false, cast.Type.Value) : CopyExpression(call.Recv.Value);
        return count ? Call(kind == 4 ? "projectedMutableListCountErased" : "projectedListCountErased", new[] { Any }, Int, receiver)
            : kind == 4 ? Call("mutableListGetErased", new[] { Any, Int, AnyN }, AnyN, receiver, CopyExpression(call.Args.Value[0]), Null())
            : Call("projectedListGetErased", new[] { Any, Int }, AnyN, receiver, CopyExpression(call.Args.Value[0]));
    }
    static BirCallStatic LowerIterableMember(BirCallInstance call, BirCast cast, string physical, bool mutable)
        // Preserve the observable cast: enumerable storage alone must not make
        // a rejected mutable collection cast succeed.
        => call.Method.OrDefault() == "iterator" ? IteratorCall(mutable, Cast(mutable ? physical : "System.Collections.IEnumerable", CopyExpression(cast.E.Value))) : null;
    static BirCallStatic IteratorCall(bool mutable, IBirExpression receiver) => new() {
        // Kotlin hasNext is idempotent; CLR MoveNext is not. The immutable raw
        // enumerator therefore needs the bridge implementing Kotlin Iterator.
        Owner = new TypeNode.Fqn(mutable ? "kotlin.collections.ClrCollectionDefaultsKt" : "kotlin.collections.ClrIteratorBridgeKt"),
        Method = mutable ? "clrMutableIteratorErased" : "iteratorOverRawEnumerable", Sig = new[] { Any }, Args = new[] { receiver },
        Ret = new TypeNode.Fqn(mutable ? "kotlin.collections.MutableIterator" : "kotlin.collections.Iterator", new[] { AnyN }),
    };
    static BirCallStatic CollectionHelper(string method, TypeNode result, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context, params IBirExpression[] arguments)
    {
        var call = new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.collections.ClrCollectionDefaultsKt"), Method = method,
            Sig = new[] { Any }, TypeArgs = new[] { AnyN }, Ret = result, Args = arguments };
        MemberCallSubstitution.BindAuthoredHelper(call, refs, context, refs.IsValueType);
        return call;
    }
    static BirCallStatic Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result, params IBirExpression[] arguments) => new() {
        Owner = new TypeNode.Fqn(RuntimeOwner), Method = method, Sig = signature.ToArray(), Ret = result, Args = arguments,
    };
    static BirClassReference ClassRef(string type) => new() { Type = new TypeNode.Fqn(type) };
    static BirConstant ConstInt(int value) => new() { Type = Int, Value = new BirLiteral.IntegerLiteral(value) };
    static BirConstant Null() => new() { Type = AnyN, Value = new BirLiteral.NullLiteral() };
    static BirCast Cast(string type, IBirExpression value) => new() { Type = new TypeNode.Fqn(type), E = OptionalField<IBirExpression>.Of(value) };
}
