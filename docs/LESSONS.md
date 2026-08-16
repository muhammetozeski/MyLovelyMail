# Lessons learned

Short "problem → solution" notes so the same wall is never hit twice.

- **Parameterless `*Css.razor` components do not re-render on theme change.** Blazor skips
  re-rendering children whose parameters did not change, so a `ThemeManager.Apply` left every
  emitted `<style>` block on the old palette. Solution: `ThemedComponentBase` (UI/Architecture)
  subscribes to the `OnThemeChanged` event and calls `StateHasChanged`; every parameterless Css
  component must inherit it.

- **The template never rendered `MainLayoutCss`.** `MainLayout.razor` did not include
  `<MainLayoutCss />`, so the aurora background, fonts and base body styles were silently missing
  on every platform. Solution: render it at the end of `MainLayout.razor`.

- **`InteractiveAuto` on the Web host moves interactivity into WASM after the first visit.**
  Clicks then run inside the browser: `File.WriteAllText` writes into the browser's virtual file
  system and the server-side disk never changes, which made settings look "not persisted" while
  the UI happily updated. The MAUI app is unaffected (BlazorWebView runs in-process). Solution:
  the Web preview host pins `@rendermode="InteractiveServer"` so preview behavior matches the
  desktop app.

- **The template's `NavigationGenerator` emitted no `BottomNavigationItems`,** but
  `AppBottomNav.razor` consumes it — the template did not compile as shipped. Solution: the
  generator now emits a `(Title, Icon, Link)[]` array after the per-page classes.

- **Windows-invalid characters in Brainstorm idea file names** (`:` and `/` in titles) make
  `Set-Content` fail with misleading binding errors. Keep idea titles free of `\/:*?"<>|`.
