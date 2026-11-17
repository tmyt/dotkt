using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Toolchain;

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
        var signature = new SignatureKey(new[] { new TypeKey(TypeKeyKind.String) });
        var relaxed = new SignatureKey(new[] { new TypeKey(TypeKeyKind.Object) });
        var missing = new SignatureKey(new[] { new TypeKey(TypeKeyKind.Int32) });
        var exactValues = new Dictionary<int, IBirExpression> { [0] = Text("signature") };
        var relaxedValues = new Dictionary<int, IBirExpression> { [0] = Text("relaxed") };
        metadata.KotlinDefaults[new("kotlin.Ownership", "call", 1, signature)] = exactValues;
        metadata.KotlinDefaults[new("kotlin.Ownership", "call", 1, relaxed, Relaxed: true)] = relaxedValues;
        metadata.InlinePayloads["kotlin.Ownership|inline|1|1"] = new() { payload };
        metadata.InlinePayloadsByDeclarationId["inline-id"] = payload;
        using var first = DefaultDemandTestIndex(metadata);
        using var second = DefaultDemandTestIndex(metadata);

        expression.Value = new BirLiteral.TextLiteral("changed input");
        values.Clear();
        exactValues[0] = Text("changed input"); relaxedValues.Clear();
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
            Check(ReadDefault(index.KotlinDefaultValuesFor("kotlin.Ownership", "call", 1, signature, relaxed)) == "signature"
                && ReadDefault(index.KotlinDefaultValuesFor("kotlin.Ownership", "call", 1, missing, relaxed)) == "relaxed",
                "signature/relaxed capture changed lookup precedence or retained input aliases");
            var ownerless = index.KotlinDefaultValuesFor(null, "call", 1);
            ((BirConstant)ownerless[0]).Value = new BirLiteral.TextLiteral("changed query");
            ownerless.Clear();
            Check(ReadDefault(index.KotlinDefaultValuesForDeclarationIdentity("default-id")) == "default"
                && ReadDefault(index.KotlinDefaultValuesFor("kotlin.Ownership", "call", 1)) == "default",
                "materializing an ownerless default changed an identity/owner lookup view");
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
        using var following = DefaultDemandTestIndex(metadata);
        Check(following.KotlinDefaultValuesForDeclarationIdentity("default-id") == null
            && following.InlineValueByDeclarationIdentity("inline-id") == null,
            "a subsequent input inherited another compilation's payloads");
        var left = new ReferenceDotKtMetadata();
        var right = new ReferenceDotKtMetadata();
        foreach (var (method, a, b) in new[] { ("same", "equal", "equal"), ("different", "left", "right"),
                     (CtorKeyName, "left ctor", "right ctor") })
        {
            left.KotlinDefaults[new("fold.Left", method, 1)] = new() { [0] = Text(a) };
            right.KotlinDefaults[new("fold.Right", method, 1)] = new() { [0] = Text(b) };
        }
        var conflicted = new DefaultKey("fold.Left", "conflicted", 1);
        left.KotlinDefaults[conflicted] = new() { [0] = Text("unused conflict") };
        left.KotlinDefaultsConflicted.Add(conflicted);
        var inputs = new List<ReferenceAssembly> { new("left.dll", "left", "1", left), new("right.dll", "right", "1", right) };
        foreach (var order in new[] { inputs, inputs.AsEnumerable().Reverse().ToList() })
        {
            using var folded = new ReferenceMetadataIndex(order,
                ManagedReferenceCatalog.Create(Array.Empty<string>(), "payload-fold-test"));
            Check(ReadDefault(folded.KotlinDefaultValuesFor(null, "same", 1)) == "equal"
                && !folded.KotlinDefaultsAmbiguous(null, "same", 1),
                "equal independent payload maps became ambiguous across inputs");
            foreach (var method in new[] { "different", "conflicted" })
                Check(folded.KotlinDefaultValuesFor(null, method, 1) == null
                    && folded.KotlinDefaultsAmbiguous(null, method, 1),
                    "a differing/conflicted ownerless fold selected one input");
            Check(folded.KotlinDefaultsAmbiguous("fold.Left", "conflicted", 1)
                && folded.KotlinDefaultValuesFor("fold.Left", "conflicted", 1) == null
                && ReadDefault(folded.KotlinDefaultValuesFor("fold.Left", CtorKeyName, 1)) == "left ctor"
                && ReadDefault(folded.KotlinDefaultValuesFor("fold.Right", CtorKeyName, 1)) == "right ctor"
                && folded.KotlinDefaultValuesFor(null, CtorKeyName, 1) == null
                && !folded.KotlinDefaultsAmbiguous(null, CtorKeyName, 1),
                "ownerful conflict or constructor separation changed during capture/folding");
        }
        Console.WriteLine("[reference executable payloads] self-test OK (input/query isolation, frames, signature/relaxed/ownerless views, conflict folds, constructors and independent indexes)");
    }
}
