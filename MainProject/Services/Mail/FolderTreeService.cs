using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One row of the folder list: the folder, how deep it sits under its parents, and whether it has child folders.</summary>
    public readonly record struct FolderNode(MailFolderData Folder, int Depth, bool HasChildren = false)
    {
        public bool IsExpanded => Folder.IsExpanded;
    }

    /// <summary>
    /// Turns the flat folder list into the tree the server actually sent. MailFolderData keeps the
    /// full server path but DisplayName is only the leaf, so a nested mailbox rendered as a wall of
    /// same-level rows and two different folders could show the identical label — in the sidebar
    /// and, worse, in the move menu, where picking the wrong one files mail somewhere the user did
    /// not mean.
    /// </summary>
    public static class FolderTreeService
    {
        /// <summary>Roles that lead the list, in this order; everything else follows alphabetically unless reordered by the user.</summary>
        static readonly FolderRole[] RoleOrder =
        [
            FolderRole.Inbox, FolderRole.Flagged, FolderRole.Drafts, FolderRole.Sent,
            FolderRole.Outbox, FolderRole.Archive, FolderRole.Junk, FolderRole.Trash, FolderRole.AllMail
        ];

        static int RoleRank(FolderRole role)
        {
            int index = Array.IndexOf(RoleOrder, role);
            return index < 0 ? RoleOrder.Length : index;
        }

        static IEnumerable<MailFolderData> OrderSiblings(IEnumerable<MailFolderData> siblings, bool isRoot) =>
            siblings
                .OrderBy(static f => f.SortOrder > 0 ? 0 : 1)
                .ThenBy(static f => f.SortOrder)
                .ThenBy(f => isRoot ? RoleRank(f.Role) : 0)
                .ThenBy(static f => f.DisplayName, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The folders in display order, each with its depth and child indicator. Local folders stay one flat group:
        /// their "Local/" prefix is a storage marker, not a mailbox the user has, so nesting them
        /// under a phantom parent would invent a folder that does not exist.
        /// </summary>
        public static List<FolderNode> Build(List<MailFolderData> folders, bool respectCollapsed = true)
        {
            List<FolderNode> nodes = [];
            var byPath = folders.ToDictionary(f => f.FullName, StringComparer.OrdinalIgnoreCase);
            var children = folders.ToLookup(folder => ParentPathOf(folder, byPath), StringComparer.Ordinal);

            foreach (var root in OrderSiblings(children[null], isRoot: true))
            {
                AppendWithChildren(root, children, depth: 0, respectCollapsed, nodes);
            }
            return nodes;
        }

        static void AppendWithChildren(MailFolderData folder, ILookup<string?, MailFolderData> children,
            int depth, bool respectCollapsed, List<FolderNode> nodes)
        {
            var childList = OrderSiblings(children[folder.FullName], isRoot: false).ToList();
            nodes.Add(new FolderNode(folder, depth, HasChildren: childList.Count > 0));
            if (respectCollapsed && !folder.IsExpanded) return;

            foreach (var child in childList)
            {
                AppendWithChildren(child, children, depth + 1, respectCollapsed, nodes);
            }
        }

        /// <summary>
        /// The full path of this folder's parent, or null when it sits at the top. A parent only
        /// counts when the account actually HAS it: servers expose folders whose intermediate
        /// levels are not selectable, and hanging a row off a path that is not in the list would
        /// make it disappear.
        /// </summary>
        internal static string? ParentPathOf(MailFolderData folder, Dictionary<string, MailFolderData> byPath)
        {
            if (folder.IsLocal || folder.FullName.StartsWith(MessageStore.LocalFolderPrefix)) return null;
            // 0 means the folder was written before the delimiter was recorded; the next folder-list
            // pass fills it in, so treating it as top-level for now heals itself.
            if (folder.Delimiter == '\0') return null;

            int cut = folder.FullName.LastIndexOf(folder.Delimiter);
            if (cut <= 0) return null;

            string parent = folder.FullName[..cut];
            return byPath.ContainsKey(parent) ? parent : null;
        }
    }
}
