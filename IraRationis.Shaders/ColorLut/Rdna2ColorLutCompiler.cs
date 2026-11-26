// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;

namespace IraRationis.Shaders.ColorLut;

/// <summary>
///     Narrow experimental lowering of one captured, acyclic native compute program.
///     Descriptor operations are mapped to explicit host bindings; wave branches are
///     lowered only because the captured program's branch operands are group-uniform.
///     The HLSL is an instruction-derived translation, not an identity LUT fallback.
///     Floating-point rounding and exceptional-value behavior are not bit-exact.
/// </summary>
public static class Rdna2ColorLutCompiler
{
    public const string ContainerHash = "FF6532D17D7306F09264F8255B03CEEAB9809C3DDA5D58491814986BFCD43EE6";
    private const string ProgramHash = "973BDC7322EB2F76A1163D549F21466FB8A216CB5043C763D28B19FAF72772D8";

    // 1,925 decoded instructions (including wait/prefetch/end operations), 73
    // forward-only basic blocks and 30 group-uniform conditional branches.
    // The two initial VOP3 NOPs and trailing s_code_end padding are not executable work.
    private const string Source = """
                                  Texture2D<float4> InputTexture : register(t0);
                                  RWTexture3D<float4> OutputTexture : register(u0);
                                  cbuffer SmallConstants : register(b0) { uint4 Small[8]; };
                                  cbuffer LargeConstants : register(b1) { uint4 Large[169]; };
                                  float LegacyMul(float a, float b)
                                  {
                                      return a == 0.0f || b == 0.0f ? 0.0f : a * b;
                                  }
                                  float NonFusedMac(float a, float b, float c)
                                  {
                                      precise float product = a * b;
                                      precise float sum = product + c;
                                      return sum;
                                  }
                                  [numthreads(8, 8, 1)]
                                  void main(uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID)
                                  {
                                      uint width, height, depth;
                                      OutputTexture.GetDimensions(width, height, depth);
                                      if (group.x * 8u + lane.x >= width || group.y * 8u + lane.y >= height || group.z >= depth)
                                      {
                                          return;
                                      }
                                      uint s0 = 0u;
                                      uint s1 = 0u;
                                      uint s2 = 0u;
                                      uint s3 = 0u;
                                      uint s4 = 0u;
                                      uint s5 = 0u;
                                      uint s6 = group.x;
                                      uint s7 = group.y;
                                      uint s8 = group.z;
                                      uint s9 = 0u;
                                      uint s10 = 0u;
                                      uint s11 = 0u;
                                      uint s12 = 0u;
                                      uint s13 = 0u;
                                      uint s14 = 0u;
                                      uint s15 = 0u;
                                      uint s16 = 0u;
                                      uint s17 = 0u;
                                      uint s18 = 0u;
                                      uint s19 = 0u;
                                      uint s20 = 0u;
                                      uint s21 = 0u;
                                      uint s22 = 0u;
                                      uint s23 = 0u;
                                      uint s24 = 0u;
                                      uint s25 = 0u;
                                      uint s26 = 0u;
                                      uint s27 = 0u;
                                      uint s28 = 0u;
                                      uint s29 = 0u;
                                      uint s30 = 0u;
                                      uint s31 = 0u;
                                      uint s32 = 0u;
                                      uint s33 = 0u;
                                      uint s34 = 0u;
                                      uint s35 = 0u;
                                      uint s36 = 0u;
                                      uint s37 = 0u;
                                      uint s38 = 0u;
                                      uint s39 = 0u;
                                      uint s40 = 0u;
                                      uint s41 = 0u;
                                      uint s42 = 0u;
                                      uint s43 = 0u;
                                      uint s44 = 0u;
                                      uint s45 = 0u;
                                      uint s46 = 0u;
                                      uint s47 = 0u;
                                      uint s48 = 0u;
                                      uint s49 = 0u;
                                      uint s50 = 0u;
                                      uint s51 = 0u;
                                      uint s52 = 0u;
                                      uint s53 = 0u;
                                      uint s54 = 0u;
                                      uint s55 = 0u;
                                      uint s56 = 0u;
                                      uint s57 = 0u;
                                      uint s58 = 0u;
                                      uint s59 = 0u;
                                      uint s60 = 0u;
                                      uint s61 = 0u;
                                      uint s62 = 0u;
                                      uint s63 = 0u;
                                      uint s64 = 0u;
                                      uint s65 = 0u;
                                      uint s66 = 0u;
                                      uint s67 = 0u;
                                      uint s68 = 0u;
                                      uint s69 = 0u;
                                      uint s70 = 0u;
                                      uint s71 = 0u;
                                      uint s72 = 0u;
                                      uint s73 = 0u;
                                      uint s74 = 0u;
                                      uint s75 = 0u;
                                      uint s76 = 0u;
                                      uint s77 = 0u;
                                      uint s78 = 0u;
                                      uint v0 = lane.x;
                                      uint v1 = lane.y;
                                      uint v2 = 0u;
                                      uint v3 = 0u;
                                      uint v4 = 0u;
                                      uint v5 = 0u;
                                      uint v6 = 0u;
                                      uint v7 = 0u;
                                      uint v8 = 0u;
                                      uint v9 = 0u;
                                      uint v10 = 0u;
                                      uint v11 = 0u;
                                      uint v12 = 0u;
                                      uint v13 = 0u;
                                      uint v14 = 0u;
                                      uint v15 = 0u;
                                      uint v16 = 0u;
                                      uint v17 = 0u;
                                      uint v18 = 0u;
                                      uint v19 = 0u;
                                      uint v20 = 0u;
                                      uint v21 = 0u;
                                      uint v22 = 0u;
                                      uint v23 = 0u;
                                      uint v24 = 0u;
                                      uint v25 = 0u;
                                      uint v26 = 0u;
                                      uint v27 = 0u;
                                      uint vcc_lo = 0u;
                                      uint scc = 0u;
                                      uint pc = 0x0018u;
                                      if (pc == 0x0018u)
                                      {
                                          s2 = s5;
                                          s3 = ((uint)(0));
                                          v0 = ((s6 << (((uint)(3)) & 31u)) + v0);
                                          // Guest descriptor load resolved by host bindings.
                                          v1 = ((s7 << (((uint)(3)) & 31u)) + v1);
                                          v4 = asuint((float)s8);
                                          s1 = 0xbb4b7c74u;
                                          v2 = asuint((float)v0);
                                          v3 = asuint((float)v1);
                                          s0 = Large[114][0];
                                          // Guest descriptor load resolved by host bindings.
                                          v2 = asuint(mad(asfloat(s0), asfloat(v2), asfloat(0xbed6358du)));
                                          v3 = asuint(mad(asfloat(s0), asfloat(v3), asfloat(0xbed6358du)));
                                          v4 = asuint(mad(asfloat(s0), asfloat(v4), asfloat(0xbed6358du)));
                                          s0 = Small[0][0];
                                          // Guest descriptor load resolved by host bindings.
                                          v2 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v2)));
                                          v3 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v3)));
                                          v4 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v4)));
                                          v2 = asuint(exp2(asfloat(v2)));
                                          v3 = asuint(exp2(asfloat(v3)));
                                          v5 = asuint(exp2(asfloat(v4)));
                                          v4 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v2), asfloat(s1)));
                                          scc = (s0 == ((uint)(0)) ? 1u : 0u);
                                          v3 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v3), asfloat(s1)));
                                          v2 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v5), asfloat(s1)));
                                          pc = scc != 0u ? 0x2838u : 0x00f0u;
                                      }
                                      if (pc == 0x00f0u)
                                      {
                                          // Guest descriptor load resolved by host bindings.
                                          v7 = ((uint)(1));
                                          v5 = 0x10001u;
                                          s24 = Small[7][0];
                                          s25 = Small[7][1];
                                          s26 = Small[7][2];
                                          s27 = Small[7][3];
                                          s16 = Small[6][0];
                                          s17 = Small[6][1];
                                          s18 = Small[6][2];
                                          s19 = Small[6][3];
                                          float4 t118 = InputTexture.Load(int3(v5 & 65535u, v5 >> 16, 0));
                                          v5 = asuint(t118.x);
                                          v6 = asuint(t118.y);
                                          float4 t120 = InputTexture.Load(int3(v7 & 65535u, v7 >> 16, 0));
                                          v7 = asuint(t120.x);
                                          v8 = asuint(t120.y);
                                          s20 = Small[2][0];
                                          s21 = Small[2][1];
                                          s22 = Small[2][2];
                                          s23 = Small[2][3];
                                          s0 = Small[0][1];
                                          s1 = Small[0][2];
                                          s2 = Small[0][3];
                                          s3 = Small[1][0];
                                          s4 = Small[1][1];
                                          s5 = Small[1][2];
                                          s6 = Small[1][3];
                                          s7 = Small[2][0];
                                          v5 = asuint((asfloat(v5) - asfloat(v7)));
                                          v6 = asuint((asfloat(v6) - asfloat(v8)));
                                          v5 = asuint(mad(asfloat(s24), asfloat(v5), asfloat(v7)));
                                          v6 = asuint(mad(asfloat(s24), asfloat(v6), asfloat(v8)));
                                          v7 = s2;
                                          v5 = asuint(max(min(asfloat(v5), asfloat(s6)), min(max(asfloat(v5), asfloat(s6)), asfloat(s20))));
                                          v6 = asuint(mad(asfloat(s25), asfloat(v6), asfloat(s26)));
                                          s6 = (asfloat(0x45cb2000u) == asfloat(s2) ? 0xffffffffu : 0u);
                                          s7 = s6;
                                          v5 = asuint((asfloat(v5) - asfloat(s0)));
                                          v6 = asuint(max(min(asfloat(v6), -1.0f), min(max(asfloat(v6), -1.0f), 1.0f)));
                                          vcc_lo = s6;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v5 = asuint(mad(asfloat(s4), asfloat(v5), asfloat(s0)));
                                          v6 = asuint((asfloat(v6) - asfloat(s1)));
                                          v10 = asuint((asfloat(s21) - asfloat(v5)));
                                          pc = vcc_lo != 0u ? 0x01f8u : 0x01a0u;
                                      }
                                      if (pc == 0x01a0u)
                                      {
                                          v8 = asuint((1.0f / asfloat(s2)));
                                          v11 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s2)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v11) ? 0xffffffffu : 0u);
                                          v8 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v8)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v9 = asuint(LegacyMul(asfloat(v8), asfloat(v8)));
                                          v11 = asuint(LegacyMul(asfloat(v9), asfloat(v8)));
                                          pc = vcc_lo == 0u ? 0x020cu : 0x01d0u;
                                      }
                                      if (pc == 0x01d0u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v8 = asuint(mad(asfloat(v8), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v8 = asuint(mad(asfloat(0x49e82740u), asfloat(v9), asfloat(v8)));
                                          v8 = asuint(mad(asfloat(0xceef2e78u), asfloat(v11), asfloat(v8)));
                                          pc = 0x0230u;
                                      }
                                      if (pc == 0x01f8u)
                                      {
                                          v7 = 0x3ea01cd6u;
                                          v9 = 0x3ea8769fu;
                                          pc = 0x0310u;
                                      }
                                      if (pc == 0x020cu)
                                      {
                                          s0 = 0x42c63852u;
                                          v8 = asuint(mad(asfloat(v8), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v8 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v9), asfloat(v8)));
                                          v8 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v11), asfloat(v8)));
                                          pc = 0x0230u;
                                      }
                                      if (pc == 0x0230u)
                                      {
                                          s0 = 0x4037ae14u;
                                          v9 = asuint(LegacyMul(asfloat(v8), asfloat(v8)));
                                          v11 = asuint(mad(asfloat(v8), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s0 = 0x340a20a1u;
                                          v13 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s2), 1.0f));
                                          v14 = asuint(LegacyMul(asfloat(s2), asfloat(s2)));
                                          vcc_lo = (asfloat(0x457a0000u) <= asfloat(s2) ? 0xffffffffu : 0u);
                                          v11 = asuint(mad(asfloat(0xc0400000u), asfloat(v9), asfloat(v11)));
                                          v9 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x39219aceu)));
                                          s0 = 0x353e175du;
                                          v13 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v14), asfloat(v13)));
                                          v12 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x3a5cd5ddu)));
                                          s0 = 0x33349873u;
                                          v9 = asuint(mad(asfloat(s2), asfloat(v9), asfloat(0x3f5c30adu)));
                                          v7 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x3831567au)));
                                          v13 = asuint((1.0f / asfloat(v13)));
                                          v12 = asuint(mad(asfloat(v12), asfloat(s2), 1.0f));
                                          v7 = asuint(mad(asfloat(s2), asfloat(v7), asfloat(0x3ea28216u)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v7 = asuint(LegacyMul(asfloat(v7), asfloat(v13)));
                                          v9 = asuint(LegacyMul(asfloat(v9), asfloat(v12)));
                                          v12 = asuint(mad(asfloat(v9), 2.0f, 4.0f));
                                          v9 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v9)));
                                          v12 = asuint(mad(asfloat(0xc1000000u), asfloat(v7), asfloat(v12)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v9 = asuint(LegacyMul(asfloat(v9), asfloat(v12)));
                                          v12 = asuint((asfloat(v12) + asfloat(v12)));
                                          v12 = asuint(LegacyMul(asfloat(v7), asfloat(v12)));
                                          v7 = (vcc_lo != 0u ? v8 : v9);
                                          v9 = (vcc_lo != 0u ? v11 : v12);
                                          pc = 0x0310u;
                                      }
                                      if (pc == 0x0310u)
                                      {
                                          v8 = s23;
                                          v5 = asuint(mad(asfloat(s22), asfloat(v10), asfloat(v5)));
                                          v6 = asuint(mad(asfloat(s5), asfloat(v6), asfloat(s1)));
                                          scc = (s19 == ((uint)(0)) ? 1u : 0u);
                                          pc = scc != 0u ? 0x03ecu : 0x0328u;
                                      }
                                      if (pc == 0x0328u)
                                      {
                                          s0 = Small[6][1];
                                          s1 = Small[6][2];
                                          v7 = asuint(max(1.0f, asfloat(v5)));
                                          s2 = 0x49742400u;
                                          v7 = asuint((1.0f / asfloat(v7)));
                                          v7 = asuint(mad(asfloat(v7), asfloat(s2), asfloat(0xc319d89eu)));
                                          v9 = asuint((1.0f - asfloat(s0)));
                                          s0 = 0x42480000u;
                                          v7 = asuint(mad(asfloat(v7), asfloat(v9), asfloat(0x4319d89eu)));
                                          v9 = v7;
                                          v9 = asuint(mad(asfloat(0x43960000u), asfloat(s1), asfloat(v9)));
                                          v10 = asuint(max(min(asfloat(v9), asfloat(s0)), min(max(asfloat(v9), asfloat(s0)), asfloat(0x4426aaabu))));
                                          v9 = asuint(max(asfloat(0x358637bdu), asfloat(v10)));
                                          v10 = asuint(mad(asfloat(0x43960000u), asfloat(s1), -asfloat(v10)));
                                          v11 = asuint((1.0f / asfloat(v9)));
                                          v13 = asuint(LegacyMul(asfloat(0x358624a2u), asfloat(v9)));
                                          v12 = asuint(LegacyMul(asfloat(v13), asfloat(v13)));
                                          v14 = asuint(LegacyMul(asfloat(0x497446c6u), asfloat(v11)));
                                          v13 = asuint(LegacyMul(asfloat(v12), asfloat(v13)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v14) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo == 0u ? 0x0440u : 0x03c4u;
                                      }
                                      if (pc == 0x03c4u)
                                      {
                                          s0 = 0x3981adbcu;
                                          v23 = asuint(mad(asfloat(v9), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v23 = asuint(mad(asfloat(0x49e82740u), asfloat(v12), asfloat(v23)));
                                          v23 = asuint(mad(asfloat(0xceef2e78u), asfloat(v13), asfloat(v23)));
                                          pc = 0x0468u;
                                      }
                                      if (pc == 0x03ecu)
                                      {
                                          v10 = asuint((1.0f / asfloat(v5)));
                                          v12 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(v5)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v12) ? 0xffffffffu : 0u);
                                          v10 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v10)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v11 = asuint(LegacyMul(asfloat(v10), asfloat(v10)));
                                          v12 = asuint(LegacyMul(asfloat(v11), asfloat(v10)));
                                          pc = vcc_lo == 0u ? 0x0b80u : 0x0418u;
                                      }
                                      if (pc == 0x0418u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v10 = asuint(mad(asfloat(v10), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v10 = asuint(mad(asfloat(0x49e82740u), asfloat(v11), asfloat(v10)));
                                          v10 = asuint(mad(asfloat(0xceef2e78u), asfloat(v12), asfloat(v10)));
                                          pc = 0x0ba8u;
                                      }
                                      if (pc == 0x0440u)
                                      {
                                          s0 = 0x38cfbbb1u;
                                          v23 = asuint(mad(asfloat(v9), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v23 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v12), asfloat(v23)));
                                          v23 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v13), asfloat(v23)));
                                          pc = 0x0468u;
                                      }
                                      if (pc == 0x0468u)
                                      {
                                          v12 = asuint(LegacyMul(asfloat(0x49742400u), asfloat(v11)));
                                          s0 = 0x353e175du;
                                          v7 = asuint((asfloat(v10) + asfloat(v7)));
                                          v17 = asuint(LegacyMul(asfloat(v23), asfloat(v23)));
                                          v11 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x3a5cd5ddu)));
                                          v13 = asuint(mad(asfloat(0xb7f30d95u), asfloat(v12), 1.0f));
                                          v14 = asuint(LegacyMul(asfloat(v12), asfloat(v12)));
                                          s0 = 0x340a20a1u;
                                          v11 = asuint(mad(asfloat(v11), asfloat(v12), 1.0f));
                                          v15 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x39219aceu)));
                                          v13 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v14), asfloat(v13)));
                                          s0 = 0x33349873u;
                                          v11 = asuint((1.0f / asfloat(v11)));
                                          v14 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x3831567au)));
                                          v18 = asuint(mad(asfloat(v15), asfloat(v12), asfloat(0x3f5c30adu)));
                                          s0 = 0x4037ae14u;
                                          v13 = asuint((1.0f / asfloat(v13)));
                                          v14 = asuint(mad(asfloat(v14), asfloat(v12), asfloat(0x3ea28216u)));
                                          v10 = asuint(mad(asfloat(v23), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s0 = (asfloat(0x457a0000u) <= asfloat(v12) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v15 = asuint(LegacyMul(asfloat(v18), asfloat(v11)));
                                          v10 = asuint(mad(asfloat(0xc0400000u), asfloat(v17), asfloat(v10)));
                                          v14 = asuint(LegacyMul(asfloat(v14), asfloat(v13)));
                                          v11 = asuint(mad(asfloat(v15), 2.0f, 4.0f));
                                          v13 = asuint((1.0f / asfloat(v5)));
                                          v18 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v15)));
                                          v11 = asuint(mad(asfloat(0xc1000000u), asfloat(v14), asfloat(v11)));
                                          v16 = asuint((1.0f / asfloat(v11)));
                                          v11 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v13)));
                                          v13 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(v5)));
                                          v12 = asuint(LegacyMul(asfloat(v11), asfloat(v11)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v13) ? 0xffffffffu : 0u);
                                          v19 = asuint((asfloat(v16) + asfloat(v16)));
                                          v22 = asuint(LegacyMul(asfloat(v12), asfloat(v11)));
                                          v13 = asuint(LegacyMul(asfloat(v18), asfloat(v16)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v15 = asuint(LegacyMul(asfloat(v14), asfloat(v19)));
                                          pc = vcc_lo == 0u ? 0x0598u : 0x0570u;
                                      }
                                      if (pc == 0x0570u)
                                      {
                                          s2 = 0x43777ae1u;
                                          v11 = asuint(mad(asfloat(v11), asfloat(s2), asfloat(0x3e72ba9du)));
                                          v11 = asuint(mad(asfloat(0x49e82740u), asfloat(v12), asfloat(v11)));
                                          v11 = asuint(mad(asfloat(0xceef2e78u), asfloat(v22), asfloat(v11)));
                                          pc = 0x05c0u;
                                      }
                                      if (pc == 0x0598u)
                                      {
                                          s2 = 0x42c63852u;
                                          v11 = asuint(mad(asfloat(v11), asfloat(s2), asfloat(0x3e79eba7u)));
                                          v11 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v12), asfloat(v11)));
                                          v11 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v22), asfloat(v11)));
                                          pc = 0x05c0u;
                                      }
                                      if (pc == 0x05c0u)
                                      {
                                          v9 = (s0 != 0u ? v23 : v13);
                                          v10 = (s0 != 0u ? v10 : v15);
                                          s0 = 0x4037ae14u;
                                          v12 = asuint(LegacyMul(asfloat(v11), asfloat(v11)));
                                          v14 = asuint(mad(asfloat(v11), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s2 = 0x39219aceu;
                                          s3 = 0x3a5cd5ddu;
                                          v18 = asuint(mad(asfloat(0xb7f30d95u), asfloat(v5), 1.0f));
                                          v13 = asuint(LegacyMul(asfloat(v5), asfloat(v5)));
                                          v14 = asuint(mad(asfloat(0xc0400000u), asfloat(v12), asfloat(v14)));
                                          v12 = asuint(mad(asfloat(0x340a20a1u), asfloat(v5), asfloat(s2)));
                                          s4 = 0x3831567au;
                                          v21 = asuint(mad(asfloat(0x35be175du), asfloat(v5), asfloat(s3)));
                                          v19 = asuint(mad(asfloat(0x33349873u), asfloat(v5), asfloat(s4)));
                                          v18 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v13), asfloat(v18)));
                                          v15 = asuint(mad(asfloat(v12), asfloat(v5), asfloat(0x3f5c30adu)));
                                          v12 = asuint(mad(asfloat(0x353e175du), asfloat(v5), asfloat(s3)));
                                          s5 = 0xb7f30d95u;
                                          v17 = asuint(mad(asfloat(v19), asfloat(v5), asfloat(0x3ea28216u)));
                                          v22 = asuint(LegacyMul(asfloat(v18), asfloat(v18)));
                                          s0 = (asfloat(0x457a0000u) <= asfloat(v5) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v16 = asuint(mad(asfloat(v12), asfloat(v5), 1.0f));
                                          v23 = asuint(mad(asfloat(0x348a20a1u), asfloat(v5), asfloat(s2)));
                                          v20 = asuint(mad(asfloat(0x33b49873u), asfloat(v5), asfloat(s4)));
                                          v5 = asuint(mad(asfloat(0x34ad5cb4u), asfloat(v5), asfloat(s5)));
                                          v22 = asuint((1.0f / asfloat(v22)));
                                          v13 = asuint(LegacyMul(asfloat(v16), asfloat(v16)));
                                          v12 = asuint((1.0f / asfloat(v16)));
                                          v19 = asuint((1.0f / asfloat(v18)));
                                          v12 = asuint(LegacyMul(asfloat(v15), asfloat(v12)));
                                          v15 = asuint(LegacyMul(asfloat(v15), asfloat(v21)));
                                          v21 = asuint((1.0f / asfloat(v13)));
                                          v19 = asuint(LegacyMul(asfloat(v17), asfloat(v19)));
                                          v15 = asuint(mad(asfloat(v16), asfloat(v23), -asfloat(v15)));
                                          v23 = asuint(LegacyMul(asfloat(v17), asfloat(v5)));
                                          v13 = asuint(mad(asfloat(v12), 2.0f, 4.0f));
                                          v17 = asuint(LegacyMul(asfloat(0x3d4ccccdu), asfloat(v6)));
                                          v5 = asuint(mad(asfloat(v18), asfloat(v20), -asfloat(v23)));
                                          v15 = asuint(LegacyMul(asfloat(v15), asfloat(v21)));
                                          v13 = asuint(mad(asfloat(0xc1000000u), asfloat(v19), asfloat(v13)));
                                          v23 = asuint(LegacyMul(asfloat(v5), asfloat(v22)));
                                          v13 = asuint((1.0f / asfloat(v13)));
                                          v16 = asuint(LegacyMul(asfloat(v23), asfloat(v23)));
                                          v5 = asuint(LegacyMul(asfloat(v17), asfloat(v23)));
                                          v23 = ((uint)(0));
                                          v16 = asuint(mad(asfloat(v15), asfloat(v15), asfloat(v16)));
                                          v20 = asuint((asfloat(v13) + asfloat(v13)));
                                          v18 = asuint(rsqrt(asfloat(v16)));
                                          v20 = asuint(LegacyMul(asfloat(v19), asfloat(v20)));
                                          v14 = (s0 != 0u ? v14 : v20);
                                          v6 = asuint(mad(asfloat(v5), asfloat(v18), asfloat(v12)));
                                          v5 = asuint(LegacyMul(asfloat(v17), asfloat(v15)));
                                          v15 = ((uint)(0));
                                          v19 = asuint(mad(-asfloat(v5), asfloat(v18), asfloat(v19)));
                                          v5 = asuint(mad(asfloat(0xc1000000u), asfloat(v19), 4.0f));
                                          v5 = asuint(mad(2.0f, asfloat(v6), asfloat(v5)));
                                          v17 = asuint((1.0f / asfloat(v5)));
                                          v5 = asuint((asfloat(v17) + asfloat(v17)));
                                          v5 = asuint(mad(asfloat(v5), asfloat(v19), -asfloat(v20)));
                                          v19 = ((uint)(0));
                                          v16 = asuint((asfloat(v5) + asfloat(v14)));
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v16) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x07b0u : 0x0770u;
                                      }
                                      if (pc == 0x0770u)
                                      {
                                          v15 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v12)));
                                          v12 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v17)));
                                          v23 = asuint(1.0f);
                                          v5 = asuint(LegacyMul(asfloat(v15), asfloat(v13)));
                                          v11 = (s0 != 0u ? v11 : v5);
                                          v5 = asuint(mad(asfloat(v12), asfloat(v6), -asfloat(v5)));
                                          v6 = asuint((1.0f / asfloat(v16)));
                                          v5 = asuint((asfloat(v5) + asfloat(v11)));
                                          v11 = asuint((1.0f - asfloat(v5)));
                                          v15 = asuint(LegacyMul(asfloat(v5), asfloat(v6)));
                                          v11 = asuint((asfloat(v11) - asfloat(v16)));
                                          v19 = asuint(LegacyMul(asfloat(v11), asfloat(v6)));
                                          pc = 0x07b0u;
                                      }
                                      if (pc == 0x07b0u)
                                      {
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v10) ? 0xffffffffu : 0u);
                                          v17 = ((uint)(0));
                                          v5 = ((uint)(0));
                                          v12 = ((uint)(0));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x07e4u : 0x07ccu;
                                      }
                                      if (pc == 0x07ccu)
                                      {
                                          v11 = asuint((1.0f - asfloat(v9)));
                                          v6 = asuint((1.0f / asfloat(v10)));
                                          v5 = asuint(1.0f);
                                          v11 = asuint((asfloat(v11) - asfloat(v10)));
                                          v12 = asuint(LegacyMul(asfloat(v9), asfloat(v6)));
                                          v17 = asuint(LegacyMul(asfloat(v11), asfloat(v6)));
                                          pc = 0x07e4u;
                                      }
                                      if (pc == 0x07e4u)
                                      {
                                          v9 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v15)));
                                          v10 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v15)));
                                          v13 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v15)));
                                          v15 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v12)));
                                          v14 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v12)));
                                          v9 = asuint(mad(asfloat(0x3e886595u), asfloat(v23), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v23), asfloat(v10)));
                                          v13 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v23), asfloat(v13)));
                                          v15 = asuint(mad(asfloat(0x3e886595u), asfloat(v5), asfloat(v15)));
                                          v12 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v12)));
                                          v9 = asuint(mad(asfloat(0xbe25460bu), asfloat(v19), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v19), asfloat(v10)));
                                          v14 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v5), asfloat(v14)));
                                          v13 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v19), asfloat(v13)));
                                          v12 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v5), asfloat(v12)));
                                          v9 = asuint((1.0f / asfloat(v9)));
                                          v15 = asuint(mad(asfloat(0xbe25460bu), asfloat(v17), asfloat(v15)));
                                          v14 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v17), asfloat(v14)));
                                          v7 = asuint(saturate(LegacyMul(asfloat(0x3c4ccccdu), abs(asfloat(v7)))));
                                          v12 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v17), asfloat(v12)));
                                          v5 = asuint((1.0f / asfloat(v10)));
                                          v7 = asuint((1.0f + asfloat(v7)));
                                          v9 = asuint(LegacyMul(asfloat(v15), asfloat(v9)));
                                          v10 = asuint((1.0f / asfloat(v13)));
                                          v5 = asuint(LegacyMul(asfloat(v14), asfloat(v5)));
                                          v6 = asuint(log2(asfloat(v9)));
                                          v9 = asuint(LegacyMul(asfloat(v12), asfloat(v10)));
                                          v5 = asuint(log2(asfloat(v5)));
                                          v6 = asuint(LegacyMul(asfloat(v6), asfloat(v7)));
                                          v9 = asuint(log2(asfloat(v9)));
                                          v6 = asuint(exp2(asfloat(v6)));
                                          v5 = asuint(LegacyMul(asfloat(v5), asfloat(v7)));
                                          v7 = asuint(LegacyMul(asfloat(v9), asfloat(v7)));
                                          v5 = asuint(exp2(asfloat(v5)));
                                          v18 = asuint(LegacyMul(asfloat(0x3f622a43u), asfloat(v6)));
                                          v9 = asuint(exp2(asfloat(v7)));
                                          v10 = asuint(LegacyMul(asfloat(0x3e869f68u), asfloat(v6)));
                                          v12 = asuint(LegacyMul(asfloat(0x3ec61f29u), asfloat(v6)));
                                          v16 = asuint(LegacyMul(asfloat(0xbe231fb6u), asfloat(v6)));
                                          v23 = asuint(LegacyMul(asfloat(0x3debdc39u), asfloat(v6)));
                                          v18 = asuint(mad(asfloat(0x3de1ef85u), asfloat(v5), asfloat(v18)));
                                          v11 = asuint(LegacyMul(asfloat(0xbd8ee5b6u), asfloat(v6)));
                                          v15 = asuint(LegacyMul(asfloat(0xbbfa26f7u), asfloat(v6)));
                                          v22 = asuint(LegacyMul(asfloat(0xbb14e69au), asfloat(v6)));
                                          v6 = asuint(LegacyMul(asfloat(0x3ab46cc2u), asfloat(v6)));
                                          v10 = asuint(mad(asfloat(0xbe810333u), asfloat(v5), asfloat(v10)));
                                          v12 = asuint(mad(asfloat(0xbec71a7bu), asfloat(v5), asfloat(v12)));
                                          v18 = asuint(mad(asfloat(0x3bcbe686u), asfloat(v9), asfloat(v18)));
                                          v16 = asuint(mad(asfloat(0xbbb0d86bu), asfloat(v5), asfloat(v16)));
                                          v23 = asuint(mad(asfloat(0x3f6361c2u), asfloat(v5), asfloat(v23)));
                                          v11 = asuint(mad(asfloat(0x3c9bd7d9u), asfloat(v5), asfloat(v11)));
                                          v15 = asuint(mad(asfloat(0xbcf616abu), asfloat(v5), asfloat(v15)));
                                          v22 = asuint(mad(asfloat(0x3d8c8526u), asfloat(v5), asfloat(v22)));
                                          v6 = asuint(mad(asfloat(0x3ac09e9cu), asfloat(v5), asfloat(v6)));
                                          v12 = asuint(mad(asfloat(0x3afb5232u), asfloat(v9), asfloat(v12)));
                                          v10 = asuint(mad(asfloat(0xbc3386dau), asfloat(v9), asfloat(v10)));
                                          v5 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v18)));
                                          v15 = asuint(mad(asfloat(0x3d1a5031u), asfloat(v9), asfloat(v15)));
                                          v7 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v18)));
                                          v16 = asuint(mad(asfloat(0x3e28a67du), asfloat(v9), asfloat(v16)));
                                          v23 = asuint(mad(asfloat(0xbb5d4762u), asfloat(v9), asfloat(v23)));
                                          v17 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v12)));
                                          v5 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v10), asfloat(v5)));
                                          v21 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v12)));
                                          v7 = asuint(mad(asfloat(0x3f371437u), asfloat(v10), asfloat(v7)));
                                          v11 = asuint(mad(asfloat(0x3d4fdf77u), asfloat(v9), asfloat(v11)));
                                          v17 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v23), asfloat(v17)));
                                          v5 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v16), asfloat(v5)));
                                          v22 = asuint(mad(asfloat(0xbd87ddf8u), asfloat(v9), asfloat(v22)));
                                          v19 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v15)));
                                          v20 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v15)));
                                          v15 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v15)));
                                          v6 = asuint(mad(asfloat(0x3f7f457bu), asfloat(v9), asfloat(v6)));
                                          v9 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v18)));
                                          v18 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v12)));
                                          v21 = asuint(mad(asfloat(0x3f371437u), asfloat(v23), asfloat(v21)));
                                          v19 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v22), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0x3f371437u), asfloat(v22), asfloat(v20)));
                                          v9 = asuint(mad(asfloat(0x3d93d07du), asfloat(v10), asfloat(v9)));
                                          v15 = asuint(mad(asfloat(0x3d93d07du), asfloat(v22), asfloat(v15)));
                                          v18 = asuint(mad(asfloat(0x3d93d07du), asfloat(v23), asfloat(v18)));
                                          v7 = asuint(mad(asfloat(0x3df41aefu), asfloat(v16), asfloat(v7)));
                                          v17 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v11), asfloat(v17)));
                                          v14 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v5)));
                                          v22 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v5)));
                                          v10 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v5)));
                                          v9 = asuint(mad(asfloat(0x3f734721u), asfloat(v16), asfloat(v9)));
                                          v21 = asuint(mad(asfloat(0x3df41aefu), asfloat(v11), asfloat(v21)));
                                          v18 = asuint(mad(asfloat(0x3f734721u), asfloat(v11), asfloat(v18)));
                                          v11 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v7)));
                                          v19 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v6), asfloat(v19)));
                                          v14 = asuint(mad(asfloat(0x3ff02120u), asfloat(v17), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v17), asfloat(v22)));
                                          v20 = asuint(mad(asfloat(0x3df41aefu), asfloat(v6), asfloat(v20)));
                                          v15 = asuint(mad(asfloat(0x3f734721u), asfloat(v6), asfloat(v15)));
                                          v6 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v7)));
                                          v7 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v7)));
                                          v10 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v17), asfloat(v10)));
                                          v13 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v9)));
                                          v5 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v9)));
                                          v11 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v21), asfloat(v11)));
                                          v6 = asuint(mad(asfloat(0x3ff02120u), asfloat(v21), asfloat(v6)));
                                          v7 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v21), asfloat(v7)));
                                          v9 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v9)));
                                          v14 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v19), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(0x3f875327u), asfloat(v19), asfloat(v22)));
                                          v5 = asuint(mad(asfloat(0x3ff02120u), asfloat(v18), asfloat(v5)));
                                          v10 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v19), asfloat(v10)));
                                          v9 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v18), asfloat(v9)));
                                          v11 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v20), asfloat(v11)));
                                          v6 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v20), asfloat(v6)));
                                          v12 = asuint(LegacyMul(asfloat(v14), asfloat(v4)));
                                          v7 = asuint(mad(asfloat(0x3f875327u), asfloat(v20), asfloat(v7)));
                                          v20 = asuint(LegacyMul(asfloat(v22), asfloat(v4)));
                                          v13 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v18), asfloat(v13)));
                                          v4 = asuint(LegacyMul(asfloat(v10), asfloat(v4)));
                                          v5 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v15), asfloat(v5)));
                                          v12 = asuint(mad(asfloat(v6), asfloat(v3), asfloat(v12)));
                                          v9 = asuint(mad(asfloat(0x3f875327u), asfloat(v15), asfloat(v9)));
                                          v20 = asuint(mad(asfloat(v7), asfloat(v3), asfloat(v20)));
                                          v13 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v15), asfloat(v13)));
                                          v4 = asuint(mad(asfloat(v11), asfloat(v3), asfloat(v4)));
                                          v12 = asuint(mad(asfloat(v5), asfloat(v2), asfloat(v12)));
                                          v20 = asuint(mad(asfloat(v9), asfloat(v2), asfloat(v20)));
                                          v4 = asuint(mad(asfloat(v13), asfloat(v2), asfloat(v4)));
                                          v3 = v12;
                                          v2 = v20;
                                          scc = (s16 == ((uint)(0)) ? 1u : 0u);
                                          pc = scc == 0u ? 0x1120u : 0x0b7cu;
                                      }
                                      if (pc == 0x0b7cu)
                                      {
                                          pc = 0x2838u;
                                      }
                                      if (pc == 0x0b80u)
                                      {
                                          s0 = 0x42c63852u;
                                          v10 = asuint(mad(asfloat(v10), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v10 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v11), asfloat(v10)));
                                          v10 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v12), asfloat(v10)));
                                          pc = 0x0ba8u;
                                      }
                                      if (pc == 0x0ba8u)
                                      {
                                          s0 = 0x4037ae14u;
                                          v11 = asuint(LegacyMul(asfloat(v10), asfloat(v10)));
                                          v13 = asuint(mad(asfloat(v10), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s2 = 0x39219aceu;
                                          s3 = 0x3a5cd5ddu;
                                          s4 = 0x3831567au;
                                          v17 = asuint(mad(asfloat(0xb7f30d95u), asfloat(v5), 1.0f));
                                          v13 = asuint(mad(asfloat(0xc0400000u), asfloat(v11), asfloat(v13)));
                                          v11 = asuint(mad(asfloat(0x340a20a1u), asfloat(v5), asfloat(s2)));
                                          v12 = asuint(mad(asfloat(0x33349873u), asfloat(v5), asfloat(s4)));
                                          v18 = asuint(mad(asfloat(0x35be175du), asfloat(v5), asfloat(s3)));
                                          s5 = 0xb7f30d95u;
                                          s0 = (asfloat(0x457a0000u) <= asfloat(v5) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v14 = asuint(mad(asfloat(v11), asfloat(v5), asfloat(0x3f5c30adu)));
                                          v11 = asuint(mad(asfloat(0x353e175du), asfloat(v5), asfloat(s3)));
                                          v16 = asuint(mad(asfloat(v12), asfloat(v5), asfloat(0x3ea28216u)));
                                          v12 = asuint(LegacyMul(asfloat(v5), asfloat(v5)));
                                          v22 = asuint(mad(asfloat(0x348a20a1u), asfloat(v5), asfloat(s2)));
                                          v23 = asuint(mad(asfloat(0x33b49873u), asfloat(v5), asfloat(s4)));
                                          v15 = asuint(mad(asfloat(v11), asfloat(v5), 1.0f));
                                          v5 = asuint(mad(asfloat(0x34ad5cb4u), asfloat(v5), asfloat(s5)));
                                          v17 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v12), asfloat(v17)));
                                          v11 = asuint((1.0f / asfloat(v15)));
                                          v20 = asuint(LegacyMul(asfloat(v15), asfloat(v15)));
                                          v21 = asuint(LegacyMul(asfloat(v17), asfloat(v17)));
                                          v19 = asuint((1.0f / asfloat(v17)));
                                          v11 = asuint(LegacyMul(asfloat(v14), asfloat(v11)));
                                          v14 = asuint(LegacyMul(asfloat(v14), asfloat(v18)));
                                          v21 = asuint((1.0f / asfloat(v21)));
                                          v18 = asuint(LegacyMul(asfloat(v16), asfloat(v19)));
                                          v14 = asuint(mad(asfloat(v15), asfloat(v22), -asfloat(v14)));
                                          v15 = asuint(LegacyMul(asfloat(v16), asfloat(v5)));
                                          v20 = asuint((1.0f / asfloat(v20)));
                                          v12 = asuint(mad(asfloat(v11), 2.0f, 4.0f));
                                          v16 = asuint(LegacyMul(asfloat(0x3d4ccccdu), asfloat(v6)));
                                          v22 = ((uint)(0));
                                          v15 = asuint(mad(asfloat(v17), asfloat(v23), -asfloat(v15)));
                                          v12 = asuint(mad(asfloat(0xc1000000u), asfloat(v18), asfloat(v12)));
                                          v5 = asuint(LegacyMul(asfloat(v15), asfloat(v21)));
                                          v14 = asuint(LegacyMul(asfloat(v14), asfloat(v20)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v15 = asuint(LegacyMul(asfloat(v5), asfloat(v5)));
                                          v5 = asuint(LegacyMul(asfloat(v16), asfloat(v5)));
                                          v15 = asuint(mad(asfloat(v14), asfloat(v14), asfloat(v15)));
                                          v19 = asuint((asfloat(v12) + asfloat(v12)));
                                          v20 = asuint(rsqrt(asfloat(v15)));
                                          v19 = asuint(LegacyMul(asfloat(v18), asfloat(v19)));
                                          v13 = (s0 != 0u ? v13 : v19);
                                          v6 = asuint(mad(asfloat(v5), asfloat(v20), asfloat(v11)));
                                          v5 = asuint(LegacyMul(asfloat(v16), asfloat(v14)));
                                          v14 = ((uint)(0));
                                          v18 = asuint(mad(-asfloat(v5), asfloat(v20), asfloat(v18)));
                                          v5 = asuint(mad(asfloat(0xc1000000u), asfloat(v18), 4.0f));
                                          v5 = asuint(mad(2.0f, asfloat(v6), asfloat(v5)));
                                          v16 = asuint((1.0f / asfloat(v5)));
                                          v5 = asuint((asfloat(v16) + asfloat(v16)));
                                          v15 = asuint(mad(asfloat(v5), asfloat(v18), -asfloat(v19)));
                                          v18 = ((uint)(0));
                                          v15 = asuint((asfloat(v15) + asfloat(v13)));
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v15) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x0d88u : 0x0d48u;
                                      }
                                      if (pc == 0x0d48u)
                                      {
                                          v5 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v11)));
                                          v11 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v16)));
                                          v18 = asuint(1.0f);
                                          v5 = asuint(LegacyMul(asfloat(v5), asfloat(v12)));
                                          v10 = (s0 != 0u ? v10 : v5);
                                          v5 = asuint(mad(asfloat(v11), asfloat(v6), -asfloat(v5)));
                                          v6 = asuint((1.0f / asfloat(v15)));
                                          v5 = asuint((asfloat(v5) + asfloat(v10)));
                                          v10 = asuint((1.0f - asfloat(v5)));
                                          v14 = asuint(LegacyMul(asfloat(v5), asfloat(v6)));
                                          v11 = asuint((asfloat(v10) - asfloat(v15)));
                                          v22 = asuint(LegacyMul(asfloat(v11), asfloat(v6)));
                                          pc = 0x0d88u;
                                      }
                                      if (pc == 0x0d88u)
                                      {
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v9) ? 0xffffffffu : 0u);
                                          v17 = ((uint)(0));
                                          v13 = ((uint)(0));
                                          v11 = ((uint)(0));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x0dbcu : 0x0da4u;
                                      }
                                      if (pc == 0x0da4u)
                                      {
                                          v6 = asuint((1.0f / asfloat(v9)));
                                          v10 = asuint((1.0f - asfloat(v7)));
                                          v13 = asuint(1.0f);
                                          v9 = asuint((asfloat(v10) - asfloat(v9)));
                                          v11 = asuint(LegacyMul(asfloat(v7), asfloat(v6)));
                                          v17 = asuint(LegacyMul(asfloat(v9), asfloat(v6)));
                                          pc = 0x0dbcu;
                                      }
                                      if (pc == 0x0dbcu)
                                      {
                                          v7 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v14)));
                                          v9 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v14)));
                                          v12 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v14)));
                                          v14 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v11)));
                                          v6 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v11)));
                                          v7 = asuint(mad(asfloat(0x3e886595u), asfloat(v18), asfloat(v7)));
                                          v9 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v18), asfloat(v9)));
                                          v12 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v18), asfloat(v12)));
                                          v11 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v11)));
                                          v14 = asuint(mad(asfloat(0x3e886595u), asfloat(v13), asfloat(v14)));
                                          v7 = asuint(mad(asfloat(0xbe25460bu), asfloat(v22), asfloat(v7)));
                                          v9 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v22), asfloat(v9)));
                                          v12 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v22), asfloat(v12)));
                                          v6 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v13), asfloat(v6)));
                                          v11 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v13), asfloat(v11)));
                                          v5 = asuint((1.0f / asfloat(v7)));
                                          v14 = asuint(mad(asfloat(0xbe25460bu), asfloat(v17), asfloat(v14)));
                                          v6 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v17), asfloat(v6)));
                                          v11 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v17), asfloat(v11)));
                                          v7 = asuint((1.0f / asfloat(v9)));
                                          v9 = asuint((1.0f / asfloat(v12)));
                                          v5 = asuint(LegacyMul(asfloat(v14), asfloat(v5)));
                                          v6 = asuint(LegacyMul(asfloat(v6), asfloat(v7)));
                                          v20 = asuint(LegacyMul(asfloat(0x3e869f68u), asfloat(v5)));
                                          v12 = asuint(LegacyMul(asfloat(0x3ec61f29u), asfloat(v5)));
                                          v15 = asuint(LegacyMul(asfloat(0xbbfa26f7u), asfloat(v5)));
                                          v13 = asuint(LegacyMul(asfloat(0x3debdc39u), asfloat(v5)));
                                          v10 = asuint(LegacyMul(asfloat(v11), asfloat(v9)));
                                          v9 = asuint(LegacyMul(asfloat(0x3f622a43u), asfloat(v5)));
                                          v11 = asuint(LegacyMul(asfloat(0xbe231fb6u), asfloat(v5)));
                                          v21 = asuint(LegacyMul(asfloat(0xbd8ee5b6u), asfloat(v5)));
                                          v16 = asuint(LegacyMul(asfloat(0xbb14e69au), asfloat(v5)));
                                          v20 = asuint(mad(asfloat(0xbe810333u), asfloat(v6), asfloat(v20)));
                                          v9 = asuint(mad(asfloat(0x3de1ef85u), asfloat(v6), asfloat(v9)));
                                          v12 = asuint(mad(asfloat(0xbec71a7bu), asfloat(v6), asfloat(v12)));
                                          v15 = asuint(mad(asfloat(0xbcf616abu), asfloat(v6), asfloat(v15)));
                                          v5 = asuint(LegacyMul(asfloat(0x3ab46cc2u), asfloat(v5)));
                                          v11 = asuint(mad(asfloat(0xbbb0d86bu), asfloat(v6), asfloat(v11)));
                                          v9 = asuint(mad(asfloat(0x3bcbe686u), asfloat(v10), asfloat(v9)));
                                          v13 = asuint(mad(asfloat(0x3f6361c2u), asfloat(v6), asfloat(v13)));
                                          v21 = asuint(mad(asfloat(0x3c9bd7d9u), asfloat(v6), asfloat(v21)));
                                          v15 = asuint(mad(asfloat(0x3d1a5031u), asfloat(v10), asfloat(v15)));
                                          v16 = asuint(mad(asfloat(0x3d8c8526u), asfloat(v6), asfloat(v16)));
                                          v5 = asuint(mad(asfloat(0x3ac09e9cu), asfloat(v6), asfloat(v5)));
                                          v12 = asuint(mad(asfloat(0x3afb5232u), asfloat(v10), asfloat(v12)));
                                          v20 = asuint(mad(asfloat(0xbc3386dau), asfloat(v10), asfloat(v20)));
                                          v17 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v9)));
                                          v6 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v9)));
                                          v9 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v9)));
                                          v11 = asuint(mad(asfloat(0x3e28a67du), asfloat(v10), asfloat(v11)));
                                          v13 = asuint(mad(asfloat(0xbb5d4762u), asfloat(v10), asfloat(v13)));
                                          v17 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v20), asfloat(v17)));
                                          v22 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v12)));
                                          v21 = asuint(mad(asfloat(0x3d4fdf77u), asfloat(v10), asfloat(v21)));
                                          v16 = asuint(mad(asfloat(0xbd87ddf8u), asfloat(v10), asfloat(v16)));
                                          v23 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v15)));
                                          v14 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v15)));
                                          v5 = asuint(mad(asfloat(0x3f7f457bu), asfloat(v10), asfloat(v5)));
                                          v10 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v15)));
                                          v6 = asuint(mad(asfloat(0x3f371437u), asfloat(v20), asfloat(v6)));
                                          v9 = asuint(mad(asfloat(0x3d93d07du), asfloat(v20), asfloat(v9)));
                                          v20 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v12)));
                                          v17 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v11), asfloat(v17)));
                                          v22 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v13), asfloat(v22)));
                                          v10 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v16), asfloat(v10)));
                                          v23 = asuint(mad(asfloat(0x3f371437u), asfloat(v16), asfloat(v23)));
                                          v14 = asuint(mad(asfloat(0x3d93d07du), asfloat(v16), asfloat(v14)));
                                          v12 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v12)));
                                          v20 = asuint(mad(asfloat(0x3f371437u), asfloat(v13), asfloat(v20)));
                                          v6 = asuint(mad(asfloat(0x3df41aefu), asfloat(v11), asfloat(v6)));
                                          v22 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v21), asfloat(v22)));
                                          v19 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v17)));
                                          v10 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v5), asfloat(v10)));
                                          v23 = asuint(mad(asfloat(0x3df41aefu), asfloat(v5), asfloat(v23)));
                                          v14 = asuint(mad(asfloat(0x3f734721u), asfloat(v5), asfloat(v14)));
                                          v5 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v17)));
                                          v9 = asuint(mad(asfloat(0x3f734721u), asfloat(v11), asfloat(v9)));
                                          v15 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v17)));
                                          v12 = asuint(mad(asfloat(0x3d93d07du), asfloat(v13), asfloat(v12)));
                                          v19 = asuint(mad(asfloat(0x3ff02120u), asfloat(v22), asfloat(v19)));
                                          v5 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v22), asfloat(v5)));
                                          v20 = asuint(mad(asfloat(0x3df41aefu), asfloat(v21), asfloat(v20)));
                                          v18 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v6)));
                                          v17 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v6)));
                                          v6 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v6)));
                                          v15 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v22), asfloat(v15)));
                                          v19 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v10), asfloat(v19)));
                                          v12 = asuint(mad(asfloat(0x3f734721u), asfloat(v21), asfloat(v12)));
                                          v11 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v9)));
                                          v7 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v9)));
                                          v17 = asuint(mad(asfloat(0x3ff02120u), asfloat(v20), asfloat(v17)));
                                          v6 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v20), asfloat(v6)));
                                          v9 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v9)));
                                          v5 = asuint(mad(asfloat(0x3f875327u), asfloat(v10), asfloat(v5)));
                                          v18 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v20), asfloat(v18)));
                                          v11 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v12), asfloat(v11)));
                                          v7 = asuint(mad(asfloat(0x3ff02120u), asfloat(v12), asfloat(v7)));
                                          v9 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v12), asfloat(v9)));
                                          v15 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v10), asfloat(v15)));
                                          v17 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v23), asfloat(v17)));
                                          v16 = asuint(LegacyMul(asfloat(v19), asfloat(v4)));
                                          v12 = asuint(LegacyMul(asfloat(v5), asfloat(v4)));
                                          v6 = asuint(mad(asfloat(0x3f875327u), asfloat(v23), asfloat(v6)));
                                          v18 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v23), asfloat(v18)));
                                          v4 = asuint(LegacyMul(asfloat(v15), asfloat(v4)));
                                          v7 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v14), asfloat(v7)));
                                          v16 = asuint(mad(asfloat(v17), asfloat(v3), asfloat(v16)));
                                          v9 = asuint(mad(asfloat(0x3f875327u), asfloat(v14), asfloat(v9)));
                                          v12 = asuint(mad(asfloat(v6), asfloat(v3), asfloat(v12)));
                                          v11 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v14), asfloat(v11)));
                                          v4 = asuint(mad(asfloat(v18), asfloat(v3), asfloat(v4)));
                                          v16 = asuint(mad(asfloat(v7), asfloat(v2), asfloat(v16)));
                                          v12 = asuint(mad(asfloat(v9), asfloat(v2), asfloat(v12)));
                                          v4 = asuint(mad(asfloat(v11), asfloat(v2), asfloat(v4)));
                                          v3 = v16;
                                          v2 = v12;
                                          scc = (s16 == ((uint)(0)) ? 1u : 0u);
                                          pc = scc != 0u ? 0x2838u : 0x1120u;
                                      }
                                      if (pc == 0x1120u)
                                      {
                                          s0 = Small[5][0];
                                          s1 = Small[5][1];
                                          s2 = Small[5][2];
                                          s3 = Small[5][3];
                                          v5 = asuint(LegacyMul(asfloat(0x3f372474u), asfloat(v3)));
                                          s12 = Small[3][0];
                                          s13 = Small[3][1];
                                          s14 = Small[3][2];
                                          s15 = Small[3][3];
                                          s16 = Small[4][0];
                                          s17 = Small[4][1];
                                          s18 = Small[4][2];
                                          s19 = Small[4][3];
                                          v5 = asuint(mad(asfloat(0x3e59999au), asfloat(v4), asfloat(v5)));
                                          v5 = asuint(mad(asfloat(0x3d93a92au), asfloat(v2), asfloat(v5)));
                                          v6 = asuint((asfloat(s3) - asfloat(s2)));
                                          v7 = asuint((asfloat(s1) - asfloat(s0)));
                                          v9 = asuint((asfloat(v5) - asfloat(s2)));
                                          v5 = asuint((asfloat(v5) - asfloat(s0)));
                                          s0 = (asfloat(0x45cb2000u) == asfloat(s12) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v6 = asuint((1.0f / asfloat(v6)));
                                          vcc_lo = s0;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v7 = asuint((1.0f / asfloat(v7)));
                                          v9 = asuint(saturate(LegacyMul(asfloat(v9), asfloat(v6))));
                                          v6 = 0x40400000u;
                                          v5 = asuint(saturate(LegacyMul(asfloat(v5), asfloat(v7))));
                                          v10 = asuint(mad(asfloat(v9), -2.0f, asfloat(0x40400000u)));
                                          v9 = asuint(LegacyMul(asfloat(v9), asfloat(v9)));
                                          v7 = s12;
                                          v6 = asuint(NonFusedMac(-2.0f, asfloat(v5), asfloat(v6)));
                                          v19 = asuint(LegacyMul(asfloat(v5), asfloat(v5)));
                                          v5 = asuint(LegacyMul(asfloat(v9), asfloat(v10)));
                                          pc = vcc_lo != 0u ? 0x1210u : 0x11b8u;
                                      }
                                      if (pc == 0x11b8u)
                                      {
                                          v9 = asuint((1.0f / asfloat(s12)));
                                          v11 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s12)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v11) ? 0xffffffffu : 0u);
                                          v9 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v9)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v10 = asuint(LegacyMul(asfloat(v9), asfloat(v9)));
                                          v11 = asuint(LegacyMul(asfloat(v10), asfloat(v9)));
                                          pc = vcc_lo == 0u ? 0x1224u : 0x11e8u;
                                      }
                                      if (pc == 0x11e8u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v9 = asuint(mad(asfloat(v9), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v9 = asuint(mad(asfloat(0x49e82740u), asfloat(v10), asfloat(v9)));
                                          v9 = asuint(mad(asfloat(0xceef2e78u), asfloat(v11), asfloat(v9)));
                                          pc = 0x1248u;
                                      }
                                      if (pc == 0x1210u)
                                      {
                                          v9 = 0x3ea01cd6u;
                                          v10 = 0x3ea8769fu;
                                          pc = 0x1328u;
                                      }
                                      if (pc == 0x1224u)
                                      {
                                          s0 = 0x42c63852u;
                                          v9 = asuint(mad(asfloat(v9), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v9 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v10), asfloat(v9)));
                                          v9 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v11), asfloat(v9)));
                                          pc = 0x1248u;
                                      }
                                      if (pc == 0x1248u)
                                      {
                                          s0 = 0x4037ae14u;
                                          v10 = asuint(LegacyMul(asfloat(v9), asfloat(v9)));
                                          v11 = asuint(mad(asfloat(v9), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s0 = 0x340a20a1u;
                                          v14 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s12), 1.0f));
                                          v15 = asuint(LegacyMul(asfloat(s12), asfloat(s12)));
                                          vcc_lo = (asfloat(0x457a0000u) <= asfloat(s12) ? 0xffffffffu : 0u);
                                          v11 = asuint(mad(asfloat(0xc0400000u), asfloat(v10), asfloat(v11)));
                                          v10 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x39219aceu)));
                                          s0 = 0x353e175du;
                                          v14 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v15), asfloat(v14)));
                                          v12 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x3a5cd5ddu)));
                                          s0 = 0x33349873u;
                                          v10 = asuint(mad(asfloat(s12), asfloat(v10), asfloat(0x3f5c30adu)));
                                          v7 = asuint(mad(asfloat(v7), asfloat(s0), asfloat(0x3831567au)));
                                          v14 = asuint((1.0f / asfloat(v14)));
                                          v12 = asuint(mad(asfloat(v12), asfloat(s12), 1.0f));
                                          v7 = asuint(mad(asfloat(s12), asfloat(v7), asfloat(0x3ea28216u)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v7 = asuint(LegacyMul(asfloat(v7), asfloat(v14)));
                                          v10 = asuint(LegacyMul(asfloat(v10), asfloat(v12)));
                                          v12 = asuint(mad(asfloat(v10), 2.0f, 4.0f));
                                          v10 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v10)));
                                          v12 = asuint(mad(asfloat(0xc1000000u), asfloat(v7), asfloat(v12)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v10 = asuint(LegacyMul(asfloat(v10), asfloat(v12)));
                                          v12 = asuint((asfloat(v12) + asfloat(v12)));
                                          v9 = (vcc_lo != 0u ? v9 : v10);
                                          v14 = asuint(LegacyMul(asfloat(v7), asfloat(v12)));
                                          v10 = (vcc_lo != 0u ? v11 : v14);
                                          pc = 0x1328u;
                                      }
                                      if (pc == 0x1328u)
                                      {
                                          v13 = asuint((1.0f / asfloat(s23)));
                                          v16 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s23)));
                                          v7 = asuint(mad(-asfloat(v19), asfloat(v6), 1.0f));
                                          v12 = s15;
                                          v11 = s14;
                                          v6 = asuint(mad(asfloat(v19), asfloat(v6), -asfloat(v5)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v16) ? 0xffffffffu : 0u);
                                          v13 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v13)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v15 = asuint(LegacyMul(asfloat(v13), asfloat(v13)));
                                          v14 = asuint(LegacyMul(asfloat(v15), asfloat(v13)));
                                          pc = vcc_lo == 0u ? 0x1398u : 0x1370u;
                                      }
                                      if (pc == 0x1370u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v13 = asuint(mad(asfloat(v13), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v13 = asuint(mad(asfloat(0x49e82740u), asfloat(v15), asfloat(v13)));
                                          v13 = asuint(mad(asfloat(0xceef2e78u), asfloat(v14), asfloat(v13)));
                                          pc = 0x13c0u;
                                      }
                                      if (pc == 0x1398u)
                                      {
                                          s0 = 0x42c63852u;
                                          v13 = asuint(mad(asfloat(v13), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v13 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v15), asfloat(v13)));
                                          v13 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v14), asfloat(v13)));
                                          pc = 0x13c0u;
                                      }
                                      if (pc == 0x13c0u)
                                      {
                                          v21 = asuint(LegacyMul(asfloat(s23), asfloat(s23)));
                                          v22 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s23), 1.0f));
                                          s0 = 0xb7f30d95u;
                                          s4 = 0x3831567au;
                                          s3 = 0x3a5cd5ddu;
                                          v20 = asuint(mad(asfloat(0x34ad5cb4u), asfloat(v8), asfloat(s0)));
                                          s0 = 0x4037ae14u;
                                          v19 = asuint(mad(asfloat(0x33349873u), asfloat(v8), asfloat(s4)));
                                          v22 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v21), asfloat(v22)));
                                          v14 = asuint(mad(asfloat(0x353e175du), asfloat(v8), asfloat(s3)));
                                          v23 = asuint(mad(asfloat(v13), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          v17 = asuint(LegacyMul(asfloat(v13), asfloat(v13)));
                                          v19 = asuint(mad(asfloat(s23), asfloat(v19), asfloat(0x3ea28216u)));
                                          v15 = asuint(mad(asfloat(0x33b49873u), asfloat(v8), asfloat(s4)));
                                          v16 = asuint(mad(asfloat(v14), asfloat(s23), 1.0f));
                                          s2 = 0x39219aceu;
                                          v23 = asuint(mad(asfloat(0xc0400000u), asfloat(v17), asfloat(v23)));
                                          v17 = asuint(LegacyMul(asfloat(v22), asfloat(v22)));
                                          v20 = asuint(LegacyMul(asfloat(v19), asfloat(v20)));
                                          v14 = asuint(LegacyMul(asfloat(v16), asfloat(v16)));
                                          v21 = asuint((1.0f / asfloat(v22)));
                                          s0 = (asfloat(0x457a0000u) <= asfloat(s23) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v22 = asuint(mad(asfloat(v22), asfloat(v15), -asfloat(v20)));
                                          v18 = asuint((1.0f / asfloat(v17)));
                                          v17 = asuint((1.0f / asfloat(v14)));
                                          v14 = asuint(mad(asfloat(0x340a20a1u), asfloat(v8), asfloat(s2)));
                                          v19 = asuint(LegacyMul(asfloat(v19), asfloat(v21)));
                                          v15 = asuint((1.0f / asfloat(v16)));
                                          v20 = asuint(LegacyMul(asfloat(v22), asfloat(v18)));
                                          v18 = asuint(mad(asfloat(s23), asfloat(v14), asfloat(0x3f5c30adu)));
                                          v21 = asuint(mad(asfloat(0x35be175du), asfloat(v8), asfloat(s3)));
                                          v8 = asuint(mad(asfloat(0x348a20a1u), asfloat(v8), asfloat(s2)));
                                          v22 = asuint(LegacyMul(asfloat(v20), asfloat(v20)));
                                          v14 = asuint(LegacyMul(asfloat(v18), asfloat(v15)));
                                          v18 = asuint(LegacyMul(asfloat(v18), asfloat(v21)));
                                          v15 = asuint(mad(asfloat(v14), 2.0f, 4.0f));
                                          v8 = asuint(mad(asfloat(v16), asfloat(v8), -asfloat(v18)));
                                          v15 = asuint(mad(asfloat(0xc1000000u), asfloat(v19), asfloat(v15)));
                                          v8 = asuint(LegacyMul(asfloat(v8), asfloat(v17)));
                                          v17 = asuint(LegacyMul(asfloat(0x3d4ccccdu), asfloat(s13)));
                                          v15 = asuint((1.0f / asfloat(v15)));
                                          v22 = asuint(mad(asfloat(v8), asfloat(v8), asfloat(v22)));
                                          v18 = asuint(LegacyMul(asfloat(v17), asfloat(v20)));
                                          v16 = asuint(rsqrt(asfloat(v22)));
                                          v22 = asuint(LegacyMul(asfloat(v17), asfloat(v8)));
                                          v17 = ((uint)(0));
                                          v21 = asuint((asfloat(v15) + asfloat(v15)));
                                          v21 = asuint(LegacyMul(asfloat(v19), asfloat(v21)));
                                          v19 = asuint(mad(-asfloat(v22), asfloat(v16), asfloat(v19)));
                                          v18 = asuint(mad(asfloat(v18), asfloat(v16), asfloat(v14)));
                                          v16 = ((uint)(0));
                                          v23 = (s0 != 0u ? v23 : v21);
                                          v8 = asuint(mad(asfloat(0xc1000000u), asfloat(v19), 4.0f));
                                          v8 = asuint(mad(2.0f, asfloat(v18), asfloat(v8)));
                                          v20 = asuint((1.0f / asfloat(v8)));
                                          v8 = asuint((asfloat(v20) + asfloat(v20)));
                                          v8 = asuint(mad(asfloat(v8), asfloat(v19), -asfloat(v21)));
                                          v19 = asuint((asfloat(v8) + asfloat(v23)));
                                          v8 = ((uint)(0));
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v19) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x15a8u : 0x1568u;
                                      }
                                      if (pc == 0x1568u)
                                      {
                                          v8 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v14)));
                                          v16 = asuint(1.0f);
                                          v8 = asuint(LegacyMul(asfloat(v8), asfloat(v15)));
                                          v15 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v20)));
                                          v13 = (s0 != 0u ? v13 : v8);
                                          v8 = asuint(mad(asfloat(v15), asfloat(v18), -asfloat(v8)));
                                          v8 = asuint((asfloat(v8) + asfloat(v13)));
                                          v13 = asuint((1.0f / asfloat(v19)));
                                          v14 = asuint((1.0f - asfloat(v8)));
                                          v14 = asuint((asfloat(v14) - asfloat(v19)));
                                          v17 = asuint(LegacyMul(asfloat(v8), asfloat(v13)));
                                          v8 = asuint(LegacyMul(asfloat(v14), asfloat(v13)));
                                          pc = 0x15a8u;
                                      }
                                      if (pc == 0x15a8u)
                                      {
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v10) ? 0xffffffffu : 0u);
                                          v13 = ((uint)(0));
                                          v21 = ((uint)(0));
                                          v15 = ((uint)(0));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x15dcu : 0x15c4u;
                                      }
                                      if (pc == 0x15c4u)
                                      {
                                          v19 = asuint((1.0f / asfloat(v10)));
                                          v15 = asuint((1.0f - asfloat(v9)));
                                          v21 = asuint(1.0f);
                                          v10 = asuint((asfloat(v15) - asfloat(v10)));
                                          v15 = asuint(LegacyMul(asfloat(v9), asfloat(v19)));
                                          v13 = asuint(LegacyMul(asfloat(v10), asfloat(v19)));
                                          pc = 0x15dcu;
                                      }
                                      if (pc == 0x15dcu)
                                      {
                                          v9 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v17)));
                                          v10 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v17)));
                                          v17 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v17)));
                                          v18 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v15)));
                                          v19 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v15)));
                                          v9 = asuint(mad(asfloat(0x3e886595u), asfloat(v16), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v16), asfloat(v10)));
                                          v17 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v16), asfloat(v17)));
                                          v18 = asuint(mad(asfloat(0x3e886595u), asfloat(v21), asfloat(v18)));
                                          v15 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v15)));
                                          v9 = asuint(mad(asfloat(0xbe25460bu), asfloat(v8), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v8), asfloat(v10)));
                                          v17 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v8), asfloat(v17)));
                                          v19 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v21), asfloat(v19)));
                                          v18 = asuint(mad(asfloat(0xbe25460bu), asfloat(v13), asfloat(v18)));
                                          v8 = asuint((1.0f / asfloat(v9)));
                                          v15 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v21), asfloat(v15)));
                                          s0 = (asfloat(0x45cb2000u) == asfloat(s15) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v19 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v13), asfloat(v19)));
                                          v9 = asuint((1.0f / asfloat(v10)));
                                          v15 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v13), asfloat(v15)));
                                          vcc_lo = s0;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v8 = asuint(LegacyMul(asfloat(v18), asfloat(v8)));
                                          v10 = asuint((1.0f / asfloat(v17)));
                                          v9 = asuint(LegacyMul(asfloat(v19), asfloat(v9)));
                                          v23 = asuint(LegacyMul(asfloat(0x3f622a43u), asfloat(v8)));
                                          v14 = asuint(LegacyMul(asfloat(0x3e869f68u), asfloat(v8)));
                                          v16 = asuint(LegacyMul(asfloat(0x3ec61f29u), asfloat(v8)));
                                          v17 = asuint(LegacyMul(asfloat(0x3debdc39u), asfloat(v8)));
                                          v18 = asuint(LegacyMul(asfloat(0xbd8ee5b6u), asfloat(v8)));
                                          v10 = asuint(LegacyMul(asfloat(v15), asfloat(v10)));
                                          v23 = asuint(mad(asfloat(0x3de1ef85u), asfloat(v9), asfloat(v23)));
                                          v15 = asuint(LegacyMul(asfloat(0xbe231fb6u), asfloat(v8)));
                                          v19 = asuint(LegacyMul(asfloat(0xbbfa26f7u), asfloat(v8)));
                                          v20 = asuint(LegacyMul(asfloat(0xbb14e69au), asfloat(v8)));
                                          v14 = asuint(mad(asfloat(0xbe810333u), asfloat(v9), asfloat(v14)));
                                          v16 = asuint(mad(asfloat(0xbec71a7bu), asfloat(v9), asfloat(v16)));
                                          v23 = asuint(mad(asfloat(0x3bcbe686u), asfloat(v10), asfloat(v23)));
                                          v8 = asuint(LegacyMul(asfloat(0x3ab46cc2u), asfloat(v8)));
                                          v15 = asuint(mad(asfloat(0xbbb0d86bu), asfloat(v9), asfloat(v15)));
                                          v17 = asuint(mad(asfloat(0x3f6361c2u), asfloat(v9), asfloat(v17)));
                                          v18 = asuint(mad(asfloat(0x3c9bd7d9u), asfloat(v9), asfloat(v18)));
                                          v19 = asuint(mad(asfloat(0xbcf616abu), asfloat(v9), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0x3d8c8526u), asfloat(v9), asfloat(v20)));
                                          v8 = asuint(mad(asfloat(0x3ac09e9cu), asfloat(v9), asfloat(v8)));
                                          v16 = asuint(mad(asfloat(0x3afb5232u), asfloat(v10), asfloat(v16)));
                                          v14 = asuint(mad(asfloat(0xbc3386dau), asfloat(v10), asfloat(v14)));
                                          v21 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v23)));
                                          v22 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v23)));
                                          v9 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v23)));
                                          v19 = asuint(mad(asfloat(0x3d1a5031u), asfloat(v10), asfloat(v19)));
                                          v15 = asuint(mad(asfloat(0x3e28a67du), asfloat(v10), asfloat(v15)));
                                          v21 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v14), asfloat(v21)));
                                          v17 = asuint(mad(asfloat(0xbb5d4762u), asfloat(v10), asfloat(v17)));
                                          v13 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v16)));
                                          v23 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v16)));
                                          v18 = asuint(mad(asfloat(0x3d4fdf77u), asfloat(v10), asfloat(v18)));
                                          v20 = asuint(mad(asfloat(0xbd87ddf8u), asfloat(v10), asfloat(v20)));
                                          v8 = asuint(mad(asfloat(0x3f7f457bu), asfloat(v10), asfloat(v8)));
                                          v10 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v16)));
                                          v22 = asuint(mad(asfloat(0x3f371437u), asfloat(v14), asfloat(v22)));
                                          v13 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v17), asfloat(v13)));
                                          v24 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v19)));
                                          v23 = asuint(mad(asfloat(0x3d93d07du), asfloat(v17), asfloat(v23)));
                                          v10 = asuint(mad(asfloat(0x3f371437u), asfloat(v17), asfloat(v10)));
                                          v21 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v15), asfloat(v21)));
                                          v17 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v19)));
                                          v9 = asuint(mad(asfloat(0x3d93d07du), asfloat(v14), asfloat(v9)));
                                          v24 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v20), asfloat(v24)));
                                          v19 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v19)));
                                          v22 = asuint(mad(asfloat(0x3df41aefu), asfloat(v15), asfloat(v22)));
                                          v13 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v18), asfloat(v13)));
                                          v25 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v21)));
                                          v10 = asuint(mad(asfloat(0x3df41aefu), asfloat(v18), asfloat(v10)));
                                          v23 = asuint(mad(asfloat(0x3f734721u), asfloat(v18), asfloat(v23)));
                                          v18 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v21)));
                                          v21 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v21)));
                                          v9 = asuint(mad(asfloat(0x3f734721u), asfloat(v15), asfloat(v9)));
                                          v17 = asuint(mad(asfloat(0x3f371437u), asfloat(v20), asfloat(v17)));
                                          v19 = asuint(mad(asfloat(0x3d93d07du), asfloat(v20), asfloat(v19)));
                                          v20 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v22)));
                                          v26 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v22)));
                                          v22 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v22)));
                                          v24 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v8), asfloat(v24)));
                                          v18 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v13), asfloat(v18)));
                                          v25 = asuint(mad(asfloat(0x3ff02120u), asfloat(v13), asfloat(v25)));
                                          v21 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v13), asfloat(v21)));
                                          v14 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v9)));
                                          v15 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v9)));
                                          v16 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v9)));
                                          v25 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v24), asfloat(v25)));
                                          v17 = asuint(mad(asfloat(0x3df41aefu), asfloat(v8), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v10), asfloat(v20)));
                                          v26 = asuint(mad(asfloat(0x3ff02120u), asfloat(v10), asfloat(v26)));
                                          v22 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v10), asfloat(v22)));
                                          v21 = asuint(mad(asfloat(0x3f875327u), asfloat(v24), asfloat(v21)));
                                          v18 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v24), asfloat(v18)));
                                          v19 = asuint(mad(asfloat(0x3f734721u), asfloat(v8), asfloat(v19)));
                                          v14 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v23), asfloat(v14)));
                                          v15 = asuint(mad(asfloat(0x3ff02120u), asfloat(v23), asfloat(v15)));
                                          v16 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v23), asfloat(v16)));
                                          v20 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v17), asfloat(v20)));
                                          v8 = asuint(LegacyMul(asfloat(v18), asfloat(v4)));
                                          v26 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v17), asfloat(v26)));
                                          v9 = asuint(LegacyMul(asfloat(v25), asfloat(v4)));
                                          v22 = asuint(mad(asfloat(0x3f875327u), asfloat(v17), asfloat(v22)));
                                          v10 = asuint(LegacyMul(asfloat(v21), asfloat(v4)));
                                          v14 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v19), asfloat(v14)));
                                          v15 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v19), asfloat(v15)));
                                          v16 = asuint(mad(asfloat(0x3f875327u), asfloat(v19), asfloat(v16)));
                                          v8 = asuint(mad(asfloat(v20), asfloat(v3), asfloat(v8)));
                                          v9 = asuint(mad(asfloat(v26), asfloat(v3), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(v22), asfloat(v3), asfloat(v10)));
                                          pc = vcc_lo != 0u ? 0x1990u : 0x1938u;
                                      }
                                      if (pc == 0x1938u)
                                      {
                                          v13 = asuint((1.0f / asfloat(s15)));
                                          v18 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s15)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v18) ? 0xffffffffu : 0u);
                                          v13 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v13)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v22 = asuint(LegacyMul(asfloat(v13), asfloat(v13)));
                                          v18 = asuint(LegacyMul(asfloat(v22), asfloat(v13)));
                                          pc = vcc_lo == 0u ? 0x19a4u : 0x1968u;
                                      }
                                      if (pc == 0x1968u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v23 = asuint(mad(asfloat(v13), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v23 = asuint(mad(asfloat(0x49e82740u), asfloat(v22), asfloat(v23)));
                                          v23 = asuint(mad(asfloat(0xceef2e78u), asfloat(v18), asfloat(v23)));
                                          pc = 0x19c8u;
                                      }
                                      if (pc == 0x1990u)
                                      {
                                          v12 = 0x3ea01cd6u;
                                          v13 = 0x3ea8769fu;
                                          pc = 0x1aa8u;
                                      }
                                      if (pc == 0x19a4u)
                                      {
                                          s0 = 0x42c63852u;
                                          v23 = asuint(mad(asfloat(v13), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v23 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v22), asfloat(v23)));
                                          v23 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v18), asfloat(v23)));
                                          pc = 0x19c8u;
                                      }
                                      if (pc == 0x19c8u)
                                      {
                                          s0 = 0x4037ae14u;
                                          v17 = asuint(LegacyMul(asfloat(v23), asfloat(v23)));
                                          v18 = asuint(mad(asfloat(v23), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s0 = 0x340a20a1u;
                                          v20 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s15), 1.0f));
                                          v21 = asuint(LegacyMul(asfloat(s15), asfloat(s15)));
                                          vcc_lo = (asfloat(0x457a0000u) <= asfloat(s15) ? 0xffffffffu : 0u);
                                          v18 = asuint(mad(asfloat(0xc0400000u), asfloat(v17), asfloat(v18)));
                                          v17 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x39219aceu)));
                                          s0 = 0x353e175du;
                                          v20 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v21), asfloat(v20)));
                                          v19 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x3a5cd5ddu)));
                                          s0 = 0x33349873u;
                                          v17 = asuint(mad(asfloat(s15), asfloat(v17), asfloat(0x3f5c30adu)));
                                          v12 = asuint(mad(asfloat(v12), asfloat(s0), asfloat(0x3831567au)));
                                          v20 = asuint((1.0f / asfloat(v20)));
                                          v19 = asuint(mad(asfloat(v19), asfloat(s15), 1.0f));
                                          v22 = asuint(mad(asfloat(s15), asfloat(v12), asfloat(0x3ea28216u)));
                                          v19 = asuint((1.0f / asfloat(v19)));
                                          v12 = asuint(LegacyMul(asfloat(v22), asfloat(v20)));
                                          v17 = asuint(LegacyMul(asfloat(v17), asfloat(v19)));
                                          v19 = asuint(mad(asfloat(v17), 2.0f, 4.0f));
                                          v17 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v17)));
                                          v19 = asuint(mad(asfloat(0xc1000000u), asfloat(v12), asfloat(v19)));
                                          v19 = asuint((1.0f / asfloat(v19)));
                                          v17 = asuint(LegacyMul(asfloat(v17), asfloat(v19)));
                                          v19 = asuint((asfloat(v19) + asfloat(v19)));
                                          v19 = asuint(LegacyMul(asfloat(v12), asfloat(v19)));
                                          v12 = (vcc_lo != 0u ? v23 : v17);
                                          v13 = (vcc_lo != 0u ? v18 : v19);
                                          pc = 0x1aa8u;
                                      }
                                      if (pc == 0x1aa8u)
                                      {
                                          v17 = asuint((1.0f / asfloat(s14)));
                                          v19 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s14)));
                                          v8 = asuint(mad(asfloat(v14), asfloat(v2), asfloat(v8)));
                                          v9 = asuint(mad(asfloat(v15), asfloat(v2), asfloat(v9)));
                                          v10 = asuint(mad(asfloat(v16), asfloat(v2), asfloat(v10)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v19) ? 0xffffffffu : 0u);
                                          v17 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v17)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v23 = asuint(LegacyMul(asfloat(v17), asfloat(v17)));
                                          v15 = asuint(LegacyMul(asfloat(v23), asfloat(v17)));
                                          pc = vcc_lo == 0u ? 0x1b0cu : 0x1ae4u;
                                      }
                                      if (pc == 0x1ae4u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v14 = asuint(mad(asfloat(v17), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v14 = asuint(mad(asfloat(0x49e82740u), asfloat(v23), asfloat(v14)));
                                          v14 = asuint(mad(asfloat(0xceef2e78u), asfloat(v15), asfloat(v14)));
                                          pc = 0x1b30u;
                                      }
                                      if (pc == 0x1b0cu)
                                      {
                                          s0 = 0x42c63852u;
                                          v14 = asuint(mad(asfloat(v17), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v14 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v23), asfloat(v14)));
                                          v14 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v15), asfloat(v14)));
                                          pc = 0x1b30u;
                                      }
                                      if (pc == 0x1b30u)
                                      {
                                          s3 = 0x3a5cd5ddu;
                                          s2 = 0x39219aceu;
                                          v15 = asuint(mad(asfloat(0x353e175du), asfloat(v11), asfloat(s3)));
                                          v23 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s14), 1.0f));
                                          s0 = 0xb7f30d95u;
                                          s4 = 0x3831567au;
                                          v21 = asuint(mad(asfloat(0x34ad5cb4u), asfloat(v11), asfloat(s0)));
                                          v17 = asuint(mad(asfloat(v15), asfloat(s14), 1.0f));
                                          s0 = 0x4037ae14u;
                                          v20 = asuint(mad(asfloat(0x33349873u), asfloat(v11), asfloat(s4)));
                                          v24 = asuint(mad(asfloat(v14), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          v25 = asuint(LegacyMul(asfloat(v14), asfloat(v14)));
                                          v15 = asuint(LegacyMul(asfloat(v17), asfloat(v17)));
                                          v22 = asuint((1.0f / asfloat(v17)));
                                          v20 = asuint(mad(asfloat(s14), asfloat(v20), asfloat(0x3ea28216u)));
                                          v26 = asuint(mad(asfloat(0x33b49873u), asfloat(v11), asfloat(s4)));
                                          v24 = asuint(mad(asfloat(0xc0400000u), asfloat(v25), asfloat(v24)));
                                          s0 = (asfloat(0x457a0000u) <= asfloat(s14) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v18 = asuint((1.0f / asfloat(v15)));
                                          v15 = asuint(mad(asfloat(0x340a20a1u), asfloat(v11), asfloat(s2)));
                                          v21 = asuint(LegacyMul(asfloat(v20), asfloat(v21)));
                                          v19 = asuint(mad(asfloat(s14), asfloat(v15), asfloat(0x3f5c30adu)));
                                          v15 = asuint(LegacyMul(asfloat(v19), asfloat(v22)));
                                          v22 = asuint(LegacyMul(asfloat(s14), asfloat(s14)));
                                          v16 = asuint(mad(asfloat(v15), 2.0f, 4.0f));
                                          v23 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v22), asfloat(v23)));
                                          v25 = asuint(LegacyMul(asfloat(v23), asfloat(v23)));
                                          v22 = asuint((1.0f / asfloat(v23)));
                                          v23 = asuint(mad(asfloat(v23), asfloat(v26), -asfloat(v21)));
                                          v25 = asuint((1.0f / asfloat(v25)));
                                          v20 = asuint(LegacyMul(asfloat(v20), asfloat(v22)));
                                          v21 = asuint(LegacyMul(asfloat(v23), asfloat(v25)));
                                          v25 = asuint(mad(asfloat(0x35be175du), asfloat(v11), asfloat(s3)));
                                          v11 = asuint(mad(asfloat(0x348a20a1u), asfloat(v11), asfloat(s2)));
                                          v16 = asuint(mad(asfloat(0xc1000000u), asfloat(v20), asfloat(v16)));
                                          v23 = asuint(LegacyMul(asfloat(v21), asfloat(v21)));
                                          v19 = asuint(LegacyMul(asfloat(v19), asfloat(v25)));
                                          v16 = asuint((1.0f / asfloat(v16)));
                                          v11 = asuint(mad(asfloat(v17), asfloat(v11), -asfloat(v19)));
                                          v11 = asuint(LegacyMul(asfloat(v11), asfloat(v18)));
                                          v18 = asuint(LegacyMul(asfloat(0x3d4ccccdu), asfloat(s16)));
                                          v22 = asuint((asfloat(v16) + asfloat(v16)));
                                          v23 = asuint(mad(asfloat(v11), asfloat(v11), asfloat(v23)));
                                          v11 = asuint(LegacyMul(asfloat(v18), asfloat(v11)));
                                          v22 = asuint(LegacyMul(asfloat(v20), asfloat(v22)));
                                          v19 = asuint(LegacyMul(asfloat(v18), asfloat(v21)));
                                          v17 = asuint(rsqrt(asfloat(v23)));
                                          v23 = ((uint)(0));
                                          v24 = (s0 != 0u ? v24 : v22);
                                          v20 = asuint(mad(-asfloat(v11), asfloat(v17), asfloat(v20)));
                                          v19 = asuint(mad(asfloat(v19), asfloat(v17), asfloat(v15)));
                                          v18 = asuint(mad(asfloat(0xc1000000u), asfloat(v20), 4.0f));
                                          v18 = asuint(mad(2.0f, asfloat(v19), asfloat(v18)));
                                          v21 = asuint((1.0f / asfloat(v18)));
                                          v18 = ((uint)(0));
                                          v11 = asuint((asfloat(v21) + asfloat(v21)));
                                          v11 = asuint(mad(asfloat(v11), asfloat(v20), -asfloat(v22)));
                                          v20 = asuint((asfloat(v11) + asfloat(v24)));
                                          v11 = ((uint)(0));
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v20) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x1d18u : 0x1cd8u;
                                      }
                                      if (pc == 0x1cd8u)
                                      {
                                          v11 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v15)));
                                          v18 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v21)));
                                          v23 = asuint(1.0f);
                                          v17 = asuint(LegacyMul(asfloat(v11), asfloat(v16)));
                                          v14 = (s0 != 0u ? v14 : v17);
                                          v11 = asuint(mad(asfloat(v18), asfloat(v19), -asfloat(v17)));
                                          v11 = asuint((asfloat(v11) + asfloat(v14)));
                                          v14 = asuint((1.0f / asfloat(v20)));
                                          v15 = asuint((1.0f - asfloat(v11)));
                                          v15 = asuint((asfloat(v15) - asfloat(v20)));
                                          v18 = asuint(LegacyMul(asfloat(v11), asfloat(v14)));
                                          v11 = asuint(LegacyMul(asfloat(v15), asfloat(v14)));
                                          pc = 0x1d18u;
                                      }
                                      if (pc == 0x1d18u)
                                      {
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v13) ? 0xffffffffu : 0u);
                                          v14 = ((uint)(0));
                                          v22 = ((uint)(0));
                                          v16 = ((uint)(0));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x1d4cu : 0x1d34u;
                                      }
                                      if (pc == 0x1d34u)
                                      {
                                          v14 = asuint((1.0f / asfloat(v13)));
                                          v15 = asuint((1.0f - asfloat(v12)));
                                          v22 = asuint(1.0f);
                                          v13 = asuint((asfloat(v15) - asfloat(v13)));
                                          v16 = asuint(LegacyMul(asfloat(v12), asfloat(v14)));
                                          v14 = asuint(LegacyMul(asfloat(v13), asfloat(v14)));
                                          pc = 0x1d4cu;
                                      }
                                      if (pc == 0x1d4cu)
                                      {
                                          v12 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v18)));
                                          v13 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v18)));
                                          v18 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v18)));
                                          v17 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v16)));
                                          v15 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v16)));
                                          v12 = asuint(mad(asfloat(0x3e886595u), asfloat(v23), asfloat(v12)));
                                          v13 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v23), asfloat(v13)));
                                          v18 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v23), asfloat(v18)));
                                          v17 = asuint(mad(asfloat(0x3e886595u), asfloat(v22), asfloat(v17)));
                                          v16 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v16)));
                                          v12 = asuint(mad(asfloat(0xbe25460bu), asfloat(v11), asfloat(v12)));
                                          v13 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v11), asfloat(v13)));
                                          v18 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v11), asfloat(v18)));
                                          v15 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v22), asfloat(v15)));
                                          v17 = asuint(mad(asfloat(0xbe25460bu), asfloat(v14), asfloat(v17)));
                                          v11 = asuint((1.0f / asfloat(v12)));
                                          v16 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v22), asfloat(v16)));
                                          vcc_lo = (asfloat(0x45cb2000u) == asfloat(s18) ? 0xffffffffu : 0u);
                                          v15 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v14), asfloat(v15)));
                                          v12 = asuint((1.0f / asfloat(v13)));
                                          v16 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v14), asfloat(v16)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v11 = asuint(LegacyMul(asfloat(v17), asfloat(v11)));
                                          v13 = asuint((1.0f / asfloat(v18)));
                                          v12 = asuint(LegacyMul(asfloat(v15), asfloat(v12)));
                                          v14 = asuint(LegacyMul(asfloat(0x3f622a43u), asfloat(v11)));
                                          v15 = asuint(LegacyMul(asfloat(0x3e869f68u), asfloat(v11)));
                                          v17 = asuint(LegacyMul(asfloat(0x3ec61f29u), asfloat(v11)));
                                          v18 = asuint(LegacyMul(asfloat(0x3debdc39u), asfloat(v11)));
                                          v19 = asuint(LegacyMul(asfloat(0xbd8ee5b6u), asfloat(v11)));
                                          v13 = asuint(LegacyMul(asfloat(v16), asfloat(v13)));
                                          v14 = asuint(mad(asfloat(0x3de1ef85u), asfloat(v12), asfloat(v14)));
                                          v16 = asuint(LegacyMul(asfloat(0xbe231fb6u), asfloat(v11)));
                                          v20 = asuint(LegacyMul(asfloat(0xbbfa26f7u), asfloat(v11)));
                                          v21 = asuint(LegacyMul(asfloat(0xbb14e69au), asfloat(v11)));
                                          v15 = asuint(mad(asfloat(0xbe810333u), asfloat(v12), asfloat(v15)));
                                          v17 = asuint(mad(asfloat(0xbec71a7bu), asfloat(v12), asfloat(v17)));
                                          v14 = asuint(mad(asfloat(0x3bcbe686u), asfloat(v13), asfloat(v14)));
                                          v11 = asuint(LegacyMul(asfloat(0x3ab46cc2u), asfloat(v11)));
                                          v16 = asuint(mad(asfloat(0xbbb0d86bu), asfloat(v12), asfloat(v16)));
                                          v18 = asuint(mad(asfloat(0x3f6361c2u), asfloat(v12), asfloat(v18)));
                                          v19 = asuint(mad(asfloat(0x3c9bd7d9u), asfloat(v12), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0xbcf616abu), asfloat(v12), asfloat(v20)));
                                          v21 = asuint(mad(asfloat(0x3d8c8526u), asfloat(v12), asfloat(v21)));
                                          v11 = asuint(mad(asfloat(0x3ac09e9cu), asfloat(v12), asfloat(v11)));
                                          v15 = asuint(mad(asfloat(0xbc3386dau), asfloat(v13), asfloat(v15)));
                                          v22 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v14)));
                                          v23 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v14)));
                                          v12 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v14)));
                                          v17 = asuint(mad(asfloat(0x3afb5232u), asfloat(v13), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(0x3d1a5031u), asfloat(v13), asfloat(v20)));
                                          v16 = asuint(mad(asfloat(0x3e28a67du), asfloat(v13), asfloat(v16)));
                                          v22 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v15), asfloat(v22)));
                                          v23 = asuint(mad(asfloat(0x3f371437u), asfloat(v15), asfloat(v23)));
                                          v12 = asuint(mad(asfloat(0x3d93d07du), asfloat(v15), asfloat(v12)));
                                          v18 = asuint(mad(asfloat(0xbb5d4762u), asfloat(v13), asfloat(v18)));
                                          v14 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v17)));
                                          v15 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v17)));
                                          v19 = asuint(mad(asfloat(0x3d4fdf77u), asfloat(v13), asfloat(v19)));
                                          v21 = asuint(mad(asfloat(0xbd87ddf8u), asfloat(v13), asfloat(v21)));
                                          v11 = asuint(mad(asfloat(0x3f7f457bu), asfloat(v13), asfloat(v11)));
                                          v13 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v17)));
                                          v14 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v18), asfloat(v14)));
                                          v24 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v20)));
                                          v15 = asuint(mad(asfloat(0x3d93d07du), asfloat(v18), asfloat(v15)));
                                          v22 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v16), asfloat(v22)));
                                          v13 = asuint(mad(asfloat(0x3f371437u), asfloat(v18), asfloat(v13)));
                                          v25 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v20)));
                                          v24 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v21), asfloat(v24)));
                                          v20 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v20)));
                                          v23 = asuint(mad(asfloat(0x3df41aefu), asfloat(v16), asfloat(v23)));
                                          v14 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v19), asfloat(v14)));
                                          v26 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v22)));
                                          v13 = asuint(mad(asfloat(0x3df41aefu), asfloat(v19), asfloat(v13)));
                                          v15 = asuint(mad(asfloat(0x3f734721u), asfloat(v19), asfloat(v15)));
                                          v19 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v22)));
                                          v22 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v22)));
                                          v12 = asuint(mad(asfloat(0x3f734721u), asfloat(v16), asfloat(v12)));
                                          v25 = asuint(mad(asfloat(0x3f371437u), asfloat(v21), asfloat(v25)));
                                          v20 = asuint(mad(asfloat(0x3d93d07du), asfloat(v21), asfloat(v20)));
                                          v21 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v23)));
                                          v27 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v23)));
                                          v23 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v23)));
                                          v24 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v11), asfloat(v24)));
                                          v19 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v14), asfloat(v19)));
                                          v26 = asuint(mad(asfloat(0x3ff02120u), asfloat(v14), asfloat(v26)));
                                          v22 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v14), asfloat(v22)));
                                          v16 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v12)));
                                          v17 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v12)));
                                          v18 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v12)));
                                          v26 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v24), asfloat(v26)));
                                          v25 = asuint(mad(asfloat(0x3df41aefu), asfloat(v11), asfloat(v25)));
                                          v21 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v13), asfloat(v21)));
                                          v27 = asuint(mad(asfloat(0x3ff02120u), asfloat(v13), asfloat(v27)));
                                          v23 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v13), asfloat(v23)));
                                          v22 = asuint(mad(asfloat(0x3f875327u), asfloat(v24), asfloat(v22)));
                                          v19 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v24), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0x3f734721u), asfloat(v11), asfloat(v20)));
                                          v16 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v15), asfloat(v16)));
                                          v17 = asuint(mad(asfloat(0x3ff02120u), asfloat(v15), asfloat(v17)));
                                          v18 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v15), asfloat(v18)));
                                          v21 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v25), asfloat(v21)));
                                          v11 = asuint(LegacyMul(asfloat(v19), asfloat(v4)));
                                          v27 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v25), asfloat(v27)));
                                          v12 = asuint(LegacyMul(asfloat(v26), asfloat(v4)));
                                          v23 = asuint(mad(asfloat(0x3f875327u), asfloat(v25), asfloat(v23)));
                                          v13 = asuint(LegacyMul(asfloat(v22), asfloat(v4)));
                                          v16 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v20), asfloat(v16)));
                                          v17 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v20), asfloat(v17)));
                                          v18 = asuint(mad(asfloat(0x3f875327u), asfloat(v20), asfloat(v18)));
                                          v11 = asuint(mad(asfloat(v21), asfloat(v3), asfloat(v11)));
                                          v12 = asuint(mad(asfloat(v27), asfloat(v3), asfloat(v12)));
                                          v13 = asuint(mad(asfloat(v23), asfloat(v3), asfloat(v13)));
                                          pc = vcc_lo != 0u ? 0x2100u : 0x20a8u;
                                      }
                                      if (pc == 0x20a8u)
                                      {
                                          v14 = asuint((1.0f / asfloat(s18)));
                                          v19 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s18)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v19) ? 0xffffffffu : 0u);
                                          v14 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v14)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v15 = asuint(LegacyMul(asfloat(v14), asfloat(v14)));
                                          v19 = asuint(LegacyMul(asfloat(v15), asfloat(v14)));
                                          pc = vcc_lo == 0u ? 0x2114u : 0x20d8u;
                                      }
                                      if (pc == 0x20d8u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v14 = asuint(mad(asfloat(v14), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v14 = asuint(mad(asfloat(0x49e82740u), asfloat(v15), asfloat(v14)));
                                          v14 = asuint(mad(asfloat(0xceef2e78u), asfloat(v19), asfloat(v14)));
                                          pc = 0x2138u;
                                      }
                                      if (pc == 0x2100u)
                                      {
                                          v14 = 0x3ea01cd6u;
                                          v15 = 0x3ea8769fu;
                                          pc = 0x2218u;
                                      }
                                      if (pc == 0x2114u)
                                      {
                                          s0 = 0x42c63852u;
                                          v14 = asuint(mad(asfloat(v14), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v14 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v15), asfloat(v14)));
                                          v14 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v19), asfloat(v14)));
                                          pc = 0x2138u;
                                      }
                                      if (pc == 0x2138u)
                                      {
                                          v15 = 0x3a5cd5ddu;
                                          v19 = 0x39219aceu;
                                          v20 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s18), 1.0f));
                                          v21 = asuint(LegacyMul(asfloat(s18), asfloat(s18)));
                                          v22 = 0x3831567au;
                                          v15 = asuint(mad(asfloat(0x353e175du), asfloat(s18), asfloat(v15)));
                                          v19 = asuint(mad(asfloat(0x340a20a1u), asfloat(s18), asfloat(v19)));
                                          s0 = 0x4037ae14u;
                                          v20 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v21), asfloat(v20)));
                                          v21 = asuint(mad(asfloat(s18), asfloat(0x33349873u), asfloat(v22)));
                                          v15 = asuint(mad(asfloat(v15), asfloat(s18), 1.0f));
                                          v22 = asuint(mad(asfloat(s18), asfloat(v19), asfloat(0x3f5c30adu)));
                                          vcc_lo = (asfloat(0x457a0000u) <= asfloat(s18) ? 0xffffffffu : 0u);
                                          v20 = asuint((1.0f / asfloat(v20)));
                                          v21 = asuint(mad(asfloat(s18), asfloat(v21), asfloat(0x3ea28216u)));
                                          v15 = asuint((1.0f / asfloat(v15)));
                                          v19 = asuint(LegacyMul(asfloat(v21), asfloat(v20)));
                                          v21 = asuint(LegacyMul(asfloat(v14), asfloat(v14)));
                                          v15 = asuint(LegacyMul(asfloat(v22), asfloat(v15)));
                                          v22 = asuint(mad(asfloat(v14), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          v20 = asuint(mad(asfloat(v15), 2.0f, 4.0f));
                                          v15 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v15)));
                                          v22 = asuint(mad(asfloat(0xc0400000u), asfloat(v21), asfloat(v22)));
                                          v20 = asuint(mad(asfloat(0xc1000000u), asfloat(v19), asfloat(v20)));
                                          v20 = asuint((1.0f / asfloat(v20)));
                                          v23 = asuint((asfloat(v20) + asfloat(v20)));
                                          v15 = asuint(LegacyMul(asfloat(v15), asfloat(v20)));
                                          v19 = asuint(LegacyMul(asfloat(v19), asfloat(v23)));
                                          v14 = (vcc_lo != 0u ? v14 : v15);
                                          v15 = (vcc_lo != 0u ? v22 : v19);
                                          pc = 0x2218u;
                                      }
                                      if (pc == 0x2218u)
                                      {
                                          v19 = asuint((1.0f / asfloat(s17)));
                                          v21 = asuint(LegacyMul(asfloat(0x3f80123bu), asfloat(s17)));
                                          v11 = asuint(mad(asfloat(v16), asfloat(v2), asfloat(v11)));
                                          v12 = asuint(mad(asfloat(v17), asfloat(v2), asfloat(v12)));
                                          v13 = asuint(mad(asfloat(v18), asfloat(v2), asfloat(v13)));
                                          vcc_lo = (asfloat(0x45dac000u) < asfloat(v21) ? 0xffffffffu : 0u);
                                          v19 = asuint(LegacyMul(asfloat(0x3f7fdb8fu), asfloat(v19)));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          v22 = asuint(LegacyMul(asfloat(v19), asfloat(v19)));
                                          v17 = asuint(LegacyMul(asfloat(v22), asfloat(v19)));
                                          pc = vcc_lo == 0u ? 0x227cu : 0x2254u;
                                      }
                                      if (pc == 0x2254u)
                                      {
                                          s0 = 0x43777ae1u;
                                          v16 = asuint(mad(asfloat(v19), asfloat(s0), asfloat(0x3e72ba9du)));
                                          v16 = asuint(mad(asfloat(0x49e82740u), asfloat(v22), asfloat(v16)));
                                          v16 = asuint(mad(asfloat(0xceef2e78u), asfloat(v17), asfloat(v16)));
                                          pc = 0x22a0u;
                                      }
                                      if (pc == 0x227cu)
                                      {
                                          s0 = 0x42c63852u;
                                          v16 = asuint(mad(asfloat(v19), asfloat(s0), asfloat(0x3e79eba7u)));
                                          v16 = asuint(mad(asfloat(0x4a3523e0u), asfloat(v22), asfloat(v16)));
                                          v16 = asuint(mad(asfloat(0xcf894c9fu), asfloat(v17), asfloat(v16)));
                                          pc = 0x22a0u;
                                      }
                                      if (pc == 0x22a0u)
                                      {
                                          v17 = 0x3a5cd5ddu;
                                          v19 = 0x39219aceu;
                                          v21 = 0x3831567au;
                                          v22 = asuint(LegacyMul(asfloat(s17), asfloat(s17)));
                                          v23 = asuint(mad(asfloat(0xb7f30d95u), asfloat(s17), 1.0f));
                                          v18 = asuint(mad(asfloat(0x353e175du), asfloat(s17), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(0x340a20a1u), asfloat(s17), asfloat(v19)));
                                          v24 = 0xb7f30d95u;
                                          v17 = asuint(mad(asfloat(0x35be175du), asfloat(s17), asfloat(v17)));
                                          v23 = asuint(mad(asfloat(0x342d5cb4u), asfloat(v22), asfloat(v23)));
                                          v18 = asuint(mad(asfloat(v18), asfloat(s17), 1.0f));
                                          v22 = asuint(mad(asfloat(0x33349873u), asfloat(s17), asfloat(v21)));
                                          v20 = asuint(mad(asfloat(s17), asfloat(v20), asfloat(0x3f5c30adu)));
                                          v24 = asuint(mad(asfloat(0x34ad5cb4u), asfloat(s17), asfloat(v24)));
                                          v26 = asuint(LegacyMul(asfloat(v23), asfloat(v23)));
                                          v25 = asuint((1.0f / asfloat(v18)));
                                          v22 = asuint(mad(asfloat(s17), asfloat(v22), asfloat(0x3ea28216u)));
                                          v27 = asuint(LegacyMul(asfloat(v20), asfloat(v17)));
                                          v21 = asuint(mad(asfloat(0x33b49873u), asfloat(s17), asfloat(v21)));
                                          v19 = asuint(mad(asfloat(0x348a20a1u), asfloat(s17), asfloat(v19)));
                                          v26 = asuint((1.0f / asfloat(v26)));
                                          s0 = 0x4037ae14u;
                                          v19 = asuint(mad(asfloat(v18), asfloat(v19), -asfloat(v27)));
                                          v17 = asuint(LegacyMul(asfloat(v20), asfloat(v25)));
                                          v20 = asuint(LegacyMul(asfloat(v22), asfloat(v24)));
                                          v24 = asuint(LegacyMul(asfloat(v18), asfloat(v18)));
                                          v25 = asuint(LegacyMul(asfloat(v16), asfloat(v16)));
                                          v18 = asuint(mad(asfloat(v17), 2.0f, 4.0f));
                                          v20 = asuint(mad(asfloat(v23), asfloat(v21), -asfloat(v20)));
                                          v21 = asuint((1.0f / asfloat(v23)));
                                          v23 = asuint(LegacyMul(asfloat(0x3d4ccccdu), asfloat(s19)));
                                          v20 = asuint(LegacyMul(asfloat(v20), asfloat(v26)));
                                          v24 = asuint((1.0f / asfloat(v24)));
                                          v21 = asuint(LegacyMul(asfloat(v22), asfloat(v21)));
                                          v22 = asuint(LegacyMul(asfloat(v20), asfloat(v20)));
                                          v20 = asuint(LegacyMul(asfloat(v23), asfloat(v20)));
                                          v18 = asuint(mad(asfloat(0xc1000000u), asfloat(v21), asfloat(v18)));
                                          v19 = asuint(LegacyMul(asfloat(v19), asfloat(v24)));
                                          v18 = asuint((1.0f / asfloat(v18)));
                                          v22 = asuint(mad(asfloat(v19), asfloat(v19), asfloat(v22)));
                                          v19 = asuint(LegacyMul(asfloat(v23), asfloat(v19)));
                                          v22 = asuint(rsqrt(asfloat(v22)));
                                          v24 = asuint((asfloat(v18) + asfloat(v18)));
                                          v24 = asuint(LegacyMul(asfloat(v21), asfloat(v24)));
                                          v21 = asuint(mad(-asfloat(v19), asfloat(v22), asfloat(v21)));
                                          v22 = asuint(mad(asfloat(v20), asfloat(v22), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(v16), asfloat(s0), asfloat(0xbe8ccccdu)));
                                          s0 = (asfloat(0x457a0000u) <= asfloat(s17) ? 0xffffffffu : 0u);
                                          s1 = s0;
                                          v19 = asuint(mad(asfloat(0xc1000000u), asfloat(v21), 4.0f));
                                          v20 = asuint(mad(asfloat(0xc0400000u), asfloat(v25), asfloat(v20)));
                                          v19 = asuint(mad(2.0f, asfloat(v22), asfloat(v19)));
                                          v23 = asuint((1.0f / asfloat(v19)));
                                          v19 = (s0 != 0u ? v20 : v24);
                                          v20 = asuint((asfloat(v23) + asfloat(v23)));
                                          v20 = asuint(mad(asfloat(v20), asfloat(v21), -asfloat(v24)));
                                          v21 = ((uint)(0));
                                          v24 = asuint((asfloat(v20) + asfloat(v19)));
                                          v20 = ((uint)(0));
                                          v19 = ((uint)(0));
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v24) ? 0xffffffffu : 0u);
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x2488u : 0x2448u;
                                      }
                                      if (pc == 0x2448u)
                                      {
                                          v17 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v17)));
                                          v19 = asuint(LegacyMul(asfloat(0x40400000u), asfloat(v23)));
                                          v20 = asuint(1.0f);
                                          v17 = asuint(LegacyMul(asfloat(v17), asfloat(v18)));
                                          v16 = (s0 != 0u ? v16 : v17);
                                          v17 = asuint(mad(asfloat(v19), asfloat(v22), -asfloat(v17)));
                                          v16 = asuint((asfloat(v17) + asfloat(v16)));
                                          v17 = asuint((1.0f / asfloat(v24)));
                                          v18 = asuint((1.0f - asfloat(v16)));
                                          v18 = asuint((asfloat(v18) - asfloat(v24)));
                                          v21 = asuint(LegacyMul(asfloat(v16), asfloat(v17)));
                                          v19 = asuint(LegacyMul(asfloat(v18), asfloat(v17)));
                                          pc = 0x2488u;
                                      }
                                      if (pc == 0x2488u)
                                      {
                                          vcc_lo = (asfloat(0x358637bdu) > asfloat(v15) ? 0xffffffffu : 0u);
                                          v16 = ((uint)(0));
                                          v17 = ((uint)(0));
                                          v18 = ((uint)(0));
                                          vcc_lo = vcc_lo;
                                          scc = (vcc_lo != 0u ? 1u : 0u);
                                          pc = vcc_lo != 0u ? 0x24bcu : 0x24a4u;
                                      }
                                      if (pc == 0x24a4u)
                                      {
                                          v16 = asuint((1.0f / asfloat(v15)));
                                          v17 = asuint((1.0f - asfloat(v14)));
                                          v15 = asuint((asfloat(v17) - asfloat(v15)));
                                          v17 = asuint(1.0f);
                                          v18 = asuint(LegacyMul(asfloat(v14), asfloat(v16)));
                                          v16 = asuint(LegacyMul(asfloat(v15), asfloat(v16)));
                                          pc = 0x24bcu;
                                      }
                                      if (pc == 0x24bcu)
                                      {
                                          v14 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v21)));
                                          v22 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v21)));
                                          v21 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v21)));
                                          v23 = asuint(LegacyMul(asfloat(0x3f652546u), asfloat(v18)));
                                          v14 = asuint(mad(asfloat(0x3e886595u), asfloat(v20), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v20), asfloat(v22)));
                                          v21 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v20), asfloat(v21)));
                                          v23 = asuint(mad(asfloat(0x3e886595u), asfloat(v17), asfloat(v23)));
                                          v14 = asuint(mad(asfloat(0xbe25460bu), asfloat(v19), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v19), asfloat(v22)));
                                          v21 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v19), asfloat(v21)));
                                          v19 = asuint(LegacyMul(asfloat(0xbf400d1bu), asfloat(v18)));
                                          v18 = asuint(LegacyMul(asfloat(0x3d1f559bu), asfloat(v18)));
                                          v14 = asuint((1.0f / asfloat(v14)));
                                          v23 = asuint(mad(asfloat(0xbe25460bu), asfloat(v16), asfloat(v23)));
                                          v19 = asuint(mad(asfloat(0x3fdb53f8u), asfloat(v17), asfloat(v19)));
                                          v18 = asuint(mad(asfloat(0xbd8c49bau), asfloat(v17), asfloat(v18)));
                                          v22 = asuint((1.0f / asfloat(v22)));
                                          v19 = asuint(mad(asfloat(0x3d1652bdu), asfloat(v16), asfloat(v19)));
                                          v18 = asuint(mad(asfloat(0x3f83c9efu), asfloat(v16), asfloat(v18)));
                                          v17 = asuint((1.0f / asfloat(v21)));
                                          v14 = asuint(LegacyMul(asfloat(v23), asfloat(v14)));
                                          v15 = asuint(LegacyMul(asfloat(v19), asfloat(v22)));
                                          v19 = asuint(LegacyMul(asfloat(0xbe231fb6u), asfloat(v14)));
                                          v20 = asuint(LegacyMul(asfloat(0x3ec61f29u), asfloat(v14)));
                                          v21 = asuint(LegacyMul(asfloat(0x3debdc39u), asfloat(v14)));
                                          v22 = asuint(LegacyMul(asfloat(0xbd8ee5b6u), asfloat(v14)));
                                          v16 = asuint(LegacyMul(asfloat(v18), asfloat(v17)));
                                          v17 = asuint(LegacyMul(asfloat(0x3f622a43u), asfloat(v14)));
                                          v18 = asuint(LegacyMul(asfloat(0x3e869f68u), asfloat(v14)));
                                          v23 = asuint(LegacyMul(asfloat(0xbbfa26f7u), asfloat(v14)));
                                          v24 = asuint(LegacyMul(asfloat(0xbb14e69au), asfloat(v14)));
                                          v14 = asuint(LegacyMul(asfloat(0x3ab46cc2u), asfloat(v14)));
                                          v17 = asuint(mad(asfloat(0x3de1ef85u), asfloat(v15), asfloat(v17)));
                                          v18 = asuint(mad(asfloat(0xbe810333u), asfloat(v15), asfloat(v18)));
                                          v19 = asuint(mad(asfloat(0xbbb0d86bu), asfloat(v15), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0xbec71a7bu), asfloat(v15), asfloat(v20)));
                                          v21 = asuint(mad(asfloat(0x3f6361c2u), asfloat(v15), asfloat(v21)));
                                          v17 = asuint(mad(asfloat(0x3bcbe686u), asfloat(v16), asfloat(v17)));
                                          v22 = asuint(mad(asfloat(0x3c9bd7d9u), asfloat(v15), asfloat(v22)));
                                          v23 = asuint(mad(asfloat(0xbcf616abu), asfloat(v15), asfloat(v23)));
                                          v24 = asuint(mad(asfloat(0x3d8c8526u), asfloat(v15), asfloat(v24)));
                                          v14 = asuint(mad(asfloat(0x3ac09e9cu), asfloat(v15), asfloat(v14)));
                                          v18 = asuint(mad(asfloat(0xbc3386dau), asfloat(v16), asfloat(v18)));
                                          v15 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v17)));
                                          v25 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v17)));
                                          v17 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v17)));
                                          v19 = asuint(mad(asfloat(0x3e28a67du), asfloat(v16), asfloat(v19)));
                                          v20 = asuint(mad(asfloat(0x3afb5232u), asfloat(v16), asfloat(v20)));
                                          v15 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v18), asfloat(v15)));
                                          v25 = asuint(mad(asfloat(0x3f371437u), asfloat(v18), asfloat(v25)));
                                          v17 = asuint(mad(asfloat(0x3d93d07du), asfloat(v18), asfloat(v17)));
                                          v21 = asuint(mad(asfloat(0xbb5d4762u), asfloat(v16), asfloat(v21)));
                                          v18 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v20)));
                                          v22 = asuint(mad(asfloat(0x3d4fdf77u), asfloat(v16), asfloat(v22)));
                                          v23 = asuint(mad(asfloat(0x3d1a5031u), asfloat(v16), asfloat(v23)));
                                          v24 = asuint(mad(asfloat(0xbd87ddf8u), asfloat(v16), asfloat(v24)));
                                          v14 = asuint(mad(asfloat(0x3f7f457bu), asfloat(v16), asfloat(v14)));
                                          v16 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v20)));
                                          v15 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v19), asfloat(v15)));
                                          v25 = asuint(mad(asfloat(0x3df41aefu), asfloat(v19), asfloat(v25)));
                                          v17 = asuint(mad(asfloat(0x3f734721u), asfloat(v19), asfloat(v17)));
                                          v19 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v20)));
                                          v16 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v21), asfloat(v16)));
                                          v18 = asuint(mad(asfloat(0x3f371437u), asfloat(v21), asfloat(v18)));
                                          v20 = asuint(LegacyMul(asfloat(0x3ed32d7cu), asfloat(v23)));
                                          v19 = asuint(mad(asfloat(0x3d93d07du), asfloat(v21), asfloat(v19)));
                                          v21 = asuint(LegacyMul(asfloat(0x3eb71437u), asfloat(v23)));
                                          v23 = asuint(LegacyMul(asfloat(0x3e38c49cu), asfloat(v23)));
                                          v20 = asuint(mad(asfloat(0x3e59c6edu), asfloat(v24), asfloat(v20)));
                                          v16 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v22), asfloat(v16)));
                                          v18 = asuint(mad(asfloat(0x3df41aefu), asfloat(v22), asfloat(v18)));
                                          v21 = asuint(mad(asfloat(0x3f371437u), asfloat(v24), asfloat(v21)));
                                          v23 = asuint(mad(asfloat(0x3d93d07du), asfloat(v24), asfloat(v23)));
                                          v19 = asuint(mad(asfloat(0x3f734721u), asfloat(v22), asfloat(v19)));
                                          v22 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v15)));
                                          v20 = asuint(mad(asfloat(0x3c9e6221u), asfloat(v14), asfloat(v20)));
                                          v21 = asuint(mad(asfloat(0x3df41aefu), asfloat(v14), asfloat(v21)));
                                          v23 = asuint(mad(asfloat(0x3f734721u), asfloat(v14), asfloat(v23)));
                                          v14 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v15)));
                                          v15 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v15)));
                                          v22 = asuint(mad(asfloat(0x3ff02120u), asfloat(v16), asfloat(v22)));
                                          v24 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v25)));
                                          v14 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v16), asfloat(v14)));
                                          v15 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v16), asfloat(v15)));
                                          v16 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v25)));
                                          v25 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v25)));
                                          v24 = asuint(mad(asfloat(0x3ff02120u), asfloat(v18), asfloat(v24)));
                                          v14 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v20), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v20), asfloat(v22)));
                                          v16 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v18), asfloat(v16)));
                                          v25 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v18), asfloat(v25)));
                                          v15 = asuint(mad(asfloat(0x3f875327u), asfloat(v20), asfloat(v15)));
                                          v20 = asuint(LegacyMul(asfloat(0xbf7821d8u), asfloat(v17)));
                                          v24 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v21), asfloat(v24)));
                                          v16 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v21), asfloat(v16)));
                                          v25 = asuint(mad(asfloat(0x3f875327u), asfloat(v21), asfloat(v25)));
                                          v21 = asuint(LegacyMul(asfloat(0x404f639du), asfloat(v17)));
                                          v17 = asuint(LegacyMul(asfloat(0x3d63ea5bu), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(0x3ff02120u), asfloat(v19), asfloat(v20)));
                                          v14 = asuint(LegacyMul(asfloat(v14), asfloat(v4)));
                                          v22 = asuint(LegacyMul(asfloat(v22), asfloat(v4)));
                                          v15 = asuint(LegacyMul(asfloat(v15), asfloat(v4)));
                                          v21 = asuint(mad(asfloat(0xbfc4c0f7u), asfloat(v19), asfloat(v21)));
                                          v17 = asuint(mad(asfloat(0xbe50ec27u), asfloat(v19), asfloat(v17)));
                                          v20 = asuint(mad(asfloat(0x3d2a36b6u), asfloat(v23), asfloat(v20)));
                                          v22 = asuint(mad(asfloat(v24), asfloat(v3), asfloat(v22)));
                                          v14 = asuint(mad(asfloat(v16), asfloat(v3), asfloat(v14)));
                                          v21 = asuint(mad(asfloat(0xbeff3f87u), asfloat(v23), asfloat(v21)));
                                          v15 = asuint(mad(asfloat(v25), asfloat(v3), asfloat(v15)));
                                          v17 = asuint(mad(asfloat(0x3f875327u), asfloat(v23), asfloat(v17)));
                                          v4 = asuint(LegacyMul(asfloat(v8), asfloat(v7)));
                                          v3 = asuint(LegacyMul(asfloat(v9), asfloat(v7)));
                                          v14 = asuint(mad(asfloat(v21), asfloat(v2), asfloat(v14)));
                                          v22 = asuint(mad(asfloat(v20), asfloat(v2), asfloat(v22)));
                                          v15 = asuint(mad(asfloat(v17), asfloat(v2), asfloat(v15)));
                                          v2 = asuint(LegacyMul(asfloat(v10), asfloat(v7)));
                                          v4 = asuint(mad(asfloat(v11), asfloat(v6), asfloat(v4)));
                                          v3 = asuint(mad(asfloat(v12), asfloat(v6), asfloat(v3)));
                                          v2 = asuint(mad(asfloat(v13), asfloat(v6), asfloat(v2)));
                                          v4 = asuint(mad(asfloat(v14), asfloat(v5), asfloat(v4)));
                                          v3 = asuint(mad(asfloat(v22), asfloat(v5), asfloat(v3)));
                                          v2 = asuint(mad(asfloat(v15), asfloat(v5), asfloat(v2)));
                                          pc = 0x2838u;
                                      }
                                      if (pc == 0x2838u)
                                      {
                                          s4 = Large[114][2];
                                          s5 = Large[114][3];
                                          scc = (s5 == ((uint)(0)) ? 1u : 0u);
                                          pc = scc != 0u ? 0x2c78u : 0x284cu;
                                      }
                                      if (pc == 0x284cu)
                                      {
                                          s12 = Large[115][0];
                                          s13 = Large[115][1];
                                          s14 = Large[115][2];
                                          s15 = Large[115][3];
                                          s16 = Large[116][0];
                                          s17 = Large[116][1];
                                          s18 = Large[116][2];
                                          s19 = Large[116][3];
                                          s20 = Large[117][0];
                                          s21 = Large[117][1];
                                          s22 = Large[117][2];
                                          s23 = Large[117][3];
                                          s24 = Large[118][0];
                                          s25 = Large[118][1];
                                          s26 = Large[118][2];
                                          s27 = Large[118][3];
                                          v5 = asuint(LegacyMul(asfloat(0x3f371759u), asfloat(v3)));
                                          s5 = Large[120][0];
                                          s0 = Large[119][0];
                                          s1 = Large[119][1];
                                          s2 = Large[119][2];
                                          s3 = Large[119][3];
                                          v5 = asuint(mad(asfloat(0x3e59b3d0u), asfloat(v4), asfloat(v5)));
                                          v5 = asuint(mad(asfloat(0x3d93dd98u), asfloat(v2), asfloat(v5)));
                                          v4 = asuint((asfloat(v4) - asfloat(v5)));
                                          v16 = asuint((asfloat(v3) - asfloat(v5)));
                                          v2 = asuint((asfloat(v2) - asfloat(v5)));
                                          v6 = asuint(LegacyMul(asfloat(s24), asfloat(s27)));
                                          v7 = asuint(LegacyMul(asfloat(s25), asfloat(s27)));
                                          v9 = asuint((1.0f / asfloat(s5)));
                                          v8 = asuint(LegacyMul(asfloat(s26), asfloat(s27)));
                                          v10 = asuint(LegacyMul(asfloat(s0), asfloat(s3)));
                                          v6 = asuint(mad(asfloat(v6), asfloat(v4), asfloat(v5)));
                                          v7 = asuint(mad(asfloat(v7), asfloat(v16), asfloat(v5)));
                                          v11 = asuint(LegacyMul(asfloat(s1), asfloat(s3)));
                                          v8 = asuint(mad(asfloat(v8), asfloat(v2), asfloat(v5)));
                                          v12 = asuint(LegacyMul(asfloat(s2), asfloat(s3)));
                                          v6 = asuint(max(0.0f, asfloat(v6)));
                                          v7 = asuint(max(0.0f, asfloat(v7)));
                                          s24 = Large[121][0];
                                          s25 = Large[121][1];
                                          s26 = Large[121][2];
                                          s27 = Large[121][3];
                                          s28 = Large[122][0];
                                          s29 = Large[122][1];
                                          s30 = Large[122][2];
                                          s31 = Large[122][3];
                                          s32 = Large[123][0];
                                          s33 = Large[123][1];
                                          s34 = Large[123][2];
                                          s35 = Large[123][3];
                                          s36 = Large[124][0];
                                          s37 = Large[124][1];
                                          s38 = Large[124][2];
                                          s39 = Large[124][3];
                                          v8 = asuint(max(0.0f, asfloat(v8)));
                                          s0 = Large[125][0];
                                          s1 = Large[125][1];
                                          s2 = Large[125][2];
                                          s3 = Large[125][3];
                                          v6 = asuint(LegacyMul(asfloat(v9), asfloat(v6)));
                                          v7 = asuint(LegacyMul(asfloat(v9), asfloat(v7)));
                                          v8 = asuint(LegacyMul(asfloat(v9), asfloat(v8)));
                                          v9 = asuint(LegacyMul(asfloat(s16), asfloat(s19)));
                                          v15 = asuint(log2(asfloat(v6)));
                                          v9 = asuint((1.0f / asfloat(v9)));
                                          v6 = asuint(LegacyMul(asfloat(v10), asfloat(v15)));
                                          v10 = asuint(log2(asfloat(v7)));
                                          v3 = asuint(LegacyMul(asfloat(s36), asfloat(s39)));
                                          v13 = asuint(LegacyMul(asfloat(s0), asfloat(s3)));
                                          v14 = asuint(LegacyMul(asfloat(s1), asfloat(s3)));
                                          v6 = asuint(exp2(asfloat(v6)));
                                          v15 = asuint(LegacyMul(asfloat(s2), asfloat(s3)));
                                          s0 = Large[131][0];
                                          s1 = Large[131][1];
                                          s2 = Large[131][2];
                                          s3 = Large[131][3];
                                          v7 = asuint(LegacyMul(asfloat(v11), asfloat(v10)));
                                          v11 = asuint(log2(asfloat(v8)));
                                          v10 = asuint(LegacyMul(asfloat(s17), asfloat(s19)));
                                          v6 = asuint(mad(asfloat(s5), asfloat(v6), asfloat(s15)));
                                          v7 = asuint(exp2(asfloat(v7)));
                                          v6 = asuint((asfloat(s12) + asfloat(v6)));
                                          v8 = asuint(LegacyMul(asfloat(v12), asfloat(v11)));
                                          v10 = asuint((1.0f / asfloat(v10)));
                                          v11 = asuint(LegacyMul(asfloat(s18), asfloat(s19)));
                                          v7 = asuint(mad(asfloat(s5), asfloat(v7), asfloat(s15)));
                                          v8 = asuint(exp2(asfloat(v8)));
                                          v7 = asuint((asfloat(s13) + asfloat(v7)));
                                          v6 = asuint(log2(asfloat(v6)));
                                          v8 = asuint(mad(asfloat(s5), asfloat(v8), asfloat(s15)));
                                          s5 = Large[126][0];
                                          v7 = asuint(log2(asfloat(v7)));
                                          v6 = asuint(LegacyMul(asfloat(v6), asfloat(v9)));
                                          v8 = asuint((asfloat(s14) + asfloat(v8)));
                                          v9 = asuint(mad(asfloat(v3), asfloat(v4), asfloat(v5)));
                                          v11 = asuint((1.0f / asfloat(v11)));
                                          v7 = asuint(LegacyMul(asfloat(v7), asfloat(v10)));
                                          v8 = asuint(log2(asfloat(v8)));
                                          v10 = asuint(LegacyMul(asfloat(s37), asfloat(s39)));
                                          v9 = asuint(max(0.0f, asfloat(v9)));
                                          v6 = asuint(exp2(asfloat(v6)));
                                          v10 = asuint(mad(asfloat(v10), asfloat(v16), asfloat(v5)));
                                          v12 = asuint((1.0f / asfloat(s5)));
                                          v8 = asuint(LegacyMul(asfloat(v8), asfloat(v11)));
                                          v11 = asuint(LegacyMul(asfloat(s38), asfloat(s39)));
                                          v10 = asuint(max(0.0f, asfloat(v10)));
                                          s36 = Large[127][0];
                                          s37 = Large[127][1];
                                          s38 = Large[127][2];
                                          s39 = Large[127][3];
                                          s40 = Large[128][0];
                                          s41 = Large[128][1];
                                          s42 = Large[128][2];
                                          s43 = Large[128][3];
                                          s44 = Large[129][0];
                                          s45 = Large[129][1];
                                          s46 = Large[129][2];
                                          s47 = Large[129][3];
                                          s48 = Large[130][0];
                                          s49 = Large[130][1];
                                          s50 = Large[130][2];
                                          s51 = Large[130][3];
                                          v7 = asuint(exp2(asfloat(v7)));
                                          v11 = asuint(mad(asfloat(v11), asfloat(v2), asfloat(v5)));
                                          v9 = asuint(LegacyMul(asfloat(v12), asfloat(v9)));
                                          v10 = asuint(LegacyMul(asfloat(v12), asfloat(v10)));
                                          v11 = asuint(max(0.0f, asfloat(v11)));
                                          v8 = asuint(exp2(asfloat(v8)));
                                          v11 = asuint(LegacyMul(asfloat(v12), asfloat(v11)));
                                          v19 = asuint(log2(asfloat(v9)));
                                          v12 = asuint(LegacyMul(asfloat(s28), asfloat(s31)));
                                          v3 = asuint(LegacyMul(asfloat(s48), asfloat(s51)));
                                          v12 = asuint((1.0f / asfloat(v12)));
                                          v9 = asuint(LegacyMul(asfloat(v13), asfloat(v19)));
                                          v4 = asuint(mad(asfloat(v3), asfloat(v4), asfloat(v5)));
                                          v19 = asuint(log2(asfloat(v10)));
                                          v13 = asuint(LegacyMul(asfloat(s29), asfloat(s31)));
                                          v4 = asuint(max(0.0f, asfloat(v4)));
                                          v9 = asuint(exp2(asfloat(v9)));
                                          v10 = asuint(LegacyMul(asfloat(v14), asfloat(v19)));
                                          v14 = asuint(log2(asfloat(v11)));
                                          v19 = asuint(LegacyMul(asfloat(s49), asfloat(s51)));
                                          v9 = asuint(mad(asfloat(s5), asfloat(v9), asfloat(s27)));
                                          v10 = asuint(exp2(asfloat(v10)));
                                          v3 = asuint(mad(asfloat(v19), asfloat(v16), asfloat(v5)));
                                          v9 = asuint((asfloat(s24) + asfloat(v9)));
                                          v11 = asuint(LegacyMul(asfloat(v15), asfloat(v14)));
                                          v15 = asuint(LegacyMul(asfloat(s50), asfloat(s51)));
                                          v13 = asuint((1.0f / asfloat(v13)));
                                          v14 = asuint(LegacyMul(asfloat(s30), asfloat(s31)));
                                          v3 = asuint(max(0.0f, asfloat(v3)));
                                          v10 = asuint(mad(asfloat(s5), asfloat(v10), asfloat(s27)));
                                          v2 = asuint(mad(asfloat(v15), asfloat(v2), asfloat(v5)));
                                          v11 = asuint(exp2(asfloat(v11)));
                                          v15 = asuint(LegacyMul(asfloat(s2), asfloat(s3)));
                                          v10 = asuint((asfloat(s25) + asfloat(v10)));
                                          v2 = asuint(max(0.0f, asfloat(v2)));
                                          v14 = asuint((1.0f / asfloat(v14)));
                                          v11 = asuint(mad(asfloat(s5), asfloat(v11), asfloat(s27)));
                                          s5 = Large[132][0];
                                          v10 = asuint(log2(asfloat(v10)));
                                          v11 = asuint((asfloat(s26) + asfloat(v11)));
                                          v9 = asuint(log2(asfloat(v9)));
                                          v10 = asuint(LegacyMul(asfloat(v10), asfloat(v13)));
                                          v13 = asuint(LegacyMul(asfloat(s0), asfloat(s3)));
                                          v11 = asuint(log2(asfloat(v11)));
                                          v9 = asuint(LegacyMul(asfloat(v9), asfloat(v12)));
                                          v12 = asuint(LegacyMul(asfloat(s40), asfloat(s43)));
                                          v10 = asuint(exp2(asfloat(v10)));
                                          v17 = asuint((1.0f / asfloat(s5)));
                                          v11 = asuint(LegacyMul(asfloat(v11), asfloat(v14)));
                                          v14 = asuint(LegacyMul(asfloat(s1), asfloat(s3)));
                                          s0 = Large[133][0];
                                          s1 = Large[133][1];
                                          s2 = Large[133][2];
                                          s3 = Large[133][3];
                                          v19 = asuint((1.0f / asfloat(v12)));
                                          v4 = asuint(LegacyMul(asfloat(v17), asfloat(v4)));
                                          v2 = asuint(LegacyMul(asfloat(v17), asfloat(v2)));
                                          v3 = asuint(LegacyMul(asfloat(v17), asfloat(v3)));
                                          v9 = asuint(exp2(asfloat(v9)));
                                          v4 = asuint(log2(asfloat(v4)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(v13), asfloat(v4)));
                                          v3 = asuint(log2(asfloat(v3)));
                                          v13 = asuint(LegacyMul(asfloat(s41), asfloat(s43)));
                                          v2 = asuint(LegacyMul(asfloat(v15), asfloat(v2)));
                                          v4 = asuint(exp2(asfloat(v4)));
                                          v2 = asuint(exp2(asfloat(v2)));
                                          v3 = asuint(LegacyMul(asfloat(v14), asfloat(v3)));
                                          v14 = asuint(LegacyMul(asfloat(s42), asfloat(s43)));
                                          v4 = asuint(mad(asfloat(s5), asfloat(v4), asfloat(s39)));
                                          v3 = asuint(exp2(asfloat(v3)));
                                          v2 = asuint(mad(asfloat(s5), asfloat(v2), asfloat(s39)));
                                          v4 = asuint((asfloat(s36) + asfloat(v4)));
                                          v15 = asuint((1.0f / asfloat(v14)));
                                          v2 = asuint((asfloat(s38) + asfloat(v2)));
                                          v3 = asuint(mad(asfloat(s5), asfloat(v3), asfloat(s39)));
                                          v4 = asuint(log2(asfloat(v4)));
                                          v3 = asuint((asfloat(s37) + asfloat(v3)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(v4), asfloat(v19)));
                                          v13 = asuint((1.0f / asfloat(v13)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v15)));
                                          v12 = asuint(exp2(asfloat(v4)));
                                          v4 = asuint((asfloat(s2) - asfloat(s1)));
                                          v14 = asuint(exp2(asfloat(v2)));
                                          v2 = asuint((1.0f / asfloat(s0)));
                                          v3 = asuint(log2(asfloat(v3)));
                                          v4 = asuint((1.0f / asfloat(v4)));
                                          v2 = asuint(saturate(LegacyMul(asfloat(v5), asfloat(v2))));
                                          v5 = asuint((asfloat(v5) - asfloat(s1)));
                                          v3 = asuint(LegacyMul(asfloat(v3), asfloat(v13)));
                                          v11 = asuint(exp2(asfloat(v11)));
                                          v4 = asuint(saturate(LegacyMul(asfloat(v5), asfloat(v4))));
                                          v13 = asuint(exp2(asfloat(v3)));
                                          v3 = 0x40400000u;
                                          v5 = asuint(mad(asfloat(v4), -2.0f, asfloat(0x40400000u)));
                                          v4 = asuint(LegacyMul(asfloat(v4), asfloat(v4)));
                                          v3 = asuint(NonFusedMac(-2.0f, asfloat(v2), asfloat(v3)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v2)));
                                          v5 = asuint(LegacyMul(asfloat(v4), asfloat(v5)));
                                          v15 = asuint(mad(-asfloat(v2), asfloat(v3), 1.0f));
                                          v17 = asuint(saturate(mad(asfloat(v2), asfloat(v3), -asfloat(v5))));
                                          v2 = asuint(LegacyMul(asfloat(s23), asfloat(v6)));
                                          v6 = asuint(LegacyMul(asfloat(s35), asfloat(v11)));
                                          v2 = asuint(LegacyMul(asfloat(s20), asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(v2), asfloat(v15)));
                                          v2 = asuint(LegacyMul(asfloat(s35), asfloat(v9)));
                                          v2 = asuint(LegacyMul(asfloat(s32), asfloat(v2)));
                                          v4 = asuint(mad(asfloat(v2), asfloat(v17), asfloat(v4)));
                                          v2 = asuint(LegacyMul(asfloat(s47), asfloat(v12)));
                                          v2 = asuint(LegacyMul(asfloat(s44), asfloat(v2)));
                                          v4 = asuint(mad(asfloat(v2), asfloat(v5), asfloat(v4)));
                                          v2 = asuint(LegacyMul(asfloat(s23), asfloat(v7)));
                                          v7 = asuint(LegacyMul(asfloat(s34), asfloat(v6)));
                                          v6 = asuint(LegacyMul(asfloat(s47), asfloat(v14)));
                                          v2 = asuint(LegacyMul(asfloat(s21), asfloat(v2)));
                                          v3 = asuint(LegacyMul(asfloat(v2), asfloat(v15)));
                                          v2 = asuint(LegacyMul(asfloat(s35), asfloat(v10)));
                                          v2 = asuint(LegacyMul(asfloat(s33), asfloat(v2)));
                                          v3 = asuint(mad(asfloat(v2), asfloat(v17), asfloat(v3)));
                                          v2 = asuint(LegacyMul(asfloat(s47), asfloat(v13)));
                                          v2 = asuint(LegacyMul(asfloat(s45), asfloat(v2)));
                                          v3 = asuint(mad(asfloat(v2), asfloat(v5), asfloat(v3)));
                                          v2 = asuint(LegacyMul(asfloat(s23), asfloat(v8)));
                                          v2 = asuint(LegacyMul(asfloat(s22), asfloat(v2)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v15)));
                                          v2 = asuint(mad(asfloat(v7), asfloat(v17), asfloat(v2)));
                                          v7 = asuint(LegacyMul(asfloat(s46), asfloat(v6)));
                                          v2 = asuint(mad(asfloat(v7), asfloat(v5), asfloat(v2)));
                                          pc = 0x2c78u;
                                      }
                                      if (pc == 0x2c78u)
                                      {
                                          scc = (s4 == ((uint)(0)) ? 1u : 0u);
                                          pc = scc != 0u ? 0x3150u : 0x2c80u;
                                      }
                                      if (pc == 0x2c80u)
                                      {
                                          s0 = Large[150][0];
                                          s1 = Large[150][1];
                                          s2 = Large[150][2];
                                          s3 = Large[150][3];
                                          s9 = Large[151][0];
                                          s4 = Large[152][0];
                                          s5 = Large[152][1];
                                          s6 = Large[152][2];
                                          s7 = Large[152][3];
                                          v5 = asuint(LegacyMul(asfloat(0x3f371759u), asfloat(v3)));
                                          s10 = 0x3f18579au;
                                          s44 = Large[134][0];
                                          s45 = Large[134][1];
                                          s46 = Large[134][2];
                                          s47 = Large[134][3];
                                          s48 = Large[135][0];
                                          s49 = Large[135][1];
                                          s50 = Large[135][2];
                                          s51 = Large[135][3];
                                          s52 = Large[136][0];
                                          s53 = Large[136][1];
                                          s54 = Large[136][2];
                                          s55 = Large[136][3];
                                          s56 = Large[137][0];
                                          s57 = Large[137][1];
                                          s58 = Large[137][2];
                                          s59 = Large[137][3];
                                          s72 = Large[138][0];
                                          s73 = Large[138][1];
                                          s74 = Large[138][2];
                                          s75 = Large[138][3];
                                          s11 = Large[139][0];
                                          v5 = asuint(mad(asfloat(0x3e59b3d0u), asfloat(v4), asfloat(v5)));
                                          s28 = Large[140][0];
                                          s29 = Large[140][1];
                                          s30 = Large[140][2];
                                          s31 = Large[140][3];
                                          s32 = Large[141][0];
                                          s33 = Large[141][1];
                                          s34 = Large[141][2];
                                          s35 = Large[141][3];
                                          s36 = Large[142][0];
                                          s37 = Large[142][1];
                                          s38 = Large[142][2];
                                          s39 = Large[142][3];
                                          s40 = Large[143][0];
                                          s41 = Large[143][1];
                                          s42 = Large[143][2];
                                          s43 = Large[143][3];
                                          v5 = asuint(mad(asfloat(0x3d93dd98u), asfloat(v2), asfloat(v5)));
                                          v7 = asuint((asfloat(0x3b4b7e21u) + asfloat(v5)));
                                          v2 = asuint((asfloat(v2) - asfloat(v5)));
                                          v4 = asuint((asfloat(v4) - asfloat(v5)));
                                          v3 = asuint((asfloat(v3) - asfloat(v5)));
                                          v6 = asuint((asfloat(s5) - asfloat(s4)));
                                          v8 = s5;
                                          v7 = asuint(log2(asfloat(v7)));
                                          v9 = s4;
                                          v12 = asuint((asfloat(s53) + asfloat(s55)));
                                          v6 = asuint(max(0.0f, asfloat(v6)));
                                          v13 = asuint((asfloat(s52) + asfloat(s55)));
                                          v16 = asuint((asfloat(s46) + asfloat(s47)));
                                          v18 = asuint((asfloat(s44) + asfloat(s47)));
                                          v19 = asuint((asfloat(s36) + asfloat(s39)));
                                          v8 = asuint(mad(asfloat(0xbea8f5c3u), asfloat(v6), asfloat(v8)));
                                          v9 = asuint(mad(asfloat(0x3ea8f5c3u), asfloat(v6), asfloat(v9)));
                                          v6 = asuint(mad(asfloat(0x3d924925u), asfloat(v7), asfloat(s10)));
                                          v20 = asuint((asfloat(s37) + asfloat(s39)));
                                          v21 = asuint((asfloat(s38) + asfloat(s39)));
                                          v10 = asuint((asfloat(s6) - asfloat(v8)));
                                          v9 = asuint((1.0f / asfloat(v9)));
                                          v8 = asuint((asfloat(v6) - asfloat(v8)));
                                          s4 = Large[144][0];
                                          s5 = Large[144][1];
                                          s6 = Large[144][2];
                                          s7 = Large[144][3];
                                          s76 = Large[145][0];
                                          s12 = Large[146][0];
                                          s13 = Large[146][1];
                                          s14 = Large[146][2];
                                          s15 = Large[146][3];
                                          s16 = Large[147][0];
                                          s17 = Large[147][1];
                                          s18 = Large[147][2];
                                          s19 = Large[147][3];
                                          s20 = Large[148][0];
                                          s21 = Large[148][1];
                                          s22 = Large[148][2];
                                          s23 = Large[148][3];
                                          s24 = Large[149][0];
                                          s25 = Large[149][1];
                                          s26 = Large[149][2];
                                          s27 = Large[149][3];
                                          v7 = asuint((1.0f / asfloat(v10)));
                                          v25 = asuint((asfloat(s28) + asfloat(s31)));
                                          v26 = asuint((asfloat(s29) + asfloat(s31)));
                                          v27 = asuint((asfloat(s30) + asfloat(s31)));
                                          v6 = asuint(saturate(LegacyMul(asfloat(v6), asfloat(v9))));
                                          v7 = asuint(saturate(LegacyMul(asfloat(v8), asfloat(v7))));
                                          v8 = 0x40400000u;
                                          v9 = asuint(LegacyMul(asfloat(v7), asfloat(v7)));
                                          v7 = asuint(mad(asfloat(v7), -2.0f, asfloat(0x40400000u)));
                                          v8 = asuint(NonFusedMac(-2.0f, asfloat(v6), asfloat(v8)));
                                          v6 = asuint(LegacyMul(asfloat(v6), asfloat(v6)));
                                          v23 = asuint((asfloat(s21) + asfloat(s23)));
                                          v24 = asuint((asfloat(s20) + asfloat(s23)));
                                          v17 = asuint(LegacyMul(asfloat(v9), asfloat(v7)));
                                          v11 = asuint(saturate(mad(asfloat(v6), asfloat(v8), -asfloat(v17))));
                                          v6 = asuint(mad(-asfloat(v6), asfloat(v8), 1.0f));
                                          v8 = asuint(max(0.0f, asfloat(v11)));
                                          v11 = asuint((asfloat(s54) + asfloat(s55)));
                                          v14 = asuint((asfloat(v8) + asfloat(v6)));
                                          v14 = asuint(mad(asfloat(v9), asfloat(v7), asfloat(v14)));
                                          v7 = asuint(max(asfloat(0x358637bdu), asfloat(v14)));
                                          v7 = asuint((1.0f / asfloat(v7)));
                                          v6 = asuint(LegacyMul(asfloat(v6), asfloat(v7)));
                                          v8 = asuint(LegacyMul(asfloat(v8), asfloat(v7)));
                                          v7 = asuint(LegacyMul(asfloat(v17), asfloat(v7)));
                                          v17 = asuint((asfloat(s45) + asfloat(s47)));
                                          v9 = asuint(LegacyMul(asfloat(s59), asfloat(v6)));
                                          v10 = asuint(LegacyMul(asfloat(s43), asfloat(v8)));
                                          v13 = asuint(LegacyMul(asfloat(v13), asfloat(v6)));
                                          v12 = asuint(LegacyMul(asfloat(v12), asfloat(v6)));
                                          v11 = asuint(LegacyMul(asfloat(v11), asfloat(v6)));
                                          v14 = asuint(LegacyMul(asfloat(s58), asfloat(v9)));
                                          v15 = asuint(LegacyMul(asfloat(s56), asfloat(v9)));
                                          v9 = asuint(LegacyMul(asfloat(s57), asfloat(v9)));
                                          v22 = asuint(LegacyMul(asfloat(s27), asfloat(v7)));
                                          v12 = asuint(mad(asfloat(v20), asfloat(v8), asfloat(v12)));
                                          v14 = asuint(mad(asfloat(s42), asfloat(v10), asfloat(v14)));
                                          v15 = asuint(mad(asfloat(s40), asfloat(v10), asfloat(v15)));
                                          v9 = asuint(mad(asfloat(s41), asfloat(v10), asfloat(v9)));
                                          v13 = asuint(mad(asfloat(v19), asfloat(v8), asfloat(v13)));
                                          v18 = asuint(LegacyMul(asfloat(v18), asfloat(v6)));
                                          v17 = asuint(LegacyMul(asfloat(v17), asfloat(v6)));
                                          v16 = asuint(LegacyMul(asfloat(v16), asfloat(v6)));
                                          v11 = asuint(mad(asfloat(v21), asfloat(v8), asfloat(v11)));
                                          v10 = asuint((asfloat(s22) + asfloat(s23)));
                                          v15 = asuint(mad(asfloat(s24), asfloat(v22), asfloat(v15)));
                                          v9 = asuint(mad(asfloat(s25), asfloat(v22), asfloat(v9)));
                                          v14 = asuint(mad(asfloat(s26), asfloat(v22), asfloat(v14)));
                                          v22 = asuint((asfloat(s13) + asfloat(s15)));
                                          v17 = asuint(mad(asfloat(v26), asfloat(v8), asfloat(v17)));
                                          v4 = asuint(mad(asfloat(v4), asfloat(v15), asfloat(v5)));
                                          v3 = asuint(mad(asfloat(v3), asfloat(v9), asfloat(v5)));
                                          v5 = asuint(mad(asfloat(v2), asfloat(v14), asfloat(v5)));
                                          v11 = asuint(mad(asfloat(v10), asfloat(v7), asfloat(v11)));
                                          v12 = asuint(mad(asfloat(v23), asfloat(v7), asfloat(v12)));
                                          v9 = asuint((asfloat(s14) + asfloat(s15)));
                                          v16 = asuint(mad(asfloat(v27), asfloat(v8), asfloat(v16)));
                                          v21 = asuint((asfloat(s12) + asfloat(s15)));
                                          v18 = asuint(mad(asfloat(v25), asfloat(v8), asfloat(v18)));
                                          v13 = asuint(mad(asfloat(v24), asfloat(v7), asfloat(v13)));
                                          v2 = asuint(exp2(asfloat(v11)));
                                          v16 = asuint(mad(asfloat(v9), asfloat(v7), asfloat(v16)));
                                          v5 = asuint(max(0.0f, asfloat(v5)));
                                          v18 = asuint(mad(asfloat(v21), asfloat(v7), asfloat(v18)));
                                          v4 = asuint(max(0.0f, asfloat(v4)));
                                          v10 = asuint(exp2(asfloat(v12)));
                                          v17 = asuint(mad(asfloat(v22), asfloat(v7), asfloat(v17)));
                                          v3 = asuint(max(0.0f, asfloat(v3)));
                                          v9 = asuint(LegacyMul(asfloat(s11), asfloat(v6)));
                                          v5 = asuint((asfloat(v16) + asfloat(v5)));
                                          v11 = asuint(exp2(asfloat(v13)));
                                          v4 = asuint((asfloat(v18) + asfloat(v4)));
                                          v3 = asuint((asfloat(v17) + asfloat(v3)));
                                          v9 = asuint(mad(asfloat(s76), asfloat(v8), asfloat(v9)));
                                          v2 = asuint(mad(asfloat(v2), asfloat(v5), asfloat(0x3b4b7e21u)));
                                          v13 = asuint(LegacyMul(asfloat(s75), asfloat(v6)));
                                          v12 = asuint(LegacyMul(asfloat(s50), asfloat(s51)));
                                          v3 = asuint(mad(asfloat(v10), asfloat(v3), asfloat(0x3b4b7e21u)));
                                          v9 = asuint(mad(asfloat(s9), asfloat(v7), asfloat(v9)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v4 = asuint(mad(asfloat(v11), asfloat(v4), asfloat(0x3b4b7e21u)));
                                          v10 = asuint(LegacyMul(asfloat(s48), asfloat(s51)));
                                          v11 = asuint(LegacyMul(asfloat(s49), asfloat(s51)));
                                          v14 = asuint(LegacyMul(asfloat(s34), asfloat(s35)));
                                          v3 = asuint(log2(asfloat(v3)));
                                          v15 = asuint(LegacyMul(asfloat(s33), asfloat(s35)));
                                          v16 = asuint(LegacyMul(asfloat(s32), asfloat(s35)));
                                          v12 = asuint(max(asfloat(0x358637bdu), asfloat(v12)));
                                          v11 = asuint(max(asfloat(0x358637bdu), asfloat(v11)));
                                          v4 = asuint(log2(asfloat(v4)));
                                          v2 = asuint(mad(asfloat(0x3d924925u), asfloat(v2), asfloat(s10)));
                                          v10 = asuint(max(asfloat(0x358637bdu), asfloat(v10)));
                                          v17 = asuint(LegacyMul(asfloat(s7), asfloat(v8)));
                                          v18 = asuint(LegacyMul(asfloat(s72), asfloat(v13)));
                                          v5 = asuint((1.0f / asfloat(v9)));
                                          v3 = asuint(mad(asfloat(0x3d924925u), asfloat(v3), asfloat(s10)));
                                          v19 = asuint(LegacyMul(asfloat(s73), asfloat(v13)));
                                          v23 = asuint(LegacyMul(asfloat(s74), asfloat(v13)));
                                          v20 = asuint(LegacyMul(asfloat(s17), asfloat(s19)));
                                          v4 = asuint(mad(asfloat(0x3d924925u), asfloat(v4), asfloat(s10)));
                                          v12 = asuint(log2(asfloat(v12)));
                                          v16 = asuint(max(asfloat(0x358637bdu), asfloat(v16)));
                                          v15 = asuint(max(asfloat(0x358637bdu), asfloat(v15)));
                                          v14 = asuint(max(asfloat(0x358637bdu), asfloat(v14)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v5)));
                                          v4 = asuint(LegacyMul(asfloat(v5), asfloat(v4)));
                                          v3 = asuint(LegacyMul(asfloat(v3), asfloat(v5)));
                                          v5 = asuint(LegacyMul(asfloat(s16), asfloat(s19)));
                                          v21 = asuint(LegacyMul(asfloat(s18), asfloat(s19)));
                                          v10 = asuint(log2(asfloat(v10)));
                                          v13 = asuint(LegacyMul(asfloat(s3), asfloat(v7)));
                                          v23 = asuint(mad(asfloat(s6), asfloat(v17), asfloat(v23)));
                                          v19 = asuint(mad(asfloat(s5), asfloat(v17), asfloat(v19)));
                                          v18 = asuint(mad(asfloat(s4), asfloat(v17), asfloat(v18)));
                                          v11 = asuint(log2(asfloat(v11)));
                                          v17 = asuint(max(asfloat(0x358637bdu), asfloat(v21)));
                                          v20 = asuint(max(asfloat(0x358637bdu), asfloat(v20)));
                                          v5 = asuint(max(asfloat(0x358637bdu), asfloat(v5)));
                                          v18 = asuint(mad(asfloat(s0), asfloat(v13), asfloat(v18)));
                                          v4 = asuint(log2(asfloat(v4)));
                                          v19 = asuint(mad(asfloat(s1), asfloat(v13), asfloat(v19)));
                                          v23 = asuint(mad(asfloat(s2), asfloat(v13), asfloat(v23)));
                                          v12 = asuint(LegacyMul(asfloat(v12), asfloat(v6)));
                                          s0 = 0xbb4b7c74u;
                                          v22 = asuint(log2(asfloat(v3)));
                                          v13 = asuint(LegacyMul(asfloat(v11), asfloat(v6)));
                                          v6 = asuint(LegacyMul(asfloat(v10), asfloat(v6)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(v4), asfloat(v18)));
                                          v14 = asuint(log2(asfloat(v14)));
                                          v3 = asuint(LegacyMul(asfloat(v22), asfloat(v19)));
                                          v15 = asuint(log2(asfloat(v15)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v23)));
                                          v16 = asuint(log2(asfloat(v16)));
                                          v12 = asuint(mad(asfloat(v14), asfloat(v8), asfloat(v12)));
                                          v17 = asuint(log2(asfloat(v17)));
                                          v13 = asuint(mad(asfloat(v15), asfloat(v8), asfloat(v13)));
                                          v20 = asuint(log2(asfloat(v20)));
                                          v6 = asuint(mad(asfloat(v16), asfloat(v8), asfloat(v6)));
                                          v5 = asuint(log2(asfloat(v5)));
                                          v12 = asuint(mad(asfloat(v17), asfloat(v7), asfloat(v12)));
                                          v4 = asuint(exp2(asfloat(v4)));
                                          v13 = asuint(mad(asfloat(v20), asfloat(v7), asfloat(v13)));
                                          v3 = asuint(exp2(asfloat(v3)));
                                          v6 = asuint(mad(asfloat(v5), asfloat(v7), asfloat(v6)));
                                          v2 = asuint(exp2(asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(v4), asfloat(v9)));
                                          v5 = asuint(exp2(asfloat(v6)));
                                          v3 = asuint(LegacyMul(asfloat(v3), asfloat(v9)));
                                          v4 = asuint(max(0.0f, asfloat(v4)));
                                          v6 = asuint(exp2(asfloat(v13)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v9)));
                                          v3 = asuint(max(0.0f, asfloat(v3)));
                                          v7 = asuint(exp2(asfloat(v12)));
                                          v2 = asuint(max(0.0f, asfloat(v2)));
                                          v6 = asuint((1.0f / asfloat(v6)));
                                          v3 = asuint(log2(asfloat(v3)));
                                          v7 = asuint((1.0f / asfloat(v7)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v3 = asuint(LegacyMul(asfloat(v3), asfloat(v6)));
                                          v5 = asuint((1.0f / asfloat(v5)));
                                          v4 = asuint(log2(asfloat(v4)));
                                          v2 = asuint(LegacyMul(asfloat(v2), asfloat(v7)));
                                          v3 = asuint(exp2(asfloat(v3)));
                                          v4 = asuint(LegacyMul(asfloat(v4), asfloat(v5)));
                                          v2 = asuint(exp2(asfloat(v2)));
                                          v3 = asuint((asfloat(0xbed6358du) + asfloat(v3)));
                                          v4 = asuint(exp2(asfloat(v4)));
                                          v3 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v3)));
                                          v2 = asuint((asfloat(0xbed6358du) + asfloat(v2)));
                                          v3 = asuint(exp2(asfloat(v3)));
                                          v4 = asuint((asfloat(0xbed6358du) + asfloat(v4)));
                                          v2 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v2)));
                                          v4 = asuint(LegacyMul(asfloat(0x41600000u), asfloat(v4)));
                                          v2 = asuint(exp2(asfloat(v2)));
                                          v3 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v3), asfloat(s0)));
                                          v4 = asuint(exp2(asfloat(v4)));
                                          v2 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v2), asfloat(s0)));
                                          v4 = asuint(mad(asfloat(0x3e3851ecu), asfloat(v4), asfloat(s0)));
                                          pc = 0x3150u;
                                      }
                                      if (pc == 0x3150u)
                                      {
                                          v4 = asuint((asfloat(0x3b4b7e21u) + asfloat(v4)));
                                          v3 = asuint((asfloat(0x3b4b7e21u) + asfloat(v3)));
                                          v2 = asuint((asfloat(0x3b4b7e21u) + asfloat(v2)));
                                          s0 = 0x3f18579au;
                                          v5 = asuint(1.0f);
                                          v4 = asuint(log2(asfloat(v4)));
                                          v3 = asuint(log2(asfloat(v3)));
                                          v2 = asuint(log2(asfloat(v2)));
                                          v4 = asuint(mad(asfloat(0x3d924925u), asfloat(v4), asfloat(s0)));
                                          v3 = asuint(mad(asfloat(0x3d924925u), asfloat(v3), asfloat(s0)));
                                          v6 = asuint(mad(asfloat(0x3d924925u), asfloat(v2), asfloat(s0)));
                                          v2 = asuint(max(0.0f, asfloat(v4)));
                                          v3 = asuint(max(0.0f, asfloat(v3)));
                                          v4 = asuint(max(0.0f, asfloat(v6)));
                                          v6 = s8;
                                          OutputTexture[uint3(v0, v1, v6)] = float4(asfloat(v2), asfloat(v3), asfloat(v4), asfloat(v5));
                                          return;
                                      }
                                  }
                                  """;

    public static string Translate(ReadOnlyMemory<byte> shader)
    {
        if (shader.Length != 8332 || Convert.ToHexString(SHA256.HashData(shader.Span)) != ContainerHash)
        {
            throw new NotSupportedException("Unsupported color-transform shader container.");
        }

        var programs = NativeProgramReader.Read(ShaderContainer.Parse(shader));

        if (programs is not [{
                    Stage: NativeShaderStage.Compute, Wave64: true,
                    VectorRegisters: 28, ScalarRegisters: 79, LocalBytes: 0, Threads: [8, 8, 1]
                } program ]
            || program.Code.Length != 12928
            || program.RootLayout.Length != 920
            || program.UsedResources.Length != 80
            || Convert.ToHexString(SHA256.HashData(program.Code)) != ProgramHash)
        {
            throw new NotSupportedException("Unsupported native compute layout or instruction stream.");
        }

        return Source;
    }
}
