#!/usr/bin/env python3
"""Embed the common journal in both paste-ready MultiCharts studies."""
from pathlib import Path
root = Path(__file__).resolve().parents[1]
shared = (root / 'shared/RangeBarJournal.cs').read_text().rstrip()
for name in ('RangeBarTrading.cs', 'RangeBarTradeHistory.cs'):
    path = root / name
    source = path.read_text()
    begin = source.find('    // BEGIN RANGE BAR JOURNAL')
    if begin >= 0:
        end = source.index('    // END RANGE BAR JOURNAL', begin) + len('    // END RANGE BAR JOURNAL')
        source = source[:begin] + shared + source[end:]
    else:
        end = source.rfind('}')
        source = source[:end] + shared + '\n' + source[end:]
    path.write_text(source)

# The background writer is signal-only; neither source depends on another study.
path = root / 'RangeBarTrading.cs'
source = path.read_text()
writer = (root / 'shared/RangeBarJournalWriter.cs').read_text().rstrip()
begin = source.find('    // BEGIN RANGE BAR JOURNAL WRITER')
if begin >= 0:
    end = source.index('    // END RANGE BAR JOURNAL WRITER', begin) + len('    // END RANGE BAR JOURNAL WRITER')
    source = source[:begin] + writer + source[end:]
else:
    end = source.rfind('}')
    source = source[:end] + writer + '\n' + source[end:]
path.write_text(source)
