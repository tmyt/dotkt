using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed class SourcePipelineTransitions : SourceDeclarationTransitions
    {
        sealed record MethodVector(BirFileDeclaration File, IBirTypeDeclaration Owner, BirFunctionDeclaration[] Continuing);
        readonly CurrentDeclarationBindings bindings;
        readonly FBoundStarProjectionErasure.NativeProofPlan proofs;
        readonly GenericRepresentationPolicy generic;
        readonly AliasDeclarationTransitions aliases;
        readonly Action<BirFunctionDeclaration> retireDeclaration;
        readonly Dictionary<BirGraphValue, MethodVector> methodVectors = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<BirFunctionDeclaration, MethodVector> originalOwners = new(ReferenceEqualityComparer.Instance);
        internal SourceDeclarationProjection Projection { get; }

        internal SourcePipelineTransitions(IReadOnlyList<BirFileDeclaration> roots,
            CurrentDeclarationBindings bindings, FBoundStarProjectionErasure.NativeProofPlan proofs,
            GenericRepresentationPolicy generic, AliasDeclarationTransitions aliases,
            Action<BirFunctionDeclaration> retireDeclaration,
            IEnumerable<BirFunctionDeclaration> earlierRetiredDeclarations)
        {
            this.bindings = bindings; this.proofs = proofs; this.generic = generic; this.aliases = aliases;
            this.retireDeclaration = retireDeclaration ?? throw new ArgumentNullException(nameof(retireDeclaration));
            Projection = new(roots, bindings);
            foreach (var file in roots)
            {
                Record(file, null, file.Methods.OrEmpty());
                foreach (var owner in file.Types.OrEmpty()) Record(file, owner, owner.Methods.OrEmpty());
            }
            foreach (var declaration in earlierRetiredDeclarations) Projection.RetireForest(declaration);
            Projection.RegisterMethodVectorValidation(() => {
                foreach (var row in methodVectors.Values)
                    if (!CurrentMethods(row.File, row.Owner).SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance))
                        throw Mismatch();
            });
            // Alias lowering already selected these exact fields. They have one
            // projection alongside any later retired containing forest.
            if (aliases != null)
                foreach (var (_, field) in aliases.RetiredFields) Projection.RetireForest(field);

            void Record(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration[] methods)
            {
                var row = new MethodVector(file, owner, methods.ToArray());
                if (!methodVectors.TryAdd((BirGraphValue)owner ?? file, row)) throw Mismatch();
                foreach (var method in methods)
                    if (!originalOwners.TryAdd(method, row)) throw Mismatch();
            }
        }

        internal override BirGraphValue CloneIndependent(BirGraphValue source) => source.DeepClone((origin, fresh) => {
            if (!bindings.TryGetCapturedSource(origin, out _)) return;
            bindings.GetSource(fresh);
            proofs.TransferExactSourceProofs(origin, fresh);
        });

        internal override void RetireDiscardedSubtree(BirGraphValue source)
        {
            Projection.RetireForest(source);
            NotifyMethods(source);
        }

        internal override void RetireMethod(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration method)
        {
            var key = (BirGraphValue)owner ?? file;
            if (!methodVectors.TryGetValue(key, out var row) || !ReferenceEquals(row.File, file)
                || !CurrentMethods(file, owner).SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)
                || row.Continuing.Count(actual => ReferenceEquals(actual, method)) != 1) throw Mismatch();
            methodVectors[key] = row with { Continuing = row.Continuing.Where(actual => !ReferenceEquals(actual, method)).ToArray() };
            RetireDiscardedSubtree(method);
        }

        internal override void ProjectMethod(BirFunctionDeclaration origin, BirFunctionDeclaration projection)
        {
            if (!originalOwners.TryGetValue(origin, out var source)
                || CurrentMethods(source.File, source.Owner).Count(actual => ReferenceEquals(actual, projection)) != 1)
                throw Mismatch();
            bindings.GetSource(projection);
            proofs.TransferExactSourceProofs(origin, projection);
        }

        internal override void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner,
            IReadOnlyList<BirFunctionDeclaration> appended)
        {
            var key = (BirGraphValue)owner ?? file;
            if (!methodVectors.TryGetValue(key, out var row) || !ReferenceEquals(row.File, file)) throw Mismatch();
            var next = row.Continuing.Concat(appended).ToArray();
            if (next.Distinct(ReferenceEqualityComparer.Instance).Count() != next.Length
                || !CurrentMethods(file, owner).SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
            methodVectors[key] = row with { Continuing = next };
            foreach (var method in appended) Admit(method);
            Projection.PublishMethods(file, owner, appended);
        }

        internal override void PublishTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
        {
            Projection.AppendTypes(file, appended);
            aliases?.AdmitMaterializedTypes(file, appended);
            generic.AdmitMaterializedNativeTypes(file, appended);
            foreach (var type in appended)
            {
                Admit((BirGraphValue)type);
                var methods = type.Methods.OrEmpty();
                var row = new MethodVector(file, type, methods.ToArray());
                if (!methodVectors.TryAdd((BirGraphValue)type, row)) throw Mismatch();
                Projection.PublishMethods(file, type, methods);
            }
        }

        internal void MoveMethods(BirFileDeclaration file, IReadOnlyList<TypeOwnershipLowering.SourceMethodMove> moves)
        {
            if (moves.Count == 0 || !methodVectors.TryGetValue(file, out var source)
                || source.Owner != null || !ReferenceEquals(source.File, file)) throw Mismatch();
            var removed = new HashSet<BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
            var ordinal = source.Continuing.Length;
            foreach (var move in moves)
            {
                if (move.SourceOrdinal < 0 || move.SourceOrdinal >= ordinal
                    || !ReferenceEquals(source.Continuing[move.SourceOrdinal], move.Method)
                    || !removed.Add(move.Method)) throw Mismatch();
                bindings.GetCapturedSource(move.Method);
                ordinal = move.SourceOrdinal;
            }
            var remaining = source.Continuing.Where(method => !removed.Contains(method)).ToArray();
            if (!CurrentMethods(file, null).SequenceEqual(remaining, ReferenceEqualityComparer.Instance)) throw Mismatch();
            var destinations = new List<(BirGraphValue Key, MethodVector Before, BirFunctionDeclaration[] After)>();
            foreach (var group in moves.GroupBy<TypeOwnershipLowering.SourceMethodMove, IBirTypeDeclaration>(
                move => move.Destination, ReferenceEqualityComparer.Instance))
            {
                var owner = group.Key;
                if (owner == null || !methodVectors.TryGetValue((BirGraphValue)owner, out var target)
                    || !ReferenceEquals(target.Owner, owner)
                    || target.File.Types.OrEmpty().Count(actual => ReferenceEquals(actual, owner)) != 1) throw Mismatch();
                bindings.GetCapturedSource((BirGraphValue)owner);
                var appended = group.ToArray();
                for (var index = 0; index < appended.Length; index++)
                    if (appended[index].DestinationOrdinal != target.Continuing.Length + index) throw Mismatch();
                var next = target.Continuing.Concat(appended.Select(move => move.Method)).ToArray();
                if (!CurrentMethods(target.File, owner).SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
                destinations.Add(((BirGraphValue)owner, target, next));
            }
            Projection.MoveMethods(file, source.Continuing, remaining, moves);
            methodVectors[file] = source with { Continuing = remaining };
            foreach (var (key, before, after) in destinations) methodVectors[key] = before with { Continuing = after };
        }

        internal override void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
        {
            Projection.RetireTypes(file, removed);
            aliases?.RetireMaterializedTypes(file, removed);
            generic.RetireMaterializedNativeTypes(file, removed);
            foreach (var type in removed) NotifyMethods((BirGraphValue)type);
        }

        internal void ReportPhysicalIdentityConsumption(BirFileDeclaration file, IBirTypeDeclaration owner,
            BirFunctionDeclaration method, string identity)
        {
            var key = (BirGraphValue)owner ?? file;
            if (!methodVectors.TryGetValue(key, out var row) || !ReferenceEquals(row.File, file)
                || !ReferenceEquals(row.Owner, owner)
                || !CurrentMethods(file, owner).SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance))
                throw Mismatch();
            Projection.ReportPhysicalIdentityConsumption(file, owner, method, identity);
        }

        internal void ReportMetadataIdentityTransfer(BirFileDeclaration file, IBirTypeDeclaration owner,
            BirFunctionDeclaration method, BirRoundtripDeclarationIdentity identity)
        {
            var key = (BirGraphValue)owner ?? file;
            if (!methodVectors.TryGetValue(key, out var row) || !ReferenceEquals(row.File, file)
                || !ReferenceEquals(row.Owner, owner)
                || !CurrentMethods(file, owner).SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance))
                throw Mismatch();
            Projection.ReportMetadataIdentityTransfer(file, owner, method, identity);
        }

        void NotifyMethods(BirGraphValue value)
        {
            if (value is IBirExecutablePayload) return;
            if (value is BirFunctionDeclaration method) retireDeclaration(method);
            value.VisitChildren(NotifyMethods);
        }

        void Admit(BirGraphValue value)
        {
            if (value is IBirExecutablePayload) return;
            if (value is IBirTypeDeclaration or BirFunctionDeclaration or BirConstructor or BirMethodImpl
                or BirNewSuspendLambda or BirInlineLambda or BirClosureDefinition) bindings.GetSource(value);
            value.VisitChildren(Admit);
        }

        internal void Release()
        {
            Projection.Release();
            methodVectors.Clear(); originalOwners.Clear();
        }

        static BirFunctionDeclaration[] CurrentMethods(BirFileDeclaration file, IBirTypeDeclaration owner) =>
            owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty();
        static InvalidOperationException Mismatch() => new("Suspend declaration transition does not name its exact owning occurrences");
    }
}
