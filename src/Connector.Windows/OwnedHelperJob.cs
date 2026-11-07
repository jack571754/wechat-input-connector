using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Connector.Windows;

public sealed class OwnedHelperJob : IDisposable
{
    private nint _job;
    public OwnedHelperJob()
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { Dispose(); throw new Win32Exception(); }
    }
    public void Attach(Process process)
    {
        if (_job == 0 || !AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception();
    }
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _job, 0);
        if (handle != 0) CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinWorkingSet, MaxWorkingSet;
        internal uint ActiveProcesses;
        internal nuint Affinity;
        internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
