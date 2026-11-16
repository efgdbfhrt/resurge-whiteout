#pragma once
#include <Psapi.h>
#include <d3d11.h>
#include <string>
#include <vector>

// Custom camo research: the game's images live in one static pool (found from the image release
// code at 0x1DFA6F0: base 0x4F55D60, 0x108 bytes per image). This probe maps that record layout
// (name field, GPU texture fields) and lists the camo images. It only runs when
// whiteout-camo-probe.txt exists next to the game exe.
namespace whiteout::camo
{
    constexpr uint64_t imagePoolRva = 0x4F55D60;
    constexpr size_t imageSize = 0x108;
    constexpr uint64_t dataEndRva = 0x030E9000 + 0x1731E9B0;

    static bool user_pointer(uint64_t value) { return value > 0x10000 && value < 0x00007FFFFFFFFFFFull && (value & 7) == 0; }

    // Copies a printable, NUL-terminated name (4..120 chars) or returns false. Faults are caught.
    static bool read_name(uint64_t address, char* out, size_t capacity)
    {
        if (address <= 0x10000 || address >= 0x00007FFFFFFFFFFFull) return false;
        bool ok = false;
        __try
        {
            const char* text = reinterpret_cast<const char*>(address);
            size_t length = 0;
            while (length < 120 && text[length]) { if (text[length] < 0x20 || text[length] > 0x7E) return false; ++length; }
            if (length >= 4 && length < 120 && length < capacity)
            {
                memcpy(out, text, length);
                out[length] = '\0';
                ok = true;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
        return ok;
    }

    static bool read_qword(uint64_t address, uint64_t& value)
    {
        __try { value = *reinterpret_cast<const uint64_t*>(address); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    static bool in_module(uint64_t address, const MODULEINFO& module)
    {
        const auto start = reinterpret_cast<uint64_t>(module.lpBaseOfDll);
        return module.lpBaseOfDll && address >= start && address < start + module.SizeOfImage;
    }

    // Which D3D11 interface a GPU pointer is: 'S' shader resource view, 'T' 2D texture, '?' other.
    static char interface_kind(uint64_t pointer)
    {
        char kind = '?';
        __try
        {
            auto* unknown = reinterpret_cast<IUnknown*>(pointer);
            void* out = nullptr;
            if (SUCCEEDED(unknown->QueryInterface(__uuidof(ID3D11ShaderResourceView), &out)) && out) { kind = 'S'; static_cast<IUnknown*>(out)->Release(); }
            else if (SUCCEEDED(unknown->QueryInterface(__uuidof(ID3D11Texture2D), &out)) && out) { kind = 'T'; static_cast<IUnknown*>(out)->Release(); }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { kind = '!'; }
        return kind;
    }

    // Writes every named image (index, name, the two GPU fields and their kind, raw size fields) to
    // whiteout-images-<run>.txt in the game folder.
    static void dump_images(int run)
    {
        char file[64];
        sprintf_s(file, "whiteout-images-%d.txt", run);
        FILE* output = nullptr;
        if (fopen_s(&output, file, "w") != 0 || !output) return;
        const uint64_t pool = REBASE(imagePoolRva);
        const uint64_t limit = REBASE(dataEndRva);
        char name[128];
        for (int index = 0; index < 60000; ++index)
        {
            const uint64_t item = pool + imageSize * index;
            if (item + imageSize > limit) break;
            uint64_t namePtr = 0, a8 = 0, b0 = 0, c0 = 0, c8 = 0;
            if (!read_qword(item + 0xF8, namePtr) || !read_name(namePtr, name, sizeof(name))) continue;
            read_qword(item + 0xA8, a8); read_qword(item + 0xB0, b0); read_qword(item + 0xC0, c0); read_qword(item + 0xC8, c8);
            fprintf(output, "%d\t%s\ta8=%llx:%c\tb0=%llx:%c\tc0=%016llx\tc8=%016llx\n", index, name,
                a8, user_pointer(a8) ? interface_kind(a8) : '-', b0, user_pointer(b0) ? interface_kind(b0) : '-', c0, c8);
        }
        fclose(output);
        ALOG("CamoProbe wrote %s", file);
    }

    static void probe()
    {
        static int runs = 0;
        static ULONGLONG next = 0;
        if (runs >= 10) return;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 30000;
        if (GetFileAttributesA("whiteout-camo-probe.txt") == INVALID_FILE_ATTRIBUTES) { runs = 10; return; }
        ++runs;

        MODULEINFO d3d{}, dxgi{};
        if (HMODULE module = GetModuleHandleA("d3d11.dll")) GetModuleInformation(GetCurrentProcess(), module, &d3d, sizeof(d3d));
        if (HMODULE module = GetModuleHandleA("dxgi.dll")) GetModuleInformation(GetCurrentProcess(), module, &dxgi, sizeof(dxgi));

        const uint64_t pool = REBASE(imagePoolRva);
        const uint64_t limit = REBASE(dataEndRva);
        int nameAt[imageSize / 8]{};
        int d3dAt[imageSize / 8]{};
        int used = 0, camos = 0;
        char name[128];
        ALOG("CamoProbe run %d: map=%s pool=%p", runs, reinterpret_cast<const char*>(REBASE(0x90FC4D0)), reinterpret_cast<void*>(pool));
        for (int index = 0; index < 60000; ++index)
        {
            const uint64_t item = pool + imageSize * index;
            if (item + imageSize > limit) break;
            int nameOffset = -1;
            for (size_t offset = 0; offset < imageSize; offset += 8)
            {
                uint64_t value = 0;
                if (!read_qword(item + offset, value)) break;
                if (nameOffset < 0 && read_name(value, name, sizeof(name))) { nameOffset = static_cast<int>(offset); ++nameAt[offset / 8]; }
                uint64_t vtable = 0;
                if (user_pointer(value) && read_qword(value, vtable) && (in_module(vtable, d3d) || in_module(vtable, dxgi))) ++d3dAt[offset / 8];
            }
            if (nameOffset < 0) continue;
            ++used;
            uint64_t namePtr = 0;
            read_qword(item + nameOffset, namePtr);
            read_name(namePtr, name, sizeof(name));
            char lower[128];
            for (size_t i = 0; i < sizeof(lower); ++i) { lower[i] = static_cast<char>(tolower(static_cast<unsigned char>(name[i]))); if (!name[i]) break; }
            if (!strstr(lower, "camo")) continue;
            ++camos;
        }
        ALOG("CamoProbe run %d done: %d named images, %d with 'camo'", runs, used, camos);
        dump_images(runs);
    }
}
