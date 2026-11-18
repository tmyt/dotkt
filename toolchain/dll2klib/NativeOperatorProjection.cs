using System.Reflection.Metadata;
using DotKt.Klib.Metadata;

internal sealed partial class AssemblyScanner
{
    void AddOperatorExtension(TypeDefinitionHandle owner, MethodDefinitionHandle method, string clrName,
        Function declaration, Package package, NameTable names, SignatureDecoder signatures, GenericContext context)
    {
        var name = clrName switch {
            "op_LogicalNot" => "not", "op_OnesComplement" => "inv",
            "op_BitwiseAnd" => "and", "op_BitwiseOr" => "or", "op_ExclusiveOr" => "xor",
            "op_LeftShift" => "shl", "op_RightShift" => "shr", "op_UnsignedRightShift" => "ushr",
            _ => null,
        };
        if (name == null || declaration.ValueParameter.Count != (name is "not" or "inv" ? 1 : 2)) return;
        var view = declaration.Clone();
        // Generic static declarations already carry the lifted class frame. For
        // closed owners use the same binding, with no lifted owner parameters.
        if (_md.GetTypeDefinition(owner).GetGenericParameters().Count == 0)
            ProjectStaticOwnerParameters(owner, method, view, names, signatures, context);
        view.Name = names.String(name);
        view.Flags &= ~(1 << 18); // package extension, not a static class member
        if (name == "not") view.Flags |= 1 << 8;
        if (view.ValueParameter.Count == 2) view.Flags |= 1 << 9;
        view.ReceiverType = view.ValueParameter[0].Type;
        view.ValueParameter.RemoveAt(0);
        ApplySourceFunctionPriority(view, 8, names);
        package.Function.Add(view);
    }
}
