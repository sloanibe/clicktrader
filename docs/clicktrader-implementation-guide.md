# MultiCharts .NET Clicktrader Implementation Guide

## Overview

This document provides a detailed explanation of the clicktrader strategy implementation for MultiCharts .NET. The strategy allows placing stop limit buy orders by Ctrl+clicking on the chart and canceling orders by Ctrl+clicking near an existing order.

## Key Features

1. **Click-to-Trade**: Place stop limit buy orders by Ctrl+clicking on the chart
2. **Intuitive Order Cancellation**: Cancel orders by Ctrl+clicking near an existing order
3. **OCO Order Support**: Uses One-Cancels-Others order groups for reliable order management
4. **Development Mode**: Toggle detailed logging for debugging purposes

## Implementation Details

### Required Attributes

```csharp
[MouseEvents(true), IOGMode(IOGMode.Enabled), RecoverDrawings(false)]
[SameAsSymbol(true)]
```

- `MouseEvents(true)`: Enables mouse event handling
- `IOGMode(IOGMode.Enabled)`: Enables Intra-bar Order Generation for responsive order handling
- `RecoverDrawings(false)`: Prevents drawings from being recovered after reloading
- `SameAsSymbol(true)`: Ensures the strategy works with the chart symbol

### Input Parameters

```csharp
[Input] public int TicksAbove { get; set; }
[Input] public int OrderQty { get; set; }
[Input] public Keys CancelOrderKey { get; set; }
[Input] public bool Development { get; set; }
[Input] public bool UseOCO { get; set; }
```

- `TicksAbove`: Number of ticks above the click price to place the stop price
- `OrderQty`: Quantity of contracts to trade
- `CancelOrderKey`: Keyboard key to cancel orders (F12 by default)
- `Development`: Toggle development mode for detailed logging
- `UseOCO`: Toggle OCO (One-Cancels-Others) order groups

### Order Objects and State Variables

```csharp
private IOrderStopLimit m_StopLimitBuy;
private double m_StopPrice;
private double m_LimitPrice;
private bool m_OrderCreatedInMouseEvent = false;
private bool m_ActiveStopLimitOrder = false;
```

- `m_StopLimitBuy`: The stop limit order object
- `m_StopPrice`: The stop price for the order
- `m_LimitPrice`: The limit price for the order
- `m_OrderCreatedInMouseEvent`: Flag to indicate an order was created in the mouse event
- `m_ActiveStopLimitOrder`: Flag to track if there's an active order

### Initialization in Create()

```csharp
protected override void Create()
{
    base.Create();

    // Create a stop limit buy order with basic parameters
    m_StopLimitBuy = OrderCreator.StopLimit(
        new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));

    // Set debug flag based on development mode
    m_Debug = Development;
}
```

The `Create()` method initializes the order object. **Important**: Order objects can only be created in the `Create()` method in MultiCharts .NET.

### Mouse Event Handling

The strategy uses the `OnMouseEvent` method to handle mouse clicks:

```csharp
protected override void OnMouseEvent(MouseClickArgs arg)
{
    // Only process left mouse clicks
    if (arg.buttons != MouseButtons.Left)
        return;

    // Check for F12 key in mouse events
    if (arg.keys == CancelOrderKey)
    {
        CancelAllOrders();
        return;
    }
    
    // Handle Ctrl+click for both order cancellation and creation
    if (arg.keys == Keys.Control)
    {
        // Get the current click price
        double currentClickPrice = arg.point.Price;
        double currentTickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
        
        // Check if we have an active order and are clicking near it
        if (m_ActiveStopLimitOrder)
        {
            // Check if click is within 5 ticks of the pending order's stop price
            if (Math.Abs(currentClickPrice - m_StopPrice) <= (5 * currentTickSize))
            {
                CancelAllOrders();
                return;
            }
        }
        
        // If we get here, we're creating a new order
        CancelAllOrders();
        m_ClickPrice = currentClickPrice;
        m_OrderCreatedInMouseEvent = true;
    }
}
```

Key aspects:
1. Detect Ctrl+click events
2. If clicking near an existing order (within 5 ticks), cancel it
3. Otherwise, set up a new order to be processed in `CalcBar()`

### Order Processing in CalcBar()

```csharp
protected override void CalcBar()
{
    try
    {
        // Process new orders that were created in OnMouseEvent
        if (m_OrderCreatedInMouseEvent && m_ClickPrice > 0 && StrategyInfo.MarketPosition == 0)
        {
            // Calculate stop price (X ticks above click)
            double tickSize = Bars.Info.MinMove / Bars.Info.PriceScale;
            m_StopPrice = m_ClickPrice + (TicksAbove * tickSize);
            m_LimitPrice = m_StopPrice + tickSize;

            // Cancel any existing orders first
            if (m_ActiveStopLimitOrder)
            {
                CancelAllOrders();
            }

            // Send the stop limit order with stop and limit prices
            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
            
            // Set flags
            m_ActiveStopLimitOrder = true;
            m_OrderCreatedInMouseEvent = false;
            m_ClickPrice = 0;
        }
        // If we have an active order and we're still flat, keep it alive
        else if (m_ActiveStopLimitOrder && StrategyInfo.MarketPosition == 0)
        {
            // Resubmit the order each bar to keep it active
            m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);
        }
        // If we have a position, stop resubmitting the order
        else if (StrategyInfo.MarketPosition > 0 && m_ActiveStopLimitOrder)
        {
            m_ActiveStopLimitOrder = false;
        }
    }
    catch (Exception ex)
    {
        Output.WriteLine("Error in CalcBar: " + ex.Message);
    }
}
```

Key aspects:
1. Process new orders flagged by `OnMouseEvent`
2. Calculate stop and limit prices based on the click price
3. Cancel any existing orders before placing a new one
4. Resubmit active orders each bar to keep them alive (required by MultiCharts)
5. Stop resubmitting orders once a position is opened

### Order Cancellation

```csharp
private void CancelAllOrders()
{
    try
    {
        if (m_StopLimitBuy != null && m_ActiveStopLimitOrder)
        {
            try
            {
                // Send with zero quantity to force cancellation
                m_StopLimitBuy.Send(0, 0, 0);
            }
            catch (Exception sendEx)
            {
                // Try another approach if the first one fails
                try
                {
                    // Send with a far-away price
                    double farAwayPrice = Bars.Close[0] * 100;
                    m_StopLimitBuy.Send(farAwayPrice, farAwayPrice, 0);
                }
                catch {}
            }
        }
        
        // Reset all order flags
        m_ActiveStopLimitOrder = false;
    }
    catch (Exception ex)
    {
        Output.WriteLine("Error in CancelAllOrders: " + ex.Message);
    }
}
```

Key aspects:
1. Send a zero-quantity order to force cancellation
2. If that fails, try sending an order with a far-away price
3. Reset the active order flag to prevent resubmission

## Important Implementation Notes

1. **Order Creation**: Orders can only be created in the `Create()` method using `OrderCreator`
2. **Order Resubmission**: Orders must be continuously resubmitted each bar to stay active
3. **Order Cancellation**: To cancel an order, stop resubmitting it and send a zero-quantity order
4. **OCO Groups**: In auto-trading mode, orders are sent in OCO groups where one order cancels others
5. **Position Tracking**: Use `StrategyInfo.MarketPosition` to track positions on the chart and `StrategyInfo.MarketPositionAtBroker` for broker positions

## Common Issues and Solutions

### Order Not Showing Up

If an order doesn't appear after clicking:
- Ensure `IOGMode(IOGMode.Enabled)` is set
- Check that `m_OrderCreatedInMouseEvent` is being set to true
- Verify `CalcBar()` is processing the order correctly

### Orders Not Canceling

If orders aren't canceling properly:
- Try both cancellation methods (Ctrl+click near order and F12 key)
- Check that `CancelAllOrders()` is being called
- Ensure `m_ActiveStopLimitOrder` is being reset to false
- Try sending a zero-quantity order to force cancellation

### Multiple Orders Being Created

If multiple orders are created unintentionally:
- Ensure `CancelAllOrders()` is called before creating a new order
- Check that the 5-tick proximity check is working correctly
- Verify `m_ActiveStopLimitOrder` is being properly tracked

## Best Practices

1. **Error Handling**: Always wrap order operations in try-catch blocks
2. **Logging**: Use detailed logging in development mode to track order operations
3. **Flag Management**: Carefully manage state flags to track order status
4. **Position Checking**: Always check market position before placing orders
5. **Tick Size**: Use the correct tick size from `Bars.Info` for price calculations

By following this guide, you should be able to recreate the clicktrader strategy or implement similar order management functionality in MultiCharts .NET.
