using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// A private source member can be reached through its compiler-authored value carrier without a caller-side
// UnsafeAccessor. Pin the complete replacement path, not merely a smaller count of accessors.
static class UnsafeCarrierInspector
{
    const string Prefix = "DotKt.Runtime.CompilerServices.";

    public static void Verify(string consumerPath, string producerPath)
    {
        using var consumerStream = File.OpenRead(consumerPath);
        using var consumerPe = new PEReader(consumerStream);
        var consumer = consumerPe.GetMetadataReader();
        using var producerStream = File.OpenRead(producerPath);
        using var producerPe = new PEReader(producerStream);
        var producer = producerPe.GetMetadataReader();

        TypeDefinitionHandle Source(string name) => producer.TypeDefinitions.Single(handle =>
            Strip(Name(producer, handle)).Replace('+', '.') == name);
        TypeDefinitionHandle Carrier(string name) => producer.TypeDefinitions.Single(handle =>
        {
            if ((producer.GetTypeDefinition(handle).Attributes & TypeAttributes.Interface) == 0) return false;
            var fact = Fact(producer, producer.GetTypeDefinition(handle).GetCustomAttributes(), "KotlinType");
            return fact?.GetProperty("name").GetString() == name;
        });

        void VerifyPath(string ownerName, MethodDefinitionHandle originalHandle,
            Func<MethodDefinitionHandle, bool> selectsSlot, bool generic = false, bool state = false)
        {
            var source = Source(ownerName);
            var carrier = Carrier(ownerName);
            var original = producer.GetMethodDefinition(originalHandle);
            Require(original.GetDeclaringType() == source
                && (original.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
                ownerName + ": original member is not private");
            if (!state)
                Require(Fact(producer, original.GetCustomAttributes(), "KotlinCallableVisibility")?
                    .GetProperty("visibility").GetString() == "private", ownerName + ": source visibility metadata lost");
            var slotHandle = producer.GetTypeDefinition(carrier).GetMethods().Single(selectsSlot);
            var slot = producer.GetMethodDefinition(slotHandle);
            Require((slot.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                && (slot.Attributes & MethodAttributes.Abstract) != 0,
                ownerName + ": value slot is not a public abstract interface member");
            Signature(producer, slot.Signature, generic, state);
            if (generic)
                Require(original.GetGenericParameters().Count == 2, ownerName + ": original method frame lost");
            var slotName = producer.GetString(slot.Name);
            var bridgeRow = producer.GetTypeDefinition(source).GetMethodImplementations()
                .Select(producer.GetMethodImplementation).Single(row =>
                    row.MethodDeclaration == slotHandle || row.MethodDeclaration.Kind == HandleKind.MemberReference
                    && Member(producer, (MemberReferenceHandle)row.MethodDeclaration, Name(producer, carrier), slotName));
            Require(bridgeRow.MethodBody.Kind == HandleKind.MethodDefinition, ownerName + ": no physical bridge body");
            var bridgeHandle = (MethodDefinitionHandle)bridgeRow.MethodBody;
            var bridge = producer.GetMethodDefinition(bridgeHandle);
            Require((bridge.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private
                && bridge.RelativeVirtualAddress != 0 && bridge.GetGenericParameters().Count == (generic ? 2 : 0),
                ownerName + ": physical bridge visibility/body/frame changed");
            Signature(producer, bridge.Signature, generic, state);
            var originalName = producer.GetString(original.Name);
            Require(Calls(producerPe, producer, bridge).Any(target =>
            {
                if (target.Kind == HandleKind.MethodDefinition) return target == originalHandle;
                if (target.Kind != HandleKind.MemberReference) return false;
                return Member(producer, (MemberReferenceHandle)target, Name(producer, source), originalName);
            }), ownerName + ": carrier MethodImpl does not forward to the private source member");
            var uses = consumer.MethodDefinitions.SelectMany(handle =>
                    Calls(consumerPe, consumer, consumer.GetMethodDefinition(handle)))
                .Where(handle => handle.Kind == HandleKind.MemberReference)
                .Select(handle => (MemberReferenceHandle)handle)
                .Where(handle => Member(consumer, handle, Name(producer, carrier), slotName)).ToArray();
            Require(uses.Length != 0, ownerName + ": consumer never calls the metadata-selected carrier slot");
            foreach (var use in uses) Signature(consumer, consumer.GetMemberReference(use).Signature, generic, state);
            Require(producer.GetTypeDefinition(source).GetGenericParameters().Count == 2,
                ownerName + ": original owner frame changed");
        }

        foreach (var owner in new[] { "GenericPrivateDefaultOwner", "ConstrainedPrivateDefaultOwner",
                     "GenericPrivateMethodDefaultOwner", "NestedGenericPrivateDefaultOwner.Entry" })
        {
            var name = "roundtrip.nc." + owner;
            var property = producer.GetTypeDefinition(Source(name)).GetProperties()
                .Select(producer.GetPropertyDefinition).Single(p => producer.GetString(p.Name) == "secret");
            var getter = property.GetAccessors().Getter;
            Require(!getter.IsNil, name + ": private source property lost its MethodSemantics getter");
            var propertySlots = producer.GetTypeDefinition(Carrier(name)).GetMethods()
                .Select(handle => Fact(producer, producer.GetMethodDefinition(handle).GetCustomAttributes(), "KotlinPropertyAccessor"))
                .Where(fact => fact?.GetProperty("name").GetString() == "secret").Select(fact => fact!.Value).ToArray();
            Require(propertySlots.Length == 1 && propertySlots[0].GetProperty("kind").GetString() == "get",
                name + ": private readonly property has an ambiguous carrier association");
            var association = propertySlots[0].GetProperty("association").GetString();
            Require(!string.IsNullOrEmpty(association) && producer.GetTypeDefinition(Carrier(name)).GetMethods()
                .Count(handle => Fact(producer, producer.GetMethodDefinition(handle).GetCustomAttributes(), "KotlinPropertyAccessor")?
                    .GetProperty("association").GetString() == association) == 1,
                name + ": carrier property association is missing or shared by an unrelated member");
            VerifyPath(name, getter, handle =>
            {
                var fact = Fact(producer, producer.GetMethodDefinition(handle).GetCustomAttributes(), "KotlinPropertyAccessor");
                return fact?.GetProperty("name").GetString() == "secret"
                    && fact.Value.GetProperty("kind").GetString() == "get"
                    && fact.Value.GetProperty("association").GetString() == association;
            });
        }
        foreach (var (owner, member, generic) in new[] {
                     ("GenericPrivateMethodDefaultOwner", "identity", true),
                     ("GenericPrivateCallableDefaultOwner", "secretValue", false) })
        {
            var name = "roundtrip.nc." + owner;
            var original = producer.GetTypeDefinition(Source(name)).GetMethods()
                .Single(handle => producer.GetString(producer.GetMethodDefinition(handle).Name) == member);
            var identity = Fact(producer, producer.GetMethodDefinition(original).GetCustomAttributes(), "KotlinDeclarationIdentity")!
                .Value.GetProperty("id").GetString();
            VerifyPath(name, original, handle =>
            {
                var fact = Fact(producer, producer.GetMethodDefinition(handle).GetCustomAttributes(), "KotlinSourceMethod");
                return fact?.GetProperty("name").GetString() == member
                    && fact.Value.GetProperty("sourceDeclaration").GetString() == identity;
            }, generic);
        }
        foreach (var name in new[] { "ConstrainedPrivateDefaultOwner", "GenericPrivateCallableDefaultOwner" })
            Require(producer.GetTypeDefinition(Source("roundtrip.nc." + name)).GetGenericParameters()
                .Any(handle => producer.GetGenericParameter(handle).GetConstraints().Any(constraint =>
                    Name(producer, producer.GetGenericParameterConstraint(constraint).Type) == "roundtrip.nc.PrivateDefaultBound")),
                name + ": original exact owner constraint lost");

        const string lateinit = "roundtrip.lateinitstorage.LateinitBox";
        var valueGetter = producer.GetTypeDefinition(Source(lateinit)).GetProperties()
            .Select(producer.GetPropertyDefinition).Single(p => producer.GetString(p.Name) == "value").GetAccessors().Getter;
        var edge = Fact(producer, producer.GetMethodDefinition(valueGetter).GetCustomAttributes(), "KotlinLateinitState")!.Value;
        Require(edge.GetProperty("owner").GetString() == lateinit && !edge.GetProperty("static").GetBoolean(),
            "lateinit getter lost its exact instance state edge");
        var stateName = edge.GetProperty("method").GetString();
        var query = producer.GetTypeDefinition(Source(lateinit)).GetMethods()
            .Single(handle => producer.GetString(producer.GetMethodDefinition(handle).Name) == stateName);
        VerifyPath(lateinit, query, handle =>
            Fact(producer, producer.GetMethodDefinition(handle).GetCustomAttributes(), "KotlinSourceMethod")?
                .GetProperty("name").GetString() == stateName, state: true);
    }

    static void Signature(MetadataReader md, BlobHandle handle, bool generic, bool state)
    {
        var blob = md.GetBlobReader(handle);
        var header = blob.ReadSignatureHeader();
        Require(header.IsInstance && (header.IsGeneric ? blob.ReadCompressedInteger() : 0) == (generic ? 2 : 0),
            "carrier member has the wrong instance/generic frame");
        Require(blob.ReadCompressedInteger() == (generic ? 1 : 0), "carrier parameter count changed");
        if (generic)
            Require(blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericMethodParameter && blob.ReadCompressedInteger() == 1
                && blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericMethodParameter && blob.ReadCompressedInteger() == 1,
                "generic carrier did not retain method storage parameter/return frame");
        else Require(blob.ReadSignatureTypeCode() == (state ? SignatureTypeCode.Boolean : SignatureTypeCode.Object),
            "carrier result does not match its physical value/state contract");
        Require(blob.RemainingBytes == 0, "carrier signature has unaccounted physical slots");
    }

    static JsonElement? Fact(MetadataReader md, CustomAttributeHandleCollection attrs, string name)
    {
        var selected = attrs.Select(md.GetCustomAttribute).Where(attr =>
        {
            var owner = attr.Constructor.Kind == HandleKind.MemberReference
                ? md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent
                : md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
            return Name(md, owner) == Prefix + name + "Attribute";
        }).ToArray();
        if (selected.Length == 0) return null;
        Require(selected.Length == 1, "duplicate metadata fact " + name);
        var blob = md.GetBlobReader(selected[0].Value);
        Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "unexpected metadata codec");
        using var document = JsonDocument.Parse(blob.ReadBytes(blob.ReadInt32()));
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected metadata arguments");
        return document.RootElement.Clone();
    }

    static bool Member(MetadataReader md, MemberReferenceHandle handle, string owner, string name)
    {
        var member = md.GetMemberReference(handle);
        return member.GetKind() == MemberReferenceKind.Method && md.GetString(member.Name) == name && Name(md, member.Parent) == owner;
    }

    static string Name(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeSpecification)
        {
            var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
            Require(blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance
                && blob.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle, "unexpected constructed owner");
            return Name(md, blob.ReadTypeHandle());
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = md.GetTypeDefinition((TypeDefinitionHandle)handle);
            return type.GetDeclaringType().IsNil ? Join(md.GetString(type.Namespace), md.GetString(type.Name))
                : Name(md, type.GetDeclaringType()) + "+" + md.GetString(type.Name);
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = md.GetTypeReference((TypeReferenceHandle)handle);
            return type.ResolutionScope.Kind != HandleKind.TypeReference ? Join(md.GetString(type.Namespace), md.GetString(type.Name))
                : Name(md, type.ResolutionScope) + "+" + md.GetString(type.Name);
        }
        return "";
    }

    static IEnumerable<EntityHandle> Calls(PEReader pe, MetadataReader md, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0) yield break;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(op => unchecked((ushort)op.Value));
        for (var index = 0; index < il.Length;)
        {
            var first = il[index++];
            var op = codes[first == 0xfe ? (ushort)(0xfe00 | il[index++]) : first];
            var size = op.OperandType switch {
                OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index), _ => 4 };
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                var target = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, index));
                if (target.Kind == HandleKind.MethodSpecification) target = md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                yield return target;
            }
            index += size;
        }
    }

    static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    static string Strip(string name) => string.Join('+', name.Split('+').Select(part => part.Split('`')[0]));
    static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
}
