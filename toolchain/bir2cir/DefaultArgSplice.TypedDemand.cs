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
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirCallEvaluation evaluation && evaluation.Expr.HasValue)
                ExpandPlan((BirGraphValue)evaluation.Expr.Value, evaluation.Bindings.OrEmpty(), evaluation.SemanticOwner.OrDefault());
            if (node is BirClassDefinition type)
                foreach (var constructor in type.Ctors.OrEmpty())
                {
                    Delegation(constructor.BaseArgs.OrDefault(), (type.Base.OrDefault() as TypeNode.Fqn)?.Name,
                        refs.SourceDeclarationArguments(type.Base.OrDefault()), constructor, type.Name.OrDefault());
                    Delegation(constructor.ThisArgs.OrDefault(), type.Name.OrDefault(),
                        refs.SourceDeclarationArguments(type.Base.OrDefault()), constructor, type.Name.OrDefault());
                }
            ExpandTrailing(node);
            if (node is BirCallInline inline) ExpandInline(inline);
            node.VisitChildSlots(slot => { Walk(slot.Value); });
        }
        void Fill(List<IBirExpression> arguments, BirCallBinding[] bindings, int count, Dictionary<int, IBirExpression> defaults,
            IBirExpression dispatch, IBirExpression extension, IBirExpression enclosing, string semantic,
            TypeNode[] methods, TypeNode[] owners)
        {
            BirCallBinding Binding(IBirExpression argument) => argument is BirBindingReference { Id.HasValue: true } read
                ? bindings.FirstOrDefault(binding => binding.Id.OrDefault() == read.Id.Value) : null;
            for (var index = 0; index < arguments.Count; index++)
            {
                var binding = Binding(arguments[index]);
                if (binding?.Expr.OrDefault() is not BirDefaultArgument || !defaults.TryGetValue(index, out var expression)) continue;
                var fill = SpliceOne(expression, dispatch, extension, enclosing, arguments, hoist, owner, semantic, methods, owners);
                if (fill == null) continue;
                binding.Expr = OptionalField<IBirExpression>.Of(fill);
                binding.Stable = ValueStability.IsReReadable(((BirGraphNode)fill).Tag,
                    fill is BirEnumValue { PhysicalValue.HasValue: true });
            }
            for (var index = arguments.Count; index < count; index++)
            {
                if (!defaults.TryGetValue(index, out var expression)) break;
                var fill = SpliceOne(expression, dispatch, extension, enclosing, arguments, hoist, owner, semantic, methods, owners);
                if (fill == null) break;
                arguments.Add(fill);
            }
        }
        void ExpandPlan(BirGraphValue call, BirCallBinding[] bindings, string semantic)
        {
            var arguments = call switch {
                BirNew value => value.Args.OrDefault(), BirCallStatic value => value.Args.OrDefault(),
                BirCallInstance value => value.Args.OrDefault(), _ => null,
            };
            if (arguments == null || !arguments.OfType<BirBindingReference>().Any(read =>
                bindings.FirstOrDefault(binding => binding.Id.OrDefault() == read.Id.OrDefault())?.Expr.OrDefault() is BirDefaultArgument)) return;
            var facts = CallFacts(call);
            var constructor = call as BirNew;
            var signature = constructor?.ArgTypes.OrDefault() ?? facts.Signature;
            var calledOwner = constructor?.Type.OrDefault() ?? facts.Owner;
            var method = constructor == null ? facts.Method : ReferenceMetadataIndex.CtorKeyName;
            if (signature == null || method == null || constructor != null && calledOwner is not TypeNode.Fqn) return;
            if (constructor != null && arguments.Length != signature.Length)
                throw new InvalidOperationException("Constructor default argument positions do not match its selected signature");
            var defaults = constructor == null ? DefaultValuesForCall(facts, refs, out _)
                : refs.KotlinDefaultValuesFor(((TypeNode.Fqn)calledOwner).Name, method, signature.Length,
                    ReferenceMetadataIndex.SignatureKeyOf(signature), ReferenceMetadataIndex.SignatureKeyOf(signature, relaxed: true));
            if (defaults == null)
            {
                if (refs.KotlinDefaultsAmbiguous((calledOwner as TypeNode.Fqn)?.Name, method, signature.Length))
                    throw new InvalidOperationException("Selected default arguments are ambiguous");
                return;
            }
            var methodArguments = call switch {
                BirCallStatic value => value.TypeArgs.OrDefault(), BirCallInstance value => value.TypeArgs.OrDefault(), _ => null,
            };
            var ownerApplication = facts.DefaultOwner ?? (constructor?.Type.OrDefault()
                ?? (call is BirCallInstance instance ? instance.OwnerType.OrDefault() : null));
            var values = arguments.ToList();
            Fill(values, bindings, signature.Length, defaults, (call as BirCallInstance)?.Recv.OrDefault(),
                arguments.FirstOrDefault(), constructor == null ? null : arguments.FirstOrDefault(), semantic, methodArguments,
                refs.SourceDeclarationArguments(ownerApplication));
            switch (call)
            {
                case BirNew value: value.Args = values.ToArray(); break;
                case BirCallStatic value: value.Args = values.ToArray(); value.DefaultDeclarationId = default; value.DefaultOwnerType = default; break;
                case BirCallInstance value: value.Args = values.ToArray(); value.DefaultDeclarationId = default; value.DefaultOwnerType = default; break;
            }
        }
        void Delegation(IBirExpression[] arguments, string calledOwner, TypeNode[] application, BirConstructor constructor, string semantic)
        {
            var bindings = constructor.DelegationBindings.OrDefault();
            if (arguments == null || calledOwner == null || bindings == null || !arguments.OfType<BirBindingReference>().Any(read =>
                bindings.FirstOrDefault(binding => binding.Id.OrDefault() == read.Id.OrDefault())?.Expr.OrDefault() is BirDefaultArgument)) return;
            var defaults = refs.KotlinDefaultValuesFor(calledOwner, ReferenceMetadataIndex.CtorKeyName, arguments.Length);
            if (defaults == null)
            {
                if (refs.KotlinDefaultsAmbiguous(calledOwner, ReferenceMetadataIndex.CtorKeyName, arguments.Length))
                    throw new InvalidOperationException("Selected constructor delegation defaults are ambiguous");
                return;
            }
            Fill(arguments.ToList(), bindings, arguments.Length, defaults, null, null, arguments.FirstOrDefault(), semantic, null, application);
        }
        void ExpandTrailing(BirGraphValue node)
        {
            var facts = CallFacts(node);
            var arguments = node switch { BirCallStatic call => call.Args.OrDefault(), BirCallInstance call => call.Args.OrDefault(), _ => null };
            if (arguments == null || facts.Signature == null || arguments.Length >= facts.Signature.Length
                || facts.Owner is not TypeNode.Fqn || facts.Method == null) return;
            var defaults = DefaultValuesForCall(facts, refs, out _);
            var values = arguments.ToList();
            for (var index = arguments.Length; index < facts.Signature.Length; index++)
            {
                if (defaults?.TryGetValue(index, out var expression) != true)
                    throw new InvalidOperationException("Cannot fill an omitted default from the selected referenced declaration");
                var fill = (IBirExpression)ObjectSlotRename.Apply((BirGraphValue)Clone(expression));
                var methodArguments = node is BirCallStatic staticCall ? staticCall.TypeArgs.OrEmpty() : ((BirCallInstance)node).TypeArgs.OrEmpty();
                fill = (IBirExpression)TypedExecutableFrames.Substitute((BirGraphValue)fill, methodArguments, methodArguments.Length,
                    refs.SourceDeclarationArguments(facts.DefaultOwner ?? facts.Owner));
                if (!IsMetadataConstant(fill)) throw new InvalidOperationException("An unplanned trailing default is not a metadata constant");
                values.Add(fill);
            }
            if (node is BirCallStatic target) target.Args = values.ToArray();
            else ((BirCallInstance)node).Args = values.ToArray();
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
                        : throw new InvalidOperationException("An omitted inline argument has no default carrier");
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
