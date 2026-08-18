using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>Holds every <see cref="FilterRule"/>, persisted as <c>UserData/filters.json</c> (ordered — rules run top to bottom).</summary>
    public static class FilterRuleStore
    {
        public const string FiltersFileName = "filters.json";

        static string FiltersPath => Path.Combine(AppPaths.UserData, FiltersFileName);

        static List<FilterRule> rules = [];

        public static event Action? OnRulesChanged;

        public static IReadOnlyList<FilterRule> Rules => rules;

        public static void Load()
        {
            rules = [];
            if (!File.Exists(FiltersPath)) return;
            try
            {
                rules = JsonSerializer.Deserialize<List<FilterRule>>(File.ReadAllText(FiltersPath), JsonDefaults.Indented) ?? [];
            }
            catch (Exception ex)
            {
                Log($"Corrupt filters file: {ex.Message}", LogLevel.Error);
            }
        }

        static void Persist()
        {
            AtomicFile.WriteAllText(FiltersPath, JsonSerializer.Serialize(rules, JsonDefaults.Indented));
            OnRulesChanged?.Invoke();
        }

        public static void Save(FilterRule rule)
        {
            int index = rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0) rules[index] = rule;
            else rules.Add(rule);
            Persist();
        }

        public static void Remove(string ruleId)
        {
            if (rules.RemoveAll(r => r.Id == ruleId) > 0)
                Persist();
        }

        /// <summary>Moves a rule one step up or down — order matters because rules run top to bottom.</summary>
        public static void MoveRule(string ruleId, bool up)
        {
            int index = rules.FindIndex(r => r.Id == ruleId);
            int target = up ? index - 1 : index + 1;
            if (index < 0 || target < 0 || target >= rules.Count) return;
            (rules[index], rules[target]) = (rules[target], rules[index]);
            Persist();
        }
    }
}
