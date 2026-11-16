using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace ResurgeLauncherCSharp.Services
{
    public static class GscInjector
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, int flAllocationType, int flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int PROCESS_WM_READ = 0x0010;
        private const int PROCESS_VM_WRITE = 0x0020;
        private const int PROCESS_VM_OPERATION = 0x0008;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int MEM_COMMIT = 0x1000;
        private const int MEM_RESERVE = 0x2000;
        private const int PAGE_READWRITE = 0x04;

        public static string Inject(string gamePath, string gscPath)
        {
            string executable = Path.GetFullPath(Path.Combine(gamePath, "Resurge - Beta.exe"));
            using var targetProcess = Process.GetProcessesByName("Resurge - Beta").FirstOrDefault(process =>
            {
                try { return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase); }
                catch (System.ComponentModel.Win32Exception) { return false; }
                catch (InvalidOperationException) { return false; }
            });

            if (targetProcess is null)
                return "Error: Start the Resurge Beta client in the selected game folder first.";
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_WM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, targetProcess.Id);

            if (hProcess == IntPtr.Zero)
            {
                return "Error: Failed to open game process. Make sure to run as Administrator.";
            }

            try
            {
                if (targetProcess.MainModule is not { } module)
                    return "Error: Resurge exited before injection.";
                IntPtr baseAddr = module.BaseAddress;
                
                // Assume MS Store offset
                long assetPoolOffset = baseAddr.ToInt64() + 0xF3B1330; 
                
                int entriesCount = 156672;
                int structSize = 32;
                int bufferSize = entriesCount * structSize;
                byte[] arrayBuffer = new byte[bufferSize];

                if (!ReadProcessMemory(hProcess, (IntPtr)assetPoolOffset, arrayBuffer, bufferSize, out _))
                {
                    return $"Error: Failed to read process memory at asset pool offset (0x{assetPoolOffset:X}).";
                }

                long headerPointer = 0;

                for (int i = 0; i < entriesCount; i++)
                {
                    int type = BitConverter.ToInt32(arrayBuffer, i * structSize);
                    if (type == 54) // Script type
                    {
                        long pHeader = BitConverter.ToInt64(arrayBuffer, i * structSize + 8);
                        if (pHeader != 0)
                        {
                            byte[] ptrBuf = new byte[8];
                            if (ReadProcessMemory(hProcess, (IntPtr)pHeader, ptrBuf, 8, out _))
                            {
                                long namePtr = BitConverter.ToInt64(ptrBuf, 0);
                                if (namePtr != 0)
                                {
                                    byte[] strBuf = new byte[256];
                                    if (ReadProcessMemory(hProcess, (IntPtr)namePtr, strBuf, 256, out _))
                                    {
                                        string scriptName = Encoding.UTF8.GetString(strBuf).TrimEnd('\0');
                                        if (scriptName.StartsWith("scripts/shared/duplicaterender_mgr.gsc"))
                                        {
                                            headerPointer = pHeader;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (headerPointer == 0)
                {
                    return "Error: Target script 'duplicaterender_mgr.gsc' not found in memory. Make sure you are in the main menu.";
                }

                long targetMemAddr = headerPointer + 16;
                
                byte[] gscData = File.ReadAllBytes(gscPath);
                if (gscData.Length < 6 || gscData[4] != 13 || gscData[5] != 10)
                {
                    return "Error: Invalid compiled GSC file format (magic bytes mismatch).";
                }

                IntPtr allocatedMem = VirtualAllocEx(hProcess, IntPtr.Zero, gscData.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (allocatedMem == IntPtr.Zero)
                {
                    return "Error: Failed to allocate memory in target process.";
                }

                if (!WriteProcessMemory(hProcess, allocatedMem, gscData, gscData.Length, out _))
                {
                    return "Error: Failed to write GSC data to target process.";
                }

                byte[] newPtrBuf = BitConverter.GetBytes(allocatedMem.ToInt64());
                if (!WriteProcessMemory(hProcess, (IntPtr)targetMemAddr, newPtrBuf, 8, out _))
                {
                    return "Error: Failed to write new script pointer to target process.";
                }

                return $"Successfully injected {Path.GetFileName(gscPath)}!";
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
    }
}
