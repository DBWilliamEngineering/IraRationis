/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Original synthetic PE test. Reads a separately supplied, original tone packet. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stddef.h>

#define PACKET_COUNT 50U
#define PCM_FRAMES 960U
#define CHANNELS 2U
#define VOICE 1U
#define PCM_BYTES (PCM_FRAMES * CHANNELS * sizeof(UINT))
#define PACKET_CAPACITY 1276U
#define ONE_FLOAT_BITS 0x3F800000U

#pragma pack(push, 1)
typedef struct CommandId
{
    UINT kind;
    UINT stream;
    UINT sequence;
    UINT options;
    UINT64 context;
} CommandId;

typedef struct ActivateCommand
{
    CommandId id;
    UINT channels;
    UINT initLength;
    void *init;
    UINT startPitchBits;
    UINT processingLength;
    void *processing;
} ActivateCommand;

typedef struct ProcessCommand
{
    CommandId id;
    UINT pitchRampBits;
    UINT targetPitchBits;
    UINT firstFrame;
    UINT frameCount;
    UINT inputLength;
    void *input;
    UINT outputLength;
    void *output;
} ProcessCommand;

typedef struct Result
{
    CommandId id;
    UINT state;
    UINT64 stateData;
    UINT64 microseconds;
    HRESULT error;
    UINT frames;
    UINT outputLength;
    void *output;
    UINT pitchBits;
    UINT inputLength;
    void *input;
} Result;
#pragma pack(pop)

typedef struct ConnectInput
{
    UINT processing;
    UINT streams;
    UINT commandsPerStream;
    UINT memory;
    UINT framesPerOutput;
    UINT options;
} ConnectInput;

typedef struct ConnectOutput
{
    BYTE *memory;
    HANDLE event;
} ConnectOutput;

C_ASSERT(sizeof(CommandId) == 24);
C_ASSERT(sizeof(ActivateCommand) == 56);
C_ASSERT(sizeof(ProcessCommand) == 64);
C_ASSERT(sizeof(Result) == 80);
C_ASSERT(offsetof(Result, error) == 44);
C_ASSERT(offsetof(ProcessCommand, input) == 44);
C_ASSERT(sizeof(ConnectInput) == 24);
C_ASSERT(sizeof(ConnectOutput) == 16);

__declspec(dllimport) HRESULT WINAPI XApuConnect(const ConnectInput *, ConnectOutput *, void **);
__declspec(dllimport) HRESULT WINAPI XApuDisconnect(void *);
__declspec(dllimport) HRESULT WINAPI XApuEnqueueCommand(void *, const CommandId *, UINT *);
__declspec(dllimport) HRESULT WINAPI XApuDequeueResult(void *, Result *);

static const ConnectInput input = { 1, 2, 2, 65536, 0, 0 };
static ConnectOutput output;
static ActivateCommand activate;
static ProcessCommand process;
static CommandId deactivate;
static Result result;

static BOOL Submit(void *connection, const CommandId *command)
{
    if (XApuEnqueueCommand(connection, command, NULL) != S_OK ||
        XApuDequeueResult(connection, &result) != S_OK)
    {
        return FALSE;
    }

    return result.error == S_OK && result.id.kind == command->kind &&
        result.id.stream == command->stream && result.id.sequence == command->sequence;
}

static BOOL LoadPacket(BYTE *destination, DWORD *length)
{
    LARGE_INTEGER size;
    DWORD read = 0;
    BOOL success = FALSE;
    HANDLE file = CreateFileW(L"G:\\tone.opuspacket", GENERIC_READ, FILE_SHARE_READ,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);

    if (file == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    if (GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= PACKET_CAPACITY &&
        ReadFile(file, destination, (DWORD)size.QuadPart, &read, NULL) && read == (DWORD)size.QuadPart)
    {
        *length = read;
        success = TRUE;
    }

    CloseHandle(file);

    return success;
}

static BOOL HasFiniteSound(const UINT *samples)
{
    UINT i;
    BOOL nonzero = FALSE;

    for (i = 0; i < PCM_FRAMES * CHANNELS; ++i)
    {
        UINT bits = samples[i] & 0x7FFFFFFFU;

        if (bits >= 0x7F800000U)
        {
            return FALSE;
        }

        if (bits != 0)
        {
            nonzero = TRUE;
        }
    }

    return nonzero;
}

DWORD WINAPI ProbeEntry(void *reserved)
{
    void *connection = NULL;
    DWORD packetLength = 0;
    DWORD code = 42;
    UINT i;
    (void)reserved;

    if (XApuConnect(&input, &output, &connection) != S_OK || connection == NULL || output.memory == NULL)
    {
        return 10;
    }

    if (!LoadPacket(output.memory, &packetLength))
    {
        code = 11;
        goto done;
    }

    activate.id.kind = 1;
    activate.id.stream = VOICE;
    activate.id.sequence = 1;
    activate.channels = CHANNELS;
    activate.startPitchBits = ONE_FLOAT_BITS;

    if (!Submit(connection, &activate.id))
    {
        code = 12;
        goto done;
    }

    process.id.kind = 3;
    process.id.stream = VOICE;
    process.targetPitchBits = ONE_FLOAT_BITS;
    process.frameCount = PCM_FRAMES;
    process.inputLength = packetLength;
    process.input = output.memory;
    process.outputLength = (UINT)PCM_BYTES;
    process.output = output.memory + 4096;

    for (i = 0; i < PACKET_COUNT; ++i)
    {
        process.id.sequence = i + 2;

        if (!Submit(connection, &process.id) || result.frames != PCM_FRAMES ||
            result.outputLength != PCM_BYTES || result.output != process.output)
        {
            code = 13;
            goto done;
        }

        if (!HasFiniteSound((const UINT *)process.output))
        {
            code = 14;
            goto done;
        }

        Sleep(20);
    }

    deactivate.kind = 2;
    deactivate.stream = VOICE;
    deactivate.sequence = PACKET_COUNT + 2;

    if (!Submit(connection, &deactivate))
    {
        code = 15;
    }

done:
    if (XApuDisconnect(connection) != S_OK && code == 42)
    {
        code = 16;
    }

    return code;
}
