namespace DotKt.Bir;

// KotlinFunctionAttribute flags for a source virtual member whose exact CLR
// entry is a nonvirtual facade. Other members derive modality from MethodDef.
internal static class KotlinFacadeModality
{
    public const int Open = 1 << 4;
    public const int Abstract = 1 << 5;
}
