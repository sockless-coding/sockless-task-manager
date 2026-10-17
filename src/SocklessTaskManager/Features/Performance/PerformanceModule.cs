using Sockless.TaskManager.Core;
using Sockless.TaskManager.Features.Performance.Cpu;
using Sockless.TaskManager.Features.Performance.Disk;
using Sockless.TaskManager.Features.Performance.Gpu;
using Sockless.TaskManager.Features.Performance.Memory;
using Sockless.TaskManager.Features.Performance.Network;

namespace Sockless.TaskManager.Features.Performance;

/// <summary>
/// Composition for the Performance slice. To add a device kind (battery, NPU, sensors…),
/// create <c>Features/Performance/&lt;Kind&gt;/</c> with a <see cref="PerformanceDevice"/> and a
/// static <see cref="DeviceScanner"/>, then add the scanner here. List order is display order.
/// </summary>
public static class PerformanceModule
{
    public static PerformanceFeature Create(SampleClock clock) => new(clock,
    [
        CpuDevice.Scan,
        MemoryDevice.Scan,
        DiskDevice.Scan,
        NetworkDevice.Scan,
        GpuDevice.Scan,
    ]);
}
