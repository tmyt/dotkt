namespace RoundtripDependentNative;

public interface IMarker<Tag> { }
public class Base { }
public sealed class Owner<Tag, T> where T : IMarker<Tag> { }
