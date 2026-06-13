using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Shadowsocks.View.Controls
{
    public static class DataGridColumnAutoSizer
    {
        private const double CellPadding = 16.0;
        private const double HeaderPadding = 28.0;
        private const double MinimumColumnWidth = 24.0;
        private const double MinimumHeaderPadding = 8.0;
        private const double WidthSafetyMargin = 2.0;

        public static readonly DependencyProperty MappingNameProperty =
            DependencyProperty.RegisterAttached("MappingName", typeof(string), typeof(DataGridColumnAutoSizer), new PropertyMetadata(null));

        public static string GetMappingName(DataGridColumn column)
        {
            return (string)column.GetValue(MappingNameProperty);
        }

        public static void SetMappingName(DataGridColumn column, string value)
        {
            column.SetValue(MappingNameProperty, value);
        }

        public static void AutoSizeColumns(DataGrid dataGrid)
        {
            if (dataGrid == null)
            {
                return;
            }

            dataGrid.UpdateLayout();
            var measurements = dataGrid.Columns
                .Where(column => column.Visibility == Visibility.Visible)
                .Select(column => MeasureColumn(dataGrid, column))
                .ToList();
            if (measurements.Count == 0)
            {
                return;
            }

            var targetWidth = GetAvailableColumnsWidth(dataGrid);
            foreach (var measurement in DistributeColumnWidths(measurements, targetWidth))
            {
                measurement.Column.Width = new DataGridLength(measurement.Width, DataGridLengthUnitType.Pixel);
            }
        }

        private static ColumnMeasurement MeasureColumn(DataGrid dataGrid, DataGridColumn column)
        {
            var desiredWidth = CoerceWidth(column, MeasureColumnWidth(dataGrid, column));
            var headerWidth = MeasureText(dataGrid, Convert.ToString(column.Header, CultureInfo.CurrentCulture)) + MinimumHeaderPadding;
            var minimumWidth = CoerceWidth(column, Math.Min(desiredWidth, Math.Max(MinimumColumnWidth, headerWidth)));

            return new ColumnMeasurement(column, Math.Max(desiredWidth, minimumWidth), minimumWidth);
        }

        private static double MeasureColumnWidth(DataGrid dataGrid, DataGridColumn column)
        {
            var width = MeasureText(dataGrid, Convert.ToString(column.Header, CultureInfo.CurrentCulture)) + HeaderPadding;

            if (column is not DataGridBoundColumn boundColumn || boundColumn.Binding is not Binding binding)
            {
                return Math.Max(width, column.ActualWidth);
            }

            var path = binding.Path?.Path;
            if (string.IsNullOrEmpty(path))
            {
                return width;
            }

            foreach (var item in dataGrid.Items)
            {
                if (item == null || item == CollectionView.NewItemPlaceholder)
                {
                    continue;
                }

                var value = GetPropertyValue(item, path);
                var text = FormatBindingValue(value, binding);
                width = Math.Max(width, MeasureText(dataGrid, text) + CellPadding);
            }

            return width;
        }

        private static IEnumerable<ColumnWidth> DistributeColumnWidths(IReadOnlyCollection<ColumnMeasurement> measurements, double targetWidth)
        {
            if (targetWidth <= 0 || double.IsNaN(targetWidth) || double.IsInfinity(targetWidth))
            {
                return measurements.Select(measurement => new ColumnWidth(measurement.Column, measurement.DesiredWidth));
            }

            var desiredTotal = measurements.Sum(measurement => measurement.DesiredWidth);
            var minimumTotal = measurements.Sum(measurement => measurement.MinimumWidth);
            targetWidth = Math.Max(targetWidth, minimumTotal);

            if (targetWidth >= desiredTotal)
            {
                return ExpandColumns(measurements, targetWidth - desiredTotal);
            }

            return ShrinkColumns(measurements, desiredTotal - targetWidth);
        }

        private static IEnumerable<ColumnWidth> ExpandColumns(IReadOnlyCollection<ColumnMeasurement> measurements, double extraWidth)
        {
            var totalWeight = measurements.Sum(measurement => measurement.DesiredWidth);
            return measurements.Select(measurement =>
            {
                var width = measurement.DesiredWidth;
                if (totalWeight > 0)
                {
                    width += extraWidth * measurement.DesiredWidth / totalWeight;
                }

                return new ColumnWidth(measurement.Column, CoerceWidth(measurement.Column, width));
            });
        }

        private static IEnumerable<ColumnWidth> ShrinkColumns(IReadOnlyCollection<ColumnMeasurement> measurements, double shrinkWidth)
        {
            var totalShrinkableWidth = measurements.Sum(measurement => measurement.DesiredWidth - measurement.MinimumWidth);
            return measurements.Select(measurement =>
            {
                var width = measurement.DesiredWidth;
                if (totalShrinkableWidth > 0)
                {
                    width -= shrinkWidth * (measurement.DesiredWidth - measurement.MinimumWidth) / totalShrinkableWidth;
                }

                return new ColumnWidth(measurement.Column, CoerceWidth(measurement.Column, Math.Max(width, measurement.MinimumWidth)));
            });
        }

        private static double GetAvailableColumnsWidth(DataGrid dataGrid)
        {
            var width = dataGrid.ActualWidth;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return 0;
            }

            width -= dataGrid.BorderThickness.Left + dataGrid.BorderThickness.Right;
            width -= dataGrid.RowHeaderActualWidth;

            var verticalScrollBar = FindVisibleScrollBar(dataGrid, Orientation.Vertical);
            if (verticalScrollBar != null)
            {
                width -= verticalScrollBar.ActualWidth;
            }

            return width - WidthSafetyMargin;
        }

        private static ScrollBar FindVisibleScrollBar(DependencyObject root, Orientation orientation)
        {
            if (root == null)
            {
                return null;
            }

            var childrenCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollBar scrollBar
                    && scrollBar.Orientation == orientation
                    && scrollBar.Visibility == Visibility.Visible
                    && scrollBar.ActualWidth > 0)
                {
                    return scrollBar;
                }

                var match = FindVisibleScrollBar(child, orientation);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static double CoerceWidth(DataGridColumn column, double width)
        {
            width = Math.Max(width, column.MinWidth);
            if (!double.IsPositiveInfinity(column.MaxWidth))
            {
                width = Math.Min(width, column.MaxWidth);
            }

            return Math.Max(1.0, width);
        }

        private static object GetPropertyValue(object item, string propertyPath)
        {
            var value = item;
            foreach (var propertyName in propertyPath.Split('.'))
            {
                if (value == null)
                {
                    return null;
                }

                var property = TypeDescriptor.GetProperties(value)[propertyName];
                if (property == null)
                {
                    return null;
                }

                value = property.GetValue(value);
            }

            return value;
        }

        private static string FormatBindingValue(object value, Binding binding)
        {
            if (value == null)
            {
                return binding.TargetNullValue != null && binding.TargetNullValue != DependencyProperty.UnsetValue
                    ? Convert.ToString(binding.TargetNullValue, CultureInfo.CurrentCulture)
                    : string.Empty;
            }

            var stringFormat = binding.StringFormat;
            if (!string.IsNullOrEmpty(stringFormat))
            {
                if (stringFormat.StartsWith("{}", StringComparison.Ordinal))
                {
                    stringFormat = stringFormat.Substring(2);
                }

                try
                {
                    return string.Format(CultureInfo.CurrentCulture, stringFormat, value);
                }
                catch (FormatException)
                {
                    try
                    {
                        return string.Format(CultureInfo.CurrentCulture, $@"{{0:{stringFormat}}}", value);
                    }
                    catch (FormatException)
                    {
                        // Fall back to plain text when the binding format is not a composite format.
                    }
                }
            }

            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static double MeasureText(DataGrid dataGrid, string text)
        {
            var typeface = new Typeface(dataGrid.FontFamily, dataGrid.FontStyle, dataGrid.FontWeight, dataGrid.FontStretch);
            var formattedText = new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentCulture,
                dataGrid.FlowDirection,
                typeface,
                dataGrid.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(dataGrid).PixelsPerDip);

            return formattedText.WidthIncludingTrailingWhitespace;
        }

        private sealed class ColumnMeasurement
        {
            public ColumnMeasurement(DataGridColumn column, double desiredWidth, double minimumWidth)
            {
                Column = column;
                DesiredWidth = desiredWidth;
                MinimumWidth = minimumWidth;
            }

            public DataGridColumn Column { get; }
            public double DesiredWidth { get; }
            public double MinimumWidth { get; }
        }

        private sealed class ColumnWidth
        {
            public ColumnWidth(DataGridColumn column, double width)
            {
                Column = column;
                Width = width;
            }

            public DataGridColumn Column { get; }
            public double Width { get; }
        }
    }
}
