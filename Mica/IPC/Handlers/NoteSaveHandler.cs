using System.Text.Json;
using Mica.Services;
using Serilog;

namespace Mica.IPC.Handlers;

public class NoteSaveHandler : IIpcHandler
{
    private readonly FileService _fileService;

    public NoteSaveHandler(FileService fileService)
    {
        _fileService = fileService;
    }

    public string Method => "note.save";

    public async Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return null;

        var relPath = parameters.Value.GetProperty("relPath").GetString();
        var body = parameters.Value.GetProperty("body").GetString();

        if (relPath is null || body is null)
        {
            Log.Warning("note.save called with null relPath or body");
            return null;
        }

        try
        {
            await _fileService.WriteFileAsync(relPath, body);
            var result = new { savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return JsonSerializer.SerializeToElement(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save note: {RelPath}", relPath);
            throw;
        }
    }
}
