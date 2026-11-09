using System.Globalization;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    public enum FilterValueKind
    {
        Text,
        Number,
        Date,
        Boolean,
        Tag
    }

    /// <summary>
    /// Shared metadata, labels, operator mappings, and date parsing for filter fields.
    /// Used both by the incoming-mail rules editor and the live mail-list advanced filter.
    /// </summary>
    public static class FilterFieldInfo
    {
        public static readonly IReadOnlyList<FilterField> RuleFields =
        [
            FilterField.From,
            FilterField.FromAddress,
            FilterField.To,
            FilterField.Subject,
            FilterField.BodyPreview,
            FilterField.SenderDomain,
            FilterField.SizeKb,
            FilterField.HasAttachment
        ];

        public static readonly IReadOnlyList<FilterField> ListFields = Enum.GetValues<FilterField>();

        public static FilterValueKind GetKind(FilterField field) => field switch
        {
            FilterField.SizeKb => FilterValueKind.Number,
            FilterField.ReceivedDate => FilterValueKind.Date,
            FilterField.HasAttachment or FilterField.IsUnread or FilterField.IsStarred
                or FilterField.IsImportant or FilterField.IsMuted => FilterValueKind.Boolean,
            FilterField.Tag => FilterValueKind.Tag,
            _ => FilterValueKind.Text
        };

        public static string FieldLabel(FilterField field) => field switch
        {
            FilterField.From => "Sender name or address",
            FilterField.FromAddress => "Exact sender address",
            FilterField.To => "Recipients",
            FilterField.Subject => "Subject",
            FilterField.BodyPreview => "Preview text",
            FilterField.SenderDomain => "Sender domain",
            FilterField.SizeKb => "Size in kilobytes",
            FilterField.HasAttachment => "Attachment",
            FilterField.ReceivedDate => "Received date",
            FilterField.IsUnread => "Read state",
            FilterField.IsStarred => "Starred",
            FilterField.IsImportant => "Important",
            FilterField.IsMuted => "Muted",
            FilterField.Tag => "Tag",
            _ => field.ToString()
        };

        public static string OperatorLabel(FilterOperator op, FilterField field)
        {
            if (field == FilterField.ReceivedDate)
            {
                return op switch
                {
                    FilterOperator.GreaterThan => "After",
                    FilterOperator.LessThan => "Before",
                    FilterOperator.Equals => "On (day)",
                    _ => op.ToString()
                };
            }

            return op switch
            {
                FilterOperator.Contains => "Contains",
                FilterOperator.NotContains => "Does not contain",
                FilterOperator.Equals => "Equals",
                FilterOperator.StartsWith => "Starts with",
                FilterOperator.EndsWith => "Ends with",
                FilterOperator.MatchesRegex => "Matches regex",
                FilterOperator.GreaterThan => "Greater than",
                FilterOperator.LessThan => "Less than",
                _ => op.ToString()
            };
        }

        public static IEnumerable<FilterOperator> OperatorsFor(FilterField field) => GetKind(field) switch
        {
            FilterValueKind.Boolean => [FilterOperator.Equals],
            FilterValueKind.Date => [FilterOperator.GreaterThan, FilterOperator.LessThan, FilterOperator.Equals],
            FilterValueKind.Number => [FilterOperator.GreaterThan, FilterOperator.LessThan, FilterOperator.Equals],
            FilterValueKind.Tag => [FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.Equals],
            _ => [FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.Equals, FilterOperator.StartsWith, FilterOperator.EndsWith, FilterOperator.MatchesRegex]
        };

        public static void NormalizeCondition(FilterCondition condition)
        {
            var allowed = OperatorsFor(condition.Field).ToArray();
            if (!allowed.Contains(condition.Operator))
            {
                condition.Operator = allowed[0];
            }

            if (GetKind(condition.Field) == FilterValueKind.Boolean)
            {
                if (!bool.TryParse(condition.Value, out _))
                {
                    condition.Value = "true";
                }
            }
            else if (condition.Field == FilterField.ReceivedDate && string.IsNullOrWhiteSpace(condition.Value))
            {
                condition.Value = DateTime.Now.ToString("yyyy-MM-dd");
            }
        }

        public static bool TryReadDate(string value, out DateTime startUtc)
        {
            string clean = value.Trim();
            if (DateTime.TryParse(clean, UiCulture.Display, DateTimeStyles.None, out var parsed))
            {
                startUtc = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            if (TryReadSpan(clean, out var span))
            {
                startUtc = DateTime.UtcNow - span;
                return true;
            }

            startUtc = default;
            return false;
        }

        static bool TryReadSpan(string value, out TimeSpan span)
        {
            span = default;
            if (value.Length < 2 || !int.TryParse(value[..^1], out int amount) || amount <= 0) return false;

            const int DaysPerWeek = 7, DaysPerMonth = 30, DaysPerYear = 365;
            int days = char.ToLowerInvariant(value[^1]) switch
            {
                'd' => amount,
                'w' => amount * DaysPerWeek,
                'm' => amount * DaysPerMonth,
                'y' => amount * DaysPerYear,
                _ => 0
            };
            if (days == 0) return false;
            span = TimeSpan.FromDays(days);
            return true;
        }

        public static string Describe(FilterCondition condition)
        {
            string fieldText = FieldLabel(condition.Field);
            var kind = GetKind(condition.Field);

            if (kind == FilterValueKind.Boolean)
            {
                bool isTrue = !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);
                return condition.Field switch
                {
                    FilterField.IsUnread => isTrue ? "Unread" : "Read",
                    FilterField.IsStarred => isTrue ? "Starred" : "Not starred",
                    FilterField.IsImportant => isTrue ? "Important" : "Not important",
                    FilterField.IsMuted => isTrue ? "Muted" : "Not muted",
                    FilterField.HasAttachment => isTrue ? "Has attachment" : "No attachment",
                    _ => $"{fieldText} = {isTrue}"
                };
            }

            string opText = OperatorLabel(condition.Operator, condition.Field);
            return $"{fieldText} {opText.ToLowerInvariant()} \"{condition.Value}\"";
        }
    }
}
