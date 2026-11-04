using System.Runtime.InteropServices;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Tests;

public sealed class NativeInputLayoutTests
{
    [Fact]
    public void InputStruct_MatchesWin32Size()
    {
        // SendInput rejects every call (error 87) unless cbSize equals the real sizeof(INPUT).
        var expected = IntPtr.Size == 8 ? 40 : 28;
        Assert.Equal(expected, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
