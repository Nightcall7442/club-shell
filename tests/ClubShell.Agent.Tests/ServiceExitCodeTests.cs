using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClubShell.Agent.Tests;

/// <summary>A crashed worker must stop the service with a failure code, or the SCM never restarts it.</summary>
public sealed class ServiceExitCodeTests
{
    [Fact]
    public async Task A_faulted_background_service_gives_exit_code_1()
    {
        try
        {
            using IHost host = new HostBuilder().ConfigureServices(s => s.AddHostedService<Faulting>()).Build();
            Func<int> exitCode = ServiceExitCode.Attach(host);
            await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
            exitCode().Should().Be(1);
            Environment.ExitCode.Should().Be(1);
        }
        finally
        {
            Environment.ExitCode = 0;
        }
    }

    [Fact]
    public async Task A_normal_stop_gives_exit_code_0()
    {
        using IHost host = new HostBuilder().ConfigureServices(s => s.AddHostedService<StopsTheHost>()).Build();
        Func<int> exitCode = ServiceExitCode.Attach(host);
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        exitCode().Should().Be(0);
    }

    private sealed class Faulting : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Not Task.Yield: a fault that lands before BackgroundService.StartAsync returns makes Host.StartAsync throw
            // instead (a race the busy test machine hit), and the case here is a worker that crashes while running.
            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            throw new InvalidOperationException("worker crashed");
        }
    }

    private sealed class StopsTheHost(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            lifetime.StopApplication();
        }
    }
}
