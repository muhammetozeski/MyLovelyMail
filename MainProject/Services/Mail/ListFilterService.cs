using System.Globalization;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    public enum FilterConditionStatus
    {
        Active,
        Overridden,
        Partial
    }

    public sealed class FilterConditionEntry
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public FilterCondition Condition { get; init; } = new();
    }

    public sealed class ResolvedFilterItem
    {
        public required string Id { get; init; }
        public required FilterCondition Condition { get; init; }
        public required int Index { get; init; }
        public required string Description { get; init; }
        public FilterConditionStatus Status { get; set; } = FilterConditionStatus.Active;
        public string? OverriddenByDescription { get; set; }
    }

    /// <summary>
    /// Resolves ordered filter selections where later conditions override earlier ones on conflicts,
    /// while non-conflicting conditions accumulate seamlessly into ranges or conjunctions.
    /// </summary>
    public static class ListFilterService
    {
        public static List<ResolvedFilterItem> Resolve(IReadOnlyList<FilterConditionEntry> entries)
        {
            var items = new List<ResolvedFilterItem>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                items.Add(new ResolvedFilterItem
                {
                    Id = entry.Id,
                    Condition = entry.Condition,
                    Index = i + 1,
                    Description = FilterFieldInfo.Describe(entry.Condition),
                    Status = FilterConditionStatus.Active
                });
            }

            // Check backwards: later conditions override earlier conditions if they conflict
            for (int j = items.Count - 1; j >= 0; j--)
            {
                var later = items[j];
                for (int i = 0; i < j; i++)
                {
                    var prior = items[i];
                    if (prior.Status == FilterConditionStatus.Overridden) continue;

                    if (Conflicts(prior.Condition, later.Condition))
                    {
                        prior.Status = FilterConditionStatus.Overridden;
                        prior.OverriddenByDescription = $"Overridden by #{later.Index}";
                    }
                }
            }

            return items;
        }

        public static bool MatchesAllActive(MailMessageSummary summary, IReadOnlyList<ResolvedFilterItem> resolved)
        {
            foreach (var item in resolved)
            {
                if (item.Status == FilterConditionStatus.Overridden) continue;
                if (!RuleEngine.MatchesCondition(item.Condition, summary))
                    return false;
            }
            return true;
        }

        static bool Conflicts(FilterCondition prior, FilterCondition later)
        {
            if (prior.Field != later.Field) return false;

            var kind = FilterFieldInfo.GetKind(later.Field);

            if (kind == FilterValueKind.Boolean)
            {
                // Any later boolean setting for the same field overrides the earlier one
                return true;
            }

            if (kind == FilterValueKind.Date)
            {
                bool priorParsed = FilterFieldInfo.TryReadDate(prior.Value, out var priorDate);
                bool laterParsed = FilterFieldInfo.TryReadDate(later.Value, out var laterDate);
                if (!priorParsed || !laterParsed) return true;

                // Same direction date bounds override (e.g. after D2 replaces after D1)
                if (prior.Operator == later.Operator) return true;
                if (later.Operator == FilterOperator.Equals || prior.Operator == FilterOperator.Equals) return true;

                // GreaterThan vs LessThan: conflict if contradictory range (after D1 but before D2 where D1 >= D2)
                if (prior.Operator == FilterOperator.GreaterThan && later.Operator == FilterOperator.LessThan)
                {
                    return priorDate >= laterDate; // contradiction: impossible range, later overrides
                }
                if (prior.Operator == FilterOperator.LessThan && later.Operator == FilterOperator.GreaterThan)
                {
                    return laterDate >= priorDate; // contradiction: impossible range, later overrides
                }

                return false;
            }

            if (kind == FilterValueKind.Number)
            {
                bool priorParsed = double.TryParse(prior.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double priorVal);
                bool laterParsed = double.TryParse(later.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double laterVal);
                if (!priorParsed || !laterParsed) return true;

                if (prior.Operator == later.Operator) return true;
                if (later.Operator == FilterOperator.Equals || prior.Operator == FilterOperator.Equals) return true;

                if (prior.Operator == FilterOperator.GreaterThan && later.Operator == FilterOperator.LessThan)
                {
                    return priorVal >= laterVal;
                }
                if (prior.Operator == FilterOperator.LessThan && later.Operator == FilterOperator.GreaterThan)
                {
                    return laterVal >= priorVal;
                }

                return false;
            }

            // Text / Tag fields
            if (later.Operator == FilterOperator.Equals && prior.Operator == FilterOperator.Equals)
            {
                return true;
            }

            if (later.Operator == FilterOperator.NotContains && prior.Operator == FilterOperator.Contains
                && string.Equals(later.Value.Trim(), prior.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (later.Operator == FilterOperator.Contains && prior.Operator == FilterOperator.NotContains
                && string.Equals(later.Value.Trim(), prior.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
