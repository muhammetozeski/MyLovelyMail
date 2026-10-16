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
    /// <summary>Handles the compose endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleComposeAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                // The quote is a pure function of the source message plus the style, so each
                // mode is checkable without opening a compose pane.
                case ("POST", "/compose/from"):
                {
                    var next = RequireAccount(RequireQueryValue(query, "accountId"));
                    if (MailUiState.ActiveCompose is not { } draft) throw new InvalidOperationException("No draft is open.");
                    ComposeService.SwitchSendingAccount(draft, next);
                    return new { from = draft.Account?.EmailAddress, bodyPrefix = draft.Body[..Math.Min(60, draft.Body.Length)], attachments = draft.AttachmentPaths.Count(File.Exists) };
                }

                case ("GET", "/reply-preview"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var style = Enum.Parse<QuoteStyle>(query["style"] ?? nameof(QuoteStyle.Full), ignoreCase: true);
                    string body = ComposeService.QuoteBody(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid), style);
                    return new { style = style.ToString(), lines = body.Split('\n').Length, characters = body.Length, body };
                }

                // The signature in all three of its shapes at once: what is stored, what goes out
                // as html, and what goes out as text. Without an accountId these read and write
                // the global value; with one they read and write that account's override.
                case ("GET", "/signature"):
                {
                    string accountId = query["accountId"] ?? string.Empty;
                    string stored = accountId.Length > 0
                        ? AccountStore.GetSettings(accountId).Signature.Value
                        : GlobalSettings.Signature.Value;
                    return new
                    {
                        accountId,
                        stored,
                        isHtml = SignatureService.IsHtml(stored),
                        overridden = accountId.Length > 0 && AccountStore.GetSettings(accountId).Signature.IsOverridden,
                        html = SignatureService.ToHtml(stored),
                        plainText = SignatureService.ToPlainText(stored),
                        plainBlock = SignatureService.PlainBlock(stored)
                    };
                }

                case ("POST", "/signature"):
                {
                    using var reader = new StreamReader(request.InputStream);
                    string value = await reader.ReadToEndAsync();
                    string accountId = query["accountId"] ?? string.Empty;
                    if (accountId.Length > 0)
                    {
                        var settings = AccountStore.GetSettings(accountId);
                        if (query["reset"] == "true") settings.Signature.ClearOverride();
                        else settings.Signature.Value = value;
                        settings.Save();
                    }
                    else
                    {
                        GlobalSettings.Signature.Set(value);
                        SettingsManager.SaveSettings();
                    }
                    return new { saved = true, accountId, length = value.Length };
                }

                // Exactly what the send path will put in the two parts of the message, without
                // sending anything.
                case ("GET", "/compose/preview"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    string signature = AccountStore.GetSettings(account.Id).Signature.Value;
                    string body = query["body"] ?? SignatureService.PlainBlock(signature);
                    var (text, html) = SignatureService.Compose(body, signature);
                    return new { text, html, multipart = html != null };
                }

                // Builds a forward-as-attachment draft and reports what actually got staged: the
                // path, its size next to the cached MIME's, and the content type MimeKit picks for
                // it in the send path's BodyBuilder loop. The draft is discarded afterwards, so the
                // probe leaves no row in local Drafts and no files in the stage folder.
                case ("GET", "/forward-attach"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);

                    var draft = ComposeService.BuildForwardAsAttachment(account, folder, summary);
                    try
                    {
                        string? staged = draft.AttachmentPaths.FirstOrDefault();
                        var builder = new BodyBuilder();
                        if (staged != null) await builder.Attachments.AddAsync(staged);

                        return new
                        {
                            draft.Subject,
                            bodyIsAttributionOnly = !draft.Body.Contains("\n> "),
                            staged,
                            stagedBytes = staged is { } stagedPath ? new FileInfo(stagedPath).Length : 0,
                            cachedBytes = MessageStore.TryLoadFullMessage(accountId, folder, uid)?.Length ?? 0,
                            contentType = builder.Attachments.FirstOrDefault()?.ContentType.MimeType
                        };
                    }
                    finally
                    {
                        ComposeService.DeleteDraft(draft);
                    }
                }

                case ("POST", "/send"):
                {
                    var body = await JsonSerializer.DeserializeAsync<SendRequest>(request.InputStream, Json)
                        ?? throw new InvalidOperationException("Empty request body.");
                    var account = RequireAccount(body.AccountId);
                    var draft = new ComposeDraft
                    {
                        Account = account,
                        To = body.To,
                        Cc = body.Cc,
                        Subject = body.Subject,
                        Body = body.Body
                    };
                    foreach (string sourcePath in body.AttachmentPaths)
                    {
                        await using var source = File.OpenRead(sourcePath);
                        await ComposeService.AttachFileAsync(draft, source, Path.GetFileName(sourcePath));
                    }
                    if (body.Queued)
                    {
                        ComposeService.SaveDraft(draft);
                        OutboxService.Enqueue(draft);
                        return new { ok = true, queued = true, dueUtc = OutboxService.Current?.DueUtc };
                    }
                    var outcome = await ComposeService.SendAsync(draft);
                    return new { ok = true, outcome = outcome.ToString(), attachments = draft.AttachmentPaths.Count };
                }

                case ("POST", "/flush-outbox"):
                    await OutboxService.FlushAsync();
                    return new { ok = true };

                // A raw header can be passed instead of a message, so the parser and the link gate
                // can be exercised on the shapes real newsletters send without hunting for one.
                // Builds a draft from the query and runs the pre-send checks on it, so every
                // warning is reachable without a compose window.
                case ("GET", "/send-check"):
                {
                    var draft = new ComposeDraft
                    {
                        Account = query["accountId"] is { Length: > 0 } guardAccountId ? RequireAccount(guardAccountId) : null,
                        To = query["to"] ?? string.Empty,
                        Cc = query["cc"] ?? string.Empty,
                        Subject = query["subject"] ?? string.Empty,
                        Body = query["body"] ?? string.Empty,
                        ArrivedAtAddress = query["arrivedAt"] ?? string.Empty
                    };
                    // GetValues, not the indexer: a multi-file draft is what the size ceiling needs.
                    draft.AttachmentPaths.AddRange(query.GetValues("attachment") ?? []);
                    return new { warnings = SendGuardService.Inspect(draft) };
                }

                case ("GET", "/outbox"):
                    return new
                    {
                        pending = OutboxService.Current is { } pending ? new { pending.Draft.Subject, pending.DueUtc } : null,
                        failure = OutboxService.LastFailure is { } failure
                            ? new { failure.Draft.Subject, failure.Reason, failure.FailedAtUtc }
                            : null,
                        heldCount = OutboxAttemptStore.HeldCount,
                        // The queue itself, which nothing could see before: a stuck message was
                        // indistinguishable from one queued a moment ago.
                        queued = AccountStore.Accounts.SelectMany(a =>
                            MessageStore.GetSummaries(a.Id, MessageStore.LocalFolderPrefix + ComposeService.LocalOutboxFolderName)
                                .Select(q => new
                                {
                                    accountId = a.Id,
                                    q.Uid,
                                    q.Subject,
                                    attempts = OutboxAttemptStore.For(a.Id, q.Uid)?.AttemptCount ?? 0,
                                    lastError = OutboxAttemptStore.For(a.Id, q.Uid)?.LastError ?? string.Empty,
                                    held = OutboxAttemptStore.IsHeld(a.Id, q.Uid)
                                }))
                    };

                case ("POST", "/outbox/hold/clear"):
                    return new { released = OutboxAttemptStore.ReleaseHolds() };

                case ("POST", "/outbox/retry"):
                    OutboxService.RetryFailed();
                    return new { ok = true };

                case ("POST", "/undo"):
                {
                    var draft = OutboxService.Undo();
                    if (draft != null) MailUiState.OpenCompose(draft);
                    return new { undone = draft != null };
                }

                case ("POST", "/compose"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var draft = ComposeService.BuildNew(account);
                    if (query["attachmentPath"] is { Length: > 0 } attachmentPath)
                    {
                        await using var source = File.OpenRead(attachmentPath);
                        await ComposeService.AttachFileAsync(draft, source, Path.GetFileName(attachmentPath));
                    }
                    // SelectAccount nulls the folder, and the page normally picks the next one
                    // straight after. Doing the same here keeps the app in a state a user could
                    // actually be in rather than one with no folder selected at all.
                    MailUiState.SelectAccount(account);
                    var folders = MessageStore.GetFolders(account.Id);
                    if (FolderMemoryStore.ResolveStartFolder(FolderMemoryStore.FolderOf(account.Id), folders) is { } start)
                        MailUiState.SelectFolder(start);
                    MailUiState.OpenCompose(draft);
                    return new { ok = true, draft.DraftId, attachments = draft.AttachmentPaths.Count };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}