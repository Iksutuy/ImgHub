using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Imgagent.App.Services;
using Imgagent.App.ViewModels;
using Imgagent.App.Views;
using Imgagent.Core;
using Imgagent.Core.Http;
using Imgagent.Core.Services;
using Imgagent.Core.Storage;

namespace Imgagent.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var home = AppPaths.ResolveHome();
        var session = new Session(home);
        var http = new HttpJsonClient();

        var services = new AppServices
        {
            Session = session,
            Http = http,
            ImageApi = new ImageApi(session.Provider, http, null),
            Polish = new PolishService(http),
            Storage = new PlatformStorage(),
            Platform = new PlatformInfo(),
        };
        services.Polish.SwitchOn = session.Config.PolishEnabled;
        services.Polish.BaseUrl = session.Config.PolishBaseUrl;
        services.Polish.Model = session.Config.PolishModel;
        services.Polish.ApiKey = session.PolishKey;

        var vm = new MainViewModel(services);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = vm };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView { DataContext = vm };

        base.OnFrameworkInitializationCompleted();
    }
}