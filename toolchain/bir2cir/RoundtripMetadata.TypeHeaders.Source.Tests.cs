using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    internal static void TypeHeaderSelfTest()
    {
        var user = HeaderMarker("Probe.UserAttribute");
        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 1, 0 });
        var source = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = new[] { new TypeNode.Fqn("Probe.Bound") } })
            .MergeNullableFrame(frame);
        var member = new BirFunctionDeclaration { Name = "alreadyStamped" };
        var owner = new BirClassDefinition {
            Name = "Probe.Owner", Kind = "class", RoundtripTypeHeaderPending = true,
            Attrs = new[] { user }, Mods = new BirModifiers { Sealed = true, Inner = true },
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
            RoundtripSupertypesPre = source, Methods = new[] { member },
            StaticCarrier = new BirStaticCarrier { Owner = "Probe.Source" },
        };
        var companion = new BirClassDefinition {
            Name = "Probe.Owner.Named", Kind = "class", RoundtripTypeHeaderPending = true,
            Mods = new BirModifiers { Object = true }, CompanionCarrier = new BirCompanionCarrier {
                Kind = "nested", Owner = "Probe.Owner", Name = "Named", Visibility = "public",
                PhysicalOwner = "Probe.Owner", PhysicalOwnerArity = 2,
            },
        };
        var contractFact = new BirManagedReferenceContract {
            Owner = "Probe.Source", Declaration = OptionalField<string>.Null, Invoke = "$ref$invoke", Slots = new[] { 1 },
        };
        var contract = new BirInterfaceDefinition {
            Name = "Probe.Contract", Kind = "interface", RoundtripTypeHeaderPending = true,
            Mods = new BirModifiers { Fun = true }, ManagedReferenceContract = contractFact,
            KotlinType = new TypeNode.Fqn("Probe.Source", new TypeNode[] { new TypeNode.Star() }),
        };
        var richFact = new BirRichEnumDeclaration {
            Entries = Array.Empty<BirRichEnumEntry>(), Name = "name", Ordinal = "ordinal", Values = "values", ValueOf = "valueOf",
        };
        var rich = new BirClassDefinition { Name = "Probe.Rich", Kind = "class", RoundtripTypeHeaderPending = true,
            RichEnum = richFact, EnumRich = true };
        var entry = new BirEnumEntryDeclaration { Name = "A", Attrs = new[] { user } };
        var enumeration = new BirEnumDefinition {
            Name = "Probe.Enum", Kind = "enum", RoundtripTypeHeaderPending = true, Entries = new[] { entry },
            BasicEnum = new BirBasicEnumCarrier { Underlying = "System.Int32", Entries = new[] {
                new BirBasicEnumCarrierEntry { Name = "A", Ordinal = 0, PhysicalValue = "7" },
            } },
        };
        var value = new BirClassDefinition { Name = "Probe.Value", Kind = "class", RoundtripTypeHeaderPending = true,
            Mods = new BirModifiers { Value = true } };
        var late = new BirClassDefinition { Name = "Probe.Late", Kind = "class", Mods = new BirModifiers { Object = true } };
        var types = new IBirTypeDeclaration[] { owner, companion, contract, rich, enumeration, value, late };
        var file = new BirFileDeclaration { Types = types };
        var methods = owner.Methods.Value;
        var entries = enumeration.Entries.Value;
        StampTypeHeaders(file, runtimeMode: false);
        if (!ReferenceEquals(file.Types.Value, types) || !ReferenceEquals(owner.Methods.Value, methods)
            || !ReferenceEquals(methods[0], member) || !ReferenceEquals(enumeration.Entries.Value, entries)
            || !ReferenceEquals(entries[0], entry) || !ReferenceEquals(owner.Attrs.Value[1], user)
            || !ReferenceEquals(entry.Attrs.Value[0], user) || late.Attrs.IsPresent
            || owner.RoundtripTypeHeaderPending.IsPresent || contract.ManagedReferenceContract.IsPresent
            || owner.RoundtripSupertypesPre.IsPresent || contract.KotlinType.IsPresent
            || companion.CompanionCarrier.IsPresent || rich.RichEnum.IsPresent || rich.EnumRich.IsPresent
            || enumeration.BasicEnum.IsPresent || owner.StaticCarrier.IsPresent)
            throw new InvalidOperationException("Typed type headers changed owning declarations, eligibility or consumed facts");
        ExpectNames(owner.Attrs.Value, ANullableCtx, "Probe.UserAttribute", AKSealed, AKInner, AKStaticCarrier, AKSupertypes);
        ExpectNames(companion.Attrs.Value, ANullableCtx, AKObject, AKCompanion);
        ExpectNames(contract.Attrs.Value, ANullableCtx, AKManagedReferenceContract, AKFunInterface, AKType);
        ExpectNames(rich.Attrs.Value, ANullableCtx, AKRichEnum);
        ExpectNames(value.Attrs.Value, ANullableCtx, AKValue);
        ExpectNames(enumeration.Attrs.Value, AKBasicEnum);
        ExpectNames(entry.Attrs.Value, "Probe.UserAttribute", AKBasicEnumOrdinal);
        var context = owner.Attrs.Value[0];
        if (!context.AttrExternal.OrDefault() || context.AttrAssembly.Value != "System.Runtime"
            || context.Args.Value[0] is not BirAttributeLiteral { Value.Value: BirLiteral.IntegerLiteral { Value: 1 } }
            || owner.Attrs.Value.Single(a => a.Attr.Value == new TypeNode.Fqn(AKInner)).Args.Value[0]
                is not BirAttributeLiteral { Value.Value: BirLiteral.IntegerLiteral { Value: 1 } })
            throw new InvalidOperationException("Typed type headers lost external nullability or captured-inner arity");
        ExpectPayload(owner, AKSupertypes, source.MergeVariances(new Dictionary<int, string> { [0] = "out" }).FrozenText);
        ExpectPayload(owner, AKStaticCarrier, """{"owner":"Probe.Source"}""");
        ExpectPayload(companion, AKCompanion, """{"kind":"nested","owner":"Probe.Owner","name":"Named","visibility":"public","physicalOwner":"Probe.Owner","physicalOwnerArity":2}""");
        ExpectPayload(contract, AKManagedReferenceContract, """{"owner":"Probe.Source","declaration":null,"invoke":"$ref$invoke","slots":[1]}""");
        ExpectPayload(contract, AKType, TypeNode.ToJson(new TypeNode.Fqn("Probe.Source", new TypeNode[] { new TypeNode.Star() })));
        ExpectPayload(enumeration, AKBasicEnum, """{"underlying":"System.Int32","entries":[{"name":"A","ordinal":0,"physicalValue":"7"}]}""");
        ExpectPayload(rich, AKRichEnum, """{"entries":[],"name":"name","ordinal":"ordinal","values":"values","valueOf":"valueOf"}""");
        if (!HasRepresentationTypeAssociations(file)) throw new InvalidOperationException("Typed representation association inventory missed the header result");
        var attrs = owner.Attrs.Value;
        StampTypeHeaders(file, runtimeMode: false);
        if (!ReferenceEquals(owner.Attrs.Value, attrs)) throw new InvalidOperationException("Type header capability was consumed twice");

        RuntimeTypeHeaderSelfTest(user, richFact);
        Console.WriteLine("[typed type headers] self-test OK (phase eligibility, exact carrier bytes/order, frames/variance, enum entries, runtime and continuing owners)");
    }

    static void RuntimeTypeHeaderSelfTest(BirAttribute user, BirRichEnumDeclaration richFact)
    {
        var representation = new BirInterfaceDefinition {
            Name = "Probe.Physical", Kind = "interface", RoundtripTypeHeaderPending = true,
            KotlinType = new TypeNode.Fqn("Probe.Source"), Attrs = new[] { HeaderMarker(AKValue), user },
        };
        var rich = new BirClassDefinition { Name = "Probe.Rich", Kind = "class", RoundtripTypeHeaderPending = true,
            RichEnum = richFact, EnumRich = true, KotlinCompanion = new BirKotlinCompanion { Owner = "Probe.Owner", Name = "Named" },
            StaticCarrier = new BirStaticCarrier { Owner = "Probe.Owner" }, Attrs = new[] { HeaderMarker(ANullableCtx) } };
        var empty = new BirClassDefinition { Name = "Probe.Empty", Kind = "class", RoundtripTypeHeaderPending = true,
            Attrs = Array.Empty<BirAttribute>() };
        var late = new BirClassDefinition { Name = "Probe.Late", Kind = "class", Attrs = new[] { HeaderMarker(AKValue) } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { representation, rich, empty, late } };
        StampTypeHeaders(file, runtimeMode: true);
        ExpectNames(representation.Attrs.Value, "Probe.UserAttribute", AKType);
        ExpectNames(rich.Attrs.Value, AKRichEnum);
        ExpectNames(late.Attrs.Value, AKValue);
        ExpectPayload(representation, AKType, TypeNode.ToJson(new TypeNode.Fqn("Probe.Source")));
        if (!ReferenceEquals(representation.Attrs.Value[0], user) || !HasRepresentationTypeAssociations(file)
            || empty.Attrs.IsPresent || representation.KotlinType.IsPresent || rich.RichEnum.IsPresent
            || rich.EnumRich.IsPresent || rich.KotlinCompanion.IsPresent || rich.StaticCarrier.IsPresent)
            throw new InvalidOperationException("Runtime type headers changed user annotations or retained compiler facts");
    }

    static void ExpectNames(BirAttribute[] attributes, params string[] names)
    {
        if (!attributes.Select(attribute => ((TypeNode.Fqn)attribute.Attr.Value).Name).SequenceEqual(names))
            throw new InvalidOperationException("Typed type header changed attribute order");
    }

    static void ExpectPayload(IBirTypeDeclaration type, string carrier, string expected)
    {
        var attribute = type.Attrs.Value.Single(value => value.Attr.Value == new TypeNode.Fqn(carrier));
        if (attribute.Args.Value[0] is not BirAttributeLiteral { Value.Value: BirLiteral.TextLiteral { Value: BirCarrier.JsonV1 } }
            || attribute.Args.Value[1] is not BirOpaqueBytes bytes || !bytes.Bytes.Value.SequenceEqual(Encoding.UTF8.GetBytes(expected)))
            throw new InvalidOperationException("Typed type header changed a metadata carrier's payload bytes");
    }
}
