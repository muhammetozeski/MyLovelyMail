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

## Universal pure-C# button and context-menu customization

Every toolbar, hover action strip, and right-click menu surface (`UiSurfaceIds`) resolves its visible
order, hidden items, and cross-surface additions through `UiCustomizationStore` (`UserData/ui-layout.json`).
`CustomizableActionBar`, `ActionSlot`, and `AppContextMenu` coordinate pointer drag-and-drop in pure
Blazor C# via `ActionDragService` (without custom DOM JavaScript):

- Non-fixed buttons reorder inside their bar, hide, or drag into the inline `Right-Click` dock to
  move onto their paired context menu.
- Context-menu items and submenu entries (including dynamic commands such as `Msg.AddTag:<Tag>`,
  `Msg.Move:<Folder>`, and sender rule presets) reorder, promote to the 1-click root context menu,
  or drag directly onto screen toolbars (`BackdropPassThrough` lets pointer drags target underlying bars).
- `UiCustomizerModal` (`WideModal`) inspects all 19 surfaces, supports inline tag creation (`+ Tag`),
  and allows resetting individual surfaces or all customizations.
- Sidebar account groups, folder-internal mail accounts, mailbox folders, and mail rows use pure-C#
  pointer drag-and-drop (`WorkspaceDragService`), persisting sort orders atomically to
  `account-folders.json`, `account.json`, `folder.json`, and `list-view.json`.

Runtime interaction, screenshot rendering, and full restart persistence were verified through `DebugApi`
(`GET /ui-layout`, `POST /input/mouse`, and `GET /screenshot`). For menu ownership and identity rules,
see [the development guide](DEVELOPMENT.md#shared-input-and-message-identity).
