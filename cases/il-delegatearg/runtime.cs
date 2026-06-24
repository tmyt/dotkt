namespace Kfc {
    public delegate int Transform(int x);
    public class Box {
        private readonly Transform _f;
        public Box(Transform f) { _f = f; }                                  // delegate as CTOR param
        public int Apply(int x) => _f(x);
        public int Run(Transform g) => g(10);                                // delegate as METHOD param
    }
}
