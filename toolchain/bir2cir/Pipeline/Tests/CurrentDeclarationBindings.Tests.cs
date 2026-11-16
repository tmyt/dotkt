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
        var ledger = new SourceDeclarationProjection(new[] { root }, bindings);
        Require(ReferenceEquals(methodBinding, bindings.GetSource(method))
            && ReferenceEquals(bindings.SourceLocalMethods["Owner\0run"][0], methodBinding)
            && ReferenceEquals(bindings.SourceDefinitions["Owner"].Source, owner), "inventory recreated a declaration");
        Reject(() => ledger.RequireOccurrence(method));
        Reject(() => bindings.CompleteSourceCustody(ledger));
        ledger.SealSourceContinuations();
        ledger.RequireOccurrence(constructor); ledger.RequireOccurrence(donor);
        Require(ReferenceEquals(ctorBinding.Source, constructor) && ReferenceEquals(donorBinding.Source, donor)
            && ReferenceEquals(methodBinding.Source, method) && !ledger.IsRetired(method)
            && ledger.TryOwner(root, 0, owner, out var ownerPosition, out var ownerRetired)
            && ownerPosition == 0 && !ownerRetired && !ledger.RetiredTypes.Any(),
            "ID-less contexts lost their exact actual occurrence");
        method.Name = "currentName";
        Require(((BirFunctionDeclaration)methodBinding.Source).Name.Value == "currentName"
            && ReferenceEquals(bindings.SourceLocalMethods["Owner\0run"][0], methodBinding),
            "actual live facts or original selection membership were replaced");
        Reject(() => ledger.RequireOccurrence(method.DeepClone()));
        var wrong = new CurrentDeclarationBindings(new[] { root });
        Reject(() => wrong.CompleteSourceCustody(ledger));
        bindings.CompleteSourceCustody(ledger);
        Require(bindings.IsBound && ReferenceEquals(root.Types.Value[0], owner)
            && ReferenceEquals(owner.Methods.Value[0], method), "completion replaced the continuing graph");
        Reject(() => { _ = methodBinding.Source; });
        Reject(() => bindings.CompleteSourceCustody(ledger));
        Reject(() => bindings.GetSource(method));
        Reject(ledger.SealSourceContinuations);
        ledger.Release();
        Reject(() => ledger.RequireOccurrence(method));
        Reject(ledger.Release);

        var missing = new CurrentDeclarationBindings(new[] { root });
        var absent = missing.GetSource(new BirConstructor());
        var missingLedger = new SourceDeclarationProjection(new[] { root }, missing);
        missingLedger.SealSourceContinuations();
        Reject(() => missing.CompleteSourceCustody(missingLedger));
        Require(ReferenceEquals(missing.GetSource(method).Source, method) && absent.Source is BirConstructor,
            "failed completion consumed source records");
        var repeated = new CurrentDeclarationBindings(new[] { root });
        repeated.GetSource(donor);
        var repeatedLedger = new SourceDeclarationProjection(new[] { root }, repeated);
        var originalBody = method.Body;
        method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = donor }, new BirExpressionStatement { Expr = donor } };
        Reject(repeatedLedger.SealSourceContinuations);
        method.Body = originalBody;
        repeatedLedger.SealSourceContinuations();
        repeated.CompleteSourceCustody(repeatedLedger);
        repeatedLedger.Release();
        SharedMetadataOccurrenceBoundary();
        RetiredPropertyOwners();
        Console.WriteLine("[current declaration bindings] self-test OK (same-record seal, ID-less contexts, exact admission, custody and release)");

        static void SharedMetadataOccurrenceBoundary()
        {
            foreach (var capture in new[] { false, true })
            {
                var attribute = new BirAttribute { Attr = new TypeNode.Fqn("System.ObsoleteAttribute") };
                var first = new BirParameter { Name = "first", Type = new TypeNode.Fqn("System.String"), Attrs = new[] { attribute } };
                var second = new BirParameter { Name = "second", Type = new TypeNode.Fqn("System.String"), Attrs = new[] { attribute } };
                var method = new BirFunctionDeclaration { Name = "sharedMetadata", Params = new[] { first, second },
                    Body = Array.Empty<IBirStatement>() };
                var file = new BirFileDeclaration { FileClass = "SharedMetadata", Methods = new[] { method } };
                var bindings = new CurrentDeclarationBindings(new[] { file });
                var ledger = new SourceDeclarationProjection(new[] { file }, bindings);
                Require(ReferenceEquals(first.Attrs.Value[0], second.Attrs.Value[0])
                    && !bindings.TryGetCapturedSource(attribute, out _), "pure shared metadata was automatically captured");
                if (capture)
                {
                    var captured = bindings.GetSource(attribute);
                    Reject(ledger.SealSourceContinuations);
                    Require(!bindings.IsBound && ReferenceEquals(captured.Source, attribute),
                        "a duplicate captured metadata occurrence consumed its source authority");
                }
                else
                {
                    ledger.SealSourceContinuations();
                    ledger.RequireOccurrence(first);
                    ledger.RequireOccurrence(second);
                    Reject(() => ledger.RequireOccurrence(attribute));
                    bindings.CompleteSourceCustody(ledger);
                    ledger.Release();
                }
            }
        }

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
                var bindings = new CurrentDeclarationBindings(new[] { file });
                var captured = bindings.GetSource(property);
                var ledger = new SourceDeclarationProjection(new[] { file }, bindings);
                Reject(() => bindings.RetireSourceProperties(onFacade ? type : file, new[] { property }));
                Reject(() => bindings.RetireSourceProperties(owner, new[] { property, property }));
                bindings.RetireSourceProperties(owner, new[] { property });
                Reject(() => bindings.RetireSourceProperties(owner, new[] { property }));
                ledger.RetireForest(property);
                Reject(ledger.SealSourceContinuations);
                if (onFacade) file.Properties = Array.Empty<BirPropertyDeclaration>();
                else type.Properties = Array.Empty<BirPropertyDeclaration>();
                ledger.SealSourceContinuations();
                Require(ledger.IsRetired(property) && ReferenceEquals(captured.Source, property),
                    "retired property lost its exact actual occurrence");
                bindings.CompleteSourceCustody(ledger);
                Reject(() => bindings.RetireSourceProperties(owner, Array.Empty<BirPropertyDeclaration>()));
                ledger.Release();
            }
        }
    }
}
