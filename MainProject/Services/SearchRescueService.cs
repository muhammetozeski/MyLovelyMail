using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// A query that would find something, and how much. <paramref name="Query"/> is what to put in
    /// the box; <paramref name="SearchAllFolders"/> says whether the all-folders switch has to go
    /// with it.
    /// </summary>
    public readonly record struct SearchRescue(string Label, string Query, bool SearchAllFolders, int Hits);

    /// <summary>
    /// Turns "nothing matched" into a query that does. The parser drops an unrecognised operator
    /// into the default arm as literal text, so <c>is:unred</c> becomes a search for the string
    /// "is:unred" and finds nothing — the query looks right and the app says only that it failed.
    /// <para>
    /// Everything needed to explain the miss is already in RAM: the matcher takes a raw string,
    /// the summaries are loaded, and the operator table is one switch. Nothing here changes how
    /// searching works.
    /// </para>
    /// </summary>
    public static class SearchRescueService
    {
        /// <summary>Chips offered at once — past a handful this stops being help and becomes a wall.</summary>
        const int MaxSuggestions = 4;

        /// <summary>
        /// Ways to get results back, best first. Empty when the query cannot be rescued by
        /// dropping one token or widening the scope.
        /// </summary>
        public static List<SearchRescue> Suggest(string accountId, string folderFullName, string query, bool searchAllFolders)
        {
            List<SearchRescue> suggestions = [];
            string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return suggestions;

            // A refused operator is the reason far more often than the mail being absent, so it
            // leads even when dropping some other token would find more.
            foreach (string dead in SearchService.DeadOperators(query))
            {
                string without = string.Join(' ', tokens.Where(t => t != dead));
                int hits = Count(accountId, folderFullName, without, searchAllFolders);
                suggestions.Add(new SearchRescue(
                    $"“{dead}” is not an operator — drop it ({hits})",
                    without, searchAllFolders, hits));
            }

            // "Right query, wrong folder" is the most common ordinary miss.
            if (!searchAllFolders)
            {
                int everywhere = Count(accountId, folderFullName, query, searchAllFolders: true);
                if (everywhere > 0)
                    suggestions.Add(new SearchRescue($"Search all folders ({everywhere})", query, true, everywhere));
            }

            if (tokens.Length > 1)
            {
                foreach (string token in tokens)
                {
                    if (suggestions.Any(s => s.Query == string.Join(' ', tokens.Where(t => t != token)))) continue;
                    string without = string.Join(' ', tokens.Where(t => t != token));
                    int hits = Count(accountId, folderFullName, without, searchAllFolders);
                    if (hits > 0)
                        suggestions.Add(new SearchRescue($"Without “{token}” ({hits})", without, searchAllFolders, hits));
                }
            }

            return [.. suggestions.Where(static s => s.Hits > 0).OrderByDescending(static s => s.Hits).Take(MaxSuggestions)];
        }

        /// <summary>
        /// Runs the real matcher over the same source the page reads, so a count can never promise
        /// more than a click delivers. An empty query counts too: clearing the box is what the user
        /// gets when the only token they typed was the broken one, and it shows the whole folder.
        /// </summary>
        static int Count(string accountId, string folderFullName, string query, bool searchAllFolders) =>
            searchAllFolders && query.Trim().Length > 0
                ? SearchService.Search(accountId, query).Count
                : MessageStore.GetSummaries(accountId, folderFullName).Count(SearchService.BuildMatcher(query, accountId));
    }
}
