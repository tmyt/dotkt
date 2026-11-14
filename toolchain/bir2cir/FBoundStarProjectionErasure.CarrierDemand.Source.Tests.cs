using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void CarrierDemandSourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning carrier demand: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        var any = new TypeNode.Fqn("System.Object");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var tv = new TypeNode.Tv("type", 0);
        TypeNode.Fqn Use(string name, TypeNode argument) => new(name, new[] { argument });
        BirTypeParameter Parameter(string variance = null) => new() { Name = "T",
            Variance = variance == null ? default : variance };
        BirClassDefinition Generic(string name, string kind = "class", string visibility = "private") => new() {
            Name = name, Kind = kind, Vis = visibility, TypeParams = new[] { Parameter() },
        };
        BirFunctionDeclaration Method(string name, params BirParameter[] parameters) => new() {
            Name = name, DeclarationId = name, Params = parameters, Ret = unit, Body = Array.Empty<IBirStatement>(),
        };

        var unused = Generic("Unused", "delegate");
        var frozen = Generic("FrozenOnly", "delegate");
        var contextOnly = Generic("ContextOnly", "delegate");
        var runtime = Generic("Runtime", "delegate");
        var bound = Generic("Bound", "delegate");
        var ancestor = Generic("Ancestor", "interface");
        ancestor.Methods = new[] { Method("sourceOverride") };
        var dependent = Generic("Dependent", "delegate");
        var exported = Generic("Exported", visibility: "public");
        exported.Interfaces = new TypeNode[] { Use("Ancestor", tv) };
        exported.Methods = new[] { Method("produce") with { Ret = Use("Dependent", tv) } };
        var inner = Generic("Inner");
        inner.SemanticOwner = "Exported";
        inner.Mods = new BirModifiers { Inner = true };
        inner.OuterTypeParamCount = 1;
        var ordinary = new BirParameter { Name = "value", Type = Use("Exported", text) };
        var accessor = new BirParameter { Name = "setterValue", Type = unit };
        var native = new BirParameter { Name = "nativeValue", Type = unit };
        var generated = new BirParameter { Name = "generatedValue", Type = unit };
        var inherited = new BirParameter { Name = "inheritedValue", Type = unit };
        var frozenParameter = new BirParameter { Name = "metadata", Type = unit, KotlinType = Use("FrozenOnly", new TypeNode.Star()) };
        var ordinaryMethod = Method("ordinary", ordinary, frozenParameter);
        var accessMethod = Method("setValue", accessor) with { PropertyName = "value", PropertyAccessor = "set" };
        var nativeMethod = Method("Dispose", native) with { Overrides = new[] {
            new BirOverride { Owner = new TypeNode.Fqn("System.IDisposable"), Member = "Dispose" } } };
        var inheritedMethod = Method("sourceOverride", inherited) with { Overrides = new[] {
            new BirOverride { Owner = Use("Ancestor", text), Member = "sourceOverride" } } };
        var generatedMethod = Method("generated", generated) with { Generated = true };
        var body = Method("use") with { TypeParams = new[] { new BirTypeParameter {
            Name = "R", Constraints = new TypeNode[] { Use("Bound", text) } } }, Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = new BirClassReference { Type = Use("Runtime", text), KotlinClassifierRef = true } },
        } };
        var root = new BirFileDeclaration { FileClass = "DemandTests", Methods = new[] { body }, Types = new IBirTypeDeclaration[] {
            unused, frozen, contextOnly, runtime, bound, ancestor, dependent, exported, inner,
            new BirClassDefinition { Name = "Exported" + CarrierMark, Kind = "class" },
            new BirClassDefinition { Name = "Owner", Kind = "class",
                Interfaces = new TypeNode[] { Use("Ancestor", text), new TypeNode.Fqn("System.IDisposable") },
                Methods = new[] { ordinaryMethod, accessMethod, nativeMethod, generatedMethod, inheritedMethod } },
        } };
        var values = GenericRepresentationPolicy.CreateSource(new[] { root }, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(new[] { root }, values, out var preparation);
        var proofs = PrepareNativeProofs(bindings, refs, values);
        var original = root.DeepClone();
        var plan = PrepareCarrierDemand(preparation, proofs, refs, values);
        MarkNeededType(new TypeNode.Fn(false, unit, Array.Empty<TypeNode>(),
            Ctx: new TypeNode[] { Use("ContextOnly", text) }), plan.Owners, false);
        Require(root.StructurallyEquals(original) && ReferenceEquals(plan.Preparation, preparation)
            && ReferenceEquals(plan.NativeProofs, proofs) && ReferenceEquals(plan.Values, values),
            "demand changed source facts or replaced its existing authorities");
        Require(plan.Owners["Exported"].ErasedName == "Exported" + CarrierMark + "$1"
            && plan.Owners["Exported"].Needed && plan.Owners["Ancestor"].Needed
            && plan.Owners["Dependent"].Needed && plan.Owners["Inner"].Needed
            && plan.Owners["Runtime"].Needed && plan.Owners["Bound"].Needed
            && !plan.Owners["Unused"].Needed && !plan.Owners["FrozenOnly"].Needed && !plan.Owners["ContextOnly"].Needed,
            "collision, visibility, dependent/ancestor/inner closure, bounds, runtime or opaque/partial-type demand changed");
        var parameters = OrdinarySourceParameters(preparation, refs);
        Require(parameters.Contains(ordinary) && parameters.Contains(inherited) && !parameters.Contains(accessor)
            && !parameters.Contains(native) && !parameters.Contains(generated), "ordinary Kotlin parameter ownership changed");

        var invariant = Generic("Invariant");
        invariant.TypeParams = new[] { Parameter("out") };
        var face = new BirInterfaceDefinition { Name = "Face", Kind = "interface", Vis = "private",
            TypeParams = new[] { Parameter("out") }, Methods = new[] { Method("get") with { Ret = tv } } };
        var capturedFace = new BirInterfaceDefinition { Name = "CapturedFace", Kind = "interface", Vis = "private",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } },
            TypeParams = new[] { Parameter("out") }, Methods = new[] {
                Method("getCapturedResult") with { Ret = new TypeNode.Tv("type", 1) } } };
        var leaf = new BirClassDefinition { Name = "Leaf", Kind = "class", Base = Use("Invariant", text) };
        var initialValue = new BirNew { Type = new TypeNode.Fqn("Leaf"), Args = Array.Empty<IBirExpression>() };
        var initializerMethod = Method("initialize") with { Ret = Use("Invariant", any), Body = new IBirStatement[] {
            new BirReturn { Value = new BirCast { Type = Use("Invariant", any), E = new BirValueBlock {
                Result = new BirConditional { Then = initialValue, Else = initialValue with { } } } } },
        } };
        var arrays = new BirFileDeclaration { FileClass = "Arrays", Methods = new[] { initializerMethod },
            Types = new IBirTypeDeclaration[] { invariant, face, capturedFace, leaf } };
        var arrayValues = GenericRepresentationPolicy.CreateSource(new[] { arrays }, refs.Aliases, refs);
        PrepareCurrentDeclarations(new[] { arrays }, arrayValues, out var arrayPreparation);
        var context = CarrierTypeContext.ForSource(arrayPreparation, refs);
        Require(context.Variance("CapturedFace", 2, 0) == null && context.Variance("CapturedFace", 2, 1) == "out"
            && HasKotlinVariantParameter(Use("Face", text), new[] { "out" })
            && !HasKotlinVariantParameter(Use("Face", text), new[] { "out", "in" }),
            "captured physical positions or exact Kotlin variance arity changed");
        Require(TryVarianceProjectedArrayOwner(Use("Invariant", any), new[] { (false, (TypeNode)new TypeNode.Fqn("Leaf")) }, context, out var selected)
            && selected.Name == "Invariant"
            && !TryVarianceProjectedArrayOwner(Use("Invariant", any), new[] { (true, (TypeNode)null) }, context, out _)
            && !TryVarianceProjectedArrayOwner(Use("Face", any), new[] { (false, (TypeNode)Use("Face", text)) }, context, out _)
            && TryVarianceProjectedArrayOwner(Use("Face", any), new[] {
                (false, (TypeNode)Use("Face", new TypeNode.Fqn("System.Int32"))) }, context, out _),
            "array join confused Kotlin class variance, CLR interface variance or all-null elements");
        Require(TrySourceSpreadOwner(new BirSpreadConcat { Elem = Use("Invariant", any), Parts = new[] {
            new BirSpreadPart { Spread = true, E = new BirNewArray { Elem = Use("Invariant", text) } } } }, context, out _)
            && TrySourceInitializerOwner(new BirNewArrayInit { Elem = Use("Invariant", any), Init = new BirNewDelegate {
                CalleeOwner = new TypeNode.Fqn("Arrays"), Method = "initialize" } }, arrayPreparation, context, out _),
            "spread or uniquely selected local initializer lost nested return demand");
        var duplicate = (BirFunctionDeclaration)initializerMethod.DeepClone();
        duplicate.DeclarationId = "other";
        arrays.Methods = new[] { initializerMethod, duplicate };
        var ambiguous = PrepareDeclarationContinuation(new CurrentDeclarationBindings(new[] { arrays }));
        Require(!TrySourceInitializerOwner(new BirNewArrayInit { Elem = Use("Invariant", any), Init = new BirNewClosure {
            ClosureType = new TypeNode.Fqn("Arrays"), Method = "initialize" } }, ambiguous,
                CarrierTypeContext.ForSource(ambiguous, refs), out _), "ambiguous local initializer was selected by a name fallback");

        // The explicit one-way projection is the production binding boundary,
        // not a second DOM demand implementation used as an oracle.
        var selectedOwner = plan.Owners["Exported"];
        var projected = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        var savedNames = _usedCarrierNames;
        try
        {
            var continued = plan.BindOwners();
            Require(ReferenceEquals(continued["Exported"], selectedOwner)
                && ReferenceEquals(selectedOwner.Def, projected["types"][7])
                && ReferenceEquals(selectedOwner.Root, projected) && selectedOwner.Needed
                && _usedCarrierNames.Contains(selectedOwner.ErasedName), "frontier recollected an owner, demand or name reservation");
            selectedOwner.Def["name"] = "CurrentPhysicalName";
            Require(Str(selectedOwner.Def["name"]) == "CurrentPhysicalName", "continuation froze its declaration address");
            var isolatedRoot = new BirFileDeclaration { FileClass = "Other", Types = new IBirTypeDeclaration[] { Generic("Exported") } };
            var isolatedValues = GenericRepresentationPolicy.CreateSource(new[] { isolatedRoot }, refs.Aliases, refs);
            var isolatedBindings = PrepareCurrentDeclarations(new[] { isolatedRoot }, isolatedValues, out var isolatedPreparation);
            var isolated = PrepareCarrierDemand(isolatedPreparation, PrepareNativeProofs(isolatedBindings, refs, isolatedValues), refs, isolatedValues);
            Require(isolated.Owners["Exported"].ErasedName == "Exported" + CarrierMark && !isolated.Owners["Exported"].Needed,
                "a preceding module leaked carrier names or visibility demand");
        }
        finally { _usedCarrierNames = savedNames; }
        Console.WriteLine("[owning carrier demand] self-test OK (typed demand, shared variance, local arrays, exact live continuation)");
    }
}
