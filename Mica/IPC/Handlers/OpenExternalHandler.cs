using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Mica.IPC.Handlers;

// 用系统默认程序打开外部链接（host.openExternal {url}）。由 web 端 Ctrl/Cmd+点击链接触发：
// 编辑器内 `#锚点` 在 web 侧自行滚动，外链（http/https/mailto）则转发到这里、用默认浏览器/邮件客户端打开。
// **安全**：只放行 http/https/mailto——绝不把任意字符串交给 ShellExecute（防 `file:`/自定义协议/本地可执行
// 被当命令执行）。URL 来自用户自己的笔记，但仍按白名单校验，避免点一下链接就跑别的程序。
public class OpenExternalHandler : IIpcHandler
{
    public string Method => "host.openExternal";

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is { } p
            && p.TryGetProperty("url", out var urlEl)
            && urlEl.GetString() is { Length: > 0 } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme is "http" or "https" or "mailto"))
            {
                try
                {
                    // UseShellExecute=true → 交给系统默认浏览器/邮件客户端打开。
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "打开外部链接失败: {Url}", url);
                }
            }
            else
            {
                Log.Warning("拒绝打开非白名单链接: {Url}", url);
            }
        }

        return Task.FromResult<JsonElement?>(null);
    }
}
