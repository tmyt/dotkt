using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace InheritedDefaultSlots;

public interface IValue { int Read() => 1; }
public interface IOverloadedReader<T> {
    int Read(T value) => 1;
    int Read(string? value) => 2;
}
public class IntReader : IOverloadedReader<int> { }
public interface IDefaultChannel<T> {
    object Read(T key) => "default";
    int Tick() => 11;
}
public class KeyBox<T> {}
public class SlotFamily {
    protected interface IProtected { int Tick() => 11; }
    public class Native : SlotFamily, IProtected {}
    public static int Tick(Native instance) => ((IProtected)instance).Tick();
}
public class ExplicitDefaultBase<T> : IDefaultChannel<T> {
    object IDefaultChannel<T>.Read(T key) => "explicit";
    public virtual object Read(T key) => "base";
}
public class NewDefaultNamesake : ExplicitDefaultBase<string> {
    public new virtual object Read(string key) => "new";
}
public class DefaultBase : IValue { }
public class IntroducesVirtualBody : DefaultBase { public virtual int Read() => 70; }
public class VirtualBase : IValue { public virtual int Read() => 2; }
public interface IWithProperty { int Read() => 1; int Value => 1; }
public class WithProperty : IWithProperty { public int Value => 2; }

public interface IChannel<T> { int Count { get; } int Read(T key); }
public interface ITextChannel : IChannel<string> {}
public class Channel<T> : IChannel<T> {
    public virtual int Count => 10;
    public virtual int Read(T key) => 11;
}
public class TextChannel : Channel<string> {}
public class HiddenChannel : TextChannel { public new virtual int Read(string key) => 12; }
public class ExplicitChannel : IChannel<string> {
    public int Count => 10;
    public virtual int Read(string key) => 5;
    int IChannel<string>.Read(string key) => 4;
}

public static class Driver
{
    public static int ReadNumber(IOverloadedReader<int> reader) => reader.Read(1);
    public static int ReadTextValue(IOverloadedReader<int> reader) => reader.Read("x");
    public static object ReadDefaultChannel(IDefaultChannel<string> channel) => channel.Read("x");
    public static object ReadDefaultBase(ExplicitDefaultBase<string> instance) => instance.Read("x");
    public static object ReadBoxBase(ExplicitDefaultBase<KeyBox<string>> instance) => instance.Read(new());
    public static object ReadBoxChannel(IDefaultChannel<KeyBox<string>> channel) => channel.Read(new());
    public static int Tick(IDefaultChannel<KeyBox<string>> channel) => channel.Tick();
    public static int ReadChannel(IChannel<string> channel) => channel.Read("x");
    public static int ReadText(ITextChannel channel) => channel.Read("x");
    public static int ReadProperty(IWithProperty instance) => instance.Value;
    public static int ReadInterface(IValue instance) => instance.Read();
    public static bool CheckChannel(ITextChannel inherited, ITextChannel hidden)
    {
        var type = inherited.GetType();
        using var stream = System.IO.File.OpenRead(type.Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var def = md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.EntityHandle(type.MetadataToken));
        var hiddenTarget = hidden.GetType().GetInterfaceMap(typeof(IChannel<string>)).TargetMethods.Single(m => m.ReturnType == typeof(int) && m.GetParameters().Length == 1);
        return def.GetMethodImplementations().Count == 0
            && !def.GetMethods().Any(h => md.GetString(md.GetMethodDefinition(h).Name).StartsWith("dotkt$ovslot$", System.StringComparison.Ordinal))
            && hiddenTarget.DeclaringType == hidden.GetType() && hiddenTarget.IsPrivate
            && type.GetInterfaceMap(typeof(IChannel<string>)).TargetMethods.All(m => m.DeclaringType == typeof(Channel<string>))
            && inherited.Count == 10 && inherited.Read("x") == 11 && hidden.Count == 10 && hidden.Read("x") == 12;
    }
    public static bool Check(IValue inherited, IValue replaced)
    {
        if (inherited.Read() != 2 || replaced.Read() != 3) return false;
        using var stream = System.IO.File.OpenRead(replaced.GetType().Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        // Runtime dispatch alone accepts the dangling MethodImpl that ILLink rejects.
        bool DeclaresFace(System.Type owner) => md.GetTypeDefinition(
            (TypeDefinitionHandle)MetadataTokens.EntityHandle(owner.MetadataToken)).GetInterfaceImplementations()
            .Any(row => typeof(IValue).IsAssignableFrom(owner.Module.ResolveType(MetadataTokens.GetToken(md.GetInterfaceImplementation(row).Interface))));
        return inherited.GetType().GetInterfaceMap(typeof(IValue)).TargetMethods[0].DeclaringType == typeof(VirtualBase)
            && !DeclaresFace(inherited.GetType()) && DeclaresFace(replaced.GetType());
    }
}
