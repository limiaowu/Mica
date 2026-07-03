using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Mica.IPC;

public interface IIpcHandler
{
    string Method { get; }
    Task<JsonElement?> HandleAsync(JsonElement? parameters);
}

public interface IIpcChannel
{
    void SendMessage(string json);
    event Action<string>? MessageReceived;
}

public class IpcRouter
{
    private readonly Dictionary<string, IIpcHandler> _handlers = new(StringComparer.Ordinal);
    private IIpcChannel? _channel;

    public void Register(IIpcHandler handler)
    {
        _handlers[handler.Method] = handler;
    }

    public void AttachChannel(IIpcChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += OnMessageReceived;
    }

    public void DetachChannel()
    {
        if (_channel is not null)
        {
            _channel.MessageReceived -= OnMessageReceived;
            _channel = null;
        }
    }

    public void SendNotification(string method, object? parameters)
    {
        if (_channel is null) return;

        var msg = IpcMessage.CreateNotification(method, parameters);
        var json = JsonSerializer.Serialize(msg);
        _channel.SendMessage(json);
    }

    public void SendToWebView(string method, object? parameters)
    {
        SendNotification(method, parameters);
    }

    private async void OnMessageReceived(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<IpcMessage>(json);
            if (msg is null) return;

            if (msg.IsRequest)
            {
                await HandleRequest(msg);
            }
            else if (msg.IsNotification)
            {
                await HandleNotification(msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing IPC message");
        }
    }

    private async Task HandleRequest(IpcMessage msg)
    {
        var method = msg.Method!;
        var id = msg.Id!.Value;

        if (!_handlers.TryGetValue(method, out var handler))
        {
            SendResponse(IpcMessage.CreateError(id, -32601, $"Method not found: {method}"));
            return;
        }

        try
        {
            var result = await handler.HandleAsync(msg.Params);
            SendResponse(IpcMessage.CreateResponse(id, result));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC handler error for method {Method}", method);
            SendResponse(IpcMessage.CreateError(id, -32000, ex.Message));
        }
    }

    private async Task HandleNotification(IpcMessage msg)
    {
        var method = msg.Method!;

        if (!_handlers.TryGetValue(method, out var handler))
        {
            Log.Warning("No handler for notification: {Method}", method);
            return;
        }

        try
        {
            await handler.HandleAsync(msg.Params);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC notification handler error for method {Method}", method);
        }
    }

    private void SendResponse(IpcMessage response)
    {
        if (_channel is null) return;

        var json = JsonSerializer.Serialize(response);
        _channel.SendMessage(json);
    }
}
