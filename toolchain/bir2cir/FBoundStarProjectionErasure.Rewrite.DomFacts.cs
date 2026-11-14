using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    // Genuine late DOM consumers use the same slot policies as the owning walk.
    // This adapter reads only facts present at that later clock.
    static TypeSlotFacts DomTypeSlotFacts(JsonObject node, ReferenceMetadataIndex refs = null,
        IReadOnlyDictionary<string, string> aliases = null)
    {
        var kind = Str(node["k"]);
        bool NativeDescriptor(TypeNode type) => refs != null && type is TypeNode.Fqn owner
            && IsNativeDescriptorOwner(owner, refs, aliases);
        return new() {
            Kind = kind,
            PhysicalSignature = Bool(node[GenericRepresentationPolicy.PhysicalDeclarationSignatureKey]),
            HasParameters = node["params"] is JsonArray,
            NativeMethodSignature = _valueSlots?.HasNativeMethodSignature(node) == true,
            DeclarationDescriptor = IsDeclarationOwnerDescriptor(node),
            NativeDescriptorOwner = NativeDescriptor(TypeJson.Read(node["owner"])),
            ExactBridgeOwnerCall = Bool(node[ExactBridgeOwnerCallKey]),
            ClrBound = ClrBoundNode.IsAny(kind),
            ArrayFactory = (node["attrs"] as JsonArray)?.OfType<JsonObject>().Any(attribute =>
                TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrArrayFactory") == true,
            Witness = kind is "cast" or "isInst" or "isInstRef" && KotlinTypeWitness.NeedsWitness(node),
            NativeConstruction = refs != null && TypeJson.Read(node["type"]) is TypeNode.Fqn constructed
                && IsNativeArrayOwner(constructed, refs, aliases),
            AttributeClass = BirTypeLowering.IsAttributeClass(node),
            PhysicalNativePropertyBridge = Bool(node[KotlinPropertyAccessors.PhysicalSlotBridgeKey])
                && new[] { "clrInterfaceImpls", "clrBaseImpls" }.SelectMany(field =>
                    (node[field] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    .Any(edge => NativeDescriptor(TypeJson.Read(edge["owner"]))),
            NativeLocation = _valueSlots?.IsNativeField(node) == true,
            FieldAddress = Bool(node[GenericRepresentationPolicy.FieldAddressKey]),
            ValueTemporaryConstruction = kind == "var" && Bool(node[CallEvalLowering.ValueTemporaryKey])
                && node["init"] is JsonObject initializer && Str(initializer["k"]) == "new"
                && TypeJson.Read(initializer["type"]) == TypeJson.Read(node["type"]),
            LexicalReceiver = node["recv"] is JsonObject receiver
                && (Str(receiver["k"]) == "this" || Bool(receiver[ExactOuterKey])),
            ExactOuter = Bool(node[ExactOuterKey]), TypeDefinition = IsTypeDefinition(node),
        };
    }
}
