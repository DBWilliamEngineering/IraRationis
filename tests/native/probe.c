/* SPDX-License-Identifier: GPL-3.0-or-later */
/* An ordinary synthetic Windows PE, linked without the CRT. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static DWORD WINAPI Worker(LPVOID parameter)
{
    return parameter == NULL ? 37 : 99;
}

DWORD WINAPI ProbeEntry(LPVOID parameter)
{
    WCHAR path[MAX_PATH];
    DWORD workerCode = 0;
    HANDLE worker;

    (void)parameter;
    if (GetModuleHandleW(NULL) == NULL || GetModuleFileNameW(NULL, path, MAX_PATH) == 0)
    {
        return 90;
    }

    worker = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    if (worker == NULL)
    {
        return 91;
    }

    if (WaitForSingleObject(worker, 10000) != WAIT_OBJECT_0 || !GetExitCodeThread(worker, &workerCode))
    {
        CloseHandle(worker);
        return 92;
    }

    CloseHandle(worker);
    return workerCode == 37 ? 42 : 93;
}
