# Sender actions and notification precedence

The sender's context menu is also reachable through **Sender actions** in the reader and **Actions**
in the person sheet. `Mail.razor.Senders.cs` captures the exact address and account before opening
the menu. Later commands resolve that captured account rather than whichever account is selected
when a callback runs. Composing opens a draft in that account; it does not send it.

## Filing old and new mail

**Move past and future mail to** first captures an existing selectable folder in the same account.
It imports matching retained history before filing anything, checks the destination again, saves
the sender rule through `FilterRuleStore.UpsertSenderMoveRule`, then submits complete message
identities to the existing grouped move operation. Failed historical imports leave existing mail
in place and do not save the new filing rule. A saved rule and a subsequent move failure are
reported separately. Server moves retain their source until the server confirms them.

History loading is specific to the selected sender:

- `ImapSyncService.CacheSenderHistoryAsync` searches accessible server folders, verifies the
  actual envelope mailbox after the server's substring search, and imports matching summaries
  in batches. A local destination also requires downloading their complete message bodies.
  It preserves ordinary synchronization watermarks; changed server message identity validity
  invalidates that folder's old cache before new identities are used.
- `Pop3Service.CacheSenderHistoryAsync` scans retained server headers without changing the
  account's fetch limit and downloads full messages only for exact sender matches.
- `FastMailSyncService.CacheSenderHistoryAsync` uses the provider's complete retained inbox list,
  the account's existing message-number map, and downloads matching message bodies over Tor.
  It participates in the account operation gate so deletion cancels or waits for this work.

Provider history means mail still retained by that provider. Previously filed cached mail from
the same sender is included as well. Drafts and Outbox are excluded as filing destinations.
When matching past messages are already local, the combined operation offers local destinations:
the existing move engine has no path for uploading local mail into a server folder. **Move future
mail only to** can select a server destination for an IMAP account independently of that history.
Before local filing, every matching source message must have a complete cached body; an
unrecoverable missing body stops the operation before messages are moved.
Long history imports use the existing round-trip guards for each protocol operation instead of
wrapping the entire history in one finite network-operation budget.

The **Cached mail from this sender** submenu deliberately acts only on existing cached matches.
Read, unread, star, importance, filing, tag, snooze and wake commands reuse `MessageActions` and
`MouseActionService`; positive and negative marker actions select only messages that need a
change. Selection and copying never expand an unrelated current selection.
Cached-only sender filing also downloads missing bodies before invoking any local move.

## Notifications and future rules

Sender notification choices are persisted filter actions, with no second mute store. **Mute this
sender** stores `Mute`, **Notify for this sender** stores `Unmute`, and inheritance removes this
notification action family. Automatic filing has a separate action family, so resetting sender
notifications preserves filing and tag choices. Future read, star, importance, tag and sound
presets use the same scoped filter store and remain editable in the full filter editor.
Incoming local filing in the receive services saves the complete message body before moving its
cache entry; a download or atomic-write failure retains the source message.

`NotificationService.AllowsNotification` resolves each arrival independently. Sender decisions
have priority 400; explicit account choices 300; mailbox folder preferences 250; application
preferences and quiet hours 100. A rule wins ties with a setting at its own scope. Thus enabling
one sender can override an account or folder mute, while a global enable action cannot override
an account mute. Trash always suppresses notifications. Rule destination checks use the same
decision before a move changes the message number. Quiet hours apply when no higher explicit
choice overrides them. A silent notification sound retains the visual notification.

## Person identities

`ContactIndexService.Recipients` parses complete mailbox addresses, including quoted display
names. `PersonProfileService.Build` counts actual messages from that person and messages sent
by the account to that exact recipient, independent of which folder currently holds them.
Address substrings and unrelated recipients in group messages do not create person history.

These contracts are based on source inspection. Native interaction and live provider behavior
require application-level verification in their own environment.
