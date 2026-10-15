#!/usr/bin/env python3
"""Ensure signal changes stay within explicitly approved trading/history edits."""
from pathlib import Path
import re
import subprocess
from check_four_tick_pinbar import method

ROOT = Path(__file__).resolve().parents[1]



def remove_annotations(source):
    for name in ('DrawFilledEntryMarkers', 'ClearFilledEntryMarkers', 'FinalizeActiveTradeEntryMarker', 'CheckForHiddenPierceSignals'):
        if re.search(r'        private (?:static )?\w+ ' + name + r'\(', source):
            source = source.replace(method(source, name), '')
    for text in (
        '        // Snapshot the realized strategy P&L when a position opens.  When it\n'
        '        // closes, the delta is the result of that one completed trade.\n',
        '        private double m_ClosedEquityAtEntry = 0;\n',
        '        private int m_StagedPinBodyTicks = 0;\n',
        '        private ITrendLineObject m_GoSignalMarker;\n',
        '        // Filled-trade annotations are retained after the position closes so\n'
        '        // the chart keeps a clean record of executed entries.\n'
        '        private readonly List<IDrawObject> m_TradeEntryMarkers = new List<IDrawObject>();\n'
        '        private IArrowObject m_ActiveTradeEntryArrow;\n',
        '            if (m_GoSignalMarker != null) { m_GoSignalMarker.Delete(); m_GoSignalMarker = null; }\n',
        '                if (m_GoSignalMarker != null) m_GoSignalMarker.Delete();\n',
        '                int filledPinBodyTicks = m_StagedPinBodyTicks;\n',
        '                m_ClosedEquityAtEntry = StrategyInfo.ClosedEquity;\n',
        '                DrawFilledEntryMarkers(currentPosition, entryPrice, tickSize,\n'
        '                                       filledEntrySetup, filledPinBodyTicks);\n',
        '                FinalizeActiveTradeEntryMarker();\n',
        '            ClearFilledEntryMarkers();\n',
        '            if (m_ActiveEntrySetup == EEntrySetup.PinBar)\n'
        '                m_StagedPinBodyTicks = 0;\n',
        '            m_StagedPinBodyTicks = Math.Max(0,\n'
        '                                             GetActivePinBarRangeTicks() -\n'
        '                                             m_PinProjectionTailTicks);\n',
        '                m_StagedPinBodyTicks = m_PinEntryCandidateBodyTicks;\n',
        '                m_StagedPinBodyTicks = 0;\n',
    ):
        source = source.replace(text, '')
    return source

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
    # Execution annotations now belong exclusively to RangeBarTradeHistory.
    original = remove_annotations(original)
    # Manual and pin entries now share optional one-tick confirmation.
    original = original.replace(
        '        // Manual entries on ranges of four ticks or less always require\n'
        '        // one tick beyond completion. This enables the same confirmation\n'
        '        // for larger ranges as well.',
        '        // Shared by manual and pin-bar entries. False enters at completion;\n'
        '        // true requires one tick beyond the boundary in the trade direction.')
    old_manual_price = (
        '            // Small ranges always confirm one tick beyond completion. The\n'
        '            // same entry price drives both the drawing and the native order.\n'
        '            bool requireConfirmation = rangeTicks <= 4 ||\n'
        '                                       ShiftProjectionRequireOneTickConfirmation;\n'
        '            double entryPrice = requireConfirmation\n'
        '                ? RoundToTick(completionPrice + (direction > 0 ? tickSize : -tickSize),\n'
        '                              tickSize)\n'
        '                : completionPrice;')
    assert old_manual_price in original
    original = original.replace(old_manual_price,
        '            // Manual and pin entries share the optional confirmation tick.\n'
        '            double entryPrice = GetConfirmedEntryPrice(completionPrice, direction, tickSize);')
    for name in ('UpdateFourTickPinBarProjection', 'UpdatePinBarProjection', 'ArmOrUpdateProjectedPinBarEntry'):
        before = method(original, name)
        after = before
        if name == 'UpdateFourTickPinBarProjection':
            after = after.replace('            m_PinEntryCandidatePrice = completionPrice;',
                '            double entryPrice = GetConfirmedEntryPrice(completionPrice, direction, tickSize);\n'
                '            m_PinEntryCandidatePrice = entryPrice;')
            after = after.replace('completionPrice, true);', 'entryPrice, true);')
            after = after.replace('UpdatePinBarProjectionLabel(completionPrice,', 'UpdatePinBarProjectionLabel(entryPrice,')
            after = after.replace('                                             completionPrice, direction, tickSize);',
                                  '                                             entryPrice, direction, tickSize);')
        elif name == 'UpdatePinBarProjection':
            after = after.replace('double entryPrice = RoundToTick(completionPrice, tickSize);',
                'double entryPrice = GetConfirmedEntryPrice(completionPrice, direction, tickSize);')
            after = after.replace('completionPrice, false);', 'entryPrice, false);')
            after = after.replace('UpdatePinBarProjectionLabel(completionPrice,', 'UpdatePinBarProjectionLabel(entryPrice,')
            after = after.replace('m_PinBarCompletionLine, completionPrice,', 'm_PinBarCompletionLine, entryPrice,')
        else:
            after = after.replace('            m_StopPrice = RoundToTick(\n'
                '                direction > 0 ? projectedHigh : projectedLow, tickSize);',
                '            m_StopPrice = GetConfirmedEntryPrice(\n'
                '                direction > 0 ? projectedHigh : projectedLow, direction, tickSize);')
        if name == 'UpdateFourTickPinBarProjection':
            after = after.replace('            double tailPrice = 0;',
                '            m_PinEntryCandidateValid = false;\n'
                '            m_PinProjectionQualified = false;\n'
                '            double tailPrice = 0;', 1)
        elif name == 'UpdatePinBarProjection':
            after = after.replace('            if (m_FourTickPinBarMode) {',
                '            m_PinEntryCandidateValid = false;\n'
                '            m_PinProjectionQualified = false;\n'
                '            if (m_FourTickPinBarMode) {', 1)
            after = after.replace('!EnablePinBarTrading || currentPosition != 0 ||',
                                  '!EnablePinBarTrading || currentPosition != 0 || m_KillModeActive ||')
            guard = """            // The current bar and its projected tail must both respect the
            // preceding bar's extreme. A new pin after an invalidated pin is
            // assessed against that immediately preceding bar, not an old setup.
            double epsilon = tickSize * 0.000001;
            bool previousExtremeValid = Bars.CurrentBar > 1 && (direction > 0
                ? Bars.Low[0] >= Bars.Low[1] - epsilon && projectedLow >= Bars.Low[1] - epsilon
                : Bars.High[0] <= Bars.High[1] + epsilon && projectedHigh <= Bars.High[1] + epsilon);
            if (!previousExtremeValid || !IsSupportedPinBarShape(pinBarRangeTicks, bodyTicks) ||
                (Bars.Status == EBarState.Close && Math.Abs(Bars.Close[0] - completionPrice) > epsilon)) {
                ClearPinBarProjectionLines();
                ClearPinBarEntryIfActive();
                return;
            }
"""
            entry_line = '            double entryPrice = GetConfirmedEntryPrice(completionPrice, direction, tickSize);'
            after = after.replace(entry_line, guard + entry_line, 1)
        assert before != after
        original = original.replace(before, after)
    helper = method(current, 'GetConfirmedEntryPrice')
    assert 'ShiftProjectionRequireOneTickConfirmation ? direction * tickSize : 0' in helper
    current = current.replace(helper, '')
    assert 'ShiftProjectionRequireOneTickConfirmation = false;' in current
    # Approved range-boundary break-even for pin/manual positions, using the
    # existing input slot and default. Preserve all other trading code.
    original = original.replace(
        '        // Retained in its original input slot to preserve saved MultiCharts\n'
        '        // settings. Automatic break-even is intentionally no longer used.',
        '        // Pin/manual trades move the stop to entry after one chart range of\n'
        '        // favorable movement from the actual fill. Enabled by default.')
    assert current.count('            UpdateRangeBoundaryBreakEven(currentPosition, tickSize);') == 1
    assert current.index('            UpdateRangeBoundaryBreakEven(currentPosition, tickSize);') < current.index(
        '                SubmitActiveExitOrders(currentPosition);')
    current = current.replace('            UpdateRangeBoundaryBreakEven(currentPosition, tickSize);', '')
    current = current.replace(method(current, 'UpdateRangeBoundaryBreakEven'), '')
    assert 'AutoProtectiveStopOn1BarProfit = true;' in current
    assert 'DrwArrow.Create' not in current
    assert 'DrawFilledEntryMarkers' not in current
    # Ignore blank lines introduced by the bounded observer sections; preserve every other line.
    lines = lambda text: [line for line in text.splitlines() if line.strip()]
    assert lines(current) == lines(original), 'Signal changed outside history, direction repair, legacy annotation removal, and shared confirmation pricing, pin validity guards, and range-boundary break-even.'
    observer = (ROOT / 'RangeBarTrading.cs').read_text().split('// BEGIN HISTORY OBSERVER METHODS')[1].split('// END HISTORY OBSERVER METHODS')[0]
    assert not re.search(r'\b(?:GetActiveRangeTicks|SendEntryStop|CancelWorkingEntryOrders|SubmitActiveExitOrders)\s*\(', observer)
    assert 'RangeBarJournal.Append(' not in observer and 'RangeBarJournal.Read(' not in observer
    assert 'WaitForIdle(' not in observer and 'Thread.Sleep(' not in observer
    print('PASS: only approved trading changes including shared confirmation pricing, pin validity guards, and range-boundary break-even; history observer does no file I/O or waiting')


if __name__ == '__main__':
    main()
