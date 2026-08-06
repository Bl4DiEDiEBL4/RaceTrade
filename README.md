# RaceTrade v2 WebUI

RaceTrade is an IRC race manager with CBFTP integration. The v2 branch splits the
old WinForms application into a reusable racing engine and a local browser-based
WebUI:

- `RaceTrade.Engine` contains the racing, rules, CBFTP, IRC, PreBot, IMDB/Tiffara,
  TMDb and TVMaze logic.
- `RaceTrade.Web` is the new local WebUI that runs in the same executable as the
  engine.

The app still stores normal config/data folders such as `sites`, `cbftp`,
`pre_bots`, `settings`, `sections` and `db`, but in v2 they live under the data
folder shown on the Settings page. By default that folder is `data` next to the
executable.

## Screenshots

Screenshots can be added here after uploading them to GitHub:

- Dashboard
- Site editor
- CBFTP server/site editor
- FXP client
- Pre / Affil spread manager
- PreBots
- Logs

## Highlights

- Local WebUI with Dashboard, Sites, CBFTP servers, PreBots, Pre manager, FXP
  client, Test release, Chat, Logs, Settings, Help and Changelog pages.
- Site editor with ZNC, channels, announce parsing, sections, rules, affils,
  blacklist, request auto-fill and CBFTP site access.
- Section editor with Racing/Off toggles, CBFTP mappings, section rules, mapping
  rules, IMDB movie filters and TVMaze series filters.
- CBFTP site import and direct CBFTP-side site editing.
- Dual-pane FXP browser.
- PreDB release import and Affil Spread / Pre Manager flow.
- Single-file self-contained publish output for Windows and Linux.

## Run From Source

Install the .NET 8 SDK, then run:

```bat
dotnet run --project RaceTrade.Web
```

The default local URL is:

```text
http://127.0.0.1:8420
```

The WebUI binds to loopback only by default. To expose it to another machine, set
a password first:

```bat
RaceTrade.exe --set-password
```

Then change `Web:BindAddress` in `RaceTrade.Web/appsettings.json` or in the
published `appsettings.json`. Prefer a VPN/tunnel over exposing the port directly,
because RaceTrade stores site and CBFTP credentials.

## Build Check

```bat
dotnet build RaceTrade.Modern.sln -c Debug
```

## Release Without Loose DLLs

Do not package `bin\Release`. A normal .NET build folder always contains loose
DLLs. That is expected build output, not the release package.

For a clean single-executable release, run:

```bat
publish.bat
```

That script runs `dotnet publish` with single-file, self-contained settings and
writes output under:

```text
Release\win-x64\RaceTrade.exe
Release\linux-x64\RaceTrade
```

The important publish settings are already in `RaceTrade.Web/RaceTrade.Web.csproj`:

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<PublishTrimmed>false</PublishTrimmed>
```

You can also publish one platform manually:

```bat
dotnet publish RaceTrade.Web\RaceTrade.Web.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -o Release\win-x64
```

For Linux:

```bash
./publish.sh linux-x64
```

On Debian, copy the Linux binary, make it executable if needed, and run it:

```bash
chmod +x RaceTrade
./RaceTrade
```

The published app is self-contained, so users do not need to install .NET on the
target machine.

## Data Folder

By default, RaceTrade creates and uses:

```text
data\
```

next to the executable. Override it with:

```bat
RaceTrade.exe --data C:\RaceTradeData
```

Keep runtime data out of git. `Release/`, `work/`, `data/`, `cbftp/`, `db/`,
`settings/` and generated config folders are ignored.

## Repository Layout

```text
RaceTrade.Engine/      racing engine and legacy-compatible services
RaceTrade.Web/         local WebUI
RaceTrade.Modern.sln   v2 solution
publish.bat            Windows release publisher
publish.sh             Linux/macOS shell publisher
RaceTrade/             legacy WinForms project
```

## Notes

- Use the Help page inside the WebUI for the current configuration guide.
- Use the Changelog page inside the WebUI for user-facing changes.
- The legacy WinForms project is kept in the repository for history and reference.
