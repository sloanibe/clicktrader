using System;

// Production arming methods are injected by check_four_tick_pinbar.py.
// Drawing and broker operations only record calls in this harness.
class PinBarArmingTests {
    enum EEntrySetup { None, PinBar }
    class BarData { public int CurrentBar = 10; }
    class PositionData { public int MarketPosition; }
    BarData Bars = new BarData();
    PositionData StrategyInfo = new PositionData();
    double[] m_SlowEMA = {100, 99};
    bool IsAskChart = true;
    bool m_FourTickPinBarMode, m_AutoEntryArmed, m_HudDisplayEnabled;
    bool m_KillModeActive = true, m_FlattenRequested = true;
    bool m_PinProjectionTailReached, m_PinProjectionBroken, m_PinProjectionQualified;
    bool m_PinProjectionOpenAligned, m_BuyOrderActive, m_SellOrderActive;
    bool m_ShiftProjectionActive;
    int m_ArmedDirection, m_PinProjectionBar, m_PinProjectionDirection;
    int m_PinProjectionTailTicks, m_EmaBounceOrderBar, m_PinBarOrderBar, m_ShiftProjectionBar;
    double m_StopPrice, m_LastSentPrice;
    EEntrySetup m_ActiveEntrySetup;
    int emergencyClears, cancellations, refreshes;
    void ClearEmergencyIndicator() { emergencyClears++; }
    void CancelWorkingEntryOrders() { cancellations++; }
    void ClearPendingEntry() { m_ActiveEntrySetup = EEntrySetup.None; }
    void ClearPinBarProjectionLines() { }
    void ResetEmaBounceProjection() { }
    void ResetEma8BounceProjection() { }
    void ClearEma8BounceProjectionLines() { }
    void ClearProjectedEntryLine() { }
    void ResetAutomaticEntryCandidates() { }
    void UpdatePinBarProjection(double tickSize, int position) { }
    void UpdateEmaBounceProjection(double tickSize, int position) { }
    void UpdateEma8BounceProjection(double tickSize, int position) { }
    void ReconcileAutomaticEntryCandidates(double tickSize, int position) { }
    void RefreshSetupCandidates(double tickSize) { refreshes++; }
    int GetInitialPinBarTailTicks() { return 3; }
    bool IsPinBarOpenOnCorrectEmaSide(int direction, double tickSize) { return true; }
    void ActivateKillMode(int position) {
        m_AutoEntryArmed = m_FourTickPinBarMode = false;
        m_ArmedDirection = 0;
        m_KillModeActive = true;
    }

    // PRODUCTION_METHODS

    static int checks;
    static void Check(bool condition, string name) {
        checks++;
        if (!condition) throw new Exception(name);
    }
    static void Main() {
        foreach (bool ask in new[] {true, false}) {
            int allowed = ask ? 1 : -1;
            var rejected = new PinBarArmingTests { IsAskChart = ask };
            rejected.ArmAutomatedEntryMode(.25, -allowed);
            Check(!rejected.m_AutoEntryArmed && rejected.m_ArmedDirection == 0,
                  "opposite direction cannot arm");
            Check(rejected.m_KillModeActive && rejected.m_FlattenRequested &&
                  rejected.emergencyClears == 0, "rejection preserves inactive state");

            var accepted = new PinBarArmingTests { IsAskChart = ask };
            accepted.ArmAutomatedEntryMode(.25, allowed);
            Check(accepted.m_AutoEntryArmed && accepted.m_ArmedDirection == allowed &&
                  !accepted.m_KillModeActive, "permitted direction arms");
            accepted.ArmAutomatedEntryMode(.25, -allowed);
            Check(accepted.m_AutoEntryArmed && accepted.m_ArmedDirection == allowed &&
                  accepted.emergencyClears == 1, "rejection preserves existing permitted watch");

            foreach (int trend in new[] {1, -1}) {
                var automatic = new PinBarArmingTests { IsAskChart = ask };
                automatic.m_SlowEMA[0] = 99 + trend;
                automatic.ArmAutomatedEntryMode(.25);
                Check(automatic.m_AutoEntryArmed == (trend == allowed),
                      "EMA-derived direction respects chart role");
            }

            var watch = new PinBarArmingTests { IsAskChart = ask };
            watch.ToggleFourTickPinBarWatch(.25);
            Check(watch.m_AutoEntryArmed && watch.m_FourTickPinBarMode &&
                  watch.m_ArmedDirection == allowed && watch.refreshes == 1,
                  "Ctrl-click watch uses permitted chart direction");
            watch.ToggleFourTickPinBarWatch(.25);
            Check(!watch.m_AutoEntryArmed && watch.m_ArmedDirection == 0,
                  "second Ctrl-click disarms");
            watch.StrategyInfo.MarketPosition = allowed;
            watch.ToggleFourTickPinBarWatch(.25);
            Check(!watch.m_AutoEntryArmed && watch.cancellations == 2,
                  "Ctrl-click leaves open position alone");
        }
        Console.WriteLine("PASS: " + checks + " pin-bar arming assertions");
    }
}
