using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ClosureSynthesis;

// Expose a source suspend lambda's declaration frame through an ordinary
// temporary MethodDef, then return its transformed facts to the same occurrence.
static class PreparedSuspendDefaultFrames
{
    static int _next;

    internal static Action Stage(BirFileDeclaration file, IEnumerable<MaterializedOccurrence> inputs)
    {
        var staged = new List<(BirNewSuspendLambda Node, BirFunctionDeclaration Declaration, int Captures)>();
        var seen = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        void Walk(BirGraphValue node)
        {
            if (node == null || node is BirAttribute or IBirExecutablePayload || !seen.Add(node)) return;
            if (node is BirNewSuspendLambda { _suspendDeclarationTypeArgs.HasValue: true } lambda)
            {
                var origins = lambda._suspendDeclarationTypeArgs.Value;
                var sourceParameters = lambda.TypeParams.Value;
                if (origins.Length != sourceParameters.Length)
                    throw new InvalidOperationException("Prepared suspend default has inconsistent source frame");
                var owner = new List<TypeNode>();
                var method = new List<TypeNode>();
                for (var index = 0; index < origins.Length; index++)
                {
                    if (origins[index] is not TypeNode.Tv variable)
                        throw new InvalidOperationException("Suspend declaration frame requires authored type variables");
                    var target = variable.Scope == "type" ? owner : method;
                    var at = lambda.TypeFrame.OrDefault() == "dense" ? index : variable.I;
                    while (target.Count <= at) target.Add(null);
                    target[at] = new TypeNode.Tv("method", index);
                }
                var captures = lambda.Captures.OrEmpty();
                var parameters = lambda.Params.OrEmpty();
                var serial = Interlocked.Increment(ref _next);
                var declaration = new BirFunctionDeclaration {
                    Name = "__preparedSuspendDefault$" + serial,
                    DeclarationId = DeclarationIdentityBinding.PhysicalOnlyId("prepared-suspend-default", serial.ToString()),
                    Generated = true, Static = true,
                    TypeParams = sourceParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray(),
                    Params = captures.Select(capture => ToParameter((BirCaptureDescriptor)capture.DeepClone()))
                        .Concat(parameters.Select(parameter => ToParameter((BirLambdaParameter)parameter.DeepClone()))).ToArray(),
                    Ret = lambda.SuspendRet.HasValue ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(lambda.SuspendRet.Value)) : OptionalField<TypeNode>.Null,
                    Body = lambda.Body.HasValue ? OptionalField<IBirStatement[]>.Of(lambda.Body.Value
                        .Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()).ToArray()) : OptionalField<IBirStatement[]>.Null,
                };
                declaration = (BirFunctionDeclaration)TypedExecutableFrames.Substitute(declaration, method.ToArray(), method.Count, owner.ToArray());
                lambda.Captures = default; lambda.Params = default; lambda.SuspendRet = default; lambda.Body = default;
                lambda.TypeParams = default; lambda._suspendDeclarationTypeArgs = default;
                lambda.DeclarationId = declaration.DeclarationId;
                if (!file.Methods.HasValue) throw new InvalidOperationException("Prepared suspend default has no methods array");
                file.Methods = file.Methods.Value.Append(declaration).ToArray();
                staged.Add((lambda, declaration, captures.Length));
                Walk(declaration);
            }
            node.VisitChildren(Walk);
        }
        foreach (var input in inputs.ToArray()) Walk(input.Slot.Value);
        return () => {
            // Nested declarations must be restored before an outer transformed
            // body is independently cloned back into its source occurrence.
            foreach (var (node, declaration, captures) in staged.AsEnumerable().Reverse())
            {
                var parameters = declaration.Params.Value;
                node.Captures = parameters.Take(captures).Select(parameter => ToCapture((BirParameter)parameter.DeepClone())).ToArray();
                node.Params = parameters.Skip(captures).Select(parameter => ToLambdaParameter((BirParameter)parameter.DeepClone())).ToArray();
                node.SuspendRet = BirGraphValue.CopyType(declaration.Ret.Value);
                node.Body = declaration.Body.Value.Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()).ToArray();
                var typeParameters = declaration.TypeParams.Value;
                node.TypeParams = typeParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
                node.TypeFrame = "dense";
                node._suspendDeclarationTypeArgs = Enumerable.Range(0, typeParameters.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
                node.DeclarationId = default;
                file.Methods = file.Methods.Value.Where(method => !ReferenceEquals(method, declaration)).ToArray();
            }
        };
    }
}
