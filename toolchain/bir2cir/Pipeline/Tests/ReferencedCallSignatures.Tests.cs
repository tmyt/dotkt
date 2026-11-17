using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class ReferencedCallSignatures
{
    internal static void SelfTest()
    {
        static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Module call signatures: " + reason); }
        using var module = new ReferencedCallSignatures();
        using var independent = new ReferencedCallSignatures();
        var arguments = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) };
        var inner = new BirCallStatic { Method = "inner", Sig = Array.Empty<TypeNode>() };
        var outer = new BirCallStatic {
            Method = "outer", Sig = new TypeNode[] { new TypeNode.Fqn("probe.Box", arguments) },
            Args = new IBirExpression[] { inner },
        };
        var payloadCall = new BirCallStatic { Method = "opaque", Sig = Array.Empty<TypeNode>() };
        var payload = new BirInlinePayload { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = payloadCall },
        } };
        module.Capture(outer);
        module.Capture(payload);
        var outerId = outer.DotktKotlinSigId.Value;
        var innerId = inner.DotktKotlinSigId.Value;
        var snapshot = module.Read(outer);
        arguments[0] = new TypeNode.Fqn("kotlin.Int");
        module.Capture(outer);
        Check(outer.DotktKotlinSigId.Value == outerId && inner.DotktKotlinSigId.Value == innerId
            && outerId != innerId && module.Read(inner).Length == 0
            && !payloadCall.DotktKotlinSigId.IsPresent
            && snapshot[0] is TypeNode.Fqn { Args: [TypeNode.Nullable { Of: TypeNode.Tv { Scope: "method", I: 0 } }] },
            "capture lost independent pre-erasure facts, nested calls, idempotence or payload opacity");
        var cold = new BirCallStatic();
        module.ProjectColdCall(outer, cold, new TypeNode.Fqn("probe.Completion"));
        var projected = module.Read(cold);
        Check(cold.DotktKotlinSigId.Value != outerId && projected.Length == 2
            && TypeNodeDataEquality.Equal(projected[0], snapshot[0]) && !ReferenceEquals(projected[0], snapshot[0])
            && projected[1] == new TypeNode.Fqn("probe.Completion") && module.Read(outer).Length == 1,
            "cold projection changed the hot signature or shared mutable facts");
        ((TypeNode.Fqn)projected[0]).Args[0] = new TypeNode.Fqn("kotlin.Long");
        Check(module.Read(cold)[0] is TypeNode.Fqn { Args: [TypeNode.Nullable] },
            "a query caller mutated the catalog");

        // A genuine independent graph clone can be admitted in another file.
        // Its token is the same, but its continuing graph has a different owner.
        var sibling = (BirCallStatic)outer.DeepClone();
        module.ReleaseMarkers(outer);
        Check(!outer.DotktKotlinSigId.IsPresent && !inner.DotktKotlinSigId.IsPresent
            && module.Read(sibling)[0] is TypeNode.Fqn { Args: [TypeNode.Nullable] }
            && module.Read(cold).Length == 2,
            "one root's release invalidated another root's clone or cold projection");
        module.ProjectColdCall(sibling, new BirCallStatic(), new TypeNode.Fqn("probe.LateCompletion"));
        module.ReleaseMarkers(sibling);
        module.ReleaseMarkers(cold);
        Check(module.Read(sibling) == null && !cold.DotktKotlinSigId.IsPresent,
            "resolved roots retained internal source tokens");

        var unrelated = new BirCallStatic { Sig = new TypeNode[] { new TypeNode.Fqn("probe.Other") } };
        independent.Capture(unrelated);
        Check(unrelated.DotktKotlinSigId.Value == outerId && independent.Read(unrelated)[0] == new TypeNode.Fqn("probe.Other"),
            "another compilation inherited an allocator or snapshot");
        module.Dispose();
        Check(module.snapshots.Count == 0 && independent.Read(unrelated).Length == 1,
            "module completion retained rows or changed another compilation");
        try { module.Read(unrelated); throw new Exception("Ended module accepted a query"); }
        catch (InvalidOperationException) { }
        using var following = new ReferencedCallSignatures();
        var fresh = new BirCallStatic { Sig = Array.Empty<TypeNode>() };
        following.Capture(fresh);
        Check(fresh.DotktKotlinSigId.Value == outerId && following.Read(fresh).Length == 0,
            "a later compilation inherited process state");
        Console.WriteLine("[source call signatures] self-test OK (module ownership, frozen/cold facts, clone lifetime, release, independent compilations)");
    }
}
