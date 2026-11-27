using System.ComponentModel;
using System.Diagnostics;

namespace LanChat.WinForms;

internal sealed class ProcessCpuLimit(Process process) : IDisposable
{
    private IntPtr? _previousAffinity;

    public void Apply(bool enabled)
    {
        if (enabled == _previousAffinity.HasValue) return;

        try
        {
            if (enabled)
            {
                process.Refresh();
                var previous = process.ProcessorAffinity;
                var mask = previous.ToInt64();
                if (mask == 0)
                {
                    Trace.TraceWarning("Windows returned an empty processor affinity mask for LanChat.");
                    return;
                }
                // Pick a processor already allowed by Windows or the process launcher.
                process.ProcessorAffinity = new IntPtr(mask & unchecked(-mask));
                _previousAffinity = previous;
            }
            else
            {
                process.ProcessorAffinity = _previousAffinity!.Value;
                _previousAffinity = null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Could not change LanChat processor affinity: {0}", ex.Message);
        }
    }

    // Restore before Program launches an installer or relaunches LanChat.
    public void Dispose() => Apply(false);
}
