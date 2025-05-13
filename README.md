# MultiCharts ClickTrader

A MultiCharts.NET trading strategy that places stop limit orders based on mouse clicks and visualizes these orders with horizontal lines on the chart.

## Features

- Place stop limit buy orders with a simple Ctrl+Click on the chart
- Configurable offset for order placement (ticks above click price)
- Persistent horizontal lines that remain visible as new price bars form
- Option to clear previous lines when placing new orders
- Configurable order quantity

## Components

1. **clicktrader.cs** - The main strategy that handles mouse events and order placement
2. **clicktrader_lines_indicator.cs** - Companion indicator that draws and manages horizontal lines

## Usage

1. Compile both files in PowerLanguage .NET Editor
2. Add the strategy to a chart
3. Add the indicator to the same chart
4. Configure the strategy inputs as desired
5. Use Ctrl+Click on the chart to place orders

## Configuration Options

### Strategy Inputs
- **TicksAbove**: Number of ticks above click price to place the order
- **OrderQty**: Quantity of contracts/shares to order
- **SimulateOrder**: Whether to simulate orders or place real ones
- **ClearPreviousLines**: Whether to clear previous lines when placing a new order

### Indicator Inputs
- **LineThickness**: Thickness of the horizontal lines
- **UseDashedLine**: Whether to use dashed or solid lines
- **LineColor**: Color of the horizontal lines

## Notes

- The indicator uses `RecoverDrawings(false)` to ensure lines remain visible
- Lines are anchored to bars to prevent them from disappearing with new data
