using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Mica.IPC.Handlers;

public class LogHandler : IIpcHandler
{
    public string Method => "log";

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        var level = parameters.Value.GetProperty("level").GetString();
        var message = parameters.Value.GetProperty("message").GetString();
        var data = parameters.Value.TryGetProperty("data", out var d) ? d.ToString() : null;

        switch (level?.ToLowerInvariant())
        {
            case "error":
                Log.Error("[WebView] {Message} {Data}", message, data);
                break;
            case "warn":
                Log.Warning("[WebView] {Message} {Data}", message, data);
                break;
            case "debug":
                Log.Debug("[WebView] {Message} {Data}", message, data);
                break;
            default:
                Log.Information("[WebView] {Message} {Data}", message, data);
                break;
        }

        return Task.FromResult<JsonElement?>(null);
    }
}
