# RaceTrade v2 WebUI

RaceTrade is an IRC race manager with CBFTP integration and a local browser UI.

## Screenshots
Dashboard
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/383ca392-4b87-447e-897a-0892be1ac6d5" />

Sites
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/58c2359c-a200-4e45-ba7a-6ac3f9e868de" />

Site Editor
<img width="1700" height="882" alt="image" src="https://github.com/user-attachments/assets/51e23355-2676-463a-b5e6-69081a4832d3" />

CBFTP servers
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/25c885c2-2a46-4d04-9b80-e4209dab05b1" />

Pre
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/d4d638fd-2949-4e97-961a-3450f765ec46" />

Test Release
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/28d349ba-918c-4855-aed3-a0ec85df0ea1" />

FXP Client
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/0b7cdfb6-82b1-4720-9834-ff47f0170c3f" />

Chat
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/3c56dbca-92f0-4df1-b969-25b2c8fb772e" />

Help
<img width="1912" height="914" alt="image" src="https://github.com/user-attachments/assets/ff8a245b-5536-4828-9675-d6e91517708b" />










The app runs as one executable. Start `RaceTrade.exe`, your browser opens, and the
racing engine runs in the same process.

## Download And Start

Choose the release for your machine:

- Windows: `RaceTrade-v2.x.x-win-x64.zip`
- Linux PC/server: `RaceTrade-v2.x.x-linux-x64.zip`
- Raspberry Pi 5 / ARM64 Linux: `RaceTrade-v2.x.x-linux-arm64.zip`

1. Download the release for your platform.
2. Put the executable in its own folder, for example `D:\RaceTrade` on Windows.
3. Start `RaceTrade.exe` on Windows or `RaceTrade` on Linux.
4. Open the WebUI at:

```text
http://127.0.0.1:8420
```

On first start RaceTrade creates a `data` folder next to the executable. That is
normal. It contains your sites, CBFTP settings, prebots, logs and databases.

On Linux/Raspberry Pi, make the binary executable and set a WebUI password before
exposing it to another machine:

```bash
chmod +x RaceTrade
./RaceTrade --set-password
```

Then run it for LAN access:

```bash
./RaceTrade --no-browser --bind 0.0.0.0
```

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
