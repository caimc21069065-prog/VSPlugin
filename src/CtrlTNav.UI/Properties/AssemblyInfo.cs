using System.Runtime.CompilerServices;

// The offscreen screenshot renderer (tools/CtrlTNav.UiDemo) drives the internal visual tree directly so the
// real popup chrome can be rendered without ever creating an HWND.
[assembly: InternalsVisibleTo("CtrlTNav.UiDemo")]
