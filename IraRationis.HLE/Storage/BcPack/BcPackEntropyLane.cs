/*
* @file        BcPackEntropyLane.cs
* @description Tracks the record and symbol state of one BCPack entropy lane.
* @author      Damon William
* @date        2026-11-27
*
* @details     Each lane can hold one decoded symbol and one byte awaiting renormalization.
*
* See the repository license and attribution notices.
*/

namespace IraRationis.HLE.Storage.BcPack;

internal struct BcPackEntropyLane(int baseWidth)
{
    private const uint NormalizedState = 1u << 24;
    private const uint FinalSlotMask = 0xffff;
    private const int LongRecordFlag = 0x20;
    private const int FlushFlag = 0x10;
    private const int ModelMask = 0x0f;

    private uint _state;
    private int _pendingByte = -1;
    private int _recordBytes;
    private int _stateBytesRead;
    private int _headerOffset;
    private int _symbolCount;
    private int _symbolWidth;
    private int _model;
    private bool _longRecord;
    private bool _readingHeader = true;
    private bool _initialStateReady;
    private byte _symbol;

    internal bool IsComplete { get; private set; }
    internal bool HasSymbol { get; private set; }

    internal readonly bool NeedsByte
    {
        get
        {
            return !IsComplete &&
                   (_readingHeader ||
                    (_pendingByte < 0 && _recordBytes > 0 && (_state < NormalizedState || !HasSymbol)));
        }
    }

    internal bool AcceptByte(byte value)
    {
        if (!_readingHeader)
        {
            _pendingByte = value;
            _recordBytes--;
            return true;
        }

        switch (_headerOffset++)
        {
            case 0:
                _symbolWidth = baseWidth - (value >> 6);
                _model = value & ModelMask;
                _longRecord = (value & LongRecordFlag) != 0;
                return (value & FlushFlag) != 0 && _symbolWidth is >= 1 and <= 8;
            case 1:
                _recordBytes = value + (_longRecord ? 257 : 1);
                return true;
            case 2 when _longRecord:
                _recordBytes += value << 8;
                return true;
        }

        _state = (_state << 8) | value;
        _stateBytesRead++;
        _recordBytes--;
        if (_stateBytesRead == sizeof(uint) || _recordBytes == 0)
        {
            _readingHeader = false;
            _initialStateReady = true;
        }

        return true;
    }

    internal bool Advance(bool waitingForTurn, int expectedSymbols)
    {
        if (IsComplete || _readingHeader)
        {
            return true;
        }

        if (_initialStateReady)
        {
            _initialStateReady = false;
            return true;
        }

        if (_state < NormalizedState)
        {
            if (_pendingByte >= 0)
            {
                MergePendingByte();
                return true;
            }

            if (_recordBytes > 0)
            {
                return true;
            }

            if (!RestoreTrailingState())
            {
                return false;
            }
        }

        if (IsFinalSlot())
        {
            return FinishRecord(expectedSymbols);
        }

        if (HasSymbol || waitingForTurn)
        {
            return true;
        }

        if (_symbolCount == expectedSymbols)
        {
            return false;
        }

        _symbol = BcPackEntropyModel.Decode(ref _state, _model, _symbolWidth);
        HasSymbol = true;
        _symbolCount++;

        if (_state < NormalizedState && _pendingByte >= 0)
        {
            MergePendingByte();
        }

        if (_recordBytes == 0 && _pendingByte < 0 && !RestoreTrailingState())
        {
            return false;
        }

        return _state < NormalizedState || !IsFinalSlot() || FinishRecord(expectedSymbols);
    }

    internal byte TakeSymbol()
    {
        HasSymbol = false;
        return _symbol;
    }

    private readonly bool IsFinalSlot()
    {
        return (_state & FinalSlotMask) == 0;
    }

    private void MergePendingByte()
    {
        _state = (_state << 8) | (uint)_pendingByte;
        _pendingByte = -1;
    }

    private bool RestoreTrailingState()
    {
        if (_state == 0)
        {
            return false;
        }

        while (_state < NormalizedState)
        {
            _state <<= 8;
        }

        return true;
    }

    private bool FinishRecord(int expectedSymbols)
    {
        if (_state != NormalizedState || _recordBytes != 0 || _pendingByte >= 0)
        {
            return false;
        }

        IsComplete = _symbolCount == expectedSymbols;
        _readingHeader = !IsComplete;
        _state = 0;
        _stateBytesRead = 0;
        _headerOffset = 0;
        return true;
    }
}
