namespace QuickReply.Desktop.Services;

internal interface IKeyboardInputSimulator
{
    void SendPasteShortcut();

    void SendEnter();

    void SendCtrlEnter();

    void SendAltS();

    void SendCtrlA();

    void SendCtrlC();

    void SendEscape();
}
