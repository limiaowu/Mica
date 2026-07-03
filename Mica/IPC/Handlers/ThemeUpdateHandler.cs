using System;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Mica.IPC.Handlers;

public class ThemeUpdateHandler : IIpcHandler
{
    public string Method => "theme.update";

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        var vars = parameters.Value.GetProperty("vars");
        var mode = parameters.Value.GetProperty("mode").GetString();

        // TODO: Apply theme variables to application resources
        Log.Information("Theme update: mode={Mode}, vars={Vars}", mode, vars);

        return Task.FromResult<JsonElement?>(null);
    }
}
