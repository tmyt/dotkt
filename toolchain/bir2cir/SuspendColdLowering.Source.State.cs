using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    // A parameter view always points at a real source parameter. Lambda parameters
    // and named parameters have different declaration records, not different meaning.
    internal sealed class SourceParameter(BirGraphValue declaration)
    {
        internal BirGraphValue Declaration => declaration;
        internal string Name => declaration switch {
            BirParameter parameter => parameter.Name.OrDefault(),
            BirLambdaParameter parameter => parameter.Name.OrDefault(),
            _ => throw new InvalidOperationException("Not a suspend parameter"),
        };
        internal TypeNode Type => declaration switch {
            BirParameter parameter => parameter.Type.OrDefault(),
            BirLambdaParameter parameter => parameter.Type.OrDefault(),
            _ => throw new InvalidOperationException("Not a suspend parameter"),
        };
        internal BirModifiers Modifiers => declaration switch {
            BirParameter parameter => parameter.Mods.OrDefault(),
            BirLambdaParameter parameter => parameter.Mods.OrDefault(),
            _ => throw new InvalidOperationException("Not a suspend parameter"),
        };
    }

    internal sealed partial class SourceFunGen
    {
        const string ThisField = "$this";
        const string StateLabelField = "dotkt$sm$label";
        const string CompletionParameter = "dotkt$sm$completion";
        const string ResumeResultParameter = "dotkt$sm$result";
        const string StateMachineLocal = "dotkt$sm$instance";
        const string TaskCompletionSourceLocal = "dotkt$sm$tcs";
        const string RootContinuationLocal = "dotkt$sm$root";
        const string BridgeResultLocal = "dotkt$sm$bridgeResult";
        const string BridgeExceptionLocal = "dotkt$sm$exception";
        const string SuspendingField = "$suspending";
        const string ActionFqn = "System.Action";

        readonly SuspendModuleContext _module;
        readonly SourceFunctionInput _input;
        readonly BirFunctionDeclaration _m;
        readonly BirNewSuspendLambda _lambda;
        readonly string _name, _fileClass, _ownerClass, _stubReason;
        readonly bool _isMember, _staticMember, _generated, _baseIsLocal;
        readonly string _tcsBcl, _taskBcl;
        readonly List<string> _ownerTypeParams, _smAllTps, _typeParams;
        readonly BirTypeParameter[] _ownerTypeParamDecls, _methodTypeParamDecls;
        readonly TypeNode _selfType, _smTypeInst, _resultType, _taskResultType, _logicalSuspendResult;
        readonly bool _memberAbstract, _memberOverride, _memberVirtual;
        readonly IReadOnlyDictionary<string, IBirTypeDeclaration> _closures;
        readonly IReadOnlyDictionary<string, BirFunctionDeclaration> _lambdaMethods;
        readonly bool _isLambda, _restrictedBase;
        readonly int _arity;
        readonly List<(string name, TypeNode type)> _captures;
        readonly string _capturedOuterName, _smType, _coldName;
        readonly bool _resultNullable;
        readonly TypeNode _resultNullableGeneric;
        readonly SourceParameter[] _params;
        readonly string _extensionReceiverName;
        readonly BirOverride[] _overrideMarkers;
        readonly bool _physicalSlotBridge, _taskOnlySlotBridge, _clrInterfaceSlotBridge;
        readonly string _physicalSlotVisibility;
        readonly OptionalField<BirMethodImpl[]> _clrInterfaceImpls, _clrBaseImpls;
        readonly OptionalField<TypeNode> _companionReceiver;
        readonly OptionalField<string> _companionSourceName, _companionMemberKind;
        readonly string _declarationId, _declarationSourceName, _explicitClrName;
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, TypeNode type)> _fieldDecls = new();
        readonly Dictionary<string, TypeNode> _localTypes = new(StringComparer.Ordinal);
        readonly List<BirFunctionDeclaration> _awaitResumeMethods = new();
        readonly List<SourceMethodProjection> _methodProjections = new();
        // Authored SM-self occurrences are distinct from source `this`. Provenance
        // stays private to this generator and is consumed before physical output.
        readonly HashSet<BirThis> _stateMachineSelf = new(ReferenceEqualityComparer.Instance);
        int _state, _label, _condCounter, _loopCounter, _excCounter, _awaitTarget;
        bool _needSuspendGuard;
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        ReferenceMetadataIndex _refs => _module.References;
        ValueTypeOracle _isValueFqn => _module.IsValue;
        IReadOnlySet<string> _localTypeFqns => _module.EmittedTypeNames;
        IReadOnlyDictionary<string, TypeNode> _calleeRet => _module.CalleeReturns;
        bool _appBuild => _module.AppBuild;
        IBirStatement[] SourceBody => _isLambda ? _lambda.Body.OrEmpty() : _m.Body.OrEmpty();

        internal SourceFunGen(SuspendModuleContext module, SourceFunctionInput input)
        {
            _module = module; _input = input;
            if (!module.Stage0Complete) throw new InvalidOperationException("Suspend generator precedes Stage0");
            _closures = module.Closures;
            _tcsBcl = TaskCompletionSourceBclFqn; _taskBcl = TaskBclFqn;
            if (input is SourceFunctionInput.Named named)
            {
                _m = named.Method;
                _name = _m.Name.OrDefault(); _fileClass = named.File.FileClass.OrDefault() ?? "Kt";
                _ownerClass = named.Owner?.Name.OrDefault();
                _staticMember = named.Owner != null && _m.Static.OrDefault();
                _isMember = named.Owner != null && !_staticMember;
                _generated = _m.Generated.OrDefault(); _baseIsLocal = module.ContinuationBaseIsLocal;
                _declarationId = _m.DeclarationId.OrDefault();
                _declarationSourceName = _m.DeclarationSourceName.OrDefault();
                _explicitClrName = _m.ExplicitClrName.OrDefault();
                _lambdaMethods = module.FileLambdaMethods(named.File);
                _ownerTypeParamDecls = named.OwnerParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
                _methodTypeParamDecls = ConstrainedTypeParameterReceiverBinding.CloneMethodParametersWithErasedSourceBounds(_m);
                _memberAbstract = _isMember && _m.Abstract.OrDefault();
                _memberOverride = _isMember && _m.Override.OrDefault();
                _memberVirtual = _isMember && _m.Virtual.OrDefault();
                _smType = (_ownerClass ?? _fileClass) + "_" + _name + named.StateMachineSuffix + "$sm";
                _coldName = (_explicitClrName ?? _name) + "$dotkt_suspend";
                var suspendReturn = _m.SuspendRet.OrDefault();
                _resultNullableGeneric = _m.NullableGenericSuspendRet.OrDefault() ?? _m.NullableGenericRet.OrDefault();
                _logicalSuspendResult = _m.SuspendResult.OrDefault()
                    ?? throw new InvalidOperationException("suspend declaration has no logical result");
                _resultNullable = suspendReturn is TypeNode.Nullable || _logicalSuspendResult is TypeNode.Nullable;
                _resultType = ResultStorageTypeSource(suspendReturn) ?? VoidTn;
                _taskResultType = _m.SuspendTaskResult.OrDefault() ?? _resultType;
                _params = _m.Params.OrEmpty().Select(parameter => new SourceParameter(parameter)).ToArray();
                _overrideMarkers = _m.Overrides.OrEmpty().Select(marker => (BirOverride)marker.DeepClone()).ToArray();
                _physicalSlotBridge = _m.PhysicalSlotBridge.OrDefault();
                _taskOnlySlotBridge = _m.SuspendTaskOnlyBridge.OrDefault();
                _clrInterfaceSlotBridge = _m.ClrInterfaceSlotBridge.OrDefault();
                _physicalSlotVisibility = _m.Vis.OrDefault();
                _clrInterfaceImpls = _m.ClrInterfaceImpls;
                _clrBaseImpls = _m.ClrBaseImpls;
                _companionReceiver = _m.CompanionReceiver;
                _companionSourceName = _m.CompanionSourceName;
                _companionMemberKind = _m.CompanionMemberKind;
                if (!_memberAbstract) _stubReason = SuspensionRefusalReasonSource(SourceBody, false, 0);
            }
            else if (input is SourceFunctionInput.Lambda lambda)
            {
                _isLambda = true; _lambda = lambda.Declaration; _restrictedBase = lambda.Restricted;
                _baseIsLocal = lambda.BaseIsLocal; _arity = _lambda.Arity.OrDefault();
                _name = _smType = lambda.StateMachineName;
                if (lambda.OwnerParameterCount < 0 || lambda.OwnerParameterCount > lambda.DeclarationParameters.Length)
                    throw new InvalidOperationException($"suspend lambda '{_smType}' has an invalid owner capture frame");
                _ownerTypeParamDecls = lambda.DeclarationParameters.Take(lambda.OwnerParameterCount)
                    .Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
                _methodTypeParamDecls = lambda.DeclarationParameters.Skip(lambda.OwnerParameterCount)
                    .Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
                _closures = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
                _lambdaMethods = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
                var captures = _lambda.Captures.OrEmpty();
                var outer = captures.Where(capture => capture.Outer.OrDefault()).ToArray();
                if (outer.Length > 1) throw new NotSupportedException($"suspend lambda '{_smType}' has multiple enclosing-receiver captures");
                _capturedOuterName = outer.FirstOrDefault()?.Name.OrDefault();
                _captures = captures.Select(capture => (capture.Name.OrDefault(), capture.Type.OrDefault())).ToList();
                _params = _lambda.Params.OrEmpty().Select(parameter => new SourceParameter(parameter)).ToArray();
                _resultNullable = _lambda.SuspendRet.OrDefault() is TypeNode.Nullable;
                _resultType = ResultStorageTypeSource(_lambda.SuspendRet.OrDefault()) ?? VoidTn;
                _overrideMarkers = [];
                _stubReason = SuspensionRefusalReasonSource(SourceBody, false, 0);
            }
            else throw new InvalidOperationException("Unknown suspend source input");
            _ownerTypeParams = _ownerTypeParamDecls.Select(parameter => parameter.Name.Value).ToList();
            _typeParams = _methodTypeParamDecls.Select(parameter => parameter.Name.Value).ToList();
            _smAllTps = [.. _ownerTypeParams, .. _typeParams];
            _selfType = _ownerClass == null ? null : new TypeNode.Fqn(_ownerClass,
                _ownerTypeParams.Count == 0 ? null : TypeTvsSource(_ownerTypeParams.Count));
            _smTypeInst = new TypeNode.Fqn(_smType, _smAllTps.Count == 0 ? null : TypeTvsSource(_smAllTps.Count));
            _extensionReceiverName = !_isLambda && _params.FirstOrDefault() is { } first
                && first.Modifiers?.ExtensionReceiver.OrDefault() == true ? first.Name : null;
        }

        static TypeNode[] TypeTvsSource(int count) => Enumerable.Range(0, count).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        TypeNode ResultStorageTypeSource(TypeNode type) => type is TypeNode.Nullable nullable
            && !(nullable.Of is TypeNode.Fqn value && _isValueFqn(value)) ? nullable.Of : type;
        int NextLabel() => ++_label;
        bool IsExactOuterStorage(string name) => _isMember && name == ThisField || _isLambda && name == _capturedOuterName;
        bool WantsBridge => !_isLambda && _name != "main" && !_baseIsLocal;
        internal bool NeedsRootContinuation => !_memberAbstract && ((_name == "main" && _ownerClass == null) || WantsBridge);
        BirThis StateMachineSelf()
        {
            var self = new BirThis();
            _stateMachineSelf.Add(self);
            return self;
        }
    }
}
