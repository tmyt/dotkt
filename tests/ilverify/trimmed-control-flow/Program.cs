// Execute the rewritten method: the untrimmed image and ILVerify on it do not exercise
// the linker's stack-depth recomputation.
return TargetUniverseProbeKt.trimmedBranchDepth(null, true) == 11
    && TargetUniverseProbeKt.trimmedBranchDepth(null, false) == 13
    && TargetUniverseProbeKt.trimmedBranchDepth("xx", false) == 12
    && TargetUniverseProbeKt.trimmedSafeCastDepth(true, 2) == 11
    && TargetUniverseProbeKt.trimmedSafeCastDepth(false, 2) == 12 ? 0 : 1;
