// Producer source for the migrated il-injbase case. Assignability must survive a non-constructible base:
// TextBox -> Frame -> Element, where Element has no accessible no-arg ctor (a WinRT-style base, like WinUI UIElement).
// The base edge is emitted for is-a even though the projected type has no constructible base path. Own namespace.
namespace Injbase {
    public abstract class Element { internal Element() {} public int tag; }   // no PUBLIC no-arg ctor (WinRT-like)
    public class Frame : Element { public Frame() {} }
    public class TextBox : Frame { public TextBox() {} }
    public static class Api { public static string place(Element e) => "placed:" + e.tag; }
}
