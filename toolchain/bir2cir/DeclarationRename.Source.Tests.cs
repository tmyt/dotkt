using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationRename
{
    internal static void SourceSelfTest()
    {
        var refs = ReferenceMetadataIndex.DeclarationRenameTestIndex();
        var text = new TypeNode.Fqn("System.String");
        BirOverride Marker(string owner, string member, string kind = "method", int arity = 1, TypeNode[] arguments = null)
            => new() { Owner = new TypeNode.Fqn(owner, arguments), Member = member, Kind = kind, Arity = arity };
        BirFunctionDeclaration Method(string name) => new() {
            Name = name, Ret = text, Params = new[] { new BirParameter { Name = "p", Type = text } },
            TypeParams = Array.Empty<BirTypeParameter>(), Overrides = new[] { Marker("Slots", "source") },
            Override = false, Vis = "protected", DeclarationId = "source-id",
        };
        var ordinary = Method("source");
        ordinary.KotlinSourceMember = "original-source";
        var parameters = ordinary.Params.Value;
        var marker = ordinary.Overrides.Value[0];
        var call = new BirCallInstance { Method = "source", OwnerType = new TypeNode.Fqn("Slots"),
            Sig = new TypeNode[] { text }, Args = new IBirExpression[] { new BirLocal { Name = "p" } },
            Overrides = ordinary.Overrides, DeclarationId = "source-id" };
        var arguments = call.Args.Value;
        var propertyCall = (BirCallInstance)call.DeepClone();
        propertyCall.PropertyName = "value"; propertyCall.PropertyAccessor = "get";
        var aliasCall = (BirCallInstance)call.DeepClone(); aliasCall.OwnerType = new TypeNode.Fqn("AliasClass");
        var interfaceCall = (BirCallInstance)aliasCall.DeepClone(); interfaceCall.OwnerType = new TypeNode.Fqn("AliasInterface");
        ordinary.Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = propertyCall },
            new BirExpressionStatement { Expr = aliasCall }, new BirExpressionStatement { Expr = interfaceCall },
        };
        var interfaceMethod = Method("source");
        var missingFlags = Method("source"); missingFlags.Override = default; missingFlags.Vis = default;
        var emptyOverride = Method("untouched"); emptyOverride.Overrides = Array.Empty<BirOverride>();
        var wrongArity = Method("source"); wrongArity.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var property = new BirFunctionDeclaration { Name = KotlinPropertyAccessors.PhysicalName("message", "get"),
            PropertyName = "message", PropertyAccessor = "get", PropertyAssociation = "property-id", Ret = text,
            Params = Array.Empty<BirParameter>(), TypeParams = Array.Empty<BirTypeParameter>(),
            Overrides = new[] { Marker("System.Exception", "message", "getter", 0) }, Override = false, Vis = "protected" };
        var propertyRow = new BirPropertyDeclaration { Name = "message", KotlinAccessors = new[] { "get" },
            Overrides = property.Overrides, PropertyAssociation = "property-id" };
        var eventRead = new BirEventGet { Name = "message", Type = new TypeNode.Fqn("Owner"), Overrides = property.Overrides };
        property.Body = new IBirStatement[] { new BirExpressionStatement { Expr = eventRead } };
        var priorOwner = new TypeNode.Fqn("System.Exception");
        property.PendingOverrideOwner = priorOwner;
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        var source = new TypeNode.Tv("type", 0);
        var storage = frame.Variable(source, NullableRepresentationFrame.Role.Storage);
        var scalar = frame.Variable(source, NullableRepresentationFrame.Role.Ordinary);
        var framed = new BirFunctionDeclaration { Name = "source", Ret = text,
            Params = new[] { new BirParameter { Name = "p", Type = storage, NullableGeneric = source } },
            Overrides = new[] { Marker("FrameSlots", "source", arguments: new TypeNode[] { scalar }) } };
        var selectedCall = new BirCallInstance { Method = "source", Sig = new TypeNode[] { storage },
            DeclarationId = "selected-slot", Overrides = framed.Overrides };
        var unselectedCall = (BirCallInstance)selectedCall.DeepClone(); unselectedCall.DeclarationId = default;
        var framedOwner = new BirClassDefinition { Name = "Framed", Kind = "class",
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame), Methods = new[] { framed },
            Fields = new[] { new BirFieldDeclaration { Name = "selected", Init = selectedCall },
                new BirFieldDeclaration { Name = "unselected", Init = unselectedCall } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class",
            Methods = new[] { ordinary, missingFlags, emptyOverride, wrongArity, property }, Properties = new[] { propertyRow } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner,
            new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Methods = new[] { interfaceMethod } }, framedOwner } };
        var owners = file.Types.Value; var methods = owner.Methods.Value;
        Apply(file, refs);
        Check(ReferenceEquals(file.Types.Value, owners) && ReferenceEquals(owner.Methods.Value, methods)
            && ReferenceEquals(owner.Methods.Value[0], ordinary) && ReferenceEquals(ordinary.Params.Value, parameters)
            && ReferenceEquals(ordinary.Overrides.Value[0], marker) && ReferenceEquals(call.Args.Value, arguments)
            && ordinary.Name.Value == "Physical" && ordinary.DeclarationId.Value == "source-id"
            && ordinary.KotlinSourceMember.Value == "original-source" && ordinary.Override.Value && ordinary.Vis.Value == "public",
            "physical allocation must continue actual declarations and keep exact source identity/overrides");
        var carrier = ordinary.Attrs.Value.Single();
        var bytes = ((BirOpaqueBytes)carrier.Args.Value[1]).Bytes.Value;
        Check(carrier.Attr.Value is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute" }
            && Encoding.UTF8.GetString(bytes) == "{\"name\":\"source\"}",
            "renaming must append the shared source-method carrier without inferring source identity from the new spelling");
        Check(call.Method.Value == "Physical" && propertyCall.Method.Value == "source" && aliasCall.Method.Value == "source"
            && interfaceCall.Method.Value == "Physical" && interfaceMethod.Name.Value == "Physical"
            && !interfaceMethod.Override.Value && interfaceMethod.Vis.Value == "protected"
            && !missingFlags.Override.IsPresent && !missingFlags.Vis.IsPresent
            && emptyOverride.Name.Value == "untouched" && wrongArity.Name.Value == "source",
            "semantic property calls, rule-3 aliases, interface scope and explicit generic/flag presence must stay distinct");
        Check(property.Name.Value == KotlinPropertyAccessors.PhysicalName("message", "get") && property.Override.Value
            && property.Vis.Value == "protected" && propertyRow.Name.Value == "message" && !property.KotlinSourceMember.IsPresent
            && ReferenceEquals(property.PendingOverrideOwner.Value, priorOwner) && eventRead.Name.Value == "message"
            && property.PendingOverrideReturn.Value == text && property.PendingOverrideMember.Value == "get_Message",
            "property identity must remain in its naming domain while binding the exact virtual base accessor");
        Check(framed.Name.Value == "FramePhysical" && selectedCall.Method.Value == "FramePhysical" && unselectedCall.Method.Value == "source"
            && ReferenceEquals(framedOwner.KotlinSupertypesPre.Value.NullableFrame, frame)
            && framed.Params.Value[0].Type.Value == storage,
            "declaration nullable companions and selected call descriptors must share slot selection without rewriting storage");
        var before = ordinary.Attrs.Value;
        Apply(file, refs);
        Check(ReferenceEquals(before, ordinary.Attrs.Value), "stable physical allocation must not duplicate source-method metadata");
        Console.WriteLine("[typed declaration rename] self-test OK (owning identity, exact slots, properties, aliases, nullable frames)");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed declaration rename: " + message); }
}

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex DeclarationRenameTestIndex()
    {
        var index = Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        var logical = new TypeNode.Tv("type", 0);
        index._membersByOwner["Slots"] = new List<MemberBinding> {
            new("Slots", "source", 1, "Physical", true, false, ParamTypeNodes: new TypeNode[] { text }),
        };
        index._membersByOwner["FrameSlots"] = new List<MemberBinding> {
            new("FrameSlots", "source", 1, "FramePhysical", true, false, ParamTypeNodes: new TypeNode[] { logical }),
        };
        index._declarationById["selected-slot"] = new MemberBinding("Stored", "source", 1, null, false, false,
            ParamTypeNodes: new TypeNode[] { new TypeNode.Tv("type", 1) }, NullableGenericParams: new TypeNode[] { logical });
        foreach (var owner in new[] { "AliasClass", "AliasInterface" })
        {
            index._ownerAlias[owner] = "System.String";
            index._ownerAliasKind[owner] = owner == "AliasInterface" ? "interface" : "class";
            index._membersByOwner[owner] = new List<MemberBinding> {
                new(owner, "source", 1, null, false, false, ParamTypeNodes: new TypeNode[] { text }),
            };
        }
        index._membersByOwner["System.Exception"] = new List<MemberBinding> {
            new("System.Exception", "get_Message", 0, null, false, false, ParamTypeNodes: Array.Empty<TypeNode>(),
                SourcePropertyName: "message", AccessorKind: "get", AssociatedPropertyName: "Message", ReturnTypeNode: text),
        };
        return index;
    }
}
