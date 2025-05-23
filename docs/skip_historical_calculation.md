# Skipping Historical Calculation in MultiCharts .NET Indicators

## Overview

This document explains how to optimize MultiCharts .NET indicators to skip historical calculations and only process real-time data. This technique is particularly useful for:

- Projection indicators that only need to show current/future values
- Indicators that draw visual elements on the chart
- Indicators that experience long loading times due to historical processing

## Implementation

### 1. Add Real-Time Only Mode

Add the following code to both `StartCalc()` and `CalcBar()` methods:

```csharp
// Skip historical calculation - only calculate for real-time data
if (!Environment.IsRealTimeCalc)
{
    // Optional: Log that we're skipping historical calculation
    Output.WriteLine("Skipping historical calculation - indicator only works in real-time");
    return;
}
```

### 2. Add UpdateOnEveryTick Attribute

If your indicator needs to update on every tick (rather than just on bar close):

```csharp
[UpdateOnEveryTick(true)] // Update on every tick for real-time projections
```

### 3. Add Throttling Mechanism

To prevent excessive updates in fast markets:

```csharp
// Add as a class member
private DateTime m_LastUpdateTime;

// Initialize in StartCalc()
m_LastUpdateTime = DateTime.MinValue;

// In CalcBar() where you do your drawing/updating
DateTime currentTime = Bars.Time[0];
TimeSpan timeSinceLastUpdate = currentTime - m_LastUpdateTime;

// Only update if it's been at least 1 second since the last update or if this is the first update
if (m_NeedToUpdate && (timeSinceLastUpdate.TotalSeconds >= 1 || m_LastUpdateTime == DateTime.MinValue))
{
    // Clear previous drawings before creating new ones
    ClearAllLines();
    
    // Your drawing/calculation code here
    
    // Update the timestamp
    m_LastUpdateTime = currentTime;
}
```

## Complete Example

Here's a complete example of these techniques applied to the `projected_future_renko` indicator:

```csharp
[RecoverDrawings(false)]
[SameAsSymbol(true)]
[UpdateOnEveryTick(true)] // Update on every tick for real-time projections
public class projected_future_renko : IndicatorObject
{
    // Class members including throttling timestamp
    private DateTime m_LastUpdateTime;
    
    protected override void StartCalc()
    {
        ClearAllLines();
        m_LastUpdateTime = DateTime.MinValue; // Initialize throttling timer
        
        // Skip historical calculation - only calculate for real-time data
        if (!Environment.IsRealTimeCalc)
        {
            Output.WriteLine("Skipping historical calculation - indicator only works in real-time");
            return;
        }
        
        // Rest of your initialization code
    }
    
    protected override void CalcBar()
    {
        // Keep indicator active with a constant value that won't affect the chart
        m_Plot.Set(0);
        
        // Skip historical calculation - only calculate for real-time data
        if (!Environment.IsRealTimeCalc)
        {
            return;
        }
        
        // Your regular calculation code
        
        // Throttled drawing code
        DateTime currentTime = Bars.Time[0];
        TimeSpan timeSinceLastUpdate = currentTime - m_LastUpdateTime;
        
        if (m_NeedToUpdate && (timeSinceLastUpdate.TotalSeconds >= 1 || m_LastUpdateTime == DateTime.MinValue))
        {
            // Drawing code
            m_LastUpdateTime = currentTime;
        }
    }
}
```

## Benefits

1. **Faster Loading**: The indicator skips all historical calculations, dramatically reducing loading time
2. **Real-Time Updates**: The indicator still updates on every tick in real-time mode
3. **Stable Performance**: The throttling prevents excessive drawing operations in fast markets
4. **Clean Visuals**: By clearing previous drawings before creating new ones, the indicator maintains clean visuals

## Important Notes

1. This approach is best for indicators that only need to show current/future projections
2. It's not suitable for indicators that need to calculate values based on historical data
3. The indicator will not work in backtesting mode (by design)
4. The throttling interval (1 second) can be adjusted based on your specific needs

## Compatibility

This technique works with all versions of MultiCharts .NET. The `Environment.IsRealTimeCalc` property is available in all versions.
