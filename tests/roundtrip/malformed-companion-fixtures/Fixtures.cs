using System;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")]

namespace DotKt.Runtime.CompilerServices
{
    [CompilerGenerated]
    public sealed class KotlinFileClassAttribute : Attribute;

    [CompilerGenerated]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class KotlinCompanionAttribute(string version, byte[] content) : Attribute
    {
        public string Version { get; } = version;
        public byte[] Content { get; } = content;
    }

    [CompilerGenerated]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class KotlinStaticCarrierAttribute(string version, byte[] content) : Attribute
    {
        public string Version { get; } = version;
        public byte[] Content { get; } = content;
    }

    [CompilerGenerated]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class KotlinRichEnumAttribute(string version, byte[] content) : Attribute
    {
        public string Version { get; } = version;
        public byte[] Content { get; } = content;
    }

    [CompilerGenerated]
#if !COMPANION_EXTENSION_WRONG_TARGET && !COMPANION_EXTENSION_PROPERTY_TARGET
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Field
#if CONSTRUCTOR_COMPANION_EXTENSION || STATIC_CONSTRUCTOR_COMPANION_EXTENSION
        | AttributeTargets.Constructor
#endif
    )]
#endif
    public sealed class KotlinCompanionExtensionAttribute(string version, byte[] content) : Attribute
    {
        public string Version { get; } = version;
        public byte[] Content { get; } = content;
#if NAMED_ARGUMENT_COMPANION_EXTENSION
        public int Extra { get; set; }
#endif
    }

    [CompilerGenerated]
    public sealed class KotlinObjectAttribute : Attribute;
}

namespace Fixture
{
#if COMPANION_EXTENSION_WRONG_TARGET
    [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            128
        })]
    public sealed class WrongCompanionExtensionTarget;
#elif COMPANION_EXTENSION_PROPERTY_TARGET
    public sealed class WrongCompanionExtensionPropertyTarget
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            128
        })]
        public static int Value => 1;
    }
#elif SPECIAL_NAME_COMPANION_EXTENSION || NAMED_ARGUMENT_COMPANION_EXTENSION
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
#if SPECIAL_NAME_COMPANION_EXTENSION
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,163,103,101,116
#else
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,168,102,117,110,99,116,105,111,110
#endif
        }
#if NAMED_ARGUMENT_COMPANION_EXTENSION
        , Extra = 1
#endif
        )]
#if SPECIAL_NAME_COMPANION_EXTENSION
        public static FileFacade operator +(FileFacade left, FileFacade right) => left;
#else
        public static int Bad() => 1;
#endif
    }
#elif NON_FILE_COMPANION_EXTENSION_METHOD
    public sealed class Owner
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,168,102,117,110,99,116,105,111,110
        })]
        public static int Bad() => 1;
    }
#elif CONSTRUCTOR_COMPANION_EXTENSION || STATIC_CONSTRUCTOR_COMPANION_EXTENSION
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,168,102,117,110,99,116,105,111,110
        })]
#if STATIC_CONSTRUCTOR_COMPANION_EXTENSION
        static FileFacade() { }
#else
        public FileFacade() { }
#endif
    }
#elif PARAMETERIZED_COMPANION_EXTENSION
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            131,168,114,101,99,101,105,118,101,114,131,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,97,114,103,
            115,145,130,161,116,163,102,113,110,164,110,97,109,101,173,107,111,116,108,105,
            110,46,83,116,114,105,110,103,164,110,97,109,101,163,98,97,100,164,107,105,
            110,100,168,102,117,110,99,116,105,111,110
        })]
        public static int Bad() => 1;
    }
#elif MALFORMED_PRIVATE_COMPANION_EXTENSION
    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            128
        })]
        private static int Bad() => 1;
    }
#elif INSTANCE_COMPANION_EXTENSION_METHOD
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,168,102,117,110,99,116,105,111,110
        })]
        public int Bad() => 1;
    }
#elif INSTANCE_COMPANION_EXTENSION_FIELD
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public sealed class FileFacade
    {
        [DotKt.Runtime.CompilerServices.KotlinCompanionExtension("bir-msgpack/1", new byte[] {
            131,168,114,101,99,101,105,118,101,114,130,161,116,163,102,113,110,164,110,97,
            109,101,173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,
            101,163,98,97,100,164,107,105,110,100,165,102,105,101,108,100
        })]
        public int Bad;
    }
#elif EXTRA_STATIC_CARRIER_PAYLOAD
    public sealed class StaticOwner<T>;

    [CompilerGenerated]
    [DotKt.Runtime.CompilerServices.KotlinStaticCarrier("bir-msgpack/1", new byte[] {
            130,165,111,119,110,101,114,179,70,105,120,116,117,114,101,46,83,116,97,116,
            105,99,79,119,110,101,114,170,117,110,101,120,112,101,99,116,101,100,195
        })]
    public static class StaticCarrier;
#elif INSTANCE_STATIC_CARRIER_MEMBER
    public sealed class StaticOwner<T>;

    [CompilerGenerated]
    [DotKt.Runtime.CompilerServices.KotlinStaticCarrier("bir-msgpack/1", new byte[] {
            129,165,111,119,110,101,114,179,70,105,120,116,117,114,101,46,83,116,97,116,
            105,99,79,119,110,101,114
        })]
    public sealed class StaticCarrier
    {
        public int Bad() => 1;
    }
#elif NON_PUBLIC_STATIC_CARRIER
    public sealed class StaticOwner<T>;

    [CompilerGenerated]
    [DotKt.Runtime.CompilerServices.KotlinStaticCarrier("bir-msgpack/1", new byte[] {
            129,165,111,119,110,101,114,179,70,105,120,116,117,114,101,46,83,116,97,116,
            105,99,79,119,110,101,114
        })]
    internal static class StaticCarrier;
#elif NON_GENERIC_STATIC_CARRIER
    public sealed class StaticOwner;

    // The payload is structurally valid, but only a generic semantic owner may require non-generic CLR storage.
    [CompilerGenerated]
    [DotKt.Runtime.CompilerServices.KotlinStaticCarrier("bir-msgpack/1", new byte[] {
            129,165,111,119,110,101,114,179,70,105,120,116,117,114,101,46,83,116,97,116,
            105,99,79,119,110,101,114
        })]
    public static class StaticCarrier;
#elif GENERIC_OWNER_NESTED_CARRIER
    // A generic owner cannot host the single companion singleton: CLR static storage is per closed constructed type,
    // so a carrier claiming to be NESTED in one is malformed however well-formed it looks otherwise.
    public class GenericOwner<T> where T : struct
    {
        [DotKt.Runtime.CompilerServices.KotlinObject]
        [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-msgpack/1", new byte[] {
            134,164,107,105,110,100,166,110,101,115,116,101,100,165,111,119,110,101,114,180,
            70,105,120,116,117,114,101,46,71,101,110,101,114,105,99,79,119,110,101,114,
            164,110,97,109,101,169,67,111,109,112,97,110,105,111,110,170,118,105,115,105,
            98,105,108,105,116,121,166,112,117,98,108,105,99,173,112,104,121,115,105,99,
            97,108,79,119,110,101,114,180,70,105,120,116,117,114,101,46,71,101,110,101,
            114,105,99,79,119,110,101,114,178,112,104,121,115,105,99,97,108,79,119,110,
            101,114,65,114,105,116,121,1
        })]
        public sealed class Carrier
        {
            public static readonly Carrier XINSTANCE = new();
        }
    }
#elif NESTED_SIDECAR_CARRIER
    // A hoisted carrier's whole point is to leave its generic owner, so one that is still nested inside it would
    // reintroduce the per-instantiation singleton the metadata claims does not exist.
    public class GenericOwner<T>
    {
        [DotKt.Runtime.CompilerServices.KotlinObject]
        [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-msgpack/1", new byte[] {
            134,164,107,105,110,100,167,115,105,100,101,99,97,114,165,111,119,110,101,114,
            180,70,105,120,116,117,114,101,46,71,101,110,101,114,105,99,79,119,110,101,
            114,164,110,97,109,101,169,67,111,109,112,97,110,105,111,110,170,118,105,115,
            105,98,105,108,105,116,121,166,112,117,98,108,105,99,173,112,104,121,115,105,
            99,97,108,79,119,110,101,114,180,70,105,120,116,117,114,101,46,71,101,110,
            101,114,105,99,79,119,110,101,114,178,112,104,121,115,105,99,97,108,79,119,
            110,101,114,65,114,105,116,121,1
        })]
        public sealed class Carrier
        {
            public static readonly Carrier XINSTANCE = new();
        }
    }
#elif NON_GENERIC_SIDECAR_CARRIER
    // Hoisting is the answer to a generic owner alone. A non-generic owner keeps its nested carrier, so a sidecar
    // claim over one is a representation that no compiler run produces.
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinObject]
    [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-msgpack/1", new byte[] {
            134,164,107,105,110,100,167,115,105,100,101,99,97,114,165,111,119,110,101,114,
            173,70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,101,169,
            67,111,109,112,97,110,105,111,110,170,118,105,115,105,98,105,108,105,116,121,
            166,112,117,98,108,105,99,173,112,104,121,115,105,99,97,108,79,119,110,101,
            114,173,70,105,120,116,117,114,101,46,79,119,110,101,114,178,112,104,121,115,
            105,99,97,108,79,119,110,101,114,65,114,105,116,121,0
        })]
    public sealed class Carrier
    {
        public static readonly Carrier XINSTANCE = new();
    }
#elif RICH_ENUM_MISSING_FIELD
    // Current-format carrier with a valid codec and payload shape but a broken declaration relation. Both consumers
    // must fail closed rather than treating the physical class as an ordinary source class or guessing FIRST.
    [DotKt.Runtime.CompilerServices.KotlinRichEnum("bir-msgpack/1", new byte[] {
            133,167,101,110,116,114,105,101,115,145,130,164,110,97,109,101,165,70,73,82,
            83,84,165,102,105,101,108,100,167,77,73,83,83,73,78,71,164,110,97,109,
            101,166,95,95,110,97,109,101,167,111,114,100,105,110,97,108,169,95,95,111,
            114,100,105,110,97,108,166,118,97,108,117,101,115,166,118,97,108,117,101,115,
            167,118,97,108,117,101,79,102,167,118,97,108,117,101,79,102
        })]
    public class RichEnum
    {
        public static readonly RichEnum FIRST = new();
        public readonly string __name = "FIRST";
        public readonly int __ordinal;

        [CompilerGenerated]
        public static RichEnum[] values() => [FIRST];

        [CompilerGenerated]
        public static RichEnum valueOf(string name) => FIRST;
    }
#elif RICH_ENUM_GENERIC_API
    // Current carrier whose named APIs have the right value-parameter and return shapes but an unusable generic arity.
    [DotKt.Runtime.CompilerServices.KotlinRichEnum("bir-msgpack/1", new byte[] {
            133,167,101,110,116,114,105,101,115,145,130,164,110,97,109,101,165,70,73,82,
            83,84,165,102,105,101,108,100,165,70,73,82,83,84,164,110,97,109,101,166,
            95,95,110,97,109,101,167,111,114,100,105,110,97,108,169,95,95,111,114,100,
            105,110,97,108,166,118,97,108,117,101,115,166,118,97,108,117,101,115,167,118,
            97,108,117,101,79,102,167,118,97,108,117,101,79,102
        })]
    public class RichEnum
    {
        public static readonly RichEnum FIRST = new();
        public readonly string __name = "FIRST";
        public readonly int __ordinal;

        [CompilerGenerated]
        public static RichEnum[] values<T>() => [FIRST];

        [CompilerGenerated]
        public static RichEnum valueOf<T>(string name) => FIRST;
    }
#elif NON_PUBLIC_CARRIER
    public class Owner
    {
        [DotKt.Runtime.CompilerServices.KotlinObject]
        [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-msgpack/1", new byte[] {
            134,164,107,105,110,100,166,110,101,115,116,101,100,165,111,119,110,101,114,173,
            70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,101,169,67,
            111,109,112,97,110,105,111,110,170,118,105,115,105,98,105,108,105,116,121,166,
            112,117,98,108,105,99,173,112,104,121,115,105,99,97,108,79,119,110,101,114,
            173,70,105,120,116,117,114,101,46,79,119,110,101,114,178,112,104,121,115,105,
            99,97,108,79,119,110,101,114,65,114,105,116,121,0
        })]
        protected sealed class Carrier
        {
            public static readonly Carrier XINSTANCE = new();
        }
    }
#else
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinObject]
    [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-msgpack/1", new byte[] {
            134,164,107,105,110,100,166,110,101,115,116,101,100,165,111,119,110,101,114,173,
            70,105,120,116,117,114,101,46,79,119,110,101,114,164,110,97,109,101,169,67,
            111,109,112,97,110,105,111,110,170,118,105,115,105,98,105,108,105,116,121,166,
            112,117,98,108,105,99,173,112,104,121,115,105,99,97,108,79,119,110,101,114,
            173,70,105,120,116,117,114,101,46,79,119,110,101,114,178,112,104,121,115,105,
            99,97,108,79,119,110,101,114,65,114,105,116,121,0
        })]
    public sealed class Carrier;
#endif
}
