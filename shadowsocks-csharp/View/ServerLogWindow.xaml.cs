using Shadowsocks.Controller;
using Shadowsocks.Controller.HttpRequest;
using Shadowsocks.Model;
using Shadowsocks.Util;
using Shadowsocks.View.Controls;
using Shadowsocks.ViewModel;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Shadowsocks.View
{
    public partial class ServerLogWindow
    {
        public ServerLogWindow(MainController controller, WindowStatus status)
        {
            InitializeComponent();
            I18NUtil.SetLanguage(Resources, @"ServerLogWindow");
            LoadLanguage();

            _controller = controller;
            Closed += (o, e) => { _controller.ConfigChanged -= controller_ConfigChanged; };
            _controller.ConfigChanged += controller_ConfigChanged;
            LoadConfig(true);

            if (status == null)
            {
                SizeToContent = SizeToContent.Width;
                Height = 600;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                SizeToContent = SizeToContent.Manual;
                status.SetStatus(this);
            }
        }

        private void LoadLanguage()
        {
            SetColumnHeader(@"IndexMappingName", @"Index");
            SetColumnHeader(@"GroupMappingName", @"Group");
            SetColumnHeader(@"ServerMappingName", @"Server");
            SetColumnHeader(@"ConnectingMappingName", @"Connecting");
            SetColumnHeader(@"AvgConnectTimeMappingName", @"Latency");
            SetColumnHeader(@"AvgDownloadBytesMappingName", @"AvgDSpeed");
            SetColumnHeader(@"MaxDownSpeedMappingName", @"MaxDSpeed");
            SetColumnHeader(@"AvgUploadBytesMappingName", @"AvgUpSpeed");
            SetColumnHeader(@"MaxUpSpeedMappingName", @"MaxUpSpeed");
            SetColumnHeader(@"TotalDownloadBytesMappingName", @"Dload");
            SetColumnHeader(@"TotalUploadBytesMappingName", @"Upload");
            SetColumnHeader(@"TotalDownloadRawBytesMappingName", @"DloadRaw");
            SetColumnHeader(@"ConnectErrorMappingName", @"Error");
            SetColumnHeader(@"ErrorTimeoutTimesMappingName", @"Timeout");
            SetColumnHeader(@"ErrorEmptyTimesMappingName", @"EmptyResponse");
            SetColumnHeader(@"ErrorContinuousTimesMappingName", @"Continuous");
            SetColumnHeader(@"ErrorPercentMappingName", @"ErrorPercent");
        }

        private void SetColumnHeader(string mappingResourceKey, string headerResourceKey)
        {
            var mappingName = Resources[mappingResourceKey]?.ToString();
            var column = ServerDataGrid.Columns.FirstOrDefault(col => DataGridColumnAutoSizer.GetMappingName(col) == mappingName);
            if (column != null)
            {
                column.Header = this.GetWindowStringValue(headerResourceKey);
            }
        }

        private void LoadConfig(bool isFirstLoad)
        {
            UpdateTitle();
            ServerLogViewModel.ReadConfig();
            ServerDataGrid.Items.Refresh();

            Dispatcher.CurrentDispatcher.InvokeOnUiThread(() =>
            {
                if (isFirstLoad && ServerLogViewModel.SelectedServer != null)
                {
                    ServerDataGrid.ScrollIntoView(ServerLogViewModel.SelectedServer, ServerDataGrid.Columns[2]);
                }
            }, DispatcherPriority.Input);
        }

        private void controller_ConfigChanged(object sender, EventArgs e)
        {
            LoadConfig(false);
        }

        private readonly MainController _controller;
        public ServerLogViewModel ServerLogViewModel { get; set; } = new();

        private void UpdateTitle()
        {
            Title = $@"{this.GetWindowStringValue(@"Title")}({(Global.GuiConfig.ShareOverLan ? this.GetWindowStringValue(@"Any") : this.GetWindowStringValue(@"Local"))}:{Global.GuiConfig.LocalPort} {this.GetWindowStringValue(@"Version")}{Controller.HttpRequest.UpdateChecker.FullVersion})";
        }

        private void AlwaysTopMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
        }

        private void AutoSizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var windowState = WindowState;
            var width = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
            var height = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;

            SizeToContent = SizeToContent.Manual;
            DataGridColumnAutoSizer.AutoSizeColumns(ServerDataGrid);

            if (windowState == WindowState.Normal)
            {
                if (width > 0)
                {
                    Width = width;
                }

                if (height > 0)
                {
                    Height = height;
                }
            }

            WindowState = windowState;
        }

        private void DisconnectDirectMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Server.ForwardServer.Connections.CloseAll();
        }

        private void DisconnectAllMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            _controller.DisconnectAllConnections();
            Server.ForwardServer.Connections.CloseAll();
        }

        private void ClearMaxMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var config = Global.GuiConfig;
            foreach (var server in config.Configs)
            {
                server.SpeedLog.ClearMaxSpeed();
            }
        }

        private void ClearAllMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var config = Global.GuiConfig;
            foreach (var server in config.Configs)
            {
                server.SpeedLog.Clear();
            }
        }

        private void ClearSelectedTotalMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var server = GetSelectedServer();
            if (server != null)
            {
                try
                {
                    _controller.ClearTransferTotal(server.Id);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void ClearTotalMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var config = Global.GuiConfig;
            foreach (var server in config.Configs)
            {
                _controller.ClearTransferTotal(server.Id);
            }
        }

        private void CopyCurrentLinkMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var server = GetSelectedServer();
            if (server != null)
            {
                Clipboard.SetDataObject(server.SsrLink);
            }
        }

        private void CopyCurrentGroupLinksMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var server = GetSelectedServer();
            if (server != null)
            {
                var group = server.Group;
                var link = Global.GuiConfig.Configs.Where(t => t.Group == group).Aggregate(string.Empty, (current, t) => current + $@"{t.SsrLink}{Environment.NewLine}");
                Clipboard.SetDataObject(link);
            }
        }

        private void CopyAllEnableLinksMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var config = Global.GuiConfig;
            var link = config.Configs.Where(t => t.Enable).Aggregate(string.Empty, (current, t) => current + $@"{t.SsrLink}{Environment.NewLine}");
            Clipboard.SetDataObject(link);
        }

        private void CopyAllLinksMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var config = Global.GuiConfig;
            var link = config.Configs.Aggregate(string.Empty, (current, t) => current + $@"{t.SsrLink}{Environment.NewLine}");
            Clipboard.SetDataObject(link);
        }

        private void ServerDataGrid_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            if (HandleRowHeaderClick(e.OriginalSource as DependencyObject, e.GetPosition(ServerDataGrid)))
            {
                e.Handled = true;
                return;
            }

            var cell = VisualTreeHelpers.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell?.DataContext is not Server server)
            {
                return;
            }

            var index = server.Index - 1;
            var mappingName = DataGridColumnAutoSizer.GetMappingName(cell.Column);
            if (mappingName == Resources[@"ServerMappingName"].ToString())
            {
                _controller.DisconnectAllConnections(true);
                _controller.SelectServerIndex(index);
            }
            else if (mappingName == Resources[@"GroupMappingName"].ToString())
            {
                var group = server.Group;
                if (!string.IsNullOrEmpty(group))
                {
                    var enable = !server.Enable;
                    foreach (var sameGroupServer in ServerLogViewModel.ServersCollection)
                    {
                        if (sameGroupServer.Group == group)
                        {
                            sameGroupServer.Enable = enable;
                        }
                    }
                    Global.SaveConfig();
                }
            }
            else
            {
                return;
            }

            SelectFirstCell(server);
        }

        private void ServerDataGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var cell = VisualTreeHelpers.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell?.DataContext is Server server)
            {
                var index = server.Index - 1;
                var mappingName = DataGridColumnAutoSizer.GetMappingName(cell.Column);
                if (mappingName == Resources[@"IndexMappingName"].ToString())
                {
                    _controller.ShowConfigForm(index);
                }
                else if (mappingName == Resources[@"ConnectingMappingName"].ToString())
                {
                    server.Connections.CloseAll();
                }
                else if (mappingName == Resources[@"MaxDownSpeedMappingName"].ToString()
                        || mappingName == Resources[@"MaxUpSpeedMappingName"].ToString())
                {
                    server.SpeedLog.ClearMaxSpeed();
                }
                else if (mappingName == Resources[@"TotalDownloadBytesMappingName"].ToString()
                         || mappingName == Resources[@"TotalUploadBytesMappingName"].ToString())
                {
                    server.SpeedLog.ClearTrans();
                }
                else if (mappingName == Resources[@"TotalDownloadRawBytesMappingName"].ToString())
                {
                    server.SpeedLog.Clear();
                    server.Enable = true;
                }
                else if (mappingName == Resources[@"ConnectErrorMappingName"].ToString()
                         || mappingName == Resources[@"ErrorTimeoutTimesMappingName"].ToString()
                         || mappingName == Resources[@"ErrorEmptyTimesMappingName"].ToString()
                         || mappingName == Resources[@"ErrorContinuousTimesMappingName"].ToString()
                         || mappingName == Resources[@"ErrorPercentMappingName"].ToString())
                {
                    server.SpeedLog.ClearError();
                    server.Enable = true;
                }
                else
                {
                    SelectFirstCell(server);
                }
            }
        }

        private bool HandleRowHeaderClick(DependencyObject source, Point position)
        {
            var rowHeader = VisualTreeHelpers.FindAncestor<DataGridRowHeader>(source);
            if (rowHeader?.DataContext is Server server)
            {
                server.Enable = !server.Enable;
                Global.SaveConfig();
                return true;
            }

            if (position.X > ServerDataGrid.RowHeaderActualWidth || position.Y > ServerDataGrid.ColumnHeaderHeight)
            {
                return false;
            }

            const string columnName = @"Enable";
            var view = CollectionViewSource.GetDefaultView(ServerDataGrid.ItemsSource);
            var oldDescription = view.SortDescriptions.FirstOrDefault(description => description.PropertyName == columnName);
            var direction = oldDescription.PropertyName == columnName && oldDescription.Direction == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
            {
                view.SortDescriptions.Clear();
            }
            else if (oldDescription.PropertyName == columnName)
            {
                view.SortDescriptions.Remove(oldDescription);
            }

            view.SortDescriptions.Add(new SortDescription(columnName, direction));
            return true;
        }

        private void SelectFirstCell(Server server)
        {
            if (ServerDataGrid.Columns.Count == 0)
            {
                return;
            }

            var cellInfo = new DataGridCellInfo(server, ServerDataGrid.Columns[0]);
            ServerDataGrid.UnselectAllCells();
            ServerDataGrid.CurrentCell = cellInfo;
            if (!ServerDataGrid.SelectedCells.Contains(cellInfo))
            {
                ServerDataGrid.SelectedCells.Add(cellInfo);
            }
        }

        private Server GetSelectedServer()
        {
            if (ServerDataGrid.CurrentCell.Item is Server currentServer)
            {
                return currentServer;
            }

            var selectedCell = ServerDataGrid.SelectedCells.FirstOrDefault();
            if (selectedCell.IsValid && selectedCell.Item is Server selectedServer)
            {
                return selectedServer;
            }

            if (ServerDataGrid.SelectedItem is Server selectedItem)
            {
                return selectedItem;
            }

            var config = Global.GuiConfig;
            return config.Index >= 0 && config.Index < config.Configs.Count ? config.Configs[config.Index] : null;
        }
    }
}
