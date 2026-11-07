using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class OwnerConstrainedMethodLowering
{
    static void BodyOwnerViewSelfTest()
    {
        var previousReferences = References;
        try
        {
            References = ReferenceMetadataIndex.Build(Array.Empty<string>());
            JsonObject Frame() => JsonNode.Parse("""
              {"typeParams":[{"name":"M","constraints":[
                {"t":"fqn","name":"MethodOwner","args":[{"t":"tv","scope":"type","i":0}]}]}]}
              """).AsObject();
            JsonObject Owner(string argument) => JsonNode.Parse($$"""
              {"typeParams":[{"name":"T","constraints":[
                {"t":"fqn","name":"TypeOwner","args":[{"t":"fqn","name":"{{argument}}"}]}]}]}
              """).AsObject();
            JsonObject Call(string owner, string name) => new() {
                ["k"] = "callInstance", ["ownerType"] = TypeJson.Fqn(owner), ["method"] = "read",
                [DeclarationIdentityBinding.Key] = "selected-" + owner,
                ["recv"] = new JsonObject { ["k"] = "local", ["name"] = name },
            };
            JsonObject Body() => new() {
                ["k"] = "newSuspendLambda",
                ["params"] = JsonNode.Parse("""
                  [{"name":"m","type":{"t":"tv","scope":"method","i":0}},
                   {"name":"t","type":{"t":"tv","scope":"type","i":0}}]
                  """),
                ["body"] = new JsonArray(Call("MethodOwner", "m"), Call("TypeOwner", "t"),
                    new JsonObject { ["k"] = "newSuspendLambda", ["params"] = new JsonArray(),
                        ["capValues"] = new JsonArray(Call("MethodOwner", "m")),
                        ["body"] = new JsonArray(Call("MethodOwner", "m")) }),
            };
            void Expect(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException("Body owner closing: " + message);
            }

            var parent = new JsonArray();
            foreach (var argument in new[] { "First", "Second" })
            {
                var bodyOwner = Body();
                parent.Add(bodyOwner);
                var originalBody = bodyOwner["body"];
                var originalParameters = bodyOwner["params"];
                var frame = Frame();
                var owner = Owner(argument);
                var originalFrame = frame.ToJsonString();
                var originalOwner = owner.ToJsonString();
                CloseBodyOwnerViews(bodyOwner, frame, owner,
                    FBoundStarProjectionErasure.ContainsOwnerTv, type => type);
                Expect(ReferenceEquals(originalBody, bodyOwner["body"])
                    && ReferenceEquals(originalParameters, bodyOwner["params"])
                    && ReferenceEquals(originalBody.Parent, bodyOwner) && ReferenceEquals(bodyOwner.Parent, parent),
                    "body or parameters were copied, detached or reparented");
                Expect(frame.ToJsonString() == originalFrame && owner.ToJsonString() == originalOwner,
                    "projected constraints mutated the donor declarations");
                Expect(TypeJson.Read(originalBody[0]["ownerType"]) == new TypeNode.Fqn("MethodOwner",
                        new TypeNode[] { new TypeNode.Tv("type", 0) })
                    && TypeJson.Read(originalBody[1]["ownerType"]) == new TypeNode.Fqn("TypeOwner",
                        new TypeNode[] { new TypeNode.Fqn(argument) })
                    && originalBody[0][DeclarationIdentityBinding.Key].GetValue<string>() == "selected-MethodOwner",
                    "method/type frames or selected declaration identity were lost");
                Expect(TypeJson.Read(originalBody[2]["capValues"][0]["ownerType"])
                        == TypeJson.Read(originalBody[0]["ownerType"])
                    && TypeJson.Read(originalBody[2]["body"][0]["ownerType"]) == new TypeNode.Fqn("MethodOwner"),
                    "nested body used the enclosing frame, or capture operands were skipped");

                // With no method frame, type-owner binding is still required. A sibling's bound must not leak.
                var typeOnly = new JsonObject { ["params"] = originalParameters.DeepClone(),
                    ["body"] = new JsonArray(Call("TypeOwner", "t")) };
                CloseBodyOwnerViews(typeOnly, new JsonObject(), owner, _ => false, type => type);
                Expect(TypeJson.Read(typeOnly["body"][0]["ownerType"])
                    == TypeJson.Read(originalBody[1]["ownerType"]), "empty method frame suppressed type binding");
            }
            Expect(TypeJson.Read(parent[0]["body"][1]["ownerType"]) != TypeJson.Read(parent[1]["body"][1]["ownerType"]),
                "sibling frames were shared");

            // The body can carry unrelated method metadata. Only the selected declaration frame owns the
            // override-bound substitution; reading the lambda's metadata would select the wrong owner argument.
            var metadataBody = Body();
            var metadataFrame = Frame();
            string Override(string argument) => new JsonArray(new JsonObject {
                ["index"] = 0,
                ["bound"] = metadataFrame["typeParams"][0]["constraints"][0].DeepClone(),
                ["template"] = metadataFrame["typeParams"][0]["constraints"][0].DeepClone(),
                ["ownerArgs"] = new JsonArray(TypeJson.Fqn(argument)), ["consequences"] = new JsonArray(),
            }).ToJsonString();
            metadataFrame[OverrideBoundsKey] = Override("DeclarationArgument");
            metadataBody[OverrideBoundsKey] = Override("BodyArgument");
            CloseBodyOwnerViews(metadataBody, metadataFrame, Owner("Outer"),
                FBoundStarProjectionErasure.ContainsOwnerTv, type => type);
            Expect(TypeJson.Read(metadataBody["body"][0]["ownerType"]) == new TypeNode.Fqn("MethodOwner",
                new TypeNode[] { new TypeNode.Fqn("DeclarationArgument") }), "lambda metadata replaced declaration-frame metadata");

            var unchanged = new JsonObject { ["params"] = new JsonArray(), ["body"] = new JsonArray(
                new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 42 }) };
            var unchangedBody = unchanged["body"];
            var before = unchanged.ToJsonString();
            CloseBodyOwnerViews(unchanged, new JsonObject(), new JsonObject(), _ => false, type => type);
            Expect(ReferenceEquals(unchangedBody, unchanged["body"]) && unchanged.ToJsonString() == before,
                "no-rewrite body changed");
        }
        finally { References = previousReferences; }
        Console.WriteLine("[body owner closing] self-test OK (in-place, frames, nested/capture, sibling, no-rewrite)");
    }
}
