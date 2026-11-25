// SPDX-License-Identifier: GPL-3.0-or-later
// Synthetic ABI probe; no title-specific data or binaries.
#include <windows.h>
#include <stdint.h>

__declspec(dllimport) HRESULT WINAPI GameInputCreate(void **out);
__declspec(dllimport) void *WINAPI XMemVirtualAlloc(void *address, SIZE_T size, UINT kind, UINT64 flags, UINT protection);
__declspec(dllimport) BOOL WINAPI XMemAllocatePhysicalPages(UINT64 flags, SIZE_T *count, SIZE_T *pages);
__declspec(dllimport) BOOL WINAPI XMemFreePhysicalPages(SIZE_T count, const SIZE_T *pages);
__declspec(dllimport) void *WINAPI XMemMapPhysicalPages(void *address, SIZE_T count, const SIZE_T *pages);
__declspec(dllimport) BOOL WINAPI XMemMapPhysicalPagesScatter(void **addresses, SIZE_T count, const SIZE_T *pages);

#define PAGE_64K ((SIZE_T)65536)
#define KEYBOARD_KIND 0x10u
#define GAMEPAD_KIND 0x40000u
#define CONNECTED 3u
#define SLOT(obj, index) (*(void ***)(obj))[index]

typedef void (WINAPI *DeviceCallback)(UINT64, void *, void *, UINT64, UINT, UINT);
typedef HRESULT (WINAPI *RegisterDevice)(void *, void *, UINT, UINT, UINT, void *, DeviceCallback, UINT64 *);
typedef BYTE (WINAPI *Unregister)(void *, UINT64, UINT64);
typedef HRESULT (WINAPI *Current)(void *, UINT, void *, void **);
typedef UINT (WINAPI *Status)(void *);
typedef const BYTE *(WINAPI *Info)(void *);
typedef UINT (WINAPI *Release)(void *);
typedef UINT64 (WINAPI *Timestamp)(void *);
typedef void (WINAPI *ReadingDevice)(void *, void **);
typedef struct PadState { UINT buttons; UINT axis_bits[6]; } PadState;
typedef BYTE (WINAPI *GetPad)(void *, PadState *);

static UINT keyboard_events;
static void *keyboard_device;

static void WINAPI OnDevice(UINT64 token, void *context, void *device, UINT64 timestamp, UINT current, UINT previous)
{
    (void)token; (void)context; (void)timestamp;
    if (current == CONNECTED && previous == 0)
    {
        keyboard_device = device;
        keyboard_events++;
    }
}

static UINT TestInput(void)
{
    void *input = 0;
    void *reading = 0;
    void *device = 0;
    UINT64 token = 0;
    const BYTE *info;
    PadState pad;

    if (FAILED(GameInputCreate(&input)) || input == 0)

    {

        return 10;

    }
    if (FAILED(((RegisterDevice)SLOT(input, 9))(input, 0, KEYBOARD_KIND, CONNECTED, 2, 0, OnDevice, &token)))
    {
        return 11;
    }
    if (keyboard_events != 1 || keyboard_device == 0 || token == 0)
    {
        return 12;
    }
    if (((Status)SLOT(keyboard_device, 4))(keyboard_device) != CONNECTED)
    {
        return 13;
    }
    info = ((Info)SLOT(keyboard_device, 3))(keyboard_device);
    if (!info || *(const UINT *)info != 320 || info[32] != 'v' || *(const UINT *)(info + 104) != KEYBOARD_KIND)
    {
        return 14;
    }
    if (FAILED(((Current)SLOT(input, 4))(input, KEYBOARD_KIND, keyboard_device, &reading)) || !reading)
    {
        return 15;
    }
    if (((Status)SLOT(reading, 3))(reading) != KEYBOARD_KIND)
    {
        return 16;
    }
    ((ReadingDevice)SLOT(reading, 6))(reading, &device);
    if (device != keyboard_device)
    {
        return 17;
    }
    if (((Timestamp)SLOT(reading, 5))(reading) > ((Timestamp)SLOT(input, 3))(input))
    {
        return 18;
    }
    if (((Release)SLOT(reading, 2))(reading) != 0)
    {
        return 19;
    }
    if (!((Unregister)SLOT(input, 13))(input, token, 1000000))
    {
        return 20;
    }
    reading = 0;
    if (FAILED(((Current)SLOT(input, 4))(input, GAMEPAD_KIND, 0, &reading)) || !reading)
    {
        return 21;
    }
    if (!((GetPad)SLOT(reading, 22))(reading, &pad))
    {
        return 22;
    }
    if (((Release)SLOT(reading, 2))(reading) != 0)
    {
        return 23;
    }
    return 0;
}

static UINT TestPages(void)
{
    SIZE_T count = 2;
    SIZE_T pages[2];
    SIZE_T scatter[2];
    void *addresses[2];
    volatile BYTE *first;
    volatile BYTE *second;
    const UINT flags = MEM_RESERVE | 0x20400000u;

    if (!XMemAllocatePhysicalPages(3, &count, pages) || count != 2)

    {

        return 30;

    }
    if ((pages[0] & 15) || pages[1] != pages[0] + 16)
    {
        return 31;
    }
    first = (volatile BYTE *)XMemVirtualAlloc(0, PAGE_64K * 2, flags, 3, PAGE_READWRITE);
    second = (volatile BYTE *)XMemVirtualAlloc(0, PAGE_64K * 2, flags, 3, PAGE_READWRITE);
    if (!first || !second || first == second)
    {
        return 32;
    }
    if (!XMemMapPhysicalPages((void *)first, 2, pages))
    {
        return 33;
    }
    if (!XMemMapPhysicalPages((void *)second, 2, pages))
    {
        return 34;
    }
    first[17] = 0xA5;
    first[PAGE_64K + 19] = 0x5A;
    if (second[17] != 0xA5 || second[PAGE_64K + 19] != 0x5A)
    {
        return 35;
    }
    second[18] = 0xC3;
    if (first[18] != 0xC3)
    {
        return 36;
    }
    addresses[0] = (void *)second;
    addresses[1] = (void *)(second + PAGE_64K);
    scatter[0] = pages[1];
    scatter[1] = pages[0];
    if (!XMemMapPhysicalPagesScatter(addresses, 2, scatter))
    {
        return 37;
    }
    if (second[19] != 0x5A || second[PAGE_64K + 17] != 0xA5)
    {
        return 38;
    }
    if (!XMemFreePhysicalPages(2, pages))
    {
        return 39;
    }
    // Aliased views remain alive even though their physical IDs were freed.
    if (first[17] != 0xA5 || second[PAGE_64K + 17] != 0xA5)
    {
        return 40;
    }
    if (!XMemMapPhysicalPages((void *)first, 2, 0) || !XMemMapPhysicalPages((void *)second, 2, 0))
    {
        return 41;
    }
    return 0;
}

DWORD WINAPI ProbeEntry(void *unused)
{
    UINT result;
    (void)unused;
    result = TestInput();
    if (result != 0) return result;
    result = TestPages();
    if (result != 0) return result;
    return 42;
}
