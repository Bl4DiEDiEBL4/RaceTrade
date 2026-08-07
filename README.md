# RaceTrade v2 WebUI

RaceTrade is an IRC race manager with CBFTP integration and a local browser UI.

The app runs as one executable. Start `RaceTrade.exe`, your browser opens, and the
racing engine runs in the same process.

## Download And Start

1. Download the Windows release.
2. Put `RaceTrade.exe` in its own folder, for example `D:\RaceTrade`.
3. Start `RaceTrade.exe`.
4. Open the WebUI at:

```text
http://127.0.0.1:8420
```

On first start RaceTrade creates a `data` folder next to the executable. That is
normal. It contains your sites, CBFTP settings, prebots, logs and databases.

## Run Multiple Copies

Every running RaceTrade needs its own web port. If port `8420` is already used,
start the next copy on another port:

```bat
RaceTrade.exe --port 8421
```

For separate configs, also give each copy its own data folder:

```bat
RaceTrade.exe --port 8421 --data D:\RaceTrade-HV\data
RaceTrade.exe --port 8422 --data D:\RaceTrade-KPN\data
```

You can put the same command in a Windows shortcut target.

## Data Folder

Default:

```text
data\
```

Override:

```bat
RaceTrade.exe --data D:\RaceTradeData
```

RaceTrade uses these folders inside the data folder:

```text
sites\
cbftp\
pre_bots\
sections\
settings\
db\
userdata\
logs\
```

## Features

- Dashboard with trader status and quick actions.
- Site editor for ZNC, channels, announce parsing, sections, rules, affils,
  blacklist, requests and CBFTP site settings.
- CBFTP server import and CBFTP-side site editor.
- Dual-pane FXP browser.
- PreBots and Pre / Affil Spread manager.
- Test Release page for checking mappings, rules, IMDB/Tiffara/TMDb and TVMaze.
- Chat, logs, settings, help and changelog pages.

## Help And Changelog

Open `Help` in the left menu for the current WebUI guide.

Open `Changelog` in the left menu for user-facing changes.

## Build A Release

Only needed when building from source:

```bat
publish.bat
```

Upload only the executable from:

```text
Release\win-x64\RaceTrade.exe
```

Do not upload `bin\Release`. That folder is normal build output and can contain
loose DLLs.
