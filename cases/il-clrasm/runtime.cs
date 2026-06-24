using System.Collections.Generic;
namespace P {
    public class Item { public Item(string n){Name=n;} public string Name{get;} }
    public class Bag { public List<Item> Items {get;} = new List<Item>(); }
    public class Sink {
        public int CountE(IEnumerable<Item> xs){int n=0;foreach(var x in xs)n++;return n;}
        public int CountC(ICollection<Item> xs){return xs.Count;}     // List IS ICollection in C#
        public int CountL(IList<Item> xs){return xs.Count;}           // List IS IList in C#
    }
}
