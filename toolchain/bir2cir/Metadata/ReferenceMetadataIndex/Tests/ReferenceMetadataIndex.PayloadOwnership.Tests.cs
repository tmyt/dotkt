using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class ReferenceMetadataIndex
{
    static void PayloadOwnershipSelfTest()
    {
        static BirConstant Text(string value) => new() {
            Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(value),
        };
        static string ReadText(IBirExpression expression) => expression is BirConstant {
            Value.Value: BirLiteral.TextLiteral text } ? text.Value : null;
        static string ReadDefault(Dictionary<int, IBirExpression> defaults) =>
            defaults != null && defaults.TryGetValue(0, out var value) ? ReadText(value) : null;
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Reference payload ownership: " + message); }
        var expression = Text("default");
        var values = new Dictionary<int, IBirExpression> { [0] = expression };
        var payload = new BirInlinePayload {
            V = 1, Owner = "kotlin.Ownership", Fqn = "kotlin.Ownership.inline", DeclarationId = "inline-id",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 0) } },
            Ret = new TypeNode.Tv("method", 0), Body = new IBirStatement[] { new BirReturn { Value = Text("inline") } },
        };
        var metadata = new ReferenceDotKtMetadata();
        metadata.KotlinDefaults[new("kotlin.Ownership", "call", 1)] = values;
        metadata.KotlinDefaultsByDeclarationId["default-id"] = values;
        metadata.InlinePayloads["kotlin.Ownership|inline|1|1"] = new() { payload };
        metadata.InlinePayloadsByDeclarationId["inline-id"] = payload;
        using var first = DefaultDemandTestIndex(metadata);
        using var second = DefaultDemandTestIndex(metadata);

        expression.Value = new BirLiteral.TextLiteral("changed input");
        values.Clear();
        payload.Params.Value[0].Type = new TypeNode.Fqn("System.Object");
        ((BirReturn)payload.Body.Value[0]).Value = Text("changed input");
        metadata.KotlinDefaults.Clear(); metadata.KotlinDefaultsByDeclarationId.Clear();
        metadata.InlinePayloads.Clear(); metadata.InlinePayloadsByDeclarationId.Clear();
        foreach (var index in new[] { first, second })
        {
            Check(ReadDefault(index.KotlinDefaultValuesForDeclarationIdentity("default-id")) == "default"
                && ReadDefault(index.KotlinDefaultValuesFor("kotlin.Ownership", "call", 1)) == "default"
                && ReadDefault(index.KotlinDefaultValuesFor(null, "call", 1)) == "default",
                "capture retained the scan input's mutable default map or expression");
            foreach (var imported in new[] {
                index.InlineValueByDeclarationIdentity("inline-id"),
                index.InlineValueCandidates("kotlin.Ownership", "inline", 1, 1).Single(),
                index.OwnerlessInlineValueCandidates("inline", 1, 1).Single(),
            })
            {
                Check(imported.Params.Value[0].Type.Value == new TypeNode.Tv("method", 0)
                    && ReadText(((BirReturn)imported.Body.Value[0]).Value.Value) == "inline",
                    "capture retained a mutable inline body or declaration frame");
                imported.Params.Value[0].Type = new TypeNode.Fqn("System.Object");
                ((BirReturn)imported.Body.Value[0]).Value = Text("changed query");
            }
            var again = index.InlineValueByDeclarationIdentity("inline-id");
            Check(again.Params.Value[0].Type.Value == new TypeNode.Tv("method", 0)
                && ReadText(((BirReturn)again.Body.Value[0]).Value.Value) == "inline",
                "a splice ingredient mutated stored inline authority through another lookup view");
        }
        first.Dispose();
        Check(ReadText(second.KotlinDefaultValuesForDeclarationIdentity("default-id")[0]) == "default",
            "one index's lifetime changed another's captured payload");
        using var following = DefaultDemandTestIndex(metadata);
        Check(following.KotlinDefaultValuesForDeclarationIdentity("default-id") == null
            && following.InlineValueByDeclarationIdentity("inline-id") == null,
            "a subsequent input inherited another compilation's payloads");
        Console.WriteLine("[reference executable payloads] self-test OK (input capture, splice isolation, frames, independent indexes and subsequent input)");
    }
}
