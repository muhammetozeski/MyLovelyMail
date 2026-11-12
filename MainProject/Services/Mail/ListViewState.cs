using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    public enum MailSortField
    {
        Date,
        Sender,
        Subject,
        Size,
        Unread
    }

    public enum SenderSortField
    {
        LatestDate,
        MessageCount,
        UnreadCount,
        Name
    }

    public enum SortDirection
    {
        Ascending,
        Descending
    }

    public sealed class PersistedListViewSnapshot
    {
        public MailSortField MailSort { get; set; } = MailSortField.Date;
        public SortDirection MailSortDirection { get; set; } = SortDirection.Descending;
        public SenderSortField SenderSort { get; set; } = SenderSortField.LatestDate;
        public SortDirection SenderSortDirection { get; set; } = SortDirection.Descending;
        public List<FilterConditionEntry> ActiveFilters { get; set; } = [];
        public double WindowRightPx { get; set; } = 16.0;
        public double WindowTopPx { get; set; } = 80.0;
    }

    /// <summary>
    /// Shared state for mail/sender sorting, active advanced filter stack,
    /// and the floating filter window position, persisted to <c>UserData/list-view.json</c>.
    /// </summary>
    public static class ListViewState
    {
        public const string FileName = "list-view.json";

        static bool persistenceEnabled;

        public static MailSortField MailSort { get; private set; } = MailSortField.Date;
        public static SortDirection MailSortDirection { get; private set; } = SortDirection.Descending;

        public static SenderSortField SenderSort { get; private set; } = SenderSortField.LatestDate;
        public static SortDirection SenderSortDirection { get; private set; } = SortDirection.Descending;

        static readonly List<FilterConditionEntry> activeFilters = [];
        public static IReadOnlyList<FilterConditionEntry> ActiveFilters => activeFilters;

        public static bool IsFilterWindowOpen { get; private set; }
        public static double WindowRightPx { get; private set; } = 16.0;
        public static double WindowTopPx { get; private set; } = 80.0;

        public static event Action? OnChanged;

        static string FilePath => Path.Combine(AppPaths.UserData, FileName);

        public static void Load()
        {
            persistenceEnabled = true;
            activeFilters.Clear();
            MailSort = MailSortField.Date;
            MailSortDirection = SortDirection.Descending;
            SenderSort = SenderSortField.LatestDate;
            SenderSortDirection = SortDirection.Descending;
            WindowRightPx = 16.0;
            WindowTopPx = 80.0;

            string path = FilePath;
            if (!File.Exists(path)) return;

            try
            {
                var snap = JsonSerializer.Deserialize<PersistedListViewSnapshot>(File.ReadAllText(path), JsonDefaults.Indented);
                if (snap == null) return;
                MailSort = snap.MailSort;
                MailSortDirection = snap.MailSortDirection;
                SenderSort = snap.SenderSort;
                SenderSortDirection = snap.SenderSortDirection;
                WindowRightPx = Math.Max(8.0, snap.WindowRightPx);
                WindowTopPx = Math.Max(40.0, snap.WindowTopPx);
                if (snap.ActiveFilters != null)
                {
                    foreach (var entry in snap.ActiveFilters)
                    {
                        if (entry?.Condition == null) continue;
                        FilterFieldInfo.NormalizeCondition(entry.Condition);
                        activeFilters.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Corrupt list-view.json '{path}': {ex.Message}", LogLevel.Warning);
            }
        }

        static void SaveIfEnabled()
        {
            if (!persistenceEnabled) return;
            try
            {
                Directory.CreateDirectory(AppPaths.UserData);
                var snap = new PersistedListViewSnapshot
                {
                    MailSort = MailSort,
                    MailSortDirection = MailSortDirection,
                    SenderSort = SenderSort,
                    SenderSortDirection = SenderSortDirection,
                    ActiveFilters = [.. activeFilters],
                    WindowRightPx = WindowRightPx,
                    WindowTopPx = WindowTopPx
                };
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snap, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not save list-view.json: {ex.Message}", LogLevel.Warning);
            }
        }

        public static int ActiveFilterCount =>
            ListFilterService.Resolve(activeFilters).Count(f => f.Status != FilterConditionStatus.Overridden);

        public static List<ResolvedFilterItem> GetResolvedFilters() =>
            ListFilterService.Resolve(activeFilters);

        public static void SetMailSort(MailSortField field, SortDirection direction)
        {
            MailSort = field;
            MailSortDirection = direction;
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void ToggleMailSort(MailSortField field)
        {
            if (MailSort == field)
            {
                MailSortDirection = MailSortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            }
            else
            {
                MailSort = field;
                MailSortDirection = (field == MailSortField.Sender || field == MailSortField.Subject)
                    ? SortDirection.Ascending
                    : SortDirection.Descending;
            }
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void SetSenderSort(SenderSortField field, SortDirection direction)
        {
            SenderSort = field;
            SenderSortDirection = direction;
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void ToggleSenderSort(SenderSortField field)
        {
            if (SenderSort == field)
            {
                SenderSortDirection = SenderSortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            }
            else
            {
                SenderSort = field;
                SenderSortDirection = (field == SenderSortField.Name)
                    ? SortDirection.Ascending
                    : SortDirection.Descending;
            }
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void AddFilter(FilterCondition condition)
        {
            FilterFieldInfo.NormalizeCondition(condition);
            activeFilters.Add(new FilterConditionEntry { Condition = condition });
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void RemoveFilter(string id)
        {
            if (activeFilters.RemoveAll(f => f.Id == id) > 0)
            {
                SaveIfEnabled();
                OnChanged?.Invoke();
            }
        }

        public static void ClearFilters()
        {
            if (activeFilters.Count > 0)
            {
                activeFilters.Clear();
                SaveIfEnabled();
                OnChanged?.Invoke();
            }
        }

        public static void MoveFilter(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= activeFilters.Count || toIndex < 0 || toIndex >= activeFilters.Count || fromIndex == toIndex)
                return;
            var item = activeFilters[fromIndex];
            activeFilters.RemoveAt(fromIndex);
            activeFilters.Insert(toIndex, item);
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static void MoveFilterUp(int index) => MoveFilter(index, index - 1);
        public static void MoveFilterDown(int index) => MoveFilter(index, index + 1);

        public static void ToggleFilterWindow()
        {
            IsFilterWindowOpen = !IsFilterWindowOpen;
            OnChanged?.Invoke();
        }

        public static void CloseFilterWindow()
        {
            if (IsFilterWindowOpen)
            {
                IsFilterWindowOpen = false;
                OnChanged?.Invoke();
            }
        }

        public static void SetWindowPosition(double right, double top)
        {
            WindowRightPx = Math.Max(8.0, right);
            WindowTopPx = Math.Max(40.0, top);
            SaveIfEnabled();
            OnChanged?.Invoke();
        }

        public static string MailSortLabel => MailSort switch
        {
            MailSortField.Date => MailSortDirection == SortDirection.Descending ? "Date (newest)" : "Date (oldest)",
            MailSortField.Sender => MailSortDirection == SortDirection.Ascending ? "Sender (A-Z)" : "Sender (Z-A)",
            MailSortField.Subject => MailSortDirection == SortDirection.Ascending ? "Subject (A-Z)" : "Subject (Z-A)",
            MailSortField.Size => MailSortDirection == SortDirection.Descending ? "Size (largest)" : "Size (smallest)",
            MailSortField.Unread => MailSortDirection == SortDirection.Descending ? "Unread first" : "Read first",
            _ => "Date"
        };

        public static string SenderSortLabel => SenderSort switch
        {
            SenderSortField.LatestDate => SenderSortDirection == SortDirection.Descending ? "Date (newest)" : "Date (oldest)",
            SenderSortField.MessageCount => SenderSortDirection == SortDirection.Descending ? "Count (most)" : "Count (least)",
            SenderSortField.UnreadCount => SenderSortDirection == SortDirection.Descending ? "Unread (most)" : "Unread (least)",
            SenderSortField.Name => SenderSortDirection == SortDirection.Ascending ? "Name (A-Z)" : "Name (Z-A)",
            _ => "Date"
        };
    }
}
