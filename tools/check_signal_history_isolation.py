#!/usr/bin/env python3
"""Ensure the committed trading signal differs only by the history observer."""
from pathlib import Path
import re
import subprocess
from check_four_tick_pinbar import method

ROOT = Path(__file__).resolve().parents[1]


def main():
    original = subprocess.check_output(['git', 'show', 'f470557:RangeBarTrading.cs'], cwd=ROOT, text=True)
    current = (ROOT / 'RangeBarTrading.cs').read_text()
    for start, end in (
        ('        // BEGIN HISTORY OBSERVER STATE', '        // END HISTORY OBSERVER STATE'),
        ('        // BEGIN HISTORY OBSERVER METHODS', '        // END HISTORY OBSERVER METHODS'),
        ('        // BEGIN ENTRY DIRECTION FIX', '        // END ENTRY DIRECTION FIX'),
        ('    // BEGIN RANGE BAR JOURNAL --', '    // END RANGE BAR JOURNAL'),
        ('    // BEGIN RANGE BAR JOURNAL WRITER --', '    // END RANGE BAR JOURNAL WRITER'),
    ):
        left = current.index(start)
        right = current.index(end, left) + len(end)
        current = current[:left] + current[right:]
    assert current.count('CaptureTradeHistoryIntent(direction);') == 1
    assert current.count('MaintainTradeHistory();') == 2
    current = current.replace('            CaptureTradeHistoryIntent(direction);', '')
    current = current.replace('                MaintainTradeHistory();', '')
    current = current.replace('            MaintainTradeHistory();', '')
    # The bid-chart repair explicitly permits directional order objects and their
    # submission/name recognition. Every other committed trading line must match.
    original = original.replace(
        '        private IOrderPriced m_PinEntryStop;\n        private IOrderPriced m_ManualEntryStop;',
        '        private IOrderPriced m_PinBuyStop, m_PinSellStop;\n        private IOrderPriced m_ManualBuyStop, m_ManualSellStop;')
    old_creation = '\n'.join([
        '            // Each chart trades one direction. Create one object per named',
        '            // entry type so its recorded name is unambiguous and unique.',
        '            EOrderAction chartEntryAction = IsAskChart ? EOrderAction.Buy : EOrderAction.SellShort;',
        '            m_PinEntryStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "pin bar", chartEntryAction));',
        '            m_ManualEntryStop = OrderCreator.Stop(new SOrderParameters(Contracts.Default, "manual", chartEntryAction));',
    ])
    assert old_creation in original
    original = original.replace(old_creation, '')
    assert current.count('            CreateNamedEntryOrders();') == 1
    current = current.replace('            CreateNamedEntryOrders();', '')
    for name in ('SendEntryStop', 'IsEntryOrderName'):
        original = original.replace(method(original, name), '')
        current = current.replace(method(current, name), '')
    # Ignore blank lines introduced by the bounded observer sections; preserve every other line.
    lines = lambda text: [line for line in text.splitlines() if line.strip()]
    assert lines(current) == lines(original), 'The committed signal changed outside history and the approved directional order repair.'
    observer = (ROOT / 'RangeBarTrading.cs').read_text().split('// BEGIN HISTORY OBSERVER METHODS')[1].split('// END HISTORY OBSERVER METHODS')[0]
    assert not re.search(r'\b(?:GetActiveRangeTicks|SendEntryStop|CancelWorkingEntryOrders|SubmitActiveExitOrders)\s*\(', observer)
    assert 'RangeBarJournal.Append(' not in observer and 'RangeBarJournal.Read(' not in observer
    assert 'WaitForIdle(' not in observer and 'Thread.Sleep(' not in observer
    print('PASS: committed inputs and other trading logic unchanged outside the bid-order repair; history observer does no file I/O or waiting')


if __name__ == '__main__':
    main()
