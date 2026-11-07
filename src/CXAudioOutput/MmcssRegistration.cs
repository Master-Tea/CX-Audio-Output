using System.Runtime.InteropServices;

namespace CXAudioOutput;

/// <summary>
/// Registers the capture thread with the Windows Multimedia Class Scheduler
/// (MMCSS). This gives the WASAPI packet pump a small scheduling advantage,
/// which reduces packet jitter under UI/network load.
/// </summary>
internal sealed class MmcssRegistration : IDisposable
{
    private IntPtr _handle;

    private MmcssRegistration(IntPtr handle)
    {
        _handle = handle;
    }

    public static MmcssRegistration? TryRegister(string taskName)
    {
        try
        {
            var handle = AvSetMmThreadCharacteristics(taskName, out _);
            return handle == IntPtr.Zero ? null : new MmcssRegistration(handle);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            AvRevertMmThreadCharacteristics(_handle);
        }
        catch
        {
            // ignored
        }

        _handle = IntPtr.Zero;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
