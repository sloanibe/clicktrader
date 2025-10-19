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

        private IPlotObject m_DailyPnLPlot;
        private DateTime m_CurrentDay;
        private double m_StartOfDayOpenEquity;
        private bool m_HaveBaseline;

        public daily_pnl_indicator(object ctx) : base(ctx)
        {
            PlotColor = Color.Lime;
            LogDailyReset = false;
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
            m_StartOfDayOpenEquity = 0.0;
            m_HaveBaseline = false;
        }

        protected override void CalcBar()
        {
            double openEquity = StrategyInfo.OpenEquity;
            if (double.IsNaN(openEquity))
            {
                return;
            }

            DateTime barDay = Bars.Time[0].Date;

            if (!m_HaveBaseline || barDay != m_CurrentDay)
            {
                m_CurrentDay = barDay;
                m_StartOfDayOpenEquity = openEquity;
                m_HaveBaseline = true;

                if (LogDailyReset)
                {
                    Output.WriteLine("Daily PnL baseline reset at " + Bars.Time[0].ToString("yyyy-MM-dd HH:mm:ss") +
                                     " OpenEquity=" + openEquity.ToString("F2"));
                }
            }

            double runningPnL = openEquity - m_StartOfDayOpenEquity;
            m_DailyPnLPlot.Set(runningPnL);
        }
    }
}
