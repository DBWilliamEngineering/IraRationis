// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace IraRationis.Shaders.Raster.NativeInstructions;

// Encoding fields follow AMD's RDNA2 ISA. This reader accepts only the named contract at its current offset.
internal ref struct RasterInstructionReader
{
    private readonly ReadOnlySpan<byte> _code;
    private int _offset;

    internal RasterInstructionReader(ReadOnlySpan<byte> code)
    {
        if (code.IsEmpty || (code.Length & 3) != 0 || code.Length > 4096)
        {
            throw new InvalidDataException("Invalid raster native instruction extent.");
        }

        _code = code;
        _offset = 0;
    }

    internal static uint Vector(uint register)
    {
        return 256 + register;
    }

    internal void IdentityHeader()
    {
        for (var headerIndex = 0; headerIndex < 2; headerIndex++)
        {
            var instruction = Next();
            var operands = Next();

            if ((instruction & 0xffff0000) != 0xd5800000 || ((operands >> 18) & 0x1ff) != 255 ||
                (operands & 0xf8000000) != 0)
            {
                throw new NotSupportedException("Unsupported raster native identity header.");
            }

            _ = Next();
        }
    }

    internal void SwapProgramCounter(uint destination, uint source)
    {
        ScalarUnary(33, destination, source);
    }

    internal void Move32(uint destination, uint source, uint? literal = null)
    {
        ScalarUnary(3, destination, source, literal);
    }

    internal void Move64(uint destination, uint source)
    {
        ScalarUnary(4, destination, source);
    }

    internal void WholeQuadMask(uint destination, uint source)
    {
        ScalarUnary(10, destination, source);
    }

    internal void And64(uint destination, uint left, uint right)
    {
        Require(0x80000000 | (15U << 23) | (destination << 16) | (right << 8) | left, "S_AND_B64");
    }

    internal void MoveSmallInteger(uint destination, ushort value)
    {
        Require(0xb0000000 | (destination << 16) | value, "S_MOVK_I32");
    }

    internal void CompareEqual(uint left, uint right)
    {
        Require(0xbf000000 | (6U << 16) | (right << 8) | left, "S_CMP_EQ_U32");
    }

    internal void Prefetch(ushort distance)
    {
        ScalarControl(32, distance);
    }

    internal void Wait(ushort counters)
    {
        ScalarControl(12, counters);
    }

    internal void BranchIfEqual(ushort displacement)
    {
        ScalarControl(5, displacement);
    }

    internal void ScalarNoOperation()
    {
        ScalarControl(0, 0);
    }

    internal void End()
    {
        ScalarControl(1, 0);
    }

    internal void LoadDescriptor(uint destination, uint pointerBase, uint dwords, uint byteOffset)
    {
        var opcode = dwords switch
        {
            4 => 2U,
            8 => 3U,
            _ => throw new InvalidOperationException("Unsupported descriptor extent in raster contract.")
        };

        ScalarMemory(opcode, destination, pointerBase, byteOffset);
    }

    internal void LoadBuffer(uint destination, uint descriptorBase, uint dwords, uint byteOffset)
    {
        var opcode = dwords switch
        {
            1 => 8U,
            4 => 10U,
            8 => 11U,
            16 => 12U,
            _ => throw new InvalidOperationException("Unsupported buffer extent in raster contract.")
        };

        ScalarMemory(opcode, destination, descriptorBase, byteOffset);
    }

    internal void FusedMultiplyAdd(uint destination, uint left, uint right, uint addend, uint negations = 0)
    {
        Require(0xd4000000 | (331U << 16) | destination, "V_FMA_F32");
        Require(left | (right << 9) | (addend << 18) | (negations << 29), "V_FMA_F32 operands");
    }

    internal void Accumulate(uint destination, uint left, uint rightVector)
    {
        VectorBinary(43, destination, left, rightVector);
    }

    internal void AccumulateNegatedProduct(uint destination, uint left, uint right)
    {
        Require(0xd4000000 | (299U << 16) | destination, "V_FMAC_F32");
        Require(left | (right << 9) | (1U << 29), "V_FMAC_F32 operands");
    }

    internal void MultiplyLegacy(uint destination, uint left, uint rightVector)
    {
        VectorBinary(7, destination, left, rightVector);
    }

    internal void Add(uint destination, uint left, uint rightVector)
    {
        VectorBinary(3, destination, left, rightVector);
    }

    internal void Subtract(uint destination, uint left, uint rightVector)
    {
        VectorBinary(4, destination, left, rightVector);
    }

    internal void Maximum(uint destination, uint left, uint rightVector)
    {
        VectorBinary(16, destination, left, rightVector);
    }

    internal void PackHalfTowardZero(uint destination, uint left, uint rightVector)
    {
        VectorBinary(47, destination, left, rightVector);
    }

    internal void VectorNoOperation()
    {
        Require(0x7e000000, "V_NOP");
    }

    internal void MoveVector32(uint destination, uint source)
    {
        VectorUnary(1, destination, source, "V_MOV_B32");
    }

    internal void Exp2(uint destination, uint source)
    {
        VectorUnary(37, destination, source, "V_EXP_F32");
    }

    internal void Log2(uint destination, uint source)
    {
        VectorUnary(39, destination, source, "V_LOG_F32");
    }

    internal void Reciprocal(uint destination, uint source)
    {
        VectorUnary(42, destination, source, "V_RCP_F32");
    }

    internal void Interpolate(uint destination, uint attribute, uint component, bool secondPhase)
    {
        Interpolate(destination, attribute, component, secondPhase ? 1U : 0U);
    }

    internal void InterpolateProvokingVertex(uint destination, uint attribute, uint component)
    {
        Interpolate(destination, attribute, component, 2U);
    }

    private void Interpolate(uint destination, uint attribute, uint component, uint phase)
    {
        Require(0xc8000000 | (destination << 18) | (phase << 16) | (attribute << 10) |
                (component << 8) | phase, "V_INTERP_F32");
    }

    internal void SamplePlane(uint destination, uint resourceBase, uint samplerBase, uint firstCoordinate,
        uint secondCoordinate)
    {
        var separateAddress = secondCoordinate != firstCoordinate + 1;

        Require(0xf0000000 | (32U << 18) | (1U << 8) | (1U << 3) | (separateAddress ? 2U : 0U),
            "IMAGE_SAMPLE 2D red");
        Require(firstCoordinate | (destination << 8) | ((resourceBase / 4) << 16) |
                ((samplerBase / 4) << 21), "IMAGE_SAMPLE descriptors");

        if (separateAddress)
        {
            Require(secondCoordinate, "IMAGE_SAMPLE nonsequential address");
        }
    }

    internal void SampleLookup(uint destination, uint resourceBase, uint samplerBase, uint coordinateBase)
    {
        SampleLodZero(destination, resourceBase, samplerBase, coordinateBase, 2, 7);
    }

    internal void SampleLodZeroAlpha(uint destination, uint resourceBase, uint samplerBase, uint coordinateBase)
    {
        SampleLodZero(destination, resourceBase, samplerBase, coordinateBase, 1, 8);
    }

    private void SampleLodZero(uint destination, uint resourceBase, uint samplerBase, uint coordinateBase,
        uint dimension, uint channelMask)
    {
        Require(0xf0000000 | (39U << 18) | (channelMask << 8) | (dimension << 3), "IMAGE_SAMPLE_LZ");
        Require(coordinateBase | (destination << 8) | ((resourceBase / 4) << 16) |
                ((samplerBase / 4) << 21), "IMAGE_SAMPLE_LZ descriptors");
    }

    internal void Export(uint target, uint first, uint second, uint third, uint fourth,
        bool done = false, bool compressed = false, bool validMask = false)
    {
        Require(0xf8000000 | 15U | (target << 4) | (compressed ? 1U << 10 : 0) |
                (done ? 1U << 11 : 0) | (validMask ? 1U << 12 : 0), "EXP");
        Require(first | (second << 8) | (third << 16) | (fourth << 24), "EXP source registers");
    }

    internal void Padding()
    {
        for (; _offset < _code.Length;)
        {
            Require(0xbf9f0000, "S_CODE_END padding");
        }
    }

    private void ScalarUnary(uint operation, uint destination, uint source, uint? literal = null)
    {
        Require(0xbe800000 | (destination << 16) | (operation << 8) | source, "scalar unary operation");

        if (source == 255)
        {
            Require(literal ?? throw new InvalidOperationException("Missing scalar literal contract."),
                "scalar literal");
        }
    }

    private void ScalarControl(uint operation, ushort immediate)
    {
        Require(0xbf800000 | (operation << 16) | immediate, "scalar control operation");
    }

    private void ScalarMemory(uint operation, uint destination, uint sourceBase, uint byteOffset)
    {
        Require(0xf4000000 | (operation << 18) | (destination << 6) | (sourceBase / 2), "scalar memory operation");
        Require(0xfa000000 | byteOffset, "scalar memory byte offset / null scalar offset");
    }

    private void VectorBinary(uint operation, uint destination, uint left, uint rightVector)
    {
        Require((operation << 25) | (destination << 17) | (rightVector << 9) | left, "vector binary operation");
    }

    private void VectorUnary(uint operation, uint destination, uint source, string operationName)
    {
        Require(0x7e000000 | (destination << 17) | (operation << 9) | source, operationName);
    }

    private void Require(uint expected, string operation)
    {
        var offset = _offset;
        var actual = Next();

        if (actual != expected)
        {
            throw new NotSupportedException($"Raster {operation} differs at native offset 0x{offset:X}: " +
                                            $"expected 0x{expected:X8}, received 0x{actual:X8}.");
        }
    }

    private uint Next()
    {
        if (_offset > _code.Length - 4)
        {
            throw new InvalidDataException("Truncated raster native instruction.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(_code[_offset..]);

        _offset += 4;

        return value;
    }

    internal static void VerifyAuthoredContracts()
    {
        var arithmetic = Convert.FromHexString("07004BD501040E00");
        var reader = new RasterInstructionReader(arithmetic);

        reader.FusedMultiplyAdd(7, 1, Vector(2), 3);

        arithmetic[0] = 6;

        try
        {
            reader = new RasterInstructionReader(arithmetic);
            reader.FusedMultiplyAdd(7, 1, Vector(2), 3);

            throw new InvalidOperationException("Planar native register mutation was accepted.");
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            reader = new RasterInstructionReader(arithmetic.AsSpan(0, 4));
            reader.FusedMultiplyAdd(6, 1, Vector(2), 3);

            throw new InvalidOperationException("Planar truncated operand was accepted.");
        }
        catch (InvalidDataException)
        {
        }

        var header = new byte[24];

        for (var offset = 0; offset < header.Length; offset += 12)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), 0xd5800000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 4), 255U << 18);
        }

        reader = new RasterInstructionReader(header);
        reader.IdentityHeader();

        header[2] ^= 1;

        try
        {
            reader = new RasterInstructionReader(header);
            reader.IdentityHeader();

            throw new InvalidOperationException("Planar non-NOP header was accepted.");
        }
        catch (NotSupportedException)
        {
        }
    }
}
