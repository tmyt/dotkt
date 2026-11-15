static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        public bool ExactTypeArgument(string owner, int index) => TypeRows(owner).ContainsKey(index);
    }

    static NativeProofPlan _nativeProofs;
}
