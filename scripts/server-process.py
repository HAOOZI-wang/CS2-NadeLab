"""Launch CS2 with a real Windows console input handle inside a hidden console.

The dedicated engine always polls console input, even without -console. DEVNULL
is not a console handle and causes an error on every server tick.
"""
import json
import ctypes
import subprocess
import sys
from pathlib import Path


def honor_timer_resolution(process_handle):
    # Windows 11 can otherwise ignore a hidden process's high-resolution timer
    # requests after a period of inactivity. Only adjust this training process.
    class PowerThrottling(ctypes.Structure):
        _fields_ = [('Version', ctypes.c_ulong), ('ControlMask', ctypes.c_ulong), ('StateMask', ctypes.c_ulong)]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.SetProcessInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong]
    kernel.SetProcessInformation.restype = ctypes.c_int
    state = PowerThrottling(1, 4, 0)  # IGNORE_TIMER_RESOLUTION explicitly disabled.
    if not kernel.SetProcessInformation(process_handle, 4, ctypes.byref(state), ctypes.sizeof(state)):
        print(f'WARNING: could not honor server timer resolution (Windows error {ctypes.get_last_error()}).', flush=True)
        return False
    print('CS2 background timer resolution requests will be honored.', flush=True)
    return True


def main():
    executable, *arguments = sys.argv[1:]
    instance = Path(__file__).resolve().parents[1]/'server'
    # The launcher creates this helper's console hidden. CS2 inherits that console;
    # CONIN$ supplies input without a visible interactive terminal or a pipe.
    with open('CONIN$', 'rb', buffering=0) as console_input:
        process = subprocess.Popen([executable, *arguments], cwd=Path(executable).parent,
                                   stdin=console_input, stdout=sys.stdout, stderr=sys.stderr)
        honor_timer_resolution(int(process._handle))
        (instance/'process.json').write_text(json.dumps({'pid': process.pid, 'executable': executable}), encoding='utf-8')
        print(f'CS2 process PID {process.pid}', flush=True)
        return process.wait()


if __name__ == '__main__':
    sys.exit(main())
