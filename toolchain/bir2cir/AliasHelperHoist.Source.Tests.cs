using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class AliasHelperHoist
{
    internal static void SelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Alias representation: " + message); }
        var frame = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 3, 0, 4, 1, 2, 5 },
            storageIndices: new[] { 0, 1 }, nullableStorageIndices: new[] { 0 });
        var policy = new GenericRepresentationPolicy(new Dictionary<string, string> { ["Alias"] = "Native" });
        var receiver = (TypeNode.Fqn)ReceiverType("Alias", frame, policy);
        Require(receiver.Args.Cast<TypeNode.Tv>().Select(type => type.I).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }),
            "receiver lost binding roles or physical permutation");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var callback = new TypeNode.Fn(false, methodVariable, new TypeNode[] { new TypeNode.Tv("type", 1) });
        var construction = new BirNew { Type = new TypeNode.Fqn("Target", new TypeNode[] { ownerVariable }),
            ArgTypes = new TypeNode[] { ownerVariable }, MemberSignature = new TypeNode[] { ownerVariable },
            Args = new IBirExpression[] { new BirThis() } };
        var suspend = new BirNewSuspendLambda { Arity = 0, CapValues = new IBirExpression[] { new BirThis() },
            Body = new IBirStatement[] { new BirReturn { Value = new BirThis() } } };
        var source = new BirFunctionDeclaration { Name = "Read", DeclarationId = "alias-test",
            DeclarationSourceName = "Read", TypeParams = new[] { new BirTypeParameter { Name = "R" } },
            Params = new[] { new BirParameter { Name = "callback", Type = callback, FunctionSignatureType = callback } },
            Ret = new TypeNode.Tv("type", 1), Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = construction }, new BirExpressionStatement { Expr = suspend } } };
        var parameters = new[] { "S0", "T0", "S1", "T1", "N0", "NS0" }
            .Select(name => new BirTypeParameter { Name = name }).ToArray();
        var helper = HoistMethod(source, parameters, receiver);
        Require(helper.DeclarationId.Value == DeclarationIdentity("alias-test") && helper.DeclarationSourceName.Value == "Read"
            && helper.Generated.Value && helper.Static.Value && helper.TypeParams.Value.Length == 7
            && helper.Ret.Value == new TypeNode.Tv("method", 1), "helper identity, flags or frame changed");
        Require(helper.Params.Value[1].FunctionSignatureType.Value is TypeNode.Fn signature
            && signature.Ret == new TypeNode.Tv("method", 6) && signature.Params[0] == new TypeNode.Tv("method", 1),
            "function signature frame was not moved");
        var hoistedNew = (BirNew)((BirExpressionStatement)helper.Body.Value[0]).Expr.Value;
        var hoistedSuspend = (BirNewSuspendLambda)((BirExpressionStatement)helper.Body.Value[1]).Expr.Value;
        Require(hoistedNew.ArgTypes.Value[0] == new TypeNode.Tv("method", 0)
            && hoistedNew.MemberSignature.Value[0] == ownerVariable
            && hoistedNew.Args.Value[0] is BirLocal { Name.Value: "__self" }
            && hoistedSuspend.CapValues.Value[0] is BirLocal { Name.Value: "__self" }
            && ((BirReturn)hoistedSuspend.Body.Value[0]).Value.Value is BirThis
            && construction.Args.Value[0] is BirThis && source.Ret.Value == new TypeNode.Tv("type", 1),
            "source identity, foreign descriptor or suspend receiver boundary changed");

        var target = new BirFunctionDeclaration { Name = "lifted", SemanticOwner = "kotlin.Array", Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "R" } },
            Params = new[] { new BirParameter { Name = "value", Type = ownerVariable } }, Ret = methodVariable,
            Body = Array.Empty<IBirStatement>() };
        var edge = new BirNewDelegate { Method = "lifted", CalleeOwner = new TypeNode.Fqn("kotlin.Array"),
            TypeArgs = new TypeNode[] { methodVariable, ownerVariable } };
        var plain = new BirClassDefinition { Name = "Kept", Kind = "class", Methods = new[] { source } };
        var alias = new BirClassDefinition { Name = "kotlin.Array", Kind = "class" };
        var rehomeRoot = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { alias, plain },
            Methods = new[] { target, new BirFunctionDeclaration { Body = new IBirStatement[] { new BirExpressionStatement { Expr = edge } } } } };
        var transitions = new AliasDeclarationTransitions();
        Apply(rehomeRoot, ReferenceMetadataIndex.Build(Array.Empty<string>()), policy, transitions, _ => { });
        Require(ReferenceEquals(rehomeRoot.Types.Value[0], plain) && ReferenceEquals(plain.Methods.Value[0], source)
            && ReferenceEquals(rehomeRoot.Methods.Value[0], target) && target.SemanticOwner.Value == "Facade"
            && target.Params.Value[0].Type.Value == new TypeNode.Tv("method", 1)
            && target.Ret.Value == methodVariable && edge.CalleeOwner.Value == new TypeNode.Fqn("Facade"),
            "kept records or authored generated-helper frame association changed");
        transitions.ValidateProjection(new JsonNode[] { rehomeRoot.ToDom() });
        var retained = rehomeRoot.Types.Value;
        rehomeRoot.Types = new IBirTypeDeclaration[] { (BirClassDefinition)plain.DeepClone() };
        var refused = false;
        try { transitions.ValidateProjection(new JsonNode[] { rehomeRoot.ToDom() }); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "an unreported replacement was accepted");
        rehomeRoot.Types = retained;
        transitions.Release();
        AliasHandoffSelfTest();
        Console.WriteLine("[alias helper receiver] self-test OK (owning identities, exact disposition, binding roles, permutation, scope transfer)");
    }

    static void AliasHandoffSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        BirFunctionDeclaration Method(string id) => new() { Name = "read", DeclarationId = id, Static = false,
            Params = Array.Empty<BirParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "R" } }, Ret = variable,
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = variable,
                E = new BirLocal { Name = "value", Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) } } } } };
        BirFieldDeclaration Field(string name, TypeNode type) => new() { Name = name, Static = true, Type = type,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var moved = Field("companion", new TypeNode.Fqn("Companion"));
        var retired = Field("discarded", new TypeNode.Fqn("kotlin.Int"));
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var firstMethod = Method("retired-alias");
        var keptMethod = Method("kept-member");
        var first = new BirClassDefinition { Name = "kotlin.Array", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
            Fields = new[] { moved, retired }, Methods = new[] { firstMethod }, KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame) };
        var removed = new BirClassDefinition { Name = "kotlin.IntArray", Kind = "class" };
        var kept = new BirClassDefinition { Name = "Kept", Kind = "class", Methods = new[] { keptMethod } };
        var child = new BirClassDefinition { Name = "Companion", Kind = "class", SemanticOwner = first.Name,
            CompanionCarrier = new BirCompanionCarrier(), Vis = "private" };
        var original = new IBirTypeDeclaration[] { first, removed, kept, child };
        var file = new BirFileDeclaration { FileClass = "AliasHandoff", Types = original };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var policy = GenericRepresentationPolicy.CreateSource(new[] { file }, new Dictionary<string, string> {
            ["kotlin.Array"] = "System.Array", ["kotlin.IntArray"] = "System.Int32[]" }, refs);
        policy.PrepareDomLocations(new[] { file });
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(new[] { file });
        UncheckedGenericCastReturnErasure.Apply(file, pending);
        var retiredMethods = new List<BirFunctionDeclaration>();
        var transitions = new AliasDeclarationTransitions();
        Apply(file, refs, policy, transitions, retiredMethods.Add);
        var host = (BirClassDefinition)file.Types.Value[0];
        if (file.Types.Value.Length != 3 || !ReferenceEquals(file.Types.Value[1], kept)
            || !ReferenceEquals(host.Fields.Value[0], moved) || !ReferenceEquals(original[0], first)
            || !ReferenceEquals(first.Methods.Value[0], firstMethod) || child.Vis.Value != "internal")
            throw new InvalidOperationException("Alias whole-type retirement changed kept source records or companion storage");
        var projection = file.ToDom();
        var retiredMap = new Dictionary<BirFunctionDeclaration, JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var method in retiredMethods) retiredMap.Add(method, method.ToDom());
        policy.BindDomDeclarations(new JsonNode[] { projection }, transitions);
        var index = UncheckedGenericCastReturnErasure.BindDomDeclarations(pending, new[] { projection }, retiredMap, transitions);
        var logical = policy.LocalDeclaration(first.Name.Value);
        if (logical == null || ReferenceEquals(logical, projection["types"][0]) || logical["fields"] != null || logical["methods"] != null
            || KotlinSupertypesRecord.ReadNullableFrame(logical)?.SourceArity != 1
            || logical["typeParams"][0]["variance"]?.GetValue<string>() != "out"
            || !ReferenceEquals(index.ByIdentity["retired-alias"].Method, retiredMap[firstMethod])
            || !ReferenceEquals(index.ByIdentity["kept-member"].Method, projection["types"][1]["methods"][0]))
            throw new InvalidOperationException("Alias logical facts, retired method or shifted exact owner handoff changed");
        policy.CheckAliasNativeHandoff(projection, retired);
        var retainedFields = host.Fields.Value;
        host.Fields = new[] { (BirFieldDeclaration)moved.DeepClone() };
        var refused = false;
        try { transitions.ValidateNativeField(moved); } catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Alias handoff accepted an unreported native field replacement");
        host.Fields = retainedFields;
        transitions.Release();
    }
}

sealed partial class GenericRepresentationPolicy
{
    internal void CheckAliasNativeHandoff(JsonObject root, BirFieldDeclaration retired)
    {
        if (_nativeFields.Count != 2 || !_nativeFields.Contains(root["types"][0]["fields"][0].AsObject())
            || _nativeFields.Count(field => field.Parent == null && Text(field["name"]) == retired.Name.Value) != 1
            || _sourceNativeStorage.Count != 0 || _pendingNativeOrigins != null)
            throw new InvalidOperationException("Alias native storage did not retain its exact continuing/detached physical occurrences");
    }
}
