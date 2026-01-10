using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pictomancy;

public partial class CImGui
{
    [LibraryImport("cimgui")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
    public static partial void igBringWindowToDisplayFront(nint ptr);

    [LibraryImport("cimgui")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
    public static partial void igBringWindowToDisplayBack(nint ptr);

    [LibraryImport("cimgui")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
    public static partial nint igGetCurrentWindow();
}
