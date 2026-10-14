#!/usr/bin/env python3
"""Test production trade matching/placement and compile against the installed SDK."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
from check_four_tick_pinbar import method

ROOT = Path(__file__).resolve().parents[1]
CSC = Path("/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")
SDK = Path(os.environ.get("MULTICHARTS_SDK", "/mnt/c/Program Files/TS Support/MultiCharts .NET64 Special Edition"))


def win(path):
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def main():
    source = (ROOT / "RangeBarTradeHistory.cs").read_text()
    shared = (ROOT / "shared/RangeBarJournal.cs").read_text().rstrip()
    for study in (source, (ROOT / "RangeBarTrading.cs").read_text()):
        start = study.index("    // BEGIN RANGE BAR JOURNAL")
        end = study.index("    // END RANGE BAR JOURNAL", start) + len("    // END RANGE BAR JOURNAL")
        assert study[start:end] == shared, "Run tools/sync_range_bar_journal.py to update embedded journal copies"
    writer_source = (ROOT / "shared/RangeBarJournalWriter.cs").read_text().rstrip()
    strategy = (ROOT / "RangeBarTrading.cs").read_text()
    writer_start = strategy.index("    // BEGIN RANGE BAR JOURNAL WRITER")
    writer_end = strategy.index("    // END RANGE BAR JOURNAL WRITER", writer_start) + len("    // END RANGE BAR JOURNAL WRITER")
    assert strategy[writer_start:writer_end] == writer_source, "Sync the signal's background writer"
    model = source[source.index("    internal sealed class HistoryExecution"):
                   source.index("    // END HISTORY MODEL")]
    strategy_source = (ROOT / "RangeBarTrading.cs").read_text().replace(
        "protected override void OnBrokerStategyOrderFilled", "private void OnBrokerStategyOrderFilled").replace(
        "            base.OnBrokerStategyOrderFilled(is_buy, quantity, avg_fill_price);", "")
    journal_methods = "\n".join(method(strategy_source, name) for name in (
        "CaptureTradeHistoryIntent", "NewTradeHistoryRecord", "SnapshotHistoryBar", "OnBrokerStategyOrderFilled",
        "MaintainTradeHistory", "ReportHistoryError"))
    signal_harness = (ROOT / "tests/RangeBarSignalJournalTests.cs").read_text()
    signal_harness = signal_harness[signal_harness.index("// Runs the signal"):].replace("    // PRODUCTION_JOURNAL_METHODS", journal_methods)
    harness = "\n".join(["using System; using System.Collections.Generic; using System.Globalization;",
                         "namespace PowerLanguage.Indicator {", model, (ROOT / "shared/RangeBarJournal.cs").read_text(),
                         (ROOT / "shared/RangeBarJournalWriter.cs").read_text(), "}",
                         (ROOT / "tests/RangeBarTradeHistoryTests.cs").read_text(),
                         (ROOT / "tests/RangeBarJournalTests.cs").read_text(),
                         (ROOT / "tests/RangeBarTradeConsolidationTests.cs").read_text(), signal_harness])
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
