#!/usr/bin/env python3
"""Test production trade matching/placement and compile against the installed SDK."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CSC = Path("/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")
SDK = Path(os.environ.get("MULTICHARTS_SDK", "/mnt/c/Program Files/TS Support/MultiCharts .NET64 Special Edition"))


def win(path):
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def main():
    source = (ROOT / "RangeBarTradeHistory.cs").read_text()
    model = source[source.index("    internal sealed class HistoryExecution"):
                   source.index("    // END HISTORY MODEL")]
    harness = "\n".join(["using System; using System.Collections.Generic; using System.Globalization;",
                         "namespace PowerLanguage.Indicator {", model, "}",
                         (ROOT / "tests/RangeBarTradeHistoryTests.cs").read_text()])
    # C# using directives must precede namespaces; retain test directives up front.
    harness = harness.replace("}\nusing System;\nusing System.Collections.Generic;\nusing PowerLanguage.Indicator;",
                              "}")
    harness = "using PowerLanguage.Indicator;\n" + harness
    windows = not shutil.which("mcs") and CSC.exists()
    if not windows and not (shutil.which("mcs") and shutil.which("mono")):
        raise SystemExit("Install Mono or use WSL with the Windows .NET Framework compiler.")
    with tempfile.TemporaryDirectory(prefix="history-check-", dir="/mnt/c/Windows/Temp" if windows else None) as folder:
        path = Path(folder)
        code, exe = path / "Tests.cs", path / "Tests.exe"
        code.write_text(harness)
        if windows:
            subprocess.run([str(CSC), "/nologo", "/out:" + win(exe), win(code)], check=True)
            subprocess.run([str(exe)], check=True)
        else:
            subprocess.run(["mcs", "-out:" + str(exe), str(code)], check=True)
            subprocess.run(["mono", str(exe)], check=True)
        references = [SDK / name for name in ("PLTypes.dll", "PLStudiesProxy.dll", "PLCommon.dll", "ATCenterProxy.interop.dll")]
        if windows and all(ref.exists() for ref in references):
            indicator = path / "Indicator.cs"
            indicator.write_text(source)
            subprocess.run([str(CSC), "/nologo", "/target:library", "/out:" + win(path / "History.dll")]
                           + ["/r:" + win(ref) for ref in references] + [win(indicator)], check=True)
            print("PASS: full indicator compiles against installed MultiCharts SDK")
            strategy = path / "Strategy.cs"
            strategy.write_text((ROOT / "RangeBarTrading.cs").read_text())
            subprocess.run([str(CSC), "/nologo", "/target:library", "/r:System.Windows.Forms.dll",
                            "/out:" + win(path / "Trading.dll")]
                           + ["/r:" + win(ref) for ref in references]
                           + ["/r:" + win(SDK / "PLBuiltInFunctions.dll"), win(strategy)], check=True)
            print("PASS: full RangeBarTrading strategy compiles against installed MultiCharts SDK")
            # The renderer doesn't instantiate a study or contact a broker.
            renderer = source[source.index("    internal sealed class HistoryExecution"):]
            drawing_test = (ROOT / "tests/RangeBarTradeHistoryDrawingTests.cs").read_text()
            test_start = drawing_test.index("// Exercises")
            render_code = path / "DrawingTests.cs"
            render_code.write_text("using System; using System.Collections.Generic; using System.Globalization;\n"
                                   "using System.Drawing; using System.Drawing.Drawing2D; using PowerLanguage;\n"
                                   "using PowerLanguage.Indicator; namespace PowerLanguage.Indicator {\n"
                                   + renderer + "\n" + drawing_test[test_start:])
            render_exe = path / "DrawingTests.exe"
            subprocess.run([str(CSC), "/nologo", "/out:" + win(render_exe)]
                           + ["/r:" + win(ref) for ref in references] + [win(render_code)], check=True)
            for ref in references:
                shutil.copy2(ref, path / ref.name)
            preview = ROOT / "analysis_output/range_bar_trade_history_preview.png"
            preview.parent.mkdir(exist_ok=True)
            subprocess.run([str(render_exe), win(preview)], check=True)
        else:
            print("SKIP: installed SDK compilation (requires Windows compiler and MultiCharts DLLs)")


if __name__ == "__main__":
    main()
