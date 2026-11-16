using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Classify faithful Kotlin forIn sources before range/enumeration realization.
// The source type and explicit reference/hierarchy facts own the decision.
static partial class ForInLowering
{
    const string IntRangeFqn = "kotlin.ranges.IntRange";
    const string IntProgressionFqn = "kotlin.ranges.IntProgression";
    static int _tmp;
    static readonly HashSet<string> CollectionFqns = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Iterable", "kotlin.collections.MutableIterable",
        "kotlin.collections.Collection", "kotlin.collections.MutableCollection",
        "kotlin.collections.List", "kotlin.collections.MutableList",
        "kotlin.collections.Set", "kotlin.collections.MutableSet", "kotlin.sequences.Sequence",
    };
    // The counted for operation is Int32. Wider progressions retain their
    // authored iterator fallback, rather than acquiring width-mismatched IL.
    static readonly HashSet<string> DownToProgressionFqns = new(StringComparer.Ordinal)
    { "kotlin.ranges.IntProgression" };

    public static BirFileDeclaration Apply(BirFileDeclaration root, bool stdlibBuild,
        IReadOnlyDictionary<string, List<string>> typeSupers, HashSet<string> localTopLevelFns,
        ReferenceMetadataIndex refs)
    {
        Walk(root);
        return root;

        // Preorder preserves outer-before-inner allocation and does not walk
        // the fallback of a loop whose source has selected a different form.
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            var current = node is BirForIn loop
                ? Rewrite(loop, stdlibBuild, typeSupers, localTopLevelFns, refs) : node;
            current.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return current;
        }
    }

    static BirGraphValue Rewrite(BirForIn loop, bool stdlibBuild,
        IReadOnlyDictionary<string, List<string>> typeSupers, HashSet<string> localTopLevelFns,
        ReferenceMetadataIndex refs)
    {
        var type = loop.SrcType.OrDefault();
        if (IsCountedRange(type, stdlibBuild)) return BuildForRange(loop);
        if (!stdlibBuild && TryBuildDownTo(loop, localTopLevelFns) is { } descending) return descending;
        // Kotlin-variant values retain their authored iterator family.
        if (OpaqueEnumerable(type, refs) && loop.Fallback.HasValue) return loop.Fallback.Value;
        if (IsNetOrSequenceEnumerable(type, refs) || stdlibBuild && IsStdlibCollection(type, typeSupers))
            return BuildForEachInline(loop, refs);
        return loop.Fallback.HasValue ? loop.Fallback.Value : loop;
    }

    static bool IsNetOrSequenceEnumerable(TypeNode type, ReferenceMetadataIndex refs)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        if (type is not TypeNode.Fqn owner) return false;
        var fqn = ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name);
        return fqn == "kotlin.sequences.Sequence"
            || refs != null && refs.ResolveNetType(fqn, owner.Args?.Length ?? 0) != null;
    }

    static bool IsCountedRange(TypeNode type, bool stdlibBuild) => type is TypeNode.Fqn owner
        && (owner.Name == IntRangeFqn || stdlibBuild && owner.Name == IntProgressionFqn);

    static bool IsStdlibCollection(TypeNode type, IReadOnlyDictionary<string, List<string>> typeSupers)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        if (type is not TypeNode.Fqn owner) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Walk(owner.Name);
        bool Walk(string name) => seen.Add(name) && (CollectionFqns.Contains(name)
            || typeSupers.TryGetValue(name, out var parents) && parents.Any(Walk));
    }

    static BirBlock TryBuildDownTo(BirForIn loop, HashSet<string> localTopLevelFns)
    {
        if (loop.SrcType.OrDefault() is not TypeNode.Fqn owner || !DownToProgressionFqns.Contains(owner.Name)
            || loop.Src.OrDefault() is not BirCallStatic source || source.Owner.HasValue
            || source.Method.OrDefault() != "downTo" || source.Args.OrDefault() is not { Length: 2 } args
            || source.Sig.OrDefault() is not { Length: 2 } sig || localTopLevelFns.Contains("downTo"))
            return null;
        var id = System.Threading.Interlocked.Increment(ref _tmp);
        var from = "$downTo$" + id + "$from";
        var to = "$downTo$" + id + "$to";
        return new BirBlock { Body = new IBirStatement[] {
            new BirVariable { Name = from, Type = OptionalField<TypeNode>.Of(sig[0]), Init = OptionalField<IBirExpression>.Of(args[0]) },
            new BirVariable { Name = to, Type = OptionalField<TypeNode>.Of(sig[1]), Init = OptionalField<IBirExpression>.Of(args[1]) },
            new BirFor {
                Label = loop.Label.IsPresent ? loop.Label : OptionalField<string>.Null,
                Var = loop.Var.IsPresent ? loop.Var : OptionalField<string>.Null,
                From = OptionalField<IBirExpression>.Of(new BirLocal { Name = from }),
                To = OptionalField<IBirExpression>.Of(new BirLocal { Name = to }),
                Cmp = ">=", Step = -1, Body = loop.Body.IsPresent ? loop.Body : OptionalField<IBirStatement[]>.Null,
            },
        } };
    }

    static BirForRange BuildForRange(BirForIn loop) => new() {
        Label = loop.Label.IsPresent ? loop.Label : OptionalField<string>.Null,
        Var = loop.Var.IsPresent ? loop.Var : OptionalField<string>.Null,
        Range = loop.Src.IsPresent ? loop.Src : OptionalField<IBirExpression>.Null,
        RangeType = loop.SrcType.IsPresent ? loop.SrcType : OptionalField<TypeNode>.Null,
        Body = loop.Body.IsPresent ? loop.Body : OptionalField<IBirStatement[]>.Null,
    };

    static BirForEach BuildForEachInline(BirForIn loop, ReferenceMetadataIndex refs) => new() {
        Label = loop.Label.IsPresent ? loop.Label : OptionalField<string>.Null,
        Elem = loop.Elem.IsPresent ? loop.Elem : OptionalField<TypeNode>.Null,
        Src = loop.Src.IsPresent ? loop.Src : OptionalField<IBirExpression>.Null,
        Var = loop.Var.IsPresent ? loop.Var : OptionalField<string>.Null,
        Body = loop.Body.IsPresent ? loop.Body : OptionalField<IBirStatement[]>.Null,
        ErasedEnumeration = OpaqueEnumerable(loop.SrcType.OrDefault(), refs),
    };

    static bool OpaqueEnumerable(TypeNode type, ReferenceMetadataIndex refs) => type switch {
        TypeNode.Nullable nullable => OpaqueEnumerable(nullable.Of, refs),
        TypeNode.Oblivious oblivious => OpaqueEnumerable(oblivious.Of, refs),
        TypeNode.Fqn owner => AliasVarianceRepresentation.RequiresErasureFromSource(owner, refs, null),
        _ => false,
    };
}
