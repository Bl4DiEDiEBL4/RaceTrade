# RaceTrade 🏁

**Advanced IRC Race Management & CBFTP Integration**

> A powerful Windows Forms application for automated FTP racing with IRC announce monitoring, CBFTP integration, advanced rules engine, and chat functionality with FiSH encryption.

---

## 📋 Table of Contents

- [Features](#-features)
- [Changelog](#-changelog)
- [Quick Start](#-quick-start)
- [Core Concepts](#-core-concepts)
- [Configuration Guide](#-configuration-guide)
  - [Adding a New Site](#adding-a-new-site)
  - [Editing an Existing Site](#editing-an-existing-site)
- [Rules System](#-rules-system)
  - [Rule Format](#rule-format)
  - [Operators](#operators)
  - [Available Keys](#available-keys)
  - [Rule Priority](#rule-priority)
- [Tag Rules vs Section Rules](#-tag-rules-vs-section-rules)
- [Real-World Examples](#-real-world-examples)
- [Troubleshooting](#-troubleshooting)
- [Advanced Configurations](#-advanced-configurations)
- [Contributing](#-contributing)
- [License](#-license)

---

## ✨ Features

### 🎯 Core Racing
- **IRC Announce Monitoring** - Real-time monitoring of multiple IRC channels
- **CBFTP Integration** - Full spreadjob management and monitoring
- **ZNC/Bouncer Support** - Connect through ZNC with multi-network support
- **Auto Request Filler** - Automatically fill requests from other sites
- **Affiliate Racing** - Race releases with affiliated sites

### 💬 IRC Chat Interface
- **Tabbed Multi-Channel Chat** - Modern tabbed interface for multiple channels
- **FiSH Encryption** - CBC mode encryption/decryption with visual indicators
- **DH1080 Key Exchange** - Secure key exchange for private conversations
- **User List Tracking** - Real-time user monitoring (join/part/quit/nick)
- **Private Messaging** - Encrypted PM support with dedicated tabs
- **Manual Key Configuration** - Persistent channel key storage

### 🎬 Media Filtering
- **IMDb Integration** (FREE - no API key required!)
  - Filter by rating, votes, genres
  - Language and country filtering
  - Quick filters (Documentary, Music, Comedy, Shows)
  - Uses free imdbapi.dev API
- **TVMaze Integration**
  - Filter TV shows by rating, status, genres
  - Network filtering (Netflix, HBO, Amazon, etc.)
  - Show type filtering
  - Skip ended/cancelled shows

### 🔧 Advanced Rules Engine
- **Section Rules** - Global filters per IRC section
- **Tag Rules** - Per-mapping specific filters
- **Multiple Operators** - regex, wildcards, contains, exact match, etc.
- **Priority System** - Clear DROP → Tag → Section ALLOW hierarchy
- **Debug Mode** - Detailed rule evaluation logging

### 🎨 Modern UI
- **Dark Theme** - Easy on the eyes
- **Consolas Font** - Clean, monospaced typography
- **Color-Coded Logs** - 4 separate log windows (Race, IRC, CBFTP, Application)
- **Integrated Help** - Built-in documentation with search
- **Non-Blocking Dialogs** - Work while forms are open

### 🔐 Security
- **Encrypted Storage** - All passwords and Blowfish keys encrypted
- **FiSH Encryption** - Secure IRC messaging
- **DH1080 Protocol** - Secure key exchange
- **Configurable SSL** - Strict or self-signed certificate support

---

## 📝 Changelog

### Version 1.0.5b (Current - Beta)
**Released:** December 2025

#### 🆕 New Features
- ✅ **Auto Request Filler** - Automatically fill requests from configured sites
- ✅ **Affiliate Racing** - Race releases with affiliate sites
- ✅ **Site Rules Viewer** - View CBFTP site rules directly from Add/Edit Site form
- ✅ **CBFTP Site Management** - Add and edit CBFTP sites directly from GUI
- ✅ **imdbapi.dev Integration** - **MIGRATED FROM OMDB TO FREE imdbapi.dev API**
  - 🎉 **NO API KEY REQUIRED** - Completely free, unlimited access!
  - 💰 **NO COST** - OMDB required paid subscription, imdbapi.dev is 100% free
  - 📊 Better data quality with native array support for genres, languages, countries
  - 🚀 More reliable (no API key expiration or rate limit issues)
  - 🔄 Automatic rating precision fixes (Math.Round to 1 decimal)
  - ⚡ Better response structure with detailed metadata
  - 🔗 API Documentation: https://imdbapi.dev/
  - 📝 Uses Swagger API: `/titles/{id}` and `/search/titles`

#### 🐛 Bug Fixes & Exception Handling
- **Rating Display Fixes:**
  - Fixed floating-point precision issues (9.1 instead of 9.10000038146973)
  - Applied Math.Round(rating, 1) during API response parsing
  - Formatted test button displays with .ToString("F1")
- **Exception Handling Improvements:**
  - Fixed null reference exceptions in CBFTP spreadjob monitoring
  - Added null checks for CBFTP API responses
  - Better error handling for IRC disconnections and reconnections
  - Improved thread safety in chat client operations
  - Fixed race conditions in user tracking dictionaries
  - Added proper exception handling in async API calls
- **Connection Issues:**
  - Fixed chat-only mode IRC connection edge cases
  - Improved SSL certificate validation handling
  - Better handling of network timeouts
- **Memory & Performance:**
  - Fixed memory leaks in long-running IRC connections
  - Corrected release regex pattern edge cases
  - Improved disposal of HTTP clients

#### 🔧 Improvements
- Enhanced CBFTP spreadjob monitoring reliability with better API polling
- Better error messages for configuration and API issues
- Improved logging clarity across all log windows
- Performance optimizations for user tracking and list operations
- Enhanced rules engine logging in debug mode
- Removed API key validation since imdbapi.dev doesn't require keys
- Updated Settings form to remove obsolete OMDB API key field

---

### Version 1.0.4b (Beta)
**Released:** December 2025

#### 🆕 New Features
- Adding and editing existing CBFTP sites (direct from GUI)
- More bugfixes and improvements in racing core and rules engine

---

### Version 1.0.3b (Beta)
**Released:** November 2025

#### 🆕 New Features

**IRC Chat Interface:**
- Tabbed IRC chat window with multi-channel support
- FiSH encryption/decryption (CBC mode) with visual indicators
- DH1080 key exchange for secure PM conversations
- Manual channel key configuration with persistent storage
- User list tracking with join/part/quit/nick change monitoring
- Separate chat-only mode (connect without racing)
- Chat button for connecting to IRC without starting trader
- Real-time message encryption/decryption
- PM (Private Message) support with dedicated tabs
- Send encrypted messages to channels and users
- Automatic channel filtering (chat channels vs racing channels)

**Chat Keys Management:**
- New `chat_keys` dictionary in site configuration
- Support for many channels per site with individual keys
- Secure storage of Blowfish keys (encrypted in JSON)
- Runtime key updates without restart
- Separate key storage for racing vs chat channels

**Racing Core & Rules Engine:**
- Unified rules engine for ALL sites and sections
- Clear priority: Section DROP → Tag rules → Section ALLOW → default ALLOW
- Tag rules per CBFTP mapping in the UI (bottom-right panel)
- Label reminder in GUI: "CBFTP Mapping Rules: DROP run before ALLOW rules"
- Updated race logging to distinguish origin site (first announce) from destination sites
- CBFTP spreadjob monitoring with automatic COMPLETED / FAILED / TIMEOUT logging

#### 🔧 Improvements

**IRC Connection Enhancements:**
- Chat-only mode skips all release processing
- Proper channel filtering based on mode (racing vs chat)
- Better SSL certificate handling with configurable security
- Improved connection logging (chat vs monitoring messages)
- ZNC nickname normalization for multi-network support
- Performance optimization: user tracking only when window visible

**Security Improvements:**
- All passwords and Blowfish keys encrypted in configuration
- Configurable SSL validation (strict vs allow self-signed)
- Secure key exchange with DH1080 protocol
- Keys stored encrypted, decrypted only at runtime

**Racing & Logging:**
- Clearer race log lines:
  - `[DETECTED]` line includes origin site
  - `[RACING]`/`[COMPLETED]`/`[FAILED]` lines show section and destination site list
- Improved CBFTP job monitoring using standard spreadjobs API fields:
  - `status` (DONE / FAILED / TIMEOUT)
  - `sites` / `sites_incomplete`
  - `size_estimated_bytes`
  - `time_spent_seconds`
- Better separation between Application log, IRC log, CBFTP log, and Race log
- Race history stored in SQLite with full site list and section

**User Interface:**
- Chat button on main form
- Tabbed interface for multiple channels/PMs
- Color-coded messages (encrypted=blue, plaintext=white, system=gray)
- Real-time user list with status updates
- Message input with encryption indicator
- Context menu for key exchange and channel management

#### 🐛 Bug Fixes
- Chat-only mode no longer processes releases
- Channel list correctly filters to chat_keys when in chat mode
- Duplicate "Chat-only mode" log messages removed
- Release regex pattern corrected (no longer strips first character)
- Proper handling of read-only channel list
- SSL certificate validation now respects settings
- User tracking cross-thread issues resolved
- Nick changes now update all dictionaries (FiSH keys, PM keys, exchanges)
- PM tabs correctly handle encryption with "PM:username" format
- Channel tabs created before connection for better UX
- Race log "origin site" now only shown on `[DETECTED]` lines
- CBFTP job monitoring no longer relies on patched `/transfers` endpoint
- Spreadjob completion status correctly reflects DONE / FAILED / TIMEOUT
- Some rules could accidentally drop everything in a section without clear logging – fixed

#### ⚠️ Known Issues
- User list may not populate immediately on join (IRC server dependent)
- Some IRC servers may require custom certificate validation

---

### Version 1.0.2b (Beta)
**Released:** November 2025

#### 🆕 New Features

**Tag Rules GUI:**
- Per-mapping rule configuration in UI
- Each CBFTP mapping now has its own rules panel
- Separate from Section Rules (per IRC section)
- Add/Edit/Remove tag-specific rules directly in GUI
- Visual distinction between Section Rules and Tag Rules

**IMDb Integration:**
- Filter movies by rating, votes, genres
- Language and country filtering
- Quick filters: No Documentary, No Music, No Comedy, No Shows
- Fallback option for API errors
- Test connection button
- Per-section configuration

**TVMaze Integration:**
- Filter TV shows by rating, status, genres
- Network filtering (Netflix, HBO, Amazon, etc.)
- Show type filtering (Scripted, Reality, Documentary, etc.)
- Skip ended/cancelled shows option
- Configurable cache duration
- Per-section configuration

**GUI Rebuild:**
- Dark theme
- Consolas font throughout
- Color-coded buttons (Green=Save, Red=Delete, Grey=Action)
- Improved layout and spacing
- Fixed form borders (non-resizable)
- Consistent styling across all forms

**Help System:**
- Integrated help window with complete guide
- Non-blocking modeless window
- Search with keyword highlighting
- Always-on-top for easy reference

**Enhanced Settings:**
- Centralized settings dialog
- Custom application name for taskbar
- Debug mode toggle moved to Settings
- Settings persist across restarts

#### 🔧 Improvements
- Enhanced section management
- Clearer distinction between IRC and CBFTP sections
- Better visual feedback for mappings
- Improved tag rule visibility
- Section removal now properly cleans up all lists
- Better documentation
- Settings consolidation

#### 🐛 Bug Fixes
- Removing IRC section now properly updates all ListBoxes
- Deleting section removes it from race sections list
- Section removal now cleans up JSON correctly
- Tag rules now save correctly per mapping
- Form dialogs no longer close parent forms
- GroupBox text colors now display correctly

---

### Version 1.0.1b (Initial Beta)
**Released:** October 2024

#### 🆕 Initial Features

**Core Racing:**
- IRC announce monitoring
- CBFTP integration
- Section mapping system

**Basic Rules Engine:**
- Section rules (per IRC section)
- Operators: `==`, `!=`, `iswm`, `matches`, `contains`, etc.
- DROP and ALLOW actions
- Rule priority system

**Site Management:**
- Add/Edit/Delete sites
- ZNC/IRC configuration
- Channel and Blowfish key support
- Multiple channel support
- Encrypted password storage

**CBFTP Sync:**
- Import sites from CBFTP
- Automatic section detection
- Bulk import

**Basic GUI:**
- Site dropdown
- Section configuration
- Mapping interface
- Rule editor (text-based)

**Configuration:**
- JSON-based site configs
- SQLite database for race history

**Logging System:**
- Color-coded IRC output
- Debug mode
- Race history tracking

---

## 🚀 Quick Start

### Prerequisites
- Windows OS (7/8/10/11)
- .NET Framework 4.7.2 or higher
- CBFTP server configured and running
- ZNC/IRC bouncer (recommended)

### Initial Setup (First Time Only)

#### Step 1: Add Your CBFTP Server
1. Click **"CBFTP Server"** button in main window
2. Fill in:
   - **Host:** Your CBFTP server IP/hostname
   - **Port:** Usually 55477
   - **Password:** Your CBFTP API password
   - **Profile:** e.g., "RACE"
3. Click **"Save"**

#### Step 2: Import Sites from CBFTP
1. Click **"Sync From CBFTP"**
2. Select your CBFTP server from the dropdown
3. Click **"Fetch Sites"**
4. Tick the sites you want to import
5. Click **"Import Selected Sites"**
6. Sites are now created with names and categories
   > ℹ️ Only categories and sitename are imported; you still configure IRC yourself

### Basic Configuration (After Import)

Now configure a site for racing:

1. **Select a site** from the site dropdown (top of main window)
2. Click **"Edit Site"** (or double-click the site)
3. In the Site Editor:
   - Make sure the IRC section you want to race is present in the top-left **"IRC Sections"** list
   - In the bottom-left **"Race Sections (irc)"** list, make sure that section is **Enabled** using the Enable/Disable buttons
   - In the top-left **"IRC Sections"** list, select that section (e.g., "TV-1080P")
   - In the top-middle **"Cbftp Sections"** list, select the matching CBFTP section (e.g., "TV-1080P" or "MOVIES-HD")
   - Click **"Map"**
   - In **"Mapping trigger (regex)"** enter: `.*` (match everything)
   - Leave Section Rules and Tag Rules empty for now
   - Click **"Save"** in the site editor

4. **Start the trader**. You should see:
   - `[DETECTED]` in the Race log when an announce hits
   - `[RACING]` when the spreadjob is started
   - `[COMPLETED]`/`[FAILED]`/`[TIMEOUT]` when CBFTP finishes

### Alternative: Manual Site Setup (Without CBFTP Import)

1. Click **"Add Site"**
2. In the Site Editor:
   - **General:**
     - Sitename: must match CBFTP sitename if you want auto section import
     - Announce: choose announce bot/mode from dropdown
   - **ZNC/IRC:**
     - Host, Port
     - Username / network
     - Password (stored encrypted)
   - **Channels:**
     - Add announce channels in "Channels" list
     - Add Blowfish keys where needed
3. In **"Race Sections and Mapping":**
   - Add IRC sections (these usually match the site's paths, e.g., TV-DE, MP3)
   - In bottom-left "Race Sections (irc)", Enable those sections
   - Map each IRC section to one or more CBFTP sections
   - Set "Mapping trigger (regex)" for each mapping (tag)
4. Click **"Save"**

### TL;DR

```
Add CBFTP server → Sync sites → Edit each site → Enable IRC sections 
in "Race Sections (irc)" → Map IRC → CBFTP → Done
```

### Quick Explanation

- **IRC Section** = What the site announces in IRC (e.g., `[TV-DE]`, `[X264-HD]`)
- **CBFTP Section** = Logical section name in CBFTP (e.g., TV-DE, MOVIES-HD)
- **Mapping (Tag)** = IRC section + CBFTP section + trigger regex (+ tag rules)
- **Section Rules** = Filters that apply to **all** mappings for that IRC section
- **Tag Rules** = Filters for **one** mapping only (per CBFTP section)

---

## 🧠 Core Concepts

### How It Works - The Flow

#### Step 1: IRC ANNOUNCE RECEIVED
```
Example: "[TV-DE] Show.Name.S01E01.1080p.WEB.H264-GROUP"
```

#### Step 2: EXTRACT SECTION NAME
```
Section: "TV-DE" (from the announce line)
```

#### Step 3: CHECK IF SECTION IS ENABLED
```
Is "TV-DE" Enabled in "Race Sections (irc)" for this site?
  - If NO → ignore
  - If YES → continue
```

#### Step 4: FIND MATCHING MAPPING
```
In "Race Sections and Mapping", find a mapping where:
  - IRC Section is "TV-DE"
  - The mapping's "Mapping trigger (regex)" matches the release name

The FIRST mapping whose trigger matches will be used
```

#### Step 5: EVALUATE RULES
```
1. Section rules for the IRC section (TV-DE):
   - All DROP rules evaluated first
   - Then Section ALLOW rules

2. Tag rules for this mapping (IRC=TV-DE → CBFTP=TV-DE or MOVIES-HD etc):
   - If a Tag rule matches, its ACTION (ALLOW/DROP) is used

3. If nothing matches: default action is ALLOW
```

#### Step 6: SEND TO CBFTP (SPREADJOB)
```
Racetrade sends:
  section  = CBFTP section name
  name     = release name
  sites    = list of allowed sites
  profile  = CBFTP profile (e.g., RACE)

CBFTP log:
  [JOB SENT]
  [JOB STARTED] Job#1234: Release → CBFTP-Server

Race log:
  [RACING] line:
  [2025-.. ..:..:..] :: [TV-DE] :: [SiteAlpha,SiteBeta] :: [RACING] :: Release.Name...
```

#### Step 7: MONITOR CBFTP JOB
```
Racetrade periodically polls CBFTP `spreadjobs/{release}`
Reads:
  status (DONE / FAILED / TIMEOUT / ACTIVE / etc)
  sites, sites_incomplete
  size_estimated_bytes
  time_spent_seconds

When terminal:
  DONE     → [COMPLETED]
  FAILED   → [FAILED]
  TIMEOUT  → [FAILED] with timeout reason

Race log examples:
  [COMPLETED] :: [TV-DE] :: [SiteAlpha,SiteBeta] :: Release
  [FAILED]    :: [TV-DE] :: [SiteAlpha,SiteBeta] :: Release (CBFTP transfer failed)
```

### Log Windows

| Log Window | Purpose |
|------------|---------|
| **Race Log** | High-level race info per release: `[DETECTED]`, `[RACING]`, `[COMPLETED]`, `[FAILED]` |
| **CBFTP Log** | Detailed job messages and Job# progress |
| **Application Log** | App-level info, errors, rules engine messages |
| **IRC Log** | Raw IRC events: connect, join, announces, FiSH status, errors |

### IRC Sections vs CBFTP Sections

#### IRC Sections
- Names emitted by the site in IRC
- Often equal to the path name on that site
- **Example on SiteAlpha:** X264-HD-1080P, TV-DE, MP3
- **Example on SiteBeta:** X264-HD, TV, MUSiC

#### CBFTP Sections
- Logical names defined in CBFTP's section config
- All sites racing together share these names
- CBFTP handles per-site paths internally for that section
- **Example:** MOVIES-HD exists for SiteAlpha and SiteBeta with different paths

#### Mapping
For each site, you map its IRC sections to shared CBFTP sections:
```
SiteAlpha: IRC X264-HD-1080P → CBFTP MOVIES-HD
SiteBeta:  IRC X264-HD       → CBFTP MOVIES-HD
```

Trigger regex & rules decide which releases go into which mapping.

---

## ⚙️ Configuration Guide

### The UI Components

#### TOP LEFT - ZNC SERVER
- Host, Port, Username, Password
- Connection details for ZNC/IRC server

#### TOP MIDDLE - Channels
- **ListBoxChannels:** channels for this site
- "Add Channel" / "Remove Channel" buttons
- Blowfish keys entered elsewhere in the editor (if supported)

#### MIDDLE TOP - Race Sections and Mapping

**LEFT - IRC Sections (listBox1):**
- List of IRC section names for this site
- Add / Remove buttons to manage sections
- These names must match the section names in the announces

**MIDDLE - Cbftp Sections (listCbftpSections):**
- List of CBFTP sections available for racing
- Typically imported from CBFTP or added manually

**RIGHT - Mapped Section (listBox5):**
- Shows mappings between IRC sections and CBFTP sections
- When you select an IRC section and a CBFTP section and click "Map", the mapping appears here
- The "Mapping trigger (regex)" textbox below is per-mapping (per tag)
- Click "Save" next to the trigger to store it for the selected mapping

**Buttons:**
- **Add/Remove** (under IRC Sections): add or delete IRC section names
- **Map**: link selected IRC section → CBFTP section
- **Unmap**: remove a mapping from "Mapped Section"
- **Save** (next to trigger): save trigger regex for the selected mapping

#### BOTTOM MIDDLE - Race Sections (irc)

**ListBox2 + Enable / Disable buttons:**
- This list shows the IRC sections for this site
- Use **Enable** to activate a section for racing
- Use **Disable** to turn it off temporarily
- Only **Enabled** sections are processed by the racer

#### BOTTOM RIGHT - Race Sites

**listBox_race_sites:**
- Shows the sites this site will race against (from CBFTP config)
- Enable/Disable buttons to include/exclude them if the UI supports it

#### BOTTOM RIGHT AREA - Rules

**Top-right box: "CBFTP Mapping Rules: DROP run before ALLOW rules"**  
(Tag Rules listBox)
- These rules belong to the **selected mapping** in "Mapped Section"
- They apply only to this single combination of IRC section + CBFTP section
- Use them for mapping-specific filters (e.g., MOVIES-HD on one site)
- Label reminder: "DROP run before ALLOW rules" – within this list, DROP rules are evaluated before ALLOW rules

**Bottom-right box: "Section Rules: DROP takes priority over ALLOW"**  
(listBox6)
- These rules apply to **all mappings** in the selected IRC section
- Perfect for global blocks:
  ```
  [release] contains INTERNAL DROP
  [group]   isin BadGroup1,BadGroup2 DROP
  ```
- First, all Section DROP rules run; then Section ALLOW rules

#### BOTTOM LEFT GROUPS

**New Regex:**
- Bot Name, NEW Regex, Ignore Words, Section Regex, Section Prefix/Suffix, Release Regex, etc.
- Used to parse announces and extract section, release, group, etc.
- Test button lets you test against a sample announce string

**Options:**
- Disable Site
- Download Only Site
- Finish Incompletes

**Requests:**
- Use for Requests
- Request Path: path used for request-filling if enabled

**Bottom buttons:**
- **Save:** save entire site configuration
- **Delete:** delete this site
- **Exit:** close Site Editor
- **Help:** open this guide

---

### Adding a New Site

1. In the main window, click **"Add Site"**

2. **Site Settings:**
   - **ZNC server:**
     - Host / Port / Username / Password (for your bouncer or IRC)
   - **General:**
     - Sitename: must match CBFTP sitename if you want to sync sections
     - Announce: choose the announce bot/mode from dropdown

3. **Channels:**
   - Add the announce channels for this site in "ListboxChannels"
   - Configure Blowfish keys where needed (chat and/or racing channels)

4. **New Regex:**
   - Configure how releases are extracted from announces
   - Usually you already have sane defaults from imports; change only if necessary

5. **Race Sections and Mapping:**
   - Add IRC sections in the "IRC Sections" list (often same as path names)
   - Use bottom-left "Race Sections (irc)" list to Enable sections you want
   - In the top area:
     - Select an IRC section
     - Select a CBFTP section
     - Click "Map"
     - Set "Mapping trigger (regex)" – start with `.*` for "everything"
     - Click "Save" (next to trigger)
   - Add Section Rules (bottom-right) for IRC section-wide filters
   - Add Tag Rules in "CBFTP Mapping Rules" box for mapping-specific filters

6. Click **"Save"** at bottom

7. **Start trader** and verify logs:
   - IRC connects
   - Announces appear in IRC log
   - Race log shows `[DETECTED]` / `[RACING]` / `[COMPLETED]`

---

### Editing an Existing Site

1. Select the site from the main window dropdown
2. Click **"Edit Site"**

Inside the editor you can:
- Change ZNC/IRC host, port, user, pass
- Add/remove channels and adjust keys
- Add/remove IRC sections
- Enable/Disable sections in "Race Sections (irc)"
- Create/remove mappings between IRC and CBFTP sections
- Adjust "Mapping trigger (regex)" per mapping
- Add/modify Section Rules and Tag Rules
- Toggle options like Disable Site, Download Only Site, Finish Incompletes

**Always click "Save" after changes.**

> 💡 **Tip:** When modifying rules heavily, enable Debug Mode in Settings so rules engine messages show up in the Application log.

---

## 📜 Rules System

### Rule Format

```
[key] operator value ACTION
```

- **[key]:** Field to check (release, section, group, year, etc.)
- **operator:** Comparison type
- **value:** Right-hand side of comparison
- **ACTION:** ALLOW or DROP (EXCEPT reserved / advanced)

**Examples:**
```
[release] contains INTERNAL DROP
[group]   isin BadGroup1,BadGroup2 DROP
[release] contains 1080p ALLOW
```

---

### Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `==` | Exact match (case-insensitive) | `[section] == TV-DE` |
| `!=` | Not equal | `[quality] != 720p` |
| `iswm` | Wildcard match (* and ?) | `[release] iswm *1080p*` |
| `matches` | Regex pattern match | `[release] matches (?i)\bGERMAN\b` |
| `contains` | Substring exists | `[release] contains INTERNAL` |
| `startswith` | Begins with substring | `[release] startswith Movie` |
| `endswith` | Ends with substring | `[release] endswith 2024` |
| `isin` | Left side is contained in comma/pipe-separated list | `[group] isin GROUP1,GROUP2` |

---

### Available Keys

| Key | Description | Example Value |
|-----|-------------|---------------|
| `[release]` | Full release name | `Movie.2024.1080p.WEB.H264-GROUP` |
| `[section]` | IRC section name | `TV-DE`, `X264-HD` |
| `[group]` | Release group name | `GROUP` |
| `[year]` | Movie/show year (if extracted) | `2024` |
| `[quality]` | Quality tag (if extracted) | `1080p`, `2160p` |
| `[source]` | Source tag (if extracted) | `WEB`, `BluRay` |

---

### Wildcard Patterns (ISWM)

| Pattern | Matches |
|---------|---------|
| `*` | Any characters |
| `?` | Single character |

**Examples:**
```
*1080p*           - contains "1080p"
*WEB*             - contains "WEB"
*.GERMAN.*        - ".GERMAN." somewhere
*S??E??*          - S01E01, S10E23, etc.
```

---

### Regex Patterns (MATCHES)

| Pattern | Description |
|---------|-------------|
| `.` | Any character |
| `.*` | Anything |
| `.+` | One or more |
| `[0-9]` | Any digit |
| `(a\|b)` | a OR b |
| `^` | Start of string |
| `$` | End of string |
| `\b` | Word boundary |
| `\.` | Literal dot |

**Examples:**
```
(?i).*1080p.*           - contains 1080p (case-insensitive)
.*S[0-9]{2}E[0-9]{2}.*  - season/episode S01E01
^Movie.*2024$           - starts with Movie and ends with 2024
```

---

### Precise Language Matches Using \b

To drop a language without dropping similar words, use regex with `\b`:

```
[release] matches (?i)\bGERMAN\b DROP
[release] matches (?i)\bFRENCH\b DROP
[release] matches (?i)\biTALiAN\b DROP
[release] matches (?i)\bPOLISH\b DROP
```

**Examples:**
```
The.Carman.Family.Deaths.2025.POLISH.1080p.WEB.H264-GROUP
  → Dropped by POLISH rule

The.Hand.That.Rocks.the.Cradle.2025.FRENCH.720p.WEB.H264-GROUP
  → Dropped by FRENCH rule
```

**Alternative (less precise):**
```
[release] iswm *.GERMAN.* DROP
```

---

### Rule Priority

> ⚠️ **VERY IMPORTANT** - The rules engine evaluates in this order:

```
1) Section DROP rules (for this IRC section)
   └─ If any matches → release is DROPPED immediately

2) Tag rules (for the selected mapping)
   └─ Checked only if the mapping's trigger matched
   └─ If a Tag rule matches, its ACTION (ALLOW/DROP) wins for that mapping

3) Section ALLOW rules (for this IRC section)
   └─ Checked only if no Section DROP and no Tag rule matched

4) Default
   └─ If no rules match anywhere → ALLOW
```

**Practically:**
- Section rules are global for that IRC section
- Tag rules let you fine-tune per CBFTP mapping
- DROP always wins when it matches

---

## 🏷️ Tag Rules vs Section Rules

### Section Rules (per IRC section)

**Location:** Bottom-right "Section Rules" box

**Scope:** Apply to **ALL** mappings under that IRC section

**Evaluation:** First: all DROP rules, then ALLOW rules

**Great for global blocks:**
```
[release] contains INTERNAL DROP
[group]   isin BadGroup1,BadGroup2 DROP
[release] matches (?i)\bPOLISH\b DROP
```

---

### Tag Rules (per mapping / per CBFTP section)

**Location:** Top-right bottom box: "CBFTP Mapping Rules: DROP run before ALLOW rules"

**Scope:** Apply **ONLY** to the selected mapping in "Mapped Section"

**Evaluation:** After Section DROP rules

**Good for:**
- Quality filters per mapping
- Group whitelists per mapping
- Special rules for MOVIES-HD vs MOVIES-LQ etc.

---

### Hierarchy Example

```
Site
└── IRC Section: MOVIES-HD
    ├── Section Rules (affect all mappings in MOVIES-HD)
    ├── Mapping A: MOVIES-HD → X264-HD-1080P
    │   └── Tag Rules for X264-HD-1080P only
    └── Mapping B: MOVIES-HD → X264-HD-720P
        └── Tag Rules for X264-HD-720P only
```

**Remember:**
- **Section Rules** = wide net (per IRC section)
- **Tag Rules** = narrow net (per mapping)

---

## 💡 Real-World Examples

### Example: Shared CBFTP Section with Different Per-Site Filters

**Goal:**
- Use one shared CBFTP section: **MOVIES-HD**
- **SiteAlpha** must be strict: only 1080p
- **SiteBeta** can send both 720p and 1080p

**CBFTP:**
```
Section: MOVIES-HD
  - CBFTP is configured so MOVIES-HD exists for both SiteAlpha and SiteBeta
    with their own paths
```

---

#### SiteAlpha Configuration

**IRC Sections:**
```
X264-HD-1080P    (path on SiteAlpha is also X264-HD-1080P)
```

**Race Sections (irc):**
```
X264-HD-1080P is Enabled
```

**Mapping:**
```
- Select IRC Section: X264-HD-1080P
- Select CBFTP Section: MOVIES-HD
- Click "Map" → shows in "Mapped Section"
- Mapping trigger (regex): .*
```

**Section Rules (for IRC section X264-HD-1080P):**
```
[release] contains 720p DROP
[release] contains CAM DROP
[release] contains HDCAM DROP
```

**Tag Rules (for MOVIES-HD mapping on SiteAlpha):**
```
(optional; you can leave empty, the Section Rules already block 720p)
```

**Result (SiteAlpha):**
```
Movie.Name.2025.1080p.WEB.H264-GROUP  → ALLOWED → MOVIES-HD
Movie.Name.2025.720p.WEB.H264-GROUP   → DROPPED (contains 720p)
```

---

#### SiteBeta Configuration

**IRC Sections:**
```
X264-HD      (path on SiteBeta is X264-HD)
```

**Race Sections (irc):**
```
X264-HD is Enabled
```

**Mapping:**
```
- Select IRC Section: X264-HD
- Select CBFTP Section: MOVIES-HD
- Click "Map"
- Mapping trigger (regex): .*
```

**Section Rules (for IRC section X264-HD):**
```
[release] contains CAM DROP
[release] contains HDCAM DROP
```

**Tag Rules (for MOVIES-HD mapping on SiteBeta):**
```
(none - SiteBeta allows both 720p and 1080p)
```

**Result (SiteBeta):**
```
Movie.Name.2025.1080p.WEB.H264-GROUP  → ALLOWED → MOVIES-HD
Movie.Name.2025.720p.WEB.H264-GROUP   → ALLOWED → MOVIES-HD
```

---

### Summary

- Both sites race into the same CBFTP section **MOVIES-HD**
- On **SiteAlpha**, the IRC section X264-HD-1080P has Section Rules that drop 720p
- On **SiteBeta**, the IRC section X264-HD does not drop 720p
- CBFTP paths are per site inside the MOVIES-HD section
- RaceTrade only cares about which CBFTP section name to use per site

---

## 🔧 Troubleshooting

### Problem: Nothing is being raced

**Checklist:**
- ✅ Is the IRC section present in "IRC Sections"?
- ✅ Is that section **Enabled** in "Race Sections (irc)"?
- ✅ Is there at least one mapping in "Mapped Section" for that IRC section?
- ✅ Does the mapping's trigger regex match? (try `.*`)
- ✅ Are Section DROP rules blocking everything?
- ✅ Did you click all the "Save" buttons (trigger save + site save)?

**Fix:**
- Temporarily remove all rules
- Use `.*` as trigger
- Enable Debug Mode and watch Application + Race logs

---

### Problem: Releases going to wrong CBFTP section

- Check the order of mappings (first trigger that matches wins)
- Make specific triggers first, catch-all mapping with `.*` last
- Check logs for which mapping was selected

---

### Problem: All releases are dropped

- Look at Section Rules box:
  ```
  [release] matches .* DROP
  ```
  will drop everything for that IRC section
- Remove or narrow overly broad DROP rules
- Use debug mode: it will show:
  ```
  [DROP] Global DROP rule matched: ...
  ```

---

### Problem: Hard to see what matched

- Enable Debug Mode
- Application log will show:
  ```
  [✓] Rule matched: [release] contains INTERNAL, Input='...'
  ```
- Race log shows when a release is filtered (FILTERED / DROPPED) vs raced

---

## 🎓 Advanced Configurations

### Multiple Sites Into Same CBFTP Section
- Configure that CBFTP section for all sites inside CBFTP
- In each site's editor:
  ```
  IRC Section: TV-DE or X264-FOREIGN
  CBFTP Section: TV-DE
  ```
- Use Section Rules per site to express per-site preferences

### Using PreBot vs Direct Site Announces
- PreBot configuration allows racing based on pre announcements
- Configure in site settings under "Announce" dropdown
- Select appropriate PreBot from dropdown

### Group Whitelisting via Tag Rules
```
Tag Rule for specific mapping:
[group] isin APPROVED1,APPROVED2,APPROVED3 ALLOW
[group] != * DROP    (drop everything else)
```

### Language-Based Filtering
```
Use regex word boundaries for precision:
[release] matches (?i)\bGERMAN\b DROP
[release] matches (?i)\bFRENCH\b DROP
```

### Quality-Based Routing
Split 720p / 1080p / 2160p into different CBFTP sections:
```
Mapping 1: IRC Section → MOVIES-HD-1080P
  Trigger: .*1080p.*
  
Mapping 2: IRC Section → MOVIES-HD-720P
  Trigger: .*720p.*
  
Mapping 3: IRC Section → MOVIES-UHD
  Trigger: .*2160p.*
```

### Performance Tips
- ⚡ Put global DROP rules in Section Rules
- ⚡ Keep triggers simple where possible
- ⚡ Place specific mappings (1080p, 2160p) before catch-all mappings


## 📄 License

This project is licensed under the WTF License

---

## 🆘 Need Help?

Enable **Debug Mode** and check:

| Log Window | What to Check |
|------------|---------------|
| **Application log** | Rules & config messages |
| **IRC log** | Announces and connection status |
| **CBFTP log** | Spreadjob status |
| **Race log** | `[DETECTED]`, `[RACING]`, `[COMPLETED]`, `[FAILED]` |

### Common Issues

In almost all cases, the problem is:
- ❌ Section not Enabled in "Race Sections (irc)"
- ❌ Missing mapping (you forgot to click "Map")
- ❌ Trigger regex not matching (too strict)
- ❌ Section DROP rule too broad
- ❌ Confusing Section Rules vs Tag Rules
- ❌ Forgot to click "Save"

---

## 🙏 Acknowledgments

- CBFTP team for the excellent racing platform
- Beta testers

---

<div align="center">

**Happy Racing! 🏁**

*Version 1.0.5b - December 2025*

</div>
