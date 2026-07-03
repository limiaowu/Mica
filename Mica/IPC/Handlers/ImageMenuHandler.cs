using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// 接收 web 端转发的「图片右键菜单请求」（host.imageMenu）。右键点在图片上时，web 先选中该图、再转发坐标+属性到
// 这里，由宿主弹出 WinUI 原生 CommandBarFlyout（复制/路径/资源管理器/大小/圆角/边框/翻转/裁剪/替换/对齐/删除）。
// 单例注册，保证订阅实例 == 路由分发实例（同 TableMenuHandler）。
public class ImageMenuHandler : IIpcHandler
{
    public string Method => "host.imageMenu";

    // 所有菜单上下文打成一个 record（字段渐多，避免十几参 Action）。坐标 (X,Y) 相对 WebView 视口 CSS 像素；
    // Src=存储路径；Abs=磁盘绝对路径（网络/base64 图为空）；Crop=当前裁剪分数串（空=未裁）；Grouped=是否图册内图；
    // Width=当前显示宽度（如 "60%"，空=自适应）；Radius/Border=圆角/边框 px（null=未设）；FlipH/FlipV=翻转态。
    public record ImageMenuInfo(
        double X, double Y, string Src, string Abs, string Crop, bool Grouped,
        string Width, int? Radius, int? Border, bool FlipH, bool FlipV);

    public event Action<ImageMenuInfo>? MenuRequested;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);
        var p = parameters.Value;
        double x = p.TryGetProperty("x", out var xe) ? xe.GetDouble() : 0;
        double y = p.TryGetProperty("y", out var ye) ? ye.GetDouble() : 0;
        string src = p.TryGetProperty("src", out var se) ? se.GetString() ?? "" : "";
        string abs = p.TryGetProperty("abs", out var ae) ? ae.GetString() ?? "" : "";
        string crop = p.TryGetProperty("crop", out var ce) ? ce.GetString() ?? "" : "";
        bool grouped = p.TryGetProperty("grouped", out var ge) && ge.ValueKind == JsonValueKind.True;
        string width = p.TryGetProperty("width", out var we) ? we.GetString() ?? "" : "";
        int? radius = p.TryGetProperty("radius", out var re) && re.ValueKind == JsonValueKind.Number ? re.GetInt32() : null;
        int? border = p.TryGetProperty("border", out var be) && be.ValueKind == JsonValueKind.Number ? be.GetInt32() : null;
        bool flipH = p.TryGetProperty("flipH", out var fhe) && fhe.ValueKind == JsonValueKind.True;
        bool flipV = p.TryGetProperty("flipV", out var fve) && fve.ValueKind == JsonValueKind.True;
        MenuRequested?.Invoke(new ImageMenuInfo(x, y, src, abs, crop, grouped, width, radius, border, flipH, flipV));
        return Task.FromResult<JsonElement?>(null);
    }
}
