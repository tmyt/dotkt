namespace P {
    // (4) A custom .NET delegate + a BCL Action parameter. A Kotlin lambda must bind to BOTH — the injector types
    // the parameter as a function type, and the backend builds the SPECIFIC delegate from the call-site signature.
    public delegate string Transform(int x);
    public class Engine {
        public string Apply(int v, Transform t) => "=" + t(v);
        public void Run(System.Action a) { a(); }
    }
}
