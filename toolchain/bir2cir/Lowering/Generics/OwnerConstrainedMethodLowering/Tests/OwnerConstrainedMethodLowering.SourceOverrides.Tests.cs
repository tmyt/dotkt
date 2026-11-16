using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OwnerConstrainedMethodLowering
{
    static void OverrideRowsSelfTest()
    {
        BirTypeParameter Parameter(string name, params TypeNode[] constraints) => new() {
            Name = name, Constraints = constraints,
        };
        var ownerVariable = new TypeNode.Tv("type", 0);
        var value = new TypeNode.Fqn("Value");
        var other = new TypeNode.Fqn("Other");
        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 1, 0 }, storageIndices: new[] { 0 });
        var implementation = new BirFunctionDeclaration {
            TypeParams = new[] {
                Parameter("P", value), Parameter("Y", other), Parameter("X", ownerVariable, value),
            },
            NullableRepresentationFrame = frame,
        };
        var ownerArguments = new TypeNode[] { ownerVariable };
        RecordOverride(implementation, new[] { Parameter("X", ownerVariable), Parameter("Y") }, ownerArguments,
            new[] { Parameter("T") }, new[] { Parameter("B", value) }, type => type, sourceParameters: true);
        var plan = implementation._ownerConstraintOverrideBounds.Value;
        if (plan.Rows.Count != 3 || plan.Rows[0].Index != 0 || plan.Rows[0].Bound != value
            || plan.Rows[1].Index != 2 || plan.Rows[1].Bound != ownerVariable
            || plan.Rows[2].Index != 2 || plan.Rows[2].Bound != value
            || plan.Rows.Any(row => row.Template != ownerVariable || row.Consequences.Length != 0
                || row.OwnerArgs.Length != 1 || row.OwnerArgs[0] != ownerVariable))
            throw new InvalidOperationException("Typed override bounds lost selected source/companion positions or their consequence order");
        ownerArguments[0] = other;
        if (plan.Rows.Any(row => row.OwnerArgs[0] != ownerVariable))
            throw new InvalidOperationException("Selected override-bound arguments did not retain their own snapshot");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) OwnerConstraintOverrideBounds.Write(writer, plan);
        var reader = new Utf8JsonReader(output.ToArray());
        if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new InvalidOperationException();
        var decoded = OwnerConstraintOverrideBounds.Read(ref reader);
        if (reader.Read() || decoded.FrozenText != plan.FrozenText || decoded.Rows.Count != 3
            || decoded.Rows[1].Template != ownerVariable)
            throw new InvalidOperationException("Selected override bounds changed their frozen string leaf at the codec boundary");

        // The actual late DOM consumer uses the same scalar carrier and row
        // builder, not a second early-declaration algorithm retained as an oracle.
        var dom = implementation.ToDom();
        var previousReferences = References;
        try
        {
            References = ReferenceMetadataIndex.Build(Array.Empty<string>());
            if (!ProjectsBound(dom, 0, value) || !ProjectsBound(dom, 2, ownerVariable)
                || ProjectsBound(dom, 1, other, _ => false))
                throw new InvalidOperationException("Selected override-bound plan did not reach its live late constraint consumer");
        }
        finally { References = previousReferences; }
        Console.WriteLine("[source override bounds] self-test OK (permuted frame, companions, scalar carrier, late consumer)");
    }
}
