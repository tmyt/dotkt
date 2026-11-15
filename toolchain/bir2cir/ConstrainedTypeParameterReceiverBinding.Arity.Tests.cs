using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void AritySnapshotSelfTest()
    {
        var nested = new BirClassDefinition { Name = "Nested", TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Plain" }, nested,
            new BirClassDefinition { Name = "Duplicate", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            new BirClassDefinition { Name = "Duplicate", TypeParams = Array.Empty<BirTypeParameter>() },
            new BirClassDefinition { Name = "Receiver", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
        } };
        var roots = new[] { root };
        var snapshot = CollectSourceTypeArity(roots);
        if (snapshot.Count != 4 || snapshot["Plain"] != 0 || snapshot["Nested"] != 1
            || snapshot["Duplicate"] != 0 || snapshot["Receiver"] != 1)
            throw new InvalidOperationException("Declaration arities lost placed types or traversal-order overwrite");

        // Each dispatch phase rebuilds its index. A new snapshot observes added/removed/renamed types and changed
        // parameter counts, while the old snapshot remains a value (no retained root or hidden cache).
        nested.Name = "Renamed";
        nested.TypeParams = nested.TypeParams.Value.Append(new BirTypeParameter { Name = "U" }).ToArray();
        nested.CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } };
        root.Types = root.Types.Value.Where((_, index) => index != 3).Append(new BirClassDefinition {
            Name = "Added", TypeParams = new[] { new BirTypeParameter { Name = "A" } },
        }).ToArray();
        var rebuilt = CollectSourceTypeArity(roots);
        if (snapshot.ContainsKey("Renamed") || snapshot.ContainsKey("Added") || snapshot["Nested"] != 1
            || rebuilt.ContainsKey("Nested") || rebuilt["Renamed"] != 3 || rebuilt["Added"] != 1
            || rebuilt["Duplicate"] != 1
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), rebuilt) != null
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), snapshot) == null)
            throw new InvalidOperationException("Declaration arity snapshot was not rebuilt across structural mutation");
        if (CollectSourceTypeArity(Array.Empty<BirFileDeclaration>()).Any())
            throw new InvalidOperationException("Declaration arities leaked into a later compilation");
        ContinuationClosingSelfTest();
        Console.WriteLine("[declaration arities] self-test OK (owning continuation, physical frame, nested/order, mutation/rebuild, isolation)");
    }

    static void ContinuationClosingSelfTest()
    {
        var expected = new TypeNode.Fqn("Nested", new TypeNode[] { new TypeNode.Star() });
        var variable = new TypeNode.Tv("type", 1);
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("Nested"), Method = "run",
            Recv = new BirLocal { Name = "x" }, Args = Array.Empty<IBirExpression>() };
        var accessor = new BirPropertyGet { Type = new TypeNode.Fqn("Nested"),
            Member = "accessor", Accessor = "get_value", Dispatch = "callvirt", Recv = new BirLocal { Name = "x" } };
        var statements = new IBirStatement[] {
            new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = accessor },
        };
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] { new BirParameter { Name = "x", Type = variable } },
            TypeParams = Array.Empty<BirTypeParameter>(), Body = statements };
        // The retained logical bound belongs to source parameter zero, whose
        // ordinary physical slot is one. Do not recover it from the storage
        // slot or an unprojected declaration cloned by the old DOM wrapper.
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var facts = new SourceTypeFacts(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { expected },
        }).MergeNullableFrame(frame);
        var owner = new BirClassDefinition { Name = "Receiver", Kind = "class", KotlinSupertypesPre = facts,
            TypeParams = new[] { new BirTypeParameter { Name = "Storage" }, new BirTypeParameter { Name = "T" } },
            Methods = new[] { method } };
        var file = new BirFileDeclaration { FileClass = "ReceiverFile", Types = new IBirTypeDeclaration[] { owner } };
        var parameters = method.Params.Value;
        var methods = owner.Methods.Value;
        CloseOpenOwners(new[] { file });
        CloseTypeOwners(owner);
        CloseTypeOwners(owner);
        CloseMethodOwners(method, owner, Array.Empty<BirTypeParameter>(), refs: null);
        if (call.OwnerType.Value != expected || accessor.Type.Value != expected
            || !call._erasedConstraintDispatch.OrDefault() || !accessor._erasedConstraintDispatch.OrDefault()
            || !ReferenceEquals(method.Body.Value, statements) || !ReferenceEquals(method.Params.Value, parameters)
            || !ReferenceEquals(owner.Methods.Value, methods) || !ReferenceEquals(methods[0], method)
            || !ReferenceEquals(((BirExpressionStatement)statements[0]).Expr.Value, call)
            || !ReferenceEquals(((BirExpressionStatement)statements[1]).Expr.Value, accessor)
            || !ReferenceEquals(owner.KotlinSupertypesPre.Value, facts) || parameters[0].Type.Value != variable)
            throw new InvalidOperationException("Index-free owning continuation lost physical frame, bound or actual declarations");

        // Reenter the actual phase-one owner without a phase-two arity input.
        CloseOpenOwners(new[] { file });
        if (call.OwnerType.Value != expected || accessor.Type.Value != expected)
            throw new InvalidOperationException("Close-only binding changed an already selected physical owner");
    }
}
