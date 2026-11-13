using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DefaultArgSplice
{
    static IBirExpression Clone(IBirExpression value) => value == null ? null : (IBirExpression)((BirGraphValue)value).DeepClone();

    static IBirExpression ParameterDefault(BirParameter parameter) => DefaultBirCarrier.OfParameter(parameter);

    // Preparation already selected representation and type arguments. Expansion
    // consumes that exact occurrence; repeating SpliceOne would substitute twice.
    internal static IBirExpression InstantiatePreparedInlineDefault(IBirExpression prepared,
        string semanticOwner, int spliceCloneId, IBirExpression dispatch, IBirExpression extension,
        IReadOnlyList<IBirExpression> arguments)
    {
        var expression = Rehome((BirGraphValue)Clone(prepared), semanticOwner, spliceCloneId);
        expression = TypedExecutableFrames.Prebind(expression);
        return SubstituteTokens((IBirExpression)expression, dispatch, extension, null, arguments);
    }

    internal static IBirExpression MaterializeDefault(IBirExpression source, List<BirFunctionDeclaration> hoist, string localOwner)
    {
        var parsed = (IBirExpression)ObjectSlotRename.Apply((BirGraphValue)Clone(source));
        AssertPlanned((BirGraphValue)parsed, false);
        var freshened = false;
        if (parsed is BirDefaultCarrier carrier)
        {
            var methods = carrier.Lifted.OrEmpty();
            var rename = methods.Where(method => method.Name.HasValue).ToDictionary(method => method.Name.Value,
                _ => "__dflt$lambda$" + FreshDefaultIdentity(), StringComparer.Ordinal);
            parsed = carrier.Expr.OrDefault();
            if (parsed == null) return null;
            void Targets(BirGraphValue root) => root.Visit(node => {
                if (node is BirNewDelegate { Method.HasValue: true } target && rename.TryGetValue(target.Method.Value, out var fresh))
                {
                    target.Method = fresh;
                    target.CalleeOwner = localOwner == null ? OptionalField<TypeNode>.Null : new TypeNode.Fqn(localOwner);
                }
            });
            Targets((BirGraphValue)parsed);
            foreach (var method in methods)
            {
                if (method.Name.HasValue && rename.TryGetValue(method.Name.Value, out var fresh)) method.Name = fresh;
                Targets(method);
            }
            LexicalDeclarationIds.Freshen(new[] { (BirGraphValue)parsed }.Concat(methods).ToArray());
            freshened = true;
            foreach (var method in methods)
            {
                if (localOwner == null) throw new InvalidOperationException("default carrier has no authored consumer file owner");
                method.SemanticOwner = default;
                hoist.Add((BirFunctionDeclaration)Rehome(method, localOwner, FreshDefaultIdentity()));
            }
        }
        if (parsed != null)
        {
            if (!freshened) LexicalDeclarationIds.Freshen((BirGraphValue)parsed);
            CallEvalLowering.FreshenPlanIds((BirGraphValue)parsed);
        }
        return parsed;
    }

    static void AssertPlanned(BirGraphValue node, bool bindingExpression)
    {
        if (node is BirDefaultArgument && !bindingExpression)
            throw new InvalidOperationException(UnplannedDefault());
        if (node is BirCallBinding binding)
        {
            if (binding.Expr.HasValue) AssertPlanned((BirGraphValue)binding.Expr.Value, binding.Id.IsPresent && binding.Phase.IsPresent);
            return;
        }
        node.VisitChildren(child => AssertPlanned(child, false));
    }

    static IBirExpression SubstituteTokens(IBirExpression source, IBirExpression dispatch, IBirExpression extension,
        IBirExpression enclosing, IReadOnlyList<IBirExpression> arguments)
    {
        BirGraphValue Rewrite(BirGraphValue node)
        {
            if (node is BirDefaultReceiver receiver)
            {
                var replacement = receiver.Kind.OrDefault() switch { "dispatch" => dispatch, "extension" => extension, "enclosing" => enclosing, _ => null };
                return (BirGraphValue)(Clone(replacement) ?? (IBirExpression)node.DeepClone());
            }
            if (node is BirDefaultParameter parameter && parameter.Idx.HasValue
                && parameter.Idx.Value >= 0 && parameter.Idx.Value < arguments.Count && arguments[parameter.Idx.Value] != null)
                return (BirGraphValue)Clone(arguments[parameter.Idx.Value]);
            return node.RewriteChildren(Rewrite);
        }
        return (IBirExpression)Rewrite((BirGraphValue)source);
    }

    static IBirExpression SpliceOne(IBirExpression source, IBirExpression dispatch, IBirExpression extension,
        IBirExpression enclosing, IReadOnlyList<IBirExpression> arguments, List<BirFunctionDeclaration> hoist,
        string localOwner, string semanticOwner, TypeNode[] methodArguments, TypeNode[] ownerArguments, string method, int slot)
    {
        var expression = MaterializeDefault(source, hoist, localOwner);
        if (expression == null) return null;
        if (semanticOwner == null) throw new InvalidOperationException(MissingDefaultOwner(method));
        expression = (IBirExpression)Rehome((BirGraphValue)expression, semanticOwner, FreshDefaultIdentity());
        expression = (IBirExpression)TypedExecutableFrames.Prebind((BirGraphValue)expression);
        expression = (IBirExpression)TypedExecutableFrames.Substitute((BirGraphValue)expression,
            methodArguments ?? Array.Empty<TypeNode>(), methodArguments?.Length ?? 0, ownerArguments);
        expression = SubstituteTokens(expression, dispatch, extension, enclosing, arguments);
        string unbound = null;
        ((BirGraphValue)expression).Visit(node => { if (node is BirDefaultReceiver receiver) unbound ??= receiver.Kind.OrDefault() ?? "unknown"; });
        if (unbound != null) throw new InvalidOperationException(UnavailableDefaultReceiver(method, slot, unbound));
        return expression;
    }

    static BirGraphValue Rehome(BirGraphValue root, string owner, int clone)
    {
        TypeNode RenameType(TypeNode type, string oldName, string next) => type switch {
            TypeNode.Fqn named => named with { Name = named.Name == oldName ? next : named.Name, Args = named.Args?.Select(argument => RenameType(argument, oldName, next)).ToArray() },
            TypeNode.Nullable value => value with { Of = RenameType(value.Of, oldName, next) },
            TypeNode.Oblivious value => value with { Of = RenameType(value.Of, oldName, next) },
            TypeNode.Projection value => value with { Of = RenameType(value.Of, oldName, next) },
            TypeNode.Array value => value with { Elem = RenameType(value.Elem, oldName, next) },
            TypeNode.ByRef value => value with { Of = RenameType(value.Of, oldName, next) },
            TypeNode.Ptr value => value with { Of = RenameType(value.Of, oldName, next) },
            TypeNode.Mod value => value with { M = RenameType(value.M, oldName, next), Of = RenameType(value.Of, oldName, next) },
            TypeNode.Fn value => value with { Ret = RenameType(value.Ret, oldName, next), Params = value.Params.Select(argument => RenameType(argument, oldName, next)).ToArray(),
                Recv = value.Recv == null ? null : RenameType(value.Recv, oldName, next), Ctx = value.Ctx?.Select(argument => RenameType(argument, oldName, next)).ToArray() },
            TypeNode.FnPtr value => value with { Ret = RenameType(value.Ret, oldName, next), Params = value.Params.Select(argument => RenameType(argument, oldName, next)).ToArray() },
            _ => type,
        };
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is BirCallInline inline) inline.SemanticOwner = owner;
            if (node is BirCallEvaluation evaluation) evaluation.SemanticOwner = owner;
            BirGraphValue synthetic = node switch {
                BirNewClosure construction => construction.SynthClass.OrDefault(), BirNewSam sam => sam.SynthClass.OrDefault(), _ => null,
            };
            var name = synthetic switch {
                BirClosureDefinition value => value.Name.OrDefault(), BirClassDefinition value => value.Name.OrDefault(), _ => null,
            };
            if (synthetic is BirClosureDefinition closure)
            {
                closure.SemanticOwner = owner; closure.OuterTypeParamCount = default; closure.OuterTypeParamOffset = default;
            }
            if (synthetic is BirClassDefinition type)
            {
                type.SemanticOwner = owner; type.OuterTypeParamCount = default; type.OuterTypeParamOffset = default;
            }
            if (name != null)
            {
                var next = name + "$dflt" + clone;
                // Replacing a generated type identity is a whole occurrence
                // operation; its declaration and every application move together.
                node = node.Rewrite(value => {
                    if (value is BirClosureDefinition closure && closure.Name.OrDefault() == name) closure.Name = next;
                    if (value is BirClassDefinition type && type.Name.OrDefault() == name) type.Name = next;
                    return value.RewriteTypeFields(type => RenameType(type, name, next));
                });
            }
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        return Walk(root);
    }
}
