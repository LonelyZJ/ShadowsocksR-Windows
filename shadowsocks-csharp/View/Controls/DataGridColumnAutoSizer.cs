using System.Windows;
using System.Windows.Controls;

namespace Shadowsocks.View.Controls
{
    public static class DataGridColumnAutoSizer
    {
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

            foreach (var column in dataGrid.Columns)
            {
                column.Width = DataGridLength.Auto;
            }

            dataGrid.UpdateLayout();
            foreach (var column in dataGrid.Columns)
            {
                if (column.ActualWidth > 0)
                {
                    column.Width = new DataGridLength(column.ActualWidth, DataGridLengthUnitType.Pixel);
                }
            }
        }
    }
}
