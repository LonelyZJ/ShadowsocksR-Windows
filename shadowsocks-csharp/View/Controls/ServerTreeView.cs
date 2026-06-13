using Shadowsocks.Model;
using Shadowsocks.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Shadowsocks.View.Controls
{
    public enum ServerTreeDropPosition
    {
        Above,
        Below,
        AsChild
    }

    public class ServerTreeViewItemDroppingEventArgs : EventArgs
    {
        public ServerTreeViewItemDroppingEventArgs(ServerTreeViewModel targetItem, IEnumerable<ServerTreeViewModel> draggingItems, ServerTreeDropPosition dropPosition)
        {
            TargetItem = targetItem;
            DraggingItems = new List<ServerTreeViewModel>(draggingItems);
            DropPosition = dropPosition;
        }

        public ServerTreeViewModel TargetItem { get; }

        public IList<ServerTreeViewModel> DraggingItems { get; }

        public ServerTreeDropPosition DropPosition { get; set; }

        public bool Handled { get; set; }
    }

    public class ServerTreeViewSelectionChangedEventArgs : EventArgs
    {
    }

    public class ServerTreeView : TreeView
    {
        public static readonly DependencyProperty SelectedItemExProperty =
            DependencyProperty.Register(nameof(SelectedItemEx), typeof(ServerTreeViewModel), typeof(ServerTreeView),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemExChanged));

        public static readonly DependencyProperty IsMultiSelectedProperty =
            DependencyProperty.RegisterAttached("IsMultiSelected", typeof(bool), typeof(ServerTreeView), new PropertyMetadata(false));

        private Point _dragStartPoint;
        private ServerTreeViewModel _dragStartItem;
        private bool _isUpdatingSelection;
        private ServerTreeViewModel _rangeAnchor;

        public ServerTreeView()
        {
            SelectedItemsEx = new ObservableCollection<ServerTreeViewModel>();
            SelectedItemsEx.CollectionChanged += SelectedItemsEx_CollectionChanged;
            SelectedItemChanged += ServerTreeView_SelectedItemChanged;
            PreviewMouseLeftButtonDown += ServerTreeView_PreviewMouseLeftButtonDown;
            PreviewMouseRightButtonDown += ServerTreeView_PreviewMouseRightButtonDown;
            MouseMove += ServerTreeView_MouseMove;
            DragOver += ServerTreeView_DragOver;
            Drop += ServerTreeView_Drop;
            AllowDrop = true;
        }

        public ServerTreeViewModel SelectedItemEx
        {
            get => (ServerTreeViewModel)GetValue(SelectedItemExProperty);
            set => SetValue(SelectedItemExProperty, value);
        }

        public ObservableCollection<ServerTreeViewModel> SelectedItemsEx { get; }

        public event EventHandler<ServerTreeViewSelectionChangedEventArgs> SelectionChanged;

        public event EventHandler<ServerTreeViewItemDroppingEventArgs> ItemDropping;

        public static bool GetIsMultiSelected(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsMultiSelectedProperty);
        }

        public static void SetIsMultiSelected(DependencyObject obj, bool value)
        {
            obj.SetValue(IsMultiSelectedProperty, value);
        }

        public void SelectAndBringIntoView(ServerTreeViewModel item)
        {
            if (item == null)
            {
                SetSelection(null, false);
                return;
            }

            ExpandPath(item);
            SetSelection(item, false);
            Dispatcher.InvokeOnUiThread(() =>
            {
                UpdateLayout();
                var container = GetContainerFromItem(item);
                if (container == null)
                {
                    return;
                }

                _isUpdatingSelection = true;
                container.IsSelected = true;
                container.Focus();
                _isUpdatingSelection = false;
                container.BringIntoView();
                UpdateSelectionVisuals();
            }, DispatcherPriority.Loaded);
        }

        public void ExpandAll()
        {
            SetExpansion(true);
        }

        public void CollapseAll()
        {
            SetExpansion(false);
        }

        protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
        {
            base.OnItemsSourceChanged(oldValue, newValue);
            SelectedItemsEx.Clear();
            SetSelectedItemValue(null);
        }

        private static void OnSelectedItemExChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var treeView = (ServerTreeView)d;
            if (treeView._isUpdatingSelection)
            {
                return;
            }

            treeView.SelectAndBringIntoView(e.NewValue as ServerTreeViewModel);
        }

        private void ServerTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_isUpdatingSelection)
            {
                return;
            }

            SetSelection(e.NewValue as ServerTreeViewModel, false);
        }

        private void ServerTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _dragStartItem = GetItemFromPoint(e.OriginalSource as DependencyObject);
            if (_dragStartItem == null)
            {
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ToggleSelection(_dragStartItem);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                SelectRange(_dragStartItem);
                e.Handled = true;
            }
        }

        private void ServerTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = GetItemFromPoint(e.OriginalSource as DependencyObject);
            if (item != null && !SelectedItemsEx.Contains(item))
            {
                SelectAndBringIntoView(item);
            }
        }

        private void ServerTreeView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStartItem == null)
            {
                return;
            }

            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (!SelectedItemsEx.Contains(_dragStartItem))
            {
                SetSelection(_dragStartItem, true);
            }

            DragDrop.DoDragDrop(this, _dragStartItem, DragDropEffects.Move);
            _dragStartItem = null;
        }

        private void ServerTreeView_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetItemFromPoint(e.OriginalSource as DependencyObject) == null ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
        }

        private void ServerTreeView_Drop(object sender, DragEventArgs e)
        {
            var target = GetItemFromPoint(e.OriginalSource as DependencyObject);
            if (target == null)
            {
                return;
            }

            var draggingItems = SelectedItemsEx.Count == 0
                ? new[] { _dragStartItem }.Where(item => item != null).ToList()
                : SortByTreeOrder(SelectedItemsEx).ToList();
            if (draggingItems.Count == 0 || draggingItems.Contains(target) || draggingItems.Any(item => IsDescendant(target, item)))
            {
                return;
            }

            var dropArgs = new ServerTreeViewItemDroppingEventArgs(target, draggingItems, GetDropPosition(e, target));
            ItemDropping?.Invoke(this, dropArgs);
            if (dropArgs.Handled || dropArgs.DraggingItems.Count == 0)
            {
                return;
            }

            MoveItems(dropArgs.TargetItem, SortByTreeOrder(dropArgs.DraggingItems).ToList(), dropArgs.DropPosition);
            e.Handled = true;
        }

        private ServerTreeDropPosition GetDropPosition(DragEventArgs e, ServerTreeViewModel target)
        {
            var container = GetContainerFromItem(target);
            if (container == null)
            {
                return ServerTreeDropPosition.Below;
            }

            var point = e.GetPosition(container);
            if (point.Y < container.ActualHeight * 0.25)
            {
                return ServerTreeDropPosition.Above;
            }
            if (point.Y > container.ActualHeight * 0.75)
            {
                return ServerTreeDropPosition.Below;
            }
            return ServerTreeDropPosition.AsChild;
        }

        private void MoveItems(ServerTreeViewModel target, IList<ServerTreeViewModel> items, ServerTreeDropPosition dropPosition)
        {
            var root = GetRootItems();
            if (root == null)
            {
                return;
            }

            foreach (var item in items)
            {
                GetParentCollection(root, item)?.Remove(item);
            }

            IList<ServerTreeViewModel> targetCollection;
            int insertIndex;
            if (dropPosition == ServerTreeDropPosition.AsChild)
            {
                targetCollection = target.Nodes;
                insertIndex = targetCollection.Count;
            }
            else
            {
                targetCollection = GetParentCollection(root, target);
                if (targetCollection == null)
                {
                    return;
                }
                insertIndex = targetCollection.IndexOf(target);
                if (dropPosition == ServerTreeDropPosition.Below)
                {
                    insertIndex++;
                }
            }

            foreach (var item in items)
            {
                targetCollection.Insert(insertIndex++, item);
            }

            SelectedItemsEx.Clear();
            foreach (var item in items)
            {
                SelectedItemsEx.Add(item);
            }
            SetSelectedItemValue(items[0]);
            SelectionChanged?.Invoke(this, new ServerTreeViewSelectionChangedEventArgs());
        }

        private void ToggleSelection(ServerTreeViewModel item)
        {
            if (SelectedItemsEx.Contains(item))
            {
                SelectedItemsEx.Remove(item);
                if (SelectedItemEx == item)
                {
                    SetSelectedItemValue(SelectedItemsEx.FirstOrDefault());
                }
            }
            else
            {
                SelectedItemsEx.Add(item);
                SetSelectedItemValue(item);
                _rangeAnchor ??= item;
            }
            SelectionChanged?.Invoke(this, new ServerTreeViewSelectionChangedEventArgs());
        }

        private void SelectRange(ServerTreeViewModel item)
        {
            var flatItems = FlattenItems().ToList();
            var anchor = _rangeAnchor ?? SelectedItemEx ?? item;
            var start = flatItems.IndexOf(anchor);
            var end = flatItems.IndexOf(item);
            if (start < 0 || end < 0)
            {
                SetSelection(item, true);
                return;
            }

            if (start > end)
            {
                (start, end) = (end, start);
            }

            SelectedItemsEx.Clear();
            for (var i = start; i <= end; i++)
            {
                SelectedItemsEx.Add(flatItems[i]);
            }
            SetSelectedItemValue(item);
            SelectionChanged?.Invoke(this, new ServerTreeViewSelectionChangedEventArgs());
        }

        private void SetSelection(ServerTreeViewModel item, bool updateContainer)
        {
            _isUpdatingSelection = true;
            SetCurrentValue(SelectedItemExProperty, item);
            SelectedItemsEx.Clear();
            if (item != null)
            {
                SelectedItemsEx.Add(item);
                _rangeAnchor = item;
            }
            _isUpdatingSelection = false;

            if (updateContainer && item != null)
            {
                var container = GetContainerFromItem(item);
                if (container != null)
                {
                    container.IsSelected = true;
                    container.Focus();
                }
            }

            SelectionChanged?.Invoke(this, new ServerTreeViewSelectionChangedEventArgs());
        }

        private void SetSelectedItemValue(ServerTreeViewModel item)
        {
            _isUpdatingSelection = true;
            SetCurrentValue(SelectedItemExProperty, item);
            _isUpdatingSelection = false;
        }

        private void SelectedItemsEx_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            foreach (var item in FlattenItems())
            {
                var container = GetContainerFromItem(item);
                if (container != null)
                {
                    SetIsMultiSelected(container, SelectedItemsEx.Contains(item));
                }
            }
        }

        private void SetExpansion(bool isExpanded)
        {
            UpdateLayout();
            foreach (var item in FlattenItems())
            {
                var container = GetContainerFromItem(item);
                if (container != null)
                {
                    container.IsExpanded = isExpanded;
                }
            }
        }

        private void ExpandPath(ServerTreeViewModel item)
        {
            var root = GetRootItems();
            if (root == null)
            {
                return;
            }

            var path = new Stack<ServerTreeViewModel>();
            var current = item;
            while (current != null)
            {
                path.Push(current);
                current = ServerTreeViewModel.FindParentNode((Collection<ServerTreeViewModel>)root, current);
            }

            UpdateLayout();
            while (path.Count > 0)
            {
                var container = GetContainerFromItem(path.Pop());
                if (container != null)
                {
                    container.IsExpanded = true;
                    container.UpdateLayout();
                }
            }
        }

        private IEnumerable<ServerTreeViewModel> SortByTreeOrder(IEnumerable<ServerTreeViewModel> items)
        {
            var set = new HashSet<ServerTreeViewModel>(items);
            return FlattenItems().Where(set.Contains);
        }

        private IEnumerable<ServerTreeViewModel> FlattenItems()
        {
            var root = GetRootItems();
            return root == null ? Enumerable.Empty<ServerTreeViewModel>() : FlattenItems(root);
        }

        private static IEnumerable<ServerTreeViewModel> FlattenItems(IEnumerable<ServerTreeViewModel> items)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in FlattenItems(item.Nodes))
                {
                    yield return child;
                }
            }
        }

        private ObservableCollection<ServerTreeViewModel> GetRootItems()
        {
            return ItemsSource as ObservableCollection<ServerTreeViewModel>;
        }

        private static IList<ServerTreeViewModel> GetParentCollection(ObservableCollection<ServerTreeViewModel> root, ServerTreeViewModel item)
        {
            if (root.Contains(item))
            {
                return root;
            }

            var parent = ServerTreeViewModel.FindParentNode(root, item);
            return parent?.Nodes;
        }

        private static bool IsDescendant(ServerTreeViewModel item, ServerTreeViewModel possibleParent)
        {
            return possibleParent.Nodes.Contains(item) || possibleParent.Nodes.Any(child => IsDescendant(item, child));
        }

        private ServerTreeViewModel GetItemFromPoint(DependencyObject source)
        {
            return FindAncestor<TreeViewItem>(source)?.DataContext as ServerTreeViewModel;
        }

        private TreeViewItem GetContainerFromItem(ServerTreeViewModel item)
        {
            return GetContainerFromItem(this, item);
        }

        private static TreeViewItem GetContainerFromItem(ItemsControl parent, object item)
        {
            if (parent == null)
            {
                return null;
            }

            parent.UpdateLayout();
            foreach (var current in parent.Items)
            {
                var container = parent.ItemContainerGenerator.ContainerFromItem(current) as TreeViewItem;
                if (container == null)
                {
                    continue;
                }
                if (current == item)
                {
                    return container;
                }

                var child = GetContainerFromItem(container, item);
                if (child != null)
                {
                    return child;
                }
            }

            return null;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                {
                    return target;
                }

                current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
