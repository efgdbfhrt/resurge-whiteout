const koffi = require('koffi');
const fs = require('fs');
const path = require('path');

const kernel32 = koffi.load('kernel32.dll');
const psapi = koffi.load('psapi.dll');

// Constants
const TH32CS_SNAPPROCESS = 0x00000002;
const PROCESS_WM_READ = 0x0010;
const PROCESS_VM_WRITE = 0x0020;
const PROCESS_VM_OPERATION = 0x0008;
const PROCESS_QUERY_INFORMATION = 0x0400;

const MEM_COMMIT = 0x1000;
const MEM_RESERVE = 0x2000;
const PAGE_READWRITE = 0x04;

// Structs
const PROCESSENTRY32 = koffi.struct('PROCESSENTRY32', {
    dwSize: 'uint32',
    cntUsage: 'uint32',
    th32ProcessID: 'uint32',
    th32DefaultHeapID: 'uint64',
    th32ModuleID: 'uint32',
    cntThreads: 'uint32',
    th32ParentProcessID: 'uint32',
    pcPriClassBase: 'int32',
    dwFlags: 'uint32',
    szExeFile: koffi.array('char', 260)
});

// APIs
const CreateToolhelp32Snapshot = kernel32.func('CreateToolhelp32Snapshot', 'void*', ['uint32', 'uint32']);
const Process32First = kernel32.func('Process32First', 'int', ['void*', koffi.inout(koffi.pointer(PROCESSENTRY32))]);
const Process32Next = kernel32.func('Process32Next', 'int', ['void*', koffi.inout(koffi.pointer(PROCESSENTRY32))]);
const OpenProcess = kernel32.func('OpenProcess', 'void*', ['uint32', 'int', 'uint32']);
const CloseHandle = kernel32.func('CloseHandle', 'int', ['void*']);
const EnumProcessModules = psapi.func('EnumProcessModules', 'int', ['void*', 'uint64*', 'uint32', 'uint32*']);
const ReadProcessMemory = kernel32.func('ReadProcessMemory', 'int', ['void*', 'uint64', 'void*', 'uint32', 'uint32*']);
const WriteProcessMemory = kernel32.func('WriteProcessMemory', 'int', ['void*', 'uint64', 'void*', 'uint32', 'uint32*']);
const VirtualAllocEx = kernel32.func('VirtualAllocEx', 'uint64', ['void*', 'void*', 'uint32', 'uint32', 'uint32']);

function findProcess() {
    const snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (!snap) return null;

    let entry = [{ dwSize: koffi.sizeof(PROCESSENTRY32) }];
    
    let target = null;
    const processNames = ["Resurge", "BlackOps3MS", "BlackOps3", "CallofDuty_BlackOps3_x64", "t7_sp64_ship", "t7_mp64_ship"];
    const currentExeName = path.basename(process.execPath).toLowerCase();

    if (Process32First(snap, entry)) {
        do {
            const exeName = Buffer.from(entry[0].szExeFile).toString('utf8').replace(/\0/g, '');
            const exeNameLow = exeName.toLowerCase();

            // Skip the installer/manager process itself to prevent injecting into our own app
            if (exeNameLow === currentExeName) {
                continue;
            }

            for (const name of processNames) {
                if (exeNameLow.includes(name.toLowerCase())) {
                    target = { pid: entry[0].th32ProcessID, name: exeName };
                    break;
                }
            }
            if (target) break;
        } while (Process32Next(snap, entry));
    }
    
    CloseHandle(snap);
    return target;
}

function getBaseAddress(handle) {
    let mod = Buffer.alloc(8);
    let needed = Buffer.alloc(4);
    if (EnumProcessModules(handle, mod, 8, needed)) {
        return mod.readBigUInt64LE(0);
    }
    return 0n;
}

function readNullTerminatedString(buffer) {
    let i = 0;
    while (i < buffer.length && buffer[i] !== 0) i++;
    return buffer.toString('utf8', 0, i);
}

async function injectGsc(gscPath) {
    const procInfo = findProcess();
    if (!procInfo) throw new Error("Call of Duty: Black Ops III is not running!");

    const handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_WM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, 0, procInfo.pid);
    if (!handle) throw new Error("Failed to open game process. Make sure to run as Administrator if needed.");

    try {
        const baseAddr = getBaseAddress(handle);
        if (baseAddr === 0n) throw new Error("Failed to get module base address.");

        const procNameLow = procInfo.name.toLowerCase();
        const isMSStore = procNameLow.includes("ms") || 
                          procNameLow.includes("callofduty") || 
                          procNameLow.includes("resurge") ||
                          procNameLow.includes("t7_");
                          
        const assetPoolOffset = baseAddr + BigInt(isMSStore ? 0xF3B1330 : 0x9407AB0);

        const entriesCount = 156672;
        const structSize = 32;
        const bufferSize = entriesCount * structSize;
        const arrayBuffer = Buffer.alloc(bufferSize);

        const success = ReadProcessMemory(handle, assetPoolOffset, arrayBuffer, bufferSize, null);
        if (!success) {
            throw new Error(`Failed to read process memory at asset pool offset (0x${assetPoolOffset.toString(16)}).`);
        }

        let headerPointer = 0n;

        for (let i = 0; i < entriesCount; i++) {
            const type = arrayBuffer.readInt32LE(i * structSize);
            if (type === 54) {
                const pHeader = arrayBuffer.readBigUInt64LE(i * structSize + 8);
                if (pHeader !== 0n) {
                    const ptrBuf = Buffer.alloc(8);
                    if (ReadProcessMemory(handle, pHeader, ptrBuf, 8, null)) {
                        const namePtr = ptrBuf.readBigUInt64LE(0);
                        if (namePtr !== 0n) {
                            const strBuf = Buffer.alloc(256);
                            if (ReadProcessMemory(handle, namePtr, strBuf, 256, null)) {
                                const scriptName = readNullTerminatedString(strBuf);
                                if (scriptName === "scripts/shared/duplicaterender_mgr.gsc") {
                                    headerPointer = pHeader;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (headerPointer === 0n) {
            throw new Error("Target script 'duplicaterender_mgr.gsc' not found in memory. Make sure you are in the main menu.");
        }

        const targetMemAddr = headerPointer + 16n;
        const num2Buf = Buffer.alloc(8);
        ReadProcessMemory(handle, targetMemAddr, num2Buf, 8, null);
        const originalNum2 = num2Buf.readBigUInt64LE(0);

        const gscData = fs.readFileSync(gscPath);
        if (gscData.length < 6 || gscData[4] !== 13 || gscData[5] !== 10) {
            throw new Error("Invalid compiled GSC file format (magic bytes mismatch).");
        }

        let ptrRaw = VirtualAllocEx(handle, null, gscData.length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (typeof ptrRaw !== 'number' && typeof ptrRaw !== 'bigint') {
            throw new Error("Failed to allocate memory in target process.");
        }
        if (ptrRaw === 0 || ptrRaw === 0n) {
            throw new Error("VirtualAllocEx returned 0 ptr.");
        }
        const memIntPtr = BigInt(ptrRaw);

        if (!WriteProcessMemory(handle, memIntPtr, gscData, gscData.length, null)) {
            throw new Error("Failed to write GSC data to target process.");
        }

        const ptrBuf = Buffer.alloc(8);
        ptrBuf.writeBigUInt64LE(memIntPtr, 0);
        if (!WriteProcessMemory(handle, targetMemAddr, ptrBuf, 8, null)) {
            throw new Error("Failed to write new script pointer to target process.");
        }

        ReadProcessMemory(handle, targetMemAddr, num2Buf, 8, null);
        const newNum2 = num2Buf.readBigUInt64LE(0);
        if (newNum2 === memIntPtr) {
            return `Successfully injected ${path.basename(gscPath)}!`;
        } else {
            throw new Error("Injection failed verification. Pointer was not updated successfully.");
        }

    } finally {
        CloseHandle(handle);
    }
}

module.exports = {
    findProcess,
    injectGsc
};
