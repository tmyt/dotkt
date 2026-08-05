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
    public sealed class KotlinObjectAttribute : Attribute;
}

namespace Fixture
{
#if CONSTRAINED_CARRIER
    public class GenericOwner<T> where T : struct
    {
        [DotKt.Runtime.CompilerServices.KotlinObject]
        [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-json/1", new byte[] {
            123,34,107,105,110,100,34,58,34,110,101,115,116,101,100,34,
            44,34,111,119,110,101,114,34,58,34,70,105,120,116,117,114,101,46,71,101,110,101,114,105,99,79,119,110,101,114,34,
            44,34,110,97,109,101,34,58,34,67,111,109,112,97,110,105,111,110,34,
            44,34,118,105,115,105,98,105,108,105,116,121,34,58,34,112,117,98,108,105,99,34,
            44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,34,58,
            34,70,105,120,116,117,114,101,46,71,101,110,101,114,105,99,79,119,110,101,114,34,
            44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,65,114,105,116,121,34,58,49,125
        })]
        public sealed class Carrier
        {
            public static readonly Carrier XINSTANCE = new();
        }
    }
#elif NON_PUBLIC_CARRIER
    public class Owner
    {
        [DotKt.Runtime.CompilerServices.KotlinObject]
        [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-json/1", new byte[] {
            123,34,107,105,110,100,34,58,34,110,101,115,116,101,100,34,
            44,34,111,119,110,101,114,34,58,34,70,105,120,116,117,114,101,46,79,119,110,101,114,34,
            44,34,110,97,109,101,34,58,34,67,111,109,112,97,110,105,111,110,34,
            44,34,118,105,115,105,98,105,108,105,116,121,34,58,34,112,117,98,108,105,99,34,
            44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,34,58,
            34,70,105,120,116,117,114,101,46,79,119,110,101,114,34,
            44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,65,114,105,116,121,34,58,48,125
        })]
        protected sealed class Carrier
        {
            public static readonly Carrier XINSTANCE = new();
        }
    }
#else
    public sealed class Owner;

    [DotKt.Runtime.CompilerServices.KotlinObject]
    [DotKt.Runtime.CompilerServices.KotlinCompanion("bir-json/1", new byte[] {
        123,34,107,105,110,100,34,58,34,110,101,115,116,101,100,34,
        44,34,111,119,110,101,114,34,58,34,70,105,120,116,117,114,101,46,79,119,110,101,114,34,
        44,34,110,97,109,101,34,58,34,67,111,109,112,97,110,105,111,110,34,
        44,34,118,105,115,105,98,105,108,105,116,121,34,58,34,112,117,98,108,105,99,34,
        44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,34,58,
        34,70,105,120,116,117,114,101,46,79,119,110,101,114,34,
        44,34,112,104,121,115,105,99,97,108,79,119,110,101,114,65,114,105,116,121,34,58,48,125
    })]
    public sealed class Carrier;
#endif
}
