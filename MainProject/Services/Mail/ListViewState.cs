using MyLovelyMail.MainProject.DataModels.Mail;

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

    /// <summary>
    /// Shared runtime state for mail/sender sorting, active advanced filter stack,
    /// and the floating filter window position.
    /// </summary>
    public static class ListViewState
    {
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

        public static int ActiveFilterCount =>
            ListFilterService.Resolve(activeFilters).Count(f => f.Status != FilterConditionStatus.Overridden);

        public static List<ResolvedFilterItem> GetResolvedFilters() =>
            ListFilterService.Resolve(activeFilters);

        public static void SetMailSort(MailSortField field, SortDirection direction)
        {
            MailSort = field;
            MailSortDirection = direction;
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
            OnChanged?.Invoke();
        }

        public static void SetSenderSort(SenderSortField field, SortDirection direction)
        {
            SenderSort = field;
            SenderSortDirection = direction;
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
            OnChanged?.Invoke();
        }

        public static void AddFilter(FilterCondition condition)
        {
            FilterFieldInfo.NormalizeCondition(condition);
            activeFilters.Add(new FilterConditionEntry { Condition = condition });
            OnChanged?.Invoke();
        }

        public static void RemoveFilter(string id)
        {
            if (activeFilters.RemoveAll(f => f.Id == id) > 0)
            {
                OnChanged?.Invoke();
            }
        }

        public static void ClearFilters()
        {
            if (activeFilters.Count > 0)
            {
                activeFilters.Clear();
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
