using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class EntryPointApartment
{
    static bool IsFrameworkMarker(ReferenceMetadataIndex refs, TypeNode type, string assembly)
    {
        if (type is not TypeNode.Fqn { Name: "System.STAThreadAttribute" or "System.MTAThreadAttribute" } marker)
            return false;
        var selected = assembly == null ? refs.ResolveRefType(marker.Name) : refs.ResolveRefTypeIn(marker.Name, assembly);
        var framework = refs.ResolveRefTypeIn(marker.Name, "System.Runtime")
            ?? refs.ResolveRefTypeIn(marker.Name, typeof(object).Assembly.GetName().Name);
        return selected != null && selected == framework;
    }

    // A suspend main's synchronous drain replaces the declaration before attribute constructors are bound.
    internal static BirAttribute[] ForDrain(BirFunctionDeclaration source, ReferenceMetadataIndex refs) =>
        source.Attrs.OrEmpty().Where(a => a.AttrClr.OrDefault() && IsFrameworkMarker(refs, a.Attr.OrDefault(), a.AttrAssembly.OrDefault()))
            .Select(a => (BirAttribute)a.DeepClone()).ToArray();

    // Only resolved framework constructors cross to the CLR startup method. Keep the attribute set on
    // the exact target method: multiple CIR files may contribute methods to the same file facade.
    internal static void Bind(BirFileDeclaration file, ReferenceMetadataIndex refs)
    {
        var main = Array.Find(file.Methods.OrEmpty(), m => m.EntryPoint.OrDefault());
        if (main == null) return;
        var attrs = main.Attrs.OrEmpty().Where(a => a.AttrExternal.OrDefault()
            && a.MemberRef.OrDefault() is {} ctor && ctor.Kind.OrDefault() == MemberRefNode.Kinds.Ctor
            && IsFrameworkMarker(refs, ctor.DeclaringType.OrDefault(), ctor.Assembly.OrDefault()))
            .Select(a => (BirAttribute)a.DeepClone()).ToArray();
        if (attrs.Length != 0) main.EntryPointAttrs = attrs;
    }
}
