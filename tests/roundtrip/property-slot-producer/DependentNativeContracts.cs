namespace RoundtripDependentNative;

public interface IMarker<Tag> { }
public sealed class Owner<Tag, T> where T : IMarker<Tag> { }
