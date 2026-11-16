using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedMemberOwnerBinding
{
    // Only source owner applications move here. Exact declaration selection,
    // member signatures, dispatch and result closure remain in the later owner.
    internal static void ProjectOwners(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex references)
    {
        var shapes = new Dictionary<string, TypeShape>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, BirGraphValue>(StringComparer.Ordinal);
        foreach (var root in roots)
        foreach (var declaration in root.Types.OrEmpty())
        {
            var shape = SourceShape(declaration);
            if (shape == null || shape.Name == null) continue;
            shapes[shape.Name] = shape;
            declarations[shape.Name] = (BirGraphValue)declaration;
        }
        void Walk(BirGraphValue node, TypeNode.Fqn enclosing)
        {
            if (node == null || node is IBirExecutablePayload) return;
            var definition = node is IBirTypeDeclaration type ? SourceShape(type) : null;
            if (definition?.Name is string name && declarations.TryGetValue(name, out var selected)
                && ReferenceEquals(selected, node) && HasSourceMethods(node))
            {
                var arity = shapes[name].TypeParamCount;
                enclosing = new(name, arity == 0 ? null
                    : Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
            }
            BindSource(node, shapes, references, enclosing);
            node.VisitChildSlots(slot => Walk(slot.Value, enclosing));
        }
        foreach (var root in roots) Walk(root, null);
    }

    static TypeShape SourceShape(IBirTypeDeclaration declaration) => declaration switch {
        BirClassDefinition type => new() { Name = type.Name.OrDefault(), Kind = type.Kind.OrDefault(),
            TypeParamCount = type.CapturedTypeParams.OrEmpty().Length + type.TypeParams.OrEmpty().Length,
            Base = type.Base.OrDefault() as TypeNode.Fqn, Interfaces = type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray() },
        BirInterfaceDefinition type => new() { Name = type.Name.OrDefault(), Kind = type.Kind.OrDefault(),
            TypeParamCount = type.CapturedTypeParams.OrEmpty().Length + type.TypeParams.OrEmpty().Length,
            Base = type.Base.OrDefault() as TypeNode.Fqn, Interfaces = type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray() },
        BirEnumDefinition type => new() { Name = type.Name.OrDefault(), Kind = type.Kind.OrDefault(),
            TypeParamCount = type.CapturedTypeParams.OrEmpty().Length + type.TypeParams.OrEmpty().Length,
            Base = type.Base.OrDefault() as TypeNode.Fqn, Interfaces = type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray() },
        _ => null,
    };

    static bool HasSourceMethods(BirGraphValue node) => node switch {
        BirClassDefinition type => type.Methods.HasValue, BirInterfaceDefinition type => type.Methods.HasValue,
        BirEnumDefinition type => type.Methods.HasValue, _ => false,
    };

    static TypeNode.Fqn ReceiverStamp(IBirExpression expression) => (expression switch {
        BirCallStatic value => value.Sty.OrDefault(), BirCallInstance value => value.Sty.OrDefault(),
        BirCallLocal value => value.Sty.OrDefault(), BirFieldGet value => value.Sty.OrDefault(),
        BirStaticFieldGet value => value.Sty.OrDefault(), BirFieldSetExpression => null,
        BirConstrainedCall value => value.Sty.OrDefault(), BirGenericStatic value => value.Sty.OrDefault(),
        BirGenericInstance value => value.Sty.OrDefault(), BirNativeStatic value => value.Sty.OrDefault(),
        BirNativeInstance value => value.Sty.OrDefault(), BirPropertyGet value => value.Sty.OrDefault(),
        BirGraphValue value => value.ResultTypeStamp, _ => null,
    }) as TypeNode.Fqn;

    static void BindSource(BirGraphValue node, IReadOnlyDictionary<string, TypeShape> shapes,
        ReferenceMetadataIndex references, TypeNode.Fqn enclosing)
    {
        var facts = node switch {
            BirCallInstance call => (call.OwnerType.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault()),
            BirNewBoundDelegate call => (call.OwnerType.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault()),
            BirBoundClrDelegate call => (call.ClrType.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault()),
            BirNativeInstance call => (call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault()),
            BirPropertyGet call => (call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault()),
            BirPropertySet call => (call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault()),
            BirEventAdd call => (call.Type.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault()),
            BirEventRemove call => (call.Type.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault()),
            _ => ((TypeNode)null, (IBirExpression)null, false, false),
        };
        if (facts.Item4 || facts.Item1 is not TypeNode.Fqn owner) return;
        var projected = ProjectUseOwner(owner, ReceiverStamp(facts.Item2), facts.Item2 is BirThis, facts.Item3,
            enclosing, shapes, references, sourceFrames: true);
        if (projected == null) return;
        switch (node)
        {
            case BirCallInstance call: call.OwnerType = projected; break;
            case BirNewBoundDelegate call: call.OwnerType = projected; call.CalleeOwner = projected; break;
            case BirBoundClrDelegate call: call.ClrType = projected; break;
            case BirNativeInstance call: call.Type = projected; break;
            case BirPropertyGet call: call.Type = projected; break;
            case BirPropertySet call: call.Type = projected; break;
            case BirEventAdd call: call.Type = projected; break;
            case BirEventRemove call: call.Type = projected; break;
        }
    }
}
