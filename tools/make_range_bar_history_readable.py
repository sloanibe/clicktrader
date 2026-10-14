#!/usr/bin/env python3
"""Consolidate histories into one readable .txt record per trade using the production store.

Original files are retained. Run with a Windows directory mounted in WSL;
New signal versions consolidate their stream automatically on first real-time calculation.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from check_range_bar_trade_history import CSC, ROOT, win


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    if not args.directory.is_dir():
        parser.error('The history directory must exist.')
    windows = CSC.exists()
    if not windows and not (shutil.which('mcs') and shutil.which('mono')):
        parser.error('Use WSL with the Windows .NET compiler, or install Mono.')
    runner = '''using System; using System.Collections.Generic;
namespace HistoryConversion {
SHARED
class Program {
    static void Main(string[] args) {
        foreach (string oldFile in System.IO.Directory.GetFiles(args[0], "rangebar-*.rbj")) {
            string target = System.IO.Path.ChangeExtension(oldFile, ".txt");
            var history = RangeBarJournal.Read(oldFile);
            if (history.Count == 0) continue;
            foreach (var record in history) RangeBarJournal.Append(target, record);
        }
        foreach (string file in RangeBarJournal.HistoryFiles(args[0])) {
            if (!file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            RangeBarJournal.Consolidate(file);
            Console.WriteLine("Consolidated trade history: " + file);
        }
    }
}
}
'''.replace('SHARED', (ROOT / 'shared/RangeBarJournal.cs').read_text())
    with tempfile.TemporaryDirectory(prefix='history-convert-', dir='/mnt/c/Windows/Temp' if windows else None) as folder:
        source, exe = Path(folder) / 'Convert.cs', Path(folder) / 'Convert.exe'
        source.write_text(runner)
        if windows:
            subprocess.run([str(CSC), '/nologo', '/out:' + win(exe), win(source)], check=True)
            subprocess.run([str(exe), win(args.directory.resolve())], check=True)
        else:
            subprocess.run(['mcs', '-out:' + str(exe), str(source)], check=True)
            subprocess.run(['mono', str(exe), str(args.directory.resolve())], check=True)


if __name__ == '__main__':
    main()
