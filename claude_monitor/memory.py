"""Memory housekeeping.

Keeping the resident set small is a stated goal for this app: it is meant to sit
in a corner of the screen all day, so it should not hold pages it is not using.
Three things do the work, in order of how much they actually save:

1. `usage_log` aggregates transcripts on ingest and never retains per-message
   objects (see that module) - the largest single win.
2. Widget mode skips history parsing entirely and releases the chart data.
3. `trim_working_set()` asks Windows to page out what is currently idle.

Point 3 lowers the *resident* set, not the total commit charge: pages return on
demand when they are touched again. It is a real reduction in what the app holds
in physical RAM while idle, not an accounting trick, but it is not a substitute
for points 1 and 2.
"""

from __future__ import annotations

import ctypes
import sys


def trim_working_set() -> bool:
    """Release idle pages back to Windows. Returns True if the call was made."""
    if sys.platform != "win32":
        return False
    try:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        # HANDLE is pointer-sized: without an explicit restype ctypes truncates
        # the pseudo-handle to a 32-bit int on 64-bit Windows.
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.GetCurrentProcess.argtypes = []
        kernel32.SetProcessWorkingSetSize.argtypes = [
            ctypes.c_void_p,
            ctypes.c_size_t,
            ctypes.c_size_t,
        ]
        kernel32.SetProcessWorkingSetSize.restype = ctypes.c_int
        handle = kernel32.GetCurrentProcess()
        # (SIZE_T)-1 for both bounds tells Windows to trim as much as it can.
        return bool(
            kernel32.SetProcessWorkingSetSize(
                handle, ctypes.c_size_t(-1), ctypes.c_size_t(-1)
            )
        )
    except (OSError, AttributeError, ValueError):
        return False


def working_set_mb() -> float:
    """Current resident set in MB, for the status line. 0.0 if unavailable."""
    if sys.platform != "win32":
        return 0.0

    class _Counters(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_uint32),
            ("PageFaultCount", ctypes.c_uint32),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    try:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.GetCurrentProcess.argtypes = []

        # Modern Windows exports this from kernel32 as K32GetProcessMemoryInfo;
        # psapi.dll is the older home. Try both before giving up.
        query = None
        for library, name in (
            (kernel32, "K32GetProcessMemoryInfo"),
            (ctypes.WinDLL("psapi", use_last_error=True), "GetProcessMemoryInfo"),
        ):
            query = getattr(library, name, None)
            if query is not None:
                break
        if query is None:
            return 0.0

        query.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(_Counters),
            ctypes.c_uint32,
        ]
        query.restype = ctypes.c_int

        counters = _Counters()
        counters.cb = ctypes.sizeof(_Counters)
        if not query(
            kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb
        ):
            return 0.0
        return counters.WorkingSetSize / (1024 * 1024)
    except (OSError, AttributeError, ValueError):
        return 0.0
