using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventSubscriptionBinding
{
    internal static void SelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        foreach (var mode in new[] { "dense", "lexical", "prebound" })
        {
            var disposable = new TypeNode.Fqn("System.IDisposable");
            var cloneable = new TypeNode.Fqn("System.ICloneable");
            BirTypeParameter Parameter(string name, TypeNode bound) => new() { Name = name, Constraints = new[] { bound } };
            var methodVariable = new TypeNode.Tv("method", 0);
            TypeNode OwnerVariable(bool body) => new TypeNode.Tv("type", body && mode != "lexical" ? 1 : 0);
            BirEventGet Read(bool body) => new() { Name = "Changed", Static = false,
                Type = new TypeNode.Fqn("Native.Source", new TypeNode[] {
                    new TypeNode.Tv(mode == "prebound" && body ? "type" : "method", 0), OwnerVariable(body) }),
                Recv = new BirLocal { Name = "source" }, Sty = Handle(new TypeNode.Fqn("System.Action`1", new TypeNode[] {
                    new TypeNode.Tv(mode == "prebound" && body ? "type" : "method", 0) })) };
            var innerRead = Read(true);
            var outerRead = Read(false);
            var body = new IBirStatement[] { new BirExpressionStatement { Expr = innerRead } };
            var denseParameters = new[] { Parameter("CapturedM", cloneable), Parameter("CapturedO", disposable) };
            IBirExpression carrier = mode == "prebound"
                ? new BirNewClosure { ClosureType = new TypeNode.Fqn("Payload"), Method = "invoke",
                    FuncType = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>()),
                    TypeArgs = new TypeNode[] { methodVariable, new TypeNode.Tv("type", 0) }, Captures = new IBirExpression[] { outerRead },
                    SynthClass = new BirClosureDefinition { Name = "Payload", Fields = Array.Empty<BirCaptureDescriptor>(),
                        Params = Array.Empty<BirLambdaParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = body,
                        TypeParams = denseParameters, _syntheticFrameBound = true } }
                : new BirNewSuspendLambda { Arity = 0, TypeFrame = mode == "dense" ? OptionalField<string>.Of("dense") : default,
                    TypeParams = denseParameters, TypeArgs = new TypeNode[] { methodVariable, new TypeNode.Tv("type", 0) },
                    Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = new IBirExpression[] { outerRead },
                    Params = Array.Empty<BirLambdaParameter>(), Body = body, SuspendRet = new TypeNode.Fqn("kotlin.Unit"),
                    FuncType = new TypeNode.Fn(true, new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>()) };
            var method = new BirFunctionDeclaration { Name = "create", Params = Array.Empty<BirParameter>(),
                TypeParams = new[] { Parameter("M", cloneable) }, Pos = new BirDeclarationPosition { F = "Event.kt", L = 23 },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(carrier) } } };
            var owner = new BirClassDefinition { Name = "Owner", Kind = "class", TypeParams = new[] { Parameter("O", disposable) }, Methods = new[] { method } };
            var file = new BirFileDeclaration { FileClass = "EventFrames", Types = new IBirTypeDeclaration[] { owner } };
            var originalTypes = file.Types.Value;
            var originalMethods = owner.Methods.Value;
            var originalBody = method.Body.Value;
            Require(ReferenceEquals(Apply(file, refs, new Dictionary<(string, string), TypeNode>(), out var handles), file)
                && ReferenceEquals(file.Types.Value, originalTypes) && ReferenceEquals(owner.Methods.Value, originalMethods)
                && ReferenceEquals(method.Body.Value, originalBody), "original declaration/vector identity changed");
            Require(handles.Count == 2, "caller and carrier event locations were not both materialized");
            foreach (var occurrence in handles)
            {
                Require(occurrence.Context.Owner == "create" && occurrence.Context.Path == "Event.kt" && occurrence.Context.Line == 23,
                    "capture diagnostics lost the authored declaration context");
                foreach (var callback in Callbacks((BirValueBlock)occurrence.Slot.Value))
                {
                    var parameters = callback.SynthClass.Value.TypeParams.Value;
                    Require(parameters.Length == 2 && parameters[0].Constraints.Value.Single() == cloneable
                        && parameters[1].Constraints.Value.Single() == disposable, "dense, lexical or prebound declaration constraints changed");
                    var prepared = (BirClosureDefinition)TypedExecutableFrames.PrepareSynthetic(callback.SynthClass.Value, callback.TypeArgs.Value);
                    Require(prepared.Fields.Value[0].Type.Value is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "type", I: 1 }] },
                        "callback frame was rebound before ClosureSynthesis's shared preparation");
                }
            }
        }
        MaterializationOwnershipSelfTest(refs);
        ConstraintClosureSelfTest(refs);
        ForwardedOwnersSelfTest();
        refs.DisposeNet();
        Console.WriteLine("[event callback frames] self-test OK (owning slots, receiver once, complete handle, dense/lexical/prebound and constraint closure)");
    }

    static TypeNode.Fqn Handle(TypeNode handler) => new("kotlin.clr.ClrEvent", new[] { handler });
    static BirNewClosure[] Callbacks(BirValueBlock block) => ((BirNew)block.Result.Value).Args.Value.Cast<BirNewClosure>().ToArray();
    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed event subscription: " + message); }

    static void MaterializationOwnershipSelfTest(ReferenceMetadataIndex refs)
    {
        var sourceType = new TypeNode.Fqn("Native.Source");
        var handler = new TypeNode.Fqn("System.Action");
        BirEventGet Read(IBirExpression receiver, bool isStatic = false) => new() { Name = "Changed", Type = sourceType,
            Static = isStatic, Recv = receiver == null ? default : OptionalField<IBirExpression>.Of(receiver), Sty = Handle(handler), MemberVisibility = "protected" };
        var inner = Read(new BirCallStatic { Method = "first", Ret = sourceType });
        var sourceCall = new BirCallStatic { Method = "second", Ret = sourceType };
        var stored = new BirVariable { Name = "storedHandle", Type = Handle(handler), Init = inner };
        var receiverBlock = new BirValueBlock { Stmts = new IBirStatement[] { stored }, Result = sourceCall, Sty = sourceType };
        var outer = Read(receiverBlock);
        var staticRead = Read(null, true);
        var returned = new BirReturn { Value = outer };
        var file = new BirFileDeclaration { FileClass = "Subscriptions", Methods = new[] { new BirFunctionDeclaration { Name = "read",
            Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirExpressionStatement { Expr = staticRead }, returned } } } };
        Apply(file, refs, new Dictionary<(string, string), TypeNode>(), out var roots);
        Require(roots.Count == 2 && roots[0].Slot.Value is BirValueBlock first && first.Stmts.Value.Length == 0
            && Callbacks(first).All(callback => callback.Captures.Value.Length == 0), "static handle allocated or evaluated a receiver");
        Require(stored.Init.Value is BirValueBlock && ReferenceEquals(roots[1].Slot.Value, returned.Value.Value)
            && roots.All(item => !ReferenceEquals(item.Slot.Value, stored.Init.Value)),
            "materialized frontier overlaps or points at detached event records");
        var final = (BirValueBlock)returned.Value.Value;
        Require(ReferenceEquals(((BirVariable)final.Stmts.Value[0]).Init.Value, receiverBlock), "receiver was cloned or moved out of evaluation order");
        var uses = 0;
        file.Visit(node => { if (ReferenceEquals(node, sourceCall)) uses++; });
        Require(uses == 1 && Callbacks(final).Select(callback => ((BirLocal)callback.Captures.Value[0]).Name.Value)
            .All(name => name == "__clrEventReceiver2"), "receiver is not evaluated exactly once/shared by ordered callbacks");
        foreach (var callback in Callbacks(final))
        {
            var operation = (BirGraphValue)((BirExpressionStatement)callback.SynthClass.Value.Body.Value[0]).Expr.Value;
            Require(operation is BirEventAdd { HandlerExact.Value: true, MemberVisibility.Value: "protected" }
                or BirEventRemove { HandlerExact.Value: true, MemberVisibility.Value: "protected" }, "event operation lost its selected handler/visibility facts");
        }
        var replacement = (BirValueBlock)roots[1].Slot.Value.DeepClone();
        roots[1].Slot.Replace(replacement);
        Require(ReferenceEquals(returned.Value.Value, replacement), "published owning slot does not update the current source graph");

        var metadata = new ReferenceDotKtMetadata();
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        metadata.NullableFrames["kotlin.clr.ClrEvent"] = frame;
        var physicalRefs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var complete = new TypeNode.Fqn("kotlin.clr.ClrEvent", frame.Close(new TypeNode[] { handler },
            ordinary => ordinary, nullable => nullable, _ => new TypeNode.Fqn("object"), nullableStorage => nullableStorage));
        var physicalRead = Read(new BirLocal { Name = "source" }); physicalRead.Sty = complete;
        var physicalFile = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
            new BirReturn { Value = physicalRead } } } } };
        Apply(physicalFile, physicalRefs, new Dictionary<(string, string), TypeNode>(), out var physicalHandles);
        var handle = (BirValueBlock)physicalHandles.Single().Slot.Value;
        Require(TypeNodeDataEquality.Equal(handle.Sty.Value, complete) && TypeNodeDataEquality.Equal(((BirNew)handle.Result.Value).Type.Value, complete)
            && Callbacks(handle).All(callback => callback.SynthClass.Value.Params.Value[0].Type.Value == handler),
            "complete physical handle was reclosed or its storage companion was selected as the handler");
        physicalRefs.DisposeNet();
    }

    static void ConstraintClosureSelfTest(ReferenceMetadataIndex refs)
    {
        var parameters = new[] {
            new BirTypeParameter { Name = "A", Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") }, SpecialConstraints = new[] { "class" } },
            new BirTypeParameter { Name = "B", Constraints = new TypeNode[] { new TypeNode.Tv("method", 0) } },
            new BirTypeParameter { Name = "C", Constraints = new TypeNode[] { new TypeNode.Tv("method", 1) } },
        };
        var read = new BirEventGet { Name = "Changed", Type = new TypeNode.Fqn("Native.Source", new TypeNode[] { new TypeNode.Tv("method", 2) }),
            Recv = new BirLocal { Name = "source" }, Sty = Handle(new TypeNode.Fqn("System.Action")) };
        var method = new BirFunctionDeclaration { Name = "read", TypeParams = parameters, Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirReturn { Value = read } } };
        var file = new BirFileDeclaration { Methods = new[] { method } };
        Apply(file, refs, new Dictionary<(string, string), TypeNode>(), out var handles);
        foreach (var callback in Callbacks((BirValueBlock)handles.Single().Slot.Value))
        {
            Require(callback.TypeArgs.Value.Cast<TypeNode.Tv>().Select(variable => variable.I).SequenceEqual(new[] { 2, 1, 0 })
                && callback.SynthClass.Value.TypeParams.Value[2].SpecialConstraints.Value.Single() == "class",
                "transitive generic constraint dependencies or descriptor facts were dropped");
            var prepared = (BirClosureDefinition)TypedExecutableFrames.PrepareSynthetic(callback.SynthClass.Value, callback.TypeArgs.Value);
            Require(prepared.TypeParams.Value[0].Constraints.Value[0] == new TypeNode.Tv("type", 1)
                && prepared.TypeParams.Value[1].Constraints.Value[0] == new TypeNode.Tv("type", 2)
                && parameters[2].Constraints.Value[0] == new TypeNode.Tv("method", 1),
                "constraint closure was rebound twice or mutated its source declaration");
        }
        var captured = new BirTypeParameter { Name = "Captured", Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") } };
        var own = new BirTypeParameter { Name = "Own", Constraints = new TypeNode[] { new TypeNode.Fqn("System.ICloneable") } };
        var capturedRead = new BirEventGet { Name = "Changed", Type = new TypeNode.Fqn("Native.Source", new TypeNode[] { new TypeNode.Tv("type", 1) }),
            Recv = new BirLocal { Name = "source" }, Sty = Handle(new TypeNode.Fqn("System.Action`1", new TypeNode[] { new TypeNode.Tv("type", 0) })) };
        var nested = new BirClassDefinition { Name = "Outer.Nested", Kind = "class", NestedIn = "Outer", SemanticOwner = "Outer",
            CapturedTypeParams = new[] { captured }, TypeParams = new[] { own }, Methods = new[] { new BirFunctionDeclaration {
                Name = "read", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirReturn { Value = capturedRead } } } } };
        var nestedFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Outer", Kind = "class", TypeParams = new[] { captured } }, nested } };
        Apply(nestedFile, refs, new Dictionary<(string, string), TypeNode>(), out var nestedHandles);
        foreach (var callback in Callbacks((BirValueBlock)nestedHandles.Single().Slot.Value))
            Require(callback.SynthClass.Value.TypeParams.Value[0].Constraints.Value[0] == own.Constraints.Value[0]
                && callback.SynthClass.Value.TypeParams.Value[1].Constraints.Value[0] == captured.Constraints.Value[0],
                "captured owner prefix was omitted from the complete physical TypeDef frame");
    }
}
