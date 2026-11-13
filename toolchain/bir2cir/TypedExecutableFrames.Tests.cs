using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class TypedExecutableFrames
{
    internal static void SelfTest()
    {
        TypeNode method = new TypeNode.Tv("method", 1), owner = new TypeNode.Tv("type", 0), text = new TypeNode.Fqn("System.String");
        var application = new TypeNode.Fqn("probe.Box", new[] { owner });
        var arguments = new TypeNode[] { method, application };
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn("probe.Owner", new[] { new TypeNode.Tv("method", 0), method, owner }),
            Method = "target", DeclarationId = "selected", Sig = new[] { method }, ArgTypes = new[] { method },
            TypeArgs = new[] { method }, Ret = method,
        };
        var substituted = (BirCallStatic)Substitute(call, arguments, 2, new[] { text });
        var actual = ((TypeNode.Fqn)substituted.Owner.Value).Args;
        if (actual[0] != method || actual[1] != application || actual[2] != text
            || substituted.Sig.Value[0] != method || substituted.ArgTypes.Value[0] != method
            || substituted.TypeArgs.Value[0] != application || substituted.Ret.Value != application
            || substituted.DeclarationId.Value != "selected"
            || ReferenceEquals(((TypeNode.Fqn)actual[1]).Args, application.Args))
            throw new InvalidOperationException("Executable substitution changed simultaneous application or foreign declaration frames");
        var construction = (BirNew)Substitute(new BirNew { Type = application, ArgTypes = new[] { method } }, arguments, 2);
        if (construction.ArgTypes.Value[0] != application)
            throw new InvalidOperationException("Constructor application was treated as a foreign formal signature");
        var localDeclaration = new BirFunctionDeclaration { Name = "local", Ret = method,
            _syntheticTypeArgs = new[] { method, owner }, TypeParams = new[] {
                new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "T" } } };
        var local = (BirLocalFunction)Substitute(new BirLocalFunction { Id = "local-id", Decl = localDeclaration }, arguments, 2);
        if (!ReferenceEquals(local.Decl.Value, localDeclaration))
            throw new InvalidOperationException("Inline substitution entered a local declaration's independent frame");

        var nested = new BirNewClosure {
            TypeArgs = new[] { method }, SynthClass = new BirClosureDefinition {
                Name = "probe.Inner", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Fields = new[] { new BirCaptureDescriptor { Name = "value", Type = method } }, Ret = method,
            },
        };
        var physical = new BirFunctionDeclaration {
            Name = "invoke", Ret = method, RetKotlinType = method, RetSuspendFnTypePre = method,
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = nested },
                new BirExpressionStatement { Expr = call } },
        };
        var sam = new BirNewSam {
            TypeArgs = new[] { method, owner, method }, SynthClass = new BirClassDefinition {
                Name = "probe.Sam", TypeParams = new[] { new BirTypeParameter { Name = "T" },
                    new BirTypeParameter { Name = "U" }, new BirTypeParameter { Name = "Duplicate" } },
                Fields = new[] { new BirFieldDeclaration { Name = "value", Type = method,
                    KotlinType = method, NullableGeneric = method } }, Methods = new[] { physical },
            },
        };
        var prepared = (BirNewSam)Prebind(sam);
        var payload = prepared.SynthClass.Value;
        var dense = new TypeNode.Tv("type", 0);
        var preparedMethod = payload.Methods.Value[0];
        var preparedNested = (BirNewClosure)((BirExpressionStatement)preparedMethod.Body.Value[0]).Expr.Value;
        var preparedCall = (BirCallStatic)((BirExpressionStatement)preparedMethod.Body.Value[1]).Expr.Value;
        if (!payload._syntheticFrameBound.OrDefault() || payload.Fields.Value[0].Type.Value != dense
            || payload.Fields.Value[0].KotlinType.Value != dense || payload.Fields.Value[0].NullableGeneric.Value != dense
            || preparedMethod.Ret.Value != dense || preparedMethod.RetKotlinType.Value != dense
            || preparedMethod.RetSuspendFnTypePre.Value != method
            || preparedCall.Sig.Value[0] != method || preparedCall.Ret.Value != dense
            || preparedNested.SynthClass.Value.Fields.Value[0].Type.Value != dense
            || preparedNested.TypeArgs.Value[0] != dense
            || sam.SynthClass.Value.Fields.Value[0].Type.Value != method)
            throw new InvalidOperationException("Synthetic frame prebinding crossed a nested declaration or lost frozen/source facts");
        var specialized = (BirNewSam)Substitute(prepared, arguments, 2, new[] { text });
        if (specialized.TypeArgs.Value[0] != application || specialized.TypeArgs.Value[1] != text
            || specialized.SynthClass.Value.Fields.Value[0].Type.Value != dense
            || ((BirNewSam)Prebind(specialized)).SynthClass.Value.Fields.Value[0].Type.Value != dense)
            throw new InvalidOperationException("Inline specialization reinterpreted an already-dense synthetic class frame");

        var source = new BirNewSuspendLambda {
            TypeArgs = new[] { method }, Captures = new[] { new BirCaptureDescriptor { Name = "value", Type = method } },
            CapValues = new IBirExpression[] { new BirLocal { Name = "value", Sty = method } },
            FuncType = new TypeNode.Fn(true, method, Array.Empty<TypeNode>()), Sty = method,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "value", Sty = method } } },
        };
        var instantiated = (BirNewSuspendLambda)Substitute(source, arguments, 2, new[] { text });
        if (instantiated._suspendDeclarationTypeArgs.Value[0] != method || instantiated.TypeArgs.Value[0] != application
            || instantiated.Captures.Value[0].Type.Value != method
            || ((BirLocal)instantiated.CapValues.Value[0]).Sty.Value != application
            || ((TypeNode.Fn)instantiated.FuncType.Value).Ret != application
            || ((BirLocal)((BirReturn)instantiated.Body.Value[0]).Value.Value).Sty.Value != method
            || source._suspendDeclarationTypeArgs.IsPresent)
            throw new InvalidOperationException("Source suspend application lost its original declaration frame");
        var denseSuspend = (BirNewSuspendLambda)Substitute(source with { TypeFrame = "dense", TypeArgs = new[] { owner } },
            arguments, 2, new[] { text });
        if (denseSuspend.TypeArgs.Value[0] != owner || denseSuspend._suspendDeclarationTypeArgs.IsPresent
            || ((BirLocal)((BirReturn)denseSuspend.Body.Value[0]).Value.Value).Sty.Value != application)
            throw new InvalidOperationException("Dense suspend substitution changed its independent class frame");
        Console.WriteLine("[typed executable frames] self-test OK (simultaneous substitution, exact foreign/local boundaries, dense/source frames)");
    }
}
