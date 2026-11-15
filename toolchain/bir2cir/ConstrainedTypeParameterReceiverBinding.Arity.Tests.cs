using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void AritySnapshotSelfTest()
    {
        var root = JsonNode.Parse("""
          {"types":[{"name":"Plain","types":[{"name":"Nested","typeParams":[{"name":"T"}]}]},
                    {"name":"Duplicate","typeParams":[{"name":"T"}]},
                    {"name":"Duplicate","typeParams":[]},
                    {"name":"Receiver","typeParams":[{"name":"T"}]}]}
          """);
        var roots = new[] { root };
        var snapshot = CollectTypeArity(roots);
        if (snapshot.Count != 4 || snapshot["Plain"] != 0 || snapshot["Nested"] != 1
            || snapshot["Duplicate"] != 0 || snapshot["Receiver"] != 1)
            throw new InvalidOperationException("Declaration arities lost nested types or traversal-order overwrite");

        // Each dispatch phase rebuilds its index. A new snapshot observes added/removed/renamed types and changed
        // parameter counts, while the old snapshot remains a value (no retained root or hidden cache).
        var declarations = root["types"].AsArray();
        var nested = declarations[0]["types"][0].AsObject();
        nested["name"] = "Renamed";
        nested["typeParams"].AsArray().Add(new TypeParameterDeclaration("U").ToDom());
        nested["capturedTypeParams"] = new JsonArray(new JsonObject { ["name"] = "Outer" });
        declarations.RemoveAt(2);
        declarations.Add(new JsonObject { ["name"] = "Added", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "A" }) });
        var rebuilt = CollectTypeArity(roots);
        if (snapshot.ContainsKey("Renamed") || snapshot.ContainsKey("Added") || snapshot["Nested"] != 1
            || rebuilt.ContainsKey("Nested") || rebuilt["Renamed"] != 3 || rebuilt["Added"] != 1
            || rebuilt["Duplicate"] != 1
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), rebuilt) != null
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), snapshot) == null)
            throw new InvalidOperationException("Declaration arity snapshot was not rebuilt across structural mutation");
        if (CollectTypeArity(Array.Empty<JsonNode>()).Any())
            throw new InvalidOperationException("Declaration arities leaked into a later compilation");
        ContinuationClosingSelfTest();
        Console.WriteLine("[declaration arities] self-test OK (owning continuation, physical frame, nested/order, mutation/rebuild, isolation)");
    }

    static void ContinuationClosingSelfTest()
    {
        var expected = new TypeNode.Fqn("Nested", new TypeNode[] { new TypeNode.Star() });
        var variable = new TypeNode.Tv("type", 1);
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("Nested"), Method = "run",
            Recv = new BirLocal { Name = "x" }, Args = Array.Empty<IBirExpression>() };
        var accessor = new BirPropertyGet { Type = new TypeNode.Fqn("Nested"),
            Member = "accessor", Accessor = "get_value", Dispatch = "callvirt", Recv = new BirLocal { Name = "x" } };
        var statements = new IBirStatement[] {
            new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = accessor },
        };
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] { new BirParameter { Name = "x", Type = variable } },
            TypeParams = Array.Empty<BirTypeParameter>(), Body = statements };
        // The retained logical bound belongs to source parameter zero, whose
        // ordinary physical slot is one. Do not recover it from the storage
        // slot or an unprojected declaration cloned by the old DOM wrapper.
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var facts = new SourceTypeFacts(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { expected },
        }).MergeNullableFrame(frame);
        var owner = new BirClassDefinition { Name = "Receiver", Kind = "class", KotlinSupertypesPre = facts,
            TypeParams = new[] { new BirTypeParameter { Name = "Storage" }, new BirTypeParameter { Name = "T" } },
            Methods = new[] { method } };
        var file = new BirFileDeclaration { FileClass = "ReceiverFile", Types = new IBirTypeDeclaration[] { owner } };
        var parameters = method.Params.Value;
        var methods = owner.Methods.Value;
        CloseOpenOwners(new[] { file });
        CloseTypeOwners(owner);
        CloseTypeOwners(owner);
        CloseMethodOwners(method, owner, Array.Empty<BirTypeParameter>(), refs: null);
        if (call.OwnerType.Value != expected || accessor.Type.Value != expected
            || !call._erasedConstraintDispatch.OrDefault() || !accessor._erasedConstraintDispatch.OrDefault()
            || !ReferenceEquals(method.Body.Value, statements) || !ReferenceEquals(method.Params.Value, parameters)
            || !ReferenceEquals(owner.Methods.Value, methods) || !ReferenceEquals(methods[0], method)
            || !ReferenceEquals(((BirExpressionStatement)statements[0]).Expr.Value, call)
            || !ReferenceEquals(((BirExpressionStatement)statements[1]).Expr.Value, accessor)
            || !ReferenceEquals(owner.KotlinSupertypesPre.Value, facts) || parameters[0].Type.Value != variable)
            throw new InvalidOperationException("Index-free owning continuation lost physical frame, bound or actual declarations");

        // The later physical closing phase remains a genuine DOM consumer.
        // Exercise its null-arity close-only guards, not a retired DOM oracle.
        var physical = file.ToDom();
        CloseOpenOwners(new JsonNode[] { physical });
        var body = physical["types"][0]["methods"][0]["body"];
        if (TypeJson.Read(body[0]["expr"]["ownerType"]) != expected
            || TypeJson.Read(body[1]["expr"]["type"]) != expected)
            throw new InvalidOperationException("Late close-only binding read a missing dispatch arity or changed the selected owner");
    }
}
