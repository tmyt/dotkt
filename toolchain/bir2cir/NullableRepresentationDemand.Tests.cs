using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class NullableRepresentationDemand
{
    public static void SelfTest()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Nullable representation self-test: " + message);
        }
        static void Malformed(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Malformed current nullable frame was accepted");
        }

        var frame = new NullableRepresentationFrame(2, new[] { 1 });
        Check(frame.PhysicalArity == 3, "physical arity");
        Check(frame.NullableVariable(new TypeNode.Tv("type", 1)) == new TypeNode.Tv("type", 2), "owner scope");
        Check(frame.NullableVariable(new TypeNode.Tv("method", 1)) == new TypeNode.Tv("method", 2), "method scope");
        var restored = NullableRepresentationFrame.Read(frame.ToJson());
        Check(restored.SourceArity == 2 && restored.NullableIndices.SequenceEqual(new[] { 1 }), "metadata correspondence");
        Check(restored.SemanticVariable(new TypeNode.Tv("method", 2)) == new TypeNode.Nullable(new TypeNode.Tv("method", 1)),
            "physical companion restores nullable source method variable");
        Check(restored.SemanticVariable(new TypeNode.Tv("type", 2)) == new TypeNode.Nullable(new TypeNode.Tv("type", 1)),
            "physical companion restores nullable source owner variable");
        Check(restored.SemanticVariable(new TypeNode.Tv("type", 0)) == new TypeNode.Tv("type", 0),
            "ordinary source variable identity is retained");
        var stringType = new TypeNode.Fqn("kotlin.String");
        var nullableString = new TypeNode.Nullable(stringType);
        var closed = frame.Close(new TypeNode[] { stringType, nullableString },
            _ => new TypeNode.Fqn("object"), source => source);
        Check(closed[2] == nullableString, "nullable closure must receive original source argument");
        Check(restored.OrdinaryArguments(closed).Length == 2, "physical companions are hidden using explicit frame metadata");
        Malformed(() => restored.SemanticVariable(new TypeNode.Tv("type", 3)));
        Malformed(() => restored.OrdinaryArguments(new TypeNode[] { stringType }));
        Malformed(() => new NullableRepresentationFrame(1, new[] { 1 }));
        Malformed(() => new NullableRepresentationFrame(2, new[] { 1, 0 }));
        Malformed(() => new NullableRepresentationFrame(2, new[] { 1, 1 }));
        Malformed(() => NullableRepresentationFrame.Read(new JsonObject { ["sourceArity"] = 1 }));

        static JsonNode Tv(string scope = "type", int index = 0) => TypeJson.Write(new TypeNode.Tv(scope, index));
        static JsonNode NullableTv(string scope = "type") => new JsonObject { ["t"] = "nullable", ["of"] = Tv(scope) };
        static JsonNode Applied(string name, JsonNode argument) => new JsonObject {
            ["t"] = "fqn", ["name"] = name, ["args"] = new JsonArray(argument),
        };
        static JsonObject Owner(string name, JsonNode field) => new() {
            ["kind"] = "class", ["name"] = name, ["typeParams"] = new JsonArray("T"),
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = field }),
        };
        static JsonObject Method(string id, JsonNode result, JsonArray body) => new() {
            ["name"] = id, [DeclarationIdentityBinding.Key] = id,
            ["typeParams"] = new JsonArray("U"), ["params"] = new JsonArray(), ["ret"] = result, ["body"] = body,
        };

        var box = Owner("Box", Tv());
        var exchange = Owner("Exchange", Applied("Box", NullableTv()));
        var wrapper = Owner("Wrapper", Applied("Exchange", Tv()));
        var outer = Owner("Outer", Applied("Wrapper", Tv()));
        var scalar = Owner("Scalar", NullableTv());
        var callee = Method("callee", Applied("Box", NullableTv("method")), new JsonArray());
        var caller = Method("caller", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "callee", ["typeArgs"] = new JsonArray(Tv("method")),
        }));
        var arrayBody = Method("arrayBody", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "newArraySized", ["elem"] = NullableTv("method"),
        }));
        var refBody = Method("refBody", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "byrefLoad", ["elem"] = NullableTv("method"),
        }));
        var callFromCtor = Owner("BodyOnly", Tv());
        callFromCtor["ctors"] = new JsonArray(new JsonObject {
            ["params"] = new JsonArray(), ["body"] = new JsonArray(new JsonObject {
                ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "callee", ["typeArgs"] = new JsonArray(Tv()),
            }),
        });
        var file = new JsonObject {
            ["fileClass"] = "FrameTests", ["types"] = new JsonArray(outer, wrapper, exchange, box, scalar, callFromCtor),
            ["methods"] = new JsonArray(caller, callee, arrayBody, refBody),
        };
        var original = file.ToJsonString();
        var demands = Collect(new[] { file });
        OwnerDemand Find(JsonObject declaration) => demands.Single(owner => ReferenceEquals(owner.Declaration, declaration));
        Check(Find(box).Frame.PhysicalArity == 1, "ordinary invariant type stays exact");
        Check(Find(scalar).Frame.PhysicalArity == 1, "direct nullable scalar does not require reified argument frame");
        Check(Find(exchange).Frame.NullableIndices.SequenceEqual(new[] { 0 }), "direct constructed demand");
        Check(Find(outer).Frame.NullableIndices.SequenceEqual(new[] { 0 }), "transitive construction fixed point");
        var callerDemand = Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, caller));
        Check(callerDemand.Frame.PhysicalArity == 1 && callerDemand.Body.Method.SetEquals(new[] { 0 }),
            "body-only call demand must not alter virtual declaration arity");
        Check(Find(callFromCtor).Frame.PhysicalArity == 1 && Find(callFromCtor).Body.Type.SetEquals(new[] { 0 }),
            "constructor body demand is separate from owner ABI");
        Check(Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, arrayBody)).Body.Method.SetEquals(new[] { 0 }),
            "array element is a reified argument position even without a container type");
        Check(Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, refBody)).Body.Method.Count == 0,
            "managed reference referent is a direct scalar slot");
        Check(file.ToJsonString() == original, "analysis must preserve all source facts");

        var importedUse = Owner("ImportedUse", Applied("ForeignProducer", Tv()));
        var imported = Collect(new[] { importedUse }, new Dictionary<string, NullableRepresentationFrame> {
            ["ForeignProducer"] = new NullableRepresentationFrame(1, new[] { 0 }),
        });
        Check(imported[0].Frame.NullableIndices.SequenceEqual(new[] { 0 }), "referenced declaration correspondence");
        Console.WriteLine("[nullable representation frame] self-test OK (source correspondence, scopes, declaration/body demand, fixed point)");
    }
}
