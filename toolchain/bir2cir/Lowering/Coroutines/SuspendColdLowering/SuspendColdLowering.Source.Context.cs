using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    // One compilation's suspend clocks. These are the actual continuing declarations,
    // not a second signature catalogue or projected MethodDef stand-ins.
    internal sealed class SuspendModuleContext
    {
        internal IReadOnlyList<BirFileDeclaration> Roots { get; private set; }
        internal ReferenceMetadataIndex References { get; }
        internal IReadOnlySet<string> EmittedTypeNames { get; }
        internal bool AppBuild { get; }
        internal IReadOnlyDictionary<string, string> ExistentialOwners { get; }
        internal ValueTypeOracle IsValue { get; }
        internal SourceDeclarationSignatures SourceSignatures { get; }
        internal IDictionary<string, int[]> PhysicalParameterIndices { get; }
        internal IDictionary<string, string> PhysicalDeclarationSources { get; }
        internal SourceDeclarationTransitions Transitions { get; }
        internal Action<BirCallStatic, BirCallStatic, TypeNode> ProjectCallSignature { get; }
        internal IReadOnlyList<SourceNamedEntry> NamedEntries { get; private set; }
        internal IReadOnlyDictionary<string, IBirTypeDeclaration> Closures { get; private set; }
        internal IReadOnlyList<SourceHelperDeclaration> HelperDeclarations { get; private set; }
        internal IReadOnlyDictionary<string, TypeNode> CalleeReturns { get; }
        internal IReadOnlyDictionary<string, TypeNode> MethodReturns { get; }
        internal IReadOnlyDictionary<string, TypeNode> FieldTypes { get; }
        TypeNode continuationStar;
        internal TypeNode ContinuationStar => continuationStar ??= ResolveContinuationStar();
        internal bool ContinuationBaseIsLocal => EmittedTypeNames.Contains(ContinuationImplFqn);
        internal HashSet<IBirTypeDeclaration> ConsumedIntrinsicClosures { get; } = new(ReferenceEqualityComparer.Instance);
        internal IReadOnlyDictionary<string, BirTypeParameter[]> LambdaOwnerParameters { get; private set; }
        internal bool Stage0Complete { get; private set; }
        bool sourceReleased;

        internal SuspendModuleContext(IReadOnlyList<BirFileDeclaration> roots,
            ReferenceMetadataIndex references, IReadOnlySet<string> emittedTypeNames, bool appBuild,
            IReadOnlyDictionary<string, string> existentialOwners, ValueTypeOracle isValue,
            SourceDeclarationSignatures sourceSignatures,
            IDictionary<string, int[]> physicalParameterIndices,
            IDictionary<string, string> physicalDeclarationSources,
            SourceDeclarationTransitions transitions,
            Action<BirCallStatic, BirCallStatic, TypeNode> projectCallSignature)
        {
            Roots = roots; References = references; EmittedTypeNames = emittedTypeNames;
            AppBuild = appBuild; ExistentialOwners = existentialOwners; IsValue = isValue ?? (_ => false);
            SourceSignatures = sourceSignatures; PhysicalParameterIndices = physicalParameterIndices;
            PhysicalDeclarationSources = physicalDeclarationSources;
            Transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            ProjectCallSignature = projectCallSignature ?? throw new ArgumentNullException(nameof(projectCallSignature));
            var entries = new List<SourceNamedEntry>();
            var closures = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
            var helpers = new List<SourceHelperDeclaration>();
            var calleeReturns = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            var methodReturns = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            var fieldTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var file in roots)
            {
                var fileClass = file.FileClass.OrDefault();
                RecordMethods(file.Methods.OrEmpty(), null, fileClass, file);
                RecordFields(file.Fields.OrEmpty(), null);
                foreach (var owner in file.Types.OrEmpty())
                {
                    var name = owner.Name.OrDefault();
                    if (name != null) closures[name] = owner;
                    RecordMethods(owner.Methods.OrEmpty(), owner, name, file);
                    RecordFields(owner.Fields.OrEmpty(), name);
                }
            }
            NamedEntries = entries; Closures = closures; HelperDeclarations = helpers;
            CalleeReturns = calleeReturns; MethodReturns = methodReturns; FieldTypes = fieldTypes;

            void RecordMethods(BirFunctionDeclaration[] methods, IBirTypeDeclaration owner,
                string ownerName, BirFileDeclaration file)
            {
                foreach (var method in methods)
                {
                    if (method.DeclarationId.HasValue
                        && sourceSignatures?.TryGetValue(method.DeclarationId.Value, out var signature) == true)
                        helpers.Add(new(ownerName, method, signature));
                    var name = method.Name.OrDefault();
                    if (name != null)
                        methodReturns[(owner == null ? "" : ownerName) + "#" + name] =
                            SourceMethodDeclRet(method, owner == null ? null : ownerName, name);
                    if (name == null || owner != null && ownerName == null
                        || !IsSourceColdCandidate(method, owner == null, appBuild)) continue;
                    entries.Add(new(file, owner, method));
                    calleeReturns[name] = method.SuspendRet.OrDefault() ?? throw new InvalidOperationException(
                        $"bir2cir: suspend-lowering: the suspend declaration `{ownerName}.{name}` carries no `suspendRet` slot.");
                }
            }
            void RecordFields(BirFieldDeclaration[] fields, string owner)
            {
                foreach (var field in fields)
                    if (field.Name.HasValue && field.Type.HasValue)
                        fieldTypes[(owner ?? "") + "#" + field.Name.Value] = field.Type.Value;
            }
        }

        TypeNode ResolveContinuationStar()
        {
            const string continuation = "kotlin.coroutines.Continuation";
            var carrier = ExistentialOwners.GetValueOrDefault(continuation);
            if (carrier == null) References.TryExistentialPhysicalOwner(continuation, out carrier);
            return new TypeNode.Fqn(carrier ?? throw new InvalidOperationException(
                "suspend lowering requires the trusted Continuation<*> existential ABI"));
        }

        // Stage0 is unconditional, including a module containing only suspend-lambda values.
        internal void PrepareStage0()
        {
            if (Stage0Complete) throw new InvalidOperationException("Suspend operand plans were prepared twice");
            PlanSuspensionBearingOperands(Roots, new SourceExprTyper(MethodReturns, FieldTypes), IsValue,
                (method, topLevel) => ShouldCheckSuspendBodySource(method, topLevel, AppBuild), Transitions.RetireDiscardedSubtree);
            Stage0Complete = true;
        }

        internal IBirStatement[] AcquireBody(SourceFunctionInput input)
        {
            if (input is SourceFunctionInput.Named named)
            {
                var body = named.Method.Body.OrEmpty();
                if (ContinuationBaseIsLocal)
                    return body.Select(statement => (IBirStatement)Transitions.CloneIndependent((BirGraphValue)statement)).ToArray();
                named.Method.Body = Array.Empty<IBirStatement>();
                return body;
            }
            if (input is SourceFunctionInput.Lambda lambda)
            {
                var body = lambda.Declaration.Body.OrEmpty();
                lambda.Declaration.Body = Array.Empty<IBirStatement>();
                return body;
            }
            throw new InvalidOperationException("Unknown suspend body owner");
        }

        // The lambda phase sees the completed Cold outputs. Do not take this first-wins
        // declaration-frame snapshot at the earlier named-entry collection clock.
        internal void BeginLambdaPhase()
        {
            if (!Stage0Complete || LambdaOwnerParameters != null)
                throw new InvalidOperationException("Invalid suspend-lambda preparation clock");
            var parameters = new Dictionary<string, BirTypeParameter[]>(StringComparer.Ordinal);
            foreach (var owner in Roots.SelectMany(file => file.Types.OrEmpty()))
                if (owner.Name.HasValue && !parameters.ContainsKey(owner.Name.Value))
                    parameters.Add(owner.Name.Value,
                        ConstrainedTypeParameterReceiverBinding.CloneTypeParametersWithErasedSourceBounds(owner));
            LambdaOwnerParameters = parameters;
        }

        internal IReadOnlyDictionary<string, BirFunctionDeclaration> FileLambdaMethods(BirFileDeclaration file)
        {
            var result = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
            foreach (var method in file.Methods.OrEmpty())
                if (method.Name.HasValue) result[method.Name.Value] = method;
            return result;
        }

        internal void ReleaseSourceDeclarations()
        {
            if (!Stage0Complete || sourceReleased) throw new InvalidOperationException("Invalid suspend source release clock");
            sourceReleased = true;
            Roots = Array.Empty<BirFileDeclaration>(); NamedEntries = Array.Empty<SourceNamedEntry>();
            Closures = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
            HelperDeclarations = Array.Empty<SourceHelperDeclaration>();
            ConsumedIntrinsicClosures.Clear(); LambdaOwnerParameters = null;
        }
    }

    internal sealed record SourceNamedEntry(BirFileDeclaration File, IBirTypeDeclaration Owner,
        BirFunctionDeclaration Method);
    internal sealed record SourceHelperDeclaration(string Owner, BirFunctionDeclaration Method,
        SourceDeclarationSignature Signature);

    internal abstract record SourceFunctionInput(BirFileDeclaration File, IBirTypeDeclaration Owner)
    {
        internal sealed record Named(BirFileDeclaration File, IBirTypeDeclaration Owner,
            BirFunctionDeclaration Method, string StateMachineSuffix, BirTypeParameter[] OwnerParameters)
            : SourceFunctionInput(File, Owner);
        internal sealed record Lambda(BirFileDeclaration File, IBirTypeDeclaration Owner,
            BirNewSuspendLambda Declaration, string StateMachineName, BirTypeParameter[] DeclarationParameters,
            int OwnerParameterCount, bool BaseIsLocal, bool Restricted)
            : SourceFunctionInput(File, Owner);
    }

    internal sealed record SourceMethodProjection(BirFunctionDeclaration Origin, BirFunctionDeclaration Projection);
    internal sealed record SourceBuildResult(IReadOnlyList<BirFunctionDeclaration> Methods,
        IReadOnlyList<IBirTypeDeclaration> Types, bool NeedsRootContinuation,
        IReadOnlyList<SourceMethodProjection> MethodProjections);

    static TypeNode SourceMethodDeclRet(BirFunctionDeclaration method, string owner, string name) =>
        method.SuspendRet.OrDefault() ?? method.Ret.OrDefault() ?? throw new InvalidOperationException(
            $"bir2cir: suspend-lowering: the declaration `{(owner == null ? "" : owner + ".")}{name}` carries neither a `suspendRet` nor a `ret` slot.");
}
