using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClosureSynthesis
{
    static void TypedFileSelfTest(ReferenceMetadataIndex refs)
    {
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var lexical = new TypeNode.Tv("method", 2);
        BirNewClosure Closure(string name) => new() {
            ClosureType = new TypeNode.Fqn(name), Method = "invoke", Captures = Array.Empty<IBirExpression>(),
            FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()),
            SynthClass = new BirClosureDefinition { Name = name, Fields = Array.Empty<BirCaptureDescriptor>(),
                Params = Array.Empty<BirLambdaParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>() },
        };
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        var capture = Closure("TypedFile.Capture");
        var inner = Closure("TypedFile.Inner");
        var outer = Closure("TypedFile.Outer");
        outer.Captures = new IBirExpression[] { capture };
        outer.SynthClass.Value.Body = new IBirStatement[] { Statement(inner) };
        var duplicate = Closure("TypedFile.Outer");
        var fieldClosure = Closure("TypedFile.Field");
        var memberClosure = Closure("TypedFile.Member");
        var samInner = Closure("TypedFile.SamInner");
        var ownerFrame = new NullableRepresentationFrame(1, new[] { 0 });
        var sourceParameter = new BirParameter { Name = "value", Type = lexical, NullableGeneric = lexical };
        var sourceInvoke = new BirFunctionDeclaration {
            Name = "accept", Static = false, Virtual = true, Override = true, Params = new[] { sourceParameter }, Ret = lexical,
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("TypedFile.KotlinSam"), DeclarationId = "selected:sam-slot" } },
            Body = new IBirStatement[] { Statement(samInner) },
        };
        var sourceSam = new BirClassDefinition {
            Name = "TypedFile.Sam", Kind = "class", Generated = true, Fields = Array.Empty<BirFieldDeclaration>(),
            Methods = new[] { sourceInvoke }, Interfaces = new TypeNode[] { new TypeNode.Fqn("TypedFile.KotlinSam") },
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "TNullable" } },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame),
        };
        var sam = new BirNewSam { SamType = new TypeNode.Fqn("TypedFile.Sam"), Captures = Array.Empty<IBirExpression>(),
            TypeArgs = new TypeNode[] { lexical, new TypeNode.Tv("method", 3) }, SynthClass = sourceSam };
        var method = new BirFunctionDeclaration {
            Name = "source", DeclarationId = "typed-file:source", Params = Array.Empty<BirParameter>(), Ret = unit,
            Body = new IBirStatement[] { Statement(outer), Statement(duplicate), Statement(sam) },
        };
        var field = new BirFieldDeclaration {
            Name = "initializer", Type = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()), Init = fieldClosure,
            Pos = new BirDeclarationPosition { F = "/source/TypedFile.kt", L = 23 },
        };
        var member = new BirFunctionDeclaration {
            Name = "member", DeclarationId = "typed-file:member", Params = Array.Empty<BirParameter>(), Ret = unit,
            Body = new IBirStatement[] { Statement(memberClosure) },
        };
        var existing = new BirClassDefinition { Name = "TypedFile.Existing", Kind = "class", Methods = new[] { member } };
        var methods = new[] { method };
        var fields = new[] { field };
        var file = new BirFileDeclaration { FileClass = "TypedFile", Methods = methods, Fields = fields, Types = new IBirTypeDeclaration[] { existing } };
        var identities = new SourceModuleIndex(new[] { file }, new Dictionary<string, string>()).DeclarationIds;
        var callbacks = 0;
        BirFunctionDeclaration projected = null;
        NullableRepresentationFrame projectedFrame = null;
        Apply(file, refs, (actual, frame) => {
            callbacks++;
            projected = actual;
            projectedFrame = frame;
            if (ReferenceEquals(actual, sourceInvoke) || ReferenceEquals(actual.Params.Value[0], sourceParameter)
                || frame == null || frame.SourceArity != 1 || frame.PhysicalArity != 2
                || actual.Params.Value[0].Type.Value is not TypeNode.Tv { Scope: "type", I: 0 }
                || actual.Params.Value[0].NullableGeneric.Value is not TypeNode.Tv { Scope: "type", I: 0 })
                throw new InvalidOperationException("Typed full closure synthesis projected a source SAM or lost its explicit owner frame");
            actual.Ret = new TypeNode.Fqn("object");
        });
        var synthesizedSam = file.Types.Value.OfType<BirClassDefinition>().Single(type => type.Name.OrDefault() == "TypedFile.Sam");
        if (callbacks != 1 || !ReferenceEquals(synthesizedSam.Methods.Value.Single(), projected)
            || !ReferenceEquals(projectedFrame, synthesizedSam.KotlinSupertypesPre.Value.NullableFrame)
            || ReferenceEquals(synthesizedSam, sourceSam) || sourceInvoke.Ret.Value != lexical
            || !ReferenceEquals(sourceInvoke.Params.Value[0], sourceParameter) || sourceParameter.Type.Value != lexical
            || projected.Ret.Value is not TypeNode.Fqn { Name: "object" }
            || synthesizedSam.KotlinSupertypesPre.Value.NullableFrame.SourceArity != 1)
            throw new InvalidOperationException("Typed full closure synthesis did not project only the cloned SAM declaration");
        if (!ReferenceEquals(file.Methods.Value, methods) || !ReferenceEquals(file.Fields.Value, fields)
            || !ReferenceEquals(file.Types.Value[0], existing) || !ReferenceEquals(existing.Methods.Value[0], member)
            || !new SourceModuleIndex(new[] { file }, new Dictionary<string, string>()).DeclarationIds.SetEquals(identities)
            || !file.Types.Value.Select(type => type.Name.Value).SequenceEqual(new[] {
                "TypedFile.Existing", "TypedFile.Capture", "TypedFile.Inner", "TypedFile.Outer", "TypedFile.SamInner", "TypedFile.Sam",
                "TypedFile.Field", "TypedFile.Member" })
            || outer.SynthClass.IsPresent || duplicate.SynthClass.IsPresent || sam.SynthClass.IsPresent)
            throw new InvalidOperationException("Typed full closure synthesis changed source identity/order, bottom-up append or name deduplication");
        if (SourceInputContext(field) != new CaptureContext("initializer", "/source/TypedFile.kt", 23))
            throw new InvalidOperationException("Typed full closure synthesis lost its field initializer source context");

        var action = new TypeNode.Fqn("System.Action");
        var nominal = new BirNewSam {
            SamType = new TypeNode.Fqn("TypedFile.Nominal"), Captures = Array.Empty<IBirExpression>(),
            SynthClass = new BirClassDefinition { Name = "TypedFile.Nominal", Interfaces = new TypeNode[] { action },
                Fields = Array.Empty<BirFieldDeclaration>(), Methods = new[] { new BirFunctionDeclaration {
                    Name = "Invoke", Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>(),
                } } },
        };
        var nominalField = new BirFieldDeclaration { Name = "callback", Init = new BirSamConversion { Type = action, E = nominal } };
        var nominalFile = new BirFileDeclaration { Fields = new[] { nominalField }, Types = Array.Empty<IBirTypeDeclaration>() };
        Apply(nominalFile, refs, (_, _) => throw new InvalidOperationException("CLR delegate SAM reached the ordinary SAM callback"));
        if (nominalField.Init.Value is not BirSamConversion { E.Value: BirNewClosure normalized }
            || normalized.SynthClass.IsPresent || normalized.DotktDelegateSlot.Value != action
            || nominalFile.Types.Value is not [BirClassDefinition { Name.Value: "TypedFile.Nominal" }])
            throw new InvalidOperationException("Typed full closure synthesis lost nominal CLR delegate normalization");
        Console.WriteLine("[typed file closures] self-test OK (actual source records, append order/dedup, explicit SAM frame/callback and nominal delegate)");
    }
}
