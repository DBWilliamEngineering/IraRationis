// SPDX-License-Identifier: GPL-3.0-or-later
// Synthetic ABI test, independent of any commercial title.
#include <windows.h>
#include <stdint.h>
#include <stddef.h>

__declspec(dllimport) HRESULT WINAPI DStorageGetFactory(const GUID *, void **);
__declspec(dllimport) void *WINAPI XMemVirtualAlloc(void *, SIZE_T, UINT, UINT64, UINT);
__declspec(dllimport) BOOL WINAPI XMemAllocatePhysicalPages(UINT64, SIZE_T *, SIZE_T *);
__declspec(dllimport) BOOL WINAPI XMemFreePhysicalPages(SIZE_T, const SIZE_T *);
__declspec(dllimport) void *WINAPI XMemMapPhysicalPages(void *, SIZE_T, const SIZE_T *);

#define SLOT(obj, i) (*(void ***)(obj))[i]
#define MEMORY_SOURCE (1ULL << 22)
#define PAGE_DESTINATION (1ULL << 21)
#define PENDING ((HRESULT)0x8000000A)

typedef struct QueueDesc
{
    UINT64 source;
    USHORT capacity;
    CHAR priority;
    const char *name;
    UINT64 options;
} QueueDesc;

typedef struct Request
{
    UINT64 options;
    void *destination;
    UINT64 page_offset;
    UINT destination_size;
    const void *source;
    UINT64 offset;
    UINT source_size;
    UINT intermediate_size;
    UINT64 tag;
    const char *name;
} Request;

C_ASSERT(sizeof(Request) == 0x48);
C_ASSERT(offsetof(Request, source) == 0x20);
C_ASSERT(sizeof(QueueDesc) == 0x20);

typedef HRESULT (WINAPI *PFN_DStorageOpenFile)(void *, const WCHAR *, const GUID *, void **);
typedef HRESULT (WINAPI *CreateStatus)(void *, UINT, const char *, const GUID *, void **);
typedef HRESULT (WINAPI *CreateQueue)(void *, const QueueDesc *, const GUID *, void **);
typedef HRESULT (WINAPI *SetStaging)(void *, UINT);
typedef void (WINAPI *Enqueue)(void *, const Request *);
typedef void (WINAPI *EnqueueStatus)(void *, void *, UINT);
typedef void (WINAPI *Action)(void *);
typedef BYTE (WINAPI *Complete)(void *, UINT);
typedef HRESULT (WINAPI *Result)(void *, UINT);
typedef UINT (WINAPI *Release)(void *);

static const GUID factory_iid = {0x62959d11,0xd45f,0x4884,{0xa0,0xa3,0xf7,0x86,0x2e,0xa2,0x43,0xae}};
static const QueueDesc description = {0, 32, 0, "synthetic", 1};
static const BYTE plain[] = {1,2,3,4,5,6,7,8};
static BYTE raw[8], inflated[8], file_bytes[8];
static Request request;
static SIZE_T page_count = 1, pages[1];
static SIZE_T deferred_pages[1];
static const BYTE compressed[] = {120,156,99,100,98,102,97,101,99,231,0,0,0,128,0,37};

static void Read(void *queue, UINT64 options, void *destination, const void *source, UINT bytes, UINT64 offset)
{
    request.options = options;
    request.destination = destination;
    request.page_offset = 7;
    request.destination_size = 8;
    request.source = source;
    request.offset = offset;
    request.source_size = bytes;
    ((Enqueue)SLOT(queue, 3))(queue, &request);
}

static BOOL Matches(const BYTE *actual)
{
    UINT index;
    for (index = 0; index < sizeof(plain); ++index)
    {
        if (actual[index] != plain[index])
        {
            return FALSE;
        }
    }
    return TRUE;
}

UINT WINAPI ProbeEntry(void *reserved)
{
    void *factory = 0, *queue = 0, *status = 0, *file = 0;
    BYTE *view;
    UINT index;
    (void)reserved;
    if (FAILED(DStorageGetFactory(&factory_iid, &factory)) || !factory)
    {
        return 10;
    }
    if (FAILED(((SetStaging)SLOT(factory, 8))(factory, 32U << 20)))
    {
        return 11;
    }
    if (FAILED(((CreateQueue)SLOT(factory, 10))(factory, &description, 0, &queue)) || !queue)
    {
        return 12;
    }
    if (FAILED(((CreateStatus)SLOT(factory, 5))(factory, 2, "status", 0, &status)) || !status)
    {
        return 13;
    }
    if (FAILED(((PFN_DStorageOpenFile)SLOT(factory, 4))(factory, L"G:\\payload.bin", 0, &file)) || !file)
    {
        return 14;
    }
    if (!XMemAllocatePhysicalPages(3, &page_count, pages))
    {
        return 15;
    }
    view = (BYTE *)XMemVirtualAlloc(0, 65536, 0x20402000, 3, PAGE_READWRITE);
    if (!view || XMemMapPhysicalPages(view, 1, pages) != view)
    {
        return 16;
    }
    Read(queue, MEMORY_SOURCE, raw, plain, (UINT)sizeof(plain), 0);
    Read(queue, MEMORY_SOURCE | 1, inflated, compressed, (UINT)sizeof(compressed), 0);
    Read(queue, 0, file_bytes, file, (UINT)sizeof(plain), 0);
    Read(queue, MEMORY_SOURCE | PAGE_DESTINATION, pages, plain, (UINT)sizeof(plain), 0);
    Read(queue, MEMORY_SOURCE | PAGE_DESTINATION | 1, deferred_pages, compressed, (UINT)sizeof(compressed), 0);
    ((EnqueueStatus)SLOT(queue, 4))(queue, status, 0);
    if (((Result)SLOT(status, 4))(status, 0) != PENDING)
    {
        return 17;
    }
    /* Unknown transform must report failure after the preceding status. */
    Read(queue, MEMORY_SOURCE | (1ULL << 63), raw, plain, (UINT)sizeof(plain), 0);
    ((EnqueueStatus)SLOT(queue, 4))(queue, status, 1);
    deferred_pages[0] = pages[0];
    ((Action)SLOT(queue, 6))(queue);
    for (index = 0; index < 10000 && !((Complete)SLOT(status, 3))(status, 1); ++index)
    {
        Sleep(1);
    }
    if (!((Complete)SLOT(status, 3))(status, 1) || ((Result)SLOT(status, 4))(status, 0) != S_OK)
    {
        return 18;
    }
    if (((Result)SLOT(status, 4))(status, 1) != E_NOTIMPL)
    {
        return 19;
    }
    if (!Matches(raw) || !Matches(inflated) || !Matches(file_bytes) || !Matches(view + 7))
    {
        return 20;
    }
    ((Action)SLOT(queue, 8))(queue);
    ((Release)SLOT(queue, 2))(queue);
    ((Release)SLOT(status, 2))(status);
    ((Release)SLOT(file, 2))(file);
    if (!XMemMapPhysicalPages(view, 1, 0) || !XMemFreePhysicalPages(1, pages))
    {
        return 21;
    }
    return 42;
}
