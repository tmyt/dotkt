using System.Collections.Generic;
namespace TX {
    // 2-hop transitive: Panel/Widget are imported; Gadget (hop 1) and Sprocket (hop 2) are NOT.
    public class Sprocket { public Sprocket(int n) { Size = n; } public int Size { get; } }
    public class Gadget {
        public Gadget(string n) { Tag = n; }
        public string Tag { get; }
        public Sprocket Core() { return new Sprocket(Tag.Length); }
    }
    public class Widget {
        public Widget(string n) { Name = n; }
        public string Name { get; }
        public Gadget Make() { return new Gadget(Name + "!"); }
    }
    public class Panel {
        private readonly List<Widget> children = new List<Widget>();
        public IList<Widget> Children { get { return children; } }
        public IReadOnlyList<Widget> View { get { return children; } }
        public Dictionary<string, Widget> Index { get; } = new Dictionary<string, Widget>();
        public IEnumerable<string> Names() { foreach (var w in children) yield return w.Name + "."; }
    }
}
