using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DefaultArgSplice
{
    internal static void SourcePreparationSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var function = new TypeNode.Fn(false, variable, Array.Empty<TypeNode>());
        var suspendFunction = new TypeNode.Fn(true, variable, Array.Empty<TypeNode>());
        var sourceClosure = new BirNewClosure {
            ClosureType = new TypeNode.Fqn("DefaultClosure", new TypeNode[] { variable }), Method = "invoke", FuncType = function,
            TypeArgs = new TypeNode[] { variable }, Captures = new IBirExpression[] { new BirDefaultParameter { Idx = 0 } },
            SynthClass = new BirClosureDefinition {
                Name = "DefaultClosure", SemanticOwner = "Producer", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Fields = new[] { new BirCaptureDescriptor { Name = "value", Type = variable } }, Params = Array.Empty<BirLambdaParameter>(), Ret = variable,
                Body = new IBirStatement[] { new BirReturn { Value = new BirFieldGet {
                    OwnerType = new TypeNode.Fqn("DefaultClosure", new TypeNode[] { variable }), Recv = new BirThis(), Name = "value", Sty = variable,
                } } },
            },
        };
        var sourceSuspend = new BirNewSuspendLambda {
            Arity = 0, TypeParams = new[] { new BirTypeParameter { Name = "T" } }, TypeArgs = new TypeNode[] { variable },
            Captures = new[] { new BirCaptureDescriptor { Name = "value", Type = variable } },
            CapValues = new IBirExpression[] { new BirDefaultParameter { Idx = 0 } }, Params = Array.Empty<BirLambdaParameter>(),
            SuspendRet = variable, FuncType = suspendFunction,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "value", Sty = variable } } },
        };
        var declaration = new BirFunctionDeclaration {
            Name = "defaults", DeclarationId = "defaults", Static = true, Mods = new BirModifiers { Inline = true },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ret = unit, Body = Array.Empty<IBirStatement>(),
            Params = new[] {
                new BirParameter { Name = "value", Type = variable },
                new BirParameter { Name = "action", Type = function, Attrs = new[] { DefaultBirCarrier.Create(1, sourceClosure) } },
                new BirParameter { Name = "suspendAction", Type = suspendFunction, Attrs = new[] { DefaultBirCarrier.Create(2, sourceSuspend) } },
            },
        };
        var call = new BirCallInline {
            Owner = new TypeNode.Fqn("FrameDefaults"), Callee = new TypeNode.Fqn("FrameDefaults.defaults"), DeclarationId = "defaults",
            Pc = 3, Ga = 1, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) }, SemanticOwner = "FrameDefaults", RetType = unit,
            Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = new TypeNode.Tv("method", 1) }, null, null },
        };
        var caller = new BirFunctionDeclaration {
            Name = "use", DeclarationId = "use", Static = true, TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } },
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 1) } }, Ret = unit,
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } },
        };
        var file = new BirFileDeclaration { FileClass = "FrameDefaults", Methods = new[] { declaration, caller }, Types = Array.Empty<IBirTypeDeclaration>() };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var policy = GenericRepresentationPolicy.CreateSource(new[] { file }, new Dictionary<string, string>(), refs);
        var restore = PrepareInlineDefaults(new[] { file }, refs);
        var preparedClosure = (BirNewClosure)call.PreparedDefaults.Value[1];
        var preparedSuspend = (BirNewSuspendLambda)call.PreparedDefaults.Value[2];
        var stagedClosure = file.Types.Value.OfType<BirClassDefinition>().Single(type => type.Name.Value == ((TypeNode.Fqn)preparedClosure.ClosureType.Value).Name);
        var stagedSuspend = file.Methods.Value.Single(method => method.DeclarationId.OrDefault() == preparedSuspend.DeclarationId.Value);
        if (call.Args.Value[1] != null || call.Args.Value[2] != null || call.PreparedDefaults.Value[0] != null
            || preparedClosure.SynthClass.IsPresent || preparedSuspend.Body.IsPresent
            || !stagedClosure.Methods.Value.Single()._preparedDelegateReturnArgument.OrDefault())
            throw new InvalidOperationException("Default preparation consumed omission slots or failed to expose independent declaration frames");
        NullableRepresentationMaterialization.Apply(new[] { file }, _ => false, refs, policy: policy);
        var expectedClosureReturn = stagedClosure.Methods.Value.Single().Ret.Value;
        var expectedSuspendReturn = stagedSuspend.Ret.Value;
        var keepMethod = new BirFunctionDeclaration { Name = "keep", Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>() };
        var keepType = new BirClassDefinition { Name = "Keep", Kind = "class" };
        file.Methods = file.Methods.Value.Append(keepMethod).ToArray();
        file.Types = file.Types.Value.Append(keepType).ToArray();
        restore();
        if (!file.Methods.Value.Contains(caller, ReferenceEqualityComparer.Instance)
            || !file.Methods.Value.Contains(keepMethod, ReferenceEqualityComparer.Instance)
            || file.Methods.Value.Contains(stagedSuspend, ReferenceEqualityComparer.Instance)
            || file.Types.Value.Length != 1 || !ReferenceEquals(file.Types.Value[0], keepType)
            || preparedClosure.SynthClass.Value.Ret.Value != expectedClosureReturn
            || !preparedClosure.SynthClass.Value._syntheticFrameBound.OrDefault()
            || preparedSuspend.SuspendRet.Value != expectedSuspendReturn || preparedSuspend.DeclarationId.IsPresent
            || preparedSuspend.TypeFrame.Value != "dense" || preparedSuspend._suspendDeclarationTypeArgs.Value.Length != preparedSuspend.TypeParams.Value.Length
            || preparedClosure.Captures.Value[0] is not BirDefaultParameter || preparedSuspend.CapValues.Value[0] is not BirDefaultParameter
            || call.Args.Value[1] != null || call.Args.Value[2] != null
            || sourceClosure.SynthClass.Value.Name.Value != "DefaultClosure" || !sourceSuspend.Body.HasValue)
            throw new InvalidOperationException("Default frame restore lost current transformed facts, deferred tokens, source independence or live collection identity");
        Console.WriteLine("[source default frame preparation] self-test OK (null slots, independent payloads, staged frames, current-fact restore, retained declarations)");
    }
}
