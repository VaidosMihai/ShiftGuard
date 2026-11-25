using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinPerfOptimizer.App.ViewModels;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Infrastructure.Services;

namespace WinPerfOptimizer.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<IPowerPlanService, PowerCfgRunner>();
        services.AddSingleton<IRegistryService, RegistryAdapter>();
        services.AddSingleton<IHardwareProbe, HardwareProbe>();
        services.AddSingleton<IProcessInspector, ProcessInspector>();
        services.AddSingleton<IBackupEngine, BackupRollbackEngine>();
        services.AddSingleton<ElevatedWorkerHost>();
        services.AddSingleton<IElevatedExecutor>(sp => sp.GetRequiredService<ElevatedWorkerHost>());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (e.Args.Length == 3 && e.Args[0] == "--elevated-worker" && e.Args[1] == "--request")
        {
            try
            {
                var worker = _serviceProvider.GetRequiredService<ElevatedWorkerHost>();
                var code = await worker.RunHeadlessCliAsync(e.Args[2]);
                Shutdown(code);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var window = _serviceProvider.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
