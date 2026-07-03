using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// 接收 web 端「打开裁剪对话框」请求（host.imageCrop {abs, crop}）。浮动条/右键菜单点「裁剪」时 web 已选中该图，
// 把图的磁盘绝对路径 + 当前裁剪框转来，宿主据此弹出 WinUI 裁剪对话框（见 MainWindow.ImageCrop.cs）。
// 确定后宿主经 editor.imageOp {op:'crop', crop} 回传，web 在选中图上写 crop 属性。单例注册（同 ImageMenuHandler）。
public class ImageCropHandler : IIpcHandler
{
    public string Method => "host.imageCrop";

    // abs = 磁盘绝对路径（裁剪须读原图文件，网络/base64 图 web 端已拦下不发）；crop = 当前裁剪分数串（空=未裁，用于预选）。
    public event Action<string, string?>? CropRequested;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);
        var p = parameters.Value;
        string abs = p.TryGetProperty("abs", out var ae) ? ae.GetString() ?? "" : "";
        string? crop = p.TryGetProperty("crop", out var ce) ? ce.GetString() : null;
        if (!string.IsNullOrEmpty(abs)) CropRequested?.Invoke(abs, string.IsNullOrEmpty(crop) ? null : crop);
        return Task.FromResult<JsonElement?>(null);
    }
}
