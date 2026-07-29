// Public STATIC members of a normal projected class are surfaced on
// a synthesized companion, reachable BOTH implicitly (`App.start`/`App.Count`) and explicitly (`App.Companion.start`).
// Covers a static method w/ delegate arg, a static property, a static readonly FIELD (ldsfld), and a const. Own ns.
namespace Injstatic {
    public delegate void InitCb(int p);
    public class App {                                  // a NORMAL class (has instance members) with STATIC members
        public int inst = 1;
        public void run() { }
        public static int start(InitCb cb) { cb(42); return 0; }   // static method w/ delegate arg
        public static int Count => 7;                              // static property
        public static readonly int Answer = 99;                    // static FIELD (surfaced as `sprop` -> ldsfld)
        public const int Magic = 123;                              // const (literal) FIELD -> inlined value, no ldsfld
    }
}
