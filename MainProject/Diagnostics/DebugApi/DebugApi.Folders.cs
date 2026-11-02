using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Diagnostics;

public static partial class DebugApi
{
    /// <summary>Handles the folders endpoints using the application's existing services.</summary>
    static async Task<object?> HandleFoldersAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {
            case ("GET", "/account-folders"):
                return AccountFolderStore.GetFlattenedTree().Select(static x => new
                {
                    x.Folder.Id,
                    x.Folder.Name,
                    x.Folder.ParentFolderId,
                    x.Folder.SortOrder,
                    x.Folder.IsExpanded,
                    x.Depth
                });

            case ("POST", "/account-folders"):
            {
                string name = RequireQueryValue(query, "name");
                string? parentId = query["parentId"];
                if (query["id"] is { Length: > 0 } existingId)
                {
                    AccountFolderStore.RenameFolder(existingId, name);
                    if (parentId != null)
                        AccountFolderStore.MoveFolder(existingId, parentId);
                    return new { id = existingId, name, parentId };
                }
                var created = AccountFolderStore.CreateFolder(name, parentId);
                return new { created.Id, created.Name, created.ParentFolderId };
            }

            case ("DELETE", "/account-folders"):
            {
                string id = RequireQueryValue(query, "id");
                AccountFolderStore.DeleteFolder(id);
                return new { deleted = id };
            }

            case ("GET", "/export/folder"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                return MailboxExportService.ExportFolder(RequireAccount(accountId), ReadFolder(query));
            }

            case ("GET", "/folders/server"):
                return new { folders = await ImapSyncService.ListServerFoldersAsync(RequireAccount(RequireQueryValue(query, "accountId"))) };

            case ("GET", "/folders/roles"):
            {
                string rolesAccountId = RequireQueryValue(query, "accountId");
                var folders = MessageStore.GetFolders(rolesAccountId);
                return new
                {
                    folders = folders.Select(f => new
                    {
                        f.FullName,
                        f.DisplayName,
                        Role = f.Role.ToString(),
                        Direction = FolderSyncPolicy.For(rolesAccountId, f.FullName).ToString(),
                        f.Selectable,
                        f.IsLocal,
                        f.TotalCount
                    }),
                    duplicateRoles = folders
                        .Where(static f => f.Role != FolderRole.None)
                        .GroupBy(static f => f.Role)
                        .Where(static g => g.Count() > 1)
                        .Select(static g => new { Role = g.Key.ToString(), Folders = g.Select(static f => f.FullName) })
                };
            }

            case ("POST", "/folders/roles/probe"):
            {
                var candidates = await JsonSerializer.DeserializeAsync<List<FolderRoleCandidate>>(
                    request.InputStream, JsonDefaults.SingleLine) ?? [];
                return new { decisions = FolderRoleResolver.Resolve(candidates).Select(static d => new { d.FullName, Role = d.Role.ToString(), Evidence = d.Evidence.ToString(), d.Why }) };
            }

            case ("GET", "/folder-why"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                string folderFullName = ReadFolder(query);
                var folder = query["probe"] == "true"
                    ? new MailFolderData
                    {
                        AccountId = accountId,
                        FullName = folderFullName,
                        DisplayName = folderFullName,
                        IsLocal = query["isLocal"] == "true",
                        TotalCount = int.TryParse(query["totalCount"], out int probeTotal) ? probeTotal : 0,
                        LastSyncedUtc = DateTime.TryParse(query["lastSynced"], out var probeSynced) ? probeSynced.ToUniversalTime() : null
                    }
                    : MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                      ?? throw new InvalidOperationException("Unknown folder.");
                var why = FolderEmptyReason.Explain(folder);
                return new
                {
                    folder = folder.FullName,
                    why.Headline,
                    why.Detail,
                    action = why.Action.ToString(),
                    why.ActionLabel,
                    folder.TotalCount,
                    folder.LastSyncedUtc,
                    cached = MessageStore.CountSummaries(accountId, folderFullName),
                    lastFolderFailure = ImapSyncService.LastFolderFailure(accountId, folderFullName)
                };
            }

            case ("POST", "/folder-why/act"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                string folderFullName = ReadFolder(query);
                var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                    ?? throw new InvalidOperationException("Unknown folder.");
                var action = Enum.Parse<EmptyFolderAction>(query["action"] ?? FolderEmptyReason.Explain(folder).Action.ToString(), ignoreCase: true);
                FolderEmptyReason.RunAction(folder, action);
                return new { ran = action.ToString(), syncing = ImapSyncService.IsFolderSyncing(accountId, folderFullName) };
            }

            case ("GET", "/folder-tree"):
            {
                List<MailFolderData> folders = query["probe"] is { Length: > 0 } probe
                    ? [.. probe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(name => new MailFolderData { FullName = name, DisplayName = name.Split('/').Last(), Delimiter = '/', IsLocal = name.StartsWith(MessageStore.LocalFolderPrefix) })]
                    : MessageStore.GetFolders(RequireQueryValue(query, "accountId"));
                return FolderTreeService.Build(folders).Select(n => new { n.Folder.FullName, n.Folder.DisplayName, n.Depth });
            }

            case ("GET", "/folders"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                return MessageStore.GetFolders(accountId)
                    .Select(f => new
                    {
                        f.FullName, f.DisplayName, f.Role, f.TotalCount, f.UnreadCount, f.IsLocal,
                        f.LastSeenUid, f.OldestFetchedUid, f.LastSyncedUtc, f.Delimiter,
                        CachedCount = MessageStore.CountSummaries(accountId, f.FullName)
                    });
            }

            case ("POST", "/folder/read-all"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                string folder = ReadFolder(query);
                return new { marked = MessageActions.SetFolderRead(RequireAccount(accountId), folder) };
            }

            case ("POST", "/backfill"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                string folder = ReadFolder(query);
                int landed = await ImapSyncService.BackfillFolderAsync(RequireAccount(accountId), folder, ReadTake(query, 300));
                return new { landed };
            }

            case ("POST", "/resync"):
            {
                string accountId = RequireQueryValue(query, "accountId");
                string folder = ReadFolder(query);
                ImapSyncService.KickFolderSync(RequireAccount(accountId), folder, resetCache: true);
                return new { ok = true };
            }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}
