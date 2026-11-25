using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinPerfOptimizer.App.ViewModels;
using WinPerfOptimizer.Core.Interfaces;
using WinPerfOptimizer.Core.Services;
using WinPerfOptimizer.Infrastructure.Probes;
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
        services.AddSingleton<ISystemRestoreService, SystemRestoreAdapter>();
        services.AddSingleton<IDriverUpdateDiscoveryService, WindowsUpdateDriverDiscoveryService>();
        services.AddSingleton<IDriverProbe, PnPDriverProbe>();
        services.AddSingleton<IDriverDeploymentService, DriverDeploymentEngine>();
        services.AddSingleton<IBatteryProbe, PowerCfgBatteryProbe>();
        services.AddSingleton<IMemoryProbe, MemoryRevisionProbe>();
        services.AddSingleton<IOptionalThermalSensorProvider, OptionalThermalSensorProvider>();
        services.AddSingleton<ICpuTelemetryProbe, CpuRevisionProbe>();
        services.AddSingleton<IStorageSmartProbe, StorageSmartProbe>();
        services.AddSingleton<IGpuRevisionProbe, GpuRevisionProbe>();
        services.AddSingleton<IRecommendationEngine, RecommendationEngine>();
        services.AddSingleton<IRevisionStore, JsonRevisionStore>();
        services.AddSingleton<IRevisionReportFormatter, RevisionReportFormatter>();
        services.AddSingleton<ElevatedWorkerHost>();
        services.AddSingleton<IElevatedExecutor>(sp => sp.GetRequiredService<ElevatedWorkerHost>());
        services.AddSingleton<ISystemRevisionOrchestrator, SystemRevisionOrchestrator>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (e.Args.Length >= 3 && e.Args[0] == "--elevated-worker" && e.Args[1] == "--request")
        {
            var sha = (e.Args.Length >= 5 && e.Args[3] == "--sha256") ? e.Args[4] : null;
            try
            {
                var worker = _serviceProvider.GetRequiredService<ElevatedWorkerHost>();
                var code = await worker.RunHeadlessCliAsync(e.Args[2], sha);
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
