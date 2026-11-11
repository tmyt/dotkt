extern alias projection;
using System.Reflection;
using System.Text;
using P = projection::DotKt.Bir;
using KType = projection::DotKt.Klib.Metadata.Type;
using KValue = projection::DotKt.Klib.Metadata.Annotation.Types.Argument.Types.Value;

internal static class SourceMetadataProjectionTests
{
    // Inspect the production DLL-to-KLIB projection and actual protobuf facts. The
    // two tools link separate model copies; the external alias preserves that boundary.
    public static void Run(string json)
    {
        var source = P.BirCarrier.DecodeBody(P.BirCarrier.JsonV1, Encoding.UTF8.GetBytes(json), P.SourceMetadataJson.Read);
        var tool = typeof(P.SourceTypeAlias).Assembly;
        var projection = tool.GetType("KotlinTypeAliasMetadata", true)!;
        var namesType = tool.GetType("NameTable", true)!;
        var names = Activator.CreateInstance(namesType, nonPublic: true)!;
        var type = projection.GetMethod("Type", BindingFlags.Static | BindingFlags.NonPublic)!;
        var value = projection.GetMethod("Value", BindingFlags.Static | BindingFlags.NonPublic)!;
        string ClassName(int id) => (string)namesType.GetMethod("ClassName")!.Invoke(names, [id])!;
        string String(int id) => (string)namesType.GetMethod("StringValue")!.Invoke(names, [id])!;
        KType Project(P.SourceType node) => (KType)type.Invoke(null, [node, names])!;
        var underlying = Project(source.UnderlyingType);
        var expanded = Project(source.ExpandedType);
        Require(underlying.HasTypeAliasName && ClassName(underlying.TypeAliasName) == "sample.Outer.Inner");
        Require(expanded.HasClassName && ClassName(expanded.ClassName) == "sample.Outer.Inner");
        Require(expanded.Argument.Select(argument => argument.Projection).SequenceEqual(new[] {
            KType.Types.Argument.Types.Projection.Inv, KType.Types.Argument.Types.Projection.In,
            KType.Types.Argument.Types.Projection.Out, KType.Types.Argument.Types.Projection.Star }));
        Require(expanded.Argument[0].Type.HasTypeParameter && expanded.Argument[0].Type.TypeParameter == 0);
        Require(expanded.Argument[1].Type.HasTypeAliasName && expanded.Argument[3].Type is null);
        Require(expanded.OuterType.TypeParameter == 0 && expanded.AbbreviatedType.HasTypeAliasName);
        Require(expanded.FlexibleUpperBound.HasClassName &&
            String(expanded.FlexibleTypeCapabilitiesId) == "kotlin.jvm.PlatformType");

        var constants = source.Annotations.Single().Arguments.Select(argument => argument.Value).ToArray();
        var projected = constants.Select(node => (KValue)value.Invoke(null, [node, names])!).ToArray();
        Require(projected.Select(node => node.Type).SequenceEqual(new[] {
            KValue.Types.Type.Byte, KValue.Types.Type.Char, KValue.Types.Type.Short, KValue.Types.Type.Int,
            KValue.Types.Type.Long, KValue.Types.Type.Boolean, KValue.Types.Type.String, KValue.Types.Type.Float,
            KValue.Types.Type.Double, KValue.Types.Type.Class, KValue.Types.Type.Enum,
            KValue.Types.Type.Annotation, KValue.Types.Type.Array }));
        Require(projected[0].IntValue == -128 && projected[1].IntValue == 0xd800 &&
            projected[2].IntValue == -32768 && projected[3].IntValue == int.MinValue);
        Require(projected[4].IntValue == -1 && projected[4].Flags == 1 && projected[5].IntValue == 1);
        Require(String(projected[6].StringValue) == "quote\"\\\n\u0001κ😀");
        Require(BitConverter.SingleToInt32Bits(projected[7].FloatValue) == unchecked((int)0xffc00017));
        Require(BitConverter.DoubleToInt64Bits(projected[8].DoubleValue) == long.MinValue);
        Require(ClassName(projected[9].ClassId) == "sample.Outer.Inner" && projected[9].ArrayDimensionCount == 2);
        Require(ClassName(projected[10].ClassId) == "sample.Values" && String(projected[10].EnumValueId) == "ENTRY");
        Require(ClassName(projected[11].Annotation.Id) == "sample.Nested" &&
            String(projected[11].Annotation.Argument.Single().Value.StringValue) == "nested");
        Require(projected[12].ArrayElement.Single().IntValue == 42);
        Console.WriteLine("Actual Kotlin source metadata -> typed DLL-to-KLIB type/constant projection: passed");
    }

    static void Require(bool condition)
    {
        if (!condition) throw new Exception("Source metadata KLIB projection changed a source fact");
    }
}
