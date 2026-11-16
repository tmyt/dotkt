using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionHelperBinding
{
    // Source and later physical helper binding share declaration-wide capture,
    // unlike ordinary expression recognition's sequential lexical walker.
    internal sealed class CollectionSourceContext
    {
        internal readonly Dictionary<string, TypeNode> Variables;
        internal readonly Dictionary<string, List<TypeNode>> Constraints;
        internal readonly HashSet<string> ProjectedCollectionVars;
        internal NullableRepresentationFrame OwnerFrame;
        internal NullableRepresentationFrame MethodFrame;
        internal CollectionSourceContext()
        {
            Variables = new(StringComparer.Ordinal);
            Constraints = new(StringComparer.Ordinal);
            ProjectedCollectionVars = new(StringComparer.Ordinal);
        }
        CollectionSourceContext(CollectionSourceContext parent)
        {
            Variables = new(parent.Variables, StringComparer.Ordinal);
            Constraints = new(parent.Constraints, StringComparer.Ordinal);
            ProjectedCollectionVars = new(parent.ProjectedCollectionVars, StringComparer.Ordinal);
            OwnerFrame = parent.OwnerFrame;
            MethodFrame = parent.MethodFrame;
        }

        static (string Name, TypeNode Type)[] Parameters(BirGraphValue node) => node switch {
            BirFunctionDeclaration value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirConstructor value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirClassDefinition value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirInterfaceDefinition value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirEnumDefinition value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirInheritedClassMethod value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirClosureDefinition value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirNewSuspendLambda value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            BirInlineLambda value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())).ToArray(),
            _ => Array.Empty<(string, TypeNode)>(),
        };

        internal CollectionSourceContext Extend(BirGraphValue node)
        {
            var parameters = Parameters(node);
            var typeParameters = node switch {
                BirFunctionDeclaration value => value.TypeParams.OrEmpty(),
                BirClassDefinition value => value.TypeParams.OrEmpty(),
                BirInterfaceDefinition value => value.TypeParams.OrEmpty(),
                BirEnumDefinition value => value.TypeParams.OrEmpty(),
                BirRefTypeDeclaration value => value.TypeParams.OrEmpty(),
                BirInheritedClassMethod value => value.TypeParams.OrEmpty(),
                BirClosureDefinition value => value.TypeParams.OrEmpty(),
                BirNewSuspendLambda value => value.TypeParams.OrEmpty(),
                BirMethodImpl value => value.TypeParams.OrEmpty(),
                _ => Array.Empty<BirTypeParameter>(),
            };
            var body = node switch {
                BirFunctionDeclaration value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
                BirConstructor value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
                BirClosureDefinition value when value.Params.HasValue && value.Body.HasValue => value.Body.Value,
                _ => null,
            };
            if (parameters.Length == 0 && typeParameters.Length == 0 && body == null) return this;
            var result = new CollectionSourceContext(this);
            if (node is IBirTypeDeclaration owner && owner.Kind.OrDefault() is "class" or "interface")
            {
                result.OwnerFrame = NullableRepresentationViews.TypeFacts(node)?.NullableFrame;
                result.MethodFrame = null;
            }
            else if (node is BirFunctionDeclaration { Params.HasValue: true } method)
                result.MethodFrame = method.NullableRepresentationFrame.OrDefault();
            else if (node is BirConstructor { Params.HasValue: true } or BirClosureDefinition { Params.HasValue: true })
                result.MethodFrame = null;
            foreach (var (name, type) in parameters)
                if (name != null && type != null) result.Variables[name] = MemberCallSubstitution.CollectionVariableType(type);
            var typeScope = node switch {
                BirClassDefinition value => value.Kind.OrDefault(),
                BirInterfaceDefinition value => value.Kind.OrDefault(),
                BirEnumDefinition value => value.Kind.OrDefault(), _ => null,
            } is "class" or "interface" ? "type" : "method";
            for (var index = 0; index < typeParameters.Length; index++)
                if (typeParameters[index].Constraints.HasValue)
                    result.Constraints[typeScope + ":" + index] = typeParameters[index].Constraints.Value.Where(type => type != null).ToList();
            if (body != null)
                foreach (var statement in body) RecordLocals((BirGraphValue)statement, result.Variables, result.ProjectedCollectionVars);
            return result;
        }

        static void RecordLocals(BirGraphValue node, IDictionary<string, TypeNode> variables,
            ISet<string> projectedCollections)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
            {
                variables[variable.Name.Value] = MemberCallSubstitution.CollectionVariableType(variable.Type.Value);
                if (ProducesProjectedCollection(variable.Init.OrDefault() as BirGraphValue))
                    projectedCollections.Add(variable.Name.Value);
            }
            if (Parameters(node).Length != 0) return;
            node.VisitChildren(child => RecordLocals(child, variables, projectedCollections));
        }

        // Provenance follows only the value-producing spine, never an arbitrary
        // argument containing a projected collection inside an unrelated call.
        internal static bool ProducesProjectedCollection(BirGraphValue node) => node switch {
            BirCast value when value.DotktProjectedCollection.OrDefault() => true,
            BirCallStatic value when value.DotktProjectedCollection.OrDefault() => true,
            BirValueBlock value => ProducesProjectedCollection(value.Result.OrDefault() as BirGraphValue),
            BirConditional value => ProducesProjectedCollection(value.Then.OrDefault() as BirGraphValue)
                || ProducesProjectedCollection(value.Else.OrDefault() as BirGraphValue),
            BirNullableWrap value => ProducesProjectedCollection(value.E.OrDefault() as BirGraphValue),
            _ => false,
        };

        internal TypeNode Receiver(IBirExpression expression, bool allowExprShapes = true)
        {
            if (expression is not BirGraphNode node) return null;
            var stamp = node switch {
                BirLocal value => value.Sty.OrDefault(), BirFieldGet value => value.Sty.OrDefault(),
                BirCallInstance value => value.Sty.OrDefault(), BirCallStatic value => value.Sty.OrDefault(),
                BirCallLocal value => value.Sty.OrDefault(), BirStaticFieldGet value => value.Sty.OrDefault(),
                BirFieldSetExpression => null, BirConstrainedCall value => value.Sty.OrDefault(),
                BirGenericStatic value => value.Sty.OrDefault(), BirGenericInstance value => value.Sty.OrDefault(),
                BirNativeStatic value => value.Sty.OrDefault(), BirNativeInstance value => value.Sty.OrDefault(),
                BirPropertyGet value => value.Sty.OrDefault(), BirCast value => value.Sty.OrDefault(),
                _ => node.ResultTypeStamp,
            };
            // The receiver policy recognizes specific declaration facts, not
            // every result shape admitted by the general Surface reader.
            var fallback = node switch {
                BirFieldGet value => value.Ret.OrDefault(), BirCallInstance value => value.Ret.OrDefault(),
                BirArrayGet value => value.Elem.OrDefault(), BirCast value => value.Type.OrDefault(), _ => null,
            };
            return MemberCallSubstitution.CollectionReceiverType(node.Tag, stamp,
                expression is BirLocal local ? local.Name.OrDefault() : null, fallback, Variables, allowExprShapes);
        }
    }
}
