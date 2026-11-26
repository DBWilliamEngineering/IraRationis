/* SPDX-License-Identifier: GPL-3.0-or-later */
/* Original synthetic PE; the sample's entry point explicitly requests host media. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

__declspec(dllimport) int WINAPI IrxPlaySampleMedia(void);

DWORD WINAPI ProbeEntry(LPVOID parameter)
{
    (void)parameter;
    return IrxPlaySampleMedia() == 0 ? 42 : 51;
}
