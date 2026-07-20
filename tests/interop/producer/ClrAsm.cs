// Producer source for the migrated il-clrasm case. A concrete generic .NET collection (List<Item>) is
// assignable to EVERY generic interface it implements (IEnumerable<T>/ICollection<T>/IList<T>). Given its OWN
// namespace so the many colliding simple names (Item/Bag/Sink) from the other migrated cases coexist in this
// single producer assembly (mirrors the roundtrip producer's per-package split).
using System.Collections.Generic;
namespace ClrAsm {
    public class Item { public Item(string n){Name=n;} public string Name{get;} }
    public class Bag { public List<Item> Items {get;} = new List<Item>(); }
    public class Sink {
        public int CountE(IEnumerable<Item> xs){int n=0;foreach(var x in xs)n++;return n;}
        public int CountC(ICollection<Item> xs){return xs.Count;}     // List IS ICollection in C#
        public int CountL(IList<Item> xs){return xs.Count;}           // List IS IList in C#
    }
}
