using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerTableMonitorGUI.Telemetry;

public sealed class TelemetryRecorder : IDisposable
{
    private StreamWriter? _writer;

    public bool IsRecording => _writer != null;
    public string? FilePath { get; private set; }
    public long SampleCount { get; private set; }

    public void Start(string directory)
    {
        Stop();

        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(
            directory,
            $"telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        _writer = new StreamWriter(
            FilePath,
            false,
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        SampleCount = 0;
    }

    public void Write(TelemetrySample sample)
    {
        if (_writer == null)
            return;

        if (SampleCount == 0)
        {
            _writer.WriteLine("# PowerTableMonitorGUI telemetry");
            _writer.WriteLine($"# Started: {sample.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            _writer.WriteLine($"# Table size: {sample.TableSize} bytes");
            _writer.WriteLine($"# DRAM base: 0x{sample.DramBaseAddress:X}");
            _writer.WriteLine("# Values are IEEE-754 single precision; HEX is the raw uint32 representation.");
            _writer.Write("Timestamp\t");
            for (int i = 0; i < sample.Values.Length; i++)
                _writer.Write($"0x{i * 4:X3}\t");
            _writer.WriteLine();

            _writer.Write("FLOAT\t");
            for (int i = 0; i < sample.Values.Length; i++)
                _writer.Write($"{sample.Values[i].ToString("F9", CultureInfo.InvariantCulture)}\t");
            _writer.WriteLine();
        }
        else
        {
            _writer.Write("FLOAT\t");
            for (int i = 0; i < sample.Values.Length; i++)
                _writer.Write($"{sample.Values[i].ToString("F9", CultureInfo.InvariantCulture)}\t");
            _writer.WriteLine();
        }

        _writer.Write("HEX\t");
        for (int i = 0; i < sample.RawValues.Length; i++)
            _writer.Write($"{sample.RawValues[i]:X8}\t");
        _writer.WriteLine();

        SampleCount++;
    }

    public void Stop()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Stop();
}
