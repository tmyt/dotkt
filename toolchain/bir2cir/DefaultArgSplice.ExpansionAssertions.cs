using System;
using System.IO;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DefaultArgSplice
{
    internal static void AssertPlaceholdersPlanned(BirGraphValue root, bool inBindingExpression) => AssertPlanned(root, inBindingExpression);

    internal static void AssertNoPlaceholder(BirGraphValue root, string context)
    {
        if (root == null) return;
        var here = DeclarationContext(root, context);
        if (root is IBirTypeDeclaration { Name.HasValue: true } owner)
            foreach (var constructor in owner.Ctors.OrEmpty())
            {
                var bindings = constructor.DelegationBindings.OrEmpty();
                for (var index = 0; index < bindings.Length; index++)
                {
                    if (bindings[index].Expr.OrDefault() is not BirDefaultArgument) continue;
                    var role = bindings[index].Role.OrDefault() ?? $"argument binding {index}";
                    var target = constructor.BaseArgs.HasValue ? (owner.Base.OrDefault() as TypeNode.Fqn)?.Name ?? "unknown" : owner.Name.Value;
                    throw new InvalidOperationException(DiagnosticPrefix(here)
                        + $"cannot fill an omitted default argument for '{target} constructor' ({role}) from the "
                        + "selected referenced declaration: its optional value could not be obtained or represented. "
                        + "The reference may be stale or the value may not be carryable. Pass the argument explicitly.");
                }
            }
        if (root is BirCallEvaluation { Expr.HasValue: true } evaluation)
        {
            var bindings = evaluation.Bindings.OrEmpty();
            for (var index = 0; index < bindings.Length; index++)
            {
                if (bindings[index].Expr.OrDefault() is not BirDefaultArgument) continue;
                var role = bindings[index].Role.OrDefault() ?? $"argument binding {index}";
                throw new InvalidOperationException(DiagnosticPrefix(here)
                    + $"cannot fill an omitted default argument for '{CallLabel((BirGraphValue)evaluation.Expr.Value)}' ({role}) from the selected referenced "
                    + "declaration: its optional value could not be obtained or represented. The reference may be "
                    + "stale or the value may not be carryable. Pass the argument explicitly.");
            }
        }
        if (root is BirDefaultArgument)
            throw new InvalidOperationException(DiagnosticPrefix(here)
                + "an omitted cross-module default argument was not filled because its optional value could not be "
                + "obtained or represented from the selected referenced declaration. The reference may be stale or "
                + "the value may not be carryable. Pass the argument explicitly.");
        root.VisitChildren(child => AssertNoPlaceholder(child, here));
    }

    internal static string DeclarationContext(BirGraphValue node, string fallback)
    {
        var (eligible, name, position) = node switch {
            BirFunctionDeclaration method => (method.Body.HasValue, method.Name.OrDefault(), method.Pos.OrDefault()),
            BirClosureDefinition closure => (closure.Body.HasValue, closure.Name.OrDefault(), (BirDeclarationPosition)null),
            BirClassDefinition type => (type.Ctors.HasValue, type.Name.OrDefault(), type.Pos.OrDefault()),
            BirInterfaceDefinition type => (type.Ctors.HasValue, type.Name.OrDefault(), type.Pos.OrDefault()),
            BirEnumDefinition type => (type.Ctors.HasValue, type.Name.OrDefault(), type.Pos.OrDefault()),
            _ => (false, null, null),
        };
        if (!eligible || name == null) return fallback;
        if (position?.F.OrDefault() is { } file)
        {
            var location = Path.GetFileName(file);
            if (position.L.HasValue)
            {
                location += ":" + position.L.Value;
                if (position.C.HasValue) location += ":" + position.C.Value;
            }
            return location + ": " + name;
        }
        return fallback ?? name;
    }

    static string CallLabel(BirGraphValue call)
    {
        if (call is BirNew construction) return ((construction.Type.OrDefault() as TypeNode.Fqn)?.Name ?? "unknown") + " constructor";
        var (owner, member) = call switch {
            BirCallInstance instance => (instance.OwnerType.OrDefault(), instance.Method.OrDefault()),
            BirCallStatic value => (value.CalleeOwner.OrDefault() ?? value.Owner.OrDefault(), value.Method.OrDefault()),
            BirCallInline inline => (inline.Owner.OrDefault(), (string)null),
            _ => ((TypeNode)null, (string)null),
        };
        member ??= (call as BirGraphNode)?.Tag ?? "unknown call";
        return owner is TypeNode.Fqn named ? named.Name + "." + member : member;
    }
}
