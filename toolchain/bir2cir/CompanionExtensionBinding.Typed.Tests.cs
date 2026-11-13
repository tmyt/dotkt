using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class CompanionExtensionBinding
{
    internal static void SelfTest()
    {
        var receiver = new TypeNode.Fqn("ExtensionProbe.Receiver");
        var own = new TypeNode.Tv("method", 0);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var defaultUse = new BirCallStatic {
            Owner = new TypeNode.Fqn("ExtensionProbe.File"), Method = "echo", CompanionReceiver = receiver,
            Args = Array.Empty<IBirExpression>(),
        };
        var parameter = new BirParameter { Name = "p", Type = own,
            Attrs = new[] { DefaultBirCarrier.Create(0, defaultUse) } };
        var implementation = new BirFunctionDeclaration {
            Name = "echo", DeclarationId = "echo-id", CompanionReceiver = receiver,
            CompanionSourceName = "echo", CompanionMemberKind = "function", Static = true,
            Params = new[] { parameter }, TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ret = own,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "p" } } },
        };
        var initializer = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(7) };
        var field = new BirFieldDeclaration {
            Name = "state", Type = integer, Init = initializer, CompanionReceiver = receiver,
            CompanionSourceName = "state", CompanionMemberKind = "field", CompanionPropertyMutable = true,
            CompanionSetterVisibility = "private", CompanionStorageReadOnly = false,
            CompanionGetterDeclarationId = "state-get", CompanionSetterDeclarationId = "state-set", Vis = "public",
        };
        var read = new BirExpressionStatement { Expr = new BirStaticFieldGet {
            OwnerType = new TypeNode.Fqn("ExtensionProbe.File"), Name = "state", CompanionReceiver = receiver, Sty = integer,
        } };
        var written = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(9) };
        var write = new BirExpressionStatement { Expr = new BirStaticFieldSet {
            OwnerType = new TypeNode.Fqn("ExtensionProbe.File"), Name = "state", CompanionReceiver = receiver, Value = written,
        } };
        var use = new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { read, write } };
        var root = new BirFileDeclaration {
            FileClass = "ExtensionProbe.File", Methods = new[] { implementation, use }, Fields = new[] { field },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition {
                Kind = "class", Name = receiver.Name, TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            } },
        };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        try
        {
            var index = Apply(new[] { root }, refs);
            var binding = index.Bindings[Key("ExtensionProbe.File", TypeNode.ToJson(receiver), "function", "echo")];
            var container = (BirClassDefinition)root.Types.Value.Single(type => type.Name.OrDefault() == binding.PhysicalOwner);
            var core = container.Methods.Value.Single(method => method.DeclarationId.OrDefault() == "echo-id");
            var wrapper = container.Methods.Value.Single(method => method.ExtensionCoreDeclarationId.OrDefault() == "echo-id");
            var group = (BirClassDefinition)root.Types.Value.Single(type => type.NestedIn.OrDefault() == container.Name.Value);
            var marker = (BirClassDefinition)root.Types.Value.Single(type => type.NestedIn.OrDefault() == group.Name.Value);
            if (!ReferenceEquals(core, implementation) || !ReferenceEquals(core.Params.Value[0], parameter)
                || !ReferenceEquals(root.Fields.Value.Single(), field) || !ReferenceEquals(field.Init.Value, initializer)
                || root.Methods.Value.Single() != use || field.Vis.Value != "private"
                || core.CompanionReceiver.IsPresent || wrapper.TypeParams.Value.Length != 2
                || wrapper.Ret.Value != new TypeNode.Tv("method", 1)
                || wrapper.Params.Value[0].Type.Value != new TypeNode.Tv("method", 1)
                || wrapper.Body.Value.Single() is not BirReturn { Value.Value: BirCallStatic { DeclarationId.Value: "echo-id" } }
                || group.TypeParams.Value.Single().Name.Value != "dotkt$receiver$0"
                || marker.CapturedTypeParams.Value.Length != 1
                || marker.Methods.Value.Single().Params.Value.Single().Type.Value is not TypeNode.Fqn { Name: "ExtensionProbe.Receiver", Args: [TypeNode.Tv { Scope: "type", I: 0 }] })
                throw new InvalidOperationException("Typed companion extension placement lost owning declarations, initialization order or wrapper frames");
            if (read.Expr.Value is not BirCallStatic getter || getter.Sty.Value != integer
                || write.Expr.Value is not BirCallStatic setter || !ReferenceEquals(setter.Args.Value.Single(), written)
                || setter.Sig.Value.Single() != integer
                || container.Methods.Value.Single(method => method.DeclarationId.OrDefault() == "state-get").Name.Value != getter.Method.Value
                || container.Methods.Value.Single(method => method.DeclarationId.OrDefault() == "state-set").Name.Value != setter.Method.Value
                || DefaultBirCarrier.OfParameter(parameter) is not BirCallStatic carried
                || carried.Method.Value != binding.PhysicalName || carried.Owner.Value != new TypeNode.Fqn(binding.PhysicalOwner))
                throw new InvalidOperationException("Typed companion extension uses or frozen defaults lost their allocated binding");

            // Late materialized payloads use the same scalar index after the one
            // module frontier; declaration placement is not run a second time.
            var materialized = new JsonObject {
                ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("ExtensionProbe.File"), ["method"] = "echo",
                ["companionReceiver"] = TypeNode.ToJson(receiver),
            };
            BindMaterializedUses(materialized, index, refs);
            if (materialized["method"].GetValue<string>() != binding.PhysicalName
                || TypeJson.Read(materialized["owner"]) != new TypeNode.Fqn(binding.PhysicalOwner))
                throw new InvalidOperationException("Late companion extension use lost the original scalar placement decision");
        }
        finally { refs.DisposeNet(); }
        Console.WriteLine("[typed companion extensions] self-test OK (owning moves, storage, generic groups/wrappers, defaults and late scalar binding)");
    }
}
