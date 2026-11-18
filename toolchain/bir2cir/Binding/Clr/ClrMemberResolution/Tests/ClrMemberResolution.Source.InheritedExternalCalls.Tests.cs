using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SourceInheritedExternalCallsSelfTest()
    {
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var obj = new TypeNode.Fqn("System.Object"); var text = new TypeNode.Fqn("System.String");
            var integer = new TypeNode.Fqn("System.Int32"); var boolean = new TypeNode.Fqn("System.Boolean");
            var voidType = new TypeNode.Fqn("void"); var ownerVariable = new TypeNode.Tv("type", 0);
            var logical = new TypeNode.Tv("method", 0); var storage = new TypeNode.Tv("method", 2);
            static void Require(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("Owning inherited external calls: " + message); }
            BirClassDefinition ListOwner(string name = "LocalList") => new() {
                Kind = "class", Name = name, TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Base = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { ownerVariable }),
                Methods = Array.Empty<BirFunctionDeclaration>(),
            };
            BirCallInstance Call(TypeNode owner, string member, TypeNode result, params TypeNode[] parameters) => new() {
                OwnerType = owner, Method = member, Sig = parameters, ArgTypes = parameters.ToArray(),
                Recv = new BirLocal { Name = "receiver" }, Ret = result, DynRet = result, Sty = result,
                Args = parameters.Select(type => (IBirExpression)new BirDefaultValue { Type = type }).ToArray(),
            };
            BirFileDeclaration File(BirCallInstance call, params IBirTypeDeclaration[] types) => new() {
                FileClass = "InheritedConsumer", Types = types, Methods = new[] {
                    new BirFunctionDeclaration { Name = "read", Static = true, Ret = call.DynRet,
                        TypeParams = new[] { new BirTypeParameter { Name = "Logical" }, new BirTypeParameter { Name = "Unused" }, new BirTypeParameter { Name = "Storage" } },
                        Params = new[] { new BirParameter { Name = "receiver", Type = call.OwnerType } },
                        Body = new IBirStatement[] { new BirReturn { Value = call } },
                        Pos = new BirDeclarationPosition { F = "Inherited.kt", L = 7 } },
                },
            };
            void Resolve(BirFileDeclaration file) => ResolveInheritedExternalCalls(null, new[] { file }, refs);

            var owner = ListOwner();
            var call = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "get_Item", text, integer);
            var signature = call.Sig.Value; var arguments = call.Args.Value; var receiver = call.Recv.Value;
            var file = File(call, owner); var method = file.Methods.Value[0]; var position = method.Pos.Value;
            Resolve(file);
            var selected = call.MemberRef.Value;
            Require(selected.Name.Value == "get_Item" && selected.ReturnType.Value == ownerVariable
                && selected.DeclaringType.Value is TypeNode.Fqn { Name: "System.Collections.Generic.List`1", Args: [var element] } && element == text
                && ReferenceEquals(call.Sig.Value, signature) && ReferenceEquals(call.Args.Value, arguments)
                && ReferenceEquals(call.Recv.Value, receiver) && call.Ret.Value == text && call.DynRet.Value == text && call.Sty.Value == text
                && !call._resolvedMethodTypeParams.IsPresent && ReferenceEquals(method.Pos.Value, position),
                "closed owner/open declaration signature or current operands/result changed");
            Resolve(file);
            Require(ReferenceEquals(call.MemberRef.Value, selected) && ReferenceEquals(method.Body.Value[0], file.Methods.Value[0].Body.Value[0]),
                "a second physical invocation rebound an already-selected call");

            // A local declaration in either open or closed owner frame outranks
            // the reflected base, even when it is inherited through another local.
            foreach (var parameter in new TypeNode[] { ownerVariable, text })
            {
                var local = ListOwner("LocalBase");
                local.Methods = new[] { new BirFunctionDeclaration { Name = "Contains", Ret = boolean,
                    Params = new[] { new BirParameter { Type = ownerVariable } }, Body = Array.Empty<IBirStatement>() } };
                var derived = new BirClassDefinition { Kind = "class", Name = "Derived",
                    Base = new TypeNode.Fqn("LocalBase", new TypeNode[] { text }) };
                var localCall = Call(new TypeNode.Fqn("Derived"), "Contains", boolean, parameter);
                Resolve(File(localCall, local, derived));
                Require(!localCall.MemberRef.IsPresent, "an open/closed local declaration lost priority to a referenced copy");
            }
            // Last-wins physical definitions, not the first frozen source owner.
            var first = ListOwner();
            first.Methods = new[] { new BirFunctionDeclaration { Name = "get_Item", Ret = text,
                Params = new[] { new BirParameter { Type = integer } }, Static = false } };
            var last = ListOwner();
            var current = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "get_Item", text, integer);
            ResolveInheritedExternalCalls(null, new[] { File(current, first), new BirFileDeclaration { Types = new IBirTypeDeclaration[] { last } } }, refs);
            Require(current.MemberRef.HasValue, "current last-wins physical method rows were replaced by early membership");
            last.Methods = first.Methods;
            var fresh = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "get_Item", text, integer);
            Resolve(File(fresh, last));
            Require(!fresh.MemberRef.IsPresent, "a fresh invocation retained a stale current-method snapshot");

            var protectedCall = Call(new TypeNode.Fqn("LocalObject"), "MemberwiseClone", obj);
            Resolve(File(protectedCall, new BirClassDefinition { Kind = "class", Name = "LocalObject", Base = obj }));
            Require(protectedCall.MemberRef.Value.Name.Value == "MemberwiseClone"
                && protectedCall.MemberRef.Value.DeclaringType.Value == obj, "protected declared-only external member was not selected");
            var absentDescriptor = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "get_Item", text, integer);
            absentDescriptor.MemberRef = OptionalField<BirMemberReference>.Null;
            Resolve(File(absentDescriptor, ListOwner()));
            Require(absentDescriptor.MemberRef.IsNull, "explicit-null descriptor was treated as missing selection");

            // Real unrelated CLR declarations remain ambiguous; do not choose
            // a read-only/mutable face by graph order.
            var ambiguous = Call(new TypeNode.Fqn("LocalViews"), "get_Item", text, integer);
            Resolve(File(ambiguous, new BirClassDefinition { Kind = "class", Name = "LocalViews", Interfaces = new TypeNode[] {
                new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { text }),
                new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList`1", new TypeNode[] { text }),
            } }));
            Require(!ambiguous.MemberRef.IsPresent, "unrelated external slots were silently resolved by graph order");
            var enumerator = Call(new TypeNode.Fqn("LocalSequence"), "GetEnumerator", obj);
            Resolve(File(enumerator, new BirInterfaceDefinition { Kind = "interface", Name = "LocalSequence", Interfaces = new TypeNode[] {
                new TypeNode.Fqn("System.Collections.Generic.IEnumerable`1", new TypeNode[] { text }),
                new TypeNode.Fqn("System.Collections.IEnumerable"),
            } }));
            Require(enumerator.MemberRef.Value.DeclaringType.Value is TypeNode.Fqn { Name: "System.Collections.Generic.IEnumerable`1" },
                "most-derived declared interface did not outrank its inherited base declaration");

            // A native delegate parameter and a generic MethodDef retain their
            // exact selected frames; this is not another source candidate lookup.
            var action = new BirNewDelegate { Method = "accept", FuncType = new TypeNode.Fn(false, voidType, new TypeNode[] { text }) };
            var forEach = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "ForEach", voidType,
                new TypeNode.Fqn("System.Action`1", new TypeNode[] { text }));
            forEach.Args = new IBirExpression[] { action };
            Resolve(File(forEach, ListOwner()));
            Require(forEach.MemberRef.HasValue && action.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Action`1", Args: [var input] } && input == text
                && ReferenceEquals(forEach.Args.Value[0], action), "selected delegate target lost its closed owner or actual operand");
            var converter = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { text }), "ConvertAll",
                new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { logical }),
                new TypeNode.Fqn("System.Converter`2", new TypeNode[] { text, logical }));
            converter.TypeArgs = new TypeNode[] { logical };
            var converterFile = File(converter, ListOwner()); Resolve(converterFile);
            Require(converter.MemberRef.Value.GenericArity.Value == 1 && converter._resolvedMethodTypeParams.Value.Length == 1
                && ReferenceEquals(((BirReturn)converterFile.Methods.Value[0].Body.Value[0]).Value.Value, converter),
                "generic declaration frame was lost or constructed return differences caused a scalar cast");

            // The different-TV result conversion moves the SAME call and its
            // registered nested declaration through the one continuing frontier.
            var donor = new BirFunctionDeclaration { Name = "donor", Params = Array.Empty<BirParameter>(), Ret = voidType,
                Body = Array.Empty<IBirStatement>() };
            var block = new BirValueBlock { Type = new TypeNode.Fqn("LocalList", new TypeNode[] { storage }),
                Stmts = new IBirStatement[] { new BirLocalFunction { Decl = donor } }, Result = new BirLocal { Name = "receiver" } };
            var changed = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { storage }), "get_Item", logical, integer);
            changed.Recv = block;
            var changedFile = File(changed, ListOwner());
            var bindings = new CurrentDeclarationBindings(new[] { changedFile }); var binding = bindings.GetSource(donor);
            var ledger = new SourceDeclarationProjection(new[] { changedFile }, bindings);
            var changedArgs = changed.Args.Value;
            Resolve(changedFile);
            var result = ((BirReturn)changedFile.Methods.Value[0].Body.Value[0]).Value.Value;
            Require(result is BirCast cast && cast.Type.Value == logical && ReferenceEquals(cast.E.Value, changed)
                && changed.Ret.Value == storage && changed.DynRet.Value == storage && changed.Sty.Value == storage
                && ReferenceEquals(changed.Recv.Value, block) && ReferenceEquals(changed.Args.Value, changedArgs)
                && ReferenceEquals(((BirLocalFunction)block.Stmts.Value[0]).Decl.Value, donor), "result conversion cloned or re-evaluated a continuing subtree");
            Resolve(changedFile);
            Require(ReferenceEquals(((BirReturn)changedFile.Methods.Value[0].Body.Value[0]).Value.Value, result), "result conversion was duplicated");
            ledger.SealSourceContinuations();
            ledger.RequireOccurrence(changed); ledger.RequireOccurrence(donor);
            Require(ReferenceEquals(binding.Source, ((BirLocalFunction)block.Stmts.Value[0]).Decl.Value)
                && ReferenceEquals(binding.Source, donor) && !ledger.IsRetired(donor)
                && ReferenceEquals(changed.Recv.Value, block),
                "registered donor lost its exact owning continuation through the physical result conversion");
            bindings.CompleteSourceCustody(ledger);
            var released = false;
            try { _ = binding.Source; } catch (InvalidOperationException) { released = true; }
            Require(released && ReferenceEquals(((BirLocalFunction)block.Stmts.Value[0]).Decl.Value, donor),
                "terminal custody retained the donor capability or replaced its actual occurrence");
            ledger.Release();
            foreach (var stamp in new[] { default(OptionalField<TypeNode>), OptionalField<TypeNode>.Null })
            {
                var unstamped = Call(new TypeNode.Fqn("LocalList", new TypeNode[] { storage }), "get_Item", logical, integer);
                unstamped.Sty = stamp;
                var unstampedFile = File(unstamped, ListOwner()); Resolve(unstampedFile);
                Require(unstamped.Sty.IsPresent == stamp.IsPresent && unstamped.Sty.IsNull == stamp.IsNull,
                    "absent/null result stamp presence was invented");
            }
        }
        Console.WriteLine("[source inherited external calls] self-test OK (current hierarchy, local priority, exact metadata, delegate frames, MOVE continuation)");
    }
}
