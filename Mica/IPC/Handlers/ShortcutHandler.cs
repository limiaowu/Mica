using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mica.IPC.Handlers;

// 接收 web 端转发的 app 级快捷键（host.shortcut {action}）。编辑器（WebView2）持有焦点时，
// 顶部菜单的 XAML KeyboardAccelerator 收不到按键，故这些 app 级快捷键（保存/关标签/新建/打开
// 文件夹/侧栏/源码/全屏）改由 web 端 keydown 捕获后转发到这里。单例注册，保证订阅的实例就是
// 路由分发的实例。
public class ShortcutHandler : IIpcHandler
{
    public string Method => "host.shortcut";

    public event Action<string>? ShortcutInvoked;

    public Task<JsonElement?> HandleAsync(JsonElement? parameters)
    {
        if (parameters is null) return Task.FromResult<JsonElement?>(null);

        if (parameters.Value.TryGetProperty("action", out var actionEl)
            && actionEl.GetString() is { } action)
        {
            ShortcutInvoked?.Invoke(action);
        }

        return Task.FromResult<JsonElement?>(null);
    }
}
