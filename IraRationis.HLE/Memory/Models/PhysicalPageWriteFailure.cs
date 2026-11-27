/**
 * @file        PhysicalPageWriteFailure.cs
 * @description Identifies the cause of a failed guest physical-page write.
 * @author      Damon William
 * @date        2026-11-27
 *
 * See the repository license and attribution notices.
 */

namespace IraRationis.HLE.Memory.Models;

public enum PhysicalPageWriteFailure
{
    None,
    InvalidDestination,
    UnpublishedPage,
    UnknownPage,
    MappingFailed,
    CommitFailed,
    IncompleteWrite
}
