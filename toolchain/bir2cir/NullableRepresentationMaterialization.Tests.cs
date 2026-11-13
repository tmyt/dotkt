using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableRepresentationMaterialization
{
    public static void SelfTest()
    {
        TestStorageDeclarations();
        TestParameterRolesAndBounds();
        TestDescriptorFrames();
        TestMaterializedGraph();
        TestSourceMetadataAndDispatch();
        Console.WriteLine("[nullable frame materialization] self-test OK (declarations, calls, exact values, metadata)");
    }

    static TypeNode.Fqn TestType(string name, params TypeNode[] args) => new(name, args.Length == 0 ? null : args);
    static TypeNode.Tv TestTv(int index, string scope = "method") => new(scope, index);
    static TypeNode TestNullable(TypeNode type) => new TypeNode.Nullable(type);
    static BirTypeParameter TestParameter(string name, params TypeNode[] bounds) => bounds.Length == 0
        ? new() { Name = name } : new() { Name = name, Constraints = bounds };
    static BirParameter TestValue(string name, TypeNode type) => new() { Name = name, Type = type };
    static BirFunctionDeclaration TestMethod(string name, TypeNode result, params BirParameter[] parameters)
        => new() { Name = name, Params = parameters, Ret = result, Body = Array.Empty<IBirStatement>() };
    static BirClassDefinition TestClass(string name, params BirTypeParameter[] parameters)
        => new() { Kind = "class", Name = name, TypeParams = parameters };
    static BirExpressionStatement TestStatement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
    static BirFileDeclaration TestFile(string name, params IBirTypeDeclaration[] declarations)
        => new() { FileClass = name, Types = declarations };
    static GenericRepresentationPolicy TestPolicy(BirFileDeclaration root, params (string Source, string Target)[] aliases)
        => GenericRepresentationPolicy.CreateSource(new[] { root }, aliases.ToDictionary(pair => pair.Source, pair => pair.Target), null);
    static bool TestSame(TypeNode left, TypeNode right) => TypeNodeDataEquality.Equal(left, right);
    static bool TestTypes(IEnumerable<TypeNode> left, params TypeNode[] right)
        => left.Count() == right.Length && left.Zip(right).All(pair => TestSame(pair.First, pair.Second));
    static void TestRequire(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void TestStorageDeclarations()
    {
        BirFileDeclaration Storage()
        {
            var method = TestMethod("identity", TestTv(0), TestValue("value", TestTv(0)));
            method.TypeParams = new[] { TestParameter("T") };
            var body = TestClass("StorageBody"); body.Methods = new[] { method };
            var user = TestClass("StorageUser");
            user.InheritedClassMethods = new[] { new BirInheritedClassMethod {
                Member = "identity", TypeParams = new[] { TestParameter("T") },
                Params = new[] { TestValue("value", TestTv(0)) }, Ret = TestTv(0),
                InheritedImplementation = new BirInheritedImplementation {
                    Owner = TestType("StorageBody"), Member = "identity", Kind = "method", Arity = 1,
                    TypeParams = new[] { TestParameter("T") },
                },
            } };
            return TestFile("Storage", body, user);
        }
        var erased = Storage();
        var policy = TestPolicy(erased);
        KotlinGenericScalarSlots.ApplyAll(new[] { erased }, policy, null);
        Apply(new[] { erased }, _ => false, policy: policy);
        var erasedMethod = ((BirClassDefinition)erased.Types.Value[0]).Methods.Value[0];
        TestRequire(erasedMethod.TypeParams.Value.Length == 2 && TestSame(erasedMethod.Params.Value[0].Type.Value, TestType("object")),
            "Opaque value transport deleted the independent generic witness frame");
        var storage = Storage();
        Apply(new[] { storage }, _ => false, policy: TestPolicy(storage));
        var method = ((BirClassDefinition)storage.Types.Value[0]).Methods.Value[0];
        var inherited = ((BirClassDefinition)storage.Types.Value[1]).InheritedClassMethods.Value[0];
        TestRequire(method.TypeParams.Value.Length == 2 && inherited.InheritedImplementation.Value.Arity.Value == 2
            && method.Params.Value.Zip(inherited.Params.Value).All(pair => pair.First.StructurallyEquals(pair.Second))
            && TestSame(method.Ret.Value, inherited.Ret.Value),
            "Inherited implementation lost its Kotlin value-storage signature frame");

        var store = TestClass("ValueStore", TestParameter("T"));
        store.Fields = new[] { new BirFieldDeclaration { Name = "value", Vis = "private", Type = TestTv(0, "type") } };
        var native = TestClass("NativeAlias", TestParameter("T"));
        var nativeStore = TestMethod("store", TestType("ValueStore", TestTv(0, "type")), TestValue("value", TestTv(0, "type")));
        nativeStore.Body = new IBirStatement[] { new BirVariable { Name = "nativeValue", Type = TestTv(0, "type") } };
        var ownMethod = TestMethod("ownMethod", TestType("ValueStore", TestTv(0)), TestValue("value", TestTv(0)));
        ownMethod.TypeParams = new[] { TestParameter("U") };
        native.Methods = new[] { nativeStore, ownMethod };
        var nativeRoot = TestFile("NativeOwnerSlots", store, native);
        Apply(new[] { nativeRoot }, _ => false, policy: TestPolicy(nativeRoot, ("NativeAlias", "NativeGeneric")));
        TestRequire(native.TypeParams.Value.Length == 2 && TestSame(nativeStore.Params.Value[0].Type.Value, TestTv(0, "type"))
            && TestSame(((BirVariable)nativeStore.Body.Value[0]).Type.Value, TestTv(0, "type"))
            && nativeStore.Ret.Value is TypeNode.Fqn { Args: { } arguments } && TestTypes(arguments, TestTv(0, "type"), TestTv(1, "type"))
            && TestSame(ownMethod.Params.Value[0].Type.Value, TestTv(1)),
            "Kotlin body demand changed a native owner's scalar slot or lost an independent method value frame");

        var nativeCell = new BirRefTypeDeclaration { Name = "NativeCell", TypeParams = new[] { TestParameter("T") },
            Elem = TestNullable(TestTv(0, "type")), _nativeLocation = true };
        var cellRoot = TestFile("NativeCellSlots"); cellRoot.RefTypes = new[] { nativeCell };
        var cellPolicy = TestPolicy(cellRoot);
        Apply(new[] { cellRoot }, _ => false, policy: cellPolicy);
        TestRequire(cellPolicy.IsNativeField(nativeCell) && nativeCell.TypeParams.Value.Length == 2
            && TestSame(nativeCell.Elem.Value, TestType("object")),
            "Native ref-cell declaration changed its value slot into a nullable argument companion");
    }

    static void TestParameterRolesAndBounds()
    {
        var roleFrame = new NullableRepresentationFrame(1, new[] { 0 }, storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 0 });
        var bounded = new[] { TestParameter("T", TestType("Bound")) };
        var physical = ExpandParameters(bounded, roleFrame);
        TestRequire(physical.Length == 4 && TestSame(physical[0].Constraints.Value[0], bounded[0].Constraints.Value[0])
            && TestSame(physical[0].Constraints.Value[1], TestTv(1))
            && physical.Skip(1).All(parameter => !parameter.Constraints.HasValue),
            "Representation companion inherited unsupported source constraints");
        var constraintFrame = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 3, 1, 2, 0 }, storageIndices: new[] { 0 });
        var mapping = new NullableRepresentationTypes(null, constraintFrame, new Dictionary<string, NullableRepresentationFrame>(), _ => false);
        var sourceConstraints = new[] { TestParameter("T", TestTv(0), TestNullable(TestTv(0))) };
        var owned = RewriteDescriptorParameters(sourceConstraints, mapping, mapping);
        var foreign = RewriteDescriptorParameters(sourceConstraints, null, mapping);
        TestRequire(TestSame(owned[0].Constraints.Value[0], constraintFrame.Variable(TestTv(0), NullableRepresentationFrame.Role.Ordinary))
            && TestSame(owned[0].Constraints.Value[1], TestType("object"))
            && TestTypes(foreign[0].Constraints.Value, TestTv(0), TestNullable(TestTv(0))),
            "Owned direct constraints adopted storage roles or changed a foreign declaration frame");
        var valueBound = new BirInterfaceDefinition { Kind = "interface", Name = "ValueBound", TypeParams = new[] { TestParameter("T") } };
        var boundRoot = TestFile("Bounds", valueBound);
        var boundPolicy = TestPolicy(boundRoot, ("NativeBound", "NativeBound"));
        var bound = TestMethod("bound", TestType("kotlin.Unit"), TestValue("value", TestTv(0)));
        bound.TypeParams = new[] { TestParameter("R", TestType("ValueBound", TestType("kotlin.Int")), TestType("NativeBound", TestType("kotlin.Int"))) };
        var boundFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var privateBound = (BirFunctionDeclaration)bound.DeepClone(); privateBound.Params = Array.Empty<BirParameter>();
        var erasedBound = (BirFunctionDeclaration)bound.DeepClone();
        erasedBound.Params.Value[0].Type = TestType("object"); erasedBound.Params.Value[0].KotlinType = TestTv(0);
        var ownerBound = TestClass("OwnerBound", (BirTypeParameter)bound.TypeParams.Value[0].DeepClone());
        ownerBound.Base = TestType("Base", TestTv(0, "type"));
        AppendParameters(bound, boundFrame, policy: boundPolicy);
        AppendParameters(privateBound, boundFrame, policy: boundPolicy);
        AppendParameters(erasedBound, boundFrame, policy: boundPolicy);
        AppendParameters(ownerBound, boundFrame, "type", policy: boundPolicy);
        var carrier = TestType("ValueBound", new TypeNode.Star());
        TestRequire(TestSame(bound.TypeParams.Value[0].Constraints.Value[0], carrier)
            && bound.TypeParams.Value[0].Constraints.Value.Length == 2
            && bound.TypeParams.Value[0].Constraints.Value.Any(type => TestSame(type, TestType("NativeBound", TestType("kotlin.Int"))))
            && bound.TypeParams.Value[1].Constraints.Value.Length == 3
            && bound.TypeParams.Value[1].Constraints.Value.Any(type => TestSame(type, carrier))
            && !privateBound.TypeParams.Value[0].Constraints.HasValue
            && bound.TypeParams.Value.Zip(erasedBound.TypeParams.Value).All(pair => pair.First.StructurallyEquals(pair.Second)),
            "Scalar value erasure changed its declaration's classifier witnesses");
        TestRequire(ownerBound.TypeParams.Value[0].Constraints.Value.Any(type => TestSame(type, carrier))
            && TestSame(privateBound.TypeParams.Value[0].ErasedInnerConstraints.Value[0], carrier),
            "Storage bound lost its physical role, narrowed a native bound, or constrained a boxed private body");
        var scalarRoot = TestFile("Scalar", new BirInterfaceDefinition { Kind = "interface", Name = "ScalarBound" });
        var scalarPolicy = TestPolicy(scalarRoot);
        var scalar = TestMethod("scalar", TestType("kotlin.Unit"), TestValue("value", TestTv(1)));
        scalar.TypeParams = new[] { TestParameter("T", TestType("ScalarBound")) };
        var helper = (BirFunctionDeclaration)scalar.DeepClone();
        var scalarFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        AppendParameters(scalar, scalarFrame, policy: scalarPolicy);
        AppendParameters(helper, scalarFrame, policy: scalarPolicy, bodyOnlyStorage: true);
        TestRequire(TestSame(scalar.TypeParams.Value[1].Constraints.Value[0], TestType("ScalarBound"))
            && !helper.TypeParams.Value[1].Constraints.HasValue
            && TestSame(helper.TypeParams.Value[1].ErasedInnerConstraints.Value[0], TestType("ScalarBound")),
            "Non-generic classifier bound disappeared from the storage role or constrained an object-only helper");
        var reordered = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 1, 0 });
        var enclosing = new[] { TestParameter("T", TestType("ScalarBound")) };
        var ownerMethod = TestMethod("owner", TestType("kotlin.Unit"), TestValue("value", TestTv(1)));
        ownerMethod.TypeParams = new[] { TestParameter("R", TestTv(1, "type")) };
        var ownerHelper = (BirFunctionDeclaration)ownerMethod.DeepClone();
        AppendParameters(ownerMethod, scalarFrame, policy: scalarPolicy, enclosingParameters: enclosing, enclosingFrame: reordered);
        AppendParameters(ownerHelper, scalarFrame, policy: scalarPolicy, bodyOnlyStorage: true, enclosingParameters: enclosing, enclosingFrame: reordered);
        TestRequire(TestSame(ownerMethod.TypeParams.Value[1].Constraints.Value[0], TestType("ScalarBound"))
            && !ownerHelper.TypeParams.Value[1].Constraints.HasValue
            && TestSame(ownerHelper.TypeParams.Value[1].ErasedInnerConstraints.Value[0], TestType("ScalarBound")),
            "Transitive owner bound lost its declaration frame or constrained an object-only helper");
        var ownerParameters = ExpandParameters(new[] { TestParameter("T") }, reordered, "type");
        TestRequire(TestSame(ownerParameters[1].Constraints.Value[0], TestTv(0, "type")), "Nullable companion constraint lost its physical owner frame");
        var unbounded = TestMethod("unbounded", TestType("kotlin.Unit")); unbounded.TypeParams = new[] { TestParameter("T") };
        PreserveCompanionSourceBounds(unbounded, roleFrame, owner: false); AppendParameters(unbounded, roleFrame);
        TestRequire(unbounded.MethodTypeParameterBoundsPre.Value.Bounds[0].Length == 0, "Physical nullable companion became a Kotlin source bound");
        var storageOwner = TestClass("StorageOnly", bounded);
        PreserveCompanionSourceBounds(storageOwner, scalarFrame, owner: true); AppendParameters(storageOwner, scalarFrame, "type");
        TestRequire(TestTypes(storageOwner.KotlinSupertypesPre.Value.Bounds[0], TestType("Bound"))
            && !storageOwner.KotlinSupertypesPre.Value.Bounds.ContainsKey(1), "Storage-only frame failed to preserve source bounds independently of physical companions");
        var captures = new[] { (TypeNode)TestTv(0), TestTv(0, "type") };
        var captureMapping = new NullableRepresentationTypes(roleFrame, roleFrame, new Dictionary<string, NullableRepresentationFrame>(), _ => false);
        var captureFrame = CaptureFrame(captures, captureMapping);
        TestRequire(captureFrame.PhysicalArity == 8 && TestTypes(captureMapping.CloseMethod(captureFrame, captures),
            TestTv(0), TestTv(0, "type"), TestTv(1), TestTv(1, "type"), TestTv(2), TestTv(2, "type"), TestTv(3), TestTv(3, "type")),
            "Synthetic capture lost scoped representation roles or their physical order");
        var declaredMapping = new NullableRepresentationTypes(roleFrame, roleFrame, new Dictionary<string, NullableRepresentationFrame>(), _ => false, policy: scalarPolicy);
        var capturedBounds = CaptureStorageBounds(enclosing, new TypeNode[] { TestTv(0) }, roleFrame, declaredMapping);
        TestRequire(capturedBounds.Count == 1 && TestTypes(capturedBounds[2], TestType("ScalarBound")), "Captured storage companion lost its own declaration's classifier bound");
        TestRequire(CaptureStorageBounds(new[] { TestParameter("T") }, new TypeNode[] { TestTv(0) }, roleFrame, declaredMapping).Count == 0,
            "Unbounded capture acquired constraints from its caller");
    }

    static void TestDescriptorFrames()
    {
        var box = TestClass("DescriptorBox", TestParameter("T"));
        var parent = TestClass("DescriptorBase", TestParameter("T"));
        var accept = TestMethod("accept", TestType("kotlin.Unit"), TestValue("value", TestType("DescriptorBox", TestNullable(TestTv(0, "type")))));
        accept.DeclarationId = "descriptor-base-accept"; parent.Methods = new[] { accept };
        BirFileDeclaration Descriptor(bool publicSignature)
        {
            var derived = TestClass("DescriptorDerived", TestParameter("U"), TestParameter("V"));
            derived.Base = TestType("DescriptorBase", TestTv(1, "type"));
            var forward = TestMethod("forward", TestType("kotlin.Unit"), TestValue("value", TestType("DescriptorBox", TestNullable(TestTv(1, "type")))));
            var call = new BirCallInstance {
                Method = "accept", DeclarationId = "descriptor-base-accept",
                OwnerType = TestType("DescriptorDerived", TestTv(0, "type"), TestTv(1, "type")),
                Recv = new BirThis(), Args = new IBirExpression[] { new BirLocal { Name = "value" } }, Ret = TestType("kotlin.Unit"),
            };
            var signature = new TypeNode[] { TestType("DescriptorBox", TestNullable(TestTv(1, "type"))) };
            if (publicSignature) call.Sig = signature;
            else { call.MemberOwnerTypeParams = new[] { TestParameter("U"), TestParameter("V") }; call.MemberSignature = signature; }
            forward.Body = new IBirStatement[] { TestStatement(call) }; derived.Methods = new[] { forward };
            return TestFile("InheritedDescriptor", (BirClassDefinition)box.DeepClone(), (BirClassDefinition)parent.DeepClone(), derived);
        }
        foreach (var publicSignature in new[] { false, true })
        {
            var root = Descriptor(publicSignature); Apply(new[] { root }, _ => false);
            var derived = (BirClassDefinition)root.Types.Value[2];
            var call = (BirCallInstance)((BirExpressionStatement)derived.Methods.Value[0].Body.Value[0]).Expr.Value;
            var signature = publicSignature ? call.Sig.Value : call.MemberSignature.Value;
            TestRequire(signature[0] is TypeNode.Fqn { Args: { } args } && TestSame(args[0], TestTv(2, "type"))
                && (publicSignature || call.MemberOwnerTypeParams.Value.Length == 3),
                publicSignature ? "Public inherited descriptor lost its accessed-owner frame"
                    : "Inherited declaration identity replaced the explicit accessed-owner frame");
        }
        var descriptorTypes = new Dictionary<string, NullableRepresentationFrame> { ["ImportedStore"] = new(1, new[] { 0 }) };
        var mapping = new NullableRepresentationTypes(null, null, descriptorTypes, _ => false);
        var declaredType = TestType("ImportedStore", TestType("kotlin.String"));
        var owned = new BirCallStatic { Sig = new TypeNode[] { declaredType } };
        var foreign = (BirCallStatic)owned.DeepClone();
        Rewrite(owned, mapping, new Dictionary<string, NullableRepresentationFrame>(), _ => mapping);
        Rewrite(foreign, mapping, new Dictionary<string, NullableRepresentationFrame>(), _ => null);
        var ownedConstructor = new BirConstructor { DelegationSig = new TypeNode[] { declaredType } };
        var foreignConstructor = (BirConstructor)ownedConstructor.DeepClone();
        Rewrite(ownedConstructor, mapping, new Dictionary<string, NullableRepresentationFrame>(), _ => mapping);
        Rewrite(foreignConstructor, mapping, new Dictionary<string, NullableRepresentationFrame>(), _ => null);
        TestRequire(owned.Sig.Value[0] is TypeNode.Fqn { Args.Length: 2 } && foreign.Sig.Value[0] is TypeNode.Fqn { Args.Length: 1 }
            && ownedConstructor.DelegationSig.Value[0] is TypeNode.Fqn { Args.Length: 2 }
            && foreignConstructor.DelegationSig.Value[0] is TypeNode.Fqn { Args.Length: 1 },
            "Nongeneric Kotlin and foreign CLR signature frames were conflated");

        var property = new BirPropertyDeclaration { Name = "extensionValue", PropertyAssociation = "p", Type = TestType("Box", TestNullable(TestTv(0))) };
        var getter = TestMethod("extensionValue", property.Type.Value); getter.PropertyAssociation = "p"; getter.PropertyAccessor = "get";
        getter.TypeParams = new[] { TestParameter("T") };
        var awaiting = new BirCallInstance { Method = "await", OwnerType = TestType("ForeignTask", TestType("kotlin.Int")),
            ClrAwaitBridge = true, AwaitResult = TestTv(0, "type"), Args = Array.Empty<IBirExpression>() };
        var dispatch = new BirCallInline { Owner = TestType("Base"), TypeArgs = Array.Empty<TypeNode>(), Ga = 0,
            Recvs = new BirInlineReceivers { DispatchTypeArgs = new TypeNode[] { TestType("kotlin.String") },
                Dispatch = new BirLocal { Name = "receiver", Sty = TestType("Base", TestType("kotlin.String")) } } };
        var use = TestMethod("awaitUse", TestType("kotlin.Int")); use.Body = new IBirStatement[] { TestStatement(awaiting), TestStatement(dispatch) };
        var baseType = TestClass("Base", TestParameter("T"));
        baseType.Fields = new[] { new BirFieldDeclaration { Name = "value", Type = TestType("Box", TestNullable(TestTv(0, "type"))) } };
        var child = TestClass("Child"); child.Base = TestType("Base", TestType("kotlin.String"));
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), BaseArgs = Array.Empty<IBirExpression>(),
            DelegationSig = new TypeNode[] { TestTv(0, "type") }, Body = Array.Empty<IBirStatement>() }; child.Ctors = new[] { constructor };
        var ownerRoot = TestFile("DescriptorOwners", baseType, child); ownerRoot.Properties = new[] { property }; ownerRoot.Methods = new[] { getter, use };
        Apply(new[] { ownerRoot }, _ => false);
        TestRequire(property.Type.Value is TypeNode.Fqn { Args: { } propertyArgs } && TestSame(propertyArgs[0], TestTv(1))
            && TestSame(constructor.DelegationSig.Value[0], TestTv(0, "type")) && TestSame(awaiting.AwaitResult.Value, TestTv(0, "type")),
            "Property, constructor, or await descriptors lost their declaration frames");
        TestRequire(dispatch.Recvs.Value.DispatchTypeArgs.Value.Length == 2
            && TestSame(dispatch.Recvs.Value.DispatchTypeArgs.Value[1], TestNullable(TestType("kotlin.String")))
            && ((BirLocal)dispatch.Recvs.Value.Dispatch.Value).Sty.Value is TypeNode.Fqn { Args: { } receiverArgs }
            && TestTypes(receiverArgs, dispatch.Recvs.Value.DispatchTypeArgs.Value),
            "Inline dispatch application lost its owner's nullable companion");
    }

    static void TestMaterializedGraph()
    {
        var box = TestClass("Box", TestParameter("T"));
        box.Fields = new[] { new BirFieldDeclaration { Name = "value", Vis = "private", Type = TestTv(0, "type") } };
        var store = TestClass("Store", TestParameter("T"));
        store.Fields = new[] { new BirFieldDeclaration { Name = "box", Type = TestType("Box", TestNullable(TestTv(0, "type"))) } };
        var derived = TestClass("Derived"); derived.Base = TestType("Store", TestType("kotlin.String"));
        var sourceResult = TestType("Box", TestNullable(TestTv(0)));
        var pass = TestMethod("pass", sourceResult, TestValue("x", sourceResult));
        pass.DeclarationId = "pass"; pass.TypeParams = new[] { TestParameter("T") };
        var sam = new BirNewSam { TypeArgs = new TypeNode[] { TestTv(0) }, SynthClass = new BirClassDefinition {
            Kind = "class", Name = "CapturedSam", TypeParams = new[] { TestParameter("T") },
            Interfaces = new TypeNode[] { TestType("Comparator", TestNullable(TestTv(0))) },
        } };
        var inline = new BirCallInline { DeclarationId = "pass", Ga = 1, TypeArgs = new TypeNode[] { TestTv(0) } };
        pass.Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "x" } }, TestStatement(sam), TestStatement(inline) };
        var call = new BirCallStatic { DeclarationId = "pass", TypeArgs = new TypeNode[] { TestType("kotlin.String") },
            MemberMethodTypeParams = new[] { TestParameter("T") }, OwnerType = TestType("FrameTest"),
            MemberSignature = new TypeNode[] { sourceResult }, MemberReturnType = sourceResult,
            ArgTypes = new TypeNode[] { sourceResult }, Ret = sourceResult };
        var owner = TestClass("ConstructorOwner", TestParameter("T"));
        owner.Ctors = new[] { new BirConstructor { Params = new[] { TestValue("value", TestType("Box", TestNullable(TestTv(0, "type")))) }, Body = Array.Empty<IBirStatement>() } };
        var construction = new BirNew { Type = TestType("ConstructorOwner", TestType("kotlin.String")),
            ArgTypes = new TypeNode[] { TestType("Box", TestNullable(TestType("kotlin.String"))) },
            MemberSignature = new TypeNode[] { TestType("Box", TestNullable(TestTv(0, "type"))) }, Args = Array.Empty<IBirExpression>() };
        var caller = TestMethod("caller", TestType("kotlin.Unit")); caller.DeclarationId = "caller";
        caller.Body = new IBirStatement[] { TestStatement(call), TestStatement(construction) };
        var nested = TestClass("NestedStore", TestParameter("Own"), TestParameter("T"));
        nested.SemanticOwner = "Store"; nested.OuterTypeParamOffset = 1; nested.OuterTypeParamCount = 1;
        nested.Fields = new[] { new BirFieldDeclaration { Name = "own", Vis = "private", Type = TestTv(0, "type") },
            new BirFieldDeclaration { Name = "captured", Type = TestType("Box", TestNullable(TestTv(1, "type"))) } };
        var defaultMember = (BirFunctionDeclaration)pass.DeepClone(); defaultMember.DeclarationId = "defaultPass";
        // The default-interface fixture tests declaration frames, not a second copy of pass's synthetic body.
        defaultMember.Body = Array.Empty<IBirStatement>();
        var defaultOwner = new BirInterfaceDefinition { Kind = "interface", Name = "DefaultSource", Methods = new[] { defaultMember } };
        var inherited = new BirInheritedDefaultMethod { Member = "pass", Params = new TypeNode[] { sourceResult }, Ret = sourceResult,
            Implementation = new BirInheritedImplementation { Owner = TestType("DefaultSource"), Member = "pass", Kind = "method", Arity = 1,
                TypeParams = new[] { TestParameter("T") } } };
        var user = TestClass("DefaultUser"); user.InheritedDefaultMethods = new[] { inherited };
        var cell = new BirRefTypeDeclaration { Name = "Cell", TypeParams = new[] { TestParameter("S") }, Elem = TestType("Box", TestNullable(TestTv(0, "type"))) };
        var boundedCell = new BirRefTypeDeclaration { Name = "BoundedCell", TypeParams = new[] { TestParameter("T"),
            TestParameter("U", TestType("Box", TestNullable(TestTv(0, "type")))) }, Elem = TestTv(1, "type") };
        var root = TestFile("FrameTest", box, store, derived, owner, nested, defaultOwner, user);
        root.RefTypes = new[] { cell, boundedCell };
        var methods = new List<BirFunctionDeclaration> { pass, caller };
        var closedCalls = new List<BirCallStatic>();
        var captures = new List<BirInlineCapture>();
        foreach (var order in new[] { false, true })
        {
            var result = TestType("Box", TestNullable(TestTv(1)));
            var closed = new BirCallStatic { OwnerType = TestType("FrameTest"), DeclarationId = "pass", TypeArgs = new TypeNode[] { TestTv(1) } };
            if (order) { closed.Sty = result; closed.Ret = result; } else { closed.Ret = result; closed.Sty = result; }
            var closedCaller = TestMethod("closedCaller" + order, result); closedCaller.TypeParams = new[] { TestParameter("A"), TestParameter("B") };
            closedCaller.Body = new IBirStatement[] { TestStatement(closed) }; methods.Add(closedCaller); closedCalls.Add(closed);
            var capture = new BirInlineCapture { Name = "captured", Type = TestTv(0) };
            if (order) capture.SharedCellTypeParams = boundedCell.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            capture.SharedCellType = TestType("BoundedCell", TestTv(1), TestTv(0));
            if (!order) capture.SharedCellTypeParams = boundedCell.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            var lambda = new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Captures = new[] { capture }, Body = Array.Empty<IBirStatement>() };
            var captureMethod = TestMethod("captureCell" + order, TestType("kotlin.Unit")); captureMethod.TypeParams = new[] { TestParameter("A"), TestParameter("B") };
            captureMethod.Body = new IBirStatement[] { TestStatement(lambda) }; methods.Add(captureMethod); captures.Add(capture);
        }
        root.Methods = methods.ToArray();
        Apply(new[] { root }, _ => false);
        foreach (var capture in captures)
            TestRequire(capture.SharedCellTypeParams.Value.Length == 3
                && capture.SharedCellTypeParams.Value.Zip(boundedCell.TypeParams.Value).All(pair => pair.First.StructurallyEquals(pair.Second))
                && capture.SharedCellType.Value is TypeNode.Fqn { Args: { } captureArgs } && TestTypes(captureArgs, TestTv(1), TestTv(0), TestTv(2))
                && TestSame(capture.SharedCellTypeParams.Value[1].Constraints.Value[0], TestType("Box", TestTv(2, "type"))),
                "Shared capture descriptor diverges from its cell's expanded declaration frame");
        TestRequire(inline.Ga.Value == 2 && TestTypes(inline.TypeArgs.Value, TestTv(0), TestTv(1)), "Inline substitution arity excludes the materialized companion frame");
        TestRequire(sam.TypeArgs.Value.Length == 2 && sam.SynthClass.Value.TypeParams.Value.Length == 2
            && TestSame(sam.TypeArgs.Value[1], TestTv(1))
            && TestSame(sam.SynthClass.Value.Interfaces.Value[0], TestType("Comparator", TestTv(1))),
            "Synthetic payload companion is absent from its capture correspondence");
        var samFrame = sam.SynthClass.Value.KotlinSupertypesPre.Value.NullableFrame;
        TestRequire(samFrame is { SourceArity: 1 } && TestSame(samFrame.NullableVariable(TestTv(0, "type")), TestTv(1, "type")),
            "Synthetic owner lost its declaration-owned nullable frame");
        TestRequire(inherited.Implementation.Value.Arity.Value == 2 && inherited.Implementation.Value.TypeParams.Value.Length == 2
            && TestSame(inherited.Ret.Value, defaultMember.Ret.Value) && cell.TypeParams.Value.Length == 2
            && TestSame(cell.Elem.Value, TestType("Box", TestTv(1, "type"))), "Inherited methods or reference cells lost their declaration-owned frames");
        TestRequire(nested.OuterTypeParamCount.Value == 2 && !nested.OuterTypeParamOffset.IsPresent
            && TestSame(nested.Fields.Value[0].Type.Value, TestTv(2, "type"))
            && TestSame(nested.Fields.Value[1].Type.Value, TestType("Box", TestTv(1, "type"))) && nested.TypeParams.Value.Length == 3,
            "Nested frame lost enclosing companions or child source positions");
        TypeOwnershipLowering.PrepareOwnershipFacts(new[] { root });
        var projected = root.ToDom();
        var afterOwnership = BirGraphDom.ReadBirFileDeclaration(projected);
        foreach (var closed in afterOwnership.Methods.Value.Where(method => method.Name.Value.StartsWith("closedCaller", StringComparison.Ordinal))
            .Select(method => (BirCallStatic)((BirExpressionStatement)method.Body.Value[0]).Expr.Value))
            TestRequire(TestSame(closed.Ret.Value, TestType("Box", TestTv(2))) && TestSame(closed.Ret.Value, closed.Sty.Value),
                "Caller result ownership depends on callee frame or property order");
        TestRequire(pass.TypeParams.Value.Length == 2 && TestSame(pass.Ret.Value, TestType("Box", TestTv(1)))
            && call.TypeArgs.Value.Length == 2 && box.TypeParams.Value.Length == 1 && store.TypeParams.Value.Length == 2
            && derived.Base.Value is TypeNode.Fqn { Args.Length: 2 } && construction.Type.Value is TypeNode.Fqn { Args.Length: 2 }
            && TestSame(construction.MemberSignature.Value[0], TestType("Box", TestTv(1, "type")))
            && call.MemberMethodTypeParams.Value.Length == 2 && TestSame(call.MemberSignature.Value[0], pass.Ret.Value)
            && TestSame(call.MemberReturnType.Value, pass.Ret.Value) && TestSame(call.ArgTypes.Value[0], pass.Ret.Value)
            && TestSame(call.Ret.Value, pass.Ret.Value) && pass.NullableRepresentationFrame.HasValue && store.KotlinSupertypesPre.HasValue && pass.NullableGenericRet.HasValue,
            "Nullable frame materialization self-test failed");
    }

    static void TestSourceMetadataAndDispatch()
    {
        var dispatchFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var specialized = TestClass("SpecializedSam", TestParameter("P"), TestParameter("T"));
        specialized.KotlinSupertypesPre = SourceTypeFacts.Empty.Merge(null,
            new TypeNode[] { TestType("SourceContract", TestTv(0, "type")) }, null).MergeNullableFrame(dispatchFrame);
        var specializedNode = new BirNewSam { SynthClass = specialized, TypeArgs = new TypeNode[] { TestType("object"), TestType("kotlin.String") } };
        InlineExpansion.PruneConcreteSynthClasses(specializedNode);
        var pruned = specializedNode;
        TestRequire(!pruned.SynthClass.Value.TypeParams.IsPresent && !pruned.TypeArgs.IsPresent
            && pruned.SynthClass.Value.KotlinSupertypesPre.Value.NullableFrame == null
            && TestSame(pruned.SynthClass.Value.KotlinSupertypesPre.Value.Interfaces[0], TestType("SourceContract", TestType("kotlin.String"))),
            "Closed synthetic specialization retained a frame or substituted a storage role into source facts");
        var source = TestMethod("run", TestType("kotlin.Unit")); source.DeclarationId = "storage-dispatch";
        source.TypeParams = new[] { TestParameter("T") }; source.Virtual = true;
        var entry = (BirFunctionDeclaration)source.DeepClone(); AppendParameters(entry, dispatchFrame);
        var dispatchOwner = TestClass("RoleDispatcher"); dispatchOwner.Methods = new[] { entry };
        var demand = new NullableRepresentationDemand.Variables(); demand.Method.Add(0);
        NullableBodyDispatch.Build(new NullableRepresentationViews.Owner(dispatchOwner), source, entry,
            new NullableRepresentationFrame(0, Array.Empty<int>()), dispatchFrame, demand,
            (method, frame) => AppendParameters(method, frame));
        var conditional = (BirConditional)((BirReturn)entry.Body.Value[0]).Value.Value;
        var then = (BirCallInstance)conditional.Then.Value;
        var otherwise = (BirCallInstance)conditional.Else.Value;
        var condition = (BirPropertyGet)conditional.Cond.Value;
        TestRequire(TestTypes(then.TypeArgs.Value, TestTv(1), TestType("object"), TestTv(0))
            && TestTypes(otherwise.TypeArgs.Value, TestTv(1), TestTv(1), TestTv(0))
            && TestSame(((BirClassReference)condition.Recv.Value).Type.Value, TestTv(1)) && entry.TypeParams.Value.Length == 2,
            "Nullable body dispatch lost an existing storage role or changed the entry frame");

        var carrierBox = TestClass("CarrierBox", TestParameter("T"));
        var carrierStore = TestClass("CarrierStore", TestParameter("T"));
        carrierStore.Fields = new[] { new BirFieldDeclaration { Name = "value", Type = TestType("CarrierBox", TestNullable(TestTv(0, "type"))) } };
        var sourceCarrier = TestType("CarrierStore", TestType("kotlin.Int"));
        var carrierMethod = TestMethod("use", sourceCarrier, TestValue("value", sourceCarrier));
        var carrierRoot = TestFile("CarrierProbe", carrierBox, carrierStore); carrierRoot.Methods = new[] { carrierMethod };
        Apply(new[] { carrierRoot }, type => type.Name == "kotlin.Int");
        var carrierDom = carrierRoot.ToDom(); NullableGenericErasure.Apply(carrierDom, type => type.Name == "kotlin.Int");
        var carrier = BirGraphDom.ReadBirFileDeclaration(carrierDom).Methods.Value[0];
        TestRequire(TestSame(carrier.NullableGenericRet.Value, sourceCarrier) && TestSame(carrier.Params.Value[0].NullableGeneric.Value, sourceCarrier),
            "A physical companion argument replaced a Kotlin source slot carrier");
        var bound = TestClass("Bound", TestParameter("A"), TestParameter("B"));
        var sourceBound = TestType("Bound", TestNullable(TestTv(0)), TestNullable(TestType("kotlin.Int")));
        var method = TestMethod("keep", TestTv(1), TestValue("x", TestTv(1)));
        method.DeclarationId = "bound-probe"; method.TypeParams = new[] { TestParameter("T"), TestParameter("U", sourceBound) };
        var boundRoot = TestFile("BoundProbe", bound); boundRoot.Methods = new[] { method };
        Apply(new[] { boundRoot }, type => type.Name == "kotlin.Int");
        TestRequire(method.TypeParams.Value.Length == 3, "Method-bound probe did not materialize its nullable companion");
        var boundDom = boundRoot.ToDom(); NullableGenericErasure.Apply(boundDom, type => type.Name == "kotlin.Int");
        var boundResult = BirGraphDom.ReadBirFileDeclaration(boundDom).Methods.Value[0];
        TestRequire(TestTypes(boundResult.MethodTypeParameterBoundsPre.Value.Bounds[1], sourceBound),
            "A physical companion index replaced a Kotlin method-bound carrier");
        var cell = TestClass("Cell", TestParameter("T"));
        var nodeBound = TestType("Node", TestTv(0, "type"));
        var node = TestClass("Node", TestParameter("N", nodeBound));
        node.Fields = new[] { new BirFieldDeclaration { Name = "previous", Type = TestType("Cell", TestNullable(TestTv(0, "type"))) } };
        var classRoot = TestFile("ClassBounds", cell, node);
        Apply(new[] { classRoot }, _ => false);
        var classDom = classRoot.ToDom(); NullableGenericErasure.Apply(classDom, _ => false);
        var classResult = (BirClassDefinition)BirGraphDom.ReadBirFileDeclaration(classDom).Types.Value[1];
        TestRequire(classResult.TypeParams.Value.Length == 2 && TestTypes(classResult.KotlinSupertypesPre.Value.Bounds[0], nodeBound),
            "Physical companion arguments replaced a Kotlin class-bound carrier");
        var bodyEntry = TestMethod("entry", TestType("kotlin.Unit"));
        bodyEntry.Static = false; bodyEntry.Virtual = true; bodyEntry.TypeParams = new[] { TestParameter("T") };
        bodyEntry.Body = new IBirStatement[] { TestStatement(new BirNewArraySized { Elem = TestNullable(TestTv(0)) }) };
        var fixedOwner = TestClass("FixedEntry"); fixedOwner.Methods = new[] { bodyEntry };
        Apply(new[] { TestFile("BodyOnly", fixedOwner) }, _ => false);
        TestRequire(fixedOwner.Methods.Value.Length == 2 && fixedOwner.Methods.Value[0].TypeParams.Value.Length == 1
            && fixedOwner.Methods.Value[1].TypeParams.Value.Length == 2
            && ((BirReturn)fixedOwner.Methods.Value[0].Body.Value[0]).Value.Value is BirConditional,
            "Body-only specialization changed the fixed entry frame");
    }
}
