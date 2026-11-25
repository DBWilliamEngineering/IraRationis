#include "InstructionDecoder.h"
#include <cassert>
#include <cstdio>
using namespace irx;
int main()
{
    const std::uint8_t clzero[] = {0x0f,0x01,0xfc};
    const std::uint8_t loop[] = {0x0f,0x01,0xfc,0x48,0x83,0xc0,0x40,0x4c,0x39,0xd0,0x72,0xf4};
    const std::uint8_t insert[] = {0xf2,0x45,0x0f,0x79,0xc1};
    const std::uint8_t rejected[] = {0xf2,0xf3,0x0f,0x78,0xc1,0x10,0x10};
    const std::uint8_t clwb[] = {0x66,0x0f,0xae,0x74,0x24,0x08};
    assert(Decode(clzero,sizeof(clzero)).kind == Kind::Clzero);
    assert(Decode(loop,sizeof(loop)).kind == Kind::ClzeroLoop);
    assert(Decode(loop,sizeof(loop)).destination == 10);
    assert(Decode(insert,sizeof(insert)).destination == 8);
    assert(Decode(insert,sizeof(insert)).source == 9);
    assert(Decode(rejected,sizeof(rejected)).kind == Kind::Unknown);
    assert(Decode(clwb,sizeof(clwb)).length == 6);
    assert(Decode(clwb,sizeof(clwb)-1).kind == Kind::Unknown);
    assert(Extract(0x123456789abcdef0ULL,8,16) == 0xbc);
    assert(Insert(UINT64_MAX,0xab,8,8) == 0xffffffffffffabffULL);
    assert(Extract(UINT64_MAX,0,0) == UINT64_MAX);
    std::puts("11 decoder assertions passed");
}
