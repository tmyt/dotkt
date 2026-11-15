using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendLambdaLowering
{
    static readonly TypeNode ContAnyTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";

    internal static void ApplyAll(SuspendColdLowering.SuspendModuleContext module)
    {
        module.BeginLambdaPhase();
        foreach (var file in module.Roots)
        {
            var appended = new List<IBirTypeDeclaration>();
            var counter = 0;
            var fileClass = file.FileClass.OrDefault() ?? "Kt";
            foreach (var method in file.Methods.OrEmpty()) WalkMethod(method, fileClass, null);
            foreach (var field in file.Fields.OrEmpty()) WalkField(field, fileClass, null);
            foreach (var owner in file.Types.OrEmpty().ToArray())
            {
                if (!owner.Name.HasValue) continue;
                var name = owner.Name.Value;
                foreach (var method in owner.Methods.OrEmpty()) WalkMethod(method, name, owner);
                foreach (var constructor in owner.Ctors.OrEmpty())
                    if (constructor.Body.HasValue)
                        WalkStatements(constructor.Body.Value, name + "_ctor", owner, name, ExtensionReceiver(constructor), null);
                foreach (var field in owner.Fields.OrEmpty()) WalkField(field, name, owner);
            }
            if (appended.Count != 0)
            {
                foreach (var type in appended) MaterializedExecutable.NormalizeSource((BirGraphValue)type);
                file.Types = file.Types.OrEmpty().Concat(appended).ToArray();
                module.Transitions.PublishTypes(file, appended);
            }

            void WalkMethod(BirFunctionDeclaration method, string prefix, IBirTypeDeclaration owner)
            {
                if (method.Body.HasValue)
                    WalkStatements(method.Body.Value, prefix + "_" + (method.Name.OrDefault() ?? "m"), owner, prefix,
                        ExtensionReceiver(method), ConstrainedTypeParameterReceiverBinding.CloneMethodParametersWithErasedSourceBounds(method));
            }
            void WalkField(BirFieldDeclaration field, string prefix, IBirTypeDeclaration owner)
            {
                if (field.Init.HasValue) field.Init = OptionalField<IBirExpression>.Of((IBirExpression)Walk((BirGraphValue)field.Init.Value,
                    prefix + "_" + (field.Name.OrDefault() ?? "f"), owner, prefix, null, null));
            }
            void WalkStatements(IBirStatement[] statements, string context, IBirTypeDeclaration owner,
                string ownerName, string outerSelf, BirTypeParameter[] methodParameters)
            {
                for (var index = 0; index < statements.Length; index++)
                    statements[index] = (IBirStatement)Walk((BirGraphValue)statements[index], context, owner, ownerName, outerSelf, methodParameters);
            }
            BirGraphValue Walk(BirGraphValue node, string context, IBirTypeDeclaration owner,
                string ownerName, string outerSelf, BirTypeParameter[] methodParameters)
            {
                if (node == null || node is IBirExecutablePayload) return node;
                if (node is BirNewSuspendLambda lambda) return BuildLambda(lambda, context, owner, ownerName, outerSelf, methodParameters);
                node.VisitSourceChildren((_, role, slot) => {
                    if (!Foreign(role)) slot.Replace(Walk(slot.Value, context, owner, ownerName, outerSelf, methodParameters));
                }, region => {
                    WalkStatements(region.Statements, context, owner, ownerName, outerSelf, methodParameters);
                    if (region.Result is { } result) result.Replace(Walk(result.Value, context, owner, ownerName, outerSelf, methodParameters));
                });
                return node;
            }
            BirGraphValue BuildLambda(BirNewSuspendLambda lambda, string context, IBirTypeDeclaration owner,
                string ownerName, string outerSelf, BirTypeParameter[] methodParameters)
            {
                var prefix = NormalizeOwnerCapturePrefix(lambda, ownerName, methodParameters, module);
                var parameters = lambda.TypeParams.OrEmpty();
                WalkStatements(lambda.Body.OrEmpty(), context, owner, ownerName, outerSelf, parameters.Skip(prefix).ToArray());
                var captures = lambda.Captures.OrEmpty();
                if (captures.Count(capture => capture.Outer.OrDefault()) > 1)
                    throw new NotSupportedException($"bir2cir: suspend-lambda lowering: `{context}` carries multiple enclosing-receiver captures; "
                        + "the producer must identify at most one outer:true capture");
                foreach (var capture in captures)
                    if (!capture.Type.HasValue)
                        throw new NotSupportedException($"bir2cir: suspend-lambda lowering: the capture `{capture.Name.OrDefault()}` carries no type — an earlier lowering dropped it.");
                var name = context + "_lambda" + ++counter + "$sm";
                var function = lambda.FuncType.OrDefault() as TypeNode.Fn;
                var restricted = function?.Recv is TypeNode.Fqn receiver && module.References.HasRestrictsSuspension(receiver.Name);
                var baseLocal = module.EmittedTypeNames.Contains(restricted ? RestrictedSuspendLambdaFqn : SuspendLambdaFqn);
                var built = new SuspendColdLowering.SourceFunGen(module, new SuspendColdLowering.SourceFunctionInput.Lambda(
                    file, owner, lambda, name, parameters, prefix, baseLocal, restricted)).Build();
                if (built.Types.Count != 1 || built.Types[0] is not BirClassDefinition machine || built.Methods.Count != 0)
                    throw new InvalidOperationException("A suspend lambda did not produce exactly its one state-machine declaration");
                machine.SemanticOwner = ownerName;
                appended.Add(machine);

                if (lambda.TypeArgs.HasValue && lambda.TypeArgs.Value.Length != parameters.Length)
                    throw new NotSupportedException($"bir2cir: suspend-lambda lowering: `{name}` has mismatched construction arguments and declaration parameters");
                if (parameters.Length != 0 && !lambda.TypeArgs.HasValue)
                    throw new NotSupportedException($"bir2cir: suspend-lambda lowering: generic `{name}` has no construction `typeArgs`");
                var application = new TypeNode.Fqn(name, parameters.Length == 0 ? null : CopyTypes(lambda.TypeArgs.Value));
                if (lambda.CapValues.IsPresent && !lambda.CapValues.IsNull && lambda.CapValues.Value.Length != captures.Length)
                    throw new NotSupportedException($"bir2cir: suspend-lambda lowering: `{name}` carries a capValues vector that does not align with its capture descriptors");
                var values = lambda.CapValues.OrDefault();
                var arguments = new List<IBirExpression>();
                var signature = new List<TypeNode>();
                for (var index = 0; index < captures.Length; index++)
                {
                    var capture = captures[index];
                    var argument = values != null && values[index] != null ? values[index]
                        : capture.Outer.OrDefault()
                            ? outerSelf != null ? (IBirExpression)new BirLocal { Name = outerSelf } : new BirThis()
                            : new BirLocal { Name = capture.Name.OrDefault() };
                    if (capture.Outer.OrDefault()) MarkOuter(argument);
                    arguments.Add(argument);
                    var parameter = TypedExecutableFrames.RewriteType(capture.Type.Value, variable => variable.Scope == "method"
                        ? new TypeNode.Tv("type", prefix + variable.I) : null);
                    if (application.Args is { } applied) parameter = SupertypeGraph.SubstOwnerTvs(parameter, applied);
                    signature.Add(parameter);
                }
                arguments.Add(new BirConstant { Type = BirGraphValue.CopyType(ContAnyTn), Value = new BirLiteral.NullLiteral() });
                signature.Add(BirGraphValue.CopyType(ContAnyTn));
                // Body and construction values have moved. Do not retire their
                // registered descendants with the consumed lambda declaration.
                lambda.CapValues = default;
                module.Transitions.RetireDiscardedSubtree(lambda);
                return new BirNew { Type = application, ArgTypes = signature.ToArray(), Args = arguments.ToArray() };
            }
        }
    }

    static string ExtensionReceiver(BirGraphValue declaration)
    {
        var parameters = declaration switch {
            BirFunctionDeclaration method => method.Params.OrEmpty(), BirConstructor constructor => constructor.Params.OrEmpty(), _ => Array.Empty<BirParameter>(),
        };
        return parameters.FirstOrDefault() is { } first && first.Mods.OrDefault()?.ExtensionReceiver.OrDefault() == true
            ? first.Name.OrDefault() : null;
    }
    static bool Foreign(BirGenericFrameRole role) => role is BirGenericFrameRole.ReferencedDeclaration
        or BirGenericFrameRole.ResolvedDeclaration or BirGenericFrameRole.OverrideDeclaration
        or BirGenericFrameRole.FrozenSourceFact or BirGenericFrameRole.StorageSourceType;
    static TypeNode[] CopyTypes(IEnumerable<TypeNode> source) => source.Select(BirGraphValue.CopyType).ToArray();
    static void MarkOuter(IBirExpression expression)
    {
        switch (expression)
        {
            case BirThis value: value.Outer = true; break;
            case BirLocal value: value.Outer = true; break;
            case BirFieldGet value: value.Outer = true; break;
        }
    }

    internal static int NormalizeOwnerCapturePrefix(BirNewSuspendLambda lambda, string owner,
        BirTypeParameter[] methodParameters, SuspendColdLowering.SuspendModuleContext module)
    {
        var ownerParameters = module.LambdaOwnerParameters.TryGetValue(owner, out var found) ? found : Array.Empty<BirTypeParameter>();
        var oldArguments = lambda.TypeArgs.OrEmpty();
        var declarationArguments = lambda._suspendDeclarationTypeArgs.OrDefault() ?? oldArguments;
        var oldParameters = lambda.TypeParams.OrEmpty();
        var dense = lambda.TypeFrame.OrDefault() == "dense";
        if (oldArguments.Length != oldParameters.Length) return 0;
        var consumed = new HashSet<int>();
        var declarations = new List<BirTypeParameter>();
        var arguments = new List<TypeNode>();
        var frameSlots = new Dictionary<(string, int), TypeNode>();
        var callerSlots = new Dictionary<(string, int), TypeNode>();
        var callerDeclarations = new HashSet<int>();
        (string, int)? FrameKey(int index) => declarationArguments[index] is TypeNode.Tv variable
            ? (variable.Scope, dense ? index : variable.I) : null;
        BirTypeParameter Clone(BirTypeParameter parameter) => (BirTypeParameter)module.Transitions.CloneIndependent(parameter);
        for (var slot = 0; slot < ownerParameters.Length; slot++)
        {
            var existing = Enumerable.Range(0, oldArguments.Length).FirstOrDefault(index => !consumed.Contains(index)
                && oldArguments[index] is TypeNode.Tv { Scope: "type" } variable && variable.I == slot, -1);
            var declaration = ownerParameters[slot];
            if (declaration == null || !declaration.Name.HasValue)
                throw new InvalidOperationException($"generic owner '{owner}' has a missing or unnamed slot {slot}");
            declarations.Add(Clone(declaration));
            arguments.Add(new TypeNode.Tv("type", slot));
            callerSlots[("type", slot)] = new TypeNode.Tv("type", slot);
            if (existing >= 0)
            {
                consumed.Add(existing);
                if (FrameKey(existing) is { } key) frameSlots[key] = new TypeNode.Tv("type", slot);
            }
        }
        var suffix = 0;
        for (var index = 0; index < oldArguments.Length; index++)
            if (!consumed.Contains(index))
            {
                BirTypeParameter declaration;
                if (oldArguments[index] is TypeNode.Tv { Scope: "method" } methodVariable
                    && methodParameters != null && methodVariable.I < methodParameters.Length)
                {
                    declaration = Clone(methodParameters[methodVariable.I]);
                    declaration.Name = oldParameters[index].Name;
                    callerDeclarations.Add(declarations.Count);
                }
                else declaration = Clone(oldParameters[index]);
                declarations.Add(declaration);
                arguments.Add(BirGraphValue.CopyType(oldArguments[index]));
                if (FrameKey(index) is { } key) frameSlots[key] = new TypeNode.Tv("method", suffix);
                if (oldArguments[index] is TypeNode.Tv caller)
                    callerSlots.TryAdd((caller.Scope, caller.I), new TypeNode.Tv("method", suffix));
                suffix++;
            }
        for (var index = ownerParameters.Length; index < declarations.Count; index++)
            if (callerDeclarations.Contains(index))
                foreach (var dependency in MethodDependencies(declarations[index]).Distinct().ToArray())
                    if (!callerSlots.ContainsKey(("method", dependency)))
                    {
                        callerDeclarations.Add(declarations.Count);
                        declarations.Add(Clone(methodParameters[dependency]));
                        arguments.Add(new TypeNode.Tv("method", dependency));
                        callerSlots[("method", dependency)] = new TypeNode.Tv("method", suffix++);
                    }
        TypeNode Remap(TypeNode type, IReadOnlyDictionary<(string, int), TypeNode> slots) =>
            TypedExecutableFrames.RewriteType(type, variable => slots.GetValueOrDefault((variable.Scope, variable.I)));
        var values = lambda.CapValues;
        var function = lambda.FuncType;
        var stamp = lambda.Sty;
        lambda.CapValues = default; lambda.FuncType = default; lambda.Sty = default;
        lambda.TypeArgs = default; lambda._suspendDeclarationTypeArgs = default; lambda.TypeFrame = default;
        TypedExecutableFrames.RewriteDeclarationFrameInPlace(lambda, type => Remap(type, frameSlots));
        for (var index = ownerParameters.Length; index < declarations.Count; index++)
        {
            var slots = callerDeclarations.Contains(index) ? callerSlots : frameSlots;
            TypedExecutableFrames.RewriteDeclarationFrameInPlace(declarations[index], type => Remap(type, slots));
        }
        lambda.TypeParams = declarations.ToArray(); lambda.TypeArgs = arguments.ToArray();
        lambda.CapValues = values; lambda.FuncType = function; lambda.Sty = stamp;
        return ownerParameters.Length;
    }

    static IEnumerable<int> MethodDependencies(BirTypeParameter declaration)
    {
        var result = new HashSet<int>();
        declaration.Visit(node => node.VisitOwnTypeFields((_, type) => {
            TypedExecutableFrames.RewriteType(type, variable => {
                if (variable.Scope == "method") result.Add(variable.I);
                return null;
            });
        }));
        return result;
    }
}
