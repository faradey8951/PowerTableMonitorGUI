using System;

namespace PowerTableMonitorGUI.Telemetry;

public sealed class TelemetrySample
{
    public DateTime Timestamp { get; }
    public float[] Values { get; }
    public uint[] RawValues { get; }
    public int TableSize { get; }
    public long DramBaseAddress { get; }

    public TelemetrySample(DateTime timestamp, float[] values, int tableSize, long dramBaseAddress)
    {
        Timestamp = timestamp;
        Values = values;
        RawValues = new uint[values.Length];
        for (int i = 0; i < values.Length; i++)
            RawValues[i] = BitConverter.SingleToUInt32Bits(values[i]);

        TableSize = tableSize;
        DramBaseAddress = dramBaseAddress;
    }
}
