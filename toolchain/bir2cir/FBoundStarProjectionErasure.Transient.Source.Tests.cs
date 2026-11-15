using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    internal static void TransientSourceSelfTest()
    {
        var markers = new HashSet<string>(StringComparer.Ordinal) {
            "_exactBridgeCast", "reifiedTypeOperand", "_exactExistentialBridgeOwnerCall",
            "outer", "delegationOuterSlot", "_nativeLocation", "_nativeFieldAddress",
            "_physicalDeclarationSignature", "_innerFactoryArguments",
        };
        BirGraphValue[] values = {
            new BirCast { ReifiedTypeOperand = false, _physicalDeclarationSignature = true,
                _exactBridgeCast = OptionalField<bool>.Null },
            new BirInstanceOrNull { ReifiedTypeOperand = true },
            new BirNullableWrap { _physicalDeclarationSignature = true },
            new BirCallStatic { _innerFactoryArguments = true },
            new BirCallInstance { _innerFactoryArguments = true, _exactExistentialBridgeOwnerCall = true,
                _physicalDeclarationSignature = true },
            new BirFieldGet { Outer = true, _nativeLocation = true, _nativeFieldAddress = true },
            new BirStaticFieldGet { _nativeLocation = true, _nativeFieldAddress = true },
            new BirFieldSetExpression { Outer = true }, new BirParameter { Outer = true },
            new BirFunctionDeclaration { _physicalDeclarationSignature = true, Name = "retained" },
            new BirConstructor { DelegationOuterSlot = 2 },
            new BirFieldDeclaration { Outer = true, _nativeLocation = true },
            new BirRefTypeDeclaration { _nativeLocation = true },
            new BirCaptureDescriptor { Outer = true }, new BirLambdaParameter { Outer = true },
            new BirInlineCapture { Outer = true }, new BirNullableValue { _physicalDeclarationSignature = true },
            new BirSetField { Outer = true }, new BirVariable { Outer = true },
            new BirLocal { Outer = true }, new BirThis { Outer = true },
            new BirConstrainedCall { _exactExistentialBridgeOwnerCall = true },
        };
        var modeledKinds = typeof(BirGraphValue).Assembly.GetTypes().Where(type => !type.IsAbstract
            && typeof(BirGraphValue).IsAssignableFrom(type) && type.GetProperties().Any(property =>
                markers.Contains(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]))).ToHashSet();
        if (!modeledKinds.SetEquals(values.Select(value => value.GetType())))
            throw new InvalidOperationException("Carrier transport coverage no longer matches the modeled owning kinds");
        // JSON is inspection only: the operation itself consumes actual typed
        // fields. No graph is converted or read back to continue lowering.
        JsonDocument Inspect(BirGraphValue value)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) value.WriteJson(writer);
            return JsonDocument.Parse(bytes.ToArray());
        }
        foreach (var value in values)
        {
            using var before = Inspect(value);
            if (!before.RootElement.EnumerateObject().Any(property => markers.Contains(property.Name)))
                throw new InvalidOperationException("Transient fixture carries none of its modeled source facts");
            ConsumeCarrierTransportFacts(value);
            using var after = Inspect(value);
            if (after.RootElement.EnumerateObject().Any(property => markers.Contains(property.Name)))
                throw new InvalidOperationException("A completed carrier transport fact survived on its owning kind");
        }
        var local = new BirLocal { Name = "same", Outer = true };
        var cast = new BirCast { E = local, _exactBridgeCast = true, FunctionValueRestoration = true };
        ConsumeCarrierTransportFacts(cast);
        if (!ReferenceEquals(cast.E.Value, local) || local.Outer.IsPresent || local.Name.Value != "same"
            || !cast.FunctionValueRestoration.OrDefault()
            || ((BirFunctionDeclaration)values[9]).Name.Value != "retained")
            throw new InvalidOperationException("Transport consumption changed an operand or an unrelated fact");
        Console.WriteLine("[typed carrier transport] self-test OK (nine facts, owning kinds, presence and retained operands)");
    }
}
