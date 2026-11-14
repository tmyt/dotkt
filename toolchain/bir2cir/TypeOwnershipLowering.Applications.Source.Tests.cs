using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypeOwnershipLowering
{
    internal static void SelfTestSourceApplications()
    {
        TypeNode T(string name) => new TypeNode.Fqn(name);
        TypeNode.Fqn Inner() => new("pkg.Outer.Inner", new[] { T("InnerArgument"), T("OuterArgument") });
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning inner application preparation: " + message);
        }
        bool PhysicalInner(TypeNode type) => type is TypeNode.Fqn { Name: "pkg.Outer.Inner", Classifier: null,
            Args: [TypeNode.Fqn { Name: "OuterArgument" }, TypeNode.Fqn { Name: "InnerArgument" }] };
        bool SourceInner(TypeNode type) => type is TypeNode.Fqn { Name: "pkg.Outer+Inner",
            Args: [TypeNode.Fqn { Name: "InnerArgument" }, TypeNode.Fqn { Name: "OuterArgument" }] };
        var outer = new BirClassDefinition { Name = "pkg.Outer", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "Outer" } } };
        var inner = new BirClassDefinition { Name = "pkg.Outer.Inner", Kind = "class", SemanticOwner = "pkg.Outer",
            Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
            TypeParams = new[] { new BirTypeParameter { Name = "Outer" }, new BirTypeParameter { Name = "Inner" } } };
        var marker = new BirClassDefinition { Name = "pkg.Outer.Marker", Kind = "class", SemanticOwner = "pkg.Outer" };
        var generated = new BirClassDefinition { Name = "Generated", Kind = "class", Generated = true, SemanticOwner = "pkg.Outer" };
        var first = new BirFieldDeclaration { Name = "first", Type = Inner() };
        var prior = new BirFieldDeclaration { Name = "prior", Type = T("physical.Erased"), KotlinType = Inner(),
            NullableGeneric = new TypeNode.Fqn("pkg.Outer.Inner", new[] { T("Wrong"), T("Other") }) };
        var generic = new BirPropertyDeclaration { Name = "generic", Type = T("physical.Erased"), KotlinType = OptionalField<TypeNode>.Null,
            NullableGeneric = Inner() };
        var parameter = new BirParameter { Name = "argument", Type = Inner() };
        var sourceClassifier = new TypeNode.Fqn("pkg.Outer.Inner", new[] { T("InnerArgument"), T("OuterArgument") }) {
            Classifier = new TypeNode.SourceClassifier("pkg", new[] { "Outer" }, "Inner"),
        };
        var explicitClassifier = new BirFieldDeclaration { Name = "explicit", Type = sourceClassifier };
        var pointer = new TypeNode.Ptr(Inner());
        var functionPointer = new TypeNode.FnPtr(0, 0, 1, Inner(), new TypeNode[] { Inner() });
        var modifier = new TypeNode.Mod(false, T("pkg.Outer.Marker"), Inner());
        var function = new TypeNode.Fn(false, new TypeNode.Nullable(Inner()), new TypeNode[] {
            new TypeNode.Oblivious(Inner()), new TypeNode.Projection("out", Inner()), new TypeNode.Array(Inner()),
            new TypeNode.ByRef(Inner()), pointer, functionPointer, modifier,
        }, Recv: Inner(), Ctx: new TypeNode[] { Inner() });
        var functionField = new BirFieldDeclaration { Name = "function", Type = function };
        var pointerField = new BirFieldDeclaration { Name = "pointer", Type = pointer };
        var functionPointerField = new BirFieldDeclaration { Name = "functionPointer", Type = functionPointer };
        var modifierField = new BirFieldDeclaration { Name = "modifier", Type = modifier };
        var unqualified = new BirFieldDeclaration { Name = "generated", Type = T("Generated") };
        var overrideOwner = Inner();
        var descriptor = new BirMemberReference { Kind = "method", Assembly = "Fixture", DeclaringType = Inner(), Name = "Run",
            GenericArity = 2, ReturnType = Inner(), ParameterTypes = new TypeNode[] { Inner() } };
        var call = new BirCallInstance { OwnerType = Inner(), Method = "Run", MemberRef = descriptor,
            TypeArgs = new[] { T("FirstMethodArgument"), T("SecondMethodArgument") }, Args = new IBirExpression[] { new BirLocal { Name = "argument" } },
            Recv = new BirLocal { Name = "argument" }, Ret = Inner() };
        var lambdaParameter = new BirLambdaParameter { Name = "lambdaArgument", Type = Inner() };
        var capture = new BirCaptureDescriptor { Name = "argument", Type = Inner() };
        var lambda = new BirNewSuspendLambda { Arity = 1, Params = new[] { lambdaParameter }, Captures = new[] { capture },
            CapValues = new IBirExpression[] { new BirLocal { Name = "argument" } }, SuspendRet = T("kotlin.Unit"),
            FuncType = new TypeNode.Fn(true, T("kotlin.Unit"), new TypeNode[] { Inner() }), Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "owner", DeclarationId = "selected-owner", Params = new[] { parameter }, Ret = Inner(),
            Overrides = new[] { new BirOverride { Owner = overrideOwner, Member = "Run", Kind = "method", Arity = 2 } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = lambda } } };
        var fields = new[] { first, prior, explicitClassifier, functionField, pointerField, functionPointerField, modifierField, unqualified };
        var file = new BirFileDeclaration { Fields = fields, Properties = new[] { generic }, Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { outer, inner, marker, generated } };
        var roots = new[] { file };
        RecordNestedSourceTypes(roots, null);
        Require(SourceInner(first.KotlinType.Value) && SourceInner(prior.KotlinType.Value) && SourceInner(generic.KotlinType.Value)
            && SourceInner(parameter.KotlinType.Value) && SourceInner(method.RetKotlinType.Value)
            && SourceInner(lambdaParameter.KotlinType.Value) && SourceInner(capture.KotlinType.Value),
            "declaration/parameter/capture/return first-writer source metadata");
        Require(explicitClassifier.KotlinType.Value is TypeNode.Fqn { Name: "pkg.Outer.Inner",
            Classifier: { PackageName: "pkg", OwnerPath: ["Outer"], Name: "Inner" } }
            && !unqualified.KotlinType.IsPresent, "explicit classifier is authoritative; semantic owner alone must not invent a lexical name");
        var capturedNames = new List<string>();
        BirGraphNameFields.VisitTypeNodes(functionField.KotlinType.Value, type => {
            if (type is TypeNode.Fqn named) capturedNames.Add(named.Name);
        });
        Require(capturedNames.Contains("pkg.Outer+Marker") && capturedNames.Count(name => name == "pkg.Outer+Inner") == 11
            && pointerField.KotlinType.Value is TypeNode.Ptr { Of: TypeNode.Fqn { Name: "pkg.Outer+Inner" } }
            && functionPointerField.KotlinType.Value is TypeNode.FnPtr { Ret: TypeNode.Fqn { Name: "pkg.Outer+Inner" } }
            && modifierField.KotlinType.Value is TypeNode.Mod { M: TypeNode.Fqn { Name: "pkg.Outer+Marker" }, Of: TypeNode.Fqn { Name: "pkg.Outer+Inner" } },
            "capture must traverse Fn, FnPtr, Ptr, Mod and source classifier structure");
        var frozen = TypeNode.ToJson(functionField.KotlinType.Value);
        sourceClassifier.Classifier.OwnerPath[0] = "ChangedAfterCapture";
        Require(explicitClassifier.KotlinType.Value is TypeNode.Fqn { Classifier.OwnerPath: ["Outer"] }, "source classifier snapshot shares mutable owner path");
        var methodParameters = method.Params.Value;
        ProjectInnerApplications(roots, null);
        Require(ReferenceEquals(file.Fields.Value, fields) && ReferenceEquals(file.Methods.Value[0], method)
            && ReferenceEquals(method.Params.Value, methodParameters) && ReferenceEquals(call.MemberRef.Value, descriptor)
            && method.DeclarationId.Value == "selected-owner" && descriptor.GenericArity.Value == 2,
            "physical projection changed declaration/member identity or selected arity");
        Require(PhysicalInner(first.Type.Value) && PhysicalInner(parameter.Type.Value) && PhysicalInner(method.Ret.Value)
            && PhysicalInner(call.OwnerType.Value) && PhysicalInner(descriptor.DeclaringType.Value)
            && PhysicalInner(descriptor.ParameterTypes.Value[0]) && PhysicalInner(descriptor.ReturnType.Value),
            "ordinary and selected descriptor applications must use enclosing-first arguments");
        Require(ReferenceEquals(method.Overrides.Value[0].Owner.Value, overrideOwner)
            && overrideOwner.Args is [TypeNode.Fqn { Name: "InnerArgument" }, TypeNode.Fqn { Name: "OuterArgument" }]
            && TypeNode.ToJson(functionField.KotlinType.Value) == frozen
            && SourceInner(first.KotlinType.Value),
            "override/source metadata must retain their recorded source frame");
        Require(generic.NullableGeneric.Value is TypeNode.Fqn { Name: "pkg.Outer.Inner", Args: [TypeNode.Fqn { Name: "InnerArgument" }, TypeNode.Fqn { Name: "OuterArgument" }] },
            "frozen generic metadata must not be projected");
        Require(functionField.Type.Value is TypeNode.Fn projected && projected.Ret is TypeNode.Nullable nullable && PhysicalInner(nullable.Of)
            && PhysicalInner(projected.Recv) && PhysicalInner(projected.Ctx[0])
            && projected.Params[0] is TypeNode.Oblivious platform && PhysicalInner(platform.Of)
            && projected.Params[1] is TypeNode.Projection projection && PhysicalInner(projection.Of)
            && projected.Params[2] is TypeNode.Array array && PhysicalInner(array.Elem)
            && projected.Params[3] is TypeNode.ByRef reference && PhysicalInner(reference.Of)
            && ReferenceEquals(projected.Params[4], pointer) && ReferenceEquals(projected.Params[5], functionPointer)
            && ReferenceEquals(projected.Params[6], modifier) && ReferenceEquals(pointerField.Type.Value, pointer)
            && ReferenceEquals(functionPointerField.Type.Value, functionPointer) && ReferenceEquals(modifierField.Type.Value, modifier),
            "projection must retain its exact partial type-container policy");
        var nestedArgument = new BirFieldDeclaration { Name = "nested", Type = new TypeNode.Fqn("pkg.Outer.Inner", new TypeNode[] { Inner(), T("OuterArgument") }) };
        var nestedFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { outer, inner }, Fields = new[] { nestedArgument } };
        ProjectInnerApplications(new[] { nestedFile }, null);
        Require(nestedArgument.Type.Value is TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "OuterArgument" }, var nested] } && PhysicalInner(nested),
            "nested argument payload must be projected exactly once");
        Console.WriteLine("[inner source applications] self-test OK (lexical classifiers, first writer, full capture/partial projection and descriptor identity)");
    }
}
