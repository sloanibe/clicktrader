using System;
using System.Drawing;
using PowerLanguage.Function;

namespace PowerLanguage.Indicator
{
    [RecoverDrawings(false)]
    [SameAsSymbol(true)]
    public class daily_pnl_indicator : IndicatorObject
    {
        [Input] public Color PlotColor { get; set; }
        [Input] public bool LogDailyReset { get; set; }
        [Input] public int TextSize { get; set; }

        private IPlotObject m_DailyPnLPlot;
        private DateTime m_CurrentDay;
        private double m_StartOfDayAccountPnL;
        private bool m_HaveBaseline;
        private string m_TargetAccount;
        private ITextObject m_PnLTextLabel;

        public daily_pnl_indicator(object ctx) : base(ctx)
        {
            PlotColor = Color.Lime;
            LogDailyReset = false;
            TextSize = 14;
            m_TargetAccount = "SIM 001";
        }

        protected override void Create()
        {
            m_DailyPnLPlot = AddPlot(new PlotAttributes("DailyPnL", EPlotShapes.Line, PlotColor));
            ResetBaseline();
        }

        protected override void StartCalc()
        {
            ResetBaseline();
        }

        private void ResetBaseline()
        {
            m_CurrentDay = DateTime.MinValue;
            m_StartOfDayAccountPnL = 0.0;
            m_HaveBaseline = false;
            
            // Clear existing text label
            if (m_PnLTextLabel != null && m_PnLTextLabel.Exist)
            {
                m_PnLTextLabel.Delete();
                m_PnLTextLabel = null;
            }
        }

        protected override void CalcBar()
        {
            var tradeManager = TradeManager;
            if (tradeManager == null)
            {
                Output.WriteLine("DEBUG: TradeManager is null");
                return;
            }

            try
            {
                tradeManager.ProcessEvents();
            }
            catch
            {
                return;
            }

            if (tradeManager.TradingData == null || tradeManager.TradingData.Accounts == null)
            {
                Output.WriteLine("DEBUG: TradingData or Accounts is null");
                return;
            }
            
            var accounts = tradeManager.TradingData.Accounts.Items;
            if (accounts == null || accounts.Length == 0)
            {
                Output.WriteLine("DEBUG: No accounts found");
                return;
            }
            
            Output.WriteLine("DEBUG: Found " + accounts.Length + " accounts");

            double? accountOpenPL = null;
            double? accountEquity = null;
            double? accountBalance = null;
            
            foreach (var account in accounts)
            {
                Output.WriteLine("DEBUG: Account Name=" + account.Name + " Profile=" + account.Profile);
                if (string.Equals(account.Name, m_TargetAccount, StringComparison.OrdinalIgnoreCase))
                {
                    accountOpenPL = account.OpenPL;
                    accountEquity = account.Equity;
                    accountBalance = account.Balance;
                    Output.WriteLine("DEBUG: Matched account! OpenPL=" + accountOpenPL + " Equity=" + accountEquity + " Balance=" + accountBalance);
                    break;
                }
            }

            // Calculate total PnL: realized (Equity - Balance) + unrealized (OpenPL)
            // If broker doesn't provide these, fall back to OpenPL only
            double currentAccountPnL = 0.0;
            
            if (accountEquity.HasValue && accountBalance.HasValue)
            {
                // Total PnL = (Equity - Balance) gives realized + unrealized
                currentAccountPnL = accountEquity.Value - accountBalance.Value;
            }
            else if (accountOpenPL.HasValue)
            {
                // Fallback: use OpenPL only (unrealized)
                currentAccountPnL = accountOpenPL.Value;
            }
            else
            {
                Output.WriteLine("DEBUG: No account data available");
                return;
            }
            
            Output.WriteLine("DEBUG: currentAccountPnL=" + currentAccountPnL.ToString("F2"));

            if (double.IsNaN(currentAccountPnL))
            {
                return;
            }

            DateTime barDay = Bars.Time[0].Date;

            if (!m_HaveBaseline || barDay != m_CurrentDay)
            {
                m_CurrentDay = barDay;
                m_StartOfDayAccountPnL = currentAccountPnL;
                m_HaveBaseline = true;

                if (LogDailyReset)
                {
                    Output.WriteLine("Daily PnL baseline reset at " + Bars.Time[0].ToString("yyyy-MM-dd HH:mm:ss") +
                                     " AccountPnL=" + currentAccountPnL.ToString("F2"));
                }
            }

            double runningPnL = currentAccountPnL - m_StartOfDayAccountPnL;
            Output.WriteLine("DEBUG: runningPnL=" + runningPnL.ToString("F2") + " baseline=" + m_StartOfDayAccountPnL.ToString("F2"));
            m_DailyPnLPlot.Set(runningPnL);
            
            // Update on-chart text display
            UpdatePnLTextDisplay(runningPnL);
        }
        
        private void UpdatePnLTextDisplay(double pnl)
        {
            // Delete old text label if it exists
            if (m_PnLTextLabel != null && m_PnLTextLabel.Exist)
            {
                m_PnLTextLabel.Delete();
            }
            
            // Format PnL text
            string pnlText = string.Format("Session P&L: ${0:N2}", pnl);
            
            // Position text in upper-right area of chart
            // Use current bar time and a high price point
            double textPrice = Bars.High[0] + (Bars.High[0] - Bars.Low[0]) * 0.5;
            ChartPoint textLocation = new ChartPoint(Bars.Time[0], textPrice);
            
            // Create new text label
            m_PnLTextLabel = DrwText.Create(textLocation, pnlText);
            
            // Set color based on profit/loss
            if (pnl >= 0)
            {
                m_PnLTextLabel.Color = Color.Lime;
            }
            else
            {
                m_PnLTextLabel.Color = Color.Red;
            }
            
            // Set text size
            m_PnLTextLabel.Size = TextSize;
        }
    }
}
