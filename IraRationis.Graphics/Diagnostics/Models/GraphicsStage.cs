// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

internal enum GraphicsStage
{
    PresentX,
    SwapChain,
    WindowSetup,
    SegmentedSubmit,
    SealBoundary,
    PublishWrite,
    GuestWait,
    RecordVertexFetch,
    ExecuteVertexFetch,
    RecordRectangle,
    ExecuteRectangle,
    RecordTexture,
    ExecuteTexture,
    RecordCompute,
    ExecuteCompute,
    UploadTexture,
    UtilitySubmit,
    PresentGap,
    FrameEvent,
    FrameEventLock,
    FrameSleep,
    FrameSchedule,
    FrameStats,
    ClockCalibration,
    MemoryPublication,
    FencePublication,
    GuardedPublication,
    KernelPublication,
    AllocateRectangleUpload,
    DetileTexture,
    SegmentFenceWait,
    UploadFenceWait,
    EmptyPreparationFenceWait,
    StateOnlyMemoryFenceWait,
    Count
}
