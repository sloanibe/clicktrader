#!/usr/bin/env python3
"""Test ManualTrading's production exits and compare policy with RangeBarTrading."""
from pathlib import Path
import shutil
import subprocess
import tempfile

from check_four_tick_pinbar import method

ROOT = Path(__file__).resolve().parents[1]


def main():
    manual = (ROOT / "ManualTrading.cs").read_text()
    signal = (ROOT / "RangeBarTrading.cs").read_text()
    harness = (ROOT / "tests/ManualTradingExitTests.cs").read_text()
    # Verify against actual signal constants, rather than hard-coded parity expectations.
    for name in ("SessionProfitTargetTicks", "MinimumAutomaticLossTicks"):
        import re
        value = re.search(r"const int " + name + r" = (\d+);", signal).group(1)
        harness = re.sub(r"\b" + name + r" = \d+", name + " = " + value, harness)
        assert re.search(r"const int " + name + r" = " + value + ";", manual), name
    manual_names = ("ConfigureTradeExits", "GetProtectiveStopLossTicks", "IsOpposingColorClose",
                    "ProcessCompletedBar", "ProcessPartialBar", "StopReached", "TargetReached",
                    "StopTouched", "TargetTouched", "StopFill", "ReadCurrentBar")
    methods = "\n".join(method(manual, name) for name in manual_names)
    # CalcBar is protected in production; exercise it directly with chart-data stubs.
    methods += "\n" + method(manual.replace("protected override void CalcBar", "private void CalcBar"), "CalcBar")
    harness = harness.replace("    // MANUAL_METHODS", methods)
    harness = harness.replace("    // SIGNAL_METHODS", "\n".join(method(signal, name) for name in
        ("GetSessionProfitTargetTicks", "GetProtectiveStopLossTicks", "IsOpposingColorClose")))
    csc = Path("/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")
    windows = not shutil.which("mcs") and csc.exists()
    with tempfile.TemporaryDirectory(prefix="manual-exit-check-", dir="/mnt/c/Windows/Temp" if windows else None) as folder:
        code, executable = Path(folder) / "Tests.cs", Path(folder) / "Tests.exe"
        code.write_text(harness)
        if windows:
            def win(p):
                return subprocess.check_output(["wslpath", "-w", str(p)], text=True).strip()
            subprocess.run([str(csc), "/nologo", "/out:" + win(executable), win(code)], check=True)
            subprocess.run([str(executable)], check=True)
        elif shutil.which("mcs") and shutil.which("mono"):
            subprocess.run(["mcs", "-out:" + str(executable), str(code)], check=True)
            subprocess.run(["mono", str(executable)], check=True)
        else:
            raise SystemExit("Install Mono or run under WSL with the .NET Framework compiler.")


if __name__ == "__main__":
    main()
