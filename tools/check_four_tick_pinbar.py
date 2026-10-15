#!/usr/bin/env python3
"""Exercise actual RangeBarTrading pin projections and session profit rules.

Runs with Mono (mcs + mono), or the Windows .NET Framework compiler under WSL.
No broker connection or strategy activation is involved.
"""
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def method(source, name):
    start = re.search(r"        private (?:static )?\w+ " + re.escape(name) + r"\(", source).start()
    body = source.index("{", start)
    depth = 1
    end = body + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    source = (ROOT / "RangeBarTrading.cs").read_text()
    suites = {
        "ClickModifierTests": ("HasModifier",),
        "PinBarArmingTests": ("ArmAutomatedEntryMode", "ToggleFourTickPinBarWatch",
                              "IsEntryDirectionAllowed", "IsFourTickPinBarEmaOrderValid"),
        "ShiftManualEntryTests": ("GetConfirmedEntryPrice", "StartShiftProjectionEntry", "UpdateShiftProjectionEntry", "SubmitShiftProjectionEntryNow", "ClearShiftProjectionEntry", "SendEntryStop"),
        "NamedEntryOrderTests": ("CreateNamedEntryOrders", "IsEntryDirectionAllowed", "SendEntryStop", "IsEntryOrderName", "IsThisStrategyOrder", "IsWorkingOrder", "CancelWorkingEntryOrders"),
        "FourTickPinBarTests": ("GetConfirmedEntryPrice", "UpdateFourTickPinBarProjection", "TryGetFourTickPinBarProjection",
                                "IsFourTickPinBarContextValid", "IsFourTickPinBarEmaOrderValid"),
        "PinBarInvalidationTests": ("GetConfirmedEntryPrice", "UpdatePinBarProjection", "ClearPinBarEntryIfActive"),
        "SessionProfitRuleTests": ("GetProfitTargetTicksForNewTrade", "GetSessionProfitTargetTicks",
                                   "IsOpposingColorClose", "UpdateSessionProfitExit", "ClampAutomaticLossExitPrice",
                                   "SubmitActiveExitOrders",
                                   "UpdateSessionExitProjectionLines", "ClearPinBarProjectionLines"),
        "AutomaticLossExitTests": ("GetProtectiveStopLossTicks", "ClampAutomaticLossExitPrice",
                                   "UpdateOppositeColorExitManagement", "SubmitActiveExitOrders"),
        "RangeBoundaryBreakEvenTests": ("UpdateRangeBoundaryBreakEven", "SubmitActiveExitOrders"),
    }
    csc = Path("/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")
    windows = not shutil.which("mcs") and csc.exists()
    temp_root = "/mnt/c/Windows/Temp" if windows else None
    for suite, names in suites.items():
        methods = "\n".join(method(source, name) for name in names)
        harness = (ROOT / "tests" / (suite + ".cs")).read_text().replace(
            "    // PRODUCTION_METHODS", methods)
        with tempfile.TemporaryDirectory(prefix="pinbar-check-", dir=temp_root) as folder:
            path = Path(folder)
            code, executable = path / "Tests.cs", path / "Tests.exe"
            code.write_text(harness)
            if windows:
                def win(p):
                    return subprocess.check_output(["wslpath", "-w", str(p)], text=True).strip()
                subprocess.run([str(csc), "/nologo", "/out:" + win(executable), win(code)], check=True)
                subprocess.run([str(executable)], check=True)
            elif shutil.which("mcs") and shutil.which("mono"):
                subprocess.run(["mcs", "-r:System.Drawing", "-out:" + str(executable), str(code)], check=True)
                subprocess.run(["mono", str(executable)], check=True)
            else:
                raise SystemExit("Install Mono or run under WSL with the .NET Framework compiler.")



if __name__ == "__main__":
    main()
