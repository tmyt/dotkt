using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumIntrinsicLowering
{
    internal static void SelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var parameter = new TypeNode.Tv("method", 0);
        var generic = new BirEnumValues { Type = parameter };
        var ordinary = new BirEnumOrdinal { Type = new TypeNode.Fqn("Ordinary"), E = new BirLocal { Name = "value" } };
        var method = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = generic }, new BirExpressionStatement { Expr = ordinary } } };
        var file = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            new BirEnumDefinition { Kind = "enum", Name = "Ordinary", Entries = new[] {
                new BirEnumEntryDeclaration { Name = "FIRST", Ordinal = 0 }, new BirEnumEntryDeclaration { Name = "SECOND", Ordinal = 1 } } } } };
        var physical = ClrEnumLowering.Apply(new[] { file }, out var parse);
        var runtime = EnumValueLowering.Apply(file, refs, new Dictionary<string, (string Name, string Ordinal)>(),
            new HashSet<string> { "Ordinary" }, physical, parse);
        if (!runtime || ((BirExpressionStatement)method.Body.Value[0]).Expr.Value is not BirCast { Type.Value: TypeNode.Array { Elem: TypeNode.Tv { Scope: "method", I: 0 } },
                E.Value: BirCallStatic { Method.Value: "kotlinEnumValues", Args.Value: [BirClassReference { Type.Value: TypeNode.Tv { Scope: "method", I: 0 } }] } }
            || !parse.ContainsKey("Ordinary") || physical.ContainsKey("Ordinary") || ordinary.Type.IsPresent || ordinary.Values.IsPresent)
            throw new InvalidOperationException("Typed enum values lost the runtime operation/frame or ordinary ordinal's constant-time form");

        var enumType = new TypeNode.Fqn("EnumType");
        var storage = new TypeNode.Fqn("StorageType");
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), physicalOrder: new[] { 1, 0 }, storageIndices: new[] { 0 });
        var arguments = new TypeNode[] { storage, enumType };
        if (!ReferenceEquals(SourceArgument(arguments, frame), enumType) || SourceArgument(arguments, null) != null
            || SourceArgument(new TypeNode[] { enumType }, frame) != null)
            throw new InvalidOperationException("Typed enum intrinsic lost its selected source argument frame");
        var rich = new TypeNode.Fqn("MappedEntry");
        var valueOfArgument = new BirCallStatic { Owner = new TypeNode.Fqn("Probe"), Method = "name", Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("kotlin.String") };
        var framedCall = new BirCallStatic { Method = "enumValues", DeclarationId = "enum-values-frame", TypeArgs = arguments, Args = Array.Empty<IBirExpression>() };
        var richParse = new BirEnumParse { Type = rich, Arg = valueOfArgument };
        var explicitOwner = new BirCallStatic { Owner = new TypeNode.Fqn("User"), Method = "enumValues", TypeArgs = new TypeNode[] { rich }, Args = Array.Empty<IBirExpression>() };
        var sourceMethod = new BirFunctionDeclaration { Name = "source", DeclarationId = "enum:source", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = framedCall }, new BirExpressionStatement { Expr = richParse }, new BirExpressionStatement { Expr = explicitOwner } } };
        var sources = new[] { sourceMethod };
        var uses = new BirFileDeclaration { Methods = sources, Types = Array.Empty<IBirTypeDeclaration>() };
        Apply(uses, new HashSet<string> { rich.Name }, new HashSet<string>(), true, refs,
            new Dictionary<string, NullableRepresentationFrame> { ["enum-values-frame"] = frame });
        var richCall = ((BirExpressionStatement)sourceMethod.Body.Value[1]).Expr.Value as BirCallStatic;
        if (!ReferenceEquals(uses.Methods.Value, sources) || !ReferenceEquals(uses.Methods.Value[0], sourceMethod)
            || ((BirExpressionStatement)sourceMethod.Body.Value[0]).Expr.Value is not BirEnumValues { Type.Value: TypeNode.Fqn { Name: "EnumType" } }
            || richCall?.Method.OrDefault() != "valueOf" || richCall.Ret.Value != rich
            || richCall.Sig.Value is not [TypeNode.Fqn { Name: "System.String" }] || ReferenceEquals(richCall.Args.Value[0], valueOfArgument)
            || !ReferenceEquals(((BirExpressionStatement)sourceMethod.Body.Value[2]).Expr.Value, explicitOwner))
            throw new InvalidOperationException("Typed enum intrinsic changed owning declarations, selected frame, rich descriptor or explicit owner");
        var shadow = new BirCallStatic { Method = "enumValues", TypeArgs = new TypeNode[] { rich }, Args = Array.Empty<IBirExpression>() };
        var shadowFile = new BirFileDeclaration { Fields = new[] { new BirFieldDeclaration { Name = "shadow", Init = shadow } } };
        Apply(shadowFile, new HashSet<string> { rich.Name }, new HashSet<string> { "enumValues" }, true, refs,
            new Dictionary<string, NullableRepresentationFrame>());
        if (!ReferenceEquals(shadowFile.Fields.Value[0].Init.Value, shadow))
            throw new InvalidOperationException("Typed enum intrinsic selected a user top-level shadow");
        var mappedValues = RichEnumCall(rich, "chosenEntriesApi", null);
        var mappedParse = RichEnumCall(rich, "chosenParseApi", valueOfArgument);
        if (BirNodeType.Of(mappedValues) != new TypeNode.Array(rich) || BirNodeType.Of(mappedParse) != rich
            || ArrayConstructionLowering.ArrayElementOf(BirGraphStaticType.Surface((IBirExpression)mappedValues, new Dictionary<string, TypeNode>())) != rich)
            throw new InvalidOperationException("Typed rich enum API lost its exact array/result type before array lowering");
        // Exercise the actual continuing owner across the single frontier,
        // rather than only the typed static-type helper that feeds it later.
        var arraySource = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "enumerate", Static = true, Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] {
                new BirForArray { Var = "entry", Array = mappedValues, Body = Array.Empty<IBirStatement>() },
            },
        } } };
        var continuing = arraySource.ToDom();
        ArrayConstructionLowering.Apply(continuing, refs);
        if (TypeJson.Read(continuing["methods"][0]["body"][0]["elem"]) != rich)
            throw new InvalidOperationException("The continuing array owner lost the generated rich API's element type");
        Console.WriteLine("[enum intrinsic frame] self-test OK (owning declarations, shadow/owner selection, ordinary frame, rich descriptors)");
    }
}
