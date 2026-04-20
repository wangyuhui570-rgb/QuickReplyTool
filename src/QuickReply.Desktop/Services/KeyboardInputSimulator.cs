using System.Runtime.InteropServices;
using QuickReply.Desktop.Native;
using SendKeys = System.Windows.Forms.SendKeys;

namespace QuickReply.Desktop.Services;

public sealed class KeyboardInputSimulator : IKeyboardInputSimulator
{
    public void SendPasteShortcut()
    {
        SendShortcut("^v", NativeMethods.VkControl, NativeMethods.VkV);
    }

    public void SendEnter()
    {
        SendShortcut("{ENTER}", NativeMethods.VkReturn);
    }

    public void SendCtrlEnter()
    {
        SendShortcut("^{ENTER}", NativeMethods.VkControl, NativeMethods.VkReturn);
    }

    public void SendShiftEnter()
    {
        SendShortcut("+{ENTER}", (ushort)NativeMethods.VkShift, NativeMethods.VkReturn);
    }

    public void SendAltS()
    {
        SendShortcut("%s", NativeMethods.VkMenu, NativeMethods.VkS);
    }

    public void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(NativeMethods.CreateUnicodeKeyboardInput(character, false));
            inputs.Add(NativeMethods.CreateUnicodeKeyboardInput(character, true));
        }

        var sent = NativeMethods.SendInput(
            (uint)inputs.Count,
            inputs.ToArray(),
            Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputs.Count)
        {
            throw new InvalidOperationException("发送文本失败。");
        }
    }

    private static void SendShortcut(string sendKeysPattern, params ushort[] fallbackVirtualKeys)
    {
        try
        {
            SendChord(fallbackVirtualKeys);
        }
        catch
        {
            SendKeys.SendWait(sendKeysPattern);
        }
    }

    private static void SendChord(params ushort[] virtualKeys)
    {
        var inputs = new List<NativeMethods.INPUT>(virtualKeys.Length * 2);

        foreach (var key in virtualKeys)
        {
            inputs.Add(NativeMethods.CreateKeyboardInput(key, false));
        }

        for (var index = virtualKeys.Length - 1; index >= 0; index--)
        {
            inputs.Add(NativeMethods.CreateKeyboardInput(virtualKeys[index], true));
        }

        var sent = NativeMethods.SendInput(
            (uint)inputs.Count,
            inputs.ToArray(),
            Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputs.Count)
        {
            throw new InvalidOperationException("发送按键失败。");
        }
    }
}
