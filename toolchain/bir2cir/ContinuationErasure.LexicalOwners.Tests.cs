using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
            IBirExpression This() => new BirThis { Sty = exact };
            BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
            BirConstant Zero() => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) };
            var method = new BirFunctionDeclaration { Name = "use", Ret = new TypeNode.Fqn("kotlin.Unit"),
                Params = new[] { new BirParameter { Name = "peer", Type = exact } }, Body = new IBirStatement[] {
                    Expr(new BirCallInstance { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    Expr(new BirFieldGet { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    new BirSetField { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), Value = Zero() },
                    Expr(new BirFieldSetExpression { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), Value = Zero() }),
                    Expr(new BirLateinitGet { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    Expr(new BirCallInstance { OwnerType = exact, Recv = new BirLocal { Name = "peer", Sty = exact } }),
                    Expr(new BirStaticFieldGet { OwnerType = exact }),
                    Expr(new BirNew { Type = exact, Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>() }),
                } };
            var forward = new BirFunctionDeclaration { Name = "forward", Params = Array.Empty<BirParameter>(),
                Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { Expr(new BirCallInstance {
                    OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), _exactExistentialBridgeOwnerCall = true,
                }) } };
            var sourceOwner = new BirClassDefinition { Kind = "class", Name = owner,
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = Array.Empty<BirFieldDeclaration>(),
                Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                    new BirSetField { OwnerType = new TypeNode.Fqn(owner), Recv = new BirThis(), Value = Zero() },
                } } }, Methods = new[] { method, forward } };
            var sourceChild = new BirClassDefinition { Kind = "class", Name = "Child",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Interfaces = new TypeNode[] { exact },
                Methods = new[] { new BirFunctionDeclaration { Name = "inherited", Params = Array.Empty<BirParameter>(),
                    Ret = new TypeNode.Fqn("kotlin.Unit"), ClrInterfaceImpls = new[] {
                        new BirMethodImpl { Owner = exact, Arity = 0, Params = new TypeNode[] { exact }, Ret = exact },
                    }, Body = new IBirStatement[] { Expr(new BirCallInstance {
                        OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()),
                    }) } } } };
            var source = new BirFileDeclaration { FileClass = "LexicalAbiProbe", Types = new IBirTypeDeclaration[] { sourceOwner, sourceChild } };
            GenericSelfInstantiation.ApplyAll(new[] { source });
            // Only the continuing late erasure is DOM-owned; the replaced self-owner binder is typed.
            var root = source.ToDom();
            var declaration = root["types"][0].AsObject();
            var body = declaration["methods"][0]["body"].AsArray();
            var ctor = declaration["ctors"][0]["body"].AsArray();
            var forwarding = declaration["methods"][1];
            var child = root["types"][1];
            FBoundStarProjectionErasure.RemoveTransientFacts(new[] { root }, preserveExactOuter: true);
            Apply(root, names);
            void Equal(JsonNode value, TypeNode expected, string role)
            {
                if (TypeJson.Read(value) != expected) throw new InvalidOperationException($"Lexical coroutine frame: {owner} {selected} {role}");
            }
            for (var i = 0; i < 5; i++)
            {
                var access = body[i]["expr"] ?? body[i];
                Equal(access["ownerType"], exact, "own member owner");
                Equal(access["recv"]["sty"], exact, "implicit this stamp");
            }
            Equal(ctor[0]["ownerType"], new TypeNode.Fqn(owner, new TypeNode[] { tv }), "constructor field owner");
            Equal(forwarding["body"][0]["expr"]["ownerType"], exact, "exact forwarding target");
            Equal(body[5]["expr"]["ownerType"], erased, "ordinary peer owner");
            Equal(body[5]["expr"]["recv"]["sty"], erased, "ordinary peer value");
            Equal(body[6]["expr"]["ownerType"], erased, "static owner");
            Equal(body[7]["expr"]["type"], erased, "allocation ABI");
            Equal(declaration["methods"][0]["params"][0]["type"], erased, "parameter ABI");
            Equal(child["interfaces"][0], erased, "implemented ancestor ABI");
            Equal(child["methods"][0]["body"][0]["expr"]["ownerType"], erased, "inherited this target");
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
            ["kind"] = "class", ["name"] = "Capture", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "Outer" }, new JsonObject { ["name"] = "Actual" }),
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
