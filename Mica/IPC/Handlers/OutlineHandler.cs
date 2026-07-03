using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Mica.Models;

namespace Mica.IPC.Handlers;

// Receives the heading outline pushed from the editor (Web → Host) and raises an
// event the window subscribes to for the sidebar outline list. Registered as a
// singleton so the subscribed instance is the same one the router dispatches to.
public class OutlineHandler : IIpcHandler
{
    public string Method => "editor.outline";

    public event Action<IReadOnlyList<OutlineItem>>? OutlineUpdated;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        var items = new List<OutlineItem>();
        if (parameters.Value.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var el in arr.EnumerateArray())
            {
                var level = el.TryGetProperty("level", out var lv) ? lv.GetInt32() : 1;
                var text = el.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "") : "";
                items.Add(new OutlineItem { Level = level, Text = text, Index = index });
                index++;
            }
        }

        OutlineUpdated?.Invoke(items);
        return Task.FromResult<JsonElement?>(null);
    }
}
