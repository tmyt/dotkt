using System.Reflection.Metadata;
using System.Reflection;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;

internal sealed partial class AssemblyScanner
{
    bool CanPublishNamespaceOperators(TypeDefinitionHandle owner)
    {
        for (; !owner.IsNil;)
        {
            var def = _md.GetTypeDefinition(owner);
            if ((def.Attributes & TypeAttributes.VisibilityMask) is not
                    (TypeAttributes.Public or TypeAttributes.NestedPublic)) return false;
            owner = def.GetDeclaringType();
        }
        return true;
    }

    static void SetOperatorFact(Function view, NameTable names, string field, string value)
    {
        var binding = view.FunctionAnnotation.Single(a => a.Id == names.Class("kotlin.clr.ClrExternal"));
        binding.Argument.Add(new Annotation.Types.Argument { NameId = names.String(field),
            Value = new Annotation.Types.Argument.Types.Value {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String, StringValue = names.String(value) } });
    }

    // Two owners in the same package can supply the same Kotlin callable signature.
    // Keep the strict declaration in metadata so Fir2Ir cannot bind the fallback by that same ID.
    static void RemoveShadowedComparisonViews(Package package, NameTable names)
    {
        var compare = package.Function.Where(f => names.StringValue(f.Name) == "compareTo" && f.ReceiverType != null)
            .Select(f => (View: f, Role: f.FunctionAnnotation.Where(a => a.Id == names.Class("kotlin.clr.ClrExternal"))
                .SelectMany(a => a.Argument).Where(arg => names.StringValue(arg.NameId) == "operatorRole")
                .Select(arg => names.StringValue(arg.Value.StringValue)).SingleOrDefault()))
            .Where(x => x.Role is "strictOrder" or "nonStrictOrder")
            .GroupBy(x => (TypeKey(x.View.ReceiverType), FunctionShapeKey(x.View, names)));
        foreach (var g in compare.ToArray())
            if (g.Any(x => x.Role == "strictOrder"))
                foreach (var x in g.Where(x => x.Role == "nonStrictOrder")) package.Function.Remove(x.View);
    }

    // Both predicates must accept each operand. Never erase mismatching generic arguments
    // just because the two MethodDefs have the same physical signature.
    static KType? CommonComparisonInput(KType a, KType b)
    {
        if (SameProjectedType(a, b)) return a;
        static KType NonNull(KType type)
        {
            var copy = type.Clone();
            copy.Nullable = false;
            copy.FlexibleUpperBound = null;
            copy.ClearFlexibleTypeCapabilitiesId();
            return copy;
        }
        var left = NonNull(a);
        if (!SameProjectedType(left, NonNull(b))) return null;
        if (!a.Nullable && a.FlexibleUpperBound == null || !b.Nullable && b.FlexibleUpperBound == null) return left;
        return a.FlexibleUpperBound != null ? a : b;
    }

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
                var declaration = first.Declaration.Clone();
                var second = byName[greater].Single();
                var mask = ReadonlyOperands(first);
                if (mask != ReadonlyOperands(second)) continue;
                KType Input(ProjectedFunction predicate, int i) => (mask & (1 << i)) != 0
                    ? signatures.ByRefElement(predicate.Declaration.ValueParameter[i].Type)!
                    : predicate.Declaration.ValueParameter[i].Type;
                var receiver = CommonComparisonInput(Input(first, 0), Input(second, 0));
                var operand = CommonComparisonInput(Input(first, 1), Input(second, 1));
                if (receiver == null || operand == null) continue;
                declaration.ValueParameter[0].Type = receiver;
                declaration.ValueParameter[1].Type = operand;
                var view = OperatorExtension(owner, first.Handle, declaration, "compareTo", package, names,
                    signatures, context);
                view.ReturnType = signatures.NamedType("kotlin.Int");
                SetOperatorFact(view, names, "pairedMember", greater);
                SetOperatorFact(view, names, "operatorRole", less == "op_LessThan" ? "strictOrder" : "nonStrictOrder");
                if (mask != 0)
                    view.FunctionAnnotation.Single(a => a.Id == names.Class("kotlin.clr.ClrExternal")).Argument.Add(
                        new Annotation.Types.Argument { NameId = names.String("readonlyOperands"),
                            Value = new Annotation.Types.Argument.Types.Value {
                                Type = Annotation.Types.Argument.Types.Value.Types.Type.Int, IntValue = mask } });
                break;
            }
        }
        int ReadonlyOperands(ProjectedFunction predicate)
        {
            var mask = 0;
            foreach (var (handle, row) in PhysicalParameters(_md.GetMethodDefinition(predicate.Handle)))
            {
                int i = row.SequenceNumber - 1;
                if (signatures.ByRefElement(predicate.Declaration.ValueParameter[i].Type) != null &&
                    (row.Attributes & (ParameterAttributes.In | ParameterAttributes.Out)) == ParameterAttributes.In &&
                    _attrs.Has(handle, "System.Runtime.CompilerServices.IsReadOnlyAttribute", requireTrust: false))
                    mask |= 1 << i;
            }
            return mask;
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
