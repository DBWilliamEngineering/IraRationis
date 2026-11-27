/**
 * @file        StorageRequestFailure.cs
 * @description Identifies the stage that rejected a DirectStorage request.
 * @author      Damon William
 * @date        2026-11-27
 *
 * See the repository license and attribution notices.
 */

namespace IraRationis.HLE.Storage.Models;

internal enum StorageRequestFailure
{
    UnsupportedOptions,
    UnsupportedBcPackMode,
    InvalidRequest,
    InvalidLength,
    ReadFailed,
    InvalidZlibPayload,
    BcPackDecodeRejected,
    PhysicalPagePublication
}
