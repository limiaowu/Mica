using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// 接收 web 端转发的「表格右键菜单请求」（host.tableMenu）。右键点在表格单元格内时，web 先把选区落到该单元格、
// 再转发坐标+整表信息到这里，由宿主弹出 WinUI 原生 CommandBarFlyout（菜单项点击后经 editor.tableOp 让 web 执行
// 对应表格命令）。单例注册，保证订阅实例 == 路由分发实例。
public class TableMenuHandler : IIpcHandler
{
    public string Method => "host.tableMenu";

    // 菜单上下文 record（同 ImageMenuHandler）。(X,Y)=WebView 视口 CSS 像素；InHeader=点中表头行；
    // Rows/Cols=当前行/列数；Preset=样式预设名（空=普通）；Radius/Border=圆角/线宽 px（null=未设）；
    // Pos=整表位置态（left/center/right/full），供「整表对齐」四选一回填高亮。
    public record TableMenuInfo(
        double X, double Y, bool InHeader,
        int Rows, int Cols, string Preset, int? Radius, int? Border, string Pos, bool ReorderOn);

    public event Action<TableMenuInfo>? MenuRequested;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);
        var p = parameters.Value;
        double x = p.TryGetProperty("x", out var xe) ? xe.GetDouble() : 0;
        double y = p.TryGetProperty("y", out var ye) ? ye.GetDouble() : 0;
        bool inHeader = p.TryGetProperty("inHeader", out var he) && he.GetBoolean();
        int rows = p.TryGetProperty("rows", out var re) && re.ValueKind == JsonValueKind.Number ? re.GetInt32() : 0;
        int cols = p.TryGetProperty("cols", out var ce) && ce.ValueKind == JsonValueKind.Number ? ce.GetInt32() : 0;
        string preset = p.TryGetProperty("preset", out var pe) ? pe.GetString() ?? "" : "";
        int? radius = p.TryGetProperty("radius", out var rde) && rde.ValueKind == JsonValueKind.Number ? rde.GetInt32() : null;
        int? border = p.TryGetProperty("border", out var bde) && bde.ValueKind == JsonValueKind.Number ? bde.GetInt32() : null;
        string pos = p.TryGetProperty("pos", out var poe) ? poe.GetString() ?? "left" : "left";
        bool reorderOn = p.TryGetProperty("reorderOn", out var roe) && roe.ValueKind == JsonValueKind.True;
        MenuRequested?.Invoke(new TableMenuInfo(x, y, inHeader, rows, cols, preset, radius, border, pos, reorderOn));
        return Task.FromResult<JsonElement?>(null);
    }
}
