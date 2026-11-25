using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace InheritedDefaultSlots;

public interface IValue { int Read() => 1; }
public class DefaultBase : IValue { }
public class VirtualBase : IValue { public virtual int Read() => 2; }

public static class Driver
{
    public static bool Check(IValue inherited, IValue replaced)
    {
        if (inherited.Read() != 2 || replaced.Read() != 3) return false;
        using var stream = System.IO.File.OpenRead(replaced.GetType().Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        // Runtime dispatch alone accepts the dangling MethodImpl that ILLink rejects.
        bool DeclaresFace(System.Type owner) => md.GetTypeDefinition(
            (TypeDefinitionHandle)MetadataTokens.EntityHandle(owner.MetadataToken)).GetInterfaceImplementations()
            .Any(row => owner.Module.ResolveType(MetadataTokens.GetToken(md.GetInterfaceImplementation(row).Interface)) == typeof(IValue));
        return inherited.GetType().GetInterfaceMap(typeof(IValue)).TargetMethods[0].DeclaringType == typeof(VirtualBase)
            && !DeclaresFace(inherited.GetType()) && DeclaresFace(replaced.GetType());
    }
}
