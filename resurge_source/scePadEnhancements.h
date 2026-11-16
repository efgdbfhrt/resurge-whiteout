#pragma once
#include <stdbool.h>
#include <stdint.h>

typedef struct _ClientGameState {
    int flags;
    int localClientNum;
    int controllerIndex;
    char unknown[0x18]; // not needed so dont care
} ClientGameState;

typedef struct _PadHandleLookup {
    int32_t handle_id;
    int32_t status; // could be pad index im too unemployed to care about that rn
} PadHandleLookup;

typedef struct _GamePad {
    bool connected;
    int pad_index;
    char state_shit[0x68];
} GamePad;

typedef struct _ScePadLightBarState {
    uint8_t r;
    uint8_t g;
    uint8_t b;
    uint8_t unk;
} ScePadLightBarState;

typedef struct _keyNumToNameMapping_t {
    int keynum;
    const char* keyname_default;
    const char* keyname_xenon;
    const char* keyname_ps3;
} keyNumToNameMapping_t;

typedef bool (*scePadSetLightBar_t)(int32_t handle, ScePadLightBarState* state);
typedef bool (*scePadResetLightBar_t)(int32_t handle);

// Controller icon override settings
// 0 = Auto (detect controller type), 1 = Force Xbox, 2 = Force PlayStation
enum ControllerIconType {
    CONTROLLER_ICONS_AUTO = 0,
    CONTROLLER_ICONS_XBOX = 1,
    CONTROLLER_ICONS_PS = 2
};
extern int g_controllerIconsOverride;

int Com_LocalClient_GetControllerIndex(int localClientNum);
void apply_scePad_enhancements();
