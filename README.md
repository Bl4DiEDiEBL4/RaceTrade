# RaceTrade v2 WebUI

RaceTrade is an IRC race manager with CBFTP integration and a local browser UI.

## Screenshots

Dashboard

<img width="1912" height="914" alt="RaceTrade dashboard" src="https://github.com/user-attachments/assets/a3b0a5b9-0620-4cec-96a6-2f2e5fc03b38" />

Site editor

<img width="1912" height="914" alt="RaceTrade site editor" src="https://github.com/user-attachments/assets/d5f79c5c-bbd6-450d-b467-227d2c308348" />

CBFTP servers and site import

<img width="1912" height="914" alt="RaceTrade CBFTP servers and site import" src="https://github.com/user-attachments/assets/36b1b3ce-aa64-46ac-896d-10493c1b1a74" />

FXP Client

<img width="1912" height="914" alt="RaceTrade FXP client" src="https://github.com/user-attachments/assets/4b93fd16-a2b8-4c3f-85f6-0410fd1bd0c8" />

Help

<img width="1912" height="914" alt="RaceTrade help page" src="https://github.com/user-attachments/assets/04e126f3-30df-4aff-98e3-a6d162922112" />

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

## Upgrade From v1.0.9 WinForms

Copy your old folders into the v2 data folder:

```text
sites\
cbftp\
pre_bots\
sections\
settings\
db\
```

Old WinForms passwords and Blowfish keys may be stored as Windows DPAPI `ENC:`
values. They are not portable password hashes; they can only be decrypted by the
same Windows user that created them.

Run this once on that same Windows account:

```bat
RaceTrade.exe --migrate-legacy-secrets --data D:\RaceTrade\data
```

That converts old `ENC:` secrets to v2 `ENC2:` secrets and keeps `.bak` backups
of changed JSON files. After that, the data folder can be used by the v2 WebUI
and copied to the Linux build if needed.

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
