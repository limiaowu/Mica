using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// Receives word/character counts pushed from the editor (Web → Host) and raises
// an event the window subscribes to for the status bar. Registered as a singleton
// so the subscribed instance is the same one the router dispatches to.
public class EditorStatsHandler : IIpcHandler
{
    public string Method => "editor.stats";

    public event Action<int, int>? StatsUpdated;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        var words = parameters.Value.GetProperty("words").GetInt32();
        var chars = parameters.Value.GetProperty("chars").GetInt32();
        StatsUpdated?.Invoke(words, chars);

        return Task.FromResult<JsonElement?>(null);
    }
}
