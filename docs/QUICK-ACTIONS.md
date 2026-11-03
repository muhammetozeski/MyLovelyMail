# Mail quick actions

The list's **Actions** button opens the same workspace menu as a right-click on blank mail space.
It contains selection, list navigation, keyboard help, compose and sync commands. Opening this menu
does not select a focused or opened message. Reader **Message actions**, **Sender actions** and the
person sheet's **Actions** button expose their existing scoped menus without requiring a mouse.

## Selecting cached results

**Select** offers all/clear/invert, unread/read, starred/unstarred, important/normal, attachment,
tag, mute and snooze criteria. These operate on the currently listed results, including a search
across folders. A matching member selects its entire displayed conversation; criteria can therefore
overlap. Counts are message counts, including collapsed conversation members. A partially selected
conversation becomes fully selected when the listed selection is inverted.

The focused row supplies the **Focused conversation only**, **Start through focused row** and
**Focused row through end** ranges. **Shift+Up/Down** extends selection using the same anchor as
Shift-click. **Ctrl+Shift+A** inverts the listed selection. **Home/End** focuses the first/last row.

Menu callbacks capture full account/folder/message identities. Selection callbacks intersect that
snapshot with the current listed identities before replacing selection, so stale results cannot
select a different account or an unrelated message with the same message number.

## Selection actions

The bulk bar's **More actions** and message-menu **Selection actions** share one factory. Explicit
star/importance commands change only captured messages whose current flag differs from the requested
state; a mixed selection is normalized rather than toggled. Filing and wake commands use
`MouseActionService.RunMessagesAsync` and retain the existing storage, protocol and status behavior.
Tag removal lists only tags actually present in the captured selection.

Copy commands re-resolve captured summaries from cache, order them newest first and copy subjects,
distinct sender addresses, distinct message references or tab-separated date/sender/recipient/subject
details. They do not fetch message bodies. Sender and message-reference lists omit empty values.

## Reading the list

The reader's **Previous**, **Next** and **Next unread** buttons follow displayed row order. The
navigation submenu also offers previous unread and previous/next starred conversations. A qualifying
conversation opens a qualifying member. Navigation stops at either boundary without wrapping and
preserves the existing draft-versus-message opening behavior. Conversation-internal **N/P** remains
separate from navigation between listed rows.

Keyboard shortcuts are scoped to the mail list. Modified browser shortcuts do not invoke plain-letter
triage. Escape dismisses tag, move and snooze pickers before clearing selection or closing the reader.
All new layout rules remain in theme-aware `Css.razor` sections using shared spacing constants.

The behavior above is established by source inspection of `Mail.razor.QuickActions.cs`, the selection
and interaction partials and the existing command services. It does not assert native-device interaction
verification. For menu ownership and identity rules, see [the development guide](DEVELOPMENT.md#shared-input-and-message-identity).
