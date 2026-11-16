using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    internal static void AllocationSourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning property allocation: " + message); }
        var text = new TypeNode.Fqn("kotlin.String");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var receiver = new BirLocal { Name = "receiver" };
        var operand = new BirLocal { Name = "operand" };
        var selected = new BirCallInstance { Method = "value", Prop = "get", OwnerType = new TypeNode.Fqn("Own"),
            Recv = receiver, Args = new IBirExpression[] { operand }, Sig = new TypeNode[] { text },
            ArgTypes = new TypeNode[] { integer }, Virtual = true, DeclarationId = "selected-call" };
        var ambiguous = new BirCallInstance { Method = "value", Prop = "get", OwnerType = new TypeNode.Fqn("Own"),
            Args = new IBirExpression[] { new BirLocal { Name = "other" } },
            Sig = Array.Empty<TypeNode>(), ArgTypes = new TypeNode[] { text } };
        var constrained = new BirConstrainedCall { Method = "bound", Prop = "get",
            Iface = new TypeNode.Fqn("Bound", new TypeNode[] { text }),
            Args = new IBirExpression[] { new BirLocal { Name = "bound" } }, Sig = new TypeNode[] { text } };
        var ordinaryStatic = new BirCallStatic { Method = "value", Prop = "get", Owner = new TypeNode.Fqn("Own"),
            Args = new IBirExpression[] { new BirLocal { Name = "static" } }, Sig = new TypeNode[] { text } };
        var referenced = new BirCallInstance { Method = "value", Prop = "get", OwnerType = new TypeNode.Fqn("Property.Ref"),
            Args = Array.Empty<IBirExpression>(), Super = false };
        var super = new BirConstrainedCall { Method = "value", Prop = "get", Iface = new TypeNode.Fqn("Property.Ref"),
            Args = Array.Empty<IBirExpression>(), Super = true };
        var localBeforeRef = new BirCallInstance { Method = "local", Prop = "get", OwnerType = new TypeNode.Fqn("Property.Ref"),
            Args = Array.Empty<IBirExpression>() };
        var getter = new BirFunctionDeclaration { Name = "item", PropertyName = "item", PropertyAccessor = "get",
            PropertyAssociation = "item-association", DeclarationId = "getter", Ret = text, Params = Array.Empty<BirParameter>() };
        var setter = new BirFunctionDeclaration { Name = "already-narrow", PropertyName = "item", PropertyAccessor = "set",
            PropertyAssociation = "item-association", Ret = new TypeNode.Fqn("void"), Params = new[] { new BirParameter { Type = text } } };
        var kept = new BirPropertyDeclaration { Name = "item", Type = text, PropertyAssociation = "item-association",
            KotlinAccessors = new[] { "get", "set" } };
        var (fileGeneric, fileProperty) = Generic("file");
        var (typeGeneric, typeProperty) = Generic("type");
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { typeGeneric },
            Properties = new[] { typeProperty } };
        var calls = new BirGraphValue[] { selected, ambiguous, constrained, ordinaryStatic, referenced, super, localBeforeRef };
        getter.Body = calls.Select(call => (IBirStatement)new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of((IBirExpression)call) }).ToArray();
        var methods = new[] { getter, setter, fileGeneric };
        var root = new BirFileDeclaration { Methods = methods, Properties = new[] { kept, fileProperty },
            Types = new IBirTypeDeclaration[] { owner } };
        var locals = new Dictionary<MemberCallSubstitution.LocalPropertyAccessorKey, IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> {
            [new("Own", "value", "get", 0, 1)] = new[] {
                new MemberCallSubstitution.LocalPropertyAccessor("selected-string", new TypeNode[] { text }),
                new MemberCallSubstitution.LocalPropertyAccessor("selected-int", new TypeNode[] { integer }),
            },
            [new("Bound", "bound", "get", 0, 1)] = new[] {
                new MemberCallSubstitution.LocalPropertyAccessor("closed-owner", new TypeNode[] { new TypeNode.Tv("type", 0) }),
            },
            [new("Property.Ref", "local", "get", 0, 0)] = new[] {
                new MemberCallSubstitution.LocalPropertyAccessor("local-first", Array.Empty<TypeNode>()),
            },
        };
        var refs = ReferenceMetadataIndex.PropertyAllocationSourceTestIndex();
        var retired = new List<BirPropertyDeclaration>();
        AllocateAll(root, refs, locals, (actualOwner, properties) => {
            var vector = actualOwner is BirFileDeclaration file ? file.Properties.Value : ((IBirTypeDeclaration)actualOwner).Properties.Value;
            Need(properties.Count == 1 && vector.Count(row => ReferenceEquals(row, properties[0])) == 1,
                "retirement did not identify a unique still-attached actual row");
            Need(ReferenceEquals(actualOwner, retired.Count == 0 ? (BirGraphValue)root : owner), "owner/callback traversal order changed");
            retired.Add(properties[0]);
        });
        Need(selected.Method.Value == "selected-string" && selected.Virtual.Value && selected.DeclarationId.Value == "selected-call"
            && ReferenceEquals(selected.Recv.Value, receiver) && ReferenceEquals(selected.Args.Value[0], operand),
            "signature precedence, existing dispatch or operand identity changed");
        Need(ambiguous.Method.Value == PhysicalName("value", "get") && !ambiguous.Virtual.IsPresent
            && constrained.Method.Value == "closed-owner" && constrained.PropertyName.Value == "bound",
            "incomplete signature ambiguity or constructed-owner substitution changed");
        Need(ordinaryStatic.Method.Value == PhysicalName("value", "get") && ordinaryStatic.PropertyName.Value == "value",
            "static calls incorrectly used instance/reference allocation");
        Need(referenced.Method.Value == "referenced-get" && referenced.Virtual.Value
            && super.Method.Value == "referenced-get" && !super.Virtual.IsPresent
            && localBeforeRef.Method.Value == "local-first" && !localBeforeRef.Virtual.IsPresent,
            "reference allocation, super dispatch or local-before-reference precedence changed");
        Need(!selected.Prop.IsPresent && !ambiguous.Prop.IsPresent && !constrained.Prop.IsPresent
            && !ordinaryStatic.Prop.IsPresent && !referenced.Prop.IsPresent && !super.Prop.IsPresent,
            "consumed property-call roles survived allocation");
        Need(ReferenceEquals(root.Methods.Value, methods) && ReferenceEquals(root.Properties.Value.Single(), kept)
            && owner.Properties.Value.Length == 0 && retired.Count == 2 && ReferenceEquals(retired[0], fileProperty)
            && ReferenceEquals(retired[1], typeProperty) && getter.Name.Value == PhysicalName("item", "get")
            && setter.Name.Value == "already-narrow" && kept.Get.Value == getter.Name.Value && kept.Set.Value == setter.Name.Value,
            "declarations were copied, a narrowed name was lost, or exact generic-property removals changed");
        Need(fileGeneric.KotlinPropertyAccessorCarrier.Value.Association.Value == "file-association"
            && typeGeneric.KotlinPropertyAccessorCarrier.Value.Name.Value == "type"
            && fileProperty.GetMethodArity.Value == 1 && fileProperty.Set.IsNull && !fileProperty.SetSig.IsPresent
            && fileProperty.PropertyAssociation.Value == "file-association" && fileProperty.KotlinAccessors.Value.Single() == "get",
            "unrepresentable generic accessor metadata or explicit absent accessor state was lost");
        var keptVector = root.Properties.Value;
        AllocateAll(root, refs, locals, (_, _) => throw new InvalidOperationException("Repeated retirement"));
        Need(ReferenceEquals(root.Properties.Value, keptVector) && selected.Method.Value == "selected-string",
            "allocation changed an already-consumed call or rewrote an unchanged owner vector");
        Console.WriteLine("[owning property allocation] self-test OK (exact call selection, dispatch, association, callback-before-detach and identity)");

        (BirFunctionDeclaration Method, BirPropertyDeclaration Property) Generic(string name) => (
            new BirFunctionDeclaration { Name = name, PropertyName = name, PropertyAccessor = "get", PropertyAssociation = name + "-association",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ret = new TypeNode.Tv("method", 0), Params = Array.Empty<BirParameter>() },
            new BirPropertyDeclaration { Name = name, Type = new TypeNode.Tv("method", 0), PropertyAssociation = name + "-association", KotlinAccessors = new[] { "get" } });
    }
}

sealed partial class ReferenceMetadataIndex
{
    // Test-only declared metadata rows through the same catalog constructor as
    // scanned compiler metadata. This is not an actual PE/KLIB roundtrip proof.
    internal static ReferenceMetadataIndex PropertyAllocationSourceTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.DotKtOwners.Add("Property.Ref");
        foreach (var name in new[] { "value", "local" })
            metadata.MemberBindings.Add(new MemberBinding("Property.Ref", "referenced-get", 0, null, false, false,
                ParamTypeNodes: Array.Empty<TypeNode>(), IsVirtual: true, ReturnTypeNode: new TypeNode.Fqn("kotlin.String"),
                SourcePropertyName: name, AccessorKind: "get"));
        return new ReferenceMetadataIndex(new List<ReferenceAssembly> {
            new("property-allocation-test", "property-allocation-test", "1", metadata),
        }, Build(Array.Empty<string>())._compileRefs);
    }
}
