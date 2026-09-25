using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.TimedShutdown.Services;
using ClassIsland.TimedShutdown.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.TimedShutdown;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 正常情况下 PluginConfigFolder 会由 ClassIsland 设置。这里仅增加一个
        // 兜底目录，避免开发调试时因为目录尚未准备好而无法启动插件。
        var configFolder = string.IsNullOrWhiteSpace(PluginConfigFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassIsland",
                "TimedShutdown")
            : PluginConfigFolder;
        Directory.CreateDirectory(configFolder);

        services.AddSingleton(provider => new ShutdownTaskService(
            provider.GetRequiredService<IExactTimeService>(),
            provider.GetRequiredService<ILogger<ShutdownTaskService>>(),
            Path.Combine(configFolder, "Tasks.json")));

        // 让调度器随 ClassIsland 主机启动，即使用户没有打开插件设置页也会工作。
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ShutdownTaskService>());

        services.AddSettingsPage<TimedShutdownSettingsPage>();
    }
}
