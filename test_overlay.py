import tempfile
import unittest
from pathlib import Path
import build_overlay as patch
import previous_overlay as old

class OverlayTests(unittest.TestCase):
    def fixture(self, root, prior=False):
        g=root/patch.G
        g.mkdir(parents=True)
        ops=old.RTV+'\n'+old.DSV+'\n        table[51] = guestDepth;\n        table[52] = guestTarget;\n'
        dev=old.INIT+' = default;\n    }\n'+old.CLOSE
        mem=old.SEAL+'\n        ReplayRasterState(self, state);\n'
        if prior:
            ops=old.operations(ops)
            dev=old.device(dev)
            mem=old.memory(mem,False)+'\n        using var completion = new HostCompletionFence();\n'
            (g/'ConsoleGraphicsDevice.HostCompletionFence.cs').write_text('existing fence')
        entries={'CommandOperations':ops,'CommandMemory':mem,'CommandState':'        public nint Pipeline;\n','Synchronization':'        ((ID3D12GraphicsCommandList7*)enhanced)->Barrier(1, (D3D12_BARRIER_GROUP*)&group);\n','RasterState':''.join(f'        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(host, {i}))(host);\n' for i in range(20,25) for _ in range(2))}
        for name,value in entries.items():
            (g/f'ConsoleGraphicsDevice.{name}.cs').write_text(value)
        (g/'ConsoleGraphicsDevice.cs').write_text(dev)
        (root/'README.md').write_text('# identity\n')
    def test_baseline_and_prior(self):
        with tempfile.TemporaryDirectory() as d:
            for prior in (False,True):
                base=Path(d); root=base/f'repo-{prior}'; out=base/f'out-{prior}'
                self.fixture(root,prior)
                before={p:p.read_bytes() for p in root.rglob('*') if p.is_file()}
                patch.build(root,out)
                g=out/patch.G
                ops=(g/'ConsoleGraphicsDevice.CommandOperations.cs').read_text()
                self.assertIn('TraceHostCommand(self, "ClearRenderTargetView")',ops)
                self.assertIn('table[51] = guestDepth;',ops)
                self.assertNotIn(old.RTV,ops)
                mem=(g/'ConsoleGraphicsDevice.CommandMemory.cs').read_text()
                self.assertIn(patch.MARK,mem)
                self.assertIn('state.HostCommandCount = 0;',mem)
                self.assertEqual('new HostCompletionFence()' in mem,prior)
                self.assertIn('# identity',(out/'README.md').read_text())
                self.assertEqual(before,{p:p.read_bytes() for p in root.rglob('*') if p.is_file()})
    def test_reject_changed_source(self):
        with tempfile.TemporaryDirectory() as d:
            base=Path(d); root=base/'repo'; out=base/'out'; self.fixture(root)
            (root/patch.G/'ConsoleGraphicsDevice.Synchronization.cs').write_text('unexpected')
            with self.assertRaises(ValueError): patch.build(root,out)
            self.assertFalse(out.exists())
    def test_reject_incomplete_fence(self):
        with tempfile.TemporaryDirectory() as d:
            base=Path(d); root=base/'repo'; out=base/'out'; self.fixture(root,True)
            (root/patch.G/'ConsoleGraphicsDevice.HostCompletionFence.cs').unlink()
            with self.assertRaises(ValueError): patch.build(root,out)
            self.assertFalse(out.exists())
if __name__=='__main__': unittest.main()
