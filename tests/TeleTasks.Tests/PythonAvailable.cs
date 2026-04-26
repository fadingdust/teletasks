using System.Diagnostics;

namespace TeleTasks.Tests;

/// <summary>
/// Lazy probe for `python3` on PATH. ArgparsePythonDetector shells out to
/// run an AST-walking helper script, so tests that exercise it depend on a
/// working Python interpreter. CI hosts without Python should see the
/// tests skipped rather than failed.
/// </summary>
internal static class PythonAvailable
{
    public static bool Value => _value.Value;

    private static readonly Lazy<bool> _value = new(Probe);

    private static bool Probe()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "-c \"import sys; sys.exit(0)\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return true;
            }
            catch { }
        }
        return false;
    }
}
