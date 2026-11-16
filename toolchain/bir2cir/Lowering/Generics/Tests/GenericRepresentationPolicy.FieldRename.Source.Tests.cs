using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    internal static void FieldRenameSourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Native backing rename: " + message); }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Native backing rename accepted " + message);
        }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var variable = new TypeNode.Tv("type", 0);
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var ownerType = new TypeNode.Fqn("RenameStorage", new TypeNode[] { variable });
        BirFieldGet Read(string name) => new() { OwnerType = ownerType, Name = name, Ret = variable, Recv = new BirThis() };

        (GenericRepresentationPolicy Policy, BirFileDeclaration File, BirClassDefinition Owner,
            BirFieldDeclaration Early, BirFieldDeclaration Late, BirFieldDeclaration Plain,
            IReadOnlyList<BackingFieldRename.Rename> Reports) Prepared()
        {
            var fields = new[] { "early", "late", "plain" }.Select(name => new BirFieldDeclaration {
                Name = name, Type = variable,
            }).ToArray();
            var getters = fields.Select(field => new BirFunctionDeclaration {
                Name = field.Name, PropertyName = field.Name, PropertyAccessor = "get",
                PropertyAssociation = field.Name.Value + "-property", Ret = variable,
                Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                    new BirReturn { Value = E(Read(field.Name.Value)) },
                },
            }).ToArray();
            var properties = fields.Select(field => new BirPropertyDeclaration {
                Name = field.Name, Type = variable, PropertyAssociation = field.Name.Value + "-property",
                KotlinAccessors = new[] { "get" },
            }).ToArray();
            BirFunctionDeclaration Address(string name) => new() {
                Name = "address" + name, Ret = unit, Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = E(new BirNativeStatic {
                    Type = new TypeNode.Fqn("NativeStorage"), Method = "consume", Ret = unit,
                    Sig = new TypeNode[] { new TypeNode.ByRef(variable) },
                    Args = new IBirExpression[] { new BirByRefOf { Inner = E(Read(name)) } },
                }) } },
            };
            var owner = new BirClassDefinition { Name = "RenameStorage", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = fields, Properties = properties,
                Methods = getters.Append(Address("early")).ToArray() };
            var file = new BirFileDeclaration { FileClass = "RenameUses", Types = new IBirTypeDeclaration[] { owner } };
            var roots = new[] { file };
            // Select an ordinary supported byref field argument, not a ClrField
            // annotation combined artificially with a Kotlin Property row.
            var policy = CreateSource(roots, new Dictionary<string, string>(), null);
            policy.PrepareSourceLocations(roots);
            Need(policy._sourceNativeStorage.SetEquals(new BirGraphValue[] { fields[0] })
                && !fields[1]._nativeLocation.IsPresent && !fields[2]._nativeLocation.IsPresent,
                "source native selection did not follow the actual early address use");
            owner.Methods = owner.Methods.Value.Append(Address("late")).ToArray();
            policy.RefreshPhysicalNativeLocations(roots);
            Need(policy._refreshedNativeLocations.Length == 2 && policy.IsNativeField(fields[1])
                && !policy._sourceNativeStorage.Contains(fields[1]) && !policy.IsNativeField(fields[2]),
                "physical refresh changed original eligibility or missed its later address use");
            KotlinPropertyAccessors.AllocateDeclarationsAndProperties(roots);
            Need(properties.All(property => property.GetSig.HasValue && property.GetSig.Value.Length == 0)
                && getters.All(getter => getter.Name.Value == KotlinPropertyAccessors.PhysicalName(getter.PropertyName.Value, "get")),
                "fixture did not pass through actual accessor/property allocation");
            var reports = BackingFieldRename.ApplyAll(roots);
            Need(reports.Count == 3 && reports.Select(report => report.Field).SequenceEqual(fields, ReferenceEqualityComparer.Instance)
                && reports.All(report => ReferenceEquals(report.Owner, owner)), "producer did not report the actual renamed field vector");
            return (policy, file, owner, fields[0], fields[1], fields[2], reports);
        }

        var state = Prepared();
        var policy = state.Policy;
        var originalStorage = policy._sourceNativeStorage;
        var expected = policy._pendingNativePhysicalBindings;
        var origins = policy._pendingNativeOrigins;
        var materializations = policy._nativeStorageMaterializations;
        var occurrences = policy._refreshedNativeLocations;
        var localOwners = policy._localOwners;
        var sourceEligibility = policy._existentialOwners;
        var ownersBefore = localOwners.ToArray();
        var eligibilityBefore = sourceEligibility.ToArray();
        var originalKey = origins[state.Early];
        var methods = state.Owner.Methods.Value;
        var fields = state.Owner.Fields.Value;
        policy.ApplyFieldRenames(state.Reports);
        var earlyKey = FieldKey("RenameStorage", state.Early.Name.Value, false, (TypeNode)null);
        var lateKey = FieldKey("RenameStorage", state.Late.Name.Value, false, (TypeNode)null);
        Need(ReferenceEquals(originalStorage, policy._sourceNativeStorage)
            && originalStorage.SetEquals(new BirGraphValue[] { state.Early })
            && ReferenceEquals(expected, policy._pendingNativePhysicalBindings) && expected.SetEquals(new[] { earlyKey })
            && ReferenceEquals(origins, policy._pendingNativeOrigins) && origins.Count == 1 && origins[state.Early] == earlyKey
            && !expected.Contains(originalKey) && earlyKey.Owner == originalKey.Owner && earlyKey.Static == originalKey.Static
            && earlyKey.Receiver == originalKey.Receiver && earlyKey.Name != originalKey.Name
            && ReferenceEquals(materializations, policy._nativeStorageMaterializations) && materializations.Count == 0
            && ReferenceEquals(occurrences, policy._refreshedNativeLocations)
            && occurrences.Single(row => ReferenceEquals(row.Declaration, state.Early)).Key == earlyKey
            && occurrences.Single(row => ReferenceEquals(row.Declaration, state.Late)).Key == lateKey
            && ReferenceEquals(localOwners, policy._localOwners) && localOwners.SetEquals(ownersBefore)
            && ReferenceEquals(sourceEligibility, policy._existentialOwners) && sourceEligibility.SetEquals(eligibilityBefore),
            "rename replaced a source authority, changed a key role, or expanded original eligibility");
        Need(policy.IsNativeField(Read(state.Early.Name.Value)) && policy.IsNativeField(Read(state.Late.Name.Value))
            && !policy.IsNativeField(Read(state.Plain.Name.Value)) && !state.Plain._nativeLocation.IsPresent
            && ReferenceEquals(state.Owner.Fields.Value, fields) && ReferenceEquals(state.Owner.Methods.Value, methods)
            && fields.SequenceEqual(new[] { state.Early, state.Late, state.Plain }, ReferenceEqualityComparer.Instance),
            "renamed physical uses lost native storage or an ordinary sibling acquired it");

        var roots = new[] { state.File };
        var projection = SourcePolicyCustody(state.File);
        policy.CompleteSourceContinuation(roots, null, projection);
        Need(ReferenceEquals(state.Owner.Fields.Value[0], state.Early)
            && ReferenceEquals(state.Owner.Fields.Value[1], state.Late)
            && policy.IsNativeField(state.Early) && policy.IsNativeField(state.Late) && !policy.IsNativeField(state.Plain)
            && policy._pendingNativeOrigins == null && policy._pendingNativePhysicalBindings == null
            && policy._refreshedNativeLocations == null && originalStorage.Count == 0,
            "typed completion lost exact native declaration identity, selected plain storage, or retained source staging");
        projection.Release();

        var unreported = Prepared();
        Reject(() => unreported.Policy.CompleteSourceContinuation(new[] { unreported.File }, null, SourcePolicyCustody(unreported.File)),
            "an actual native backing rename without its producer report");
        var replaced = Prepared();
        replaced.Policy.ApplyFieldRenames(replaced.Reports);
        replaced.Owner.Fields = new[] { (BirFieldDeclaration)replaced.Early.DeepClone(), replaced.Late, replaced.Plain };
        Reject(() => replaced.Policy.CompleteSourceContinuation(new[] { replaced.File }, null, SourcePolicyCustody(replaced.File)),
            "an equal replacement of the exact selected field after its reported rename");
        Console.WriteLine("[native backing rename] self-test OK (actual allocation/byref selection, same source authorities, physical-only refresh, exact reported keys/frontier and strict negative ownership)");
    }
}
