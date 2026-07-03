using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Mica.IPC;
using Mica.IPC.Handlers;
using Mica.Services;
using Serilog;

namespace Mica;

public partial class App : Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = BuildServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static IServiceProvider BuildServices()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/mica-.log", rollingInterval: RollingInterval.Day)
            .WriteTo.Debug()
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSerilog();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<NotebookMaintenanceService>();
        services.AddSingleton<IpcRouter>();
        services.AddTransient<NoteSaveHandler>();
        services.AddTransient<ImageSaveHandler>();
        services.AddTransient<ThemeUpdateHandler>();
        services.AddTransient<LogHandler>();
        services.AddSingleton<EditorStatsHandler>();
        services.AddSingleton<EditorZoomHandler>();
        services.AddSingleton<OutlineHandler>();
        services.AddSingleton<ShortcutHandler>();
        services.AddTransient<OpenExternalHandler>();
        services.AddSingleton<TableMenuHandler>();
        services.AddSingleton<ImageMenuHandler>();
        services.AddSingleton<ImageCropHandler>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
