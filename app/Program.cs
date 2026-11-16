using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Simple_PC_GSC_Injector;

internal class Program
{
	public struct ScriptParseTree
	{
		public unsafe char* name;

		public int len;

		public int unk;

		public unsafe char* buffer;
	}

	public struct XAssetEntry
	{
		public int type;

		public int unk1;

		public unsafe long* HeaderPointer;

		public byte zoneindex;

		public byte inuse;

		public short unk2;

		public int nextHash;

		public int nextOverride;

		public int unk3;
	}

	private const int PROCESS_WM_READ = 16;

	private const int PROCESS_VM_WRITE = 32;

	private const int PROCESS_VM_OPERATION = 8;

	private const uint MEM_COMMIT = 4096u;

	private const uint MEM_RESERVE = 8192u;

	private const uint PAGE_READWRITE = 4u;

	private IntPtr processBAddr = (IntPtr)0;

	private IntPtr processHandle = (IntPtr)0;

	private IntPtr memIntPtr;

	[STAThread]
	private static void Main(string[] args)
	{
		Console.WriteLine("=== Spet01 T7 MS Store Compiler ===");
		Console.WriteLine("Enhanced version for MS Store\n");

		if (args.Length != 0)
		{
			new Program().InjectScript(args[0]);
			return;
		}
		
		// First, let's check if we can find the process
		Program program = new Program();
		if (!program.UpdateProcessInfo())
		{
			Console.WriteLine("\nDo you want to see all running processes to manually find Call of Duty? (y/n)");
			ConsoleKeyInfo key = Console.ReadKey();
			if (key.KeyChar == 'y' || key.KeyChar == 'Y')
			{
				program.ListAllProcesses();
			}
			return;
		}

		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "Compiled GSC File|*.gsc"
		};
		if (openFileDialog.ShowDialog() != DialogResult.OK)
		{
			return;
		}
		using (openFileDialog)
		{
			program.InjectScript(openFileDialog.FileName);
		}
	}

	[DllImport("kernel32.dll")]
	public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll")]
	public static extern bool ReadProcessMemory(int hProcess, long lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

	[DllImport("kernel32.dll")]
	public unsafe static extern bool ReadProcessMemory(int hProcess, long lpBaseAddress, byte* lpBuffer, int dwSize, out int lpNumberOfBytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private unsafe static extern bool WriteProcessMemory(int hProcess, long lpBaseAddress, byte** lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

	[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
	private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

	private byte[] ReadBytes(long address, int numBytes)
	{
		byte[] array = new byte[numBytes];
		ReadProcessMemory((int)processHandle, address, array, array.Length, out var _);
		return array;
	}

	private unsafe T[] ReadArrayUnsafe<T>(long address, int count) where T : unmanaged
	{
		byte[] array = ReadBytes(address, count * sizeof(T));
		T[] array2 = new T[count];
		fixed (byte* source = array)
		{
			fixed (T* destination = array2)
			{
				Buffer.MemoryCopy(source, destination, array.Length, array.Length);
			}
		}
		return array2;
	}

	private unsafe string ReadNullTerminatedString(long address, int bufferSize = 512)
	{
		byte* ptr = stackalloc byte[(int)(uint)bufferSize];
		ReadProcessMemory((int)processHandle, address, ptr, bufferSize, out var _);
		int i;
		for (i = 0; i < bufferSize && ptr[i] != 0; i++)
		{
		}
		return Encoding.ASCII.GetString(ptr, i);
	}

	private bool UpdateProcessInfo()
	{
		// List possible process names for different game versions (exact matches first)
		string[] possibleProcessNames = {
			"Resurge", //Compatible with Resurge
			"resurge",
			"BlackOps3",
			"BlackOps3MS",           // Legacy MS
			"CallofDuty_BlackOps3_x64",
			"t7_sp64_ship",
			"t7_mp64_ship",
			"BlackOps3_x64",
			"cod_blackops3"
		};

		// Try exact names first
		foreach (string processName in possibleProcessNames)
		{
			try
			{
				Process[] processes = Process.GetProcessesByName(processName);
				if (processes.Length > 0)
				{
					Process process = processes[0];
					Console.WriteLine($"Process found: {process.ProcessName} (PID: {process.Id})");
					processBAddr = process.MainModule.BaseAddress;
					processHandle = OpenProcess(56, bInheritHandle: true, process.Id);
					Console.WriteLine($"Base Address: 0x{processBAddr.ToInt64():X}");
					return true;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error trying to access process {processName}: {ex.Message}");
			}
		}

		// Fallback: find related processes by substring and ask the user for confirmation per candidate
		Console.WriteLine("\nSearching for Call of Duty related processes (fallback)...");
		Process[] allProcesses = Process.GetProcesses();
		List<Process> related = new List<Process>();

		foreach (Process proc in allProcesses)
		{
			try
			{
				string procName = proc.ProcessName.ToLower();
				if (procName.Contains("blackops") || procName.Contains("callofduty") ||
					procName.Contains("cod") || procName.Contains("t7_") || procName.Contains("resurge"))
				{
					related.Add(proc);
				}
			}
			catch
			{
				// Ignore processes we can't access
			}
		}

		if (related.Count == 0)
		{
			Console.WriteLine("No Call of Duty related processes found.");
			return false;
		}

		// Iterate through related candidates and ask the user to confirm each one
		foreach (Process candidate in related)
		{
			try
			{
				Console.WriteLine($"\nCandidate found: {candidate.ProcessName} (PID: {candidate.Id})");
				Console.Write("This is the correct EXE? Inject? (Y/N): ");
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);
				Console.WriteLine(key.KeyChar);
				if (key.KeyChar == 'y' || key.KeyChar == 'Y')
				{
					try
					{
						processBAddr = candidate.MainModule.BaseAddress;
						processHandle = OpenProcess(56, bInheritHandle: true, candidate.Id);
						Console.WriteLine($"Selected: {candidate.ProcessName} (PID: {candidate.Id})");
						Console.WriteLine($"Base Address: 0x{processBAddr.ToInt64():X}");
						return true;
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error opening process {candidate.ProcessName}: {ex.Message}");
						// continue to next candidate
					}
				}
				// User said no: continue to next candidate
			}
			catch
			{
				// If something goes wrong with this candidate, continue to next one
			}
		}

		// If we get here, no candidate was confirmed
		Console.WriteLine("\nNo candidate was confirmed by the user.");
		return false;
	}

	private void ListAllProcesses()
	{
		Console.WriteLine("\n=== ALL RUNNING PROCESSES ===");
		Process[] allProcesses = Process.GetProcesses();
		Array.Sort(allProcesses, (p1, p2) => string.Compare(p1.ProcessName, p2.ProcessName, StringComparison.OrdinalIgnoreCase));
		
		Console.WriteLine("Look for processes that might be Call of Duty:\n");
		
		foreach (Process proc in allProcesses)
		{
			try
			{
				Console.WriteLine($"{proc.ProcessName} (PID: {proc.Id})");
			}
			catch
			{
				// Some processes might not be accessible
			}
		}
		
		Console.WriteLine("\nPress any key to continue...");
		Console.ReadKey();
	}

	public unsafe void InjectScript(string GSCPath)
	{
		Console.WriteLine("Searching for Call of Duty: Black Ops III process...");
		
		if (!UpdateProcessInfo())
		{
			Console.WriteLine("\n=== ERROR ===");
			Console.WriteLine("Call of Duty: Black Ops III is not running or was not detected!");
			Console.WriteLine("\nPossible solutions:");
			Console.WriteLine("1. Make sure the game is running");
			Console.WriteLine("2. Run this injector as Administrator");
			Console.WriteLine("3. If it's the MS Store version, the process name might be different");
			Console.WriteLine("\nPress any key to exit...");
			Console.ReadKey();
			return;
		}
		
		// Detectar se é versão MS Store vs Steam baseado no processo
		bool isWindowsStore = false;
		string processName = "";
		
		try 
		{
			// Obter o nome do processo atual através dos processos em execução
			Process[] processes = Process.GetProcesses();
			foreach (Process proc in processes)
			{
				try
				{
					if (proc.MainModule?.BaseAddress == processBAddr)
					{
						processName = proc.ProcessName;
						break;
					}
				}
				catch { } // Ignorar erros de acesso
			}
		}
		catch 
		{
			// Fallback: usar MS Store como padrão para este build
			processName = "BlackOps3MS";
		}
		
		if (string.IsNullOrEmpty(processName))
		{
			processName = "BlackOps3MS"; // Default para MS Store
		}
		
		isWindowsStore = processName.Contains("MS") || processName.Contains("CallofDuty") || 
		                processName.Contains("_x64") || processName.Contains("t7_") ||
		                processName.ToLower().Contains("resurge");
		
		Console.WriteLine($"Detected process: {processName}");
		
		// Updated offsets based on shiversoftdev/t7-compiler
		long assetPoolOffset;
		if (isWindowsStore)
		{
			// MS Store offset
			assetPoolOffset = (long)processBAddr + 0xF3B1330;
			Console.WriteLine("Detected: Call of Duty Black Ops III (MS Store)");
		}
		else
		{
			// Steam offset  
			assetPoolOffset = (long)processBAddr + 0x9407AB0;
			Console.WriteLine("Detected: Call of Duty Black Ops III (Steam)");
		}
		
		Console.WriteLine($"Using Asset Pool Offset: 0x{assetPoolOffset:X}");
		long address = assetPoolOffset;
		XAssetEntry[] array = ReadArrayUnsafe<XAssetEntry>(address, 156672);
		long num = 0L;
		
		Console.WriteLine($"Searching for 'scripts/shared/duplicaterender_mgr.gsc' in {array.Length} assets...");
		
		// Primeira tentativa: procurar o script padrão
		XAssetEntry[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			XAssetEntry xAssetEntry = array2[i];
			if (xAssetEntry.type == 54 && xAssetEntry.HeaderPointer != null)
			{
				try
				{
					string scriptName = ReadNullTerminatedString(BitConverter.ToInt64(ReadBytes((long)xAssetEntry.HeaderPointer, 8), 0));
					if (scriptName.Equals("scripts/shared/duplicaterender_mgr.gsc"))
					{
						num = (long)xAssetEntry.HeaderPointer;
						Console.WriteLine($"Target script found at index {i}!");
						break;
					}
				}
				catch 
				{
					// Ignore read errors
				}
			}
		}
		
		// If not found, try to find any valid script for debugging
		if (num == 0L)
		{
			Console.WriteLine("Default script not found. Searching for other available scripts...");
			
			List<string> foundScripts = new List<string>();
			for (int i = 0; i < Math.Min(array2.Length, 1000); i++) // Limit search
			{
				XAssetEntry xAssetEntry = array2[i];
				if (xAssetEntry.type == 54 && xAssetEntry.HeaderPointer != null)
				{
					try
					{
						string scriptName = ReadNullTerminatedString(BitConverter.ToInt64(ReadBytes((long)xAssetEntry.HeaderPointer, 8), 0));
						if (!string.IsNullOrEmpty(scriptName) && scriptName.Contains("scripts/"))
						{
							foundScripts.Add(scriptName);
							if (foundScripts.Count >= 10) break; // Show only first 10
						}
					}
					catch { }
				}
			}
			
			if (foundScripts.Count > 0)
			{
				Console.WriteLine($"Scripts found (first {foundScripts.Count}):");
				foreach (string script in foundScripts)
				{
					Console.WriteLine($"  - {script}");
				}
				
				// Try to use the first script found as alternative
				for (int i = 0; i < array2.Length; i++)
				{
					XAssetEntry xAssetEntry = array2[i];
					if (xAssetEntry.type == 54 && xAssetEntry.HeaderPointer != null)
					{
						try
						{
							string scriptName = ReadNullTerminatedString(BitConverter.ToInt64(ReadBytes((long)xAssetEntry.HeaderPointer, 8), 0));
							if (scriptName.Equals(foundScripts[0]))
							{
								num = (long)xAssetEntry.HeaderPointer;
								Console.WriteLine($"Using alternative script: {scriptName}");
								break;
							}
						}
						catch { }
					}
				}
			}
		}
		if (num == 0L)
		{
			Console.WriteLine("Unable to find Header to Inject! Make sure you are in the main menu.");
			Console.ReadKey();
			return;
		}
		long num2 = BitConverter.ToInt64(ReadBytes(num + 16, 8), 0);
		byte[] array3 = File.ReadAllBytes(GSCPath);
		if (array3[4] != 13 && array3[5] != 10)
		{
			Console.WriteLine("This is not a compiled GSC file! Double check that you aren't trying to inject source code.");
			Console.ReadKey();
			return;
		}
		memIntPtr = Marshal.AllocHGlobal(array3.Length);
		Marshal.Copy(array3, 0, memIntPtr, array3.Length);
		IntPtr intPtr = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)array3.Length, 12288u, 4u);
		WriteProcessMemory((int)processHandle, (long)intPtr, (byte**)(void*)memIntPtr, array3.Length, out var lpNumberOfBytesWritten);
		byte* ptr = (byte*)(void*)intPtr;
		WriteProcessMemory((int)processHandle, num + 16, &ptr, 8, out lpNumberOfBytesWritten);
		if (BitConverter.ToInt64(ReadBytes(num + 16, 8), 0) != num2)
		{
			Console.WriteLine("Script injected successfully!");
		}
		else
		{
			Console.WriteLine("Unable to write to BO3's memory!");
		}
		Console.ReadKey();
	}
}
