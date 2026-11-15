using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static class CurrentDeclarationBindingsTests
{
    internal static void SelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Current declaration bindings: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Current declaration bindings accepted an incomplete or repeated frontier");
        }
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var donor = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "run", DeclarationId = "run", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = donor } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Ctors = new[] { constructor }, Methods = new[] { method } };
        var root = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { owner } };
        var bindings = new CurrentDeclarationBindings(new[] { root });
        var ctorBinding = bindings.GetSource(constructor);
        var donorBinding = bindings.GetSource(donor);
        var methodBinding = bindings.SourceLocalDeclarations[("Owner", "run")];
        Require(ReferenceEquals(methodBinding, bindings.GetSource(method))
            && ReferenceEquals(bindings.SourceLocalMethods["Owner\0run"][0], methodBinding)
            && ReferenceEquals(bindings.SourceDefinitions["Owner"].Source, owner), "inventory recreated a declaration");
        var projected = root.ToDom(bindings.ObserveProjection);
        Require(JsonNode.DeepEquals(projected, root.ToDom()), "observation changed the emitted graph");
        bindings.BindProjected();
        Require(ReferenceEquals(ctorBinding.Physical, projected["types"][0]["ctors"][0])
            && ReferenceEquals(donorBinding.Physical, projected["types"][0]["methods"][0]["body"][0]["expr"])
            && ReferenceEquals(methodBinding.Physical, projected["types"][0]["methods"][0]),
            "ID-less contexts were inferred instead of retaining their actual projected occurrence");
        methodBinding.Physical["name"] = "physicalName";
        Require(bindings.GetPhysical(methodBinding.Physical).Physical["name"]?.GetValue<string>() == "physicalName",
            "physical queries retained a stale snapshot");
        Reject(() => { _ = methodBinding.Source; });
        Reject(bindings.BindProjected);
        Reject(() => bindings.GetSource(method));
        Reject(() => bindings.GetPhysical(methodBinding.Physical.DeepClone().AsObject()));
        var late = new JsonObject { ["name"] = "late" };
        Require(ReferenceEquals(bindings.RegisterPhysical(late), bindings.RegisterPhysical(late)), "late admission duplicated its authority");

        var missing = new CurrentDeclarationBindings(new[] { root });
        Reject(() => missing.BindProjected(new Dictionary<BirGraphValue, JsonObject>()));
        Require(ReferenceEquals(missing.GetSource(method).Source, method), "failed binding consumed source records");
        var repeated = new CurrentDeclarationBindings(new[] { root });
        root.ToDom(repeated.ObserveProjection);
        Reject(() => root.ToDom(repeated.ObserveProjection));
        RetiredPropertyOwners();
        Console.WriteLine("[current declaration bindings] self-test OK (generated projection, ID-less contexts, live handoff, exact admission and release)");

        static void RetiredPropertyOwners()
        {
            foreach (var onFacade in new[] { false, true })
            {
                var property = new BirPropertyDeclaration { Name = "genericProperty" };
                var type = new BirClassDefinition { Name = "PropertyOwner", Kind = "class",
                    Properties = onFacade ? Array.Empty<BirPropertyDeclaration>() : new[] { property } };
                var file = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { type },
                    Properties = onFacade ? new[] { property } : Array.Empty<BirPropertyDeclaration>() };
                BirGraphValue owner = onFacade ? file : type;
                var ledger = new CurrentDeclarationBindings(new[] { file });
                var captured = ledger.GetSource(property);
                Reject(() => ledger.RetireSourceProperties(onFacade ? type : file, new[] { property }));
                Reject(() => ledger.RetireSourceProperties(owner, new[] { property, property }));
                ledger.RetireSourceProperties(owner, new[] { property });
                Reject(() => ledger.RetireSourceProperties(owner, new[] { property }));
                // A detached declaration still has its original captured
                // continuation, projected by the producer's retired forest.
                ledger.RetireSourceForest(property);
                file.ToDom(ledger.ObserveProjection);
                Reject(ledger.BindProjected);
                if (onFacade) file.Properties = Array.Empty<BirPropertyDeclaration>();
                else type.Properties = Array.Empty<BirPropertyDeclaration>();
                // The unsuccessful validation above must not consume bindings.
                var actual = new Dictionary<BirGraphValue, JsonObject>();
                file.ToDom((source, dom) => actual.Add(source, dom));
                var retired = property.ToDom();
                actual.Add(property, retired);
                ledger.BindProjected(actual);
                Require(ReferenceEquals(captured.Physical, retired), "retired property lost its exact projection");
                Reject(() => ledger.RetireSourceProperties(owner, Array.Empty<BirPropertyDeclaration>()));
            }
        }
    }
}
