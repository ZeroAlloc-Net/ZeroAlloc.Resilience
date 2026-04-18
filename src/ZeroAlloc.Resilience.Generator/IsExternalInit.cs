// Required polyfill so that C# records compile on netstandard2.0.
// The compiler synthesises init-only setters that reference this type;
// on older TFMs it is absent from the BCL, so we supply it ourselves.
using System.ComponentModel;

namespace System.Runtime.CompilerServices;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit { }
