using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// Receives the current zoom factor pushed from the editor (Web → Host) whenever the
// user zooms via Ctrl+wheel / Ctrl+Shift+±/0. Raises an event the window subscribes to
// for the status-bar zoom indicator + persistence. Singleton so the subscribed instance
// matches the one the router dispatches to (same pattern as EditorStatsHandler).
public class EditorZoomHandler : IIpcHandler
{
    public string Method => "editor.zoom";

    public event Action<double>? ZoomChanged;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        var factor = parameters.Value.GetProperty("factor").GetDouble();
        ZoomChanged?.Invoke(factor);

        return Task.FromResult<JsonElement?>(null);
    }
}
