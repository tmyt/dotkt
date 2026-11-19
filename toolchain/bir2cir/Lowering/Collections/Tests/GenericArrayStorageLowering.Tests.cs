using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericArrayStorageLowering
{
    static void FreshNamesSelfTest()
    {
        static void Expect(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Array fresh names: " + message); }
        var arrayType = new TypeNode.Fqn("System.Array");
        var objectType = new TypeNode.Fqn("System.Object");
        var intType = new TypeNode.Fqn("System.Int32");
        BirLocal Read(string name, TypeNode type) => new() { Name = name, Sty = type };
        BirForArray LoopNode(string variable) => new() {
            Array = Read("array", arrayType), Var = variable, Elem = objectType, Body = Array.Empty<IBirStatement>(),
        };
        var inner = LoopNode("inner");
        var outer = LoopNode("outer");
        outer.Body = new IBirStatement[] { inner };
        var store = new BirArraySet { Array = Read("receiverEffect", arrayType),
            Index = Read("indexEffect", intType), Value = Read("valueEffect", objectType) };
        var cast = new BirCast { Type = new TypeNode.Array(new TypeNode.Star()), E = Read("value", objectType) };
        var method = new BirFunctionDeclaration { Name = "first", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { outer, new BirExpressionStatement { Expr = store }, new BirExpressionStatement { Expr = cast } } };
        var file = new BirFileDeclaration { Methods = new[] { method, new BirFunctionDeclaration {
            Name = "later", Params = new[] { new BirParameter { Name = "$arrayStorage$0", Type = arrayType } },
            // Both declarations and loop variables in a later method are reserved up front.
            Body = new IBirStatement[] { new BirVariable { Name = "$arrayIndex$0", Type = intType },
                LoopNode("$arrayLength$0"), new BirVariable { Name = "$arrayValue$0", Type = objectType },
                new BirVariable { Name = "$arrayComponent$0", Type = objectType },
                new BirVariable { Name = "$arrayCast$0", Type = objectType },
                new BirVariable { Name = "$arrayCastType$0", Type = objectType } },
        } } };
        var names = new BirTemporaryNames(file);
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var loweredInner = (BirBlock)TypedGenericArrayStorageLowering.LowerOperation(inner, refs, names);
        outer.Body = new IBirStatement[] { loweredInner };
        var loweredOuter = (BirBlock)TypedGenericArrayStorageLowering.LowerOperation(outer, refs, names);
        method.Body.Value[0] = loweredOuter;
        Expect(((BirVariable)loweredOuter.Body.Value[0]).Name.Value == "$arrayStorage$2"
            && ReferenceEquals(((BirFor)loweredOuter.Body.Value[2]).Body.Value[1], loweredInner)
            && ((BirVariable)loweredInner.Body.Value[0]).Name.Value == "$arrayStorage$1",
            "post-order loop allocations collided or changed actual body ownership");
        var loweredStore = (BirValueBlock)TypedGenericArrayStorageLowering.LowerOperation(store, refs, names);
        ((BirExpressionStatement)method.Body.Value[1]).Expr = loweredStore;
        var statements = loweredStore.Stmts.Value;
        Expect(((BirVariable)statements[0]).Name.Value == "$arrayStorage$3"
            && ((BirVariable)statements[1]).Name.Value == "$arrayIndex$3"
            && ((BirVariable)statements[2]).Name.Value == "$arrayValue$1"
            && ((BirVariable)statements[3]).Name.Value == "$arrayComponent$1", "store names collided");
        Expect(statements.Take(3).Cast<BirVariable>().Select(node => ((BirLocal)node.Init.Value).Name.Value)
            .SequenceEqual(new[] { "receiverEffect", "indexEffect", "valueEffect" }),
            "store receiver/index/value evaluation order changed");
        var loweredCast = (BirValueBlock)LowerTypeOperation(cast, cast.Type.Value, null, null, refs,
            type => type, _ => false, names, (_, _) => null);
        ((BirExpressionStatement)method.Body.Value[2]).Expr = loweredCast;
        Expect(((BirVariable)loweredCast.Stmts.Value[0]).Name.Value == "$arrayCast$1"
            && ((BirVariable)((BirIf)loweredCast.Stmts.Value[2]).Branches.Value[0].Body.Value[0]).Name.Value == "$arrayCastType$1",
            "cast names collided with later declarations");

        // Destination reservations do not rely on attachment or Parent links.
        var detached = LoopNode("$arrayStorage$4");
        names.Reserve(detached);
        var loweredDetached = (BirBlock)TypedGenericArrayStorageLowering.LowerOperation(detached, refs, names);
        Expect(((BirVariable)loweredDetached.Body.Value[0]).Name.Value == "$arrayStorage$5",
            "detached rewrite lost destination names");
        method.Body.Value[0] = new BirBlock { Body = Array.Empty<IBirStatement>() };
        Expect(names.Next("$arrayStorage$") == "$arrayStorage$6", "removed/generated reservations were reused");
        var secondFile = new BirTemporaryNames(new BirFileDeclaration());
        Expect(secondFile.Next("$arrayStorage$") == "$arrayStorage$0", "allocator leaked across files");
        var lazyFile = new BirFileDeclaration();
        var lazyNames = new BirTemporaryNames(lazyFile);
        lazyFile.Fields = new[] { new BirFieldDeclaration { Name = "$arrayStorage$0", Type = arrayType } };
        Expect(lazyNames.Next("$arrayStorage$") == "$arrayStorage$1", "file was not seeded at first allocation");
        var nextPhase = new BirTemporaryNames(file);
        Expect(nextPhase.Next("$arrayStorage$") == "$arrayStorage$1", "new phase retained removed names");
        EntryPointFreshNamesSelfTest(refs);
        TypedGenericArrayStorageLowering.SelfTest(refs);
        Console.WriteLine("[array fresh names] self-test OK (file lifetime, later declarations, detached rewrites, reservations, evaluation order)");
    }

    static void EntryPointFreshNamesSelfTest(ReferenceMetadataIndex refs)
    {
        BirFileDeclaration File(string fileClass, string lateName) => new() {
            FileClass = fileClass, Types = Array.Empty<IBirTypeDeclaration>(),
            Methods = new[] { new BirFunctionDeclaration {
                Name = "probe", Ret = new TypeNode.Fqn("void"),
                Params = new[] { new BirParameter { Name = "array", Type = new TypeNode.Fqn("System.Array") } },
                Body = new IBirStatement[] { new BirForArray {
                    Array = new BirLocal { Name = "array", Sty = new TypeNode.Fqn("System.Array") },
                    Var = "element", Elem = new TypeNode.Fqn("System.Object"), Body = Array.Empty<IBirStatement>(),
                }, new BirVariable { Name = lateName, Type = new TypeNode.Fqn("System.Int32"),
                    Init = new BirConstant { Type = new TypeNode.Fqn("System.Int32"), Value = new BirLiteral.IntegerLiteral(0) } } },
            } },
        };
        foreach (var physical in new[] { false, true })
        {
            var sources = new[] { File("FirstArrayFile", "$arrayIndex$0"), File("SecondArrayFile", "$arrayStorage$1") };
            if (physical)
                TypedPhysicalValueCoercion.ApplyAll(null, new PhysicalDelegateDeclarations(), null, sources,
                    () => throw new InvalidOperationException("Unexpected array Unit conversion"),
                    type => type.Name == "System.Int32", references: refs);
            else
            {
                var values = GenericRepresentationPolicy.CreateSource(sources, refs.Aliases, refs);
                var declarations = FBoundStarProjectionErasure.PrepareCurrentDeclarations(sources, values, out var preparation);
                var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(declarations, refs, values);
                var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, proofs, refs, values);
                var realization = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
                    FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs);
                FBoundStarProjectionErasure.RealizeCarriersSource(sources, realization,
                    Array.Empty<SharedSyntheticSynthesis.CellStorage>(), (_, _) => null, (_, _) => { });
            }
            var firstBody = ((BirBlock)sources[0].Methods.Value[0].Body.Value[0]).Body.Value;
            var secondBody = ((BirBlock)sources[1].Methods.Value[0].Body.Value[0]).Body.Value;
            if (((BirVariable)firstBody[0]).Name.Value != "$arrayStorage$0"
                || ((BirFor)firstBody[2]).Var.Value != "$arrayIndex$1"
                || ((BirVariable)secondBody[0]).Name.Value != "$arrayStorage$0")
                throw new InvalidOperationException("Array allocator entrypoint lost file lifetime or later declarations: "
                    + (physical ? "physical coercion" : "FBound"));
        }
    }
}
