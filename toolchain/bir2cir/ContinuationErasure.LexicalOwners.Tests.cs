using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ContinuationErasure
{
    static void LexicalOwnerSelfTest()
    {
        var names = new Dictionary<string, string> { [Cont] = "probe.ContinuationPhysical", [ResultFqn] = "probe.ResultPhysical" };
        foreach (var owner in new[] { Cont, ResultFqn })
        foreach (var selected in new[] { owner, names[owner] })
        {
            var tv = new TypeNode.Tv("type", 0);
            var exact = new TypeNode.Fqn(selected, new TypeNode[] { tv });
            var erased = new TypeNode.Fqn(selected, AnyArg);
            JsonNode Type(TypeNode type) => TypeJson.Write(type);
            var root = JsonNode.Parse("""
              {"fileClass":"LexicalAbiProbe","types":[{"kind":"class","name":"REPLACED","typeParams":["T"],
                "fields":[],"ctors":[{"params":[],"body":[]}],
                "methods":[{"name":"use","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},"body":[]}]}]}
              """)!;
            var declaration = root["types"][0].AsObject();
            declaration["name"] = owner;
            var body = declaration["methods"][0]["body"].AsArray();
            foreach (var kind in new[] { "callInstance", "field", "setField", "setFieldExpr", "lateinitGet" })
                body.Add(new JsonObject { ["k"] = kind, ["ownerType"] = Type(exact),
                    ["recv"] = new JsonObject { ["k"] = "this", ["sty"] = Type(exact) } });
            var ctor = declaration["ctors"][0]["body"].AsArray();
            ctor.Add(new JsonObject { ["k"] = "setField", ["ownerType"] = TypeJson.Fqn(owner),
                ["recv"] = new JsonObject { ["k"] = "this" } });
            body.Add(new JsonObject { ["k"] = "callInstance", ["ownerType"] = Type(exact),
                ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "peer", ["sty"] = Type(exact) } });
            body.Add(new JsonObject { ["k"] = "staticField", ["ownerType"] = Type(exact) });
            body.Add(new JsonObject { ["k"] = "new", ["type"] = Type(exact) });
            declaration["methods"][0]["params"].AsArray().Add(new JsonObject { ["name"] = "peer", ["type"] = Type(exact) });
            var forwarding = new JsonObject { ["name"] = "forward", ["params"] = new JsonArray(),
                ["body"] = new JsonArray(new JsonObject { ["k"] = "callInstance", ["ownerType"] = Type(exact),
                    ["recv"] = new JsonObject { ["k"] = "this" }, [FBoundStarProjectionErasure.ExactBridgeOwnerCallKey] = true }) };
            declaration["methods"].AsArray().Add(forwarding);
            var child = new JsonObject { ["kind"] = "class", ["name"] = "Child", ["typeParams"] = new JsonArray("T"),
                ["interfaces"] = new JsonArray(Type(exact)), ["methods"] = new JsonArray(new JsonObject {
                    ["name"] = "inherited", ["params"] = new JsonArray(), ["clrInterfaceImpls"] = new JsonArray(
                        new JsonObject { ["owner"] = Type(exact), ["params"] = new JsonArray(Type(exact)), ["ret"] = Type(exact) }),
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "callInstance", ["ownerType"] = Type(exact),
                        ["recv"] = new JsonObject { ["k"] = "this" } }) }) };
            root["types"].AsArray().Add(child);
            GenericSelfInstantiation.ApplyAll(new[] { root });
            FBoundStarProjectionErasure.RemoveTransientFacts(new[] { root }, preserveExactOuter: true);
            Apply(root, names);
            void Equal(JsonNode value, TypeNode expected, string role)
            {
                if (TypeJson.Read(value) != expected) throw new InvalidOperationException($"Lexical coroutine frame: {owner} {selected} {role}");
            }
            for (var i = 0; i < 5; i++)
            {
                Equal(body[i]["ownerType"], exact, "own member owner");
                Equal(body[i]["recv"]["sty"], exact, "implicit this stamp");
            }
            Equal(ctor[0]["ownerType"], new TypeNode.Fqn(owner, new TypeNode[] { tv }), "constructor field owner");
            Equal(forwarding["body"][0]["ownerType"], exact, "exact forwarding target");
            Equal(body[5]["ownerType"], erased, "ordinary peer owner");
            Equal(body[5]["recv"]["sty"], erased, "ordinary peer value");
            Equal(body[6]["ownerType"], erased, "static owner");
            Equal(body[7]["type"], erased, "allocation ABI");
            Equal(declaration["methods"][0]["params"][0]["type"], erased, "parameter ABI");
            Equal(child["interfaces"][0], erased, "implemented ancestor ABI");
            Equal(child["methods"][0]["body"][0]["ownerType"], erased, "inherited this target");
            var descriptor = child["methods"][0]["clrInterfaceImpls"][0];
            Equal(descriptor["owner"], erased, "MethodImpl owner");
            Equal(descriptor["params"][0], erased, "MethodImpl parameter");
            Equal(descriptor["ret"], erased, "MethodImpl return");
        }
        ExactEnclosingFrames(names);
        Console.WriteLine("[coroutine lexical owners] self-test OK (own frames, enclosing storage, values and ancestor ABI)");
    }

    static void ExactEnclosingFrames(IReadOnlyDictionary<string, string> names)
    {
        var captured = new TypeNode.Fqn(ResultFqn, new TypeNode[] { new TypeNode.Tv("type", 1) });
        var nested = new TypeNode.Fqn("probe.Frame", new TypeNode[] { captured });
        var outerKey = FBoundStarProjectionErasure.ExactOuterKey;
        var root = new JsonObject { ["types"] = new JsonArray(new JsonObject {
            ["kind"] = "class", ["name"] = "Capture", ["typeParams"] = new JsonArray("Outer", "Actual"),
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "receiver", ["type"] = TypeJson.Write(nested), [outerKey] = true }),
            ["ctors"] = new JsonArray(new JsonObject { ["params"] = new JsonArray(new JsonObject {
                ["name"] = "receiver", ["type"] = TypeJson.Write(nested), [outerKey] = true }) }),
            ["methods"] = new JsonArray(new JsonObject { ["name"] = "read", ["params"] = new JsonArray(),
                ["body"] = new JsonArray(new JsonObject {
                    ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(nested),
                    ["recv"] = new JsonObject { ["k"] = "field", ["name"] = "receiver", [outerKey] = true,
                        ["ret"] = TypeJson.Write(nested), ["sty"] = TypeJson.Write(nested) } },
                    new JsonObject { ["k"] = "var", ["name"] = "ordinary", ["type"] = TypeJson.Write(nested) },
                    new JsonObject { ["k"] = "var", ["name"] = "exactAlias", ["type"] = TypeJson.Write(nested), [outerKey] = true }) }) }) };
        FBoundStarProjectionErasure.RemoveTransientFacts(new[] { root }, preserveExactOuter: true);
        Apply(root, names);
        var owner = root["types"][0];
        var call = owner["methods"][0]["body"][0];
        foreach (var exact in new[] { owner["fields"][0]["type"], owner["ctors"][0]["params"][0]["type"],
                     call["ownerType"], call["recv"]["ret"], call["recv"]["sty"], owner["methods"][0]["body"][2]["type"] })
            if (TypeJson.Read(exact) != nested) throw new InvalidOperationException("Exact captured frame arguments changed");
        var ordinary = new TypeNode.Fqn("probe.Frame", new TypeNode[] { new TypeNode.Fqn(ResultFqn, AnyArg) });
        if (TypeJson.Read(owner["methods"][0]["body"][1]["type"]) != ordinary
            || owner["fields"][0]["kotlinType"] != null)
            throw new InvalidOperationException("Exact enclosing storage changed ordinary ABI/source metadata");
        FBoundStarProjectionErasure.RemoveTransientFacts(new[] { root });
        bool HasOuter(JsonNode node) => node is JsonObject obj && (obj.ContainsKey(outerKey) || obj.Any(pair => HasOuter(pair.Value)))
            || node is JsonArray array && array.Any(HasOuter);
        if (HasOuter(root)) throw new InvalidOperationException("Enclosing receiver role escaped final cleanup");
    }
}
