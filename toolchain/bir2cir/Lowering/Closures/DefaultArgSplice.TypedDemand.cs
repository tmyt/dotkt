using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DefaultArgSplice
{
    readonly record struct DefaultCallFacts(TypeNode Owner, TypeNode DefaultOwner, string Method, int Arity,
        bool Static, string Property, string Identity, string DefaultIdentity, TypeNode[] Signature);

    static DefaultCallFacts CallFacts(BirGraphValue node) => node switch {
        BirCallStatic call => new(call.OwnerType.OrDefault() ?? call.CalleeOwner.OrDefault() ?? call.Owner.OrDefault(),
            call.DefaultOwnerType.OrDefault(), call.Method.OrDefault(), call.TypeArgs.OrEmpty().Length, true, call.Prop.OrDefault(),
            call.DeclarationId.OrDefault(), call.DefaultDeclarationId.OrDefault(), call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault()),
        BirCallInstance call => new(call.OwnerType.OrDefault(), call.DefaultOwnerType.OrDefault(), call.Method.OrDefault(),
            call.TypeArgs.OrEmpty().Length, false, call.Prop.OrDefault(), call.DeclarationId.OrDefault(), call.DefaultDeclarationId.OrDefault(),
            call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault()), _ => default,
    };

    static Dictionary<int, IBirExpression> DefaultValuesForCall(DefaultCallFacts call, ReferenceMetadataIndex refs, out string[] names)
    {
        names = null;
        if (call.DefaultIdentity != null) return refs.KotlinDefaultValuesForDeclarationIdentity(call.DefaultIdentity);
        if (call.Identity != null) return refs.KotlinDefaultValuesForDeclarationIdentity(call.Identity);
        if (call.Signature == null) return null;
        var key = ReferenceMetadataIndex.SignatureKeyOf(call.Signature);
        var relaxed = ReferenceMetadataIndex.SignatureKeyOf(call.Signature, relaxed: true);
        if (call.DefaultOwner is TypeNode.Fqn inherited)
        {
            if (refs.TryKotlinDefaultValuesForSelectedMethod(inherited, call.Method, call.Arity, call.Static,
                call.Signature, out var selected, out names, call.Property)) return selected;
            return refs.KotlinDefaultValuesFor(inherited.Name, call.Method, call.Signature.Length, key, relaxed)
                ?? refs.KotlinDefaultValuesForImplementedInterface(inherited.Name, inherited.Args?.Length ?? 0,
                    call.Method, call.Signature.Length, key, relaxed);
        }
        if (call.Owner is TypeNode.Fqn owner && refs.TryKotlinDefaultValuesForSelectedMethod(owner, call.Method, call.Arity,
            call.Static, call.Signature, out var own, out names, call.Property)) return own;
        var ownerName = (call.Owner as TypeNode.Fqn)?.Name;
        return refs.KotlinDefaultValuesFor(ownerName, call.Method, call.Signature.Length, key, relaxed)
            ?? refs.KotlinDefaultValuesForImplementedInterface(ownerName, (call.Owner as TypeNode.Fqn)?.Args?.Length ?? 0,
                call.Method, call.Signature.Length, key, relaxed);
    }

    internal static BirFileDeclaration CreateDemandView(BirFileDeclaration source, ReferenceMetadataIndex refs, TypedInlineBirIndex declarations)
    {
        var root = (BirFileDeclaration)source.DeepClone();
        var hoist = new List<BirFunctionDeclaration>();
        var owner = root.FileClass.OrDefault();
        var expansion = new TypedDefaultExpansion(refs, hoist, owner, "nullable-witness demand");
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            expansion.ExpandNode(node);
            if (node is BirCallInline inline) ExpandInline(inline);
            node.VisitChildSlots(slot => { Walk(slot.Value); });
        }
        void ExpandInline(BirCallInline call)
        {
            if (!call.Args.HasValue || call.Args.Value.All(argument => argument != null)) return;
            var payload = declarations.Resolve(call, refs);
            var extension = payload.Recv.OrDefault() == "extensionParam";
            var arguments = call.Args.Value;
            var bound = new List<IBirExpression>();
            if (extension) bound.Add(Clone(call.Recvs.OrDefault()?.Extension.OrDefault()));
            for (var index = 0; index < arguments.Length; index++)
            {
                var parameterIndex = index + (extension ? 1 : 0);
                if (arguments[index] == null)
                {
                    var parameter = payload.Params.Value[parameterIndex];
                    var expression = parameter.Default.HasValue ? Clone(parameter.Default.Value)
                        : ParameterDefault(parameter) is { } carrier ? MaterializeDefault(carrier, hoist, owner)
                        : throw new InvalidOperationException("bir2cir: omitted inline argument has no default carrier");
                    expression = (IBirExpression)TypedExecutableFrames.Prebind((BirGraphValue)expression);
                    expression = (IBirExpression)TypedExecutableFrames.Substitute((BirGraphValue)expression, call.TypeArgs.OrEmpty(),
                        payload.TypeParams.OrEmpty().Length, call.Recvs.OrDefault()?.DispatchTypeArgs.OrDefault());
                    arguments[index] = SubstituteTokens(expression, call.Recvs.OrDefault()?.Dispatch.OrDefault(),
                        extension ? bound[0] : null, null, bound);
                }
                bound.Add(Clone(arguments[index]));
            }
        }
        Walk(root);
        for (var index = 0; index < hoist.Count; index++)
        {
            Walk(hoist[index]);
            if (!root.Methods.HasValue) throw new InvalidOperationException("Default demand view has no methods for carried helpers");
            root.Methods = root.Methods.Value.Append(hoist[index]).ToArray();
        }
        return root;
    }

    static bool IsMetadataConstant(IBirExpression expression) => expression switch {
        BirConstant => true, BirDefaultValue value => value.Type.HasValue,
        BirEnumValue value => value.Type.HasValue && value.Underlying.HasValue && value.PhysicalValue.HasValue,
        BirNullableWrap value => value.Elem.HasValue && value.E.HasValue && IsMetadataConstant(value.E.Value)
            && MetadataValueType(value.E.Value) == value.Elem.Value,
        BirCast value => value.Type.HasValue && value.E.HasValue && IsMetadataConstant(value.E.Value),
        BirNew value => MetadataConstruction(value), _ => false,
    };
    static TypeNode MetadataValueType(IBirExpression expression) => expression switch {
        BirConstant value => value.Type.OrDefault(), BirDefaultValue value => value.Type.OrDefault(),
        BirEnumValue value => value.Type.OrDefault(), BirCast value => value.Type.OrDefault(), BirNew value => value.Type.OrDefault(), _ => null,
    };
    static string[] MetadataConstructorParameters(string owner) => owner switch {
        "System.Decimal" => new[] { "System.Int32", "System.Int32", "System.Int32", "System.Boolean", "System.Byte" },
        "System.DateTime" => new[] { "System.Int64" }, _ => null,
    };
    static bool MetadataConstruction(BirNew value)
    {
        var expected = MetadataConstructorParameters((value.Type.OrDefault() as TypeNode.Fqn)?.Name);
        return expected != null && value.ArgTypes.OrEmpty().Length == expected.Length && value.Args.OrEmpty().Length == expected.Length
            && expected.Select((name, index) => value.ArgTypes.Value[index] is TypeNode.Fqn type && type.Name == name
                && value.Args.Value[index] is BirConstant { Type.Value: TypeNode.Fqn constant } && constant.Name == name).All(matches => matches);
    }
}
