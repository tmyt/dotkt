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
        OperatorExtension(owner, method, declaration, name, package, names, signatures, context);
    }

    void AddComparisonExtensions(TypeDefinitionHandle owner, List<ProjectedFunction> candidates,
        Package package, NameTable names, SignatureDecoder signatures, GenericContext context)
    {
        foreach (var group in candidates.GroupBy(p => (p.PhysicalParameters[0], p.PhysicalParameters[1],
                     _md.GetMethodDefinition(p.Handle).GetGenericParameters().Count)))
        {
            var byName = group.ToLookup(p => _md.GetString(_md.GetMethodDefinition(p.Handle).Name));
            foreach (var (less, greater) in new[] { ("op_LessThan", "op_GreaterThan"),
                         ("op_LessThanOrEqual", "op_GreaterThanOrEqual") })
            {
                if (byName[less].Count() != 1 || byName[greater].Count() != 1) continue;
                var first = byName[less].Single();
                var view = OperatorExtension(owner, first.Handle, first.Declaration, "compareTo", package, names,
                    signatures, context);
                view.ReturnType = signatures.NamedType("kotlin.Int");
                var binding = view.FunctionAnnotation.Single(a => a.Id == names.Class("kotlin.clr.ClrExternal"));
                binding.Argument.Add(new Annotation.Types.Argument { NameId = names.String("pairedMember"),
                    Value = new Annotation.Types.Argument.Types.Value {
                        Type = Annotation.Types.Argument.Types.Value.Types.Type.String, StringValue = names.String(greater) } });
                break;
            }
        }
    }

    Function OperatorExtension(TypeDefinitionHandle owner, MethodDefinitionHandle method, Function declaration,
        string name, Package package, NameTable names, SignatureDecoder signatures, GenericContext context)
    {
        var view = declaration.Clone();
        // Generic static declarations already carry the lifted class frame. For
        // closed owners use the same binding, with no lifted owner parameters.
        if (_md.GetTypeDefinition(owner).GetGenericParameters().Count == 0)
            ProjectStaticOwnerParameters(owner, method, view, names, signatures, context);
        view.Name = names.String(name);
        view.Flags &= ~(1 << 18); // package extension, not a static class member
        if (name is "not" or "compareTo") view.Flags |= 1 << 8;
        else if (view.ValueParameter.Count == 2) view.Flags |= 1 << 9;
        view.ReceiverType = view.ValueParameter[0].Type;
        view.ValueParameter.RemoveAt(0);
        ApplySourceFunctionPriority(view, 8, names);
        package.Function.Add(view);
        return view;
    }
}
