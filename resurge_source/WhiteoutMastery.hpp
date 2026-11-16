#pragma once
#include <algorithm>
#include <atomic>
#include <climits>
#include <cmath>
#include <memory>
#include <mutex>
#include <thread>
#include "WhiteoutCamo.hpp"

// Mastery camos: four original, animated camos (camos/make_camos.py) drawn over four existing camos.
//
// A .wcam file holds static gloss/spec/normal maps, the base colour, and the inputs of the glow
// animation. A worker thread builds every animation frame twice: the glow on its own (for hosts whose
// shader shows the emissive map) and painted into the colour map (so the animation shows on any host).
// Frames are BC1-compressed with full mip chains, which keeps four camos at ~50 MB of VRAM. The main
// thread creates the D3D textures a few per frame, and every frame points the host camo images (see
// WhiteoutCamo.hpp for the image record layout) at the current animation frame.
namespace whiteout::mastery
{
    using namespace whiteout::camo;

#pragma pack(push, 1)
    struct Header { char magic[4]; uint32_t version, size, glow, frames, loopMs; };
    struct Params
    {
        int32_t scroll[4]; float cycles, glow, pulse; int32_t pulseRate;
        float sweep, sweepWidth, sweepColor[3], sparkle; int32_t sparkleRate; float sparklePower, sparkleColor[3];
        float paint; // how much of the glow is painted into the colour map
    };
#pragma pack(pop)
    static_assert(sizeof(Header) == 24 && sizeof(Params) == 80);

    enum Kind { Color, Glow, Gloss, Spec, Normal, Occlusion, Icon, KindCount };

    // One BC1 texture with all its mip levels, ready for CreateTexture2D.
    struct Bc1
    {
        uint32_t size = 0;
        std::vector<uint8_t> bytes;
        std::vector<size_t> offsets; // per mip level
    };

    // Pixels for one camo, built by a worker thread and then only read on the main thread.
    struct Data
    {
        uint32_t size = 0, glowSize = 0, frames = 0, loopMs = 1;
        std::vector<uint8_t> maps[KindCount];  // RGBA size x size: Gloss, Spec, Normal, Occlusion, Icon
        std::vector<Bc1> colorFrames, glowFrames;
    };

    struct Camo
    {
        int tier;
        std::string file;
        std::string host;   // image name prefix of the camo we draw over
        std::string icon;   // the picker swatch image
        bool glow;          // false for hosts whose shader tints the emissive map: their glow slot is blacked out
        std::shared_ptr<Data> data;      // main thread
        std::shared_ptr<Data> pending;   // handed over by the worker (under `lock`)
        std::mutex lock;
        FILETIME stamp{};                // file time of the loaded data; a newer file is reloaded
        // Created on the main thread from `data`, per colour space (0 linear, 1 sRGB):
        ID3D11ShaderResourceView* views[KindCount][2]{};
        std::vector<ID3D11ShaderResourceView*> colorViews[2], glowViews[2];
    };

    // Host choice matters: "Black Ops III" and "Nuk3Town" show the emissive map as painted, while
    // "Cyborg" and "Weaponized 115" tint it (orange / green), so those two carry the glow in colour only.
    struct HostDefaults { const char* file; const char* host; const char* icon; bool glow; };
    static constexpr HostDefaults defaults[] = {
        { "molten.wcam",  "i_t7_camo_ce_bo3",    "menu_camo_ce_bo3_pattern",    true },
        { "glacier.wcam", "i_t7_camo_ce_cyborg", "menu_camo_ce_cyborg_pattern", false },
        { "nebula.wcam",  "i_t7_camo_ce_115",    "menu_camo_ce_115_pattern",    false },
        { "prism.wcam",   "i_t7_camo_nuketown",  "menu_camo_nuketown_pattern",  true },
    };
    static Camo camos[] = {
        { 1, defaults[0].file, defaults[0].host, defaults[0].icon, defaults[0].glow },
        { 2, defaults[1].file, defaults[1].host, defaults[1].icon, defaults[1].glow },
        { 3, defaults[2].file, defaults[2].host, defaults[2].icon, defaults[2].glow },
        { 4, defaults[3].file, defaults[3].host, defaults[3].icon, defaults[3].glow },
    };

    static bool enabled = false;
    static int unlockedTier = 0;     // tiers above this keep the host camo's own look
    static char folder[MAX_PATH]{};
    static char settingsPath[MAX_PATH]{};
    static void forget_bindings();

    // Tier N can be moved to another host camo (hostN / iconN / fileN in [WhiteoutMastery]); the ini
    // is re-read whenever it changes, so hosts can be compared while the game runs.
    static void read_hosts()
    {
        bool changed = false;
        for (auto& camo : camos)
        {
            const HostDefaults& fallback = defaults[camo.tier - 1];
            char key[16], value[128];
            const auto update = [&](const char* name, const char* standard, std::string& field)
            {
                sprintf_s(key, "%s%d", name, camo.tier);
                GetPrivateProfileStringA("WhiteoutMastery", key, standard, value, sizeof(value), settingsPath);
                if (!value[0]) strcpy_s(value, standard);
                if (field != value) { field = value; changed = true; }
            };
            const std::string oldFile = camo.file;
            update("host", fallback.host, camo.host);
            update("icon", fallback.icon, camo.icon);
            update("file", fallback.file, camo.file);
            sprintf_s(key, "glow%d", camo.tier);
            const bool glow = GetPrivateProfileIntA("WhiteoutMastery", key, fallback.glow ? 1 : 0, settingsPath) == 1;
            if (glow != camo.glow) { camo.glow = glow; changed = true; }
            if (camo.file != oldFile) camo.stamp = {};
        }
        const int tier = std::clamp(static_cast<int>(GetPrivateProfileIntA("WhiteoutMastery", "tier", 0, settingsPath)), 0, 4);
        if (tier != unlockedTier) { ALOG("Mastery: unlocked tier %d", tier); unlockedTier = tier; changed = true; }
        if (changed) forget_bindings();
    }

    static void read_settings(const char* iniPath)
    {
        strcpy_s(settingsPath, iniPath);
        enabled = GetPrivateProfileIntA("WhiteoutMastery", "enabled", 0, iniPath) == 1;
        GetPrivateProfileStringA("WhiteoutMastery", "folder", "", folder, sizeof(folder), iniPath);
        if (!folder[0]) enabled = false;
        if (enabled) read_hosts();
    }

    static void watch_settings()
    {
        static ULONGLONG next = 0;
        static FILETIME stamp{};
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 2000;
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExA(settingsPath, GetFileExInfoStandard, &info) || CompareFileTime(&info.ftLastWriteTime, &stamp) == 0) return;
        const bool first = stamp.dwLowDateTime == 0 && stamp.dwHighDateTime == 0;
        stamp = info.ftLastWriteTime;
        if (!first) read_hosts();
    }

    // ------------------------------------------------------------------ BC1 compression

    static uint16_t to565(int r, int g, int b)
    {
        return static_cast<uint16_t>(((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255));
    }

    static void from565(uint16_t c, int out[3])
    {
        out[0] = ((c >> 11) & 31) * 255 / 31; out[1] = ((c >> 5) & 63) * 255 / 63; out[2] = (c & 31) * 255 / 31;
    }

    // Range-fit BC1 block (4-colour mode): endpoints are the inset bounding-box corners along the
    // block's dominant diagonal, indices the nearest of the four palette colours.
    static void encode_block(const uint8_t* rgba, size_t stride, uint32_t w, uint32_t h, uint8_t out[8])
    {
        int px[16][3];
        for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 4; ++x)
            {
                const uint8_t* p = rgba + (std::min<uint32_t>(y, h - 1)) * stride + (std::min<uint32_t>(x, w - 1)) * 4;
                px[y * 4 + x][0] = p[0]; px[y * 4 + x][1] = p[1]; px[y * 4 + x][2] = p[2];
            }
        int lo[3] = { 255, 255, 255 }, hi[3] = { 0, 0, 0 };
        for (auto& p : px) for (int c = 0; c < 3; ++c) { lo[c] = (std::min)(lo[c], p[c]); hi[c] = (std::max)(hi[c], p[c]); }
        // Pick the diagonal (sign of each channel's covariance with the widest channel).
        int axis = 0;
        for (int c = 1; c < 3; ++c) if (hi[c] - lo[c] > hi[axis] - lo[axis]) axis = c;
        double mean[3] = {};
        for (auto& p : px) for (int c = 0; c < 3; ++c) mean[c] += p[c] / 16.0;
        int a[3], b[3];
        for (int c = 0; c < 3; ++c)
        {
            double cov = 0;
            for (auto& p : px) cov += (p[c] - mean[c]) * (p[axis] - mean[axis]);
            const int inset = (hi[c] - lo[c]) / 16;
            const int l = lo[c] + inset, u = hi[c] - inset;
            a[c] = cov >= 0 ? u : l; b[c] = cov >= 0 ? l : u;
        }
        uint16_t c0 = to565(a[0], a[1], a[2]), c1 = to565(b[0], b[1], b[2]);
        if (c0 < c1) std::swap(c0, c1);
        uint32_t indices = 0;
        if (c0 != c1)
        {
            int p0[3], p1[3], pal[4][3];
            from565(c0, p0); from565(c1, p1);
            for (int c = 0; c < 3; ++c)
            {
                pal[0][c] = p0[c]; pal[1][c] = p1[c];
                pal[2][c] = (2 * p0[c] + p1[c]) / 3; pal[3][c] = (p0[c] + 2 * p1[c]) / 3;
            }
            for (int i = 0; i < 16; ++i)
            {
                int best = 0, bestError = INT_MAX;
                for (int k = 0; k < 4; ++k)
                {
                    const int dr = px[i][0] - pal[k][0], dg = px[i][1] - pal[k][1], db = px[i][2] - pal[k][2];
                    const int error = dr * dr * 2 + dg * dg * 4 + db * db;
                    if (error < bestError) { bestError = error; best = k; }
                }
                indices |= static_cast<uint32_t>(best) << (i * 2);
            }
        }
        memcpy(out, &c0, 2); memcpy(out + 2, &c1, 2); memcpy(out + 4, &indices, 4);
    }

    static void downsample(std::vector<uint8_t>& rgba, uint32_t& size)
    {
        const uint32_t to = size / 2;
        std::vector<uint8_t> next(static_cast<size_t>(to) * to * 4);
        for (uint32_t y = 0; y < to; ++y)
            for (uint32_t x = 0; x < to; ++x)
                for (int c = 0; c < 4; ++c)
                {
                    const auto at = [&](uint32_t sx, uint32_t sy) { return rgba[(static_cast<size_t>(sy) * size + sx) * 4 + c]; };
                    next[(static_cast<size_t>(y) * to + x) * 4 + c] = static_cast<uint8_t>(
                        (at(x * 2, y * 2) + at(x * 2 + 1, y * 2) + at(x * 2, y * 2 + 1) + at(x * 2 + 1, y * 2 + 1) + 2) / 4);
                }
        rgba.swap(next);
        size = to;
    }

    static Bc1 compress(std::vector<uint8_t> rgba, uint32_t size)
    {
        Bc1 result;
        result.size = size;
        for (uint32_t s = size;; )
        {
            result.offsets.push_back(result.bytes.size());
            const uint32_t blocks = (std::max)(1u, (s + 3) / 4);
            const size_t start = result.bytes.size();
            result.bytes.resize(start + static_cast<size_t>(blocks) * blocks * 8);
            for (uint32_t by = 0; by < blocks; ++by)
                for (uint32_t bx = 0; bx < blocks; ++bx)
                {
                    const uint32_t x = bx * 4, y = by * 4;
                    encode_block(rgba.data() + (static_cast<size_t>(y) * s + x) * 4, static_cast<size_t>(s) * 4,
                        (std::min)(4u, s - x), (std::min)(4u, s - y), &result.bytes[start + (static_cast<size_t>(by) * blocks + bx) * 8]);
                }
            if (s == 1) break;
            downsample(rgba, s);
        }
        return result;
    }

    // ------------------------------------------------------------------ worker: file -> frames

    // One glow frame (RGBA, n x n). Mirrors glow_frame() in camos/make_camos.py.
    static void glow_frame(const Params& p, uint32_t f, uint32_t frames, int n, const uint8_t* mask, const uint8_t* f1,
        const uint8_t* f2, const uint8_t* phase, const uint8_t* sparkle, const uint8_t* sweepMask, const uint8_t* palette, uint8_t* out)
    {
        constexpr double tau = 6.283185307179586;
        const double t = static_cast<double>(f) / frames;
        const int o1x = static_cast<int>(std::lround(p.scroll[0] * n * t)), o1y = static_cast<int>(std::lround(p.scroll[1] * n * t));
        const int o2x = static_cast<int>(std::lround(p.scroll[2] * n * t)), o2y = static_cast<int>(std::lround(p.scroll[3] * n * t));
        const int cycle = static_cast<int>(std::lround(p.cycles * 256 * t));
        const double pulse = 1.0 + p.pulse * std::sin(tau * t * p.pulseRate);
        const auto wrap = [n](int v) { return ((v % n) + n) % n; };
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                const size_t i = static_cast<size_t>(y) * n + x;
                const int a = f1[static_cast<size_t>(wrap(y + o1y)) * n + wrap(x + o1x)];
                const int b = f2[static_cast<size_t>(wrap(y + o2y)) * n + wrap(x + o2x)];
                const int index = (a + b + cycle) & 255;
                const double scale = mask[i] / 255.0 * p.glow * pulse;
                double rgb[3] = { palette[index * 3] * scale, palette[index * 3 + 1] * scale, palette[index * 3 + 2] * scale };
                if (p.sweep > 0)
                {
                    double u = std::fmod((x + y) / (2.0 * n) - t, 1.0);
                    if (u < 0) u += 1.0;
                    double band = (std::max)(0.0, 1.0 - std::fabs(u - 0.5) * p.sweepWidth);
                    band = band * band * p.sweep * sweepMask[i] / 255.0;
                    for (int c = 0; c < 3; ++c) rgb[c] += p.sweepColor[c] * band;
                }
                if (p.sparkle > 0 && sparkle[i])
                {
                    const double tw = std::pow((std::max)(0.0, std::sin(tau * (t * p.sparkleRate + phase[i] / 255.0))), p.sparklePower);
                    for (int c = 0; c < 3; ++c) rgb[c] += p.sparkleColor[c] * sparkle[i] / 255.0 * tw * p.sparkle;
                }
                for (int c = 0; c < 3; ++c) out[i * 4 + c] = static_cast<uint8_t>((std::min)(255.0, (std::max)(0.0, std::round(rgb[c]))));
                out[i * 4 + 3] = 255;
            }
    }

    static bool camo_path(const Camo& camo, char (&path)[MAX_PATH * 2])
    {
        return sprintf_s(path, "%s\\%s", folder, camo.file.c_str()) > 0;
    }

    static std::shared_ptr<Data> load_camo(const Camo& source)
    {
        char path[MAX_PATH * 2];
        if (!camo_path(source, path)) return nullptr;
        FILE* input = nullptr;
        if (fopen_s(&input, path, "rb") != 0 || !input) return nullptr;
        std::unique_ptr<FILE, decltype(&fclose)> guard(input, &fclose);
        Header header{};
        Params params{};
        if (fread(&header, sizeof(header), 1, input) != 1 || memcmp(header.magic, "WCAM", 4) != 0 || header.version != 3 ||
            header.size < 16 || header.size > 2048 || (header.size & (header.size - 1)) || header.glow < 16 ||
            header.glow > header.size || header.size % header.glow || header.frames == 0 || header.frames > 240 ||
            fread(&params, sizeof(params), 1, input) != 1)
            return nullptr;
        const size_t s = static_cast<size_t>(header.size) * header.size, g = static_cast<size_t>(header.glow) * header.glow;
        std::vector<uint8_t> color(s * 3), gloss(s), spec(s * 3), normal(s * 2), mask(g), f1(g), f2(g), phase(g), sparkle(g), sweep(g), palette(768);
        for (auto* block : { &color, &gloss, &spec, &normal, &mask, &f1, &f2, &phase, &sparkle, &sweep, &palette })
            if (fread(block->data(), 1, block->size(), input) != block->size()) return nullptr;

        auto result = std::make_shared<Data>();
        Data& camo = *result;
        camo.size = header.size; camo.glowSize = header.glow; camo.frames = header.frames; camo.loopMs = (std::max)(100u, header.loopMs);
        for (Kind kind : { Gloss, Spec, Normal, Occlusion, Icon }) camo.maps[kind].assign(s * 4, 255);
        for (size_t i = 0; i < s; ++i)
        {
            auto* ic = &camo.maps[Icon][i * 4]; ic[0] = color[i * 3]; ic[1] = color[i * 3 + 1]; ic[2] = color[i * 3 + 2];
            auto* gl = &camo.maps[Gloss][i * 4]; gl[0] = gl[1] = gl[2] = gl[3] = gloss[i];
            auto* sp = &camo.maps[Spec][i * 4]; sp[0] = spec[i * 3]; sp[1] = spec[i * 3 + 1]; sp[2] = spec[i * 3 + 2];
            // Normal: x in R and A, y in G, so both two-channel (RG) and swizzled (AG) layouts read it.
            const double nx = normal[i * 2] / 127.5 - 1.0, ny = normal[i * 2 + 1] / 127.5 - 1.0;
            const double nz = std::sqrt((std::max)(0.0, 1.0 - nx * nx - ny * ny));
            auto* nm = &camo.maps[Normal][i * 4];
            nm[0] = normal[i * 2]; nm[1] = normal[i * 2 + 1]; nm[2] = static_cast<uint8_t>(std::lround(nz * 127.5 + 127.5)); nm[3] = normal[i * 2];
        }

        const int n = static_cast<int>(header.glow), scale = static_cast<int>(header.size / header.glow);
        std::vector<uint8_t> glow(g * 4), painted(s * 4);
        for (uint32_t f = 0; f < camo.frames; ++f)
        {
            glow_frame(params, f, camo.frames, n, mask.data(), f1.data(), f2.data(), phase.data(), sparkle.data(), sweep.data(), palette.data(), glow.data());
            camo.glowFrames.push_back(compress(glow, header.glow));
            // Colour frame: base colour plus the glow (bilinearly upscaled, wrapping), clamped.
            for (uint32_t y = 0; y < header.size; ++y)
                for (uint32_t x = 0; x < header.size; ++x)
                {
                    const double gx = (x + 0.5) / scale - 0.5, gy = (y + 0.5) / scale - 0.5;
                    const int x0 = static_cast<int>(std::floor(gx)), y0 = static_cast<int>(std::floor(gy));
                    const double fx = gx - x0, fy = gy - y0;
                    const auto at = [&](int xx, int yy, int c) { return glow[((static_cast<size_t>((yy % n + n) % n)) * n + (xx % n + n) % n) * 4 + c]; };
                    const size_t i = static_cast<size_t>(y) * header.size + x;
                    for (int c = 0; c < 3; ++c)
                    {
                        const double v = (at(x0, y0, c) * (1 - fx) + at(x0 + 1, y0, c) * fx) * (1 - fy) + (at(x0, y0 + 1, c) * (1 - fx) + at(x0 + 1, y0 + 1, c) * fx) * fy;
                        painted[i * 4 + c] = static_cast<uint8_t>((std::min)(255.0, color[i * 3 + c] + v * params.paint));
                    }
                    painted[i * 4 + 3] = 255;
                }
            camo.colorFrames.push_back(compress(painted, header.size));
        }
        return result;
    }

    // Loads every camo whose file is new or changed (checked every 2 s), each on its own worker thread.
    // Editing a .wcam while the game runs shows the new look within a few seconds.
    static void watch_files()
    {
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 2000;
        for (auto& camo : camos)
        {
            if (camo.tier > unlockedTier) continue; // Not earned yet: nothing to load.
            char path[MAX_PATH * 2];
            WIN32_FILE_ATTRIBUTE_DATA info{};
            if (!camo_path(camo, path) || !GetFileAttributesExA(path, GetFileExInfoStandard, &info)) continue;
            if (CompareFileTime(&info.ftLastWriteTime, &camo.stamp) == 0) continue;
            camo.stamp = info.ftLastWriteTime;
            std::thread([&camo]
            {
                const auto started = GetTickCount64();
                auto data = load_camo(camo);
                ALOG("Mastery: %s %s (%llu ms)", camo.file.c_str(), data ? "loaded" : "could not be loaded", GetTickCount64() - started);
                if (!data) return;
                std::lock_guard guard(camo.lock);
                camo.pending = std::move(data);
            }).detach();
        }
    }

    // Takes newly loaded frames; views made from the old ones stay alive for the engine and are not reused.
    static void take_pending()
    {
        for (auto& camo : camos)
        {
            std::shared_ptr<Data> fresh;
            {
                std::lock_guard guard(camo.lock);
                fresh = std::move(camo.pending);
            }
            if (!fresh) continue;
            camo.data = std::move(fresh);
            for (auto& kind : camo.views) kind[0] = kind[1] = nullptr;
            for (int srgb = 0; srgb < 2; ++srgb) { camo.colorViews[srgb].clear(); camo.glowViews[srgb].clear(); }
        }
    }

    // ------------------------------------------------------------------ main thread: textures and binding

    static ID3D11ShaderResourceView* create_view(ID3D11Device* device, DXGI_FORMAT format, uint32_t size, UINT levels,
        const D3D11_SUBRESOURCE_DATA* data)
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = desc.Height = size;
        desc.MipLevels = levels;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* texture = nullptr;
        if (FAILED(device->CreateTexture2D(&desc, data, &texture)) || !texture) return nullptr;
        ID3D11ShaderResourceView* view = nullptr;
        device->CreateShaderResourceView(texture, nullptr, &view);
        texture->Release();
        return view;
    }

    static ID3D11ShaderResourceView* rgba_view(ID3D11Device* device, std::vector<uint8_t> rgba, uint32_t size, bool srgb)
    {
        std::vector<std::vector<uint8_t>> levels;
        for (uint32_t s = size;; )
        {
            levels.push_back(rgba);
            if (s == 1) break;
            downsample(rgba, s);
        }
        std::vector<D3D11_SUBRESOURCE_DATA> data;
        for (size_t level = 0; level < levels.size(); ++level) data.push_back({ levels[level].data(), (size >> level) * 4, 0 });
        return create_view(device, srgb ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB : DXGI_FORMAT_R8G8B8A8_UNORM, size,
            static_cast<UINT>(levels.size()), data.data());
    }

    static ID3D11ShaderResourceView* bc1_view(ID3D11Device* device, const Bc1& texture, bool srgb)
    {
        std::vector<D3D11_SUBRESOURCE_DATA> data;
        for (size_t level = 0; level < texture.offsets.size(); ++level)
        {
            const uint32_t s = (std::max)(1u, texture.size >> level);
            data.push_back({ texture.bytes.data() + texture.offsets[level], (std::max)(1u, (s + 3) / 4) * 8, 0 });
        }
        return create_view(device, srgb ? DXGI_FORMAT_BC1_UNORM_SRGB : DXGI_FORMAT_BC1_UNORM, texture.size,
            static_cast<UINT>(data.size()), data.data());
    }

    static bool is_srgb(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_BC1_UNORM_SRGB: case DXGI_FORMAT_BC2_UNORM_SRGB: case DXGI_FORMAT_BC3_UNORM_SRGB:
        case DXGI_FORMAT_BC7_UNORM_SRGB: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            return true;
        default:
            return false;
        }
    }

    struct Binding { int index; Camo* camo; Kind kind; std::string name; int srgb = -1; };
    static std::vector<Binding> bindings;

    static void forget_bindings() { bindings.clear(); }

    static bool kind_for(const char* suffix, Kind& kind)
    {
        if (!strcmp(suffix, "c") || !strcmp(suffix, "c2")) kind = Color;
        else if (suffix[0] == 'e') kind = Glow;
        else if (!strcmp(suffix, "g")) kind = Gloss;
        else if (!strcmp(suffix, "s")) kind = Spec;
        else if (!strcmp(suffix, "n")) kind = Normal;
        else if (!strcmp(suffix, "o")) kind = Occlusion;
        else return false;
        return true;
    }

    // Finds every image of the four host camos (any variant: _c, _e, _alt_g, ...) and their swatches.
    static void scan_images()
    {
        bindings.clear();
        const uint64_t limit = REBASE(dataEndRva);
        char name[128];
        for (int index = 0; index < 60000; ++index)
        {
            if (REBASE(imagePoolRva) + imageSize * (index + 1) > limit) break;
            uint64_t namePtr = 0;
            if (!read_qword(REBASE(imagePoolRva) + imageSize * index + 0xF8, namePtr) || !read_name(namePtr, name, sizeof(name))) continue;
            for (auto& camo : camos)
            {
                if (camo.tier > unlockedTier) continue;
                if (camo.icon == name) { bindings.push_back({ index, &camo, Icon, name }); break; }
                const size_t prefix = camo.host.size();
                if (prefix == 0 || strncmp(name, camo.host.c_str(), prefix) != 0 || name[prefix] != '_') continue;
                const char* suffix = strrchr(name, '_') + 1;
                Kind kind;
                if (kind_for(suffix, kind)) bindings.push_back({ index, &camo, kind, name });
                break;
            }
        }
        ALOG("Mastery: found %zu host images", bindings.size());
    }

    static ID3D11Device* device_from(uint64_t view)
    {
        ID3D11Device* device = nullptr;
        reinterpret_cast<ID3D11ShaderResourceView*>(view)->GetDevice(&device);
        return device;
    }

    // The view a binding should show right now, creating views as the per-frame budget allows.
    static ID3D11ShaderResourceView* current_view(Binding& binding, uint64_t original, ULONGLONG now, int& budget)
    {
        Camo& camo = *binding.camo;
        const Data& data = *camo.data;
        const bool srgb = binding.srgb == 1;
        if (binding.kind == Glow && !camo.glow)
        {
            static ID3D11ShaderResourceView* black[2]{};
            if (!black[binding.srgb] && budget > 0)
            {
                if (ID3D11Device* device = device_from(original))
                {
                    std::vector<uint8_t> pixels(4 * 4 * 4, 0);
                    for (size_t i = 3; i < pixels.size(); i += 4) pixels[i] = 255;
                    black[binding.srgb] = rgba_view(device, pixels, 4, srgb);
                    device->Release();
                }
                --budget;
            }
            return black[binding.srgb];
        }
        if (binding.kind == Color || binding.kind == Glow)
        {
            auto& views = (binding.kind == Color ? camo.colorViews : camo.glowViews)[binding.srgb];
            const auto& frames = binding.kind == Color ? data.colorFrames : data.glowFrames;
            if (views.size() < frames.size())
            {
                ID3D11Device* device = budget > 0 ? device_from(original) : nullptr;
                while (device && budget > 0 && views.size() < frames.size())
                {
                    views.push_back(bc1_view(device, frames[views.size()], srgb));
                    --budget;
                }
                if (device) device->Release();
                if (views.size() < frames.size()) return nullptr;
            }
            return views[(now % data.loopMs) * views.size() / data.loopMs];
        }
        auto& slot = camo.views[binding.kind][binding.srgb];
        if (!slot && budget > 0)
        {
            if (ID3D11Device* device = device_from(original))
            {
                slot = rgba_view(device, data.maps[binding.kind], data.size, srgb);
                device->Release();
            }
            --budget;
        }
        return slot;
    }

    static void apply()
    {
        if (!enabled) return;
        watch_settings();
        if (unlockedTier == 0) return;
        watch_files();
        take_pending();
        static ULONGLONG nextScan = 0;
        const auto now = GetTickCount64();
        // Images are reloaded with every map: re-scan when a bound slot no longer has our name.
        char name[128];
        bool stale = bindings.empty();
        for (const auto& binding : bindings)
        {
            uint64_t namePtr = 0;
            if (!read_qword(REBASE(imagePoolRva) + imageSize * binding.index + 0xF8, namePtr) ||
                !read_name(namePtr, name, sizeof(name)) || binding.name != name) { stale = true; break; }
        }
        if (stale)
        {
            if (now < nextScan) return;
            nextScan = now + 3000;
            scan_images();
        }

        int budget = 8; // Texture creations per frame, to avoid a hitch.
        for (auto& binding : bindings)
        {
            if (!binding.camo->data) continue;
            const uint64_t item = REBASE(imagePoolRva) + imageSize * binding.index;
            uint64_t b0 = 0, a8 = 0;
            read_qword(item + 0xB0, b0);
            read_qword(item + 0xA8, a8);
            const uint64_t original = user_pointer(a8) ? a8 : b0;
            if (!user_pointer(original)) continue;
            if (binding.srgb < 0)
            {
                // Learn the colour space from the game's own view the first time (before we replace it).
                D3D11_SHADER_RESOURCE_VIEW_DESC desc{};
                reinterpret_cast<ID3D11ShaderResourceView*>(original)->GetDesc(&desc);
                binding.srgb = is_srgb(desc.Format) ? 1 : 0;
            }
            ID3D11ShaderResourceView* view = current_view(binding, original, now, budget);
            if (!view) continue;
            for (const uint64_t field : { 0xB0ull, 0xA8ull })
            {
                uint64_t current = 0;
                read_qword(item + field, current);
                if (!user_pointer(current) || current == reinterpret_cast<uint64_t>(view)) continue;
                view->AddRef(); // The engine holds (and may release) the reference in its slot.
                *reinterpret_cast<uint64_t*>(item + field) = reinterpret_cast<uint64_t>(view);
            }
        }
    }
}
