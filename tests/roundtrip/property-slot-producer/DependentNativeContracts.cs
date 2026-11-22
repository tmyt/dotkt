namespace RoundtripDependentNative;

public interface IMarker<Tag> { }
public static class Family { public interface IMark<Tag> { } }
public interface IJoined<T> where T : Family.IMark<string>, IMarker<string> { }
public class Base { }
public sealed class Owner<Tag, T> where T : IMarker<Tag> { }
