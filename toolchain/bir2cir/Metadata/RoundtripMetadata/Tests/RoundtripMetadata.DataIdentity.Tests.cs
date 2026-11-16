using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    static void DataIdentitySelfTest()
    {
        foreach (var runtime in new[] { false, true })
        {
            var data = new BirClassDefinition { Name = "Probe.Data", Kind = "class",
                Mods = new BirModifiers { Data = true } };
            var ordinary = new BirClassDefinition { Name = "Probe.Ordinary", Kind = "class" };
            var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { data, ordinary } };
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) file.WriteJson(writer);
            foreach (var restored in new[] { BirGraphJson.ReadFile(bytes.ToArray()), BirFileCodec.Decode(BirFileCodec.Encode(file)) })
            {
                if (restored.Types.Value[0].Mods.Value.Data.OrDefault() != true || restored.Types.Value[1].Mods.IsPresent)
                    throw new InvalidOperationException("BIR transport lost source data identity or invented ordinary-class modifiers");
                PrepareMetadata(restored, runtime, (_, _, _, _) => { });
                StampTypeHeaders(restored, runtime);
                if (restored.Types.Value[0].Mods.Value.Data.IsPresent
                    || restored.Types.Value[0].Attrs.OrEmpty().Count(a => a.Attr.Value == new TypeNode.Fqn(AKData)) != 1
                    || restored.Types.Value[1].Attrs.OrEmpty().Any(a => a.Attr.Value == new TypeNode.Fqn(AKData)))
                    throw new InvalidOperationException("Source data identity did not survive reference/runtime header generation");
                var attributes = restored.Types.Value[0].Attrs.Value;
                StampTypeHeaders(restored, runtime);
                if (!ReferenceEquals(attributes, restored.Types.Value[0].Attrs.Value))
                    throw new InvalidOperationException("Data identity was stamped twice after consuming header eligibility");
            }
        }
        Console.WriteLine("[data identity] self-test OK (both BIR codecs, reference/runtime metadata, ordinary control)");
    }
}
