# SPDX-License-Identifier: GPL-3.0-or-later
"""Synthetic fixtures only; accepts CLI command as arguments."""
import pathlib, struct, subprocess, sys, tempfile


def put(data, offset, fmt, *values):
    struct.pack_into('<' + fmt, data, offset, *values)


def pe_fixture():
    data = bytearray(0x800)
    data[:2] = b'MZ'
    put(data, 0x3c, 'I', 0x80)
    data[0x80:0x84] = b'PE\0\0'
    put(data, 0x84, 'HHIIIHH', 0x8664, 1, 0, 0, 0, 240, 0x22)
    opt = 0x98
    put(data, opt, 'H', 0x20b)
    put(data, opt + 16, 'I', 0x1000)
    put(data, opt + 24, 'Q', 0x140000000)
    put(data, opt + 56, 'II', 0x2000, 0x200)
    put(data, opt + 68, 'HHQQ', 14, 0x100, 0x100000, 0x1000)
    put(data, opt + 108, 'I', 16)
    for index, rva, size in [(0, 0x1200, 0x80), (1, 0x1000, 40), (9, 0x1300, 40), (13, 0x1400, 64), (6, 0x1500, 28)]:
        put(data, opt + 112 + index * 8, 'II', rva, size)
    sec = opt + 240
    data[sec:sec+8] = b'.rdata\0\0'
    put(data, sec + 8, 'IIII', 0x600, 0x1000, 0x600, 0x200)
    put(data, sec + 36, 'I', 0x40000040)
    def off(rva): return rva - 0x1000 + 0x200
    def string(rva, s): data[off(rva):off(rva)+len(s)+1] = s + b'\0'
    put(data, off(0x1000), 'IIIII', 0x1080, 0, 0, 0x1060, 0x10a0)
    string(0x1060, b'example.dll')
    put(data, off(0x1080), 'QQQ', 0x10c0, (1 << 63) | 42, 0)
    put(data, off(0x10c0), 'H', 7)
    string(0x10c2, b'Example')
    put(data, off(0x1200)+16, 'IIIIII', 1, 2, 1, 0x1240, 0x1248, 0x124c)
    put(data, off(0x1240), 'II', 0x1100, 0x1260)
    put(data, off(0x1248), 'I', 0x1270)
    put(data, off(0x124c), 'H', 0)
    string(0x1260, b'other.Func')
    string(0x1270, b'Exported')
    put(data, off(0x1300)+24, 'Q', 0x140001340)
    put(data, off(0x1340), 'QQ', 0x140001100, 0)
    put(data, off(0x1400), 'IIIIIIII', 1, 0x1460, 0, 0x14a0, 0x1480, 0, 0, 0)
    string(0x1460, b'delay.dll')
    put(data, off(0x1480), 'QQ', (1 << 63) | 12, 0)
    record = b'RSDS' + bytes(range(16)) + struct.pack('<I', 2) + b'example.pdb\0'
    put(data, off(0x1500)+12, 'IIII', 2, len(record), 0x1540, off(0x1540))
    data[off(0x1540):off(0x1540)+len(record)] = record
    return data


def shader_fixture():
    bitcode = b'BC\xc0\xde' + b'\xaa' * 12
    prefix, trailer = b'XDAT\0\0\0\0', b'xbop\0\0\0\0'
    size = 24 + len(prefix) + len(bitcode) + len(trailer)
    program = struct.pack('<II4sIII', 5 << 16 | 0x65, size // 4, b'DXIL', 0x105, 16 + len(prefix), len(bitcode)) + prefix + bitcode + trailer
    return b'DXBC' + bytes(16) + struct.pack('<HHIII', 1, 0, 44 + len(program), 1, 36) + b'DXIL' + struct.pack('<I', len(program)) + program


def main():
    command = sys.argv[1:]
    if not command:
        raise SystemExit('Usage: smoke.py <cli command>')
    with tempfile.TemporaryDirectory() as tmp:
        root = pathlib.Path(tmp)
        def run(mode, data, expected, success=True):
            path = root / 'input.bin'
            path.write_bytes(data)
            result = subprocess.run(command + [mode, str(path)], capture_output=True, text=True)
            assert (result.returncode == 0) == success, result
            for token in expected:
                assert token.lower() in result.stdout.lower(), (token, result)
        pe = pe_fixture()
        shader = shader_fixture()
        run('inspect', pe, ['AMD64 xbox', 'example.dll!Example', 'example.dll!#42', 'delay.dll!#12', 'Exported', 'other.Func', 'TLS callbacks: 0x1100', 'example.pdb 030201000504070608090A0B0C0D0E0F2'])
        run('shader', shader, ['DXBC 1.0', 'kind=5 SM=6.5', 'XDAT=true', 'xbop=true'])
        run('inspect', pe[:200], [], False)
        corrupt_pe = bytearray(pe)
        put(corrupt_pe, 0x200 + 16, 'I', 0x1fff)
        run('inspect', corrupt_pe, [], False)
        run('shader', shader[:-1], [], False)
        bad_shader = bytearray(shader)
        put(bad_shader, 32, 'I', 0xffffffff)
        run('shader', bad_shader, [], False)
        run('shader', b'nope', [], False)
        bad_shader = bytearray(shader)
        put(bad_shader, 32, 'I', 32)
        run('shader', bad_shader, [], False)
        bad_tls = bytearray(pe)
        put(bad_tls, 0x98 + 112 + 9 * 8 + 4, 'I', 24)
        run('inspect', bad_tls, [], False)
        bad_export = bytearray(pe)
        put(bad_export, 0x98 + 112 + 4, 'I', 20)
        run('inspect', bad_export, [], False)
        print('10 synthetic smoke cases passed')

if __name__ == '__main__':
    main()
