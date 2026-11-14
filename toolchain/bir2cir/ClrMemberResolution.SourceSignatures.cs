using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // Source overload selection precedes representation erasure. The same
    // allocator/table serves source capture and later cold-call projection.
    internal static void CaptureReferencedStaticCallSignatures(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirCallStatic { Sig.HasValue: true } call && !call.DotktKotlinSigId.HasValue)
            call.DotktKotlinSigId = RememberKotlinSignature(call.Sig.Value);
        node.VisitChildren(CaptureReferencedStaticCallSignatures);
    }

    static int RememberKotlinSignature(IEnumerable<TypeNode> signature)
    {
        var id = ++_nextKotlinSigSnapshotId;
        KotlinSigSnapshots.Add(id, signature.Select(BirGraphValue.CopyType).ToArray());
        return id;
    }

    internal static void SourceSignatureSelfTest()
    {
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
        CaptureReferencedStaticCallSignatures(outer);
        CaptureReferencedStaticCallSignatures(payload);
        var outerId = outer.DotktKotlinSigId.Value;
        var innerId = inner.DotktKotlinSigId.Value;
        var snapshot = KotlinSigSnapshots[outerId];
        try
        {
            arguments[0] = new TypeNode.Fqn("kotlin.Int");
            CaptureReferencedStaticCallSignatures(outer);
            if (outer.DotktKotlinSigId.Value != outerId || inner.DotktKotlinSigId.Value != innerId
                || outerId == innerId || KotlinSigSnapshots[innerId].Length != 0
                || payloadCall.DotktKotlinSigId.IsPresent
                || snapshot[0] is not TypeNode.Fqn { Args: { Length: 1 } saved }
                || saved[0] is not TypeNode.Nullable { Of: TypeNode.Tv { Scope: "method", I: 0 } })
                throw new InvalidOperationException("Source signatures lost their exact independent pre-erasure snapshot");
            var projected = new System.Text.Json.Nodes.JsonObject();
            CarryReferencedStaticCallSignatureSnapshot(outer.ToDom(), projected,
                TypeJson.Write(new TypeNode.Fqn("probe.Completion")));
            var projectedId = projected[KotlinSigSnapshotId].GetValue<int>();
            try
            {
                var cold = KotlinSigSnapshots[projectedId];
                if (projectedId == outerId || cold.Length != 2
                    || !TypeNodeDataEquality.Equal(cold[0], snapshot[0])
                    || ReferenceEquals(cold[0], snapshot[0])
                    || cold[1] != new TypeNode.Fqn("probe.Completion") || snapshot.Length != 1)
                    throw new InvalidOperationException("Cold signature projection changed its hot snapshot or shared mutable type facts");
            }
            finally { KotlinSigSnapshots.Remove(projectedId); }
        }
        finally
        {
            KotlinSigSnapshots.Remove(outerId);
            KotlinSigSnapshots.Remove(innerId);
        }
        Console.WriteLine("[source call signatures] self-test OK (one authority, pre-erasure frame, independent cold projection, opaque payload)");
    }
}
