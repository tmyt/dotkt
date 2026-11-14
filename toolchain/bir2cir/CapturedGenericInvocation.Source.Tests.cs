using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal static void SelfTestPhysicalInvocation()
    {
        var references = ReferenceMetadataIndex.SourceCaptureTestIndex();
        var variable = new TypeNode.Tv("method", 0);
        var parameter = new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { variable },
            ErasedInnerConstraints = new TypeNode[] { variable }, Variance = "out" };
        var cloned = CloneParameter(parameter, type => Map(type, tv => new TypeNode.Tv("type", tv.I)));
        Require(cloned.Constraints.Value[0] == new TypeNode.Tv("type", 0)
            && cloned.ErasedInnerConstraints.Value[0] == variable && cloned.Variance.Value == "out"
            && !ReferenceEquals(cloned.ErasedInnerConstraints.Value, parameter.ErasedInnerConstraints.Value),
            "selected constraints must move frames without remapping erased constraints");

        var key = new TypeNode.Fqn("Key", new TypeNode[] { variable });
        BirCallStatic Call() => new() { Owner = new TypeNode.Fqn("Local"), Method = "selected", DeclarationId = "selected-id",
            TypeArgs = new TypeNode[] { Object }, Sig = new TypeNode[] { key }, Ret = Object,
            Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("Key", new TypeNode[] { new TypeNode.Star() }),
                Value = new BirLiteral.NullLiteral() } } };
        BirFunctionDeclaration Caller() => Method("caller", [], [], Object,
            new IBirStatement[] { new BirReturn { Value = Call() } }, true, "public");
        var selected = Method("selected", new[] { new BirTypeParameter { Name = "T" } }, new TypeNode[] { key }, Object, [], true, "public");
        selected.DeclarationId = "selected-id";
        var runtime = Method("starProjectionInvokeCapturedHelper", [], RuntimeSignature, Object, [], true, "public");
        runtime.DeclarationId = "runtime-id";
        var roots = PreserveSourceFacts(new[] {
            new BirFileDeclaration { FileClass = "First", Methods = new[] { Caller() }, Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Name = "Key", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
                new BirClassDefinition { Name = "Local", Kind = "class", Methods = new[] { selected } },
                new BirClassDefinition { Name = RuntimeOwner, Kind = "class", Methods = new[] { runtime } },
            } },
            new BirFileDeclaration { FileClass = "Second", Types = Array.Empty<IBirTypeDeclaration>(), Methods = new[] { Caller() } },
        }, references);
        var dom = roots.Select(root => (JsonNode)root.ToDom()).ToArray();
        CaptureParameterWitnesses(dom);
        var pending = PrepareLocalDeclarations(dom, flattenMissingOwners: false);
        var originalRoots = pending.Roots;
        var savedBucket = pending.Declarations["selected-id"];
        foreach (var root in dom) RoundtripMetadata.PrepareMetadata(root, runtimeMode: false);
        Require(savedBucket[0].Dom[DeclarationIdentityBinding.Key] == null,
            "test must cross the real ID-consuming metadata phase");
        var physical = dom.Select(BirGraphDom.ReadBirFileDeclaration).ToArray();
        var prepared = BindPhysicalDeclarations(pending, dom, physical);
        var target = physical[0].Types.Value[1].Methods.Value[0];
        Require(ReferenceEquals(savedBucket, pending.Declarations["selected-id"])
            && ReferenceEquals(savedBucket[0].Method, target) && pending.Roots == null && savedBucket[0].Dom == null
            && originalRoots.All(root => root.DomTypes == null && root.File.Dom == null
                && root.Types.All(type => type.Dom == null && type.DomMethods == null && type.Methods == null)),
            "frontier must transition the captured dictionaries once and release their DOM anchors");
        Reject(() => BindPhysicalDeclarations(pending, dom, physical));

        // Membership is fixed, while the selected declaration value is live.
        target.Ret = String;
        var late = Method("late", [], [], Object, [], true, "public");
        late.DeclarationId = "late-id";
        physical[0].Types.Value[1].Methods = new[] { target, late };
        Require(!pending.Declarations.ContainsKey("late-id"), "later methods must not be recaptured into membership");
        var firstMethod = physical[0].Methods.Value[0];
        var firstBody = firstMethod.Body.Value;
        var firstType = physical[0].Types.Value[1];
        var emitted = new HashSet<string>(physical.SelectMany(root => root.Types.OrEmpty()).Select(type => type.Name.Value), StringComparer.Ordinal);
        prepared.Apply(physical[0], references, emitted);
        prepared.Apply(physical[1], references, emitted);
        var helper = (BirClassDefinition)physical[0].Types.Value.Last();
        var forward = (BirCallStatic)((BirCast)((BirReturn)helper.Methods.Value[0].Body.Value[0]).Value.Value).E.Value;
        var replacement = (BirCallStatic)((BirReturn)firstBody[0]).Value.Value;
        Require(ReferenceEquals(firstMethod, physical[0].Methods.Value[0]) && ReferenceEquals(firstBody, firstMethod.Body.Value)
            && ReferenceEquals(firstType, physical[0].Types.Value[1]) && ReferenceEquals(target, firstType.Methods.Value[0])
            && helper.Name.Value == "First.dotkt$captured$0" && physical[1].Types.Value.Single().Name.Value == "Second.dotkt$captured$1"
            && emitted.Contains(helper.Name.Value) && forward.Ret.Value == String && forward.TypeArgs.Value.Single() == variable
            && !forward._capturedGenericArgument.HasValue && !forward._capturedGenericTarget.HasValue
            && !forward._capturedGenericResult.HasValue && !forward._resolvedMethodTypeParams.HasValue
            && replacement.Method.Value == runtime.Name.Value && replacement.Args.Value.Length == RuntimeSignature.Length
            && !helper.RoundtripTypeHeaderPending.HasValue && !helper.Methods.Value[0].RoundtripMetadataPending.HasValue,
            "typed invocation must continue live owners, preserve module order and append fresh unmarked helpers");
        var witnessIndices = (BirNewArray)replacement.Args.Value[5];
        Require(witnessIndices.Elems.Value.Length == 1
            && ((BirLiteral.IntegerLiteral)((BirConstant)witnessIndices.Elems.Value[0]).Value.Value).Value == 0,
            "source capture witnesses must survive the metadata frontier");
        FrontierGuards();
        CapturedValueReceiverInvocation.SelfTest();

        static void FrontierGuards()
        {
            var source = new BirFileDeclaration { FileClass = "Guard", Types = Array.Empty<IBirTypeDeclaration>(),
                Methods = new[] { new BirFunctionDeclaration { Name = "same", Static = true, DeclarationId = "guard-id" } } }.ToDom();
            var pending = PrepareLocalDeclarations(new[] { source }, false);
            // An equal replacement is not the captured actual occurrence.
            source["methods"][0] = source["methods"][0].DeepClone();
            Reject(() => BindPhysicalDeclarations(pending, new[] { source }, new[] { BirGraphDom.ReadBirFileDeclaration(source) }));
            Require(pending.Roots != null && pending.Declarations["guard-id"][0].Dom != null,
                "failed handoff must not release the original membership authority");
        }
    }

    static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Typed captured invocation: " + message); }
    static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Captured declaration frontier accepted a changed or consumed occurrence");
    }
}
