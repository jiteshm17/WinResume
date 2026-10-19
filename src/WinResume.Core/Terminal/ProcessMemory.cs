using System.Runtime.InteropServices;
using System.Text;

namespace WinResume.Core.Terminal;

/// <summary>
/// Reads a few named facts out of another process of the same user: its current directory and
/// selected environment variables. Only the variable names the caller asks for are returned;
/// the rest of the environment is never kept.
/// </summary>
internal static class ProcessMemory
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;

    // x64 layout of the PEB and RTL_USER_PROCESS_PARAMETERS (stable since Windows 8).
    const int PebProcessParameters = 0x20;
    const int ParamsCurrentDirectory = 0x38; // UNICODE_STRING DosPath
    const int ParamsEnvironment = 0x80;
    const int ParamsEnvironmentSize = 0x3F0;
    const int MaxEnvironmentBytes = 256 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public sealed record Facts(string? CurrentDirectory, Dictionary<string, string> Variables);

    public static Facts? Read(uint pid, IReadOnlyCollection<string> wantedVariables)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;

            var parameters = ReadPointer(h, pbi.PebBaseAddress + PebProcessParameters);
            if (parameters == IntPtr.Zero) return null;

            var cwd = ReadUnicodeString(h, parameters + ParamsCurrentDirectory);
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var env = ReadPointer(h, parameters + ParamsEnvironment);
            var size = (int)Math.Min(ReadPointer(h, parameters + ParamsEnvironmentSize).ToInt64(), MaxEnvironmentBytes);
            if (env != IntPtr.Zero && size > 0)
            {
                var block = new byte[size];
                if (ReadProcessMemory(h, env, block, size, out _))
                {
                    foreach (var entry in Encoding.Unicode.GetString(block).Split('\0'))
                    {
                        if (entry.Length == 0) break; // end of block
                        var eq = entry.IndexOf('=', 1); // index 1: skips entries like "=C:=C:\..."
                        if (eq < 0) continue;
                        var name = entry[..eq];
                        if (wantedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)) vars[name] = entry[(eq + 1)..];
                    }
                }
            }
            return new Facts(cwd, vars);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    static IntPtr ReadPointer(IntPtr h, IntPtr address)
    {
        var buffer = new byte[8];
        return ReadProcessMemory(h, address, buffer, 8, out _) ? (IntPtr)BitConverter.ToInt64(buffer) : IntPtr.Zero;
    }

    static string? ReadUnicodeString(IntPtr h, IntPtr address)
    {
        var header = new byte[16]; // UNICODE_STRING: Length(2) MaximumLength(2) pad(4) Buffer(8)
        if (!ReadProcessMemory(h, address, header, 16, out _)) return null;
        int length = BitConverter.ToUInt16(header, 0);
        var buffer = (IntPtr)BitConverter.ToInt64(header, 8);
        if (length == 0 || buffer == IntPtr.Zero) return null;
        var data = new byte[length];
        return ReadProcessMemory(h, buffer, data, length, out _) ? Encoding.Unicode.GetString(data) : null;
    }
}
