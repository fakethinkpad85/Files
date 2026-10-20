// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Files.App.UserControls.TabBar
{
	/// <summary>
	/// Provides the flyouts shared by every <see cref="ITabBar"/> implementation:
	/// the context menu of a tab and the tab actions menu.
	/// Items are rebuilt each time a menu opens so they reflect the current state of the commands.
	/// </summary>
	public static class TabBarFlyouts
	{
		private static MenuFlyout? _tabContextFlyout;
		private static MenuFlyout? _tabActionsFlyout;
		private static TabBarItem? _contextMenuTarget;

		/// <summary>
		/// Raised with the tab a context menu targets while the menu is open, and with null once it closes.
		/// </summary>
		public static event EventHandler<TabBarItem?>? SelectedTabItemChanged;

		public static MenuFlyout TabContextFlyout
			=> _tabContextFlyout ??= CreateTabContextFlyout();

		public static MenuFlyout TabActionsFlyout
			=> _tabActionsFlyout ??= CreateTabActionsFlyout();

		private static ICommandManager Commands
			=> Ioc.Default.GetRequiredService<ICommandManager>();

		private static MenuFlyout CreateTabContextFlyout()
		{
			var flyout = new MenuFlyout();

			flyout.Opening += (_, _) =>
			{
				_contextMenuTarget = flyout.Target?.DataContext as TabBarItem;
				SelectedTabItemChanged?.Invoke(null, _contextMenuTarget);
				Populate(flyout, CreateTabContextItems());
			};
			flyout.Closing += (_, _) => SelectedTabItemChanged?.Invoke(null, null);

			return flyout;
		}

		private static MenuFlyout CreateTabActionsFlyout()
		{
			var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
			flyout.Opening += (_, _) => Populate(flyout, CreateTabActionsItems());

			return flyout;
		}

		private static void Populate(MenuFlyout flyout, IEnumerable<MenuFlyoutItemBase> items)
		{
			flyout.Items.Clear();
			foreach (var item in items)
				flyout.Items.Add(item);
		}

		private static IEnumerable<MenuFlyoutItemBase> CreateTabContextItems()
		{
			var commands = Commands;

			var moveTabToNewWindow = new MenuFlyoutItem
			{
				Text = Strings.HorizontalMultitaskingControlMoveTabToNewWindow_Text.GetLocalizedResource(),
				Icon = new FontIcon { Glyph = "" },
				IsEnabled = MainPageViewModel.AppInstances.Count > 1,
			};
			moveTabToNewWindow.Click += MoveTabToNewWindow_Click;

			yield return CommandItem(commands.NewTab);
			yield return CommandItem(commands.DuplicateSelectedTab);
			yield return moveTabToNewWindow;
			yield return CommandItem(commands.CloseTabsToTheLeftSelected);
			yield return CommandItem(commands.CloseTabsToTheRightSelected);
			yield return CommandItem(commands.CloseOtherTabsSelected);
			yield return CommandItem(commands.ReopenClosedTab);

			// Groups are only shown by the vertical tab bar
			if (_contextMenuTarget is not TabBarItem tab || !Ioc.Default.GetRequiredService<IAppearanceSettingsService>().UseVerticalTabs)
				yield break;

			yield return new MenuFlyoutSeparator();

			var addToNewGroup = new MenuFlyoutItem { Text = Strings.AddTabToNewGroup.GetLocalizedResource() };
			addToNewGroup.Click += (_, _) => _ = TabGroup.AddToNewGroup(tab);
			yield return addToNewGroup;

			var otherGroups = TabGroup.AllGroups.Where(group => group != tab.Group).ToList();
			if (otherGroups.Count > 0)
			{
				var addToGroup = new MenuFlyoutSubItem { Text = Strings.AddTabToGroup.GetLocalizedResource() };
				foreach (var group in otherGroups)
				{
					var groupItem = new MenuFlyoutItem
					{
						Text = group.DisplayName,
						Icon = new FontIcon { Glyph = "", Foreground = group.Brush },
					};
					groupItem.Click += (_, _) => TabGroup.AddToGroup(tab, group);
					addToGroup.Items.Add(groupItem);
				}

				yield return addToGroup;
			}

			if (tab.Group is not null)
			{
				var removeFromGroup = new MenuFlyoutItem { Text = Strings.RemoveTabFromGroup.GetLocalizedResource() };
				removeFromGroup.Click += (_, _) => TabGroup.RemoveFromGroup(tab);
				yield return removeFromGroup;
			}
		}

		private static IEnumerable<MenuFlyoutItemBase> CreateTabActionsItems()
		{
			var commands = Commands;

			yield return ThemedCommandItem(commands.NewWindow);

			if (commands.EnterCompactOverlay.IsExecutable)
				yield return ThemedCommandItem(commands.EnterCompactOverlay);

			if (commands.ExitCompactOverlay.IsExecutable)
				yield return ThemedCommandItem(commands.ExitCompactOverlay);

			if (commands.SplitPaneHorizontally.IsExecutable)
			{
				var splitPane = new MenuFlyoutSubItem { Text = Strings.SplitPane.GetLocalizedResource() };
				splitPane.Items.Add(ThemedCommandItem(commands.SplitPaneVertically));
				splitPane.Items.Add(ThemedCommandItem(commands.SplitPaneHorizontally));
				yield return splitPane;
			}

			if (commands.CloseActivePane.IsExecutable)
			{
				var arrangePanes = new MenuFlyoutSubItem { Text = Strings.ArrangePanes.GetLocalizedResource() };
				arrangePanes.Items.Add(ThemedToggleItem(commands.ArrangePanesVertically));
				arrangePanes.Items.Add(ThemedToggleItem(commands.ArrangePanesHorizontally));
				yield return arrangePanes;

				yield return ThemedCommandItem(commands.CloseActivePane);
			}
		}

		private static MenuFlyoutItem CommandItem(IRichCommand command)
			=> new()
			{
				Text = command.Label,
				Command = command,
				KeyboardAcceleratorTextOverride = command.HotKeyText,
			};

		private static MenuFlyoutItemWithThemedIcon ThemedCommandItem(IRichCommand command)
			=> new()
			{
				Text = command.Label,
				Command = command,
				KeyboardAcceleratorTextOverride = command.HotKeyText,
				ThemedIconStyle = command.ThemedIconStyle,
			};

		private static ToggleMenuFlyoutItemWithThemedIcon ThemedToggleItem(IRichCommand command)
			=> new()
			{
				Text = command.Label,
				Command = command,
				IsChecked = command.IsOn,
				KeyboardAcceleratorTextOverride = command.HotKeyText,
				ThemedIconStyle = command.ThemedIconStyle,
			};

		private static async void MoveTabToNewWindow_Click(object sender, RoutedEventArgs e)
		{
			var control = Ioc.Default.GetRequiredService<IMultitaskingContext>().Control;
			if (_contextMenuTarget is null || control is null)
				return;

			await MultitaskingTabsHelpers.MoveTabToNewWindow(_contextMenuTarget, control);
		}
	}
}
