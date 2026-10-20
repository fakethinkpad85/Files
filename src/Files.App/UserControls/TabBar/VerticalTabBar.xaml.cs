// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT;

namespace Files.App.UserControls.TabBar
{
	/// <summary>
	/// Lists the open tabs in a resizable vertical pane. Selection is shared with the rest of the app through
	/// <see cref="AppModel.TabStripSelectedIndex"/>, like the horizontal <see cref="TabBar"/>.
	/// </summary>
	public sealed partial class VerticalTabBar : BaseTabBar
	{
		private readonly ICommandManager Commands = Ioc.Default.GetRequiredService<ICommandManager>();
		private readonly IAppearanceSettingsService AppearanceSettingsService = Ioc.Default.GetRequiredService<IAppearanceSettingsService>();
		private readonly IWindowContext WindowContext = Ioc.Default.GetRequiredService<IWindowContext>();

		private double _preManipulationPaneWidth;
		private bool _isDraggingResizer;
		private TabBarItem? _lastSelectedItem;
		private TabBarItem? _pointerOverItem;
		private string _searchQuery = string.Empty;

		// How long a dragged tab must rest on another tab before a drop groups them instead of reordering
		private static readonly TimeSpan GroupDropDelay = TimeSpan.FromMilliseconds(800);
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _groupDropTimer;
		private TabBarItem? _draggedTab;
		private TabBarItem? _dragHoverTab;
		private TabBarItem? _groupDropTarget;
		private (TabBarItem DraggedTab, TabBarItem Target)? _pendingGroupDrop;

		public bool ShowTabActionsButton
			=> AppearanceSettingsService.ShowTabActions;

		public bool AllowTabsDrag
			=> WindowContext.CanDragAndDrop;

		/// <summary>
		/// Gets or sets the width of the tab pane, kept within the limits declared in <see cref="Constants.UI"/>.
		/// </summary>
		public double PaneWidth
		{
			get => (double)GetValue(PaneWidthProperty);
			set => SetValue(PaneWidthProperty, value);
		}

		public static readonly DependencyProperty PaneWidthProperty =
			DependencyProperty.Register(nameof(PaneWidth), typeof(double), typeof(VerticalTabBar), new PropertyMetadata(Constants.UI.DefaultVerticalTabsWidth, OnPaneWidthChanged));

		public VerticalTabBar()
		{
			InitializeComponent();

			PaneColumn.Width = new GridLength(ClampPaneWidth(PaneWidth));

			_groupDropTimer = DispatcherQueue.CreateTimer();
			_groupDropTimer.IsRepeating = false;
			_groupDropTimer.Interval = GroupDropDelay;
			_groupDropTimer.Tick += GroupDropTimer_Tick;

			Loaded += VerticalTabBar_Loaded;
			Unloaded += VerticalTabBar_Unloaded;
		}

		public override DependencyObject? ContainerFromItem(ITabBarItem item)
		{
			return TabsList.ContainerFromItem(item);
		}

		private void VerticalTabBar_Loaded(object sender, RoutedEventArgs e)
		{
			Items.CollectionChanged += Items_CollectionChanged;
			AppearanceSettingsService.PropertyChanged += AppearanceSettingsService_PropertyChanged;
			TabGroup.GroupsChanged += TabGroup_GroupsChanged;
			TabGroup.GroupCreated += TabGroup_GroupCreated;
		}

		private void VerticalTabBar_Unloaded(object sender, RoutedEventArgs e)
		{
			Items.CollectionChanged -= Items_CollectionChanged;
			AppearanceSettingsService.PropertyChanged -= AppearanceSettingsService_PropertyChanged;
			TabGroup.GroupsChanged -= TabGroup_GroupsChanged;
			TabGroup.GroupCreated -= TabGroup_GroupCreated;
		}

		private void AppearanceSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(IAppearanceSettingsService.ShowTabActions))
				NotifyPropertyChanged(nameof(ShowTabActionsButton));
		}

		// When the selected tab is removed the list keeps the same index for the tab that moves into its place,
		// so the two-way binding writes nothing back. Assign the index explicitly, as TabBar does on item changes,
		// so the app-wide selected tab follows.
		private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (TabsList.SelectedItem is TabBarItem selectedItem)
			{
				_lastSelectedItem = selectedItem;
				App.AppModel.TabStripSelectedIndex = TabsList.SelectedIndex;

				// Like Edge, switching to a tab in a collapsed group expands it
				if (selectedItem.Group is { IsCollapsed: true } group)
					group.IsCollapsed = false;
			}

			RefreshTabs();
			TabView_SelectionChanged(sender, e);
		}

		// Recycled containers keep the previous tab's state.
		private void TabsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			if (args.ItemContainer is ListViewItem container && args.Item is TabBarItem item)
				UpdateTab(container, item, args.ItemIndex);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabItem_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			_pointerOverItem = ((FrameworkElement)sender).DataContext as TabBarItem;
			RefreshTabs();
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabItem_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (ReferenceEquals(((FrameworkElement)sender).DataContext, _pointerOverItem))
				_pointerOverItem = null;

			RefreshTabs();
		}

		private void RefreshTabs()
		{
			for (var i = 0; i < Items.Count; i++)
			{
				if (TabsList.ContainerFromIndex(i) is ListViewItem container)
					UpdateTab(container, Items[i], i);
			}
		}

		/// <summary>
		/// Shows the group header above the first tab of a group, hides the tabs of a collapsed group and tabs
		/// that do not match the search, and draws hover and selection on the tab row.
		/// </summary>
		private void UpdateTab(ListViewItem container, TabBarItem item, int index)
		{
			if (container.ContentTemplateRoot is not FrameworkElement root ||
				root.FindName("GroupHeader") is not FrameworkElement groupHeader ||
				root.FindName("GroupChevron") is not FontIcon groupChevron ||
				root.FindName("GroupBar") is not FrameworkElement groupBar ||
				root.FindName("TabRow") is not Panel tabRow ||
				root.FindName("CloseTabButton") is not UIElement closeButton ||
				root.FindName("GroupDropHint") is not FrameworkElement groupDropHint ||
				root.FindName("GroupDropHintText") is not TextBlock groupDropHintText)
				return;

			var isGroupDropTarget = ReferenceEquals(item, _groupDropTarget);
			groupDropHint.Visibility = isGroupDropTarget ? Visibility.Visible : Visibility.Collapsed;
			if (isGroupDropTarget)
			{
				groupDropHintText.Text = item.Group is not null
					? Strings.DropToAddToTabGroup.GetLocalizedResource()
					: Strings.DropToCreateTabGroup.GetLocalizedResource();
			}

			var group = item.Group;
			var isSearching = !string.IsNullOrWhiteSpace(_searchQuery);
			var isFirstInGroup = group is not null && (index == 0 || Items[index - 1].Group != group);
			var showHeader = isFirstInGroup && !isSearching;
			var showTab = isSearching ? MatchesSearch(item) : group is not { IsCollapsed: true };

			groupHeader.Visibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
			groupChevron.Glyph = group is { IsCollapsed: true } ? "\uE76C" : "\uE70D";
			groupBar.Visibility = group is not null ? Visibility.Visible : Visibility.Collapsed;
			tabRow.Visibility = showTab ? Visibility.Visible : Visibility.Collapsed;
			container.Visibility = showTab || showHeader ? Visibility.Visible : Visibility.Collapsed;

			var isSelected = ReferenceEquals(item, TabsList.SelectedItem);
			var isPointerOver = ReferenceEquals(item, _pointerOverItem);
			tabRow.Background = (isSelected, isPointerOver) switch
			{
				(true, true) => (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
				(true, false) => (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
				(false, true) => (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
				_ => (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
			};

			// Like Edge, the close button shows only on the selected tab and the tab under the pointer
			var showCloseButton = isSelected || isPointerOver;
			closeButton.Opacity = showCloseButton ? 1 : 0;
			closeButton.IsHitTestVisible = showCloseButton;
		}

		// Matches the title and the whole path, so "botnia" finds K:\Monitor\Kunder\Botnia\Undermapp
		private bool MatchesSearch(TabBarItem item)
		{
			var paths = item.NavigationParameter?.NavigationParameter switch
			{
				PaneNavigationArguments paneArguments => new[] { paneArguments.LeftPaneNavPathParam, paneArguments.RightPaneNavPathParam },
				string path => new[] { path },
				_ => [],
			};

			return new[] { item.Header, item.ToolTipText }.Concat(paths)
				.Any(text => text?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) == true);
		}

		private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			_searchQuery = SearchBox.Text.Trim();
			RefreshTabs();
		}

		// Enter switches to the first match and Escape clears the search, as in a browser's tab search
		private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0)
			{
				SearchBox.Text = string.Empty;
				e.Handled = true;
			}
			else if (e.Key == VirtualKey.Enter && Items.FirstOrDefault(MatchesSearch) is TabBarItem match)
			{
				App.AppModel.TabStripSelectedIndex = Items.IndexOf(match);
				SearchBox.Text = string.Empty;
				e.Handled = true;
			}
		}

		private void TabGroup_GroupsChanged(object? sender, EventArgs e)
			=> RefreshTabs();

		private void TabsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
			=> _draggedTab = e.Items.FirstOrDefault() as TabBarItem;

		// The list has finished its own reorder here; grouping earlier would leave its drag visuals behind
		private void TabsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
		{
			var pendingGroupDrop = _pendingGroupDrop;
			_pendingGroupDrop = null;
			_draggedTab = null;
			SetDragHoverTab(null);

			if (pendingGroupDrop is not (TabBarItem draggedTab, TabBarItem target) || args.DropResult is DataPackageOperation.None)
				return;

			var group = target.Group ?? TabGroup.AddToNewGroup(target);
			TabGroup.AddToGroup(draggedTab, group);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabRow_DragEnter(object sender, DragEventArgs e)
		{
			// Entering a child of the row bubbles here too, which must not restart the delay
			if (_draggedTab is not null && ((FrameworkElement)sender).DataContext is TabBarItem tab && tab != _draggedTab && tab != _dragHoverTab)
				SetDragHoverTab(tab);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabRow_DragLeave(object sender, DragEventArgs e)
		{
			var row = (FrameworkElement)sender;
			var position = e.GetPosition(row);

			// Leaving a child of the row bubbles here while the pointer is still on the row
			if (position.X >= 0 && position.Y >= 0 && position.X < row.ActualWidth && position.Y < row.ActualHeight)
				return;

			if (ReferenceEquals(row.DataContext, _dragHoverTab))
				SetDragHoverTab(null);
		}

		// Not handled, so the list still completes its reorder; the grouping follows in DragItemsCompleted
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabRow_Drop(object sender, DragEventArgs e)
		{
			if (_draggedTab is TabBarItem draggedTab &&
				_groupDropTarget is TabBarItem target &&
				ReferenceEquals(((FrameworkElement)sender).DataContext, target))
				_pendingGroupDrop = (draggedTab, target);
		}

		private void SetDragHoverTab(TabBarItem? tab)
		{
			_groupDropTimer.Stop();
			_dragHoverTab = tab;

			if (_groupDropTarget is not null)
			{
				_groupDropTarget = null;
				RefreshTabs();
			}

			if (tab is not null)
				_groupDropTimer.Start();
		}

		private void GroupDropTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
		{
			_groupDropTarget = _dragHoverTab;
			RefreshTabs();
		}

		// A new group opens its editor so it can be named right away, as in Edge
		private void TabGroup_GroupCreated(object? sender, TabGroup group)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				if (group.Members.FirstOrDefault() is TabBarItem firstTab &&
					TabsList.ContainerFromItem(firstTab) is ListViewItem { ContentTemplateRoot: FrameworkElement root } &&
					root.FindName("GroupHeader") is FrameworkElement groupHeader)
					ShowGroupEditor(groupHeader, group);
			});
		}

		// Handled on press so the list does not also select the tab below the header
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void GroupHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			var header = (FrameworkElement)sender;
			e.Handled = true;
			if (header.DataContext is not TabBarItem { Group: TabGroup group } ||
				!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
				return;

			if (group.IsCollapsed)
			{
				group.IsCollapsed = false;
				return;
			}

			// Like Edge, collapsing the group of the selected tab switches to the nearest tab outside it
			if (TabsList.SelectedItem is TabBarItem { Group: TabGroup selectedGroup } && selectedGroup == group)
			{
				var members = group.Members.ToList();
				var afterGroup = Items.IndexOf(members[^1]) + 1;
				var beforeGroup = Items.IndexOf(members[0]) - 1;
				if (afterGroup < Items.Count)
					App.AppModel.TabStripSelectedIndex = afterGroup;
				else if (beforeGroup >= 0)
					App.AppModel.TabStripSelectedIndex = beforeGroup;
				else
					return;
			}

			group.IsCollapsed = true;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void GroupHeader_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			var header = (FrameworkElement)sender;
			if (header.DataContext is not TabBarItem { Group: TabGroup group })
				return;

			e.Handled = true;
			ShowGroupEditor(header, group);
		}

		private void ShowGroupEditor(FrameworkElement target, TabGroup group)
		{
			var nameBox = new TextBox
			{
				Text = group.Name,
				PlaceholderText = Strings.NameThisGroup.GetLocalizedResource(),
			};
			nameBox.TextChanged += (_, _) => group.Name = nameBox.Text;

			var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
			for (var i = 0; i < TabGroup.Colors.Count; i++)
			{
				var colorIndex = i;
				var colorButton = new Button
				{
					Width = 24,
					Height = 24,
					Padding = new Thickness(0),
					CornerRadius = new CornerRadius(12),
					Background = new SolidColorBrush(TabGroup.Colors[i].Color),
					BorderThickness = new Thickness(2),
					BorderBrush = group.ColorIndex == i ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : null,
				};
				AutomationProperties.SetName(colorButton, TabGroup.Colors[i].Name);
				ToolTipService.SetToolTip(colorButton, TabGroup.Colors[i].Name);
				colorButton.Click += (_, _) =>
				{
					group.ColorIndex = colorIndex;
					foreach (var (child, childIndex) in colors.Children.OfType<Button>().Select((child, childIndex) => (child, childIndex)))
						child.BorderBrush = childIndex == colorIndex ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : null;
				};
				colors.Children.Add(colorButton);
			}

			var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

			var ungroupButton = new Button { Content = Strings.UngroupTabs.GetLocalizedResource() };
			ungroupButton.Click += (_, _) =>
			{
				flyout.Hide();
				group.Ungroup();
			};

			var closeGroupButton = new Button { Content = Strings.CloseTabGroup.GetLocalizedResource() };
			closeGroupButton.Click += (_, _) =>
			{
				flyout.Hide();
				DispatcherQueue.TryEnqueue(() => group.Close(this));
			};

			flyout.Content = new StackPanel
			{
				Spacing = 12,
				Children =
				{
					nameBox,
					colors,
					new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ungroupButton, closeGroupButton } },
				},
			};
			flyout.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);
			flyout.Closed += (_, _) => TabGroup.SaveSession();
			flyout.ShowAt(target);
		}

		// The list drops its selection when the selected tab is removed. A drag reorder is a removal followed by
		// an insertion of the same tab, so keep that tab selected; otherwise pick the neighbour, as TabView does.
		private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			// A dragged or inserted tab joins or leaves a group depending on where it lands
			if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move &&
				e.NewItems?[0] is TabBarItem addedItem)
				TabGroup.UpdateGroupAfterMove(addedItem);

			DispatcherQueue.TryEnqueue(RefreshTabs);

			if (e.Action is not NotifyCollectionChangedAction.Remove ||
				e.OldItems?[0] is not TabBarItem removedItem ||
				!ReferenceEquals(removedItem, _lastSelectedItem))
				return;

			var removedIndex = e.OldStartingIndex;
			DispatcherQueue.TryEnqueue(() =>
			{
				if (Items.Count == 0)
					return;

				if (Items.Contains(removedItem))
					TabsList.SelectedItem = removedItem;
				else if (TabsList.SelectedIndex < 0)
					TabsList.SelectedIndex = Math.Clamp(removedIndex, 0, Items.Count - 1);
			});
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void CloseTabButton_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: TabBarItem tabItem })
				DispatcherQueue.TryEnqueue(() => CloseTab(tabItem));
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void TabItem_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (sender is not FrameworkElement { DataContext: TabBarItem tabItem } element ||
				!e.GetCurrentPoint(element).Properties.IsMiddleButtonPressed)
				return;

			e.Handled = true;
			DispatcherQueue.TryEnqueue(() => CloseTab(tabItem));
		}

		private static void OnPaneWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is VerticalTabBar tabBar)
				tabBar.PaneColumn.Width = new GridLength(ClampPaneWidth((double)e.NewValue));
		}

		private static double ClampPaneWidth(double width)
			=> Math.Clamp(width, Constants.UI.MinimumVerticalTabsWidth, Constants.UI.MaximumVerticalTabsWidth);

		private void PaneResizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
		{
			_isDraggingResizer = true;
			_preManipulationPaneWidth = PaneColumn.ActualWidth;
			VisualStateManager.GoToState(this, "ResizerPressed", true);
			e.Handled = true;
		}

		private void PaneResizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
		{
			PaneWidth = ClampPaneWidth(_preManipulationPaneWidth + e.Cumulative.Translation.X);
			e.Handled = true;
		}

		private void PaneResizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
		{
			_isDraggingResizer = false;
			VisualStateManager.GoToState(this, "ResizerNormal", true);
			e.Handled = true;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void PaneResizer_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			((FrameworkElement)sender).ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
			VisualStateManager.GoToState(this, "ResizerPointerOver", true);
			e.Handled = true;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void PaneResizer_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (_isDraggingResizer)
				return;

			((FrameworkElement)sender).ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
			VisualStateManager.GoToState(this, "ResizerNormal", true);
			e.Handled = true;
		}
	}
}
