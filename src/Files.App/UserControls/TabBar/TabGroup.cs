// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;

namespace Files.App.UserControls.TabBar
{
	/// <summary>
	/// Represents a named, colored group of adjacent tabs, shown by <see cref="VerticalTabBar"/>.
	/// The members of a group are always adjacent in <see cref="MainPageViewModel.AppInstances"/>.
	/// </summary>
	public sealed partial class TabGroup : ObservableObject
	{
		public static IReadOnlyList<(Windows.UI.Color Color, string Name)> Colors { get; } =
		[
			(ColorHelper.FromArgb(255, 0x7A, 0x75, 0x74), Strings.Gray.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0x00, 0x78, 0xD7), Strings.Blue.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0xD1, 0x34, 0x38), Strings.BrickRed.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0xFF, 0xB9, 0x00), Strings.YellowGold.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0x10, 0x7C, 0x10), Strings.Green.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0xEA, 0x00, 0x5E), Strings.RoseBright.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0x87, 0x64, 0xB8), Strings.IrisPastel.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0x00, 0xB7, 0xC3), Strings.Seafoam.GetLocalizedResource()),
			(ColorHelper.FromArgb(255, 0xF7, 0x63, 0x0C), Strings.OrangeBright.GetLocalizedResource()),
		];

		/// <summary>
		/// Raised after tabs join or leave a group, or a group is collapsed or expanded.
		/// </summary>
		public static event EventHandler? GroupsChanged;

		/// <summary>
		/// Raised with a group that was just created, so its name can be edited.
		/// </summary>
		public static event EventHandler<TabGroup>? GroupCreated;

		private static ObservableCollection<TabBarItem> Tabs
			=> MainPageViewModel.AppInstances;

		public string Id { get; private init; } = Guid.NewGuid().ToString("N");

		private string _Name = string.Empty;
		public string Name
		{
			get => _Name;
			set
			{
				if (SetProperty(ref _Name, value))
					OnPropertyChanged(nameof(DisplayName));
			}
		}

		/// <summary>
		/// Gets the name, or the color name for a group without one.
		/// </summary>
		public string DisplayName
			=> string.IsNullOrWhiteSpace(Name) ? Colors[ColorIndex].Name : Name;

		private int _ColorIndex;
		public int ColorIndex
		{
			get => _ColorIndex;
			set
			{
				if (SetProperty(ref _ColorIndex, Math.Clamp(value, 0, Colors.Count - 1)))
				{
					OnPropertyChanged(nameof(Brush));
					OnPropertyChanged(nameof(DisplayName));
					SaveSession();
				}
			}
		}

		public SolidColorBrush Brush
			=> new(Colors[ColorIndex].Color);

		private bool _IsCollapsed;
		public bool IsCollapsed
		{
			get => _IsCollapsed;
			set
			{
				if (SetProperty(ref _IsCollapsed, value))
					NotifyGroupsChanged();
			}
		}

		public IEnumerable<TabBarItem> Members
			=> Tabs.Where(tab => tab.Group == this);

		public static IEnumerable<TabGroup> AllGroups
			=> Tabs.Select(tab => tab.Group).OfType<TabGroup>().Distinct();

		public static TabGroup AddToNewGroup(TabBarItem tab)
		{
			var usedColors = AllGroups.Select(group => group.ColorIndex).ToHashSet();
			var group = new TabGroup
			{
				_ColorIndex = Enumerable.Range(0, Colors.Count).FirstOrDefault(index => !usedColors.Contains(index), AllGroups.Count() % Colors.Count),
			};

			AddToGroup(tab, group);
			GroupCreated?.Invoke(null, group);

			return group;
		}

		public static void AddToGroup(TabBarItem tab, TabGroup group)
		{
			if (tab.Group == group)
				return;

			// Joining an existing group places the tab after its last member; a new group starts where the tab leaves its old one.
			var anchor = group.Members.LastOrDefault() ?? tab.Group?.Members.LastOrDefault();
			if (anchor is not null)
				MoveAfter(tab, anchor);

			tab.Group = group;
			group.IsCollapsed = false;
			NotifyGroupsChanged();
		}

		public static void RemoveFromGroup(TabBarItem tab)
		{
			if (tab.Group is not TabGroup group)
				return;

			MoveAfter(tab, group.Members.Last());
			tab.Group = null;
			NotifyGroupsChanged();
		}

		public void Ungroup()
		{
			foreach (var tab in Members.ToList())
				tab.Group = null;

			NotifyGroupsChanged();
		}

		public void Close(ITabBar tabBar)
		{
			foreach (var tab in Members.ToList())
				tabBar.CloseTab(tab);
		}

		/// <summary>
		/// Fixes the group of a tab that was dragged or inserted: between two tabs of one group it joins that group,
		/// and away from the rest of its own group it leaves it. This keeps every group adjacent.
		/// </summary>
		public static void UpdateGroupAfterMove(TabBarItem tab)
		{
			var index = Tabs.IndexOf(tab);
			if (index < 0)
				return;

			var previous = index > 0 ? Tabs[index - 1].Group : null;
			var next = index < Tabs.Count - 1 ? Tabs[index + 1].Group : null;

			TabGroup? newGroup;
			if (previous is not null && previous == next)
				newGroup = previous;
			else if (tab.Group is null || previous == tab.Group || next == tab.Group || !tab.Group.Members.Any(member => member != tab))
				newGroup = tab.Group;
			else
				newGroup = null;

			if (newGroup == tab.Group)
				return;

			tab.Group = newGroup;
			NotifyGroupsChanged();
		}

		public string Serialize()
			=> new JsonObject
			{
				[nameof(Id)] = Id,
				[nameof(Name)] = Name,
				[nameof(ColorIndex)] = ColorIndex,
				[nameof(IsCollapsed)] = IsCollapsed,
			}.ToJsonString();

		/// <summary>
		/// Restores the groups saved by <see cref="AppLifecycleHelper.SaveSessionTabs"/>, one entry per tab and empty for ungrouped tabs.
		/// </summary>
		public static void RestoreSession(List<string>? sessionGroups)
		{
			if (sessionGroups is null || sessionGroups.Count != Tabs.Count)
				return;

			var groups = new Dictionary<string, TabGroup>();
			for (var i = 0; i < sessionGroups.Count; i++)
			{
				if (string.IsNullOrEmpty(sessionGroups[i]))
					continue;

				try
				{
					var data = JsonNode.Parse(sessionGroups[i])?.AsObject();
					var id = data?[nameof(Id)]?.GetValue<string>();
					if (data is null || string.IsNullOrEmpty(id))
						continue;

					if (!groups.TryGetValue(id, out var group))
					{
						group = new TabGroup
						{
							Id = id,
							_Name = data[nameof(Name)]?.GetValue<string>() ?? string.Empty,
							_ColorIndex = Math.Clamp(data[nameof(ColorIndex)]?.GetValue<int>() ?? 0, 0, Colors.Count - 1),
							_IsCollapsed = data[nameof(IsCollapsed)]?.GetValue<bool>() ?? false,
						};
						groups.Add(id, group);
					}

					Tabs[i].Group = group;
				}
				catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
				{
					// A damaged entry only loses its group.
				}
			}

			// The selected tab was restored before its group, so a collapsed group would hide it
			var selectedIndex = App.AppModel.TabStripSelectedIndex;
			if (selectedIndex >= 0 && selectedIndex < Tabs.Count && Tabs[selectedIndex].Group is TabGroup selectedGroup)
				selectedGroup._IsCollapsed = false;

			NotifyGroupsChanged();
		}

		private static void MoveAfter(TabBarItem tab, TabBarItem anchor)
		{
			if (tab == anchor)
				return;

			var selectedIndex = App.AppModel.TabStripSelectedIndex;
			var selectedTab = selectedIndex >= 0 && selectedIndex < Tabs.Count ? Tabs[selectedIndex] : null;

			var oldIndex = Tabs.IndexOf(tab);
			var newIndex = Tabs.IndexOf(anchor);
			if (oldIndex > newIndex)
				newIndex++;

			if (oldIndex == newIndex)
				return;

			Tabs.Move(oldIndex, newIndex);

			if (selectedTab is not null)
				App.AppModel.TabStripSelectedIndex = Tabs.IndexOf(selectedTab);
		}

		private static void NotifyGroupsChanged()
		{
			GroupsChanged?.Invoke(null, EventArgs.Empty);
			SaveSession();
		}

		/// <summary>
		/// Saves the session; the name is saved this way once editing ends rather than on every keystroke.
		/// </summary>
		public static void SaveSession()
			=> AppLifecycleHelper.SaveSessionTabs();
	}
}
