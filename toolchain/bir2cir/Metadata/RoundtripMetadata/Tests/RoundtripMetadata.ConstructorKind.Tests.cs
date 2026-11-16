using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    static void ConstructorKindSelfTest()
    {
        foreach (var runtime in new[] { false, true })
        {
            var constructors = new[] {
                new BirConstructor { DeclarationId = "primary", DeclarationSourceName = ".ctor", Primary = true,
                    Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() },
                new BirConstructor { DeclarationId = "secondary", DeclarationSourceName = ".ctor", Primary = false,
                    Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() },
                new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() },
            };
            var owner = new BirClassDefinition { Name = "ConstructorKinds", Kind = "class", Ctors = constructors };
            var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
            // Both maintained transports retain a present false, not just true.
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) file.WriteJson(writer);
            foreach (var restored in new[] { BirGraphJson.ReadFile(bytes.ToArray()), BirFileCodec.Decode(BirFileCodec.Encode(file)) })
            {
                var rows = restored.Types.Value[0].Ctors.Value;
                if (!rows[0].Primary.HasValue || !rows[0].Primary.Value || !rows[1].Primary.HasValue
                    || rows[1].Primary.Value || rows[2].Primary.IsPresent)
                    throw new InvalidOperationException("Constructor kind transport collapsed false or invented a synthesized source kind");
            }
            PrepareMetadata(file, runtime, (_, _, _, _) => { });
            if (!ReferenceEquals(owner.Ctors.Value, constructors) || constructors.Any(row => row.Primary.IsPresent)
                || constructors[0].RoundtripDeclarationIdentity.Value.ConstructorPrimary.Value != true
                || !constructors[1].RoundtripDeclarationIdentity.Value.ConstructorPrimary.HasValue
                || constructors[1].RoundtripDeclarationIdentity.Value.ConstructorPrimary.Value != false
                || constructors[2].RoundtripDeclarationIdentity.IsPresent)
                throw new InvalidOperationException("Constructor kind did not move to exact metadata custody before physical projection");
            CompleteMetadata(file, runtime);
            for (var index = 0; index < 2; index++)
            {
                var payload = AttributePayload(constructors[index].Attrs.Value.Single());
                if (payload["constructorPrimary"]?.GetValue<bool>() != (index == 0)
                    || constructors[index].RoundtripDeclarationIdentity.IsPresent
                    || payload["name"]?.GetValue<string>() != ".ctor")
                    throw new InvalidOperationException("Reference/runtime constructor metadata lost the exact source classification");
            }
        }
        Console.WriteLine("[constructor kind metadata] self-test OK (source true/false, absent synthetic, both codecs and reference/runtime custody)");
    }
}
