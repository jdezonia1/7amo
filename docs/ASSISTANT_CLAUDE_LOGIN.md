# Ask Raffaello with your Claude login (no API key)

Branch: `claude/assistant-claude-login` (based on `claude/hotel-recon`). Not merged anywhere yet.

## What it is

"Ask Raffaello" can now answer with your own Claude subscription instead of an Anthropic API key.
The app starts **Claude Code** (Anthropic's command-line tool, the same one you already use) in the background for each
question. Claude Code signs in with your Claude account, so there is no key to buy, paste or store.

Claude gets a small set of **read-only** Raffaello tools (an "MCP server" built into Raffaello.exe). With them it looks up
the real figures in your data file before it answers, and it puts clickable sources (rooms, invoices, WIRs ...) under the answer,
like the API-key mode does.

The same tools also work in the **Claude Desktop** app (optional, see below).

## One-time setup on the work PC

1. Install Claude Code (skip if `claude --version` already works in a terminal):
   open PowerShell and run
   `irm https://claude.ai/install.ps1 | iex`
   (or, if Node.js is installed: `npm install -g @anthropic-ai/claude-code`).
2. Log in once: open a **new** PowerShell window, type `claude`, press Enter and sign in with your Claude account
   (the browser opens). When you see the Claude prompt, type `/exit`.
   Note: being logged in to the Claude *Desktop* app is not enough - the command-line tool keeps its own login.
   You can check with `claude auth status` (it must say `"loggedIn": true`).
3. In Raffaello: SETTINGS > ASSISTANT > PROVIDER. Leave **AUTO** (uses an API key if one is saved, otherwise your Claude
   login, otherwise offline) or choose **CLAUDE LOGIN**. Press **CHECK CLAUDE CODE**: it must say
   "Claude Code found (...), logged in". Press SAVE.

That is all. The status line under the chat shows `CLAUDE LOGIN (Claude Code) | ... | read-only` when it is active.

## How to use

Open Ask Raffaello (Ctrl+Shift+A or the ASSISTANT page) and ask as usual, in English or Arabic, e.g.

- How many hotel guest rooms have 2nd fix DATA remaining?
- What has ROOTS claimed on level 3, 2nd fix light?
- Which WIRs are still open for the hotel?
- كم المتبقي في الغرفة P2-106 للتمديد الثاني؟

While it works you see "Reading: rooms remaining..." etc. STOP cancels the run at once. A question normally takes
10-60 seconds (Claude Code starts, reads the data, answers). The time limit (default 240 s) is in Settings.

Settings > Assistant, CLAUDE LOGIN box:
- CLAUDE.EXE: leave empty (found automatically: PATH, `%USERPROFILE%\.local\bin`, npm). Fill only if it is installed elsewhere.
- MODEL: leave empty for your plan's default, or type `sonnet` / `opus`.
- TIME LIMIT S: seconds before a question is stopped.

## What Claude can see (read-only)

Through the Raffaello tools Claude can READ: rooms (type, area, level), the room ledger and remaining per room x stage x item,
invoices and their Aconex workflow, contracts and their schedule items, delivery notes / POs / MIR, WIRs, site statements,
the "needs you today" list, anomalies, the management reports, stored documents (search) and the house rules.

It can NOT change anything:
- The data file is opened read-only and copied to a private temporary snapshot; every tool reads the snapshot. The snapshot is
  refreshed when the data changes and deleted afterwards.
- The writing tools of the API-key mode (draft claim, invoice revision, reply e-mail, variation, reminder) are not offered.
  If you ask Claude to post or create something, it tells you what to enter in the app. (For CONFIRM cards use the API-key mode.)
- Claude Code runs with all its own tools switched off (no commands, no file editing, no web) and without your other
  Claude Code settings, plugins or MCP servers.

## Limits

- Every question counts against your Claude plan's usage limits (the same pool as claude.ai and Claude Code).
  Long questions that need many lookups use more.
- Slower than the API-key mode (Claude Code starts for every question).
- Attached files: only their text is passed. Scans / images need the API-key mode (cloud reading).
- Server mode (Settings > Data source = SERVER) is not supported yet: CLAUDE LOGIN reads the local data file only.
- If Claude Code is missing, logged out or fails, the chat says why and answers from local data (offline), as before.

## Privacy

- Your question, the screen context (screen, filter, selected line), the last messages of the chat and the results of the
  read tools are sent to Anthropic through Claude Code - the same kind of data the API-key mode sends. The line under the
  chat box says this.
- No password, token or key is read, stored or written by Raffaello. The login stays inside Claude Code.
- Claude Code is started with "no session persistence", so it does not keep a transcript of these questions on disk.
  The conversation is kept in Raffaello's own history, as before.

## Claude Desktop (optional)

Settings > Assistant > **USE WITH CLAUDE DESKTOP** shows a ready snippet and a COPY button. Paste it into Claude Desktop's
`claude_desktop_config.json` (Claude Desktop > Settings > Developer > Edit Config), save, quit Claude Desktop from the tray
and start it again. The tools icon then lists "raffaello". Raffaello never edits that file itself.
The snippet points at `Raffaello.exe --mcp --db <your data file>` - keep Raffaello in the same folder.

## Tested on this PC (03-Oct-2026, copy of the test data file)

- `Raffaello.exe --mcp` answered the MCP handshake and listed 14 read-only tools; no writing tool listed; a writing call is refused.
- The real Claude Code 2.1.287 accepted every option, started the Raffaello server ("connected") and saw exactly the 14
  Raffaello tools and no built-in tools. It then stopped with "Not logged in" because the command-line tool on this laptop
  was never signed in (the Claude Desktop app's login is separate) - step 2 above fixes that.
- The same tools Claude would call answered (test copy):
  - Hotel guest rooms (area GUESTROOM) with 2ND FIX DATA remaining: **152 of 187** (1,108 of 1,464 points remaining;
    35 fully claimed; 117 not started; none OVER).
  - Hotel guest rooms with 2ND FIX LIGHT remaining: 185 of 187 (5,320 of 9,734 remaining).
  - Hotel 2ND FIX DATA overall: PROJECT QTY 2,909, claimed 576, remaining 2,333.
  - Hotel WIRs: 255 (212 APPROVED, 39 OPEN, 4 REJECTED).
- Core tests: 520 passed (35 new). App build 0 errors / 0 warnings. XamlCheck 0 problems.

## For whoever merges (files)

New:
- `src/Raffaello.Core/Assistant/AssistantProvider.cs` - AUTO / API KEY / CLAUDE LOGIN / OFFLINE choice, Claude Desktop snippet
- `src/Raffaello.Core/Assistant/ClaudeCode/ClaudeCodeLocator.cs`, `ClaudeCodeRunner.cs`, `ClaudeCodeStream.cs`
- `src/Raffaello.Core/Assistant/Mcp/McpServer.cs`, `McpHost.cs`, `McpDataSource.cs`, `McpToolCatalog.cs`, `ExtraReadTools.cs`
  (new read tools: list_rooms, rooms_remaining, list_wirs, list_site_statements - MCP only for now)
- `tests/Raffaello.Core.Tests/ClaudeLoginTests.cs`

Changed (small, marked `[claude-login]`):
- `src/Raffaello.Core/Assistant/AssistantSession.cs` - Router / ClaudeCode hooks, Claude Code route, privacy line
- `src/Raffaello.Core/Assistant/AssistantSettings.cs` - Provider, ClaudeCodePath, ClaudeCodeModel, time limit, max turns
- `src/Raffaello.App/App.xaml.cs` - `Raffaello.exe --mcp` runs the server before any window
- `src/Raffaello.App/Services/Assistant/AssistantHost.cs` - Claude Code check, route, runner, MCP command
- `src/Raffaello.App/ViewModels/AskViewModel.cs` - status line per engine
- `src/Raffaello.App/ViewModels/AssistantSettingsViewModel.cs`, `src/Raffaello.App/Views/AssistantSettingsCard.xaml` - PROVIDER,
  CLAUDE LOGIN box, CHECK, USE WITH CLAUDE DESKTOP
- `tools/Raffaello.Cli/Program.cs` - one line: `raffaello-cli mcp --db FILE`

Overlap with other branches at the time of writing: none of these files are changed on `claude/hotel-recon`,
`claude/dazzling-turing-20kq62` or `claude/aconex-saved-logins` (the latter changes SettingsView.xaml / SettingsViewModel.cs,
which this branch does not touch). `claude/home-and-tracker` was not on GitHub yet - check `AskViewModel.cs`, `App.xaml.cs`
and `tools/Raffaello.Cli/Program.cs` when merging it.

## Not done yet (future)

- Writing through Claude (draft claim / invoice revision / reminder ...) - would need the app's CONFIRM flow over MCP.
- Server mode data; cable tools; the extra read tools are not yet offered to the API-key mode.
- Not yet clicked through on screen: the new Settings box and a real answer in the chat (needs the CLI login first).