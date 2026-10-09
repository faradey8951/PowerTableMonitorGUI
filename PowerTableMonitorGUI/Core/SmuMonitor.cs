using System;
using System.Threading;
using System.Threading.Tasks;
using ZenStates.Core;
using PowerTableMonitorGUI.Telemetry;

namespace PowerTableMonitorGUI.Core;

public sealed class SmuMonitor : IDisposable
{
    private Cpu? _cpu;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private readonly object _sync = new();

    public string CpuName { get; private set; } = "—";
    public string CodeName { get; private set; } = "—";
    public int TableSize { get; private set; }
    public long DramBaseAddress { get; private set; }
    public int IntervalMs { get; set; } = 500;

    public event EventHandler<TelemetrySample>? SampleReady;
    public event EventHandler<Exception>? Error;

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning)
                return;

            _cpu = new Cpu();
            CpuName = _cpu.info.cpuName ?? "Unknown";
            CodeName = _cpu.info.codeName.ToString();
            TableSize = _cpu.powerTable.TableSize;
            DramBaseAddress = _cpu.powerTable.DramBaseAddress;

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => MonitorLoop(_cts.Token));
        }
    }

    private async Task MonitorLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Cpu? cpu;
                lock (_sync)
                    cpu = _cpu;

                if (cpu == null)
                    break;

                cpu.powerTable.Refresh();

                float[] source = cpu.powerTable.Table;
                if (source == null || source.Length == 0)
                    throw new InvalidOperationException("Power Table is empty.");

                var values = new float[source.Length];
                Array.Copy(source, values, source.Length);

                var sample = new TelemetrySample(
                    DateTime.Now,
                    values,
                    cpu.powerTable.TableSize,
                    cpu.powerTable.DramBaseAddress);

                SampleReady?.Invoke(this, sample);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }

            try
            {
                await Task.Delay(IntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;

        lock (_sync)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        if (cts == null)
            return;

        cts.Cancel();

        try
        {
            worker?.Wait(1500);
        }
        catch
        {
            // Shutdown path: don't let a worker exception prevent UI closing.
        }

        cts.Dispose();

        lock (_sync)
        {
            _cpu?.Dispose();
            _cpu = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
