using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void PhysicalLocalDelegateTargetsSourceSelfTest()
    {
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Physical local delegate targets: " + reason); }
        static TypeNode.Fqn Named(string name, params TypeNode[] arguments) => new(name, arguments.Length == 0 ? null : arguments);
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var previousReferences = _refs; var previousCarriers = _localCarriers;
        var adaptersBefore = _nextAdapter; var temporariesBefore = _nextValueAdapter;
        try
        {
            var unit = Named("kotlin.Unit"); var none = Named("void"); var text = Named("System.String");
            var erased = Named("System.Object"); var integer = Named("System.Int32");
            var ownerVariable = new TypeNode.Tv("type", 0); var methodVariable = new TypeNode.Tv("method", 0);
            var action = new TypeNode.Fn(false, none, Array.Empty<TypeNode>(), Clr: "System.Action");
            var unitFunction = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>(), Clr: "System.Func");
            BirFunctionDeclaration Method(string name, TypeNode result, params TypeNode[] parameters) => new() {
                Name = name, Ret = result, Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
            };
            BirExpressionStatement Evaluate(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
            var declaredVoid = Method("declaredUnit", none);
            var genericUnit = Method("generic", methodVariable);
            genericUnit.TypeParams = new[] { new BirTypeParameter { Name = "R" } };
            var fromVoid = new BirNewDelegate { CalleeOwner = Named("Functions"), Method = "declaredUnit",
                Sig = Array.Empty<TypeNode>(), FuncType = unitFunction };
            var fromGeneric = new BirNewDelegate { CalleeOwner = Named("Functions"), Method = "generic",
                Sig = Array.Empty<TypeNode>(), TypeArgs = new TypeNode[] { unit }, FuncType = action };
            var nullDestination = fromVoid with { DotktDelegateSlot = OptionalField<TypeNode>.Null };
            var exactDestination = fromVoid with { DotktDelegateSlot = Named("System.Func", text) };
            var unchangedValue = fromGeneric with { FuncType = unitFunction };

            var identity = Method("currentName", text, ownerVariable, methodVariable);
            identity.DeclarationId = "selected";
            identity.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
            var indexed = new BirNewDelegate { CalleeOwner = Named("Owner", text), Method = "oldName", DeclarationId = "selected",
                TypeArgs = new TypeNode[] { integer }, Sig = new TypeNode[] { erased, erased },
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text, integer }, Clr: "System.Func") };
            var plain = Method("choose", text, integer);
            var other = Method("choose", text, text);
            var merged = new BirNewDelegate { CalleeOwner = Named("Functions"), Method = "choose", Sig = new TypeNode[] { text },
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { text }, Clr: "System.Func") };
            var functionParameter = new TypeNode.Fn(false, integer, new TypeNode[] { integer }, Clr: "System.Func");
            var functionSlot = Method("functionSlot", text, erased);
            functionSlot.Params.Value[0].FunctionSignatureType = functionParameter;
            var discriminated = new BirNewDelegate { CalleeOwner = Named("Functions"), Method = "functionSlot", Sig = new TypeNode[] { erased },
                FuncType = new TypeNode.Fn(false, text, new TypeNode[] { erased }, Clr: "System.Func") };

            var captured = new BirLocal { Name = "capture" };
            var captures = new IBirExpression[] { captured };
            var invoke = Method("invoke", ownerVariable, erased);
            invoke.Params.Value[0].FunctionSignatureType = functionParameter;
            var closure = new BirNewClosure { ClosureType = Named("Closure"), Method = "invoke", TypeArgs = new TypeNode[] { unit },
                Captures = captures, FuncType = new TypeNode.Fn(false, none, new TypeNode[] { erased }, Clr: "System.Action") };
            var bound = new BirNewBoundDelegate { Method = "not-a-late-target", FuncType = unitFunction };
            var ordinary = new BirCallStatic { Method = "not-a-late-target", Owner = Named("Functions"), Sig = new TypeNode[] { text } };
            var use = Method("use", none);
            var body = new IBirStatement[] { Evaluate(fromVoid), Evaluate(fromGeneric), Evaluate(nullDestination), Evaluate(exactDestination),
                Evaluate(unchangedValue), Evaluate(indexed), Evaluate(merged), Evaluate(discriminated), Evaluate(closure), Evaluate(bound), Evaluate(ordinary) };
            use.Body = body;
            var payload = new byte[] { 3, 1, 4 }; use.InlineBir = payload;
            var owner = new BirClassDefinition { Kind = "class", Name = "Owner", TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { identity } };
            var closureOwner = new BirClassDefinition { Kind = "class", Name = "Closure", TypeParams = new[] { new BirTypeParameter { Name = "C" } }, Methods = new[] { invoke } };
            var first = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { declaredVoid, genericUnit, plain, functionSlot, use },
                Types = new IBirTypeDeclaration[] { owner, closureOwner } };
            var second = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { other } };
            var methodVector = first.Methods.Value; var declarationVector = first.Types.Value;
            ResolvePhysicalLocalDelegateTargets(new[] { first, second }, refs);
            Need(fromVoid.FuncType.Value == action && fromVoid.DotktDelegateSlot.Value == Named("System.Func", unit)
                && fromGeneric.FuncType.Value == unitFunction && fromGeneric.DotktDelegateSlot.Value == Named("System.Action")
                && unchangedValue.FuncType.Value == unitFunction && !unchangedValue.DotktDelegateSlot.IsPresent,
                "declared Unit/void was confused with a generic Unit value or a needless adapter slot was stamped");
            Need(nullDestination.DotktDelegateSlot.IsNull && nullDestination.FuncType.Value == action
                && exactDestination.DotktDelegateSlot.Value == Named("System.Func", text) && exactDestination.FuncType.Value == action,
                "present-null or already selected destination was overwritten");
            Need(indexed.Sig.Value.SequenceEqual(new TypeNode[] { ownerVariable, methodVariable })
                && indexed.DeclarationId.Value == "selected" && merged.Sig.Value.SequenceEqual(new TypeNode[] { text })
                && discriminated.CalleeParams.Value.Single() == FunctionSignatureIdentity.SignatureType(functionSlot.Params.Value[0]),
                "exact ID, current merged membership, open signature or function discriminator changed");
            Need(closure.FuncType.Value == BirTypeLowering.PhysicalDelegate(unit, new TypeNode[] { erased })
                && closure.CalleeParams.Value.Single() == FunctionSignatureIdentity.SignatureType(invoke.Params.Value[0])
                && closure.DotktDelegateSlot.Value == Named("System.Action", erased)
                && ReferenceEquals(closure.Captures.Value, captures) && ReferenceEquals(captures[0], captured),
                "closure construction typeArgs did not close the owner frame or captures became call arguments");
            Need(ReferenceEquals(first.Methods.Value, methodVector) && ReferenceEquals(first.Types.Value, declarationVector)
                && ReferenceEquals(use.Body.Value, body) && ReferenceEquals(use.InlineBir.Value, payload)
                && bound.FuncType.Value == unitFunction && !bound.Sig.IsPresent && ordinary.Sig.Value.Single() == text,
                "physical reconciliation changed declaration membership, opaque bytes or early-only call kinds");

            var listType = refs.PhysicalTypeNamed("System.Collections.Generic.IReadOnlyList`1")
                ?? throw new InvalidOperationException("Real IReadOnlyList metadata missing");
            var getter = listType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Single(method => method.Name == "get_Item");
            var reference = BirMemberReference.FromPhysical(MemberRefOf(refs, getter, "method", listType, new TypeNode[] { unit }));
            var referenced = new BirNewDelegate { CalleeOwner = Named("System.Collections.Generic.IReadOnlyList", unit), Method = "get_Item",
                MemberRef = reference, Sig = new TypeNode[] { integer },
                FuncType = new TypeNode.Fn(false, none, new TypeNode[] { integer }, Clr: "System.Action") };
            var arrayType = refs.PhysicalTypeNamed("System.Array") ?? throw new InvalidOperationException("Real Array metadata missing");
            var empty = arrayType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Single(method => method.Name == "Empty" && method.IsGenericMethodDefinition);
            var arrayReference = BirMemberReference.FromPhysical(MemberRefOf(refs, empty, "method", arrayType, Array.Empty<TypeNode>()));
            var referencedMethod = new BirNewDelegate { CalleeOwner = Named("System.Array"), Method = "Empty", MemberRef = arrayReference,
                Sig = Array.Empty<TypeNode>(), TypeArgs = new TypeNode[] { text },
                FuncType = new TypeNode.Fn(false, erased, Array.Empty<TypeNode>(), Clr: "System.Func") };
            var externalRoot = new BirFileDeclaration { Methods = new[] { Method("external", none) with { Body = new IBirStatement[] {
                Evaluate(referenced), Evaluate(referencedMethod) } } } };
            ResolvePhysicalLocalDelegateTargets(new[] { externalRoot }, refs);
            Need(ReferenceEquals(referenced.MemberRef.Value, reference) && reference.ReturnType.Value is TypeNode.Tv { Scope: "type", I: 0 }
                && referenced.FuncType.Value == BirTypeLowering.PhysicalDelegate(unit, new TypeNode[] { integer })
                && referenced.DotktDelegateSlot.Value == Named("System.Action", integer)
                && ReferenceEquals(referencedMethod.MemberRef.Value, arrayReference)
                && referencedMethod.FuncType.Value == BirTypeLowering.PhysicalDelegate(new TypeNode.Array(text), Array.Empty<TypeNode>()),
                "selected referenced open owner/method frames or exact MemberRef identity changed");
            var modified = new TypeNode.Mod(true, Named("Marker"), new TypeNode.Mod(false, Named("OptionalMarker"), methodVariable));
            Need(PhysicalReferencedDelegateSlot(modified, new TypeNode[] { integer }, new TypeNode[] { unit }) == unit,
                "physical reference modifiers were not removed before method-frame closing");

            var duplicate = new BirNewDelegate { CalleeOwner = Named("Ambiguous"), Method = "same", Sig = Array.Empty<TypeNode>() };
            var ambiguous = new BirFileDeclaration { FileClass = "Ambiguous", Methods = new[] { Method("same", none), Method("same", text),
                Method("use", none) with { Body = new IBirStatement[] { Evaluate(duplicate) } } } };
            try
            {
                ResolvePhysicalLocalDelegateTargets(new[] { ambiguous }, refs);
                throw new Exception("Ambiguous current declaration target was accepted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("resolves to 2 exact local methods", StringComparison.Ordinal)) { }
            Need(_nextAdapter == adaptersBefore && _nextValueAdapter == temporariesBefore,
                "slot marking materialized an adapter or allocated an evaluation temporary");
            Console.WriteLine("[physical local delegate targets] current declarations, Unit/void, exact frames, slots and reference leaves: OK");
        }
        finally { _refs = previousReferences; _localCarriers = previousCarriers; }
    }
}
