#!/usr/bin/env python3
"""Build a source overlay against baseline or previous IRX close patch; never edit checkout."""
import argparse
import shutil
import tempfile
from pathlib import Path
import previous_overlay as old

HERE = Path(__file__).resolve().parent
G = Path('IraRationis.Graphics')
MARK = 'IRX-GFX-CLOSE-R2'


def once(s, a, b, label):
    return old.replace_once(s, a, b, label)


def ensure(s, a, b, label):
    if b in s and a not in s:
        return s
    return once(s, a, b, label)


def operations(s):
    if old.RTV in s and old.DSV in s:
        s = old.operations(s)
    elif ')->ClearRenderTargetView(' not in s or ')->ClearDepthStencilView(' not in s:
        raise ValueError('Unknown clear implementation; review manually')
    # Track the actual host call, not the guest operation that might be skipped.
    for method in ('ClearRenderTargetView', 'ClearDepthStencilView'):
        anchor = f'        ((ID3D12GraphicsCommandList*)list)->{method}('
        s = once(s, anchor, f'        TraceHostCommand(self, "{method}");\n' + anchor, method)
    return s


def device(s):
    if 'EnableHostValidation();' not in s:
        s = old.device(s)
    elif 'ReportHostCloseFailure(self, hr, "guest-close");' not in s:
        raise ValueError('Partial previous device patch')
    return once(s, '        EnableHostValidation();', '        Console.Error.WriteLine("[graphics] IRX-GFX-CLOSE-R2 active.");\n        EnableHostValidation();', 'build marker')


def memory(s):
    if 'ReportHostCloseFailure(self, hr, boundary);' not in s:
        s = old.memory(s, False)
    s = once(s, old.SEAL, '            Environment.FailFast($"[graphics] IRX-GFX-CLOSE-R2: closing memory segment failed: 0x{hr:X8}.");', 'distinct failure marker')
    s = once(s, '        ReplayRasterState(self, state);', '        state.LastHostCommand = null;\n        state.HostCommandCount = 0;\n        ReplayRasterState(self, state);', 'new segment trace reset')
    return s


def state(s):
    return once(s, '        public nint Pipeline;', '        public nint Pipeline;\n        public string? LastHostCommand;\n        public int HostCommandCount;', 'state breadcrumbs')


def sync(s):
    return once(s, '        ((ID3D12GraphicsCommandList7*)enhanced)->Barrier(1, (D3D12_BARRIER_GROUP*)&group);', '        TraceHostCommand(self, "GlobalBarrier");\n        ((ID3D12GraphicsCommandList7*)enhanced)->Barrier(1, (D3D12_BARRIER_GROUP*)&group);', 'barrier breadcrumb')


def raster(s):
    # Capture actual host raster calls (including replay) with no per-call string allocation.
    for slot, method in [(20,'IASetPrimitiveTopology'),(21,'RSSetViewports'),(22,'RSSetScissorRects'),(23,'OMSetBlendFactor'),(24,'OMSetStencilRef')]:
        lines = s.splitlines(keepends=True)
        matches = [i for i,l in enumerate(lines) if f'Slot(host, {slot})' in l and 'delegate*' in l]
        if len(matches) != 2:
            raise ValueError(f'Raster slot {slot}: expected two host calls, got {len(matches)}')
        for i in reversed(matches):
            indent = lines[i][:len(lines[i])-len(lines[i].lstrip())]
            lines.insert(i, f'{indent}TraceHostCommand(self, "{method}");\n')
        s = ''.join(lines)
    return s


def build(root, out):
    root, out = root.resolve(), out.resolve()
    if root.is_file():
        root = root.parent
    if out.exists() or root == out or root in out.parents:
        raise ValueError('Output must be a new directory outside the checkout')
    transforms = {'CommandOperations':operations,'CommandMemory':memory,'CommandState':state,'Synchronization':sync,'RasterState':raster}
    files = {}
    for suffix, fn in transforms.items():
        path = G / f'ConsoleGraphicsDevice.{suffix}.cs'
        files[path] = fn((root/path).read_text(encoding='utf-8-sig')).encode()
    path = G/'ConsoleGraphicsDevice.cs'
    files[path] = device((root/path).read_text(encoding='utf-8-sig')).encode()
    for filename in ('ConsoleGraphicsDevice.HostValidation.cs',):
        path = G/filename
        files[path] = (HERE/path).read_bytes()
    # Retain previous event-driven fence if already installed. Do not introduce it in crash isolation.
    if b'new HostCompletionFence()' in files[G/'ConsoleGraphicsDevice.CommandMemory.cs'] and not (root/G/'ConsoleGraphicsDevice.HostCompletionFence.cs').exists():
        raise ValueError('Previous fence patch is incomplete')
    # Full standalone replacement is included separately. Preserve all local history by default.
    readme = (root/'README.md').read_text(encoding='utf-8-sig')
    if '## Graphics close stabilization R2' in readme:
        raise ValueError('R2 README already present')
    files[Path('README.md')] = (readme.rstrip()+'\n\n'+(HERE/'README-addendum.md').read_text()).encode()
    out.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='irx-r2-',dir=out.parent))
    try:
        for p,data in files.items():
            (stage/p).parent.mkdir(parents=True,exist_ok=True)
            (stage/p).write_bytes(data)
        if out.exists():
            raise ValueError('Output already exists')
        stage.rename(out)
    finally:
        if stage.exists():
            shutil.rmtree(stage)
    print(f'Generated {len(files)} replacement files in {out}. Copy overlay contents into repository.')

if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--solution',required=True,type=Path)
    p.add_argument('--output',required=True,type=Path)
    a=p.parse_args()
    try:
        build(a.solution,a.output)
    except (ValueError,OSError) as e:
        p.exit(1,f'Overlay aborted: {e}\n')
