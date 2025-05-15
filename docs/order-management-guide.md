# Advanced Order Management in MultiCharts .NET Clicktrader

This document outlines comprehensive order management strategies for the clicktrader implementation, focusing on entry orders, protective stops, take profit levels, and automatic order cancellation.

## Table of Contents
1. [Order Management Workflow](#order-management-workflow)
2. [Entry Orders](#entry-orders)
3. [Protective Stop Orders](#protective-stop-orders)
4. [Take Profit Orders](#take-profit-orders)
5. [OCO (One-Cancels-Other) Implementation](#oco-one-cancels-other-implementation)
6. [Manual Position Management](#manual-position-management)
7. [Implementation Guidelines](#implementation-guidelines)

## Order Management Workflow

The complete order management workflow consists of these key steps:

1. **Place Entry Order**: Initial stop limit order (buy or sell)
2. **Entry Fill Detection**: Monitor for position changes indicating order fill
3. **Automatic Order Placement**: Place protective stop and take profit orders upon entry fill
4. **OCO Group Management**: Link related orders to ensure proper cancellation
5. **Position Monitoring**: Track position status and manage orders accordingly
6. **Exit Handling**: Process exits via stop loss, take profit, or manual intervention

## Entry Orders

Entry orders are the initial orders placed to enter a position:

- **Stop Limit Buy Orders**: Placed X ticks above the click price (Shift+Click)
- **Stop Limit Sell Orders**: Placed X ticks below the click price (Ctrl+Click)
- **Order Parameters**:
  - Stop Price: The price at which the order becomes active
  - Limit Price: The maximum (for buy) or minimum (for sell) price to accept
  - Quantity: Number of contracts/shares to trade

### Entry Order Creation

```csharp
// Create stop limit orders with basic parameters
m_StopLimitBuy = OrderCreator.StopLimit(
    new SOrderParameters(Contracts.Default, "StopLimitBuy", EOrderAction.Buy));
    
m_StopLimitSell = OrderCreator.StopLimit(
    new SOrderParameters(Contracts.Default, "StopLimitSell", EOrderAction.Sell));
```

### Entry Order Submission

```csharp
// For buy orders
m_StopLimitBuy.Send(m_StopPrice, m_LimitPrice, OrderQty);

// For sell orders
m_StopLimitSell.Send(m_StopPrice, m_LimitPrice, OrderQty);
```

## Protective Stop Orders

Protective stop orders are automatically placed after an entry order is filled to limit potential losses:

- **For Long Positions**: Place a stop sell order below the entry price
- **For Short Positions**: Place a stop buy order above the entry price

### Protective Stop Calculation

```csharp
// For long positions (buy entry)
double stopLossPrice = entryFillPrice - (StopLossTicks * tickSize);

// For short positions (sell entry)
double stopLossPrice = entryFillPrice + (StopLossTicks * tickSize);
```

### Protective Stop Creation

```csharp
// For long positions
IOrderStop stopLoss = OrderCreator.Stop(
    new SOrderParameters(Contracts.Default, "ProtectiveStop", EOrderAction.Sell));
stopLoss.Send(stopLossPrice, OrderQty);

// For short positions
IOrderStop stopLoss = OrderCreator.Stop(
    new SOrderParameters(Contracts.Default, "ProtectiveStop", EOrderAction.Buy));
stopLoss.Send(stopLossPrice, OrderQty);
```

## Take Profit Orders

Take profit orders are limit orders placed to secure profits at predetermined price levels:

- **For Long Positions**: Place a limit sell order above the entry price
- **For Short Positions**: Place a limit buy order below the entry price

### Take Profit Calculation

```csharp
// For long positions (buy entry)
double takeProfitPrice = entryFillPrice + (TakeProfitTicks * tickSize);

// For short positions (sell entry)
double takeProfitPrice = entryFillPrice - (TakeProfitTicks * tickSize);
```

### Take Profit Creation

```csharp
// For long positions
IOrderLimit takeProfit = OrderCreator.Limit(
    new SOrderParameters(Contracts.Default, "TakeProfit", EOrderAction.Sell));
takeProfit.Send(takeProfitPrice, OrderQty);

// For short positions
IOrderLimit takeProfit = OrderCreator.Limit(
    new SOrderParameters(Contracts.Default, "TakeProfit", EOrderAction.Buy));
takeProfit.Send(takeProfitPrice, OrderQty);
```

## OCO (One-Cancels-Other) Implementation

OCO groups link related orders so that when one order is filled or canceled, the others in the group are automatically canceled:

### OCO Group Setup

```csharp
// Create OCO group for stop loss and take profit
string ocoGroupId = "OCO_" + DateTime.Now.Ticks;

// Assign OCO group to stop loss order
stopLoss.OCOGroup = ocoGroupId;

// Assign same OCO group to take profit order
takeProfit.OCOGroup = ocoGroupId;
```

### OCO Benefits

1. **Automatic Cancellation**: When stop loss is hit, take profit is canceled (and vice versa)
2. **Reduced Risk**: Eliminates the possibility of duplicate or conflicting orders
3. **Simplified Management**: No need to manually track and cancel related orders

## Manual Position Management

Manual position management allows for direct intervention in the trading process:

### Position Closing

```csharp
// Close long position with market sell order
IOrderMarket closePosition = OrderCreator.Market(
    new SOrderParameters(Contracts.Default, "ClosePosition", EOrderAction.Sell));
closePosition.Send(OrderQty);

// Close short position with market buy order
IOrderMarket closePosition = OrderCreator.Market(
    new SOrderParameters(Contracts.Default, "ClosePosition", EOrderAction.Buy));
closePosition.Send(OrderQty);
```

### Order Cancellation

```csharp
// Cancel all pending orders
private void CancelAllOrders()
{
    // Cancel stop loss order if it exists
    if (m_StopLossOrder != null)
    {
        m_StopLossOrder.Send(0, 0); // Send zero quantity to cancel
    }
    
    // Cancel take profit order if it exists
    if (m_TakeProfitOrder != null)
    {
        m_TakeProfitOrder.Send(0, 0); // Send zero quantity to cancel
    }
    
    // Reset order tracking flags
    m_ActiveStopLossOrder = false;
    m_ActiveTakeProfitOrder = false;
}
```

## Implementation Guidelines

To implement this comprehensive order management system in the clicktrader strategy:

1. **Add New Input Parameters**:
   - `StopLossTicks`: Distance for protective stop orders
   - `TakeProfitTicks`: Distance for take profit orders
   - `UseOCO`: Flag to enable/disable OCO grouping

2. **Add Order Tracking Variables**:
   - `m_StopLossOrder`: Reference to the protective stop order
   - `m_TakeProfitOrder`: Reference to the take profit order
   - `m_ActiveStopLossOrder`: Flag indicating active stop loss
   - `m_ActiveTakeProfitOrder`: Flag indicating active take profit
   - `m_EntryFillPrice`: Price at which the entry order was filled

3. **Enhance CalcBar Method**:
   - Add fill detection logic
   - Implement protective stop and take profit placement
   - Manage OCO grouping
   - Handle position monitoring and cleanup

4. **Modify CancelAllOrders Method**:
   - Enhance to cancel all related orders (entry, stop loss, take profit)
   - Reset all order tracking flags
   - Provide appropriate logging

5. **Add Position Close Functionality**:
   - Implement Delete+Click to close positions and cancel all orders
   - Add market order creation for immediate position closing

By implementing these guidelines, the clicktrader strategy will have a robust order management system that handles entry orders, protective stops, take profit levels, and automatic order cancellation in a comprehensive and efficient manner.
