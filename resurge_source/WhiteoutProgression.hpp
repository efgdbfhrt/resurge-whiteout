#pragma once
#include "hks.h"
#include <algorithm>
#include <array>
#include <charconv>
#include <filesystem>
#include <string_view>
#include <cctype>
#include "WhiteoutCamo.hpp"
#include "WhiteoutMastery.hpp"

namespace whiteout
{
    // Non-zero while Whiteout code runs inside its own SEH guard. The global exception hook lets
    // those faults reach the guard instead of treating them as a crash.
    inline thread_local int guardDepth = 0;

    static wchar_t roundFile[32768]{};
    static wchar_t statusFile[32768]{};
    static bool officialMaps = false;
    static char localSaves[MAX_PATH]{};
    static bool statsSession = false;
    static bool camos = false;
    static bool attachments = false;
    static bool masterRank = false;
    static bool weaponMax = false;
    static bool bots = false;
    static int botAllies = 6;
    static int botEnemies = 6;
    static int botDifficulty = 3;
    // Last career zombie-kill count read from the stats (see track_kills), reported in the status file.
    static uint32_t zmKills = 0;
    static int zmKillsFile = -1;

    static uint32_t session_mode()
    {
        return *reinterpret_cast<const uint32_t*>(REBASE(0x18AE65C4));
    }

    static bool in_frontend()
    {
        return strcmp(reinterpret_cast<const char*>(REBASE(0x90FC4D0)), "core_frontend") == 0;
    }

    static bool local_stats()
    {
        return statsSession && (session_mode() & 0x3C0) == 0x40;
    }

    // Unlock options (camos, attachments, weapon level, Mega GobbleGums) follow the launcher's ini in
    // every normal session, online included. A practice session keeps them to its offline profile and
    // the official-maps session stays vanilla.
    static bool progression_on()
    {
        if (officialMaps) return false;
        return statsSession ? local_stats() : true;
    }

    static void read_session_environment()
    {
        if (GetEnvironmentVariableW(L"WHITEOUT_ROUND_FILE", roundFile, _countof(roundFile)) >= _countof(roundFile))
            roundFile[0] = L'\0';
        wchar_t value[2]{};
        officialMaps = GetEnvironmentVariableW(L"WHITEOUT_OFFICIAL_MAPS", value, _countof(value)) == 1 && value[0] == L'1';
        statsSession = GetEnvironmentVariableW(L"WHITEOUT_STATS_SESSION", value, _countof(value)) == 1 && value[0] == L'1';
        if (GetEnvironmentVariableA("WHITEOUT_LOCAL_SAVES", localSaves, _countof(localSaves)) >= _countof(localSaves))
            localSaves[0] = '\0';
        if (GetEnvironmentVariableW(L"WHITEOUT_STATUS_FILE", statusFile, _countof(statusFile)) >= _countof(statusFile))
            statusFile[0] = L'\0';
    }

    // Current Zombies round from the HUD model, or 0 outside a Zombies match.
    static int current_round()
    {
        if ((session_mode() & 0xF) != 0) return 0;
        using FindModel = uint16_t(__fastcall*)(uint16_t, const char*, bool, bool);
        const auto findModel = reinterpret_cast<FindModel>(REBASE(0x21348E0));
        const uint16_t controller = *reinterpret_cast<const uint16_t*>(REBASE(0x186CD218));
        const uint16_t global = *reinterpret_cast<const uint16_t*>(REBASE(0x18835224));
        uint16_t model = controller ? findModel(controller, "GameScore.roundsPlayed", false, false) : 0;
        if (!model && global) model = findModel(global, "GameScore.roundsPlayed", false, false);
        if (!model) return 0;
        const auto slot = REBASE(0x186CD228) + 40ull * model;
        if (*reinterpret_cast<const uint32_t*>(slot) != 3) return 0; // Integer model value.
        const int encodedRound = *reinterpret_cast<const int*>(slot + 8);
        return encodedRound > 1 && encodedRound <= 10001 ? encodedRound - 1 : 0;
    }

    // Live status for the launcher (friends list): mode, map and round, refreshed every 3 s.
    static void write_status()
    {
        if (!statusFile[0]) return;
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 3000;
        char map[64]{};
        strncpy_s(map, reinterpret_cast<const char*>(REBASE(0x90FC4D0)), _TRUNCATE);
        for (char& c : map) if (c && (c < 0x20 || c > 0x7E)) c = '?';
        const uint32_t mode = session_mode() & 0xF;
        const int round = strcmp(map, "core_frontend") == 0 ? 0 : current_round();
        wchar_t temporary[32800];
        swprintf_s(temporary, L"%s.tmp", statusFile);
        if (FILE* output = _wfopen(temporary, L"w"))
        {
            fprintf(output, "map=%s\nmode=%s\nround=%d\n", map,
                mode == 0 ? "zm" : mode == 1 ? "mp" : mode == 2 ? "cp" : "other", round);
            if (zmKillsFile >= 0) fprintf(output, "zmkills=%u\nzmstats=%d\n", zmKills, zmKillsFile);
            if (fclose(output) == 0) MoveFileExW(temporary, statusFile, MOVEFILE_REPLACE_EXISTING);
        }
    }

    static void record_round()
    {
        if (!roundFile[0] || (session_mode() & 0xF) != 0) return;
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 1000;

        using FindModel = uint16_t(__fastcall*)(uint16_t, const char*, bool, bool);
        const auto findModel = reinterpret_cast<FindModel>(REBASE(0x21348E0));
        const uint16_t controller = *reinterpret_cast<const uint16_t*>(REBASE(0x186CD218));
        const uint16_t global = *reinterpret_cast<const uint16_t*>(REBASE(0x18835224));
        uint16_t model = controller ? findModel(controller, "GameScore.roundsPlayed", false, false) : 0;
        if (!model && global) model = findModel(global, "GameScore.roundsPlayed", false, false);
        if (!model) return;

        const auto slot = REBASE(0x186CD228) + 40ull * model;
        if (*reinterpret_cast<const uint32_t*>(slot) != 3) return; // Integer model value.
        const int encodedRound = *reinterpret_cast<const int*>(slot + 8);
        if (encodedRound <= 1 || encodedRound > 10001) return;
        const int round = encodedRound - 1; // The engine's model is one above the HUD round.
        static int best = 0;
        if (round <= best) return;
        if (FILE* output = _wfopen(roundFile, L"a"))
        {
            fprintf(output, "%d\n", round);
            if (fclose(output) == 0) best = round;
        }
    }

    struct TableCell { const char* value; uint64_t hash; };
    struct StringTable { const char* name; int columns; int rows; const TableCell* cells; };

    static const StringTable* table(const char* name)
    {
        const StringTable* result = nullptr;
        reinterpret_cast<void(__fastcall*)(const char*, const StringTable**)>(REBASE(0x238E020))(name, &result);
        return result && result->cells && result->rows > 0 && result->rows < 12000 &&
            result->columns > 0 && result->columns < 100 ? result : nullptr;
    }

    static std::string_view cell(const StringTable& data, int row, int column)
    {
        if (column >= data.columns) return {};
        const char* value = data.cells[row * data.columns + column].value;
        return value ? value : "";
    }

    static int number(std::string_view value)
    {
        int result = -1;
        const auto [end, error] = std::from_chars(value.data(), value.data() + value.size(), result);
        return error == std::errc{} && end == value.data() + value.size() ? result : -1;
    }

    struct Gum { int id; int index; std::string reference; int quantity; };
    static std::vector<Gum> gums;
    static std::array<int, 5> packSlots{ -1, -1, -1, -1, -1 };

    static void load_catalog(const char* iniPath)
    {
        static bool masteryRead = false;
        if (!masteryRead && !officialMaps) { masteryRead = true; mastery::read_settings(iniPath); }
        if (officialMaps || !gums.empty()) return;
        bots = GetPrivateProfileIntA("WhiteoutBots", "enabled", 0, iniPath) == 1;
        botAllies = std::clamp(static_cast<int>(GetPrivateProfileIntA("WhiteoutBots", "allies", 6, iniPath)), 0, 9);
        botEnemies = std::clamp(static_cast<int>(GetPrivateProfileIntA("WhiteoutBots", "enemies", 6, iniPath)), 0, 9);
        botDifficulty = std::clamp(static_cast<int>(GetPrivateProfileIntA("WhiteoutBots", "difficulty", 3, iniPath)), 0, 3);
        camos = GetPrivateProfileIntA("WhiteoutProgression", "camos", 0, iniPath) == 1;
        attachments = GetPrivateProfileIntA("WhiteoutProgression", "attachments", 0, iniPath) == 1;
        masterRank = GetPrivateProfileIntA("WhiteoutProgression", "level_1000", 0, iniPath) == 1;
        weaponMax = GetPrivateProfileIntA("WhiteoutProgression", "weapon_max", 0, iniPath) == 1;
        const bool useInventory = GetPrivateProfileIntA("WhiteoutProgression", "enabled", 0, iniPath) == 1;
        if (!useInventory) return;
        const auto* stats = table("gamedata/stats/zm/zm_statsTable.csv");
        const auto* loot = table("gamedata/loot/zmlootitems.csv");
        if (!stats || !loot) return;
        const int defaultQuantity = static_cast<int>(GetPrivateProfileIntA("WhiteoutProgression", "all_mega", -1, iniPath));

        for (int row = 0; row < stats->rows; ++row)
        {
            if (cell(*stats, row, 2) != "bubblegum_consumable") continue;
            const auto ref = cell(*stats, row, 4);
            const int index = number(cell(*stats, row, 0));
            if (ref.empty() || index < 0) continue;
            const std::string key(ref);
            const int overrideQuantity = static_cast<int>(GetPrivateProfileIntA("WhiteoutGums", key.c_str(), -1, iniPath));
            const int quantity = overrideQuantity < 0 ? defaultQuantity : overrideQuantity;
            if (quantity < 0 || quantity > 999) continue;
            for (int item = 0; item < loot->rows; ++item)
            {
                if (cell(*loot, item, 0) != ref) continue;
                const int id = number(cell(*loot, item, 1));
                if (id > 0) gums.push_back({ id, index, key, quantity });
                break;
            }
        }
        for (size_t slot = 0; slot < packSlots.size(); ++slot)
        {
            const std::string key = "slot" + std::to_string(slot + 1);
            const int index = static_cast<int>(GetPrivateProfileIntA("WhiteoutPack", key.c_str(), -1, iniPath));
            for (const auto& gum : gums)
                if (gum.index == index && gum.quantity > 0) packSlots[slot] = index;
        }
        if (!gums.empty()) ALOG("Whiteout: loaded %zu Mega GobbleGum items from game tables", gums.size());
    }

    static int configured_gum(const char* reference)
    {
        if (!reference || !progression_on() || (session_mode() & 0xF) != 0) return -1;
        for (const auto& gum : gums)
            if (gum.reference == reference) return gum.quantity;
        return -1;
    }

    static constexpr std::array<const char*, 5> localDlcMaps{
        "zm_factory", "zm_castle", "zm_island", "zm_stalingrad", "zm_genesis"
    };

    // Local (offline / private) sessions, regardless of the stats mode. The installed DLC
    // Zombies maps are exposed here so every Whiteout launch lists them.
    static bool local_session()
    {
        return (session_mode() & 0x3C0) == 0x40;
    }

    static bool installed_dlc_map(const char* mapName)
    {
        if (!mapName || officialMaps) return false;
        const auto it = std::find_if(localDlcMaps.begin(), localDlcMaps.end(),
            [mapName](const char* name) { return strcmp(name, mapName) == 0; });
        if (it == localDlcMaps.end()) return false;

        // The zone folder does not change while the game runs; check each fastfile once.
        static std::array<int8_t, localDlcMaps.size()> present{ -1, -1, -1, -1, -1 };
        auto& state = present[static_cast<size_t>(it - localDlcMaps.begin())];
        if (state < 0)
        {
            std::error_code error;
            const auto path = std::filesystem::path("zone") / (std::string(mapName) + ".ff");
            const bool exists = std::filesystem::is_regular_file(path, error) && !error;
            const auto size = exists ? std::filesystem::file_size(path, error) : 0;
            state = exists && !error && size > 0 ? 1 : 0;
        }
        return state == 1;
    }

    // True when every DLC map pack (Zombies maps and the four MP map packs) is on disk. Then the full
    // DLC ownership path can be used, which Private/online Zombies lobbies need before they launch a
    // DLC map; Solo games only need installed_dlc_map.
    static bool all_dlc_installed()
    {
        static const char* maps[] = {
            "zm_factory", "zm_castle", "zm_island", "zm_stalingrad", "zm_genesis",
            "mp_ruins", "mp_cryogen", "mp_rome", "mp_kung_fu", "mp_conduit", "mp_crucible", "mp_aerospace", "mp_banzai",
            "mp_city", "mp_miniature", "mp_shrine", "mp_western", "mp_arena", "mp_rise", "mp_skyjacked", "mp_waterpark" };
        wchar_t exe[MAX_PATH]{};
        if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return false;
        const auto zone = std::filesystem::path(exe).parent_path() / "zone";
        for (const char* map : maps)
        {
            std::error_code error;
            const auto path = zone / (std::string(map) + ".ff");
            if (!std::filesystem::is_regular_file(path, error) || error || std::filesystem::file_size(path, error) == 0 || error)
                return false;
        }
        return true;
    }

    static uint32_t installed_dlc_mask()
    {
        if (officialMaps) return 0;
        const auto packForMap = reinterpret_cast<int(__fastcall*)(const char*)>(REBASE(0x221A210));
        uint32_t mask = 0;
        for (const char* name : localDlcMaps)
        {
            if (!installed_dlc_map(name)) continue;
            const int pack = packForMap(name);
            if (pack > 0 && pack <= 13) mask |= 1u << (pack + 1);
        }
        return mask;
    }

    struct DdlState
    {
        bool valid;
        int offset;
        int arrayIndex;
        const void* member;
        const void* def;
    };
    static_assert(sizeof(DdlState) == 32);

    static void apply_weapon_xp(int mode, const DdlState* root, void* context)
    {
        static std::array<void*, 2> applied{};
        if (applied[mode] == context) return;
        const auto move = reinterpret_cast<bool(__fastcall*)(const DdlState*, DdlState*, const char*)>(REBASE(0x14E34D0));
        const auto index = reinterpret_cast<bool(__fastcall*)(const DdlState*, DdlState*, int)>(REBASE(0x14E3720));
        const auto get = reinterpret_cast<uint32_t(__fastcall*)(const DdlState*, const void*)>(REBASE(0x25E7320));
        const auto set = reinterpret_cast<bool(__fastcall*)(const DdlState*, void*, uint32_t)>(REBASE(0x25E7290));
        DdlState itemStats{};
        if (!move(root, &itemStats, "ItemStats") || !itemStats.member) return;
        const auto catalog = REBASE(0x1A0C03C0) + 0x13570ull * mode;
        // Highest XP in the gun rank table: the weapon's last level, whatever its attachments.
        constexpr int rankRows = (0x13570 - 0xF148) / 12;
        int updated = 0;
        for (int item = 0; item < 256; ++item)
        {
            const auto record = catalog + 0xD8ull * item + 0x10;
            if (!(*reinterpret_cast<const uint8_t*>(record + 0xB) & 4)) continue;
            uint32_t cap = 0;
            int topLevel = -1;
            for (int attachment = 0; attachment < 45; ++attachment)
            {
                if (!*reinterpret_cast<const uint8_t*>(record + 0x3B + attachment)) continue;
                const auto level = *reinterpret_cast<const uint16_t*>(record + 0x68 + 2 * attachment);
                if (level >= rankRows) continue;
                topLevel = (std::max)(topLevel, static_cast<int>(level));
                cap = (std::max)(cap, *reinterpret_cast<const uint32_t*>(catalog + 0xF148 + 12ull * level));
            }
            if (cap == 0) continue;
            DdlState weapon{}, xp{};
            if (!index(&itemStats, &weapon, item) || !move(&weapon, &xp, "xp")) continue;
            if (get(&xp, context) >= cap || (set(&xp, context, cap) && get(&xp, context) == cap))
                ++updated;
        }
        if (updated > 0)
        {
            applied[mode] = context;
            ALOG("Whiteout: verified weapon XP thresholds for %d items in mode %d", updated, mode);
        }
    }

    // Returns the stats context for a file type when it is built on the given DDL definition.
    // Unknown file types are probed under SEH so a bad slot cannot take the game down.
    static void* probe_stats_context(int file, const void* def)
    {
        const auto getContext = reinterpret_cast<void*(__fastcall*)(int, int, int)>(REBASE(0x234ECA0));
        void* found = nullptr;
        ++guardDepth;
        __try
        {
            void* context = getContext(0, file, 0);
            if (context && *reinterpret_cast<void* const*>(static_cast<char*>(context) + 16) == def) found = context;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        --guardDepth;
        return found;
    }

    // The stats context the current session uses for `mode` (offline or online file), or nullptr.
    static void* session_stats_context(int mode, const DdlState* root, int& fileType)
    {
        const bool offline = local_stats() || local_session();
        if (offline)
        {
            fileType = mode == 0 ? 18 : 13; // GDK offline ZM and MP stats files.
            return probe_stats_context(fileType, root->def);
        }
        static std::array<int, 2> onlineFile{ -1, -1 };
        if (onlineFile[mode] >= 0)
        {
            fileType = onlineFile[mode];
            return probe_stats_context(fileType, root->def);
        }
        for (int file = 0; file < 40; ++file)
        {
            if (file == 13 || file == 18) continue;
            if (void* context = probe_stats_context(file, root->def))
            {
                onlineFile[mode] = fileType = file;
                ALOG("Whiteout: online stats for mode %d use file type %d", mode, file);
                return context;
            }
        }
        return nullptr;
    }

    // Career zombie kills from the Zombies stats (PlayerStatsList.KILLS), read in the Zombies menus.
    // The launcher turns increases of this number into mastery camo progress.
    static bool read_zm_kills(uint32_t& kills, int& fileType)
    {
        if ((session_mode() & 0xF) != 0 || !in_frontend()) return false;
        const auto getRoot = reinterpret_cast<const DdlState*(__fastcall*)(int)>(REBASE(0x1FADED0));
        const auto* root = getRoot(0);
        if (reinterpret_cast<uintptr_t>(root) < 0x1000 || !root->valid || !root->def) return false;
        void* context = session_stats_context(0, root, fileType);
        if (!context) return false;
        const auto move = reinterpret_cast<bool(__fastcall*)(const DdlState*, DdlState*, const char*)>(REBASE(0x14E34D0));
        const auto get = reinterpret_cast<uint32_t(__fastcall*)(const DdlState*, const void*)>(REBASE(0x25E7320));
        DdlState players{}, node{}, value{};
        if (!move(root, &players, "PlayerStatsList") || !move(&players, &node, "KILLS") || !move(&node, &value, "statValue")) return false;
        kills = get(&value, context);
        return true;
    }

    static void track_kills()
    {
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 2000;
        uint32_t kills = 0;
        int file = -1;
        if (!read_zm_kills(kills, file)) return;
        if (kills != zmKills || file != zmKillsFile) ALOG("Whiteout: zombie kills %u (stats file %d)", kills, file);
        zmKills = kills;
        zmKillsFile = file;
    }

    static void apply_stats()
    {
        if ((!masterRank && !weaponMax) || !progression_on() || !in_frontend()) return;
        const int mode = session_mode() & 0xF;
        if (mode != 0 && mode != 1) return;
        static std::array<void*, 2> applied{};
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 1000;
        const auto getRoot = reinterpret_cast<const DdlState*(__fastcall*)(int)>(REBASE(0x1FADED0));
        const auto* root = getRoot(mode);
        if (reinterpret_cast<uintptr_t>(root) < 0x1000 || !root->valid || !root->def) return;

        void* context = nullptr;
        const bool offline = local_stats() || local_session();
        if (offline)
            context = probe_stats_context(mode == 0 ? 18 : 13, root->def); // GDK offline ZM and MP stats files.
        else
        {
            // Online stats live in another file type with the same DDL; find it once per mode.
            static std::array<int, 2> onlineFile{ -1, -1 };
            if (onlineFile[mode] >= 0)
                context = probe_stats_context(onlineFile[mode], root->def);
            else
            {
                for (int file = 0; file < 40 && !context; ++file)
                {
                    if (file == 13 || file == 18) continue;
                    if ((context = probe_stats_context(file, root->def)) != nullptr)
                    {
                        onlineFile[mode] = file;
                        ALOG("Whiteout: online stats for mode %d use file type %d", mode, file);
                    }
                }
            }
        }
        if (!context) return;
        if (weaponMax) apply_weapon_xp(mode, root, context);
        // Level 1000 is written to whichever stats file the session uses (online or offline), the
        // same as weapon XP; Multiplayer showed rank 1 online before because only offline was written.
        if (!masterRank || applied[mode] == context) return;
        const auto move = reinterpret_cast<bool(__fastcall*)(const DdlState*, DdlState*, const char*)>(REBASE(0x14E34D0));
        const auto get = reinterpret_cast<uint32_t(__fastcall*)(const DdlState*, const void*)>(REBASE(0x25E7320));
        const auto set = reinterpret_cast<bool(__fastcall*)(const DdlState*, void*, uint32_t)>(REBASE(0x25E7290));
        DdlState players{};
        if (!move(root, &players, "PlayerStatsList")) return;

        const auto* rankTable = table(mode == 0 ? "gamedata/tables/zm/zm_paragonranktable.csv" :
            "gamedata/tables/mp/mp_paragonranktable.csv");
        const int xpRow = mode == 0 ? 965 : 945;
        if (!rankTable || rankTable->rows <= xpRow) return;
        const int xp = number(cell(*rankTable, xpRow, 7));
        if (xp < 0) return;
        struct Change { const char* key; uint32_t value; DdlState state{}; uint32_t before{}; };
        // RANKXP is only needed in Multiplayer (top of rank 55); Zombies already shows 1000 without it.
        Change changes[] = {
            { "PLEVEL", 11 },
            { "HASPRESTIGED", 1 },
            { "RANK", mode == 0 ? 34u : 54u },
            { "RANKXP", 1457200u },
            { "PARAGON_RANK", mode == 0 ? 964u : 944u },
            { "PARAGON_RANKXP", static_cast<uint32_t>(xp + (mode == 0 ? 54244 : 55600)) }
        };
        for (auto& change : changes)
        {
            if (mode == 0 && strcmp(change.key, "RANKXP") == 0) continue;
            DdlState node{};
            if (!move(&players, &node, change.key) || !move(&node, &change.state, "statValue")) return;
            change.before = get(&change.state, context);
        }
        // Write every stat; if any write does not stick, put back what was there before.
        size_t written = 0;
        bool ok = true;
        for (const auto& change : changes)
        {
            if (mode == 0 && strcmp(change.key, "RANKXP") == 0) { ++written; continue; }
            if (!set(&change.state, context, change.value) || get(&change.state, context) != change.value) { ok = false; ++written; break; }
            ++written;
        }
        if (!ok)
        {
            while (written > 0)
            {
                auto& change = changes[--written];
                if (mode == 0 && strcmp(change.key, "RANKXP") == 0) continue;
                set(&change.state, context, change.before);
            }
            return;
        }
        applied[mode] = context;
        ALOG("Whiteout: verified local level 1000 in mode %d", mode);
    }

    static void apply_local_gums()
    {
        if (gums.empty() || !progression_on() || (session_mode() & 0xF) != 0) return;
        const auto cache = REBASE(0xB60A130);
        if (*reinterpret_cast<const int*>(cache + 0x13BE0) != 4 ||
            *reinterpret_cast<const int*>(cache + 0x34C) != 3) return;
        static bool applied = false;
        if (applied) return;
        using FindItem = int*(__fastcall*)(int, int);
        using SetItem = void(__fastcall*)(int, int, int, int, int, uint16_t);
        const auto findItem = reinterpret_cast<FindItem>(REBASE(0x1EFF100));
        const auto setItem = reinterpret_cast<SetItem>(REBASE(0x1EFF170));
        for (const auto& item : gums)
        {
            if (auto* entry = findItem(0, item.id))
                entry[1] = item.quantity;
            else if (item.quantity > 0)
                setItem(0, item.id, item.quantity, -1, -1, 1);
        }
        applied = true;
        ALOG("Whiteout: applied the local Mega GobbleGum profile");
    }

    static void apply_pack()
    {
        if (!progression_on() || (session_mode() & 0xF) != 0 || !in_frontend()) return;
        if (std::none_of(packSlots.begin(), packSlots.end(), [](int slot) { return slot >= 0; })) return;
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 1000;
        struct Loadout { void* context; const void* root; } loadout{};
        const int type = ((session_mode() >> 14) & 0xF) == 4 ? 5 : 9;
        reinterpret_cast<Loadout*(__fastcall*)(Loadout*, int, int)>(REBASE(0x1FB1C40))(&loadout, 0, type);
        if (!loadout.context || !loadout.root) return;
        static void* applied = nullptr;
        if (applied == loadout.context) return;
        const auto get = reinterpret_cast<int(__fastcall*)(const Loadout*, int, int)>(REBASE(0x278BDF0));
        const auto set = reinterpret_cast<void(__fastcall*)(const Loadout*, int, int, int)>(REBASE(0x278BEF0));
        for (size_t slot = 0; slot < packSlots.size(); ++slot)
        {
            const int index = packSlots[slot];
            if (index < 0) continue;
            const int position = static_cast<int>(slot);
            if (get(&loadout, 0, position) != index) set(&loadout, 0, position, index);
            if (get(&loadout, 0, position) != index) return;
        }
        applied = loadout.context;
        ALOG("Whiteout: verified the selected Mega items in offline GobbleGum Pack 1");
    }

    // Multiplayer lobbies the player hosts (Custom Games, offline matches) fill vacant places with
    // bots. The lobby reads these dvars when the match starts.
    static void apply_bots()
    {
        if (!bots || officialMaps || (session_mode() & 0xF) != 1 || !in_frontend()) return;
        static ULONGLONG next = 0;
        const auto now = GetTickCount64();
        if (now < next) return;
        next = now + 1000;
        char value[8];
        sprintf_s(value, "%d", botAllies);
        Dvar_SetFromStringByName("bot_maxAllies", value);
        sprintf_s(value, "%d", botEnemies);
        Dvar_SetFromStringByName("bot_maxAxis", value);
        Dvar_SetFromStringByName("bot_maxFree", "0");
        sprintf_s(value, "%d", botDifficulty);
        Dvar_SetFromStringByName("bot_difficulty", value);
    }

    // Each per-frame task runs under SEH: a stale pointer in game data skips that task for the
    // frame instead of crashing the game, and a task that keeps faulting is switched off.
    template <typename Task>
    static void guarded(Task task, int& faults, const char* name)
    {
        if (faults >= 5) return;
        ++guardDepth;
        __try { task(); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            if (++faults == 5) ALOG("Whiteout: disabled %s after repeated faults", name);
        }
        --guardDepth;
    }

    static void frame_tasks()
    {
        static int roundFaults = 0, gumFaults = 0, packFaults = 0, statFaults = 0, botFaults = 0, statusFaults = 0;
        guarded(record_round, roundFaults, "round tracking");
        guarded(write_status, statusFaults, "live status");
        guarded(apply_local_gums, gumFaults, "GobbleGum inventory");
        guarded(apply_pack, packFaults, "GobbleGum pack");
        guarded(apply_stats, statFaults, "stats unlocks");
        guarded(apply_bots, botFaults, "bot settings");
        static int killFaults = 0;
        guarded(track_kills, killFaults, "kill tracking");
        static int camoFaults = 0, masteryFaults = 0;
        guarded(camo::probe, camoFaults, "camo probe");
        guarded(mastery::apply, masteryFaults, "mastery camos");
    }
}
