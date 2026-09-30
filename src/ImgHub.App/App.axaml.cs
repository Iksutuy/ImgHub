using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ImgHub.App.Services;
using ImgHub.App.ViewModels;
using ImgHub.App.Views;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Http;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var home = AppPaths.ResolveHome();

        // 用户要求（v5.24.0）：分级日志落盘到 <数据目录>/log，供事后诊断。
        // 必须在其它组件之前初始化 —— 后续 IO/网络失败才有地方记录原因与位置。
        AppLog.Init(home);
        AppLog.Info("应用启动", where: "App.OnFrameworkInitializationCompleted",
                    detail: $"数据目录：{home}");

        var session = new Session(home);
        var http = new HttpJsonClient();

        var services = new AppServices
        {
            Session = session,
            Http = http,
            ImageApi = new ImageApi(session.Provider, http, session.BaseUrlFor(session.Provider)),
            Polish = new PolishService(http),
            Storage = new PlatformStorage(),
            Platform = new PlatformInfo(),
            ModelStats = new ModelStatsService(home),
            // v5.26.0：未完成任务落盘（提交即记 → 程序意外退出也能找回图片）
            Pending = new PendingTaskStore(home),
        };
        services.Polish.SwitchOn = session.Config.PolishEnabled;
        services.Polish.BaseUrl = session.Config.PolishBaseUrl;
        services.Polish.Model = session.Config.PolishModel;
        services.Polish.ApiKey = session.PolishKey;
        // v0.5.31：润色提示词风格（auto 时跟随生图 provider，见 PolishService.EffectiveStyle）
        services.Polish.Style = session.Config.PolishStyle;
        services.Polish.ActiveStyle = session.Provider;

        MainViewModel vm;
        try
        {
            vm = new MainViewModel(services);
        }
        catch (Exception ex)
        {
            // VM 构造失败是致命错误：必须留下完整原因，而不是"窗口一闪就没了"
            AppLog.Error("MainViewModel 构造失败（应用无法启动）",
                         "App.OnFrameworkInitializationCompleted", ex: ex);
            throw;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // P0-2：退出时释放非托管位图（缩略图 / 预览图），不靠 GC finalizer
            desktop.Exit += (_, _) => vm.Dispose();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView { DataContext = vm };

        base.OnFrameworkInitializationCompleted();
    }
}