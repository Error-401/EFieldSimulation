using EFieldSimulation.Compute;
using EFieldSimulation.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace EFieldSimulation;

public partial class MainWindow : System.Windows.Window
{
    private MainViewModel mainViewModel = new MainViewModel();
    public MainWindow()
    {
        DataContext = mainViewModel;

        var gpu = GpuComputeBackend.TryCreate(
            allowCpuFallback: false,
            log: msg => Console.WriteLine($"[Compute] {msg}"));

        ComputeBackend.Default = gpu is { IsRealGpu: true }
            ? gpu
            : new CpuComputeBackend();

    }
}