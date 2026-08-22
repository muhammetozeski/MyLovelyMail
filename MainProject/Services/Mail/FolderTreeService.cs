using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One row of the folder list: the folder plus how deep it sits under its parents.</summary>
    public readonly record struct FolderNode(MailFolderData Folder, int Depth);

    /// <summary>
    /// Turns the flat folder list into the tree the server actually sent. MailFolderData keeps the
    /// full server path but DisplayName is only the leaf, so a nested mailbox rendered as a wall of
    /// same-level rows and two different folders could show the identical label — in the sidebar
    /// and, worse, in the move menu, where picking the wrong one files mail somewhere the user did
    /// not mean.
    /// </summary>
    public static class FolderTreeService
    {
        /// <summary>Roles that lead the list, in this order; everything else follows alphabetically.</summary>
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

        /// <summary>
        /// The folders in display order, each with its depth. Local folders stay one flat group:
        /// their "Local/" prefix is a storage marker, not a mailbox the user has, so nesting them
        /// under a phantom parent would invent a folder that does not exist.
        /// </summary>
        public static List<FolderNode> Build(List<MailFolderData> folders)
        {
            List<FolderNode> nodes = [];
            var byPath = folders.ToDictionary(f => f.FullName, StringComparer.OrdinalIgnoreCase);

            foreach (var root in folders.Where(f => ParentPathOf(f, byPath) == null)
                                        .OrderBy(f => RoleRank(f.Role))
                                        .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                AppendWithChildren(root, folders, byPath, depth: 0, nodes);
            }
            return nodes;
        }

        static void AppendWithChildren(MailFolderData folder, List<MailFolderData> all,
            Dictionary<string, MailFolderData> byPath, int depth, List<FolderNode> nodes)
        {
            nodes.Add(new FolderNode(folder, depth));
            foreach (var child in all.Where(f => ParentPathOf(f, byPath) == folder.FullName)
                                     .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                AppendWithChildren(child, all, byPath, depth + 1, nodes);
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
