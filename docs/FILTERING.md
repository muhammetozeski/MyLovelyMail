# Scoped mail filters

Filters are stored in `UserData/filters.json` by `FilterRuleStore`. Incoming summaries enter
`RuleEngine.ProcessIncoming` before filing or notification decisions. The caller executes the
returned move requests through its protocol/storage owner; the engine itself performs no network
operations. Rules affect genuinely new arrivals. The editor's **Try it** scans cached summaries
without changing flags, tags, bodies or server state.

## Scope and precedence

`FilterRule` retains the existing `AccountId` field and adds three optional selectors:

| Selector | Meaning | Priority |
| --- | --- | --- |
| `SenderAddress` | Exact sender mailbox, ignoring display name and letter case; optionally limited by `AccountId` | 400 |
| `AccountId` alone | One mail account | 300 |
| `AccountId` + `FolderFullName` | One exact source mailbox path | 250 |
| `AccountFolderId` | Accounts in this sidebar container and all descendant containers | 200 + nesting depth, capped at 249 |
| No scope selectors | Entire application | 100 |

Mailbox folders are the account's received-mail folders; account folders are sidebar containers of
accounts. Both are supported. A mailbox path matches its source before any rule moves the message.
Matching a container follows current account placement and parent links. A deleted container ID
matches nothing and never becomes the default Main container. Moving an account or a container
therefore changes the applicable inherited rules on its next arrivals.

Rules are sorted by descending priority, retaining saved order for ties. Each message resolves one
action per conflicting family: move, read/unread, important/clear, star/unstar, mute/allow and sound.
Tag actions resolve separately for each case-insensitive tag name, so unrelated tags accumulate.
The first matching action in a family wins. A higher rule choosing the current folder also claims
the move family, so broader rules cannot redirect it. At most one remote or local move is emitted.
`StopProcessing` ends remaining rules at the current and lower priorities; a broad Stop cannot
prevent a stronger sender rule from being considered first. Move-up/down controls reorder peers
within the same priority and cannot reverse scope precedence.

A scoped rule without conditions matches every message in its scope. Application rules require
at least one condition. Old rules without new selectors retain their application/account scope;
legacy attachment operators and nonnumeric size operators are normalized on load to their previous
effective behavior. Numeric size comparison now also supports equality using invariant decimal
parsing. `FromAddress` tests the sender address alone; `From` retains name-plus-address matching.

## Notifications and explicit overrides

`Mute` and `Unmute` form one action family. `RuleEngine.GetNotificationOverride` and
`GetNotificationPriority` expose the winning per-summary decision without a cross-account
message-ID lookup. Custom sounds use the same summary identity, including messages with an absent
or duplicated Message-ID. `NotificationService` resolves this with account and folder preferences:
sender rule, explicit account choice, mailbox folder choice, then application defaults/quiet hours.
At equal priority an explicit rule wins. Trash always suppresses arrival notifications. Destination
folder suppression uses the same decision before moves can change message numbers.

Notification decision metadata is transient and weakly references the arrival summary. It is used
for that receive pass, while persisted Muted flags retain ordinary message state. Sound selection
remains independent from suppressing a notification; the sound name `none` keeps a silent toast.

## Editor and integration entry points

`FilterRules.LinkFor(accountId, folderFullName, accountFolderId, senderAddress)` creates escaped
scope links. The page lists rules for the requested scope, seeds new rules with those selectors,
and offers **Show all filters** to return to the full list. Rules can be searched by name, scope and
action, restricted to enabled rules, previewed, duplicated or copied as a complete JSON definition.
The regular action button and right-click menu share the same commands.

Preview examines at most 5,000 cached summaries and reports when that cap excludes more results.
Changing an input clears the prior preview. Saved actions run only after input validation: invalid
regular expressions, unsupported field/operator combinations, missing action arguments, missing
scope references and multiple move destinations are rejected. Folder target choices use real
selectable folders and intersect available paths for rules spanning several accounts.

Sender shortcuts use `UpsertSenderActionRule`, `UpsertSenderMoveRule` and `UpsertSenderMuteRule`.
They maintain one condition-free shortcut rule per sender/account/action family (per tag for tag
actions), replacing a previous move or opposite notification choice. An explicit sender shortcut
moves its rule before peers at the same priority so an
older conditional sender rule cannot silently shadow the clicked choice. Local move arguments omit
`MessageStore.LocalFolderPrefix`; the convenience API accepts and removes that prefix.
`RemoveSenderActionRule` removes the paired family, including both remote/local moves or mute/allow.
Rules with additional conditions are preserved because they belong to the full editor.

Persistence uses an atomic file write and publishes the new list only after success. Saves clone
their input so an editor draft or later caller mutation cannot silently change stored rules. A failed
read blocks edits from overwriting the unreadable file; a later edit retries the read before merging
its change into recovered rules. Store mutations are serialized with one lock.

Source ownership: [rule model](../MainProject/DataModels/Mail/FilterRule.cs),
[engine](../MainProject/Services/Mail/RuleEngine.cs),
[store](../MainProject/Stores/FilterRuleStore.cs),
[editor](../MainProject/UI/Pages/FilterRules.razor),
[notification decisions](../MainProject/Services/NotificationService.cs).

## Verification

`tests/FilterChecks` consumes the real MainProject through a project reference with no additional
test packages. Its executable refuses to initialize stores outside its designated temporary artifacts
folder. The synthetic service suite passed 46 checks covering scope inheritance and isolation,
conflicting actions, peer ordering, explicit sender shortcut precedence, sender shortcut updates,
notification/sound isolation, invariant numeric comparison, validation, scoped read-only preview,
draft cloning, escaped scope links and persistence failure/recovery. It uses synthetic account and
message data without contacting providers. The shared Razor pages compiled with that project.
This is service/compiler verification; it does not claim native-window interaction or real-provider delivery.

```powershell
dotnet build tests/FilterChecks/FilterChecks.csproj -p:SkipAndroidTargets=true -p:ArtifactsPath=C:\C\TorAnonymity\FilterChecks
dotnet C:\C\TorAnonymity\FilterChecks\bin\FilterChecks\debug\FilterChecks.dll
```

Expected completion: `Completed 46 filter checks.` followed by the isolated data directory.
