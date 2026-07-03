using System.Text.Json;
using Mica.Services;
using Serilog;

namespace Mica.IPC.Handlers;

// 粘贴/拖入图片：web 发图片字节（base64）+ 笔记绝对路径，宿主按存储模式落盘，回传要写进 .md 的 src。
public class ImageSaveHandler : IIpcHandler
{
    private readonly FileService _fileService;
    private readonly SettingsService _settings;

    public ImageSaveHandler(FileService fileService, SettingsService settings)
    {
        _fileService = fileService;
        _settings = settings;
    }

    public string Method => "image.save";

    public async Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return null;
        var p = parameters.Value;

        var noteAbsPath = p.GetProperty("noteAbsPath").GetString();
        var dataBase64 = p.GetProperty("dataBase64").GetString();
        var ext = p.TryGetProperty("ext", out var e) ? e.GetString() : "png";
        var sourceName = p.TryGetProperty("sourceName", out var s) ? s.GetString() : null;

        if (string.IsNullOrEmpty(noteAbsPath) || string.IsNullOrEmpty(dataBase64))
        {
            Log.Warning("image.save called with null noteAbsPath or data");
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(dataBase64);
            var root = _settings.ResolveNotebookRoot(noteAbsPath);
            var src = await _fileService.SavePastedImageAsync(
                noteAbsPath, root, bytes, ext ?? "png", sourceName, _settings.ImageStorageMode);
            return JsonSerializer.SerializeToElement(new { src });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save pasted image for {Note}", noteAbsPath);
            throw;
        }
    }
}
