using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DefaultArgSplice
{
    // Omission selection and planned value filling are shared by the independent
    // demand view and the actual source preparation. Inline defaults deliberately
    // have different owning destinations in those two consumers.
    sealed class TypedDefaultExpansion
    {
        readonly ReferenceMetadataIndex refs;
        readonly List<BirFunctionDeclaration> hoist;
        readonly string owner;
        readonly string diagnosticContext;
        internal TypedDefaultExpansion(ReferenceMetadataIndex refs, List<BirFunctionDeclaration> hoist, string owner, string diagnosticContext)
        {
            this.refs = refs; this.hoist = hoist; this.owner = owner; this.diagnosticContext = diagnosticContext;
        }
        internal void ExpandNode(BirGraphValue node)
        {
            if (node is BirCallEvaluation evaluation && evaluation.Expr.HasValue)
                ExpandPlan((BirGraphValue)evaluation.Expr.Value, evaluation.Bindings.OrEmpty(), evaluation.SemanticOwner.OrDefault());
            var (constructors, baseType, name) = ConstructorOwner(node);
            foreach (var constructor in constructors)
            {
                Delegation(constructor.BaseArgs.OrDefault(), (baseType as TypeNode.Fqn)?.Name,
                    refs.SourceDeclarationArguments(baseType), constructor, name);
                Delegation(constructor.ThisArgs.OrDefault(), name,
                    refs.SourceDeclarationArguments(baseType), constructor, name);
            }
            ExpandTrailing(node);
        }
        void Fill(List<IBirExpression> arguments, BirCallBinding[] bindings, int count, Dictionary<int, IBirExpression> defaults,
            IBirExpression dispatch, IBirExpression extension, IBirExpression enclosing, string semantic,
            TypeNode[] methods, TypeNode[] owners, string label)
        {
            BirCallBinding Binding(IBirExpression argument) => argument is BirBindingReference { Id.HasValue: true } read
                ? bindings.FirstOrDefault(binding => binding.Id.OrDefault() == read.Id.Value) : null;
            for (var index = 0; index < arguments.Count; index++)
            {
                var binding = Binding(arguments[index]);
                if (binding?.Expr.OrDefault() is not BirDefaultArgument || !defaults.TryGetValue(index, out var expression)) continue;
                var fill = SpliceOne(expression, dispatch, extension, enclosing, arguments, hoist, owner, semantic, methods, owners, label, index);
                if (fill == null) continue;
                binding.Expr = OptionalField<IBirExpression>.Of(fill);
                binding.Stable = ValueStability.IsReReadable(((BirGraphNode)fill).Tag,
                    fill is BirEnumValue { PhysicalValue.HasValue: true, Underlying.HasValue: true });
            }
            for (var index = arguments.Count; index < count; index++)
            {
                if (!defaults.TryGetValue(index, out var expression)) break;
                var fill = SpliceOne(expression, dispatch, extension, enclosing, arguments, hoist, owner, semantic, methods, owners, label, index);
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
                throw new InvalidOperationException(ConstructorDefaultPosition(((TypeNode.Fqn)calledOwner).Name, arguments.Length, signature.Length));
            var label = constructor == null ? method : ((TypeNode.Fqn)calledOwner).Name + " constructor";
            var defaults = constructor == null ? DefaultValuesForCall(facts, refs, out _)
                : refs.KotlinDefaultValuesFor(((TypeNode.Fqn)calledOwner).Name, method, signature.Length,
                    ReferenceMetadataIndex.SignatureKeyOf(signature), ReferenceMetadataIndex.SignatureKeyOf(signature, relaxed: true));
            if (defaults == null)
            {
                if (refs.KotlinDefaultsAmbiguous((calledOwner as TypeNode.Fqn)?.Name, method, signature.Length))
                    throw new InvalidOperationException(AmbiguousDefault(label, signature.Length));
                return;
            }
            var methodArguments = call switch {
                BirCallStatic value => value.TypeArgs.OrDefault(), BirCallInstance value => value.TypeArgs.OrDefault(), _ => null,
            };
            var ownerApplication = facts.DefaultOwner ?? (constructor?.Type.OrDefault() ?? facts.Owner);
            var values = arguments.ToList();
            Fill(values, bindings, signature.Length, defaults, (call as BirCallInstance)?.Recv.OrDefault(),
                arguments.FirstOrDefault(), constructor == null ? null : arguments.FirstOrDefault(), semantic, methodArguments,
                refs.SourceDeclarationArguments(ownerApplication), label);
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
                    throw new InvalidOperationException(AmbiguousDefault(calledOwner, arguments.Length, constructorDelegation: true));
                return;
            }
            Fill(arguments.ToList(), bindings, arguments.Length, defaults, null, null, arguments.FirstOrDefault(), semantic, null, application,
                calledOwner + " constructor");
        }
        void ExpandTrailing(BirGraphValue node)
        {
            var facts = CallFacts(node);
            var arguments = node switch { BirCallStatic call => call.Args.OrDefault(), BirCallInstance call => call.Args.OrDefault(), _ => null };
            if (arguments == null || facts.Signature == null || arguments.Length >= facts.Signature.Length
                || facts.Owner is not TypeNode.Fqn || facts.Method == null) return;
            var defaults = DefaultValuesForCall(facts, refs, out var parameterNames);
            var label = ((TypeNode.Fqn)facts.Owner).Name + "." + facts.Method;
            var values = arguments.ToList();
            for (var index = arguments.Length; index < facts.Signature.Length; index++)
            {
                if (defaults?.TryGetValue(index, out var expression) != true)
                    throw new InvalidOperationException(UnrepresentableDefault(diagnosticContext, label, index, parameterNames));
                var fill = (IBirExpression)ObjectSlotRename.Apply((BirGraphValue)Clone(expression));
                var methodArguments = node is BirCallStatic staticCall ? staticCall.TypeArgs.OrEmpty() : ((BirCallInstance)node).TypeArgs.OrEmpty();
                fill = (IBirExpression)TypedExecutableFrames.Substitute((BirGraphValue)fill, methodArguments, methodArguments.Length,
                    refs.SourceDeclarationArguments(facts.DefaultOwner ?? facts.Owner));
                if (!IsMetadataConstant(fill))
                    throw new InvalidOperationException(UnrepresentableDefault(diagnosticContext, label, index, parameterNames));
                values.Add(fill);
            }
            if (node is BirCallStatic target) target.Args = values.ToArray();
            else ((BirCallInstance)node).Args = values.ToArray();
        }
    }

    static (BirConstructor[] Constructors, TypeNode Base, string Name) ConstructorOwner(BirGraphValue node) => node switch {
        BirClassDefinition owner => (owner.Ctors.OrEmpty(), owner.Base.OrDefault(), owner.Name.OrDefault()),
        BirInterfaceDefinition owner => (owner.Ctors.OrEmpty(), owner.Base.OrDefault(), owner.Name.OrDefault()),
        BirEnumDefinition owner => (owner.Ctors.OrEmpty(), owner.Base.OrDefault(), owner.Name.OrDefault()),
        _ => (Array.Empty<BirConstructor>(), null, null),
    };
}
