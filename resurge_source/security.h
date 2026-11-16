#pragma once
#include "framework.h"
#include <fstream>
#include <filesystem>

#define PATCH_CONFIG_LOCATION "T7Patch.ini"
#define DEBUG_SECURITY 0

// Global toggle for security prefix (defined in dllmain.cpp)
extern bool g_useSecurityPrefix;

// Global toggle to disable ALL security hooks for matchmaking testing
extern bool g_disableSecurityHooks;

struct sec_config
{
	std::filesystem::file_time_type modified;
	uint64_t password_history[2];
	uint64_t password_changed_time;
	uint8_t is_friends_only;
    uint8_t is_patch_on;
    uint8_t is_password_on;
    uint8_t exists;
	char playername[16];

    bool fs_exists(const char* filename);
    bool update_watcher_time(const char* path);
    void loadfrom(const char* path);
    void saveto(const char* path);
};

// ZBR prefix is only active if both enabled globally AND password is configured
#define ZBR_PREFIX_BYTE (g_useSecurityPrefix ? (uint8_t)((security::conf.password_history[1] & 0xFF0000) >> 16) : 0)
#define ZBR_PREFIX_BYTE2 (g_useSecurityPrefix ? (uint8_t)((security::conf.password_history[1] & 0xFF000000) >> 24) : 0)

namespace security
{
	extern sec_config conf;
	void init();
	void init_delayed();
	bool handle_exception(PEXCEPTION_RECORD ExceptionRecord, PCONTEXT ContextRecord);

    uint64_t canon_hash64(const char* input);
    
		// Retrieve a string config value by key (returns nullptr if not found)
		const char* get_config_value(const char* key);
}

#if DEBUG_SECURITY
#define SALOG(...) ALOG(__VA_ARGS__)
#else
#define SALOG(...)
#endif