using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal static void SelfTestPhysicalInvocation()
    {
        SourceParameterWitnessSelfTest();
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
        CaptureParameterWitnesses(roots);
        var selectedSource = roots[0].Types.Value[1].Methods.Value[0];
        var pending = PrepareLocalDeclarations(roots, flattenMissingOwners: false);
        var originalRoots = pending.Roots;
        var savedBucket = pending.Declarations["selected-id"];
        var bindings = new CurrentDeclarationBindings(roots);
        var projection = new SourceDeclarationProjection(roots, bindings);
        foreach (var root in roots) RoundtripMetadata.PrepareMetadata(root, runtimeMode: false, projection.ReportMetadataIdentityTransfer);
        Require(!selectedSource.DeclarationId.IsPresent && ReferenceEquals(savedBucket[0].Source, selectedSource)
            && selectedSource.RoundtripDeclarationIdentity.HasValue && savedBucket[0].Method == null,
            "test must cross the real ID-consuming metadata phase");
        projection.SealSourceContinuations();
        bindings.CompleteSourceCustody(projection);
        projection.Release();
        var physical = roots;
        var prepared = BindPhysicalDeclarations(pending, physical);
        var target = physical[0].Types.Value[1].Methods.Value[0];
        Require(ReferenceEquals(savedBucket, pending.Declarations["selected-id"])
            && ReferenceEquals(savedBucket[0].Method, selectedSource) && ReferenceEquals(target, selectedSource)
            && pending.Roots == null && savedBucket[0].Source == null
            && originalRoots.All(root => root.Source == null && root.SourceOccurrences == null
                && root.File.Source == null && root.File.SourceOccurrences == null
                && root.Types.All(type => type.Source == null && type.SourceOccurrences == null && type.SourceHeaders == null)),
            "same-record completion must retain the captured dictionaries once and release only staging anchors");
        Reject(() => BindPhysicalDeclarations(pending, physical));

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
        var coordinates = (BirNewArray)replacement.Args.Value[6];
        Require(coordinates.Elems.Value.Length == 5
            && ((BirLiteral.IntegerLiteral)((BirConstant)coordinates.Elems.Value[0]).Value.Value).Value == 1
            && ((BirLiteral.IntegerLiteral)((BirConstant)coordinates.Elems.Value[1]).Value.Value).Value == 0,
            "source capture witnesses must survive the metadata frontier");
        FrontierGuards();
        CapturedValueReceiverInvocation.SelfTest();

        static void FrontierGuards()
        {
            static (BirFileDeclaration Source, PendingDeclarations Pending) Start()
            {
                var source = new BirFileDeclaration { FileClass = "Guard", Types = Array.Empty<IBirTypeDeclaration>(),
                    Methods = new[] {
                        new BirFunctionDeclaration { Name = "same", Static = true, DeclarationId = "guard-id" },
                        new BirFunctionDeclaration { Name = "idless", Static = false },
                    } };
                return (source, PrepareLocalDeclarations(new[] { source }, false));
            }
            void RejectSourceChange(Action<BirFileDeclaration> change)
            {
                var state = Start(); change(state.Source);
                Reject(() => BindPhysicalDeclarations(state.Pending, new[] { state.Source }));
                Require(state.Pending.Roots[0].Source != null && state.Pending.Declarations["guard-id"][0].Source != null
                    && state.Pending.Declarations["guard-id"][0].Method == null,
                    "failed completion must not partially release or re-admit membership");
            }
            RejectSourceChange(file => file.Methods.Value[0] = (BirFunctionDeclaration)file.Methods.Value[0].DeepClone());
            RejectSourceChange(file => file.Methods.Value[1] = (BirFunctionDeclaration)file.Methods.Value[1].DeepClone());
            RejectSourceChange(file => (file.Methods.Value[0], file.Methods.Value[1]) = (file.Methods.Value[1], file.Methods.Value[0]));
            RejectSourceChange(file => file.Methods = file.Methods.Value.Append(new BirFunctionDeclaration { Name = "unreported" }).ToArray());
            RejectSourceChange(file => file.Methods = file.Methods.Value.ToArray());
            RejectSourceChange(file => file.Methods.Value[1].Name = "changed-idless");
            RejectSourceChange(file => file.Methods.Value[1].Static = true);
            RejectSourceChange(file => file.FileClass = "other");
            RejectSourceChange(file => file.Types = OptionalField<IBirTypeDeclaration[]>.Null);

            // Absence and explicit null stay distinct even for empty vectors.
            foreach (var nullVector in new[] { false, true })
            {
                var source = new BirFileDeclaration { FileClass = "Presence",
                    Methods = nullVector ? OptionalField<BirFunctionDeclaration[]>.Null : default };
                var pending = PrepareLocalDeclarations(new[] { source }, false);
                source.Methods = nullVector ? default : OptionalField<BirFunctionDeclaration[]>.Null;
                Reject(() => BindPhysicalDeclarations(pending, new[] { source }));
            }
            var wrongRoot = Start();
            Reject(() => BindPhysicalDeclarations(wrongRoot.Pending, new[] { (BirFileDeclaration)wrongRoot.Source.DeepClone() }));

            var one = new BirFunctionDeclaration { Name = "one", DeclarationId = "shared-id" };
            var two = new BirFunctionDeclaration { Name = "two", DeclarationId = "shared-id" };
            var earlier = new BirClassDefinition { Name = "Earlier", Kind = "class", Methods = new[] { one } };
            var later = new BirClassDefinition { Name = "Later", Kind = "class", Methods = new[] { two } };
            var last = new BirClassDefinition { Name = "Earlier", Kind = "class", Methods = Array.Empty<BirFunctionDeclaration>() };
            var files = new[] { new BirFileDeclaration { FileClass = "First", Types = new IBirTypeDeclaration[] { earlier, later } },
                new BirFileDeclaration { FileClass = "Last", Types = new IBirTypeDeclaration[] { last } } };
            var captured = PrepareLocalDeclarations(files, true);
            var bucket = captured.Declarations["shared-id"];
            Require(bucket.Count == 2 && ReferenceEquals(bucket[0].Source, one) && ReferenceEquals(bucket[1].Source, two)
                && ReferenceEquals(captured.Types["Earlier"], captured.Roots[1].Types[0]) && captured.FlattenMissingOwners,
                "same-ID buckets, module order, last type membership or flatten mode changed");
            Reject(() => BindPhysicalDeclarations(captured, files.Reverse().ToArray()));
            _ = BindPhysicalDeclarations(captured, files);
            Require(ReferenceEquals(bucket[0].Method, one) && ReferenceEquals(bucket[1].Method, two)
                && ReferenceEquals(captured.Types["Earlier"].Type, last), "typed completion reselected bucket or last type membership");

            foreach (var change in new Action<BirClassDefinition>[] {
                type => type.Name = "different",
                type => type.Kind = "interface",
                type => type.Methods = type.Methods.Value.ToArray(),
            })
            {
                var type = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { new BirFunctionDeclaration { Name = "method" } } };
                var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { type } };
                var pending = PrepareLocalDeclarations(new[] { file }, false);
                change(type);
                Reject(() => BindPhysicalDeclarations(pending, new[] { file }));
            }
            // The same actual declaration cannot own two physical slots.
            var shared = new BirFunctionDeclaration { Name = "shared" };
            var duplicate = new BirFileDeclaration { Methods = new[] { shared, shared } };
            Reject(() => BindPhysicalDeclarations(PrepareLocalDeclarations(new[] { duplicate }, false), new[] { duplicate }));
        }
    }

    static void SourceParameterWitnessSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var signature = new TypeNode.Fqn("Key", new TypeNode[] { variable });
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var method = new BirFunctionDeclaration { Name = "selected", DeclarationId = "capture-witness",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "key", Type = signature } }, NullableRepresentationFrame = frame };
        var member = new BirFunctionDeclaration { Name = "member", DeclarationId = "member-witness",
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("type", 0) } } };
        var sibling = new BirFunctionDeclaration { Name = "sibling", DeclarationId = "sibling-witness",
            Params = Array.Empty<BirParameter>() };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { member },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var files = new[] { new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } },
            new BirFileDeclaration { Methods = new[] { sibling } } };
        CaptureParameterWitnesses(files);
        Require(_parameterWitnesses["capture-witness"].Single() is TypeNode.Fqn captured
            && captured.Args.Single() == variable && !ReferenceEquals(captured, signature)
            && !ReferenceEquals(captured.Args, signature.Args)
            && _parameterWitnesses["member-witness"].Single() == new TypeNode.Tv("type", 0)
            && _parameterWitnesses["sibling-witness"].Length == 0
            && ReferenceEquals(_methodWitnessFrames["capture-witness"], frame)
            && ReferenceEquals(files[0].Methods.Value[0], method) && ReferenceEquals(owner.Methods.Value[0], member),
            "typed witness capture changed module membership/frame or kept a mutable source type vector");
        signature.Args[0] = String;
        method.Params.Value[0].Type = Object;
        Require(((TypeNode.Fqn)_parameterWitnesses["capture-witness"][0]).Args[0] == variable,
            "late physical parameter mutation changed the earlier witness authority");
    }

    static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Typed captured invocation: " + message); }
    static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Captured declaration frontier accepted a changed or consumed occurrence");
    }
}
