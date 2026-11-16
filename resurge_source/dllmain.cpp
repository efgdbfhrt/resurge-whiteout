// dllmain.cpp : Defines the entry point for the DLL application.
#include "framework.h"
#include <intrin.h>
#include <unordered_map>
#include <fstream>
#include <mutex>
#include "hks.h"
#include "WhiteoutProgression.hpp"
#include "WhiteoutPad.hpp"
#include "pe.h"
#include "MemoryModule.h"
#include "scePadEnhancements.h"
#include "linux_helper.h"

#if ENABLE_STEAMAPI
#include "steam.h"
#endif
#include "security.h"
#include "server_browser.h"
#include <direct.h>

// FUTURE MAYBE: steamchat (requires steamlobbies)
// FUTURE MAYBE: friends list

// =====================================
// CONFIG FILE (Resurge.ini)
// =====================================
bool g_forceXboxLive = true;
int g_forceDisableSteam = 2;
bool g_enableConsole = false;
bool g_enableUnlockAllItems = true;

bool g_gobbleGumHackEnabled = false;
bool g_cryptokeysHackEnabled = false;
bool g_liquidDiviniumHackEnabled = false;
int g_controllerIconsOverride = 0; // 0=Auto, 1=Xbox, 2=PS
char g_iniPath[MAX_PATH] = {0};
bool g_experimentalNetPatches = false;
char g_expIniPath[MAX_PATH] = {0};

// Get path to INI file (same directory as DLL)
void GetIniPath(char* outPath, size_t maxLen)
{
    HMODULE hModule = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)&GetIniPath, &hModule);
    if (hModule)
    {
        GetModuleFileNameA(hModule, outPath, (DWORD)maxLen);
        char* lastSlash = strrchr(outPath, '\\');
        if (lastSlash) {
            *(lastSlash + 1) = '\0';
            strcat_s(outPath, maxLen, "Resurge.ini");
        }
    }
}

// Read integer from INI file
int ReadIniInt(const char* section, const char* key, int defaultValue)
{
    return GetPrivateProfileIntA(section, key, defaultValue, g_iniPath);
}

// Write integer to INI file  
void WriteIniInt(const char* section, const char* key, int value)
{
    char buffer[32];
    sprintf_s(buffer, "%d", value);
    WritePrivateProfileStringA(section, key, buffer, g_iniPath);
}

// Create default INI file if it doesn't exist
void CreateDefaultIni()
{
    FILE* f = fopen(g_iniPath, "r");
    if (f) {
        fclose(f);
        return; // File already exists
    }
    
    // Create new INI with defaults
    f = fopen(g_iniPath, "w");
    if (f) {
        fprintf(f, "; ==========================================\n");
        fprintf(f, "; Resurge Configuration File\n");
        fprintf(f, "; ==========================================\n");
        fprintf(f, "\n");
        fprintf(f, "[System]\n");
        fprintf(f, "; Force Xbox Live account (makes uses_gdk = 1)\n");
        fprintf(f, "; 0 = Disabled, 1 = Enabled (Default)\n");
        fprintf(f, "force_xbox_live=1\n");
        fprintf(f, "\n");
        fprintf(f, "; Force Disable Steam connection\n");
        fprintf(f, "; 0 = Leave API enabled\n");
        fprintf(f, "; 1 = Disable Steam API\n");
        fprintf(f, "; 2 = Disable API and taskkill steam.exe on startup (Default)\n");
        fprintf(f, "force_disable_steam=2\n");
        fprintf(f, "\n");
        fprintf(f, "; Enable Developer Console\n");
        fprintf(f, "; 0 = Disabled (Default), 1 = Enabled\n");
        fprintf(f, "enable_console=0\n");
        fprintf(f, "\n");
        fprintf(f, "[DLC]\n");
        fprintf(f, "; Enable Unlock All Calling Cards, Emblems and Specialist Outfits\n");
        fprintf(f, "; 0 = Disabled, 1 = Enabled (Default)\n");
        fprintf(f, "enable_unlock_all=1\n");
        fprintf(f, "\n");
        fprintf(f, "; Enable full DLC spoof (all DLC map packs unlocked)\n");
        fprintf(f, "; 0 = Base game only, 1 = All DLCs\n");
        fprintf(f, "spoof_dlc_mappacks=1\n");
        fprintf(f, "\n");
        fprintf(f, "[Security]\n");
        fprintf(f, "; Enable security prefix (Anti-RCE protection)\n");
        fprintf(f, "; 0 = Disabled (test matchmaking), 1 = Enabled (secure)\n");
        fprintf(f, "security_prefix=1\n");
        fprintf(f, "\n");
        fprintf(f, "; Enable security hooks (Anti-exploit protection)\n");
        fprintf(f, "; 0 = Disabled (vanilla-like, may fix MP matchmaking), 1 = Enabled (secure)\n");
        fprintf(f, "security_hooks=1\n");
        fprintf(f, "\n");
        fprintf(f, "[Hacks]\n");
        fprintf(f, "; Enable GobbleGum hack (sets all GobbleGums to max)\n");
        fprintf(f, "; 0 = Disabled, 1 = Enabled\n");
        fprintf(f, "gobblegum_hack=1\n");
        fprintf(f, "\n");
        fprintf(f, "; GobbleGum value (default 999)\n");
        fprintf(f, "gobblegum_value=999\n");
        fprintf(f, "\n");
        fprintf(f, "; Enable Cryptokeys hack (sets Cryptokeys to max)\n");
        fprintf(f, "; 0 = Disabled, 1 = Enabled\n");
        fprintf(f, "cryptokeys_hack=1\n");
        fprintf(f, "\n");
        fprintf(f, "; Cryptokeys value (default 99999)\n");
        fprintf(f, "cryptokeys_value=99999\n");
        fprintf(f, "\n");
        fprintf(f, "; Enable Liquid Divinium hack (sets Liquid Divinium to max)\n");
        fprintf(f, "; 0 = Disabled, 1 = Enabled\n");
        fprintf(f, "liquid_divinium_hack=1\n");
        fprintf(f, "\n");
        fprintf(f, "; Liquid Divinium value (default 99999)\n");
        fprintf(f, "liquid_divinium_value=99999\n");
        fprintf(f, "\n");
        fprintf(f, "[Controller]\n");
        fprintf(f, "; Controller button icons displayed in-game\n");
        fprintf(f, "; 0 = Auto (detect controller type)\n");
        fprintf(f, "; 1 = Force Xbox icons\n");
        fprintf(f, "; 2 = Force PlayStation icons\n");
        fprintf(f, "controller_icons=0\n");
        fclose(f);
    }
}

// Forward declarations of config variables (defined later in the file)
extern bool g_spoofDLCMapPacks;
extern bool g_useSecurityPrefix;
extern bool g_disableSecurityHooks;

// Load all settings from INI
void LoadConfigFromIni()
{
    GetIniPath(g_iniPath, sizeof(g_iniPath));
    CreateDefaultIni();
    
    // Read settings
    g_forceXboxLive = (ReadIniInt("System", "force_xbox_live", 1) != 0);
    g_forceDisableSteam = ReadIniInt("System", "force_disable_steam", 2);
    g_enableConsole = (ReadIniInt("System", "enable_console", 0) != 0);
    
    g_enableUnlockAllItems = (ReadIniInt("DLC", "enable_unlock_all", 1) != 0);
    g_spoofDLCMapPacks = (ReadIniInt("DLC", "spoof_dlc_mappacks", 0) != 0);
    g_useSecurityPrefix = (ReadIniInt("Security", "security_prefix", 1) != 0);
    g_disableSecurityHooks = (ReadIniInt("Security", "security_hooks", 1) == 0); // Inverted: 1=enabled means hooks NOT disabled
    g_gobbleGumHackEnabled = (ReadIniInt("Hacks", "gobblegum_hack", 0) != 0);
    g_cryptokeysHackEnabled = (ReadIniInt("Hacks", "cryptokeys_hack", 0) != 0);
    g_liquidDiviniumHackEnabled = (ReadIniInt("Hacks", "liquid_divinium_hack", 0) != 0);
    
    // Controller settings
    g_controllerIconsOverride = ReadIniInt("Controller", "controller_icons", 0); // 0 = match the controller in use
    if (g_controllerIconsOverride < 0 || g_controllerIconsOverride > 2)
        g_controllerIconsOverride = 0;
}

void GetExperimentalIniPath(char* outPath, size_t maxLen)
{
    HMODULE hModule = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)&GetIniPath, &hModule);
    if (hModule)
    {
        GetModuleFileNameA(hModule, outPath, (DWORD)maxLen);
        char* lastSlash = strrchr(outPath, '\\');
        if (lastSlash) {
            *(lastSlash + 1) = '\0';
            strcat_s(outPath, maxLen, "experimental.ini");
        }
    }
}

void CreateDefaultExperimentalIni()
{
    FILE* f = fopen(g_expIniPath, "r");
    if (f) {
        fclose(f);
        return; 
    }
    
    f = fopen(g_expIniPath, "w");
    if (f) {
        fprintf(f, "; ==========================================\n");
        fprintf(f, "; EXPERIMENTAL Configuration File\n");
        fprintf(f, "; ==========================================\n");
        fprintf(f, "\n");
        fprintf(f, "[Network]\n");
        fprintf(f, "; Enable experimental network patches (dedicated server search fix)\n");
        fprintf(f, "; 0 = Disabled (Default), 1 = Enabled\n");
        fprintf(f, "networkpatches=0\n");
        fclose(f);
    }
}

void LoadExperimentalConfig()
{
    GetExperimentalIniPath(g_expIniPath, sizeof(g_expIniPath));
    CreateDefaultExperimentalIni();
    g_experimentalNetPatches = (GetPrivateProfileIntA("Network", "networkpatches", 0, g_expIniPath) != 0);
    ALOG("Experimental Config: networkpatches=%d", g_experimentalNetPatches);
}

// =====================================
// GOBBLEGUM MEMORY HACK - Direct Offsets
// =====================================
#define GOBBLEGUM_VALUE_DEFAULT 999  

// All GobbleGum offsets from CT (B60AXXX region = inventory)
const uint64_t g_gobbleGumOffsets[] = {
    // PERKAHOLIC - Only BlackOps3MS.exe offsets (removed 0x186FA500 - out of range)
    0xB611EE4, 0xB60AC5C, 0xB60AE98, 0xB5D2674,
    0xB60A4FC, 0xB64E3B0, 0xB64E400, 0xB64E450, 0xB65C660,
    0xB69EE18, 0xB816884, 0xB8168D4, 0xB816974, 0xB8169C4,
    0xB816A14, 0xB816B54, 0xB816BA4, 0xB816BF4, 0xB816C44,
    0xB60ABF8, 0xB60AE84, 0xB816C94, 0xB816CE4, 0xB816D34,
    0xB816D84, 0xB817004, 0xB60AEAC, 0xB60AC98, 0xB60AD10,
    0xB60AEC0,
    // REIGN DROPS
    0xB6063A8, 0xB60A150, 0xB60AE3C, 0xB60AF24, 0xB64E590,
    0xB64E5E0, 0xB64E630, 0xB65C6E0, 0xB69ED74, 0xB60AEE8,
    0xB60AE28, 0xB60AF10, 0xB60A188, 0xB60AD44, 0xB60AE78,
    0xB612164, 0xB64E680, 0xB64E6D0, 0xB64E720, 0xB65C720,
    0xB69EEC0, 0xB60A4B4, 0xB60AE50, 0xB60AF38, 0xB60AEB4,
    // SHOPPING FREE
    0xB60A5EC, 0xB60A9AC, 0xB60A920, 0xB60A998, 0xB60A9FC,
    0xB60AA24, 0xB60ADC4, 0xB60AEDC, 0xB60D56C,
    // WALL POWER
    0xB147EEC, 0xB69EE88, 0xB228810, 0xB6064B8, 0xB606460,
    0xB60AC20, 0xB60AC48, 0xB60AD4C, 0xB64E0E0, 0xB64E130,
    0xB60AEFC,
    // MISC GUMS
    0xB60A1F8, 0xB60AD24, 0xB60AD88, 0xB64E4A0,
    0xB64E4F0, 0xB64E540, 0xB65C6A0, 0xB69E70C, 0xB69EE50,
    0xB816888, 0xB8168D8, 0xB816978, 0xB8169C8, 0xB816A18,
    0xB816B58, 0xB816BA8, 0xB816BF8, 0xB816C34, 0xB816C48,
    0xB60A650, 0xB60A984, 0xB60AA10, 0xB60AA60, 0xB60AA88,
    0xB60ADEC, 0xB60AEF0,
    // EXTRA GUMS - Safe B6XXXXX range offsets from CT
    0xB60A4C8, 0xB60AD94, 0xB60AA38, 0xB60AAB0, 0xB60AAC4,
    0xB60AB14, 0xB60AB3C, 0xB60AF18, 0xB60D580, 0xB636CE0,
    0xB6439A0, 0xB64D780, 0xB64D7D0, 0xB64D820, 0xB65C320,
    0xB69EB40, 0xB60A704, 0xB60A4D4, 0xB60A5CC, 0xB60ADF8,
    0xB6057BC, 0xB605874, 0xB60A48C, 0xB60A510, 0xB60A5B0,
    0xB60AE20, 0xB64E860, 0xB64E8B0, 0xB64E900, 0xB65C7A0,
    0xB69E72C, 0xB69EF30, 0xB60A540, 0xB60AF04, 0xB64DC30,
    0xB64DC80, 0xB64DCD0, 0xB65C460, 0xB69EC58, 0xB60A4A0,
    0xB60AE14, 0xB60B654, 0xB64DE10, 0xB64DE60, 0xB64DEB0,
    0xB65C4E0, 0xB69ECC8, 0xB74AED8
    // Removed high offsets that may cause crashes: 0xBF0818C, 0xBF0D554, 0xBF6E9B4, 0xBF73D7C, 0xBFD96AC, 0xBF058D4, 0xBF6C0FC, 0xB7740D4, 0xB7ECE70, 0xB83FA64, 0xBEFF37C
};
const size_t g_gobbleGumOffsetsCount = sizeof(g_gobbleGumOffsets) / sizeof(g_gobbleGumOffsets[0]);

// Thread to continuously set GobbleGum values
DWORD WINAPI GobbleGumHackThread(LPVOID lpParam)
{
    // Wait for game to initialize
    Sleep(20000);
    
    // Read GobbleGum value from INI (can be changed at runtime)
    int gobbleGumValue = GOBBLEGUM_VALUE_DEFAULT;
    if (g_iniPath[0] != '\0') {
        gobbleGumValue = ReadIniInt("Hacks", "gobblegum_value", GOBBLEGUM_VALUE_DEFAULT);
    }
    
    while (true)
    {
        if (g_gobbleGumHackEnabled)
        {
            for (size_t i = 0; i < g_gobbleGumOffsetsCount; i++)
            {
                uint64_t addr = REBASE(g_gobbleGumOffsets[i]);
                // Only write if memory is accessible
                __try
                {
                    *(int32_t*)addr = gobbleGumValue;
                }
                __except(EXCEPTION_EXECUTE_HANDLER)
                {
                    // Ignore access violations - address might not be valid yet
                }
            }
        }
        
        Sleep(500); // Update every 500ms
    }
    
    return 0;
}

// =====================================
// END GOBBLEGUM HACK
// =====================================

// =====================================
// CRYPTOKEYS MEMORY HACK
// =====================================
#define CRYPTOKEYS_VALUE_DEFAULT 99999

// Cryptokeys offsets from CT (BlackOps3MS.exe+B60A18C is the main one)
// Note: 0x186D2C08 is too high/dynamic, only using safe offset
const uint64_t g_cryptokeysOffsets[] = {
    0xB60A18C,  // Main Cryptokeys address
};
const size_t g_cryptokeysOffsetsCount = sizeof(g_cryptokeysOffsets) / sizeof(g_cryptokeysOffsets[0]);

// Thread to continuously set Cryptokeys values
DWORD WINAPI CryptokeysHackThread(LPVOID lpParam)
{
    // Wait for game to initialize
    Sleep(20000);
    
    // Read Cryptokeys value from INI
    int cryptokeysValue = CRYPTOKEYS_VALUE_DEFAULT;
    if (g_iniPath[0] != '\0') {
        cryptokeysValue = ReadIniInt("Hacks", "cryptokeys_value", CRYPTOKEYS_VALUE_DEFAULT);
    }
    
    while (true)
    {
        if (g_cryptokeysHackEnabled)
        {
            for (size_t i = 0; i < g_cryptokeysOffsetsCount; i++)
            {
                uint64_t addr = REBASE(g_cryptokeysOffsets[i]);
                __try
                {
                    *(int32_t*)addr = cryptokeysValue;
                }
                __except(EXCEPTION_EXECUTE_HANDLER)
                {
                    // Ignore access violations
                }
            }
        }
        
        Sleep(500);
    }
    
    return 0;
}

// =====================================
// LIQUID DIVINIUM MEMORY HACK
// =====================================
#define LIQUID_DIVINIUM_VALUE_DEFAULT 99999

// Liquid Divinium offsets from CT (BlackOps3MS.exe+B60A1C4 is the main one)
// Note: 0x186FB0B8 is too high/dynamic, only using safe offset
const uint64_t g_liquidDiviniumOffsets[] = {
    0xB60A1C4,  // Main Liquid Divinium address
};
const size_t g_liquidDiviniumOffsetsCount = sizeof(g_liquidDiviniumOffsets) / sizeof(g_liquidDiviniumOffsets[0]);

// Thread to continuously set Liquid Divinium values
DWORD WINAPI LiquidDiviniumHackThread(LPVOID lpParam)
{
    // Wait for game to initialize
    Sleep(20000);
    
    // Read Liquid Divinium value from INI
    int liquidDiviniumValue = LIQUID_DIVINIUM_VALUE_DEFAULT;
    if (g_iniPath[0] != '\0') {
        liquidDiviniumValue = ReadIniInt("Hacks", "liquid_divinium_value", LIQUID_DIVINIUM_VALUE_DEFAULT);
    }
    
    while (true)
    {
        if (g_liquidDiviniumHackEnabled)
        {
            for (size_t i = 0; i < g_liquidDiviniumOffsetsCount; i++)
            {
                uint64_t addr = REBASE(g_liquidDiviniumOffsets[i]);
                __try
                {
                    *(int32_t*)addr = liquidDiviniumValue;
                }
                __except(EXCEPTION_EXECUTE_HANDLER)
                {
                    // Ignore access violations
                }
            }
        }
        
        Sleep(500);
    }
    
    return 0;
}

// =====================================
// END CURRENCY HACKS
// =====================================

HMODULE __thismodule;
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved);
bool g_needsUpdatePrompt = false;
bool g_spoofDLC = true; // Controls whether DLC spoofing is enabled (can be toggled via prompt)
bool g_spoofDLCMapPacks = false; // Controls whether DLC map packs (Awakening, Eclipse, etc.) are spoofed
bool g_useSecurityPrefix = true; // Controls ZBR anti-RCE prefix (can be toggled for testing)
bool g_disableSecurityHooks = false; // Disable ALL security hooks for matchmaking testing

#if REMAP_EXPERIMENT
thread_local char tls_data[0x2000];
#pragma optimize("", off)
void NTAPI tls_callback(PVOID DllHandle, DWORD dwReason, PVOID)
{

}
#pragma optimize("", on)

#pragma comment (linker, "/INCLUDE:_tls_used")
#pragma comment (linker, "/INCLUDE:tls_callback_func")
#pragma const_seg(".CRT$XLF")
EXTERN_C const PIMAGE_TLS_CALLBACK tls_callback_func = tls_callback;
#pragma const_seg()
#endif

bool is_remapped_exe = false;
bool dbg_is_console_available = true;
bool dbg_is_attached = false;
HANDLE dbg_hWriteConsole = NULL, dbg_hReadConsole = NULL;

typedef unsigned long long(__fastcall* ZwContinue_t)(PCONTEXT ThreadContext, BOOLEAN RaiseAlert);
ZwContinue_t ZwContinue = reinterpret_cast<ZwContinue_t>(GetProcAddress(GetModuleHandleA("ntdll.dll"), "ZwContinue"));

typedef LONG(NTAPI* NtSuspendProcess)(IN HANDLE ProcessHandle);
void SuspendProcess()
{
    HANDLE processHandle = OpenProcess(PROCESS_ALL_ACCESS, FALSE, GetCurrentProcessId());

    NtSuspendProcess pfnNtSuspendProcess = (NtSuspendProcess)GetProcAddress(
        GetModuleHandle("ntdll"), "NtSuspendProcess");

    pfnNtSuspendProcess(processHandle);
    CloseHandle(processHandle);
}

void noprange(__int64 addy, __int32 size)
{
    unsigned char* data = new unsigned char[size];
    memset(data, 0x90, size);

    chgmem(addy, size, data);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)addy, size);

    delete[] data;
    data = 0;
}

int istrcmp_(const char* s1, const char* s2)
{
    if (!s1 || !s2)
    {
        return 0;
    }

    int c = 0;
    while (s1[c] && s2[c] && (tolower(s1[c]) == tolower(s2[c]))) c++;

    if (s1[c] || s2[c])
    {
        return 1;
    }

    return 0;
}

void MSG_Init(msg_t* buf, char* data, uint32_t length)
{
    if (!*(uint32_t*)REBASE(0x190B0990)) // msg_init
    {
        ((void(__fastcall*)())REBASE(0x2225880))(); // MSG_InitHuffman
    }
    memset(buf, 0LL, sizeof(msg_t));
    buf->data = data;
    buf->maxsize = length;
    buf->readOnly = 0;
    buf->splitData = 0LL;
    buf->splitSize = 0;
}

void MSG_InitReadOnly(msg_t* buf, char* data, uint32_t length)
{
    MSG_Init(buf, data, length);
    buf->readOnly = 1;
    buf->maxsize = length;
    buf->cursize = length;
}

void MSG_BeginReading(msg_t* msg)
{
    msg->overflowed = 0;
    msg->readcount = 0;
    msg->bit = 0;
}

uint8_t sanityCheckAndGetMsgType(msg_t* msg)
{
    uint8_t msgType = 0;
    if (MSG_ReadByte(msg) == '1')
    {
        msgType = MSG_ReadByte(msg);
    }
    return msgType;
}

char I_CleanChar(char character)
{
    if ((uint8_t)character == (uint8_t)0x92)
        return '\'';
    if (character == '%')
        return '.';
    return character;
}

void MSG_WriteData(msg_t* buf, const void* data, int length)
{
    auto newsize = length + buf->cursize;
    if (newsize > buf->maxsize)
    {
        buf->overflowed = 1;
    }
    else
    {
        memcpy(&buf->data[buf->cursize], data, length);
        buf->cursize = newsize;
    }
}

void MSG_WriteInt64(msg_t* buf, uint64_t data)
{
    auto newsize = sizeof(data) + buf->cursize;
    if (newsize > buf->maxsize)
    {
        buf->overflowed = 1;
    }
    else
    {
        *(uint64_t*)&buf->data[buf->cursize] = data;
        buf->cursize = newsize;
    }
}

void MSG_WriteUInt16(msg_t* buf, uint16_t data)
{
    auto newsize = sizeof(data) + buf->cursize;
    if (newsize > buf->maxsize)
    {
        buf->overflowed = 1;
    }
    else
    {
        *(uint16_t*)&buf->data[buf->cursize] = data;
        buf->cursize = newsize;
    }
}

void MSG_WriteString(msg_t* sb, const char* s)
{
    char string[1024];

    auto l = strlen(s);

    if (l > 1023)
    {
        MSG_WriteData(sb, "", 1);
        return;
    }
    
    for (int i = 0; i < l; i++)
    {
        string[i] = I_CleanChar(s[i]);
    }

    string[l] = 0;
    MSG_WriteData(sb, string, l + 1);
}

uint8_t MSG_GetByte(msg_t* msg, int _where)
{
    if (_where < msg->cursize)
    {
        return msg->data[_where];
    }
    return msg->splitData[_where - msg->cursize];
}

void MSG_GetBytes(msg_t* msg, int _where, uint8_t* dest, int len)
{
    for (int i = 0; i < len; ++i)
    {
        dest[i] = MSG_GetByte(msg, _where++);
    }
}

int MSG_ReadBit(msg_t* msg)
{
    int bit = msg->bit & 7;
    
    if (bit)
    {
        goto LABEL_5;
    }

    if (msg->readcount < msg->splitSize + msg->cursize)
    {
        msg->bit = 8 * msg->readcount++;
    LABEL_5:
        auto b = MSG_GetByte(msg, msg->bit >> 3);
        ++msg->bit;
        return (b >> bit) & 1;
    }

    msg->overflowed = 1;
    return -1;
}

uint8_t MSG_ReadByte(msg_t* msg)
{
    if (msg->readcount >= msg->splitSize + msg->cursize)
    {
        msg->overflowed = 1;
        return 0;
    }

    return MSG_GetByte(msg, msg->readcount++);
}

void MSG_ReadData(msg_t* msg, void* data, int len)
{
    auto newcount = len + msg->readcount;
    if (newcount > msg->cursize)
    {
        if (newcount > msg->splitSize + msg->cursize)
        {
            msg->overflowed = 1;
            // memset(data, 0xFF, len); they do this in the pdb but this causes certain crashes so... my impl will not be doing this!
        }
        else
        {
            auto cursize = msg->cursize - msg->readcount;
            if (cursize > 0)
            {
                memcpy(data, &msg->data[msg->readcount], cursize);
                len -= cursize;
                data = (char*)data + cursize;
            }
            if (len > 0)
            {
                memcpy(data, &msg->splitData[msg->readcount - msg->cursize], len);
            }
            msg->readcount = newcount;
        }
    }
    else
    {
        memcpy(data, &msg->data[msg->readcount], len);
        msg->readcount = newcount;
    }
}

uint64_t MSG_ReadInt64(msg_t* msg)
{
    uint64_t c = 0;
    auto newcount = msg->readcount + 8;
    if (newcount > msg->splitSize + msg->cursize)
    {
        msg->overflowed = 1;
        return c;
    }
    MSG_GetBytes(msg, msg->readcount, (uint8_t*)&c, 8);
    msg->readcount = newcount;
    return c;
}

uint32_t MSG_ReadInt32(msg_t* msg)
{
    uint32_t c = -1;
    auto newcount = msg->readcount + 4;
    if (newcount > msg->splitSize + msg->cursize)
    {
        msg->overflowed = 1;
        return c;
    }
    MSG_GetBytes(msg, msg->readcount, (uint8_t*)&c, 4);
    msg->readcount = newcount;
    return c;
}

uint16_t MSG_ReadInt16(msg_t* msg)
{
    uint16_t c = -1;
    auto newcount = msg->readcount + 2;
    if (newcount > msg->splitSize + msg->cursize)
    {
        msg->overflowed = 1;
        return c;
    }
    MSG_GetBytes(msg, msg->readcount, (uint8_t*)&c, 2);
    msg->readcount = newcount;
    return c;
}

void MSG_WriteByte(msg_t* msg, uint8_t c)
{
    if (msg->cursize >= msg->maxsize)
    {
        msg->overflowed = 1;
    }
    else
    {
        msg->data[msg->cursize++] = c;
    }
}

char* MSG_ReadStringLine(msg_t* msg, char* string, uint32_t maxChars)
{
    for (auto l = 0; ; ++l)
    {
        auto c = MSG_ReadByte(msg);
        if (c == '\n' || c == -1)
        {
            c = 0;
        }

        if (l < maxChars)
        {
            string[l] = I_CleanChar(c);
        }

        if (!c)
        {
            break;
        }
    }

    string[maxChars - 1] = 0;
    return string;
}

void MSG_WriteBit0(msg_t* msg)
{
    if (msg->cursize < msg->maxsize)
    {
        auto msgBit = msg->bit;
        if (!(msgBit & 7))
        {
            msgBit = 8 * msg->cursize;
            msg->data[msg->cursize++] = 0;
        }
        msg->bit = msgBit + 1;
    }
    else
    {
        msg->overflowed = 1;
    }
}

void MSG_WriteBit1(msg_t* msg)
{
    if (msg->cursize < msg->maxsize)
    {
        auto msgBit = msg->bit;
        auto bit = msg->bit & 7;
        if (!(msgBit & 7))
        {
            msgBit = 8 * msg->cursize;
            msg->data[msg->cursize++] = 0;
        }
        msg->data[msgBit >> 3] |= 1 << bit;
        msg->bit = msgBit + 1;
    }
    else
    {
        msg->overflowed = 1;
    }
}

SessionClient* LobbySession_GetClientByClientNum(LobbySession* session, uint32_t index)
{
    if (index == 0xFFFFFFFF)
    {
        return NULL;
    }
    return (SessionClient*)(48llu * index + (uint64_t)session + 0xF8);
}

netadr_t LobbySession_GetClientNetAdrByIndex(LobbyType type, uint32_t clientnum)
{
    auto session = LobbySession_GetSession(type);
    auto client = LobbySession_GetClientByClientNum(session, clientnum);

    if (!client->activeClient)
    {
        return netadr_t();
    }

    return client->activeClient->sessionInfo[session->type].netAdr;
}

struct delayed_call
{
public:
    std::function<void(void)> lambda;
    uint64_t execute_time;
    uint32_t event_id;
};

std::unordered_map<uint32_t, delayed_call> local_events_named;
std::vector<std::function<void()>> local_events_every_frame;

void dispatch_events()
{
    for (auto it = local_events_every_frame.begin(); it != local_events_every_frame.end(); ++it)
    {
        (*it)();
    }

    std::vector<uint32_t> dispatched_events;

    for (auto it = local_events_named.begin(); it != local_events_named.end(); it++)
    {
        if (it->second.execute_time > GetTickCount64())
        {
            continue;
        }

        dispatched_events.push_back(it->first);

        it->second.lambda();
    }

    for (auto& i : dispatched_events)
    {
        local_events_named.erase(i);
    }
}

void register_frame_event(std::function<void()> lambda)
{
    local_events_every_frame.push_back(lambda);
}

void set_delay(uint32_t delayMS, uint32_t eventID, std::function<void()> lambda)
{
    if (local_events_named.find(eventID) != local_events_named.end())
    {
        return;
    }

    local_events_named[eventID] = delayed_call();
    local_events_named[eventID].lambda = lambda;
    local_events_named[eventID].event_id = eventID;
    local_events_named[eventID].execute_time = GetTickCount64() + delayMS;
}

LobbyType LobbySession_GetControllingLobbySession(LobbyModule lobbyModule)
{
    return (LobbyType)(*(uint32_t*)(REBASE(0xBEFF3C0) + 0x2A58llu * lobbyModule) > 0);
}

LobbySession* LobbySession_GetSession(LobbyType t)
{
    return (LobbySession*)(REBASE(0xBEFF380) + 0x2A58llu * t);
}

XUID GetXUID(uint32_t controllerIndex)
{
    if (controllerIndex)
    {
        return **(XUID**)REBASE(0x33614D8);
    }
    return **(XUID**)REBASE(0x33614D0);
}

bool HasHost()
{
    LobbyType sessionType = LOBBY_TYPE_PRIVATE;
    if (LobbySession_GetControllingLobbySession(LOBBY_MODULE_CLIENT))
    {
        sessionType = LOBBY_TYPE_GAME;
    }

    const auto session = LobbySession_GetSession(sessionType);
    if (!session)
    {
        return false;
    }

    uint64_t sessPtr = (uint64_t)session;
    return *(XUID*)(sessPtr + 0x60) == GetXUID(0);
}

bool dwInstantHandleLobbyMessage(XUID senderID, const uint32_t controllerIndex, msg_t* msg)
{
    char msgBuf[2048]{ 0 };

    auto size = msg->cursize - msg->readcount;
    if (size >= 2048)
    {
        return 1;
    }

    MSG_ReadData(msg, msgBuf, size);

    if (msg->overflowed)
    {
        return 1;
    }

    return LobbyMsg_HandleIM(controllerIndex, senderID, msgBuf, size);
}

bool LobbyMsgRW_PrepReadMsg(LobbyMsg* lobbyMsg, char* data, int length)
{
    MSG_Init(&lobbyMsg->msg, data, length);
    lobbyMsg->msg.cursize = length;
    return LobbyMsg_PrepReadMsg(lobbyMsg);
}

IHOOK_HEADER(IsProcessorFeaturePresent, BOOL, (DWORD processorFeature))
{
    if (processorFeature == 0x17)
    {
        // force a crash
        *(__int64*)0x17 = processorFeature;
        *(__int64*)processorFeature = 0x17;
    }
    return IHOOK_ORIGINAL(IsProcessorFeaturePresent, (processorFeature));
}

__int64 __fn_ptr_hook = 0;
void InstallHook(void* func)
{
    auto kiuserexceptiondispatcher = (__int64)GetProcAddress(GetModuleHandleA("ntdll.dll"), "KiUserExceptionDispatcher");

    if (kiuserexceptiondispatcher)
    {
        if (*(__int32*)kiuserexceptiondispatcher == 0x58B48FC)
        {
            auto distance = *(DWORD*)(kiuserexceptiondispatcher + 4);
            auto ptr = (kiuserexceptiondispatcher + 8) + distance;
            __fn_ptr_hook = ptr;

            auto OldProtection = 0ul;
            VirtualProtect(reinterpret_cast<void*>(ptr), 8, PAGE_EXECUTE_READWRITE, &OldProtection);
            *reinterpret_cast<void**>(ptr) = func;
            VirtualProtect(reinterpret_cast<void*>(ptr), 8, OldProtection, &OldProtection);
        }
    }
}

void alog_guid(GUID guid) {
    ALOG("Guid = {%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX}",
        guid.Data1, guid.Data2, guid.Data3,
        guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3],
        guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7]);
}

extern FILE* LOGFILE;
std::unordered_map<INT64, CONTEXT> SavedExceptions;
DWORD lck_dumping = NULL;
uint64_t old_address_step = 0;
void ExceptHook(PEXCEPTION_RECORD ExceptionRecord, PCONTEXT ContextRecord)
{
    bool b_fatal = false;

    if (security::handle_exception(ExceptionRecord, ContextRecord))
    {
        ZwContinue(ContextRecord, false);
    }

    // Faults inside Whiteout's own SEH guards are expected and handled there.
    if (whiteout::guardDepth > 0) return;

    if ((ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) || (ExceptionRecord->ExceptionFlags & EXCEPTION_NONCONTINUABLE) || (ExceptionRecord->ExceptionCode == EXCEPTION_ILLEGAL_INSTRUCTION))
    {
        if (IsDebuggerPresent())
        {
            return;
        }

        INT64 faultingModule = 0;
        char module_name[MAX_PATH];

        if (lck_dumping)
        {
            INT64 addy = (INT64)ExceptionRecord->ExceptionAddress;
            SavedExceptions[addy] = *ContextRecord;
        }
        else
        {
            lck_dumping = GetCurrentThreadId();

            FILE* f;
            f = fopen(CRASH_LOG_NAME, "a+"); // a+ (create + append)
            if (!f)
            {
                ExitProcess(-444);
            }

            ALOG("Crash log %p %u\n\n", time(NULL), GetCurrentThreadId());
            fprintf(f, "Crash log %p\n\n", time(NULL));
            INT64 addy = (INT64)ExceptionRecord->ExceptionAddress;
            SavedExceptions[addy] = *ContextRecord;
            while (SavedExceptions.size())
            {
                auto kvp = *SavedExceptions.begin();
                RtlPcToFileHeader((PVOID)kvp.first, (PVOID*)&faultingModule);
                if (faultingModule)
                {
                    GetModuleFileNameA((HMODULE)faultingModule, module_name, MAX_PATH);
                }
                else
                {
                    strcpy_s(module_name, "<Unknown Module>");
                }

                ALOG("Game: %p\n", (void*)REBASE(0x0));
                fprintf(f, "Game: %p\n", (void*)REBASE(0x0));

                {
                    auto peb = NtCurrentPeb();
                    auto head = &peb->Ldr->InMemoryOrderModuleList;

                    int mc = 0;
                    auto entry = head->Flink;
                    while (entry != head)
                    {
                        auto table_entry = CONTAINING_RECORD(entry, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks);

                        size_t size = (wcslen(table_entry->FullDllName.Buffer) + 1) * sizeof(wchar_t);
                        char* buffer = new char[size];
                        std::wcstombs(buffer, table_entry->FullDllName.Buffer, size);
                        

                        auto dll = Pe::PeNative::fromModule(table_entry->DllBase);
                        auto inf = dll.debug().findPdbDebugInfo();

                        if (inf)
                        {
                            
                            auto pdb = dll.debug().findPdbDebugInfo()->pdb70;
                            auto guid = pdb.guid;
                            ALOG("%s: %p pdb: %s {%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX} %u\n", buffer, table_entry->DllBase, dll.debug().findPdbDebugInfo()->pdb70.pdbName, guid.Data1, guid.Data2, guid.Data3,
                                guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3],
                                guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7], dll.headers().nt()->FileHeader.TimeDateStamp);
                            fprintf(f, "%s: %p pdb: %s {%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX} %u\n", buffer, table_entry->DllBase, dll.debug().findPdbDebugInfo()->pdb70.pdbName, guid.Data1, guid.Data2, guid.Data3,
                                guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3],
                                guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7], dll.headers().nt()->FileHeader.TimeDateStamp);
                        }
                        else
                        {
                            ALOG("%s: %p\n", buffer, table_entry->DllBase);
                            fprintf(f, "%s: %p\n", buffer, table_entry->DllBase);
                        }

                        delete[] buffer;
                        buffer = NULL;
                        entry = entry->Flink;
                    }
                }

                ALOG("Module: %s\n", module_name);
                fprintf(f, "Module: %s\n", module_name);

                ALOG("[%p]Exception at (%p) (RIP:%p) (Rsp:%p) (RBP: %p)\n", faultingModule, kvp.first - faultingModule, kvp.second.Rip, kvp.second.Rsp, kvp.second.Rbp);
                fprintf(f, "[%p]Exception at (%p) (RIP:%p) (Rsp: %p)\n", faultingModule, kvp.first - faultingModule, kvp.second.Rip, kvp.second.Rsp, kvp.second.Rbp);

                ALOG("[%p]Rcx: (%p) Rdx: (%p) R8: (%p) R9: (%p)\n", kvp.first, kvp.second.Rcx, kvp.second.Rdx, kvp.second.R8, kvp.second.R9);
                fprintf(f, "[%p]Rcx: (%p) Rdx: (%p) R8: (%p) R9: (%p)\n", kvp.first, kvp.second.Rcx, kvp.second.Rdx, kvp.second.R8, kvp.second.R9);

                ALOG("[%p]Rax: (%p) Rbx: (%p) Rsi: (%p) Rdi: (%p)\n", kvp.first, kvp.second.Rax, kvp.second.Rbx, kvp.second.Rsi, kvp.second.Rdi);
                fprintf(f, "[%p]Rax: (%p) Rbx: (%p) Rsi: (%p) Rdi: (%p)\n", kvp.first, kvp.second.Rax, kvp.second.Rbx, kvp.second.Rsi, kvp.second.Rdi);

                ALOG("[%p]R10: (%p) R11: (%p) R12: (%p) R13: (%p)\n", kvp.first, kvp.second.R10, kvp.second.R11, kvp.second.R12, kvp.second.R13);
                fprintf(f, "[%p]R10: (%p) R11: (%p) R12: (%p) R13: (%p)\n", kvp.first, kvp.second.R10, kvp.second.R11, kvp.second.R12, kvp.second.R13);

                ALOG("[%p]Rbp: (%p) R14: (%p) R15: (%p) Dr7: (%p)\n", kvp.first, kvp.second.Rbp, kvp.second.R14, kvp.second.R15, kvp.second.Dr7);
                fprintf(f, "[%p]Rbp: (%p) R14: (%p) R15: (%p) Dr7: (%p)\n", kvp.first, kvp.second.Rbp, kvp.second.R14, kvp.second.R15, kvp.second.Dr7);

                ALOG("[%p]Dr0: (%p) Dr1: (%p) Dr2: (%p) Dr3: (%p)\n", kvp.first, kvp.second.Dr0, kvp.second.Dr1, kvp.second.Dr2, kvp.second.Dr3);
                fprintf(f, "[%p]Dr0: (%p) Dr1: (%p) Dr2: (%p) Dr3: (%p)\n", kvp.first, kvp.second.Dr0, kvp.second.Dr1, kvp.second.Dr2, kvp.second.Dr3);

                for (int i = 0; i < ((STATUS_ILLEGAL_INSTRUCTION == ExceptionRecord->ExceptionCode) ? 0x1200 : 0x400); i += 0x10)
                {
                    ALOG("[%p] %p %p\n", kvp.second.Rsp + i, *(int64_t*)(kvp.second.Rsp + i), *(int64_t*)(kvp.second.Rsp + i + 8));
                    fprintf(f, "[%p] %p %p\n", kvp.second.Rsp + i, *(int64_t*)(kvp.second.Rsp + i), *(int64_t*)(kvp.second.Rsp + i + 8));
                }
                SavedExceptions.erase(kvp.first);
            }

            // dump script context

            std::fflush(f);
            std::fclose(f);
            lck_dumping = NULL;
        }

        if (STATUS_ILLEGAL_INSTRUCTION != ExceptionRecord->ExceptionCode)
        {
            b_fatal = true;
        }
    }

    if (b_fatal)
    {
        if (lck_dumping && lck_dumping != GetCurrentThreadId())
        {
            // Another thread is writing the crash log; it ends the process when done.
            HANDLE hThread = OpenThread(THREAD_ALL_ACCESS, FALSE, GetCurrentThreadId());
            ContextRecord->Rip = (INT64)SuspendThread;
            ContextRecord->Rcx = (INT64)hThread;
            ZwContinue(ContextRecord, false);
        }
        else
        {
            // The stock handler suspended every thread here, which left a frozen window that had
            // to be ended in Task Manager. Close the game instead so the launcher can report it.
            if (LOGFILE) fflush(LOGFILE);
            TerminateProcess(GetCurrentProcess(), ExceptionRecord->ExceptionCode);
        }
    }
}

void chgmem(__int64 addy, __int32 size, void* copy)
{
    DWORD oldprotect;
    VirtualProtect((void*)addy, size, PAGE_EXECUTE_READWRITE, &oldprotect);
    memcpy((void*)addy, copy, size);
    VirtualProtect((void*)addy, size, oldprotect, &oldprotect);
}

FILE* LOGFILE = NULL;
HWND notepad = NULL;
HWND edit = NULL;

void vnlog(const char* str, va_list ap)
{
    // Bounded formatting: game messages can be longer than the buffer, and the old unbounded
    // vsprintf overwrote the stack (crashes that jumped to 0x3F800000).
    char buf[4096];
    int length = vsnprintf(buf, sizeof(buf) - 8, str, ap);
    if (length < 0) return;
    length = (std::min)(length, static_cast<int>(sizeof(buf) - 8));
    memcpy(buf + length, "^7\r\n", 5);

    if (LOGFILE)
    {
        static std::mutex logLock;
        static ULONGLONG lastFlush = 0;
        std::lock_guard<std::mutex> guard(logLock);
        fputs(buf, LOGFILE);
        // Flushing every line stalls the game during long sessions; flush at most every second.
        const ULONGLONG now = GetTickCount64();
        if (now - lastFlush >= 1000)
        {
            fflush(LOGFILE);
            lastFlush = now;
        }
    }

    if (IsDebuggerPresent())
        OutputDebugStringA(buf);

    if (dbg_is_console_available && dbg_is_attached)
    {
        DWORD written;
        WriteFile(dbg_hWriteConsole, buf, static_cast<DWORD>(strlen(buf) + 1), &written, 0);
    }
}

void nlog(const char* str, ...)
{
    va_list ap;
    va_start(ap, str);
    vnlog(str, ap);
    va_end(ap);
}

void spoof_ownscontent()
{
    for (int i = 0; i < 5; i++)
    {
        *(uint8_t*)REBASE(0x148A852A + i) = 1;
    }
}

MDT_Define_FASTCALL(PTR_Content_GetAvailableContentPacks, Content_GetAvailableContentPacks_hook, uint64_t, (char a1))
{
    // Always spoof base game ownership (Campaign, MP, Zombies)
    spoof_ownscontent();
    
    // Only spoof DLC map packs if user chose to
    if (g_spoofDLCMapPacks)
    {
        auto mask = (int*)s_contentPackMetaData;
        auto v7 = (uint8_t*)REBASE(0x18FAB490);
        for (int i = 0; i < 11; i++)
        {
            *v7 = 1;

            v7 += 130;
            mask += 20;
        }
        return 0xFFFFFFFFFFFFFFFF; // All DLCs owned
    }
    
    // Return original result (only owned DLCs)
    return MDT_ORIGINAL(Content_GetAvailableContentPacks_hook, (a1)) | whiteout::installed_dlc_mask();
}

MDT_Define_FASTCALL(REBASE(0x21F0960), Com_InitMods_hook, void, ())
{
    MDT_ORIGINAL(Com_InitMods_hook, ());
    Dvar_SetFromStringByName("mods_enabled", whiteout::officialMaps ? "0" : "1");

    // FIX MP DEDICATED SERVER SEARCH (MS Store version)
    // These patches enable dedicated server searches which are disabled by default
    // The same patches are applied in steam.cpp when SteamAPI is enabled
    chgmem<uint8_t>(REBASE(0x1FEAB49 + 2), 0); // lobbyDedicatedSearchSkip: 1 -> 0
    
    // NOTE: The changelist mismatch might be why dedicateds aren't found!
    // Our EXE has changelist 20659811 but servers might expect a different one.
    // We could try patching it to match the current MS Store version if known.
    // chgmem<uint32_t>(REBASE(0x1FDE201) + 6, NEW_CHANGELIST_HERE);
    
    ALOG("Applied dedicated server search patches (lobbyDedicatedSearchSkip=0)");

    // Allow XP gain in MP custom/private games.
    Dvar_SetFromStringByName("scr_xpscalemp", "1", true);
    
}

MDT_Define_FASTCALL(REBASE(0x221A3A0), Content_DoWeHaveContentPack_hook, bool, (const char* mapName))
{
    spoof_ownscontent();
    if (whiteout::installed_dlc_map(mapName)) return true;
    if (g_spoofDLCMapPacks) {
        return true; // Full spoof - all DLCs
    }
    return MDT_ORIGINAL(Content_DoWeHaveContentPack_hook, (mapName));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x221A330), WhiteoutContentPackAvailable, bool, (int pack))
{
    if (pack > 0 && pack <= 13 && (whiteout::installed_dlc_mask() & (1u << (pack + 1)))) return true;
    return MDT_ORIGINAL(WhiteoutContentPackAvailable, (pack));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x277E4F0), WhiteoutItemOptionLocked, bool, (int mode, int controller, int item, int option))
{
    if (whiteout::progression_on() && whiteout::camos && (mode == 0 || mode == 1) && controller == 0) return false;
    return MDT_ORIGINAL(WhiteoutItemOptionLocked, (mode, controller, item, option));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x277D920), WhiteoutAttachmentLocked, bool, (int mode, int controller, int item, int attachment))
{
    if (whiteout::progression_on() && whiteout::attachments && (mode == 0 || mode == 1) && controller == 0) return false;
    return MDT_ORIGINAL(WhiteoutAttachmentLocked, (mode, controller, item, attachment));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x2029F70), WhiteoutLootQuantity, int, (lua_State* s))
{
    if (whiteout::progression_on())
    {
        const auto getInt = reinterpret_cast<int(__fastcall*)(lua_State*, int)>(REBASE(0x14E3C40));
        const auto getString = reinterpret_cast<const char*(__fastcall*)(lua_State*, int)>(REBASE(0x13D590));
        if (getInt(s, 1) == 0 && getInt(s, 3) == 0)
        {
            const int quantity = whiteout::configured_gum(getString(s, 2));
            if (quantity >= 0)
            {
                auto*& top = *reinterpret_cast<HksObject**>(reinterpret_cast<char*>(s) + 0x48);
                top->t = TNUMBER;
                top->v.number = static_cast<float>(quantity);
                ++top;
                return 1;
            }
        }
    }
    return MDT_ORIGINAL(WhiteoutLootQuantity, (s));
}

MDT_Define_FASTCALL(REBASE(0x1EED6C0), MSStore_OwnsContent_hook, bool, (const char* contentName))
{
    spoof_ownscontent();
    if (g_spoofDLCMapPacks) {
        return true; // Full spoof - all DLCs
    }
    return MDT_ORIGINAL(MSStore_OwnsContent_hook, (contentName));
}

MDT_Define_FASTCALL(REBASE(0x220F4F0), Com_MessagePrintHook, void, (int channel, int consoleType, const char* message, int other))
{
    // Asset-redundancy notices repeat thousands of times per map load and only grow the log.
    if (message && strncmp(message, "Redundant ", 10) != 0)
        ALOG("%s", message);
    MDT_ORIGINAL(Com_MessagePrintHook, (channel, consoleType, message, other));
}

MDT_Define_FASTCALL(REBASE(0x29C3A20), bdLogMessage_hook, void, (uint8_t type, const char* bchan, const char* chan, const char* file, const char* func, uint32_t line, const char* fmt, ...))
{
    va_list ap;
    char buf[4096];

    va_start(ap, fmt);

    char fmtbuf[512]{ 0 };
    const char* typeStr = type ? ((type == 2) ? "err-" : "warn") : "info";
    sprintf_s(fmtbuf, "[DW][%s][%s][@%s]%s", typeStr, chan, func, fmt);

    vnlog(fmtbuf, ap);

    va_end(ap);
}

MDT_Define_FASTCALL(REBASE(0x21F00B0), set_mod_super_hook, void, (int a1, char* fs_game, uint8_t a3))
{
    if (whiteout::officialMaps || !fs_game || !*fs_game)
    {
        char empty[] = "";
        MDT_ORIGINAL(set_mod_super_hook, (a1, empty, a3));
        return;
    }
    Mods_UpdateUsermapsList();
    Mods_UpdateModsList();

    ugcinfo_entry_wstor localinfo{ 0 };
    ugcinfo_entry_wstor* target = NULL;

    auto ugcmods = ugc_modslist;
    
    do
    {
        if (!fs_game) break;
        if (!*fs_game) break;

        for (uint32_t i = 0; i < ugcmods->num_entries; i++)
        {
            if (!strcmp(ugcmods->entries[i].ugcName, fs_game))
            {
                target = &ugcmods->entries[i];
                break;
            }
        }
    } 
    while (false);
    
    if (!target)
    {
        target = &localinfo;

        sprintf_s(localinfo.name, "%s", fs_game);
        sprintf_s(localinfo.internalName, "%s", fs_game);
        sprintf_s(localinfo.ugcName, "%s", fs_game);
        sprintf_s(localinfo.ugcPathBasic, "mods/%s", fs_game);
        sprintf_s(localinfo.ugcRoot, "%s", OsPathRoot);
        sprintf_s(localinfo.ugcPath, "%s/mods/%s", OsPathRoot, fs_game);
        localinfo.unk_4B0 = 1;

        if (fs_game)
        {
            auto v39 = *fs_game;
            for (localinfo.hash_id = 5381; *fs_game; v39 = *fs_game)
            {
                ++fs_game;
                localinfo.hash_id = tolower(v39) + 33 * localinfo.hash_id;
            }
        }
        localinfo.unk_4B8 = 1;
    }

    // mods_reinit() (inlined)
    memset((void*)REBASE(0xF2DCC60), 0, 0x4100u);
    *(uint32_t*)REBASE(0xF2E0D88) = 0;

    if (*target->ugcName)
    {
        *currently_loaded_mod = *target;
    }
    else
    {
        memset(currently_loaded_mod, 0, sizeof(ugcinfo_entry_wstor));
    }

    // mods_resetunk(a1)
    ((void(__fastcall*)(uint32_t))REBASE(0x13E4FE0))(a1);

    Mods_LoadModStart(a1, target, a3);
}

MDT_Define_FASTCALL(REBASE(0x21F0000), Dvar_Callback_ModChanged_hook, void, (void* dvar_ptr))
{
    // do nothing. its normally used to validate that fs_game was set correctly but we dont care because if someone does something stupid its their fault
}

MDT_Define_FASTCALL(REBASE(0x237EDE0), UI_ValidateText_hook, bool, ())
{
    return true;
}

MDT_Define_FASTCALL(REBASE(0x1492A50), UI_IsBadWord_hook, int, ())
{
    return 0;
}

char* custom_jpg_buf = NULL;
uint32_t custom_jpg_buf_size = 0;
char* custom_thumb_buf = NULL;
uint32_t custom_thumb_buf_size = 0;

void load_custom_image(const std::string& filename, char*& buffer, uint32_t& buffer_size)
{
    if (buffer)
    {
        delete[] buffer;
        buffer = NULL;
        buffer_size = 0;
    }

    std::ifstream ifs(filename, std::ifstream::in | std::ifstream::binary);
    if (!ifs.is_open())
    {
        return;
    }

    ifs.seekg(0, std::ios::end);
    size_t fileSize = ifs.tellg();
    ifs.seekg(0, std::ios::beg);

    buffer_size = static_cast<uint32_t>(fileSize);
    buffer = new char[buffer_size];
    ifs.read(buffer, buffer_size);

    ifs.close();
}

MDT_Define_FASTCALL(REBASE(0x26D73E0), Demo_SaveScreenshotToContentServer_hook, void, (uint32_t localClientNum, int fileSlot))
{
    load_custom_image("screenshot.jpg", custom_jpg_buf, custom_jpg_buf_size);
    load_custom_image("thumbnail.jpg", custom_thumb_buf, custom_thumb_buf_size);

    if (custom_jpg_buf)
    {
        ALOG("Uploading custom screenshot...");
        *(uint64_t*)(*(uint64_t*)REBASE(0x9ABF730) + 0x5A8490) = (uint64_t)custom_jpg_buf;
        *(uint32_t*)(*(uint64_t*)REBASE(0x9ABF730) + 0x5A8498) = custom_jpg_buf_size;
    }

    if (custom_thumb_buf) // Thumbnails are a resolution of 426x240 and smaller than 0x88000 bytes
    {
        ALOG("Uploading custom thumbnail...");
        LPVOID destination = (LPVOID)((*(uint64_t*)REBASE(0x9ABF730)) + 0x5A84A0);
        memcpy(destination, custom_thumb_buf, custom_thumb_buf_size);
        *(uint32_t*)(*(uint64_t*)REBASE(0x9ABF730) + 0x5A849C) = custom_thumb_buf_size;
    }

    MDT_ORIGINAL(Demo_SaveScreenshotToContentServer_hook, (localClientNum, fileSlot));
}

MDT_Define_FASTCALL(REBASE(0x1EFF100), Loot_GetInventoryItem_hook, uint64_t, (uint64_t a1, uint64_t a2))
{
    int v2; // er9
    __int64 v3; // rcx
    char* v4; // r10
    __int64 v5; // r8
    int v6; // edx
    __int64 v7; // rcx
    int32_t* i; // rax

    v2 = a2;
    v3 = 81064ULL * a1;
    v4 = (char*)REBASE(0xB60A130) + v3 + 856;
    if (*(int32_t*)((char*)REBASE(0xB60A130) + v3 + 80864) != 4)
        return 0;
    v5 = *(int*)((char*)REBASE(0xB60A130) + v3 + 80856);
    v6 = 0;
    if (v5 <= 0)
        return 0;
    v7 = 0;
    for (i = (int32_t*)v4; v2 != *i; i += 5)
    {
        ++v6;
        if (++v7 >= v5)
        {
            *(int*)((char*)REBASE(0xB60A130) + v3 + 80856) += 1;
            i += 5;
            *i = v2;
            i[1] = 0x7FFFFFFF;
            i[2] = 0xFFFFFFFF;
            i[3] = 0xFFFFFFFF;
            i[4] = 1;
            return (__int64)&v4[20 * v6];
        }
    }
    return (__int64)&v4[20 * v6];
}

void enable_fileshare()
{
    Dvar_SetFromStringByName("fileshare_enabled", "1");
    Dvar_SetFromStringByName("fileshareAllowDownload", "1");
    Dvar_SetFromStringByName("fileshareAllowPaintjobDownload", "1");
    Dvar_SetFromStringByName("fileshareAllowVariantDownload", "1");
    Dvar_SetFromStringByName("fileshareAllowEmblemDownload", "1");
    Dvar_SetFromStringByName("fileshareAllowDownloadingOthersFiles", "1");
}

MDT_Define_FASTCALL(REBASE(0x23C0110), anticheat_sus_peepo_hook, void, ())
{
    SPOOFED_CALL(((void(__fastcall*)())MDT_ORIGINAL_PTR(anticheat_sus_peepo_hook)));
}

MDT_Define_FASTCALL(REBASE(0x22649C0), anticheat_sus_peepo2_hook, void, ()) // probably anticheat pump
{
    SPOOFED_CALL(((void(__fastcall*)())MDT_ORIGINAL_PTR(anticheat_sus_peepo2_hook)));
}

MDT_Define_FASTCALL(REBASE(0x22C0140), anticheat_unk3_hook, uint64_t, (uint64_t a1, uint64_t a2))
{
    auto old = *(uint64_t*)REBASE(0x310C688);
    auto res = SPOOFED_CALL(((uint64_t(__fastcall*)(uint64_t, uint64_t))MDT_ORIGINAL_PTR(anticheat_unk3_hook)), a1, a2);
    *(uint64_t*)REBASE(0x310C688) = old;
    return res;
}

MDT_Define_FASTCALL(REBASE(0x22BE810), anticheat_unk4_hook, uint64_t, (uint64_t a1))
{
    auto old = *(uint64_t*)REBASE(0x310C688);
    auto res = SPOOFED_CALL(((uint64_t(__fastcall*)(uint64_t))MDT_ORIGINAL_PTR(anticheat_unk4_hook)), a1);
    *(uint64_t*)REBASE(0x310C688) = old;
    return res;
}

MDT_Define_FASTCALL(REBASE(0x22BD4C0), anticheat_unk5_hook, uint64_t, (uint64_t a1))
{
    auto old = *(uint64_t*)REBASE(0x310C688);
    auto res = SPOOFED_CALL(((uint64_t(__fastcall*)(uint64_t))MDT_ORIGINAL_PTR(anticheat_unk5_hook)), a1);
    *(uint64_t*)REBASE(0x310C688) = old;
    return res;
}

MDT_Define_FASTCALL(REBASE(0x22BC1E0), anticheat_unk6_hook, uint64_t, (uint64_t a1))
{
    auto old = *(uint64_t*)REBASE(0x310C688);
    auto res = SPOOFED_CALL(((uint64_t(__fastcall*)(uint64_t))MDT_ORIGINAL_PTR(anticheat_unk6_hook)), a1);
    *(uint64_t*)REBASE(0x310C688) = old;
    return res;
}

MDT_Define_FASTCALL((int64_t)GetProcAddress(GetModuleHandle("ntdll.dll"), "NtQuerySystemInformation"), NtQuerySystemInformation_hook, NTSTATUS, (const SYSTEM_INFORMATION_CLASS system_information_class,
    const PVOID system_information,
    const ULONG system_information_length,
    const PULONG return_length))
{
    uint64_t faultingModule;
    RtlPcToFileHeader((PVOID)_ReturnAddress(), (PVOID*)&faultingModule);

    if (faultingModule && faultingModule == REBASE(0))
    {
        ALOG("SLEEPY ANTICHEAT ACTIVATED");
        while (1)
        {
            Sleep(100000);
        }
    }

    auto result = MDT_ORIGINAL(NtQuerySystemInformation_hook, (system_information_class, system_information, system_information_length, return_length));
    return result;
}

MDT_Define_FASTCALL((int64_t)GetProcAddress(GetModuleHandle("ntdll.dll"), "NtSetInformationThread"), NtSetInformationThread_hook, NTSTATUS, (HANDLE ThreadHandle, THREAD_INFORMATION_CLASS ThreadInformationClass, PVOID ThreadInformation, ULONG ThreadInformationLength))
{
    if (ThreadInformationClass == 0x11) // ThreadHideFromDebugger
    {
        if (ThreadInformationLength)
        {
            return -1;
        }

        if ((int64_t)ThreadHandle != -2)
        {
            return -1;
        }
        
        // ALOG("CAUGHT ThreadHideFromDebugger @ %p thread %u", ((uint64_t)_ReturnAddress()) - REBASE(0), GetCurrentThreadId());
        return 0;
    }

    return MDT_ORIGINAL(NtSetInformationThread_hook, (ThreadHandle, ThreadInformationClass, ThreadInformation, ThreadInformationLength));
}

bool NtQueryInformationThread_hook_disable = false;
MDT_Define_FASTCALL((int64_t)GetProcAddress(GetModuleHandle("ntdll.dll"), "NtQueryInformationThread"), NtQueryInformationThread_hook, NTSTATUS, (HANDLE ThreadHandle, uint32_t ThreadInformationClass, PVOID ThreadInformation, ULONG ThreadInformationLength, PULONG ReturnLength))
{
    if (!NtQueryInformationThread_hook_disable && (ThreadInformationClass == 0x11)) // ThreadHideFromDebugger
    {
        if (ThreadInformationLength != 1)
        {
            return -1;
        }

        *(uint8_t*)ThreadInformation = 1;

        return 0;
    }

    return MDT_ORIGINAL(NtQueryInformationThread_hook, (ThreadHandle, ThreadInformationClass, ThreadInformation, ThreadInformationLength, ReturnLength));
}

MDT_Define_FASTCALL((int64_t)GetProcAddress(GetModuleHandle("kernelbase.dll"), "VirtualAlloc"), VirtualPretend, uint64_t, (LPVOID lpAddress, SIZE_T dwSize, DWORD flAllocationType, DWORD flProtect))
{
    if ((int64_t)_ReturnAddress() == REBASE(0x225920D) // in TLS callback
        || (int64_t)_ReturnAddress() == REBASE(0x23C583B) // in memory initializer
        || (int64_t)_ReturnAddress() == REBASE(0x2269D9D) // anticheat2.1-
        || (int64_t)_ReturnAddress() == REBASE(0x22A05D2) // anticheat2.2
        ) 
    {
        ALOG("VirtualPretend succeeded!");
        return (uint64_t)GetModuleHandleA("ntdll.dll") + 0x1000;
    }

    return MDT_ORIGINAL(VirtualPretend, (lpAddress, dwSize, flAllocationType, flProtect));
}

MDT_Define_FASTCALL(REBASE(0x736E0), anticheat_unk7_hook, void, ())
{
    // ALOG("anticheat unk7 invoked!!"); // apparently this guy isnt important LMAO
}

MDT_Define_FASTCALL(REBASE(0x77B60), anticheat_unk10_hook, void, ())
{
    // ALOG("anticheat unk10 invoked!!"); // apparently this guy isnt important LMAO
}

MDT_Define_FASTCALL(REBASE(0x7BBF0), anticheat_unk11_hook, void, ())
{
    // ALOG("anticheat unk11 invoked!!"); // apparently this guy isnt important LMAO
}

lua_State* g_whiteoutUiState = nullptr;

MDT_Define_FASTCALL(REBASE(0x1E54EF0), lua_pcall_hook, uint32_t, (lua_State* s, uint32_t nargs, uint32_t nresults, uint32_t errfunc))
{
    auto res = MDT_ORIGINAL(lua_pcall_hook, (s, nargs, nresults, errfunc));
    
    const char* sourceData = NULL;
    const char* extraData = NULL;
    if ((uint64_t)_ReturnAddress() == REBASE(0x202C560)) // ui.uieditor.conditions
    {
        // The menu Lua state is rebuilt on every map change; only a state created in the main menus is
        // kept for WhiteoutTick (see EveryFrameHook), and it is forgotten as soon as a map loads.
        g_whiteoutUiState = whiteout::in_frontend() ? s : nullptr;
        whiteout::load_catalog(g_iniPath);
        if (whiteout::progression_on() && whiteout::camos)
            extraData =
"EnableGlobals()\n\
Engine.IsItemOptionLocked = function() return false end\n\
Engine.GetItemOptionLocked = function() return false end\n\
Engine.IsCamoLocked = function() return false end\n\
Engine.GetIsEliteLockedForItemOption = function() return false end\n\
-- Dark Matter is listed only when its mastery challenge (Diamond in every class) is complete.\n\
local whiteoutDarkMatter = nil\n\
local function whiteoutFindDarkMatter()\n\
    whiteoutDarkMatter = {}\n\
    for mode, folder in pairs({ [0] = 'cp', [1] = 'mp', [2] = 'zm' }) do\n\
        for n = 1, 6 do\n\
            local file = 'gamedata/stats/' .. folder .. '/statsmilestones' .. n .. '.csv'\n\
            local ok, rows = pcall(Engine.TableFindRows, file, 9, 'camo_darkmatter')\n\
            if ok and type(rows) == 'table' then\n\
                for _, row in pairs(rows) do\n\
                    local index = tonumber(Engine.TableLookupGetColumnValueForRow(file, row, 0))\n\
                    if index then whiteoutDarkMatter[mode .. ':' .. index] = true end\n\
                end\n\
            end\n\
        end\n\
    end\n\
end\n\
local whiteoutChallengeComplete = Engine.GetChallengeCompleteForChallengeIndex\n\
Engine.GetChallengeCompleteForChallengeIndex = function(mode, controller, index, ...)\n\
    if not whiteoutDarkMatter then whiteoutFindDarkMatter() end\n\
    if whiteoutDarkMatter[tostring(mode) .. ':' .. tostring(index)] then return true end\n\
    return whiteoutChallengeComplete(mode, controller, index, ...)\n\
end";
        ALOG("lua_pcall_hook: injecting conditions block (IsServerBrowserEnabled=true) at RA=%llx", (uint64_t)_ReturnAddress());
        if (g_enableUnlockAllItems)
        {
            sourceData = 
"EnableGlobals()\n\
function IsServerBrowserEnabled() return true end\n\
\n\
-- Entitlement Override (CRITICAL)\n\
Engine.IsEntitlementValid = function(entitlementId) return true end\n\
Engine.GetEntitlementInfo = function(itemId, entitlementType) return {entitled=true, locked=false, new=false, count=999} end\n\
\n\
-- Weapon Token Bypass\n\
Engine.IsWeaponOptionLockedEntitlement = function() return false end\n\
Engine.DoesPlayerHaveWeapon = function(weaponId) return true end\n\
Engine.GetWeaponTokenCount = function(weaponId) return 999 end\n\
Engine.GetPlayerWeaponTokenCount = function(playerIndex, weaponId) return 999 end\n\
Engine.GetWeaponUnlockCost = function(weaponId) return 0 end\n\
Engine.IsItemLocked = function() return false end\n\
Engine.IsLootItemLocked = function() return false end\n\
Engine.IsWeaponLocked = function() return false end\n\
Engine.GetItemUnlockLevel = function() return 0 end\n\
\n\
-- Attachment Unlock Bypass\n\
Engine.IsAttachmentLocked = function(weaponId, attachmentId) return false end\n\
Engine.DoesPlayerHaveAttachment = function(weaponId, attachmentId) return true end\n\
Engine.GetAttachmentTokenCount = function(weaponId, attachmentId) return 999 end\n\
Engine.IsAttachmentNew = function() return false end\n\
\n\
-- Loot & Black Market\n\
Engine.GetLootTokenCount = function(lootItemId) return 999 end\n\
\n\
-- Specialist/Hero Unlocks\n\
Engine.IsHeroItemLocked = function(heroId, itemId) return false end\n\
Engine.IsHeroAbilityLocked = function(heroId, abilityId) return false end\n\
Engine.GetHeroTokenCount = function(heroId, itemId) return 999 end\n\
Engine.IsSpecialistLocked = function() return false end\n\
\n\
-- Cosmetic Unlocks\n\
Engine.IsCamoLocked = function(weaponId, camoId) return false end\n\
Engine.IsReticleLocked = function(reticleId) return false end\n\
Engine.IsCallingCardLocked = function(cardId) return false end\n\
Engine.IsBackgroundLocked = function() return false end\n\
Engine.IsEmblemLocked = function() return false end\n\
Engine.IsEmblemBackgroundLocked = function() return false end\n\
Engine.GetCosmeticTokenCount = function(cosmeticId) return 999 end\n\
Engine.GetPlayerCallingCardIndex = function() return 0 end\n\
Engine.GetCallingCardUnlockData = function() return true end\n\
Engine.IsReticleNew = function() return false end\n\
Engine.IsCamoNew = function() return false end\n\
\n\
-- Challenge & Prestige Bypass\n\
Engine.IsChallengeLocked = function(challengeId) return false end\n\
Engine.IsPrestigeItemLocked = function(prestigeId) return false end\n\
Engine.GetChallengeTokenCount = function(challengeId) return 999 end\n\
\n\
-- Gesture/Taunt Unlocks\n\
Engine.IsGestureLocked = function() return false end\n\
Engine.IsTauntLocked = function() return false end\n\
Engine.IsGestureNew = function() return false end\n\
\n\
-- Emblem Unlocks\n\
local defaultEmblemsCached = nil\n\
local function getAllDefaults()\n\
if defaultEmblemsCached ~= nil then return defaultEmblemsCached end\n\
local tbl = {}\n\
for i = 0, 750, 1 do\n\
local desc = Engine.TableLookupGetColumnValueForRow( \"gamedata/emblems/backgrounds.csv\", i, 4 )\n\
if Engine.TableLookupGetColumnValueForRow( \"gamedata/emblems/backgrounds.csv\", i, 9 ) == \"default\" and \"EM_BACK_CWL_default\" ~= desc then\n\
local curTbl = {}\n\
curTbl.id = i\n\
curTbl.description = Engine.Localize(desc)\n\
curTbl.isBGLocked = false\n\
curTbl.entitlement = nil\n\
curTbl.isContractBg = false\n\
table.insert(tbl, curTbl)\n\
end\n\
end\n\
defaultEmblemsCached = tbl\n\
return tbl\n\
end\n\
local oldGBFCN = Engine.GetBackgroundsForCategoryName\n\
Engine.GetBackgroundsForCategoryName = function(arg0, arg1)\n\
if arg1 == \"default\" then\n\
return getAllDefaults()\n\
end\n\
return oldGBFCN(arg0, arg1)\n\
end";
        }
        else
        {
            sourceData = "EnableGlobals()\n\
function IsServerBrowserEnabled() return true end";
        }
    }

    if (g_enableUnlockAllItems && (uint64_t)_ReturnAddress() == REBASE(0x202CB5F)) // ui.ffotd_tu32
    {
        ALOG("lua_pcall_hook: injecting ffotd_tu32 unlocks at RA=%llx", (uint64_t)_ReturnAddress());
        sourceData =
            "EnableGlobals()\n\
require(\"ui.t7.utility.storeutility\")\n\
\n\
-- Entitlement Override (CRITICAL)\n\
Engine.IsEntitlementValid = function(entitlementId) return true end\n\
Engine.GetEntitlementInfo = function(itemId, entitlementType) return {entitled=true, locked=false, new=false, count=999} end\n\
\n\
-- Weapon Token Bypass\n\
Engine.IsWeaponOptionLockedEntitlement = function() return false end\n\
Engine.DoesPlayerHaveWeapon = function(weaponId) return true end\n\
Engine.GetWeaponTokenCount = function(weaponId) return 999 end\n\
Engine.GetPlayerWeaponTokenCount = function(playerIndex, weaponId) return 999 end\n\
Engine.GetWeaponUnlockCost = function(weaponId) return 0 end\n\
Engine.IsItemLocked = function() return false end\n\
Engine.IsLootItemLocked = function() return false end\n\
Engine.IsWeaponLocked = function() return false end\n\
Engine.GetItemUnlockLevel = function() return 0 end\n\
\n\
-- Attachment Unlock Bypass\n\
Engine.IsAttachmentLocked = function(weaponId, attachmentId) return false end\n\
Engine.DoesPlayerHaveAttachment = function(weaponId, attachmentId) return true end\n\
Engine.GetAttachmentTokenCount = function(weaponId, attachmentId) return 999 end\n\
Engine.IsAttachmentNew = function() return false end\n\
\n\
-- Loot & Black Market\n\
Engine.GetLootTokenCount = function(lootItemId) return 999 end\n\
\n\
-- Specialist/Hero Unlocks\n\
Engine.IsHeroItemLocked = function(heroId, itemId) return false end\n\
Engine.IsHeroAbilityLocked = function(heroId, abilityId) return false end\n\
Engine.GetHeroTokenCount = function(heroId, itemId) return 999 end\n\
Engine.IsSpecialistLocked = function() return false end\n\
\n\
-- Cosmetic Unlocks\n\
Engine.IsCamoLocked = function(weaponId, camoId) return false end\n\
Engine.IsReticleLocked = function(reticleId) return false end\n\
Engine.IsCallingCardLocked = function(cardId) return false end\n\
Engine.IsBackgroundLocked = function() return false end\n\
Engine.IsEmblemLocked = function() return false end\n\
Engine.IsEmblemBackgroundLocked = function() return false end\n\
Engine.GetCosmeticTokenCount = function(cosmeticId) return 999 end\n\
Engine.GetPlayerCallingCardIndex = function() return 0 end\n\
Engine.GetCallingCardUnlockData = function() return true end\n\
Engine.IsReticleNew = function() return false end\n\
Engine.IsCamoNew = function() return false end\n\
\n\
-- Challenge & Prestige Bypass\n\
Engine.IsChallengeLocked = function(challengeId) return false end\n\
Engine.IsPrestigeItemLocked = function(prestigeId) return false end\n\
Engine.GetChallengeTokenCount = function(challengeId) return 999 end\n\
\n\
-- Gesture/Taunt Unlocks\n\
Engine.IsGestureLocked = function() return false end\n\
Engine.IsTauntLocked = function() return false end\n\
Engine.IsGestureNew = function() return false end\n\
\n\
-- Black Market/Store Unlocks\n\
CoD.BlackMarketUtility.IsItemLocked = function(self, itemId) return false end\n\
CoD.BlackMarketUtility.GetItemCount = function(self, itemId) return 999 end\n\
CoD.BlackMarketUtility.GetItemTokenCount = function(self, itemId) return 999 end\n\
CoD.BlackMarketUtility.IsItemNew = function() return false end\n\
CoD.BlackMarketUtility.IsItemPurchased = function() return true end\n\
CoD.StoreUtility.IsInventoryItemPurchased = function(self, itemId) return true end\n\
CoD.StoreUtility.GetInventoryItemCost = function(self, itemId) return 0 end\n\
CoD.StoreUtility.GetInventoryItemCount = function(self, itemId) return 999 end\n\
CoD.StoreUtility.IsInventoryItemVisible = function() return true end\n\
CoD.StoreUtility.IsInventoryItemLocked = function() return false end\n\
CoD.StoreUtility.IsInventoryItemNew = function() return false end\n\
\n\
-- Calling Cards\n\
CoD.SpecialCallingCards = {}";
    }

    // Optional developer script: whiteout-debug.lua next to the game exe is run in the menus.
    static std::string debugScript;
    const char* debugData = NULL;
    if ((uint64_t)_ReturnAddress() == REBASE(0x202C560))
    {
        char exePath[MAX_PATH] = {0};
        if (GetModuleFileNameA(NULL, exePath, sizeof(exePath)))
        {
            if (char* slash = strrchr(exePath, '\\')) *slash = '\0';
            std::string path = std::string(exePath) + "\\whiteout-debug.lua";
            if (FILE* f = fopen(path.c_str(), "rb"))
            {
                debugScript.clear();
                char buffer[4096];
                size_t read;
                while ((read = fread(buffer, 1, sizeof(buffer), f)) > 0 && debugScript.size() < 262144)
                    debugScript.append(buffer, read);
                fclose(f);
                if (!debugScript.empty()) debugData = debugScript.c_str();
                ALOG("lua_pcall_hook: running whiteout-debug.lua (%zu bytes)", debugScript.size());
            }
        }
    }

    for (const char* chunk : { sourceData, extraData, debugData })
    {
        if (!chunk) continue;
        *(char*)(*(uint64_t*)((uint64_t)s + 16) + 472) = HKS_BYTECODE_SHARING_ON;
        HksCompilerSettings hks_compiler_settings;
        int result = hksi_hksL_loadbuffer(s, &hks_compiler_settings, chunk, strlen(chunk), chunk);
        if (!result)
        {
            result = hks::vm_call_internal(s, 0, 0, 0);
        }
        *(char*)(*(uint64_t*)((uint64_t)s + 16) + 472) = HKS_BYTECODE_SHARING_SECURE;
    }

    return res;
}

// Runs a small chunk in the menu Lua state. The chunk itself uses pcall, so a Lua error cannot unwind
// through the game; an access violation (stale state) is caught here and drops the state.
static void whiteout_run_ui_chunk(lua_State* s, const char* chunk)
{
    *(char*)(*(uint64_t*)((uint64_t)s + 16) + 472) = HKS_BYTECODE_SHARING_ON;
    HksCompilerSettings hks_compiler_settings;
    int result = hksi_hksL_loadbuffer(s, &hks_compiler_settings, chunk, strlen(chunk), chunk);
    if (!result)
        hks::vm_call_internal(s, 0, 0, 0);
    *(char*)(*(uint64_t*)((uint64_t)s + 16) + 472) = HKS_BYTECODE_SHARING_SECURE;
}

static void whiteout_ui_tick()
{
    static ULONGLONG next = 0;
    if (!whiteout::in_frontend())
    {
        g_whiteoutUiState = nullptr;
        return;
    }
    const ULONGLONG now = GetTickCount64();
    if (!g_whiteoutUiState || now < next) return;
    next = now + 200;
    whiteout::guardDepth++;
    __try
    {
        whiteout_run_ui_chunk(g_whiteoutUiState, "if WhiteoutTick then pcall(WhiteoutTick) end");
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        g_whiteoutUiState = nullptr;
    }
    whiteout::guardDepth--;
}

int Lua_ResetEmblemsCache(void* s)
{
    ALOG("Resetting emblems cache...");
    BG_ResetEmblemsCache();
    return 1;
}

int Lua_GetServerList(void* s)
{
    ALOG("Lua_GetServerList called");
    // For now, just return an empty table
    // TODO: properly implement this with HKS API for table creation
    return 0;
}

int Lua_RefreshServers(void* s)
{
    ALOG("Lua_RefreshServers called");
    server_browser::refresh_from_game();
    return 0;
}

int Lua_JoinServer(void* s)
{
    ALOG("Lua_JoinServer called");
    // TODO: extract IP from Lua stack and call server_browser::join()
    return 0;
}

int Lua_WhiteoutLog(void* s)
{
    const auto getString = reinterpret_cast<const char*(__fastcall*)(void*, int)>(REBASE(0x13D590));
    if (const char* text = getString(s, 1)) ALOG("%s", text);
    return 0;
}

int Lua_AddCustomServer(void* s)
{
    ALOG("Lua_AddCustomServer called");
    // TODO: extract server info from Lua table and call server_browser::add_custom_server()
    return 0;
}

MDT_Define_FASTCALL(PTR_hksI_openlib, hksI_openlib_hook, void, (lua_State* s, const char* libname, const luaL_Reg l[], int nup, int isHksFunc))
{
    if ((uint64_t)_ReturnAddress() == REBASE(0x1E49523)) // just some random call to openlib, only need it once
    {
        const luaL_Reg lib[] =
        {
            {"ResetEmblemsCache", Lua_ResetEmblemsCache},
            {"GetServerList", Lua_GetServerList},
            {"RefreshServers", Lua_RefreshServers},
            {"JoinServer", Lua_JoinServer},
            {"AddCustomServer", Lua_AddCustomServer},
            {"Log", Lua_WhiteoutLog},
            {nullptr, nullptr}
        };

        ALOG("Registering BO3Enhanced lua functions...");

        MDT_ORIGINAL(hksI_openlib_hook, (s, "BO3Enhanced", lib, 0, 1));
    }

    MDT_ORIGINAL(hksI_openlib_hook, (s, libname, l, nup, isHksFunc));
}

void patch_lua()
{
    // disable server browser
    MDT_Activate(lua_pcall_hook);
}

void kill_gamechat()
{
    //noprange(REBASE(0x1EE81E1), 0x1EE81FB - 0x1EE81E1); // skip init
    //noprange(REBASE(0x1EE829B), 0x1EE82B5 - 0x1EE829B); // skip user creation
    //noprange(REBASE(0x1EE82BD), 0x1EE82C4 - 0x1EE82BD); // skip user creation 2

    noprange(REBASE(0x1EE864A), 0x1EE8664 - 0x1EE864A); // kill uninit
}

uint64_t fakeMemoryAddress = 0;

static DWORD WINAPI console_input_thread(_In_ LPVOID)
{
    for (;;)
    {
        char line[1024] = { 0 };
        if (!fgets(line, sizeof(line), stdin))
        {
            Sleep(50);
            continue;
        }

        // Trim CR/LF
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r'))
        {
            line[len - 1] = '\0';
            len--;
        }

        if (len == 0)
        {
            continue;
        }

        char cmd[1100] = { 0 };
        snprintf(cmd, sizeof(cmd), "%s\n", line);
        Cbuf_AddText(cmd);
    }
    return 0;
}

void ac_bypass_allocconsole()
{
    MDT_Activate(anticheat_sus_peepo_hook);
    MDT_Activate(anticheat_sus_peepo2_hook);
    MDT_Activate(anticheat_unk3_hook);
    MDT_Activate(anticheat_unk4_hook);
    MDT_Activate(anticheat_unk5_hook);
    MDT_Activate(anticheat_unk6_hook);
    MDT_Activate(NtQuerySystemInformation_hook);
    MDT_Activate(NtSetInformationThread_hook);
    MDT_Activate(NtQueryInformationThread_hook);
    MDT_Activate(VirtualPretend);
    MDT_Activate(anticheat_unk7_hook);
    MDT_Activate(anticheat_unk10_hook);
    MDT_Activate(anticheat_unk11_hook);

    // anticheat intitializer constants
    *(uint64_t*)REBASE(0x8E57400) = 0x7331D36860FF300D;
    *(uint64_t*)REBASE(0x8E57408) = 0x0D092BEA64EC3D509;
    *(uint64_t*)REBASE(0x8E573A8) = 0x0FD802801C8DEB7BD;

    chgmem<uint8_t>(REBASE(0x22C7FE0), 0xC3); // anticheat_unk13_THREADED can just be nuked lol
    chgmem<uint8_t>(REBASE(0x22F04C0), 0xC3); // anticheat_8 which is threaded
    chgmem<uint8_t>(REBASE(0x22628C0), 0xC3); // anticheat_2_thread
    chgmem<uint8_t>(REBASE(0x2263AC0), 0xC3); // AnticheatOnDLLLoad (called with LdrRegisterDllNotification in anticheat init)

    // skip where they copy the data into the ntdll binary in...
    chgmem<uint8_t>(REBASE(0x22595C4), 0xEB); // TLS callback
    chgmem<uint8_t>(REBASE(0x23C5C01), 0xEB); // memory init
    chgmem<uint8_t>(REBASE(0x226A15C), 0xEB); // anticheat2.1
    chgmem<uint8_t>(REBASE(0x22A09E9), 0xEB); // anticheat2.2

    // patch where they usually set threadinfo (anti-debug)
    unsigned char xorRaxRax[] = { 0x48, 0x31, 0xC0 };
    unsigned char xorEaxEax[] = { 0x31, 0xC0 };
    chgmem(REBASE(0x225B381), sizeof(xorRaxRax), xorRaxRax); 
    chgmem(REBASE(0x226B921), sizeof(xorEaxEax), xorEaxEax); // only 2 bytes here but they test eax :)
    chgmem(REBASE(0x23D3326), sizeof(xorRaxRax), xorRaxRax);
    
    // CreateRemoteThread check (225A173)
    noprange(REBASE(0x22598FA), 0x22598FC - 0x22598FA);
    noprange(REBASE(0x2259904), 0x2259906 - 0x2259904);
    chgmem<uint16_t>(REBASE(0x225990F), 0xE990);

    // you WILL keep running game. i DEMAND IT
    noprange(REBASE(0x23F120E), 0x23F1210 - 0x23F120E);
    noprange(REBASE(0x23F1217), 0x23F1219 - 0x23F1217);
    noprange(REBASE(0x23F1232), 0x23F1234 - 0x23F1232);
    noprange(REBASE(0x23F0FC0), 0x23F0FC6 - 0x23F0FC0);
    noprange(REBASE(0x23F0D6A), 0x23F0D70 - 0x23F0D6A);

    chgmem<uint16_t>(REBASE(0x23F123F), 0xE990);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x23F123F), 2);

    // patch CheckRemoteDebuggerPresent
    noprange(REBASE(0x2282470), 0x2282476 - 0x2282470);
    chgmem<uint32_t>(REBASE(0x2282470), 0x90c03148); // xor rax, rax; nop
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x2282470), 1);

    // remove mutex checks (allows more than 1 of the game to run)
    noprange(REBASE(0x226D85A), 0x226D860 - 0x226D85A);
    noprange(REBASE(0x226CF83), 0x226CF85 - 0x226CF83);
    noprange(REBASE(0x226E40F), 0x226E415 - 0x226E40F);
    noprange(REBASE(0x226DE83), 0x226DE85 - 0x226DE83);

    if (g_enableConsole)
    {
        // Always try to provide a usable console (even if a debugger is attached).
    // If a console already exists, AllocConsole will fail harmlessly.
    if (!GetConsoleWindow())
    {
        AllocConsole();
    }

    FILE* fDummy;
    freopen_s(&fDummy, "CONIN$", "r", stdin);
    freopen_s(&fDummy, "CONOUT$", "w", stderr);
    freopen_s(&fDummy, "CONOUT$", "w", stdout);

    // Allow simple command entry from the Windows console.
    // Lines typed here are forwarded into the game's command buffer.
        CreateThread(NULL, 0, console_input_thread, NULL, 0, NULL);
    }
}

uint64_t gfxVertexDeclLck = 0;
bool gfxVertexDeclPatchLck = false;
MDT_Define_FASTCALL(REBASE(0x1DE9B10), R_UpdateVertexDecl_hook, void, (uint64_t gfxstate))
{
    MDT_ORIGINAL(R_UpdateVertexDecl_hook, (gfxstate));
    auto v1 = *(uint64_t*)(gfxstate + 7544);
    auto v3 = *(uint64_t*)(v1 + 8);
    auto mat = **(uint64_t**)(gfxstate + 0x1D60);

    /*if (strcmp((char*)mat, "lensflare"))
    {
        return;
    }*/

    if (v3)
    {
        auto vert = *(int*)(gfxstate + 0x40C8);
        uint64_t res = *(uint64_t*)(v3 + 8llu * vert + 24llu);
        uint64_t shader;
        if (*(uint32_t*)v1 == 2)
            shader = *(uint64_t*)(v1 + 48);
        else
            shader = *(uint64_t*)(v1 + 24);

        if (!res)
        {
            ALOG("%s FAILURE: res %p vert %u mat %p v3 %p shader %p", (char*)mat, (uint32_t)res, vert, mat, v3, shader);
            SuspendProcess();
        }
        else if (!(gfxVertexDeclLck % 250))
        {
            if (!gfxVertexDeclPatchLck)
            {
                gfxVertexDeclPatchLck = true;
                DWORD old;
                VirtualProtect((LPVOID)v3, 255, PAGE_READONLY, &old);
            }
        }
    }

    ++gfxVertexDeclLck;
}

/* LOL!!!

mov    DWORD PTR [rdi],eax
movabs rax,0x11223344556677
mov    rdx,QWORD PTR [rbx+0x8]
mov    ecx,DWORD PTR [rbx]
mov    r8,rbx
call   rax
jmp    rax

*/
char zone_patch_asm[] = { 0x89, 0x07, 0x48, 0xB8, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0x48, 0x8B, 0x53, 0x08, 0x8B, 0x0B, 0x49, 0x89, 0xD8, 0xFF, 0xD0, 0xFF, 0xE0 };
bool no_print_debug = false;
const char* unload_prevention_list[] = {
    "wc/lit_detail_advanced#882572b8",
    "wc/lit_decal_reveal#882572b8"
};
int num_unload_preventions = sizeof(unload_prevention_list) / sizeof(unload_prevention_list[0]);
uint64_t DB_FreeXAssetHeader_hook(uint8_t assetType, uint64_t header, uint64_t entry)
{
    if (assetType == xasset_techset)
    {
        auto name = *(char**)header;
        if (name)
        {
            for (int i = 0; i < num_unload_preventions; i++) {
                if (strcmp(name, unload_prevention_list[i]) == 0)
                    return REBASE(0x14E0DA3);
            }
        }
    }

    // call original DB_FreeXAssetHeader
    ((void(__fastcall*)(uint32_t, uint64_t))REBASE(0x14DA3A0))(assetType, header);
    return REBASE(0x14E0D8C); // original return address
}

MDT_Define_FASTCALL(REBASE(0x14DA3A0), DB_FreeXAssetHeader_hook_2, void, (uint8_t assetType, uint64_t header))
{
    // ALOG("UNLOAD INFO %u", (uint32_t)assetType);
    if (xasset_techset == assetType || xasset_material == assetType || xasset_computeshaderset == assetType)
    {
        ALOG("UNLOAD INFO %u name: %s", (uint32_t)assetType, *(char**)header);
    }

    MDT_ORIGINAL(DB_FreeXAssetHeader_hook_2, (assetType, header));
}

MDT_Define_FASTCALL(REBASE(0x1F009E0), Live_SystemInfo_Hook, bool, (int controllerIndex, int infoType, char* outputString, const int outputLen))
{
    if (g_disableSecurityHooks)
    {
        return MDT_ORIGINAL(Live_SystemInfo_Hook, (controllerIndex, infoType, outputString, outputLen));
    }

    if (infoType)
    {
        return MDT_ORIGINAL(Live_SystemInfo_Hook, (controllerIndex, infoType, outputString, outputLen));
    }

    if (is_on_linux())
        snprintf(outputString, outputLen, "%s (Wine)", VERSION_STRING);
    else
        strcpy_s(outputString, outputLen, VERSION_STRING);
    return true;
}

// Always return true for content ownership checks to allow MS Store/Xbox users

MDT_Define_FASTCALL(REBASE(0x291F620), Content_HasEntitlementOwnershipByRef_hook, uint8_t, ())
{
    if (g_spoofDLCMapPacks) {
        return 1; // Full spoof - all DLCs
    }
    return MDT_ORIGINAL(Content_HasEntitlementOwnershipByRef_hook, ()); // Base only - let game check real ownership
}

// if found in current directory, use cd *first* instead of having the xbox override paths first
MDT_Define_FASTCALL(REBASE(0x1EED4E0), FS_FindXZone_hook, const char*, (const char* filename))
{
    static thread_local char cd[MAX_PATH]{ 0 };
    char file[MAX_PATH]{ 0 };

    memset(cd, 0, sizeof(cd));
    memset(file, 0, sizeof(file));

    if (!_getcwd(cd, MAX_PATH))
    {
        return MDT_ORIGINAL(FS_FindXZone_hook, (filename));
    }

    strcat_s(cd, "/zone");
    sprintf_s(file, "%s/%s", cd, filename);

    if (std::filesystem::exists(file))
    {
        return cd;
    }

    return MDT_ORIGINAL(FS_FindXZone_hook, (filename));
}

// DDL Hooks from Ghidra (MS Store) - using REBASE for proper address calculation
// Track last DDL field accessed (thread-local for safety)
thread_local char g_lastDDLName[128] = {0};
thread_local char g_lastDDLPath[256] = {0};

MDT_Define_FASTCALL(REBASE(0x14E34D0), DDL_MoveToName_hook, uint64_t, (uint64_t fromState, char* toState, const char* name))
{
    if (name)
    {
        strncpy(g_lastDDLName, name, sizeof(g_lastDDLName) - 1);
        g_lastDDLName[sizeof(g_lastDDLName) - 1] = '\0';
    }
    if (g_enableUnlockAllItems)
    {
        static int logCountMoveName = 0;
        if (logCountMoveName++ < 25 && name)
        {
            ALOG("DDL_MoveToName: %s", name);
        }
    }
    return MDT_ORIGINAL(DDL_MoveToName_hook, (fromState, toState, name));
}

MDT_Define_FASTCALL(REBASE(0x25E71D0), DDL_MoveToPath_hook, uint64_t, (uint64_t fromState, char* toState, int depth, const char** path))
{
    if (path && depth > 0 && depth < 64)
    {
        g_lastDDLPath[0] = '\0';
        for (int i = 0; i < depth; ++i)
        {
            const char* seg = path[i];
            if (!seg) break;
            // ensure segment is reasonably sized
            size_t seglen = strnlen(seg, 128);
            if (seglen == 0) continue;
            size_t curLen = strlen(g_lastDDLPath);
            if (curLen + seglen + 2 < sizeof(g_lastDDLPath))
            {
                if (curLen > 0) strcat_s(g_lastDDLPath, sizeof(g_lastDDLPath), "/");
                strncat_s(g_lastDDLPath, sizeof(g_lastDDLPath), seg, seglen);
            }
        }
        if (g_enableUnlockAllItems)
        {
            static int logCountMovePath = 0;
            if (logCountMovePath++ < 20)
            {
                ALOG("DDL_MoveToPath: %s", g_lastDDLPath);
            }
        }
    }
    return MDT_ORIGINAL(DDL_MoveToPath_hook, (fromState, toState, depth, path));
}

MDT_Define_FASTCALL(REBASE(0x25E7320), DDL_GetUInt_hook, uint64_t, (char* param_1, uint64_t* param_2))
{
    if (g_enableUnlockAllItems)
    {
        // If reading known token fields, force large value
        auto contains = [](const char* hay, const char* needle) -> bool {
            if (!hay || !needle) return false;
            size_t nlen = strlen(needle);
            size_t hlen = strlen(hay);
            if (nlen == 0 || hlen == 0 || nlen > hlen) return false;
            for (size_t i = 0; i + nlen <= hlen; ++i)
            {
                bool match = true;
                for (size_t j = 0; j < nlen; ++j)
                {
                    char a = hay[i + j];
                    char b = needle[j];
                    if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                    if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                    if (a != b) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        };

        bool isTokenField = false;
        if (g_lastDDLName[0] != '\0')
        {
            if (contains(g_lastDDLName, "token") || contains(g_lastDDLName, "unlock"))
                isTokenField = true;
        }
        if (g_lastDDLPath[0] != '\0')
        {
            if (contains(g_lastDDLPath, "token") || contains(g_lastDDLPath, "unlock"))
                isTokenField = true;
            if (contains(g_lastDDLPath, "mp/PlayerData/UnlockTokens"))
                isTokenField = true;
        }
        // Additional common fields observed in CoD DDLs
        if (contains(g_lastDDLPath, "mp/PlayerData/UnlockTokens") || contains(g_lastDDLName, "UnlockTokens"))
            isTokenField = true;

        if (isTokenField)
        {
            static int logTokenReads = 0;
            if (logTokenReads++ < 20)
            {
                ALOG("DDL_GetUInt override: name=%s path=%s -> 999", g_lastDDLName, g_lastDDLPath);
            }
            return 999;
        }

        uint64_t result = MDT_ORIGINAL(DDL_GetUInt_hook, (param_1, param_2));
        static int logCount = 0;
        if (logCount++ < 20)
        {
            ALOG("DDL_GetUInt: name=%s path=%s result=%llu", g_lastDDLName, g_lastDDLPath, result);
        }
        return result;
    }
    return MDT_ORIGINAL(DDL_GetUInt_hook, (param_1, param_2));
}

// Temporarily disable SetUInt hook to improve stability
// MDT_Define_FASTCALL(REBASE(0x25E7290), DDL_SetUInt_hook, uint64_t, (char* param_1, uint64_t* param_2, uint64_t value))
// {
// #if DWINVENTORY_UNLOCK_ALL
//     static int logCount = 0;
//     if (logCount++ < 10)
//     {
//         ALOG("DDL_SetUInt: name=%s path=%s value=%llu", g_lastDDLName, g_lastDDLPath, value);
//     }
// #endif
//     return MDT_ORIGINAL(DDL_SetUInt_hook, (param_1, param_2, value));
// }

MDT_Define_FASTCALL(REBASE(0x1FA52B0), LiveStats_Core_GetDDLContext_hook, uint64_t, (uint32_t ControllerIndex, int mode))
{
#if DWINVENTORY_UNLOCK_ALL
    static int logCount = 0;
    if (logCount++ < 10)
    {
        ALOG("LiveStats_Core_GetDDLContext called: ControllerIndex=%u, mode=%d", ControllerIndex, mode);
    }
#endif
    return MDT_ORIGINAL(LiveStats_Core_GetDDLContext_hook, (ControllerIndex, mode));
}

MDT_Define_FASTCALL(REBASE(0x1FA17D0), LiveStats_AreStatsDeltasValid_hook, uint8_t, ())
{
    return 1;
}

MDT_Define_FASTCALL(REBASE(0x13ED0D0), EveryFrameHook, void, ())
{
    MDT_ORIGINAL(EveryFrameHook, ());
    dispatch_events();
    whiteout::frame_tasks();
    whiteout_ui_tick();
    // vnlog flushes at most once a second when it writes; flush here too so quiet periods reach the file.
    static ULONGLONG lastLogFlush = 0;
    if (LOGFILE && GetTickCount64() - lastLogFlush >= 1000)
    {
        fflush(LOGFILE);
        lastLogFlush = GetTickCount64();
    }
}

// UI model lookup (model handle -> value). A stale or garbage handle (seen as 0x3D4CCCCD after long
// Zombies sessions) made the stock code read outside the model pool and crash. Valid handles can be
// larger than 16 bits (the DLC maps' loading screens use them), so nothing is filtered up front: the
// lookup runs normally and only an access violation falls back to the stock "no model" path.
MDT_Define_FASTCALL_STATIC(REBASE(0x1389B20), WhiteoutModelLookup, void*, (void* result, int controller, uint32_t handle))
{
    void* value = nullptr;
    bool faulted = false;
    whiteout::guardDepth++;
    __try
    {
        value = MDT_ORIGINAL(WhiteoutModelLookup, (result, controller, handle));
    }
    __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
    {
        faulted = true;
    }
    whiteout::guardDepth--;
    if (faulted) value = MDT_ORIGINAL(WhiteoutModelLookup, (result, controller, 0));
    return value;
}

MDT_Define_FASTCALL_STATIC(REBASE(0x13F5C90), WhiteoutClientFrame, void, (int controller))
{
    // The stock null test follows the controller stride addition, so controller 1
    // sees a non-null address even when the client state has already been freed.
    if (controller < 0 || controller >= 2 || !*reinterpret_cast<void* const*>(REBASE(0x40A9288))) return;
    MDT_ORIGINAL(WhiteoutClientFrame, (controller));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x100E130), WhiteoutClientSnapshotValue, int, (int client))
{
    if (!*reinterpret_cast<void* const*>(REBASE(0x9B25A28))) return 0;
    return MDT_ORIGINAL(WhiteoutClientSnapshotValue, (client));
}

MDT_Define_FASTCALL_STATIC(REBASE(0x292049C), WhiteoutSaveFolderResult, uint64_t, (uint64_t request, size_t capacity, char* result))
{
    // Settings and offline stats always live in the launcher's local save folder, so every player
    // gets the same behaviour whether or not an Xbox account is signed in.
    const uint64_t status = MDT_ORIGINAL(WhiteoutSaveFolderResult, (request, capacity, result));
    if (!result || !whiteout::localSaves[0]) return status;
    const size_t length = strlen(whiteout::localSaves) + 1;
    if (length > capacity) return status;
    memcpy(result, whiteout::localSaves, length);
    return 0;
}

//defined in steam.cpp
extern void init_steamapi();
namespace server_browser { void init(); }

__int64 old_IsProcessorFeaturePresent = 0;
void add_prehooks()
{
    // Load configuration from BlackOps3MS.ini (replaces MessageBox prompts)
    LoadConfigFromIni();
    LoadExperimentalConfig();
    whiteout::read_session_environment();
    if (whiteout::statsSession)
    {
        g_enableUnlockAllItems = false;
        g_spoofDLCMapPacks = false;
    }
    // A complete install (every DLC map on disk) takes the full DLC ownership path, which Private and
    // online Zombies lobbies need before they launch a DLC map. Partial installs keep the per-map path.
    else if (!g_spoofDLCMapPacks && !whiteout::officialMaps && whiteout::all_dlc_installed())
    {
        g_spoofDLCMapPacks = true;
    }
    if (whiteout::roundFile[0] && g_forceDisableSteam == 2) g_forceDisableSteam = 1;
    
    g_spoofDLC = true; // Always spoof base game 
    
    ALOG("Config loaded from BlackOps3MS.ini - DLC Maps: %s, Security Prefix: %s, Security Hooks: %s, GobbleGum Hack: %s",
         g_spoofDLCMapPacks ? "ON" : "OFF",
         g_useSecurityPrefix ? "ON" : "OFF",
         g_disableSecurityHooks ? "OFF" : "ON",
         g_gobbleGumHackEnabled ? "ON" : "OFF");

    // Initialize file logger in DLL directory so user can find logs easily
    if (!LOGFILE)
    {
        char modulePath[MAX_PATH] = {0};
        if (GetModuleFileNameA((HMODULE)REBASE(0), modulePath, sizeof(modulePath)))
        {
            char* lastSlash = strrchr(modulePath, '\\');
            if (lastSlash) *lastSlash = '\0';
            char logPath[MAX_PATH] = {0};
            sprintf_s(logPath, "%s\\bo3enhanced.log", modulePath);
            // Keep the log small: start fresh each session, keeping the previous one as .old.
            char oldPath[MAX_PATH] = {0};
            sprintf_s(oldPath, "%s\\bo3enhanced.old.log", modulePath);
            MoveFileExA(logPath, oldPath, MOVEFILE_REPLACE_EXISTING);
            LOGFILE = fopen(logPath, "w");
            if (LOGFILE)
            {
                fprintf(LOGFILE, "==== BO3EnhancedXS log started ====\n");
                fflush(LOGFILE);
                OutputDebugStringA("BO3EnhancedXS: logging to file");
                char announce[512];
                sprintf_s(announce, "BO3EnhancedXS log path: %s", logPath);
                OutputDebugStringA(announce);
            }
        }
    }

    // setup a callspoof trampoline
    chgmem<uint32_t>(SPOOF_TRAMP - 2, 0x27FFFFFF);

    InstallHook(ExceptHook);

    old_IsProcessorFeaturePresent = (__int64)&IsProcessorFeaturePresent;
    IHOOK_INSTALL(IsProcessorFeaturePresent, GetModuleHandle(0));


    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());

    // actual client stuff
    MDT_Activate(Com_InitMods_hook);
    if (g_spoofDLC) {
        MDT_Activate(Content_DoWeHaveContentPack_hook);
        MDT_Activate(MSStore_OwnsContent_hook);
    }
    MDT_Activate(Com_MessagePrintHook);
    MDT_Activate(set_mod_super_hook);
    MDT_Activate(Dvar_Callback_ModChanged_hook);
    MDT_Activate(Live_SystemInfo_Hook);
    if (g_spoofDLC) {
        MDT_Activate(Content_HasEntitlementOwnershipByRef_hook);
    }
    MDT_Activate(FS_FindXZone_hook);
    
    // DDL hooks for inventory unlock (MS Store)
    // MDT_Activate(DDL_MoveToName_hook); // disabled to isolate crash
    // MDT_Activate(DDL_MoveToPath_hook); // disabled to isolate crash
    MDT_Activate(DDL_GetUInt_hook);
    MDT_Activate(LiveStats_Core_GetDDLContext_hook);
    
    MDT_Activate(LiveStats_AreStatsDeltasValid_hook);
    MDT_Activate(hksI_openlib_hook);
    MDT_Activate(EveryFrameHook);
    MDT_Activate(WhiteoutClientFrame);
    MDT_Activate(WhiteoutClientSnapshotValue);
    MDT_Activate(WhiteoutModelLookup);
    // Installed DLC Zombies maps are listed in every local session; unlock hooks consult the ini
    // flags at call time (see whiteout::progression_on).
    MDT_Activate(WhiteoutContentPackAvailable);
    MDT_Activate(WhiteoutItemOptionLocked);
    MDT_Activate(WhiteoutAttachmentLocked);
    MDT_Activate(WhiteoutLootQuantity);
    if (whiteout::localSaves[0]) MDT_Activate(WhiteoutSaveFolderResult);

    // MDT_Activate(R_UpdateVertexDecl_hook);
    // MDT_Activate(DB_FreeXAssetHeader_hook_2);

    // testing stuff
#if BADWORD_BYPASS
    MDT_Activate(UI_ValidateText_hook);
    MDT_Activate(UI_IsBadWord_hook);
#endif
#if LOG_DEMONWARE
    MDT_Activate(bdLogMessage_hook);
#endif
#if SCREENSHOT_OVERRIDE
    MDT_Activate(Demo_SaveScreenshotToContentServer_hook);
#endif

    // MUST BE INITIALIZED BEFORE STEAM!!!!!!!
    security::init();

    // Initialize server browser/QOL scaffolding (reads config, loads custom list)
    server_browser::init();

    // initialise the steamapi (optional; uses real Steam identity when available)
    if (g_forceDisableSteam < 1) {
        init_steamapi();
    } else if (g_forceDisableSteam == 2) {
        system("taskkill /F /IM steam.exe >nul 2>&1");
    }

    // chgmem<uint8_t>(REBASE(0x20d7d3c), 0x01); ban me!!

    apply_scePad_enhancements();
    ac_bypass_allocconsole();
    kill_gamechat();
    patch_lua();

    // fix the weird zone unloading issue
    {
        // fixup a jump to our asm
        // note we only have 0xD byte space
        char patch[] = { 0x48, 0xB9, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0xFF, 0xE1 };
        *(uint64_t*)(patch + 2) = (uint64_t)zone_patch_asm;

        // install the patch
        chgmem(REBASE(0x14E0D7F), sizeof(patch), patch);

        // protect asm
        DWORD ____old;
        VirtualProtect((LPVOID)zone_patch_asm, sizeof(zone_patch_asm), PAGE_EXECUTE_READWRITE, &____old); // note: this *technically* causes a vulnerability... vprotect works in pages so this will RWX the entire page including nearby stuff!

        // fixup free entry hook
        *(uint64_t*)(zone_patch_asm + 4) = (uint64_t)&DB_FreeXAssetHeader_hook;
    }

    DetourTransactionCommit();

    chgmem<uint32_t>(REBASE(0x21F09C0 + 2), 0); // wipe flags of mods_enabled

    // nop out all the calls in the start of Mods_LoadModStart
    char fiveNops[5] = { 0x90, 0x90, 0x90, 0x90, 0x90 };

    chgmem(REBASE(0x21F0402), 5, fiveNops);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x21F0402), 5);

    chgmem(REBASE(0x21F0434), 5, fiveNops);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x21F0434), 5);

    chgmem(REBASE(0x21F0448), 5, fiveNops);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x21F0448), 5);

    chgmem(REBASE(0x21F0450), 5, fiveNops);
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)REBASE(0x21F0450), 5);

#if BADWORD_BYPASS
    // more offensiveness filter stuff
    chgmem<uint16_t>(REBASE(0x2F69AA8), 0xFF69);
#endif

    // work around mutex crash in xbox curl code by making it a recursive lock
    char mutexPatch[] = { 0xBA, 0x02, 0x01, 0x00, 0x00, 0xE8, 0x8A, 0x2F, 0xF8, 0xFF };
    chgmem(REBASE(0x2D7AAB2), sizeof(mutexPatch), mutexPatch);

    // stub microphone enumeration
    chgmem<uint8_t>(REBASE(0x1EEA680), 0xC3);
}

void add_hooks()
{
    ALOG("installing hooks...");
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());

    if (g_spoofDLC) {
        MDT_Activate(Content_GetAvailableContentPacks_hook);
    }
    security::init_delayed();
    whiteout_pad::attach(); // PS5 DualSense pads reach the game as XInput pads

    DetourTransactionCommit();

    if (g_spoofDLC) {
        spoof_ownscontent();
    }

    chgmem<uint8_t>(REBASE(0x14A2F4C), 0xEB); // Fastfile archive checksum (0x%08X 0x%08

    enable_fileshare();

    if (g_experimentalNetPatches)
    {
        ALOG("APPLYING EXPERIMENTAL NETWORK PATCHES (Dedicated Server Fix)");
        // fix multiplayer dedicated server searches
        chgmem<uint8_t>(REBASE(0x1FEAB49 + 2), 0); // lobbyDedicatedSearchSkip: 1 -> 0
        chgmem<uint32_t>(REBASE(0x1FDE201) + 6, 0xD3FC12u); // changelist in LobbyHostMsg_SendJoinRequest -> steam changelist
    }

    // show the callstack instead of just ui errors
    INT64 ptrDvar = *(INT64*)(PTR_ui_error_callstack_ship);
    *(DWORD*)(ptrDvar + 0x18) = 0; // clear flags

    Dvar_SetFromStringByName("ui_error_callstack_ship", "1", true);
    Dvar_SetFromStringByName("loot_enabled", "1", true);

    // enable text chat
    // Dvar_SetFromStringByName("chatClientEnabled", "1");
    // chgmem<uint8_t>(REBASE(0x20E39BC + 3), 1);

    if (g_enableUnlockAllItems)
    {
        Dvar_SetFromStringByName("ui_allLootUnlocked", "1", true);
        Dvar_SetFromStringByName("loot_unlockUnreleasedLoot", "1", true);
    }

    // Dvar_SetFromStringByName("ui_lobbydebugvis", "3", true);

    // anticheat related import caches
    /*dumpa_da_anticheats(REBASE(0x1A122AD8), (0x1A122C88 - 0x1A122AD8) / 8);
    dumpa_da_anticheats(REBASE(0x1A122D20), (0x1A122D50 - 0x1A122D20) / 8);
    dumpa_da_anticheats(REBASE(0x1A1118A8), (0x1A1118D0 - 0x1A1118A8) / 8);
    dumpa_da_anticheats(REBASE(0x8E573B8), (0x8E57400 - 0x8E573B8) / 8);
    dumpa_da_anticheats(REBASE(0x8E57410), (0x8E574A0 - 0x8E57410) / 8);*/
    // dumpa_da_anticheats(REBASE(0x8E573B8), (0x8E57498 - 0x8E573B8) / 8);

    // processor affinity for AMD cpus
    register_frame_event([]()
    {
        static int affinityState = 0;
        static ULONG_PTR processAffinity = 0;
        static ULONG_PTR systemAffinity = 0;

        if (affinityState > 1)
        {
            return;
        }

        set_delay(1000, DELAYED_EVENT_PROCESSORAFFINITY, []()
        {
            switch (affinityState)
            {
                case 0:
                {
                    ALOG("Attempting to set affinity state...");
                    if (!GetProcessAffinityMask(GetCurrentProcess(), &processAffinity, &systemAffinity))
                    {
                        ALOG("Unable to get affinity state");
                        break;
                    }

                    if (!processAffinity)
                    {
                        ALOG("Unable to get affinity state (1)");
                        break;
                    }

                    ULONG_PTR affinity = processAffinity & (1 | 2 | 4 | 8);

                    if (!affinity)
                    {
                        break;
                    }

                    if (!SetProcessAffinityMask(GetCurrentProcess(), affinity))
                    {
                        ALOG("Unable to set affinity state");
                        break;
                    }

                    ALOG("Processor affinity set!");
                    affinityState++;
                }
                break;
                case 1:
                {
                    if (!SetProcessAffinityMask(GetCurrentProcess(), processAffinity))
                    {
                        ALOG("Processor affinity failed to reset");
                        break;
                    }

                    ALOG("Processor affinity reset!");
                    affinityState++;
                }
                break;
            }
        });
    });
}

DWORD WINAPI watch_ready_inject(_In_ LPVOID lpParameter)
{
    for (;;)
    {
        Sleep(10);
        if (*(__int64*)REBASE(0x8DEE3D8))
        {
            break;
        }
    }

    add_hooks();

    /*for (;;)
    {
        if (GetAsyncKeyState(VK_OEM_3) & 0x8000)
        {
            Cbuf_AddText("statwritemode mp playerstatslist plevel statvalue 10\n");
            Cbuf_AddText("statwritemode mp playerstatslist plevel challengevalue 10\n");
            Cbuf_AddText("statwritemode mp playerstatslist rankxp statvalue 1457200\n");
            Cbuf_AddText("statwritemode mp playerstatslist rankxp challengevalue 1457200\n");
            Cbuf_AddText("statwritemode mp playerstatslist rank statvalue 54\n");
            Cbuf_AddText("statwritemode mp playerstatslist rank challengevalue 54\n");
            Cbuf_AddText("uploadstats\n");
        }
        Sleep(1000);
    }*/

    return 0;
}

#if REMAP_EXPERIMENT
__declspec(dllexport) void* get_tls_data() // without this the compiler wont emit the tls data
{
    return &tls_data[0];
}

void replace_steam_exe()
{
    ALOG("replace_steam_exe");

    ALOG("Caching TLS info from steam bo3...");
    // cache the original tls info from steam bo3
    auto bo3 = Pe::PeNative::fromModule((const void*)REBASE(0));
    auto original_tls = *bo3.tls().descriptor().ptr;

    ALOG("original start: %p, Base address of memory: %p, addressofcallbacks %p", original_tls.StartAddressOfRawData, REBASE(0), original_tls.AddressOfCallBacks);

    typedef uint32_t NtUnmapViewOfSection_t(HANDLE processHandle, PVOID baseAddress);
    auto NtUnmapViewOfSection = (NtUnmapViewOfSection_t*)GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtUnmapViewOfSection");

    // unmap the original bo3.exe from memory
    ALOG("Unmapping section...");
    NtUnmapViewOfSection(GetCurrentProcess(), (PVOID)REBASE(0));

    // map new one in
    std::ifstream ifs;
    ifs.open(WSTORE_MOD_NAME, std::ifstream::in | std::ifstream::binary);

    if (!ifs.is_open())
    {
        ALOG("ERROR: COULD NOT FIND OR OPEN %s", WSTORE_MOD_NAME);
        SuspendProcess();
        return;
    }

    ifs.seekg(0, std::ios::end);
    size_t fileSize = ifs.tellg();
    ifs.seekg(0, std::ios::beg);

    auto execBuf = new char[(uint32_t)fileSize];
    ifs.read(execBuf, (uint32_t)fileSize);
    ifs.close();

    ALOG("Mapping new module...");
    auto res = MemoryLoadLibrary(execBuf, fileSize, (void*)REBASE(0), true);

    delete[] execBuf;
    execBuf = NULL;

    if (!res)
    {
        ALOG("ERROR: FAILED TO MAP MAIN MODULE (0x%X)", GetLastError());
        SuspendProcess();
        return;
    }

    auto wbo3 = Pe::PeNative::fromModule((const void*)REBASE(0));

    // initialize TLS data (thanks momo5502 for example for this in boiii)
    auto our_mod = Pe::PeNative::fromModule((const void*)__thismodule); // (IMAGE_TLS_PTR)
    {
        auto target = (IMAGE_TLS_DIRECTORY64*)our_mod.tls().descriptor().ptr;
        auto source = (IMAGE_TLS_DIRECTORY64*)wbo3.tls().descriptor().ptr;

        auto target_start = target->StartAddressOfRawData;
        auto source_start = source->StartAddressOfRawData;
        auto source_size = source->EndAddressOfRawData - source->StartAddressOfRawData;
        auto tls_index = *(uint32_t*)target->AddressOfIndex;
        
        // copy existing tls index over to theirs
        chgmem<uint32_t>(source->AddressOfIndex, tls_index);

        DWORD old;
        VirtualProtect((LPVOID)target_start, source_size, PAGE_READWRITE, &old);

        // copy the tls data from windows store binary into our current thread tls memory region
        auto tls_base = *(LPVOID*)(__readgsqword(0x58) + 8ull * tls_index);
        memcpy(tls_base, (void*)source_start, source_size);

        // copy the data from windows store into our dll's tls region
        memcpy((void*)target_start, (void*)source_start, source_size);

        // find the bo3 LDR data entry and assign it the correct TLS index
        {
            auto peb = NtCurrentPeb();
            auto head = &peb->Ldr->InMemoryOrderModuleList;

            int mc = 0;
            auto entry = head->Flink;
            while (entry != head)
            {
                auto table_entry = CONTAINING_RECORD(entry, LDR_DATA_TABLE_ENTRY, InMemoryOrderLinks);
                auto n = static_cast<int>(offsetof(LDR_DATA_TABLE_ENTRY, DllBase));

                if ((uint64_t)table_entry->DllBase == REBASE(0))
                {
                    *(uint16_t*)((uint64_t)table_entry + 0x6E) = (uint16_t)tls_index;

                    // also while we are here lets fix up other image attributes
                    *(uint64_t*)((uint64_t)table_entry + 0x40) = (uint64_t)wbo3.imageSize();
                    *(uint64_t*)((uint64_t)table_entry + 0x38) = (uint64_t)wbo3.entryPoint();

                    break;
                }

                entry = entry->Flink;
            }
        }

        // restore the original steam bo3's TLS buffer so that LdrpTlsList doesnt have bad data
        auto restored_tls_adr = VirtualAlloc((void*)original_tls.StartAddressOfRawData, source_size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        memcpy(restored_tls_adr, (void*)source->StartAddressOfRawData, source_size);
    }

    // invoke the original entrypoint of this dll (to apply hooks, etc)
    ALOG("Calling our dll entrypoint...");
    DllMain(__thismodule, DLL_PROCESS_ATTACH, NULL);

    // invoke TLS of windows store bo3
    ALOG("Calling WSBO3's TLS...");
    for (const auto& cb : wbo3.tls())
    {
        cb.callback()((void*)REBASE(0), DLL_PROCESS_ATTACH, NULL);
    }

    for (const auto& cb : wbo3.tls())
    {
        // change our tls callback to be theirs
        chgmem<uint64_t>(our_mod.tls().descriptor().ptr->AddressOfCallBacks, (uint64_t)cb.callback());
        break;
    }

    // invoke the main module entrypoint
    ALOG("Calling WSBO3's entrypoint...");
    ((void(__fastcall*)(PVOID, uint32_t, const char*, uint32_t))wbo3.entryPoint())((void*)REBASE(0), DLL_PROCESS_ATTACH, "", SW_SHOW);

    ALOG("Exiting process...");
}

bool start_winstore_exe()
{
    if (*(uint32_t*)(REBASE(0) + 0x3C) != 0x160)
    {
        return false; // this is not the steam executable we expect
    }

    ALOG("Steam BO3 detected... patching module!");

    // 1. Remove the original TLS callbacks in bo3
    auto bo3 = Pe::PeNative::fromModule((const void*)REBASE(0));
    for (const auto& cb : bo3.tls())
    {
        chgmem<uint8_t>((uint64_t)cb.callback(), 0xC3);
    }

    // 2. Hook the entrypoint of the original bo3 to jump into our code
    char code[] = { 0x48, 0xB8, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0xFF, 0xE0 };
    *(uint64_t*)(code + 2) = (uint64_t)replace_steam_exe;
    chgmem(bo3.entryPoint(), sizeof(code), code);
    
    return true;
}
#endif

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:

    {
        __thismodule = hModule;
        // Initialize file logger immediately on process attach
        if (!LOGFILE)
        {
            char modulePath[MAX_PATH] = {0};
            if (GetModuleFileNameA(hModule, modulePath, sizeof(modulePath)))
            {
                char* lastSlash = strrchr(modulePath, '\\');
                if (lastSlash) *lastSlash = '\0';
                char logPath[MAX_PATH] = {0};
                sprintf_s(logPath, "%s\\bo3enhanced.log", modulePath);
                // Start a fresh log every session; the previous one is kept as .old.
                char oldPath[MAX_PATH] = {0};
                sprintf_s(oldPath, "%s\\bo3enhanced.old.log", modulePath);
                MoveFileExA(logPath, oldPath, MOVEFILE_REPLACE_EXISTING);
                LOGFILE = fopen(logPath, "w");
                if (LOGFILE)
                {
                    fprintf(LOGFILE, "==== BO3EnhancedXS log started ====\n");
                    fflush(LOGFILE);
                }
            }
        }
        nlog("DllMain attach: logger initialized");

#if REMAP_EXPERIMENT
        if (start_winstore_exe())
        {
            is_remapped_exe = true;
            return TRUE;
        }
#endif
        if (*(uint32_t*)(REBASE(0) + 0x3C) != 0x1A0)
        {
            return TRUE; // this is not the windows store executable we expect
        }

        {
            add_prehooks();

            SECURITY_ATTRIBUTES sa;
            sa.nLength = sizeof(SECURITY_ATTRIBUTES);
            sa.bInheritHandle = 1;
            sa.lpSecurityDescriptor = 0;
            dbg_is_console_available = CreatePipe(&dbg_hReadConsole, &dbg_hWriteConsole, &sa, 0xF4240);
            ALOG("Console available...");

            CreateThread(NULL, NULL, watch_ready_inject, NULL, NULL, NULL);
            
            // Start GobbleGum hack thread (controlled by g_gobbleGumHackEnabled from INI)
            if (!whiteout::roundFile[0] && g_gobbleGumHackEnabled)
                CreateThread(NULL, NULL, GobbleGumHackThread, NULL, NULL, NULL);
            
            // Start Cryptokeys hack thread (controlled by g_cryptokeysHackEnabled from INI)
            CreateThread(NULL, NULL, CryptokeysHackThread, NULL, NULL, NULL);
            ALOG("Cryptokeys hack thread started!");
            
            // Start Liquid Divinium hack thread (controlled by g_liquidDiviniumHackEnabled from INI)
            CreateThread(NULL, NULL, LiquidDiviniumHackThread, NULL, NULL, NULL);
            ALOG("Liquid Divinium hack thread started!");
        }
    }
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

EXPORT void dummy_proc() {}

EXPORT uint64_t GetDevPipe()
{
    return (uint64_t)dbg_hReadConsole;
}
EXPORT void NotifyDebuggerAttached(const char* password)
{
    dbg_is_attached = true;
}

namespace Iat_hook_
{
    void** find(const char* function, HMODULE module)
    {
        if (!module)
            module = GetModuleHandle(0);

        PIMAGE_DOS_HEADER img_dos_headers = (PIMAGE_DOS_HEADER)module;
        PIMAGE_NT_HEADERS img_nt_headers = (PIMAGE_NT_HEADERS)((BYTE*)img_dos_headers + img_dos_headers->e_lfanew);
        PIMAGE_IMPORT_DESCRIPTOR img_import_desc = (PIMAGE_IMPORT_DESCRIPTOR)((BYTE*)img_dos_headers + img_nt_headers->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
        if (img_dos_headers->e_magic != IMAGE_DOS_SIGNATURE)
            printf("\n");

        for (IMAGE_IMPORT_DESCRIPTOR* iid = img_import_desc; iid->Name != 0; iid++) {
            for (int func_idx = 0; *(func_idx + (void**)(iid->FirstThunk + (size_t)module)) != nullptr; func_idx++) {
                char* mod_func_name = (char*)(*(func_idx + (size_t*)(iid->OriginalFirstThunk + (size_t)module)) + (size_t)module + 2);
                const intptr_t nmod_func_name = (intptr_t)mod_func_name;
                if (nmod_func_name >= 0) {
                    if (!::strcmp(function, mod_func_name))
                        return func_idx + (void**)(iid->FirstThunk + (size_t)module);
                }
            }
        }

        return 0;

    }

    uintptr_t detour_iat_ptr(const char* function, void* newfunction, HMODULE module)
    {
        auto&& func_ptr = find(function, module);
        if (*func_ptr == newfunction || *func_ptr == nullptr)
            return 0;

        DWORD old_rights, new_rights = PAGE_READWRITE;
        VirtualProtect(func_ptr, sizeof(uintptr_t), new_rights, &old_rights);
        uintptr_t ret = (uintptr_t)*func_ptr;
        *func_ptr = newfunction;
        VirtualProtect(func_ptr, sizeof(uintptr_t), old_rights, &new_rights);
        return ret;
    }
};
