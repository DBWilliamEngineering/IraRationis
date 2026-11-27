/*
* @file        PresentationProbeFrame.cs
* @description Owns the back buffer, upload and allocator for one presentation probe slot.
* @author      Damon William
* @date        2026-11-27
*
* See the repository license and attribution notices.
*/

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Win32 = global::TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_HEAP_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_BARRIER_TYPE;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_DIMENSION;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.D3D12_TEXTURE_COPY_TYPE;
using static TerraFX.Interop.DirectX.D3D12_TEXTURE_LAYOUT;

namespace IraRationis.Graphics.Presentation;

internal sealed unsafe class PresentationProbeFrame : IDisposable
{
    private const uint HorizontalGradientStepPerFrame = 4;
    private const uint VerticalGradientStepPerFrame = 2;
    private const uint CheckerTileExtentPixels = 64;
    private const uint FramesPerCheckerPhase = 8;
    private const uint BrightCheckerBlue = 220;
    private const uint DarkCheckerBlue = 30;
    private const int GreenChannelShift = 8;
    private const int BlueChannelShift = 16;
    private const uint OpaqueRgba8Alpha = 0xFF000000;

    private ComPtr<ID3D12Resource> _target;
    private ComPtr<ID3D12Resource> _upload;
    private ComPtr<ID3D12CommandAllocator> _allocator;
    private D3D12_PLACED_SUBRESOURCE_FOOTPRINT _footprint;
    private byte* _mappedUpload;

    internal ID3D12CommandAllocator* Allocator
    {
        get
        {
            return _allocator.Get();
        }
    }

    internal ulong FenceValue { get; set; }

    internal int Create(ID3D12Device* device, IDXGISwapChain3* swapChain, uint index, D3D12_HEAP_TYPE heapType)
    {
        int result;
        fixed (ID3D12Resource** target = _target)
        {
            result = swapChain->GetBuffer(index, Win32.__uuidof<ID3D12Resource>(), (void**)target);
        }

        if (result < 0)
        {
            return result;
        }

        var description = _target.Get()->GetDesc();
        var footprint = default(D3D12_PLACED_SUBRESOURCE_FOOTPRINT);
        ulong uploadBytes = 0;
        device->GetCopyableFootprints(&description, 0, 1, 0, &footprint, null, null, &uploadBytes);
        _footprint = footprint;

        var heap = new D3D12_HEAP_PROPERTIES { Type = heapType, CreationNodeMask = 1, VisibleNodeMask = 1 };
        var uploadDescription = new D3D12_RESOURCE_DESC
        {
            Dimension = D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = uploadBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR
        };

        fixed (ID3D12Resource** upload = _upload)
        {
            result = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &uploadDescription,
                D3D12_RESOURCE_STATE_GENERIC_READ, null, Win32.__uuidof<ID3D12Resource>(), (void**)upload);
        }

        if (result < 0)
        {
            return result;
        }

        var noRead = default(D3D12_RANGE);
        void* mapped = null;
        result = _upload.Get()->Map(0, &noRead, &mapped);

        if (result < 0)
        {
            return result;
        }

        _mappedUpload = (byte*)mapped;

        fixed (ID3D12CommandAllocator** allocator = _allocator)
        {
            return device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                Win32.__uuidof<ID3D12CommandAllocator>(), (void**)allocator);
        }
    }

    public void Dispose()
    {
        if (_mappedUpload != null)
        {
            _upload.Get()->Unmap(0, null);
            _mappedUpload = null;
        }

        _allocator.Dispose();
        _upload.Dispose();
        _target.Dispose();
    }

    internal void Fill(int width, int height, uint frameNumber)
    {
        // O(width * height) with sequential, write-only access to the mapped buffer.
        for (var rowIndex = 0; rowIndex < height; rowIndex++)
        {
            var row = (uint*)(_mappedUpload + (nuint)rowIndex * _footprint.Footprint.RowPitch);
            var green = ((uint)rowIndex + frameNumber * VerticalGradientStepPerFrame) & byte.MaxValue;
            var tileRow = (uint)rowIndex / CheckerTileExtentPixels + frameNumber / FramesPerCheckerPhase;

            for (var column = 0; column < width; column++)
            {
                var red = ((uint)column + frameNumber * HorizontalGradientStepPerFrame) & byte.MaxValue;
                var tileColumn = (uint)column / CheckerTileExtentPixels;
                var blue = ((tileColumn + tileRow) & 1) != 0 ? BrightCheckerBlue : DarkCheckerBlue;
                row[column] = OpaqueRgba8Alpha | (blue << BlueChannelShift) | (green << GreenChannelShift) | red;
            }
        }
    }

    internal int RecordCopy(ID3D12GraphicsCommandList* commands)
    {
        int result = _allocator.Get()->Reset();

        if (result < 0)
        {
            return result;
        }

        result = commands->Reset(_allocator.Get(), null);

        if (result < 0)
        {
            return result;
        }

        var barrier = new D3D12_RESOURCE_BARRIER
        {
            Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
            Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
            {
                pResource = _target.Get(),
                Subresource = uint.MaxValue,
                StateBefore = D3D12_RESOURCE_STATE_PRESENT,
                StateAfter = D3D12_RESOURCE_STATE_COPY_DEST
            }
        };

        var source = new D3D12_TEXTURE_COPY_LOCATION
        {
            pResource = _upload.Get(),
            Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            PlacedFootprint = _footprint
        };
        var destination = new D3D12_TEXTURE_COPY_LOCATION
        {
            pResource = _target.Get(),
            Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX
        };

        commands->ResourceBarrier(1, &barrier);
        commands->CopyTextureRegion(&destination, 0, 0, 0, &source, null);

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        commands->ResourceBarrier(1, &barrier);

        return commands->Close();
    }
}
