#pragma once
// Native PlayStation 5 DualSense (and DualSense Edge) support.
//
// The game reads controllers through XInput (xinput9_1_0.dll) and through Sony's libScePad 1.6,
// which predates the DualSense. This module reads DualSense pads over HID (USB or Bluetooth) on
// background threads and hands them to the game as XInput pads, including rumble. When any real
// XInput pad (Xbox pad, Steam Input, DS4Windows) is connected the module stays out of the way so
// no controller is ever seen twice.
#include <windows.h>
#include <setupapi.h>
#include <hidsdi.h>
#include <Xinput.h>
#include <array>
#include <atomic>
#include <string>
#include <vector>
#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

namespace whiteout_pad
{
    constexpr int MaxPads = 4;

    struct Pad
    {
        std::atomic<bool> live{ false };
        bool bluetooth = false;
        bool enhanced = false;      // Bluetooth report 0x31 (full report) instead of the simple 0x01
        HANDLE reader = INVALID_HANDLE_VALUE;
        HANDLE writer = INVALID_HANDLE_VALUE;
        std::wstring path;
        SRWLOCK lock = SRWLOCK_INIT;
        XINPUT_GAMEPAD state{};
        DWORD packet = 0;
        uint8_t rumbleLeft = 0, rumbleRight = 0;
        uint8_t btSequence = 0;
    };

    inline std::array<Pad, MaxPads> pads;
    inline std::atomic<bool> realXInput[XUSER_MAX_COUNT]{};
    inline std::atomic<int> livePads{ 0 };

    using GetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
    using SetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_VIBRATION*);
    using GetCapsFn = DWORD(WINAPI*)(DWORD, DWORD, XINPUT_CAPABILITIES*);
    inline GetStateFn originalGetState = nullptr;
    inline SetStateFn originalSetState = nullptr;
    inline GetCapsFn originalGetCaps = nullptr;

    // ------------------------------------------------------------------ report parsing

    inline SHORT axis(uint8_t value, bool invert)
    {
        int centered = static_cast<int>(value) - 128;
        if (invert) centered = -centered - 1;
        const int scaled = centered * 257;
        return static_cast<SHORT>(scaled < -32768 ? -32768 : scaled > 32767 ? 32767 : scaled);
    }

    // b0: hat (low nibble) + square/cross/circle/triangle, b1: L1 R1 L2 R2 create options L3 R3, b2: PS touchpad mute
    inline XINPUT_GAMEPAD to_xinput(uint8_t lx, uint8_t ly, uint8_t rx, uint8_t ry, uint8_t l2, uint8_t r2,
        uint8_t b0, uint8_t b1, uint8_t b2)
    {
        XINPUT_GAMEPAD pad{};
        pad.sThumbLX = axis(lx, false);
        pad.sThumbLY = axis(ly, true);
        pad.sThumbRX = axis(rx, false);
        pad.sThumbRY = axis(ry, true);
        pad.bLeftTrigger = l2;
        pad.bRightTrigger = r2;
        WORD buttons = 0;
        static constexpr WORD hat[8] = {
            XINPUT_GAMEPAD_DPAD_UP, XINPUT_GAMEPAD_DPAD_UP | XINPUT_GAMEPAD_DPAD_RIGHT, XINPUT_GAMEPAD_DPAD_RIGHT,
            XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_RIGHT, XINPUT_GAMEPAD_DPAD_DOWN,
            XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_LEFT, XINPUT_GAMEPAD_DPAD_LEFT,
            XINPUT_GAMEPAD_DPAD_UP | XINPUT_GAMEPAD_DPAD_LEFT };
        if ((b0 & 0x0F) < 8) buttons |= hat[b0 & 0x0F];
        if (b0 & 0x10) buttons |= XINPUT_GAMEPAD_X;          // square
        if (b0 & 0x20) buttons |= XINPUT_GAMEPAD_A;          // cross
        if (b0 & 0x40) buttons |= XINPUT_GAMEPAD_B;          // circle
        if (b0 & 0x80) buttons |= XINPUT_GAMEPAD_Y;          // triangle
        if (b1 & 0x01) buttons |= XINPUT_GAMEPAD_LEFT_SHOULDER;
        if (b1 & 0x02) buttons |= XINPUT_GAMEPAD_RIGHT_SHOULDER;
        if (b1 & 0x10) buttons |= XINPUT_GAMEPAD_BACK;       // create
        if (b1 & 0x20) buttons |= XINPUT_GAMEPAD_START;      // options
        if (b1 & 0x40) buttons |= XINPUT_GAMEPAD_LEFT_THUMB;
        if (b1 & 0x80) buttons |= XINPUT_GAMEPAD_RIGHT_THUMB;
        if (b2 & 0x02) buttons |= XINPUT_GAMEPAD_BACK;       // touchpad click = scoreboard
        pad.wButtons = buttons;
        return pad;
    }

    inline bool parse(Pad& pad, const uint8_t* r, DWORD length, XINPUT_GAMEPAD& out)
    {
        if (length < 10) return false;
        if (r[0] == 0x01 && length >= 64)
        {
            // USB full report
            out = to_xinput(r[1], r[2], r[3], r[4], r[5], r[6], r[8], r[9], r[10]);
            return true;
        }
        if (r[0] == 0x31 && length >= 12)
        {
            // Bluetooth full report: same layout shifted by one byte
            pad.enhanced = true;
            out = to_xinput(r[2], r[3], r[4], r[5], r[6], r[7], r[9], r[10], r[11]);
            return true;
        }
        if (r[0] == 0x01)
        {
            // Bluetooth simple report
            out = to_xinput(r[1], r[2], r[3], r[4], r[8], r[9], r[5], r[6], r[7]);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ output (rumble)

    inline uint32_t crc32(const uint8_t* data, size_t length, uint32_t crc = 0xFFFFFFFF)
    {
        for (size_t i = 0; i < length; ++i)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        return crc;
    }

    inline void send_rumble(Pad& pad, uint8_t left, uint8_t right)
    {
        if (pad.writer == INVALID_HANDLE_VALUE) return;
        uint8_t report[78]{};
        DWORD size = 0;
        uint8_t* common = nullptr;
        if (!pad.bluetooth)
        {
            report[0] = 0x02;
            common = report + 1;
            size = 48;
        }
        else
        {
            // Rumble over Bluetooth needs the full (0x31) output report with a CRC.
            report[0] = 0x31;
            report[1] = 0x02;
            common = report + 2;
            size = 78;
        }
        common[0] = 0x03;   // use rumble emulation + allow haptics
        common[1] = 0x00;
        common[2] = right;  // small motor
        common[3] = left;   // large motor
        if (pad.bluetooth)
        {
            uint8_t seed = 0xA2;
            uint32_t crc = crc32(&seed, 1);
            crc = ~crc32(report, 74, crc);
            memcpy(report + 74, &crc, 4);
        }
        DWORD written = 0;
        WriteFile(pad.writer, report, size, &written, nullptr);
    }

    // ------------------------------------------------------------------ device threads

    inline void close_pad(Pad& pad)
    {
        pad.live = false;
        if (pad.reader != INVALID_HANDLE_VALUE) CloseHandle(pad.reader);
        if (pad.writer != INVALID_HANDLE_VALUE) CloseHandle(pad.writer);
        pad.reader = pad.writer = INVALID_HANDLE_VALUE;
        pad.path.clear();
        pad.enhanced = false;
        AcquireSRWLockExclusive(&pad.lock);
        pad.state = {};
        ReleaseSRWLockExclusive(&pad.lock);
        --livePads;
    }

    inline DWORD WINAPI read_thread(LPVOID parameter)
    {
        auto& pad = *static_cast<Pad*>(parameter);
        uint8_t buffer[128];
        for (;;)
        {
            DWORD read = 0;
            if (!ReadFile(pad.reader, buffer, sizeof(buffer), &read, nullptr)) break;
            XINPUT_GAMEPAD state{};
            if (!parse(pad, buffer, read, state)) continue;
            AcquireSRWLockExclusive(&pad.lock);
            if (memcmp(&pad.state, &state, sizeof(state)) != 0)
            {
                pad.state = state;
                ++pad.packet;
            }
            ReleaseSRWLockExclusive(&pad.lock);
        }
        close_pad(pad);
        return 0;
    }

    inline bool is_dualsense(const wchar_t* path)
    {
        std::wstring lower(path);
        for (auto& c : lower) c = towlower(c);
        if (lower.find(L"vid_054c") == std::wstring::npos) return false;
        return lower.find(L"pid_0ce6") != std::wstring::npos || lower.find(L"pid_0df2") != std::wstring::npos;
    }

    inline void open_device(const wchar_t* path)
    {
        for (auto& pad : pads)
            if (pad.live && pad.path == path) return;
        for (auto& pad : pads)
        {
            if (pad.live) continue;
            HANDLE reader = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
            if (reader == INVALID_HANDLE_VALUE) return; // hidden (HidHide) or in exclusive use
            HIDP_CAPS caps{};
            PHIDP_PREPARSED_DATA data = nullptr;
            if (HidD_GetPreparsedData(reader, &data))
            {
                HidP_GetCaps(data, &caps);
                HidD_FreePreparsedData(data);
            }
            // Only the gamepad collection (usage page 1, usage 5).
            if (caps.UsagePage != 0x01 || caps.Usage != 0x05)
            {
                CloseHandle(reader);
                return;
            }
            pad.reader = reader;
            pad.writer = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
            pad.path = path;
            // USB input reports are 64 bytes; Bluetooth ones are 78 (full) or 10 (simple).
            pad.bluetooth = caps.InputReportByteLength != 64;
            if (pad.bluetooth)
            {
                // Reading the calibration feature report switches a Bluetooth DualSense to full reports.
                uint8_t feature[41]{ 0x05 };
                HidD_GetFeature(reader, feature, sizeof(feature));
            }
            pad.rumbleLeft = pad.rumbleRight = 0;
            pad.live = true;
            ++livePads;
            if (HANDLE thread = CreateThread(nullptr, 0, read_thread, &pad, 0, nullptr)) CloseHandle(thread);
            return;
        }
    }

    inline DWORD WINAPI scan_thread(LPVOID)
    {
        GUID hid{};
        HidD_GetHidGuid(&hid);
        for (;;)
        {
            HDEVINFO set = SetupDiGetClassDevsW(&hid, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set != INVALID_HANDLE_VALUE)
            {
                SP_DEVICE_INTERFACE_DATA item{ sizeof(item) };
                for (DWORD i = 0; SetupDiEnumDeviceInterfaces(set, nullptr, &hid, i, &item); ++i)
                {
                    DWORD size = 0;
                    SetupDiGetDeviceInterfaceDetailW(set, &item, nullptr, 0, &size, nullptr);
                    if (size == 0 || size > 4096) continue;
                    std::vector<uint8_t> storage(size);
                    auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(storage.data());
                    detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, &item, detail, size, nullptr, nullptr)) continue;
                    if (is_dualsense(detail->DevicePath)) open_device(detail->DevicePath);
                }
                SetupDiDestroyDeviceInfoList(set);
            }
            Sleep(2000);
        }
    }

    // ------------------------------------------------------------------ XInput hooks

    inline bool any_real_xinput()
    {
        for (const auto& connected : realXInput)
            if (connected) return true;
        return false;
    }

    // The n-th live DualSense, when DualSense pads are standing in for XInput.
    inline Pad* pad_for_slot(DWORD user)
    {
        if (user >= XUSER_MAX_COUNT || livePads <= 0 || any_real_xinput()) return nullptr;
        DWORD seen = 0;
        for (auto& pad : pads)
        {
            if (!pad.live) continue;
            if (seen++ == user) return &pad;
        }
        return nullptr;
    }

    /// True when the game's XInput slot is a DualSense, so PlayStation button icons can be shown.
    inline bool slot_is_playstation(int user)
    {
        return user >= 0 && pad_for_slot(static_cast<DWORD>(user)) != nullptr;
    }

    inline DWORD WINAPI hooked_get_state(DWORD user, XINPUT_STATE* state)
    {
        const DWORD result = originalGetState(user, state);
        if (user < XUSER_MAX_COUNT) realXInput[user] = result == ERROR_SUCCESS;
        if (result == ERROR_SUCCESS || !state) return result;
        Pad* pad = pad_for_slot(user);
        if (!pad) return result;
        AcquireSRWLockShared(&pad->lock);
        state->Gamepad = pad->state;
        state->dwPacketNumber = pad->packet;
        ReleaseSRWLockShared(&pad->lock);
        return ERROR_SUCCESS;
    }

    inline DWORD WINAPI hooked_get_caps(DWORD user, DWORD flags, XINPUT_CAPABILITIES* caps)
    {
        const DWORD result = originalGetCaps(user, flags, caps);
        if (result == ERROR_SUCCESS || !caps || !pad_for_slot(user)) return result;
        *caps = {};
        caps->Type = XINPUT_DEVTYPE_GAMEPAD;
        caps->SubType = XINPUT_DEVSUBTYPE_GAMEPAD;
        caps->Gamepad.wButtons = 0xF3FF;
        caps->Gamepad.bLeftTrigger = caps->Gamepad.bRightTrigger = 0xFF;
        caps->Gamepad.sThumbLX = caps->Gamepad.sThumbLY = caps->Gamepad.sThumbRX = caps->Gamepad.sThumbRY = static_cast<SHORT>(0xFFC0);
        caps->Vibration.wLeftMotorSpeed = caps->Vibration.wRightMotorSpeed = 0xFF;
        return ERROR_SUCCESS;
    }

    inline DWORD WINAPI hooked_set_state(DWORD user, XINPUT_VIBRATION* vibration)
    {
        const DWORD result = originalSetState(user, vibration);
        if (result == ERROR_SUCCESS || !vibration) return result;
        Pad* pad = pad_for_slot(user);
        if (!pad) return result;
        const uint8_t left = static_cast<uint8_t>(vibration->wLeftMotorSpeed >> 8);
        const uint8_t right = static_cast<uint8_t>(vibration->wRightMotorSpeed >> 8);
        if (left != pad->rumbleLeft || right != pad->rumbleRight)
        {
            pad->rumbleLeft = left;
            pad->rumbleRight = right;
            send_rumble(*pad, left, right);
        }
        return ERROR_SUCCESS;
    }

    /// Call inside an open Detours transaction.
    inline void attach()
    {
        HMODULE xinput = GetModuleHandleW(L"xinput9_1_0.dll");
        if (!xinput) xinput = LoadLibraryW(L"xinput9_1_0.dll");
        if (!xinput) return;
        originalGetState = reinterpret_cast<GetStateFn>(GetProcAddress(xinput, "XInputGetState"));
        originalSetState = reinterpret_cast<SetStateFn>(GetProcAddress(xinput, "XInputSetState"));
        originalGetCaps = reinterpret_cast<GetCapsFn>(GetProcAddress(xinput, "XInputGetCapabilities"));
        if (!originalGetState || !originalSetState || !originalGetCaps) return;
        DetourAttach(&(PVOID&)originalGetState, hooked_get_state);
        DetourAttach(&(PVOID&)originalSetState, hooked_set_state);
        DetourAttach(&(PVOID&)originalGetCaps, hooked_get_caps);
        if (HANDLE thread = CreateThread(nullptr, 0, scan_thread, nullptr, 0, nullptr)) CloseHandle(thread);
    }
}
