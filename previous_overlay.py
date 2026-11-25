#!/usr/bin/env python3
"""Generate complete replacement files from the checked-out IraRationis source.
No Git writes. Strict anchors, no partial output on source mismatch.
"""
import argparse
import shutil
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
G = Path('IraRationis.Graphics')

def replace_once(text, before, after, label):
    count = text.count(before)
    if count != 1:
        raise ValueError(f'{label}: expected one source anchor, found {count}')
    return text.replace(before, after, 1)

RTV = '        ((delegate* unmanaged[Stdcall]<nint, nuint, float*, uint, RECT*, void>)Slot(list, 52))(list, view, color, count, rects);'
DSV = '        ((delegate* unmanaged[Stdcall]<nint, nuint, uint, float, byte, uint, RECT*, void>)Slot(list, 51))(list, view, flags, depth, stencil, count, rects);'
INIT = '    private static int InitializeHost()\n    {\n        var factoryId'
CLOSE = '''    private static int CloseList(nint self)
    {
        var host = Host(self);
        var hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(host, 9))(host);
        if (hr >= 0)
        {
            *(int*)(self + ListClosed) = 1;
        }
        return hr;
    }'''
SEAL = '            Environment.FailFast($"[graphics] Closing memory segment failed: 0x{hr:X8}.");'
WAIT_START = '    private static void WaitForHostCompletion(nint queue)\n    {'
SUBMIT_START = '    private static void SubmitWithCommandMemory(nint queue, nint list, nint hostList)'
SUBMIT_LOOP = '''        if (CommandStates.TryGetValue(list, out var state))
        {
            foreach (var boundary in state.MemoryBoundaries)'''
WAIT_CALL = '                WaitForHostCompletion(queue);'
README_HEADING = '## Host graphics validation and segmented completion'


def operations(s):
    s = replace_once(s, RTV, '''        // Host COM calls use Windows interface types, not console vtable indices.
        ((ID3D12GraphicsCommandList*)list)->ClearRenderTargetView(
            new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = view }, color, count, rects);''', 'host RTV clear')
    return replace_once(s, DSV, '''        ((ID3D12GraphicsCommandList*)list)->ClearDepthStencilView(
            new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = view }, (D3D12_CLEAR_FLAGS)flags, depth, stencil, count, rects);''', 'host DSV clear')


def device(s):
    s = replace_once(s, INIT, '    private static int InitializeHost()\n    {\n        EnableHostValidation();\n        var factoryId', 'host validation initialization')
    return replace_once(s, CLOSE, CLOSE.replace('        return hr;', '''        else
        {
            ReportHostCloseFailure(self, hr, "guest-close");
        }
        return hr;'''), 'guest Close')


def memory(s, optimize):
    s = replace_once(s, SEAL, '''            var boundary = write.HasValue ? "write" : wait.HasValue ? "wait" : copy.HasValue ? "copy" : "unknown";
            ReportHostCloseFailure(self, hr, boundary);
''' + SEAL, 'segment Close')
    if not optimize:
        return s
    for anchor in (WAIT_START, SUBMIT_START):
        if s.count(anchor) != 1:
            raise ValueError('completion method boundary changed: ' + anchor)
    start, end = s.index(WAIT_START), s.index(SUBMIT_START)
    if start >= end or 'Thread.Sleep(1);' not in s[start:end] or 'HostRelease(fence)' not in s[start:end]:
        raise ValueError('existing completion method differs from reviewed implementation')
    s = s[:start] + '''    private static void WaitForHostCompletion(nint queue)
    {
        using var completion = new HostCompletionFence();
        completion.Wait(queue);
    }

''' + s[end:]
    s = replace_once(s, SUBMIT_LOOP, '''        if (CommandStates.TryGetValue(list, out var state) && state.MemoryBoundaries.Count != 0)
        {
            using var completion = new HostCompletionFence();
            foreach (var boundary in state.MemoryBoundaries)''', 'segmented submission loop')
    return replace_once(s, WAIT_CALL, '                completion.Wait(queue);', 'segmented GPU completion')


def readme(s, optimize):
    if README_HEADING in s:
        raise ValueError('README patch already present')
    extra = (HERE / 'README-addendum.md').read_text(encoding='utf-8')
    if not optimize:
        extra = extra.replace('The segmented submission now reuses a private completion fence and event per guest command list; each boundary signals a monotonically increasing value and waits only if GPU completion is pending. This removes per-boundary fence creation and 1 ms host-fence polling. Guest memory waits still poll and cross-queue scheduling remains incomplete.', 'The optional event-driven completion optimization is not installed in this ABI-only overlay; guest memory ordering is unchanged.')
    return s.rstrip() + '\n\n' + extra.rstrip() + '\n'


def build(solution, output, optimize):
    root = solution.resolve()
    if root.is_file():
        root = root.parent
    out = output.resolve()
    if out == root or out.exists():
        raise ValueError('Output must be a new, nonexistent directory (never the repository root).')
    if not (root / G).is_dir() or not (root / 'README.md').is_file():
        raise ValueError('Provide the IraRationis solution directory or solution file.')
    paths = {
        G / 'ConsoleGraphicsDevice.CommandOperations.cs': operations,
        G / 'ConsoleGraphicsDevice.cs': device,
        G / 'ConsoleGraphicsDevice.CommandMemory.cs': lambda s: memory(s, optimize),
        Path('README.md'): lambda s: readme(s, optimize),
    }
    files = {}
    for path, edit in paths.items():
        raw = (root / path).read_bytes()
        s = raw.decode('utf-8-sig').replace('\r\n', '\n')
        result = edit(s)
        if b'\r\n' in raw:
            result = result.replace('\n', '\r\n')
        files[path] = (b'\xef\xbb\xbf' if raw.startswith(b'\xef\xbb\xbf') else b'') + result.encode('utf-8')
    new = ['ConsoleGraphicsDevice.HostValidation.cs']
    if optimize:
        new.append('ConsoleGraphicsDevice.HostCompletionFence.cs')
    for name in new:
        path = G / name
        if (root / path).exists():
            raise ValueError(f'New file already exists: {path}')
        files[path] = (HERE / path).read_bytes()
    # Stage only after all source anchors are validated. Never overwrite an existing output.
    out.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='.irx-overlay-', dir=out.parent))
    try:
        for path, data in files.items():
            target = stage / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        if out.exists():
            raise ValueError('Output appeared during staging; refusing to overwrite.')
        stage.rename(out)
    finally:
        if stage.exists():
            shutil.rmtree(stage)
    for path in files:
        print(path)
    print(f'Generated {len(files)} files in {out}. Review then copy these files to your checkout.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--solution', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--abi-only', action='store_true', help='Omit fence optimization to isolate the crash fix.')
    args = parser.parse_args()
    try:
        build(args.solution, args.output, not args.abi_only)
    except (OSError, ValueError) as error:
        parser.exit(1, f'Overlay aborted: {error}\n')


if __name__ == '__main__':
    main()
