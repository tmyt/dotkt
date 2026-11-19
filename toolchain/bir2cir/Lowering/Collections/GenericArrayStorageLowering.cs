using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Open Kotlin arrays retain the allocation's runtime component, independently of the caller's T or star projection.
// The physical value slot is System.Array; exact native array signatures remain separate facts.
// All access is materialized here as ordinary CLR calls, not reconstructed by ilemit.
static partial class GenericArrayStorageLowering
{
    internal static bool NeedsStorage(TypeNode.Array array) =>
        array.SzArray && array.Rank == 1 && ContainsOpenComponent(array.Elem);

    static bool ContainsOpenComponent(TypeNode type) => type switch {
        // A projection's bound is not the allocation's component: Array<out Any?>
        // can hold an Int32[] just as Array<*> can.
        TypeNode.Tv or TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Nullable n => ContainsOpenComponent(n.Of),
        TypeNode.Oblivious o => ContainsOpenComponent(o.Of),
        TypeNode.Array a => ContainsOpenComponent(a.Elem),
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOpenComponent),
        _ => false,
    };

    static bool ContainsVariable(TypeNode type) => type switch {
        TypeNode.Tv => true,
        TypeNode.Nullable n => ContainsVariable(n.Of),
        TypeNode.Oblivious o => ContainsVariable(o.Of),
        TypeNode.Projection p => ContainsVariable(p.Of),
        TypeNode.Array a => ContainsVariable(a.Elem),
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsVariable),
        _ => false,
    };

    internal static void SelfTest()
    {
        FreshNamesSelfTest();
        var openComponents = new TypeNode[] {
            new TypeNode.Star(), new TypeNode.Tv("method", 0),
            new TypeNode.Projection("out", new TypeNode.Fqn("kotlin.Any")),
            new TypeNode.Projection("in", new TypeNode.Fqn("kotlin.Int")),
            new TypeNode.Array(new TypeNode.Star()),
        };
        if (openComponents.Any(component => !NeedsStorage(new TypeNode.Array(component)))
            || NeedsStorage(new TypeNode.Array(new TypeNode.Fqn("kotlin.Int")))
            || NeedsStorage(new TypeNode.Array(new TypeNode.Star(), Rank: 2, SzArray: false)))
            throw new InvalidOperationException("Open array storage lost its component or native-shape distinction");
        // Exercise the actual source renderer, not a test-only DOM helper.
        // Exact opaque storage succeeds directly; all other allocations retain
        // the selected source classifier over their actual runtime component.
        var component = new TypeNode.Fqn("OpaqueComponent", new TypeNode[] { new TypeNode.Star() });
        var classifier = new BirConstant { Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(false) };
        var operation = new BirCast { Type = new TypeNode.Array(component),
            E = new BirLocal { Name = "array", Sty = new TypeNode.Fqn("System.Object") } };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var check = LowerTypeOperation(operation, operation.Type.Value, null, null, refs,
            _ => new TypeNode.Fqn("System.Object"), _ => false,
            new BirTemporaryNames(operation), (expected, _) => expected == component ? classifier : null);
        var exact = false;
        check.Visit(node => {
            if (node is BirConditional { Cond.Value: BirObjectEquals {
                Lhs.Value: BirNativeInstance { Method.Value: "GetElementType", MemberRef.HasValue: true },
                Rhs.Value: BirClassReference { Type.Value: TypeNode.Fqn { Name: "System.Object" } } },
                Then.Value: BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } } } selected
                && ReferenceEquals(selected.Else.Value, classifier)) exact = true;
        });
        if (!exact)
            throw new InvalidOperationException("Erased array component lost exact storage or classifier checks");
        Console.WriteLine("[array component classifiers] self-test OK (exact opaque storage, retained native classifier)");
    }

}
