using System;
using Mica.Controls;

namespace Mica.IPC;

public class WebView2IpcChannel : IIpcChannel
{
    private readonly EditorHostView _editorView;

    public WebView2IpcChannel(EditorHostView editorView)
    {
        _editorView = editorView;
        _editorView.WebMessageReceived += OnWebMessageReceived;
    }

    public event Action<string>? MessageReceived;

    public void SendMessage(string json)
    {
        _editorView.PostWebMessageAsJson(json);
    }

    private void OnWebMessageReceived(object? sender, string json)
    {
        MessageReceived?.Invoke(json);
    }
}
