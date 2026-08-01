using System;
using System.Drawing;
using System.Windows.Forms;
using AntButton = AntdUI.Button;
using AntInput = AntdUI.Input;
using AntPanel = AntdUI.Panel;

namespace RaceTrader
{
    public class HelpForm : AntdUI.Window
    {
        private RichTextBox helpTextBox;
        private AntPanel topPanel;
        private Label titleLabel;
        private AntButton closeButton;
        private AntInput searchBox;
        private AntButton searchButton;
        private readonly string windowTitle;
        private readonly string headingText;
        private readonly string contentText;

        private const string DOCUMENTATION_CONTENT = @"
================================================================================
                    RACETRADE COMPLETE GUIDE
              Rules, Mapping, and Configuration Manual
================================================================================

TABLE OF CONTENTS
-----------------
1. Changelog
2. Quick Start - Get Racing in 5 Minutes
3. Core Concepts - Understanding the System
4. Configuration Walkthrough
   4.1 Adding a New Site
   4.2 Editing an Existing Site
5. Rules System - Complete Reference
6. Tag Rules vs Section Rules - CRITICAL DISTINCTION
7. Real-World Examples
8. Troubleshooting
9. Advanced Configurations



================================================================================
1. CHANGELOG
================================================================================
Version 1.0.9b (Current - Beta)
--------------------------------
Released: August 1, 2026

PERFORMANCE & RACE-PATH FIXES:

- CBFTP race command speedups
  - Reused CBFTP HTTP clients per endpoint/password so spreadjob commands keep connections warm
  - Reduced spreadjob request timeout to fail faster when a CBFTP endpoint is dead
  - Disabled Expect100Continue and Nagle buffering at startup for lower POST latency
  - Raised the default connection limit so concurrent races are not serialized behind two HTTP connections

- IRC announce processing speedups
  - Cached announce regexes instead of rebuilding them for every IRC line
  - Reused cached linked site configs for Global PreBot announce processing
  - Avoided re-serializing site config JSON on the hot announce path
  - Moved first-PreBot diagnostic logging off the critical race path

- Filtering and cache fixes
  - Cached selected movie provider settings until settings.json changes
  - Reused one pretime database lookup across all site threshold checks for a release
  - Trimmed log buffers in chunks to reduce lock time during heavy announce bursts
  - Invalidated site config cache after CBFTP site sync imports
  - Reloaded racer site snapshots after successful CBFTP imports so imported sites are usable immediately
  - Updated app version text under the logo to v1.0.9b


Version 1.0.8b (Beta)
--------------------------------
Released: July 9, 2026
Updated: July 30, 2026

BUG FIXES & THEMING:

- Dashboard cleanup
  - Moved Exit button away from Tools and placed it bottom-right
  - Replaced useless ""Secure Connection"" footer with ""linknet / #racetrade""
  - Added GitHub link action to the footer text
  - Fixed Race Log dashboard button so it shows ""Race (Off)"" / ""Race Log""
  - Updated app version text under the logo to v1.0.8b

- Dark theme button polish
  - Added translucent dark-theme colors for important actions
  - Save/Add/Import/Start actions use green success styling
  - Edit/Test/Refresh/Send actions use blue primary styling
  - Delete/Remove/Close/Cancel/Clear actions use red danger styling
  - Removed rounded button corners for a sharper RaceTrade look
  - Applied consistent button styling across forms and dialogs

- FXP browser fixes
  - Fixed incorrect path appending that could browse completely wrong directories
  - Added more spacing around Up and Refresh buttons
  - Fixed release packaging so only the required runtime files are included

- ChatBox fixes
  - Lightened pure black text colors so bot/status text remains readable
  - Kept the dark panel look while improving contrast
  - Fixed Send button height so it matches the input field
  - Fixed Blowfish key dialog to prefill existing keys
  - Improved Blowfish dialog OK/Cancel button visibility

- ZNC multi-network fixes
  - Added a dedicated Network field to Edit/Add Site for ZNC network selection
  - Added ZNC placeholder hints for Host, Port, Username, and Network fields
  - Saved the explicit server.network value and kept fallback support for legacy user/network usernames
  - Fixed ChatBox ZNC auth so configured channels join the selected ZNC network instead of the default network
  - Fixed race IRC ZNC auth to use account/network:password when a network is configured
  - Kept NICK/USER as the bare account name so user tracking and FiSH nick matching stay clean
  - Made ChatBox wait for IRC/ZNC registration before sending JOIN commands
  - Skipped ChatBox JOINs when ZNC rejects or cannot confirm the requested network
  - Prevented channels from other ZNC networks from being joined under LINKNET/default network

- Help and manager form fixes
  - Restored readable formatting in the Help window
  - Fixed Affil Spread & Pre Manager button spacing
  - Fixed centered text on Pre Manager action buttons
  - Fixed Clear Log button clipping in the Pre Manager
  - Fixed Add Affil text contrast in the Site Editor
  - Added working Release Skiplist editor for Site Editor sections
  - Added inline Release Skiplist explanation and wildcard examples
  - Applied section skiplists to release-name wildcard filtering before racing

- Site editor settings persistence
  - Fixed TVMaze/IMDB section settings being lost after saving the Site Editor
  - Preserved section-level imdb/tvmaze config in the site model
  - Aligned TVMaze/IMDB settings file path with the Site Editor sites folder
  - Preserved and displayed the ZNC network setting when editing existing sites

- Save/persistence audit fixes
  - Added atomic config writes with temp-file replacement and .bak backup support
  - Converted config saves to AtomicFile writes to avoid truncated/corrupt JSON files
  - Fixed AddSite save wiping Blowfish keys when DPAPI decrypt failed
  - Preserved original encrypted Blowfish ciphertext unless a new key is entered
  - Fixed AddSite save losing release_prefix and release_suffix values
  - Fixed preBot editor save rebuilding SiteSettings and wiping hidden fields
  - Fixed preBot password and Blowfish key load/save encryption handling
  - Fixed SettingsForm save wiping unrelated settings like docked log layout

- Race filtering and concurrency fixes
  - Fixed RaceHelper iterating site configs while reloads could modify the list
  - Fixed RequestAutoFillManager polling loop crashes from concurrent site list updates
  - Added synchronized TVMaze and IMDB rate limiting to avoid concurrent API bursts
  - Added regex timeouts for trigger, blacklist, and wildcard rule checks
  - Fixed IMDb title search so stale not-found cache entries no longer block new lookups
  - Added IMDb search query variants and support for alternate API response shapes
  - Changed IMDb API test buttons to verify live title-search instead of cached fixed-ID lookup
  - Added selectable movie lookup provider: Tiffara or TMDb
  - Added encrypted TMDb API key setting for TMDb lookup mode/fallback
  - Added TMDb fallback lookup that finds the movie and IMDb ID when Tiffara is unavailable
  - Kept IMDb rating strict: TMDb ratings are never used as IMDb ratings
  - Made API tests report TMDb fallback/no IMDb rating instead of passing on cached data
  - Enforced IMDb quick filters for No Documentary, No Music, No Comedy, and Movies Only
  - Fixed TVMaze filters to block when rating or show type is missing but that filter is configured
  - Updated Test Release validation to show IMDb search title/year and TVMaze show type results
  - Fixed Test Release so global skiplist/blacklist matches stop validation and cannot end as ""ALL CHECKS PASSED""

- IRC / FiSH fixes
  - Chat IRC now joins configured channels Chan1 through Chan20
  - Normalized channel names when loading Blowfish keys
  - Unified FiSH and PM key dictionary locking to prevent runtime dictionary races
  - Made FiSH key dictionaries case-insensitive for IRC nick/channel matching
  - Fixed DH1080 key decode to choose the valid Base64 variant by key length
  - Kept ZNC-compatible PASS/NICK/USER ordering while adding explicit network selection

- Rules and matching fixes
  - Fixed trigger regex parsing so patterns ending in i are not mangled
  - Fixed isin rules to use exact list membership instead of substring matching
  - Fixed rules whose value is literally ALLOW, DROP, or EXCEPT
  - Fixed MULTI detection to require a delimited release tag

- Log and small form fixes
  - Added Exit button to the CBFTP Sections form
  - Matched log window width to the main window while preserving height auto-resize
  - Aligned docked log windows with the main frame and added an 8px dock gap

- Bugfix audit updates
  - Made atomic file saves use unique temp files so concurrent saves do not collide
  - Added DPAPI entropy for newly encrypted secrets while keeping legacy decrypt fallback
  - Reloaded CBFTP racer config immediately after CBFTP server edits
  - Fixed request auto-fill startup so it receives the populated site list
  - Added request auto-fill in-flight tracking to prevent duplicate fill jobs
  - Fixed CBFTP transfer handling for ABORTED jobs and undocumented 2xx spreadjob responses
  - Stored plain release names in race history so duplicate detection works again
  - Fixed Pre Spread FXP to post jobs to the source CBFTP server with explicit credentials
  - Made Pre Spread completion checks fail closed when source listings cannot be verified
  - Fixed IRC PASS/NICK/USER ordering for ZNC and kept bot processing from blocking PINGs
  - Reloaded FiSH keys on reconnect and tightened chat user-list locking
  - Fixed DH1080 CBC key parsing and rejected degenerate DH public keys
  - Fixed IMDB search hits by fetching full title records before caching/filtering
  - Fixed TVMaze episode cache writes to stay inside the active transaction
  - Fixed FXP browser UTF-8 basic auth, symlink delete safety, and image viewer disposal
  - Made LogManager singleton initialization thread-safe and preserved errors in the core log
  - Added Test Release guards against double Enter runs and disposed dialog updates


Version 1.0.7b (Beta)
----------------------
Released: January 2026

NEW FEATURES:

FXP Client Browser

- Dual-pane FTP browser with side-by-side site navigation
- Direct CBFTP integration using proper REST API endpoints
- FXP transfers between sites with automatic status monitoring
- Smart auto-refresh after operations (deletes and transfers)
- Recursive directory deletion with proper CBFTP API usage
- Symlink support with visual indicators and navigation
- Folder size calculation showing total directory size
- Same-directory transfers (left-to-left or right-to-right)
- Tabbed logging console with Console and Transfer Log tabs
- Date/size display for files and directories
- Context menus for quick operations (Delete, FXP, Refresh)


Version 1.0.6b (Beta)
--------------------------------
Released: December 2025

NEW FEATURES:

- Pre Spread & Affil Manager
  - Complete affil distribution management interface
  - Configure affil directories per site and section
  - Multi-CBFTP server support for spreading
  - Preview distribution before sending SITE PRE commands
  - Simultaneous SITE PRE to multiple CBFTP servers
  - Section mapping configuration (IRC → CBFTP sections)
  - Per-site affil directory paths with variable support
  - Distribution preview showing complete spread plan
  - Integrated with CBFTP /path endpoint for remote directory listing
  - Smart completion detection (only enables button when all jobs done)

- PreBot Management Redesign
  - Removed dropdown from MainForm PreBots section
  - Added Import PreDB button to MainForm (moved from PreBot form)
  - PreBot Edit button now opens PreBot form with dropdown selector
  - Consistent UI with Sites and CBFTP Servers management
  - MainForm PreBots section: [Add] [Edit] [Import PreDB] buttons

- Affils Management Overhaul
  - Removed confusing ""Enabled Race Sites"" list from Site Editor
  - Added ""Download Only Sites"" (DL-Only Affils) list in Site Editor
  - Sites marked as DL-Only will only download, not upload
  - Clear distinction between racing sites and affil-only sites
  - Simplified affil configuration workflow

- Pretime & IMDB/TVMaze Filtering Per-Site
  - Moved pretime check from global PreBot to per-site filtering in RaceHelper
  - Moved IMDB validation from IRCClient to per-site filtering in RaceHelper
  - Moved TVMaze validation from IRCClient to per-site filtering in RaceHelper
  - Each site now independently checks pretime, IMDB, TVMaze based on own config
  - Correct site names in logs (shows actual site name, not PreBot name)
  - Integrated logging flow: Pretime → IMDB → TVMaze → Rules → Blacklist
  - Support for both site-level and section-level max pretime settings
  - Import PreDB button imports latest 100 releases from predb.club
  - Populates local pretime database for max pretime filtering
  - One-click import for quick pretime database setup

IMPROVEMENTS:

- Log Display Quality
  - Fixed color tag bleeding in Application Log
  - Removed LogColors wrappers from LogManager.Info/Success/Warning/Error calls
  - Color formatting only used in CBFTP Integration Log where it's supported
  - Cleaner, more readable App Log output

- PreBot Form Enhancements
  - Dropdown shows all available PreBots when editing
  - Dropdown hidden when adding new PreBot
  - Loads configuration immediately when PreBot selected from dropdown
  - Refreshes dropdown after save/delete operations
  - Better form title updates (Add vs Edit mode)

- Code Organization
  - Cleaned up IRCClient.cs - removed IMDB/TVMaze/Pretime code
  - Centralized all per-site filtering in RaceHelper.FilterAllowedSites
  - Better separation of concerns between IRC handling and race filtering
  - Improved method signatures and parameter passing

BUG FIXES:

- Fixed PreBot dropdown not loading configuration on selection
- Fixed PreBot dropdown visible when adding new PreBot
- Fixed color tags appearing as literal text in Application Log
- Fixed site name showing as PreBot name in pretime check logs
- Fixed pretime/IMDB/TVMaze checks running globally before site filtering
- Fixed inconsistent log formatting across different log windows

TECHNICAL CHANGES:

- IRCClient.cs cleanup
  - Removed CheckMaxPretimeAsync call (moved to RaceHelper)
  - Removed ValidateIMDB and ValidateTVMaze methods (moved to RaceHelper)
  - Removed pretime check block before FilterAllowedSites
  - Simplified processing flow: Detect → Store Pretime → Map Section → Filter Sites

- RaceHelper.cs enhancements
  - Added per-site pretime check in FilterAllowedSites method
  - Added per-site IMDB validation in FilterAllowedSites method
  - Added per-site TVMaze validation in FilterAllowedSites method
  - Added ValidateIMDB private method with siteName parameter
  - Added ValidateTVMaze private method with siteName parameter
  - Checks run in order: Section enabled → Pretime → IMDB → TVMaze → Rules

- PreBot.cs updates
  - Added LoadPreBotsIntoDropdown method
  - Added Edit_PreBot_comboBox_SelectedIndexChanged handler
  - Updated constructor to handle Add vs Edit mode
  - Dropdown visibility controlled by mode (hidden for Add, visible for Edit)
  - Event handler temporarily disabled during dropdown population
  - Manual config load for first item to avoid event timing issues

- MainApp.cs simplification
  - Removed PreBot_comboBox1 dropdown from MainForm
  - Removed LoadPreBotsIntoDropdown from MainForm
  - Removed PreBot_comboBox1_SelectedIndexChanged handler
  - Updated Prebot_edit_button_Click to simply open PreBot form
  - Added Import_PreBot_button_Click for predb.club import

- Site Configuration Schema
  - Removed race_sites_enabled array (obsolete)
  - Added dl_only_sites array for affil-only downloads
  - Simplified site enable/disable logic

MIGRATION NOTES:

If upgrading from 1.0.5b:
1. Existing ""Enabled Race Sites"" configuration will be ignored
2. Using affil groups now for setting dl_only sites
3. PreBot management now uses Edit button → dropdown selector workflow
4. Import PreDB data.. 100 releases from predb.club to populate pretime DB


================================================================================


Version 1.0.5b (Beta)
--------------------------------
Released: December 2025

NEW FEATURES:

- Auto Request Filler 
  - Automatically fill requests from configured sites
  - Request path configuration per site
  
- Racing of Affiliates
  - Race releases with affiliated sites
  - Configurable affiliate relationships
  
- Getting Site Rules on Add/Edit Site form
  - View CBFTP site rules directly from GUI
  - No need to access CBFTP separately
  
- CBFTP Site Management
  - Add and edit CBFTP sites directly from GUI
  - Complete site configuration interface
  
- IMDb Integration Migration - OMDB -> IMDb-compatible providers
  - Tiffara is the current IMDb-compatible provider option
  - TMDb can be selected for metadata lookup when configured
  - ✅ Better data quality with native array support for genres, languages, countries
  - ✅ More reliable - no API key expiration or rate limit issues
  - ✅ Automatic rating precision fixes (Math.Round to 1 decimal)
  - ✅ Better response structure with detailed metadata
  - Uses Swagger API: /titles/{id} and /search/titles
  - Tiffara base URL: https://api.tiffara.com

BUG FIXES & EXCEPTION HANDLING:

Rating Display Fixes:
- Fixed floating-point precision issues (now shows 9.1 instead of 9.10000038146973)
- Applied Math.Round(rating, 1) during API response parsing
- Formatted test button displays with .ToString(""F1"") for clean output

Exception Handling Improvements:
- Fixed null reference exceptions in CBFTP spreadjob monitoring
- Added comprehensive null checks for CBFTP API responses
- Better error handling for IRC disconnections and reconnections
- Improved thread safety in chat client operations
- Fixed race conditions in user tracking dictionaries
- Added proper exception handling in async API calls
- Better disposal of HTTP clients to prevent memory leaks

Connection Issues:
- Fixed chat-only mode IRC connection edge cases
- Improved SSL certificate validation handling
- Better handling of network timeouts and retries

Memory & Performance:
- Fixed memory leaks in long-running IRC connections
- Corrected release regex pattern edge cases
- Improved disposal patterns for HTTP clients
- Better cleanup of resources on disconnect

IMPROVEMENTS:

- Enhanced CBFTP spreadjob monitoring reliability with better API polling
- Better error messages for configuration and API issues
- Improved logging clarity across all log windows (Race, IRC, CBFTP, Application)
- Performance optimizations for user tracking and list operations
- Enhanced rules engine logging in debug mode
- Removed legacy OMDB API key validation
- Updated Settings form to remove obsolete OMDB API key field
- Cleaner IMDb test functionality showing accurate data

TECHNICAL CHANGES:

- IMDBHelper.cs updated for IMDb-compatible APIs
  - Supports Tiffara and TMDb provider selection
  - Updated endpoints: GET /titles/{titleId} and GET /search/titles
  - Rewrote JSON parsing for new response structure
  - Removed API key requirement (kept property for backwards compatibility)
  - Increased rate limiting to 500ms (conservative for free API)
  
- SettingsForm.cs updated
  - Removed OMDB API key input field
  - Removed ""Test API Key"" button
  - Removed ""Get API Key"" link
  - Simplified settings management

- Response field mapping (OMDB -> IMDb-compatible API):
  - Title → primaryTitle
  - Year → startYear
  - imdbRating → rating.aggregateRating
  - imdbVotes → rating.voteCount
  - Runtime → runtimeSeconds (converted to ""X min"" format)
  - Genre → genres (array)
  - Country → originCountries (array)
  - Language → spokenLanguages (array)
  - Director → directors (array of name objects)
  - Actors → stars (array of name objects)


Version 1.0.4b (Beta)
--------------------------------
Released: December 2025

NEW FEATURES:

- Adding and editing existing CBFTP sites (direct from GUI)
- More bugfixes and improvements in racing core and rules engine

Version 1.0.3b (Beta)
--------------------------------

NEW FEATURES:

- IRC Chat Interface
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

- Chat Keys Management
  - New chat_keys dictionary in site configuration
  - Support for many channels per site with individual keys
  - Secure storage of Blowfish keys (encrypted in JSON)
  - Runtime key updates without restart
  - Separate key storage for racing vs chat channels

- Racing Core & Rules Engine
  - Unified rules engine for ALL sites and sections
  - Clear priority:
        Section DROP → Tag (CBFTP Mapping) rules → Section ALLOW → default ALLOW
  - Tag rules per CBFTP mapping in the UI (bottom-right panel)
  - Label reminder in GUI:
        ""CBFTP Mapping Rules: DROP run before ALLOW rules""
  - Updated race logging to distinguish origin site (first announce)
    from destination sites (where the job is sent)
  - CBFTP spreadjob monitoring with automatic COMPLETED / FAILED / TIMEOUT logging

IMPROVEMENTS:

- IRC Connection Enhancements
  - Chat-only mode skips all release processing
  - Proper channel filtering based on mode (racing vs chat)
  - Better SSL certificate handling with configurable security
  - Improved connection logging (chat vs monitoring messages)
  - ZNC nickname normalization for multi-network support
  - Performance optimization: user tracking only when window visible

- Security Improvements
  - All passwords and Blowfish keys encrypted in configuration
  - Configurable SSL validation (strict vs allow self-signed)
  - Secure key exchange with DH1080 protocol
  - Keys stored encrypted, decrypted only at runtime

- Racing & Logging
  - Clearer race log lines:
    - [DETECTED] line includes origin site
    - [RACING]/[COMPLETED]/[FAILED] lines show section and destination site list
  - Improved CBFTP job monitoring using standard spreadjobs API fields:
    - status (DONE / FAILED / TIMEOUT)
    - sites / sites_incomplete
    - size_estimated_bytes
    - time_spent_seconds
  - Better separation between Application log, IRC log, CBFTP log, and Race log
  - Race history stored in SQLite with full site list and section

- User Interface
  - ChatBox button on main form
  - Tabbed interface for multiple channels/PMs
  - Color-coded messages (encrypted=blue, plaintext=white, system=gray)
  - Real-time user list with status updates
  - Message input with encryption indicator
  - Context menu for key exchange and channel management

BUG FIXES:

- Chat-only mode no longer processes releases
- Channel list correctly filters to chat_keys when in chat mode
- Duplicate ""Chat-only mode"" log messages removed
- Release regex pattern corrected (no longer strips first character)
- Proper handling of read-only channel list
- SSL certificate validation now respects settings
- User tracking cross-thread issues resolved
- Nick changes now update all dictionaries (FiSH keys, PM keys, exchanges)
- PM tabs correctly handle encryption with ""PM:username"" format
- Channel tabs created before connection for better UX
- Race log ""origin site"" now only shown on [DETECTED] lines
- CBFTP job monitoring no longer relies on patched `/transfers` endpoint
- Spreadjob completion status correctly reflects DONE / FAILED / TIMEOUT
- Some rules could accidentally drop everything in a section without clear logging – fixed

KNOWN ISSUES:

- User list may not populate immediately on join (IRC server dependent)
- Some IRC servers may require custom certificate validation

--------------------------------------------------------------------------------

Version 1.0.2b (Beta)
--------------------------------
Released: November 2025

NEW FEATURES:

- Tag Rules GUI - Per-mapping rule configuration in UI
  - Each CBFTP mapping now has its own rules panel
  - Separate from Section Rules (per IRC section)
  - Add/Edit/Remove tag-specific rules directly in GUI
  - Visual distinction between Section Rules and Tag Rules
  
- IMDb Integration (Tiffara/TMDb provider selection)
  - Filter movies by rating, votes, genres
  - Language and country filtering
  - Quick filters: No Documentary, No Music, No Comedy, No Shows
  - Fallback option for API errors
  - Test connection button
  - Per-section configuration
  
- TVMaze Integration
  - Filter TV shows by rating, status, genres
  - Network filtering (Netflix, HBO, Amazon, etc.)
  - Show type filtering (Scripted, Reality, Documentary, etc.)
  - Skip ended/cancelled shows option
  - Configurable cache duration
  - Per-section configuration
  
- GUI Rebuild
  - Dark theme
  - Consolas font throughout
  - Color-coded buttons (Green=Save, Red=Delete, Grey=Action)
  - Improved layout and spacing
  - Fixed form borders (non-resizable)
  - Consistent styling across all forms

- Help System
  - Integrated help window with complete guide
  - Non-blocking modeless window
  - Search with keyword highlighting
  - Always-on-top for easy reference

- Enhanced Settings
  - Centralized settings dialog
  - Custom application name for taskbar
  - Debug mode toggle moved to Settings
  - Settings persist across restarts

IMPROVEMENTS:

- Enhanced section management
  - Clearer distinction between IRC and CBFTP sections
  - Better visual feedback for mappings
  - Improved tag rule visibility
  - Section removal now properly cleans up all lists
  
- Better documentation
  - Guide updated with Tag Rules vs Section Rules distinction
  - Real-world examples with scene groups
  - Expanded troubleshooting

- Settings consolidation
  - Debug mode moved from main form to Settings
  - Application name customization for taskbar

BUG FIXES:

- Removing IRC section now properly updates all ListBoxes
- Deleting section removes it from race sections list
- Section removal now cleans up JSON correctly
- Tag rules now save correctly per mapping
- Form dialogs no longer close parent forms
- GroupBox text colors now display correctly

--------------------------------------------------------------------------------

Version 1.0.1b (Initial Beta)
-----------------------------
Released: October 2024

INITIAL FEATURES:

- Core racing functionality
  - IRC announce monitoring
  - CBFTP integration
  - Section mapping system
  
- Basic Rules Engine
  - Section rules (per IRC section)
  - Operators: ==, !=, iswm, matches, contains, etc.
  - DROP and ALLOW actions
  - Rule priority system
  
- Site Management
  - Add/Edit/Delete sites
  - ZNC/IRC configuration
  - Channel and Blowfish key support
  - Multiple channel support
  - Encrypted password storage
  
- CBFTP Sync
  - Import sites from CBFTP
  - Automatic section detection
  - Bulk import
  
- Basic GUI
  - Site dropdown
  - Section configuration
  - Mapping interface
  - Rule editor (text-based)
  
- Configuration
  - JSON-based site configs
  - SQLite database for race history
  
- Logging System
  - Color-coded IRC output
  - Debug mode
  - Race history tracking

================================================================================
2. QUICK START - GET RACING IN 5 MINUTES
================================================================================

INITIAL SETUP (FIRST TIME ONLY):

Step 1: Add Your CBFTP Server
1. Click ""CBFTP Server"" button in main window.
2. Fill in:
   - Host: Your CBFTP server IP/hostname
   - Port: Usually 55477
   - Password: Your CBFTP API password
   - Profile: e.g. ""RACE""
3. Click ""Save"".

Step 2: Import Sites from CBFTP
1. Click ""Sync From CBFTP"".
2. Select your CBFTP server from the dropdown.
3. Click ""Fetch Sites"".
4. Tick the sites you want to import.
5. Click ""Import Selected Sites"".
6. Sites are now created with names and categories.
   (Only categories and sitename are imported; you still configure IRC yourself.)

BASIC CONFIGURATION (AFTER IMPORT):

Now configure a site for racing:

1. Select a site from the site dropdown (top of main window).
2. Click ""Edit Site"" (or double-click the site).
3. In the Site Editor:
   - Make sure the IRC section you want to race is present in the top-left
     ""IRC Sections"" list (add if needed).
   - In the bottom-left ""Race Sections (irc)"" list, make sure that section
     is **Enabled** using the Enable/Disable buttons.
   - In the top-left ""IRC Sections"" list, select that section (e.g. ""TV-1080P"").
   - In the top-middle ""Cbftp Sections"" list, select the matching CBFTP section
     (e.g. ""TV-1080P"" or ""MOVIES-HD"").
   - Click ""Map"".
   - In ""Mapping trigger (regex)"" enter: .*   (match everything).
   - Leave Section Rules and Tag Rules empty for now.
   - Click ""Save"" in the site editor.

4. Start the trader. You should see:
   - [DETECTED] in the Race log when an announce hits.
   - [RACING] when the spreadjob is started.
   - [COMPLETED]/[FAILED]/[TIMEOUT] when CBFTP finishes.

Alternative: Manual Site Setup (Without CBFTP Import):

1. Click ""Add Site"".
2. In the Site Editor:
   - General:
     • Sitename: must match CBFTP sitename if you want auto section import.
     • Announce: choose announce bot/mode from dropdown.
   - ZNC/IRC:
     • Host, Port
     • Username / network
     • Password (stored encrypted)
   - Channels:
     • Add announce channels in ""Channels"" list.
     • Add Blowfish keys where needed.
3. In ""Race Sections and Mapping"":
   - Add IRC sections (these usually match the site's paths, e.g. TV-DE, MP3).
   - In bottom-left ""Race Sections (irc)"", Enable those sections.
   - Map each IRC section to one or more CBFTP sections.
   - Set ""Mapping trigger (regex)"" for each mapping (tag).
4. Click ""Save"".

TL;DR:
- Add CBFTP server → Sync sites → Edit each site → Enable IRC sections
  in ""Race Sections (irc)"" → Map IRC → CBFTP → Done.

Quick Explanation:
- IRC Section   = What the site announces in IRC (e.g. [TV-DE], [X264-HD]).
- CBFTP Section = Logical section name in CBFTP (e.g. TV-DE, MOVIES-HD).
- Mapping (Tag) = IRC section + CBFTP section + trigger regex (+ tag rules).
- Section Rules = Filters that apply to **all** mappings for that IRC section.
- Tag Rules     = Filters for **one** mapping only (per CBFTP section).

================================================================================
3. CORE CONCEPTS - UNDERSTANDING THE SYSTEM
================================================================================

HOW IT WORKS - THE FLOW
------------------------

Step 1: IRC ANNOUNCE RECEIVED
  Example:
    ""[TV-DE] Show.Name.S01E01.1080p.WEB.H264-GROUP""

Step 2: EXTRACT SECTION NAME
  Section: ""TV-DE"" (from the announce line).

Step 3: CHECK IF SECTION IS ENABLED
  Is ""TV-DE"" Enabled in ""Race Sections (irc)"" for this site?  
  - If NO → ignore.  
  - If YES → continue.

Step 4: FIND MATCHING MAPPING
  In ""Race Sections and Mapping"", find a mapping where:
    - IRC Section is ""TV-DE""
    - The mapping's ""Mapping trigger (regex)"" matches the release name.

  The **first** mapping whose trigger matches will be used.

Step 5: EVALUATE RULES
  1. Section rules for the IRC section (TV-DE):
     - All DROP rules evaluated first.
     - Then Section ALLOW rules.
  2. Tag rules for this mapping (IRC=TV-DE → CBFTP=TV-DE or MOVIES-HD etc):
     - If a Tag rule matches, its ACTION (ALLOW/DROP) is used.
  3. If nothing matches: default action is ALLOW.

Step 6: SEND TO CBFTP (SPREADJOB)
  - Racetrade sends:
        section  = CBFTP section name
        name     = release name
        sites    = list of allowed sites
        profile  = CBFTP profile (e.g. RACE)
  - CBFTP log:
        [JOB SENT]
        [JOB STARTED] Job#1234: Release → CBFTP-Server
  - Race log:
        [RACING] line:
        [2025-.. ..:..:..] :: [TV-DE] :: [SiteAlpha,SiteBeta] :: [RACING] :: Release.Name...

Step 7: MONITOR CBFTP JOB
  - Racetrade periodically polls CBFTP `spreadjobs/{release}`.
  - Reads:
        status (DONE / FAILED / TIMEOUT / ACTIVE / etc)
        sites, sites_incomplete
        size_estimated_bytes
        time_spent_seconds
  - When terminal:
        DONE     → [COMPLETED]
        FAILED   → [FAILED]
        TIMEOUT  → [FAILED] with timeout reason

  Race log examples:
    [COMPLETED] :: [TV-DE] :: [SiteAlpha,SiteBeta] :: Release
    [FAILED]    :: [TV-DE] :: [SiteAlpha,SiteBeta] :: Release (CBFTP transfer failed)

LOG WINDOWS
-----------

Race Log:
  - High-level race info per release:
    [DETECTED], [RACING], [COMPLETED], [FAILED].

CBFTP Log:
  - Detailed job messages and Job# progress.

Application Log:
  - App-level info, errors, rules engine messages.

IRC Log:
  - Raw IRC events: connect, join, announces, FiSH status, errors.

IRC SECTIONS vs CBFTP SECTIONS
-------------------------------

IRC Sections:
- Names emitted by the site in IRC.
- Often equal to the path name on that site.
- Example on SiteAlpha: X264-HD-1080P, TV-DE, MP3.
- Example on SiteBeta: X264-HD, TV, MUSiC.

CBFTP Sections:
- Logical names defined in CBFTP's section config.
- All sites racing together share these names.
- CBFTP handles per-site paths internally for that section.
- Example: MOVIES-HD exists for SiteAlpha and SiteBeta with different paths.

Mapping:
- For each site, you map its IRC sections to shared CBFTP sections:
  SiteAlpha: IRC X264-HD-1080P → CBFTP MOVIES-HD
  SiteBeta : IRC X264-HD       → CBFTP MOVIES-HD

Trigger regex & rules decide which releases go into which mapping.

================================================================================
4. CONFIGURATION WALKTHROUGH
================================================================================

THE UI COMPONENTS
-----------------

TOP LEFT - ZNC SERVER
  - Host, Port, Username, Password
  - Connection details for ZNC/IRC server

TOP MIDDLE - Channels
  - ListBoxChannels: channels for this site
  - ""Add Channel"" / ""Remove Channel"" buttons
  - Blowfish keys entered elsewhere in the editor (if supported)

MIDDLE TOP - Race Sections and Mapping
--------------------------------------

LEFT - IRC Sections (listBox1):
  • List of IRC section names for this site.
  • Add / Remove buttons to manage sections.
  • These names must match the section names in the announces.

MIDDLE - Cbftp Sections (listCbftpSections):
  • List of CBFTP sections available for racing.
  • Typically imported from CBFTP or added manually.

RIGHT - Mapped Section (listBox5):
  • Shows mappings between IRC sections and CBFTP sections.
  • When you select an IRC section and a CBFTP section and click ""Map"",
    the mapping appears here.
  • The ""Mapping trigger (regex)"" textbox below is per-mapping (per tag).
  • Click ""Save"" next to the trigger to store it for the selected mapping.

Buttons:
  - Add/Remove (under IRC Sections): add or delete IRC section names.
  - Map: link selected IRC section → CBFTP section.
  - Unmap: remove a mapping from ""Mapped Section"".
  - Save (next to trigger): save trigger regex for the selected mapping.

BOTTOM MIDDLE - Race Sections (irc)
-----------------------------------

- ListBox2 + Enable / Disable buttons:
  • This list shows the IRC sections for this site.
  • Use **Enable** to activate a section for racing.
  • Use **Disable** to turn it off temporarily.
  • Only **Enabled** sections are processed by the racer.

BOTTOM MIDDLE - Affils (Group Names)
-------------------------

- When a group is detected on the site from an affil group, it triggers the site a DL-Only site.

BOTTOM RIGHT AREA - Rules
-------------------------

Top-right box: **CBFTP Mapping Rules: DROP run before ALLOW rules**  
  (Tag Rules listBox)

  - These rules belong to the **selected mapping** in ""Mapped Section"".
  - They apply only to this single combination of IRC section + CBFTP section.
  - Use them for mapping-specific filters (e.g. MOVIES-HD on one site).
  - Label reminder: ""DROP run before ALLOW rules"" – within this list,
    DROP rules are evaluated before ALLOW rules.

Bottom-right box: **Section Rules: DROP takes priority over ALLOW**  
  (listBox6)

  - These rules apply to **all mappings** in the selected IRC section.
  - Perfect for global blocks:
      [release] contains INTERNAL DROP
      [group]   isin BadGroup1,BadGroup2 DROP
  - First, all Section DROP rules run; then Section ALLOW rules.

BOTTOM LEFT GROUPS
------------------

New Regex:
  - Bot Name, NEW Regex, Ignore Words, Section Regex, Section Prefix/Suffix,
    Release Regex, etc.
  - Used to parse announces and extract section, release, group, etc.
  - Test button lets you test against a sample announce string.

Options:
  - Disable Site
  - Download Only Site
  - Finish Incompletes

Requests:
  - Use for Requests
  - Request Path: path used for request-filling if enabled.

Bottom buttons:
  - Save   : save entire site configuration.
  - Delete : delete this site.
  - Close  : close Site Editor.
  - Help   : open this guide.

---------------------------------------------
4.1 ADDING A NEW SITE
---------------------------------------------

1. In the main window, click ""Add Site"".

2. Site Settings:
   - ZNC server:
       Host / Port / Username / Password (for your bouncer or IRC).
   - General:
       Sitename: must match CBFTP sitename if you want to sync sections.
       Announce: choose the announce bot/mode from dropdown.

3. Channels:
   - Add the announce channels for this site in ""ListboxChannels"".
   - Configure Blowfish keys where needed (chat and/or racing channels).

4. New Regex:
   - Configure how releases are extracted from announces.
   - Usually you already have sane defaults from imports; change only if necessary.

5. Race Sections and Mapping:
   - Add IRC sections in the ""IRC Sections"" list (often same as path names).
   - Use bottom-left ""Race Sections (irc)"" list to Enable sections you want.
   - In the top area:
       • Select an IRC section.
       • Select a CBFTP section.
       • Click ""Map"".
       • Set ""Mapping trigger (regex)"" – start with .* for ""everything"".
       • Click ""Save"" (next to trigger).
   - Add Section Rules (bottom-right) for IRC section-wide filters.
   - Add Tag Rules in ""CBFTP Mapping Rules"" box for mapping-specific filters.

6. Click ""Save"" at bottom.

7. Start trader and verify logs:
   - IRC connects.
   - Announces appear in IRC log.
   - Race log shows [DETECTED] / [RACING] / [COMPLETED].

---------------------------------------------
4.2 EDITING AN EXISTING SITE
---------------------------------------------

1. Select the site from the main window dropdown.
2. Click ""Edit Site"".

Inside the editor you can:
- Change ZNC/IRC host, port, user, pass.
- Add/remove channels and adjust keys.
- Add/remove IRC sections.
- Enable/Disable sections in ""Race Sections (irc)"".
- Create/remove mappings between IRC and CBFTP sections.
- Adjust ""Mapping trigger (regex)"" per mapping.
- Add/modify Section Rules and Tag Rules.
- Toggle options like Disable Site, Download Only Site, Finish Incompletes.

Always click ""Save"" after changes.

Tip: When modifying rules heavily, enable Debug Mode in Settings so rules engine
messages show up in the Application log.

================================================================================
5. RULES SYSTEM - COMPLETE REFERENCE
================================================================================

RULE FORMAT
-----------

    [key] operator value ACTION

- [key]    : Field to check (release, section, group, year, etc.).
- operator : Comparison type.
- value    : Right-hand side of comparison.
- ACTION   : ALLOW or DROP (EXCEPT reserved / advanced).

Examples:
  [release] contains INTERNAL DROP
  [group]   isin BadGroup1,BadGroup2 DROP
  [release] contains 1080p ALLOW

OPERATORS
---------

==          Exact match (case-insensitive)
!=          Not equal
iswm        Wildcard match (* and ?)
matches     Regex pattern match
contains    Substring exists
startswith  Begins with substring
endswith    Ends with substring
isin        Left side is contained in comma/pipe-separated list

AVAILABLE KEYS
--------------

[release]   Full release name.
[section]   IRC section name (e.g. TV-DE, X264-HD).
[group]     Release group name.
[year]      Movie/show year (if extracted).
[quality]   Quality tag (if extracted, e.g. 1080p, 2160p).
[source]    Source tag (if extracted, e.g. WEB, BluRay).

WILDCARD PATTERNS (ISWM)
------------------------

*     Any characters
?     Single character

Examples:
  *1080p*           - contains ""1080p""
  *WEB*             - contains ""WEB""
  *.GERMAN.*        - "".GERMAN."" somewhere
  *S??E??*          - S01E01, S10E23, etc.

REGEX PATTERNS (MATCHES)
------------------------

.     Any character
.*    Anything
.+    One or more
[0-9] Any digit
(a|b) a OR b
^     Start of string
$     End of string
\b    Word boundary
\.    Literal dot

Examples:
  (?i).*1080p.*           - contains 1080p (case-insensitive)
  .*S[0-9]{2}E[0-9]{2}.*  - season/episode S01E01
  ^Movie.*2024$           - starts with Movie and ends with 2024

PRECISE LANGUAGE MATCHES USING \b
---------------------------------

To drop a language without dropping similar words, use regex with \b:

  [release] matches (?i)\bGERMAN\b DROP
  [release] matches (?i)\bFRENCH\b DROP
  [release] matches (?i)\biTALiAN\b DROP
  [release] matches (?i)\bPOLISH\b DROP

Examples:
  The.Carman.Family.Deaths.2025.POLISH.1080p.WEB.H264-GROUP
    → Dropped by POLISH rule.

  The.Hand.That.Rocks.the.Cradle.2025.FRENCH.720p.WEB.H264-GROUP
    → Dropped by FRENCH rule.

Alternative, less precise:
  [release] iswm *.GERMAN.* DROP

RULE PRIORITY (VERY IMPORTANT)
-------------------------------

The rules engine evaluates in this order:

1) Section DROP rules (for this IRC section)
   - If any matches → release is DROPPED immediately.

2) Tag rules (for the selected mapping)
   - Checked only if the mapping's trigger matched.
   - If a Tag rule matches, its ACTION (ALLOW/DROP) wins for that mapping.

3) Section ALLOW rules (for this IRC section)
   - Checked only if no Section DROP and no Tag rule matched.

4) Default
   - If no rules match anywhere → ALLOW.

So practically:
- Section rules are global for that IRC section.
- Tag rules let you fine-tune per CBFTP mapping.
- DROP always wins when it matches.

================================================================================
6. TAG RULES vs SECTION RULES - CRITICAL DISTINCTION
================================================================================

Section Rules (per IRC section)
-------------------------------

- Located in the bottom-right ""Section Rules"" box.
- Apply to ALL mappings under that IRC section.
- Evaluated first: all DROP rules, then ALLOW rules.
- Great for global blocks:
    [release] contains INTERNAL DROP
    [group]   isin BadGroup1,BadGroup2 DROP
    [release] matches (?i)\bPOLISH\b DROP

Tag Rules (per mapping / per CBFTP section)
-------------------------------------------

- Located in the top-right bottom box:
    ""CBFTP Mapping Rules: DROP run before ALLOW rules""
- Apply ONLY to the selected mapping in ""Mapped Section"".
- Evaluated after Section DROP rules.
- Good for:
    - Quality filters per mapping
    - Group whitelists per mapping
    - Special rules for MOVIES-HD vs MOVIES-LQ etc.

Hierarchy:

  Site
  └── IRC Section: MOVIES-HD
      ├── Section Rules (affect all mappings in MOVIES-HD)
      ├── Mapping A: MOVIES-HD → X264-HD-1080P
      │   └── Tag Rules for X264-HD-1080P only
      └── Mapping B: MOVIES-HD → X264-HD-720P
          └── Tag Rules for X264-HD-720P only

Remember:
- Section Rules = wide net (per IRC section).
- Tag Rules     = narrow net (per mapping).

================================================================================
7. REAL-WORLD EXAMPLES
================================================================================

EXAMPLE: Shared CBFTP section (MOVIES-HD) with different per-site filters
-------------------------------------------------------------------------

Goal:
- Use one shared CBFTP section: MOVIES-HD.
- SiteAlpha must be strict: only 1080p.
- SiteBeta can send both 720p and 1080p.

CBFTP:
  Section: MOVIES-HD
  - CBFTP is configured so MOVIES-HD exists for both SiteAlpha and SiteBeta
    with their own paths.

SiteAlpha configuration:
------------------------

IRC Sections:
  - X264-HD-1080P    (path on SiteAlpha is also X264-HD-1080P)

Race Sections (irc):
  - X264-HD-1080P is **Enabled**.

Mapping:
  - Select IRC Section: X264-HD-1080P.
  - Select CBFTP Section: MOVIES-HD.
  - Click ""Map"" → shows in ""Mapped Section"".
  - Mapping trigger (regex): .*

Section Rules (for IRC section X264-HD-1080P):
  [release] contains 720p DROP
  [release] contains CAM DROP
  [release] contains HDCAM DROP

Tag Rules (for MOVIES-HD mapping on SiteAlpha):
  (optional; you can leave empty, the Section Rules already block 720p)

Result (SiteAlpha):
  - Movie.Name.2025.1080p.WEB.H264-GROUP  → ALLOWED → MOVIES-HD.
  - Movie.Name.2025.720p.WEB.H264-GROUP   → DROPPED (contains 720p).

SiteBeta configuration:
-----------------------

IRC Sections:
  - X264-HD      (path on SiteBeta is X264-HD)

Race Sections (irc):
  - X264-HD is **Enabled**.

Mapping:
  - Select IRC Section: X264-HD.
  - Select CBFTP Section: MOVIES-HD.
  - Click ""Map"".
  - Mapping trigger (regex): .*

Section Rules (for IRC section X264-HD):
  [release] contains CAM DROP
  [release] contains HDCAM DROP

Tag Rules (for MOVIES-HD mapping on SiteBeta):
  (none - SiteBeta allows both 720p and 1080p)

Result (SiteBeta):
  - Movie.Name.2025.1080p.WEB.H264-GROUP  → ALLOWED → MOVIES-HD.
  - Movie.Name.2025.720p.WEB.H264-GROUP   → ALLOWED → MOVIES-HD.

Summary:
--------
- Both sites race into the same CBFTP section MOVIES-HD.
- On SiteAlpha, the IRC section X264-HD-1080P has Section Rules that drop 720p.
- On SiteBeta, the IRC section X264-HD does not drop 720p.
- CBFTP paths are per site inside the MOVIES-HD section; RaceTrade only
  cares about which CBFTP section name to use per site.

================================================================================
8. TROUBLESHOOTING
================================================================================

(Short version – focused on current behaviour.)

Problem: Nothing is being raced
-------------------------------

Checklist:
- Is the IRC section present in ""IRC Sections""?
- Is that section **Enabled** in ""Race Sections (irc)""?
- Is there at least one mapping in ""Mapped Section"" for that IRC section?
- Does the mapping's trigger regex match? (try .*)
- Are Section DROP rules blocking everything?
- Did you click all the ""Save"" buttons (trigger save + site save)?

Fix:
- Temporarily remove all rules.
- Use .* as trigger.
- Enable Debug Mode and watch Application + Race logs.

Problem: Releases going to wrong CBFTP section
----------------------------------------------

- Check the order of mappings (first trigger that matches wins).
- Make specific triggers first, catch-all mapping with .* last.
- Check logs for which mapping was selected.

Problem: All releases are dropped
---------------------------------

- Look at Section Rules box:
    [release] matches .* DROP
  will drop everything for that IRC section.
- Remove or narrow overly broad DROP rules.
- Use debug mode: it will show:
    [DROP] Global DROP rule matched: ...

Problem: Hard to see what matched
---------------------------------

- Enable Debug Mode.
- Application log will show:
    [✓] Rule matched: [release] contains INTERNAL, Input='...'
- Race log shows when a release is filtered (FILTERED / DROPPED) vs raced.

Problem: IMDb API not working
-----------------------------

- Choose Settings -> Movie APIs -> Provider: Tiffara or TMDb.
- Tiffara provides IMDb-compatible ratings; TMDb is lookup metadata only.
- Test the API from Section Settings (Test IMDb button)
- Check Application log for error messages
- Verify internet connection
- Tiffara endpoint: https://api.tiffara.com/

Problem: Rating shows weird decimals
------------------------------------

- This has been fixed in version 1.0.5b
- Ratings now display as 9.1 instead of 9.10000038146973
- If you still see this, make sure you're using the latest version

Problem: Application crashes or null reference exceptions
---------------------------------------------------------

- Version 1.0.5b has improved exception handling
- Enable Debug Mode to see detailed error messages
- Check Application log for stack traces
- Common causes:
  - CBFTP server not responding
  - IRC connection dropped
  - Malformed configuration JSON
  - Missing or invalid site settings

================================================================================
9. ADVANCED CONFIGURATIONS
================================================================================

Most of the original advanced configurations still apply:

- Multiple sites racing into the same CBFTP section.
- Using a prebot vs direct site announces.
- Group-whitelisting via Tag Rules on specific mappings.
- Language-based filtering with regex word boundaries.
- Quality-based routing by splitting 720p / 1080p / 2160p into different
  CBFTP sections with different mappings and rules.
- Performance tips:
    • Put global DROP rules in Section Rules.
    • Keep triggers simple where possible.
    • Place specific mappings (1080p, 2160p) before catch-all mappings.

Key reminder:
-------------
When mapping multiple real IRC sections (e.g. TV-DE on SiteAlpha
and X264-FOREIGN on SiteBeta) into a single CBFTP section (TV-DE):

- Configure that CBFTP section for both sites inside CBFTP.
- In each site's editor:
    IRC Section: TV-DE or X264-FOREIGN
    CBFTP Section: TV-DE
- Use Section Rules per site to express per-site preferences.

IMDB FILTERING TIPS:
-------------------

RaceTrader can use Tiffara or TMDb for movie lookup:

- Tiffara provides IMDb-compatible ratings with no key configured here
- TMDb requires the TMDb key in Settings and does not provide IMDb ratings
- Better data with native arrays for genres, languages, countries
- Test connection from Section Settings → IMDb tab → Test IMDb button

Example IMDb filters in Section Settings:
- Minimum Rating: 7.0
- Minimum Votes: 10000
- Allowed Genres: Action, Thriller, Sci-Fi
- Blocked Genres: Documentary, Musical
- Only English Language: ✓
- Only US Country: ✓

The IMDb cache stores results for 30 days by default to minimize API calls
and improve performance.

TVMAZE FILTERING:
----------------

Similar to IMDb but for TV shows:
- Filter by rating, status, genres
- Network filtering (Netflix, HBO, Amazon, etc.)
- Skip ended/cancelled shows
- Configurable cache duration

CHAT FUNCTIONALITY:
------------------

Version 1.0.3b+ includes full IRC chat:
- Tabbed interface for multiple channels
- FiSH encryption with visual indicators
- DH1080 key exchange for secure PMs
- Chat-only mode (no racing)
- Configure chat keys separately from racing keys

To use chat:
1. Configure chat_keys in site config (separate from racing keys)
2. Click ""Chat"" button on main form
3. Select chat channels when connecting
4. Use context menu for key exchange and channel management

================================================================================
END OF GUIDE
================================================================================

Need help? Enable Debug Mode and check:

- Application log  – rules & config messages
- IRC log          – announces and connection status
- CBFTP log        – spreadjob status
- Race log         – [DETECTED], [RACING], [COMPLETED], [FAILED]

In almost all cases, the problem is:
- Section not Enabled in ""Race Sections (irc)""
- Missing mapping (you forgot to click ""Map"")
- Trigger regex not matching (too strict)
- Section DROP rule too broad
- Confusing Section Rules vs Tag Rules
- Forgot to click ""Save""

Happy Racing! 🏁

Version 1.0.5b - December 2025

";

        public HelpForm()
            : this("RaceTrader Help", "RaceTrader Complete Guide", BuildGuideContent())
        {
        }

        protected HelpForm(string windowTitle, string headingText, string contentText)
        {
            this.windowTitle = windowTitle;
            this.headingText = headingText;
            this.contentText = contentText;
            InitializeComponent();
            RaceTrade.ThemeManager.ApplyTheme(this);
            ApplySyntaxHighlighting();
        }

        internal static string GetChangelogContent()
        {
            int start = FindSectionStart(DOCUMENTATION_CONTENT, "1. CHANGELOG");
            int end = FindSectionStart(DOCUMENTATION_CONTENT, "2. QUICK START - GET RACING IN 5 MINUTES", start + 1);
            if (start < 0 || end <= start)
                return "No changelog content found.";

            return DOCUMENTATION_CONTENT.Substring(start, end - start)
                .Replace("1. CHANGELOG", "CHANGELOG")
                .Trim();
        }

        private static string BuildGuideContent()
        {
            string content = RemoveChangelog(DOCUMENTATION_CONTENT);

            return content
                .Replace("1. Changelog\r\n", "")
                .Replace("1. Changelog\n", "")
                .Replace("2. Quick Start - Get Racing in 5 Minutes", "1. Quick Start - Get Racing in 5 Minutes")
                .Replace("3. Core Concepts - Understanding the System", "2. Core Concepts - Understanding the System")
                .Replace("4. Configuration Walkthrough", "3. Configuration Walkthrough")
                .Replace("   4.1 Adding a New Site", "   3.1 Adding a New Site")
                .Replace("   4.2 Editing an Existing Site", "   3.2 Editing an Existing Site")
                .Replace("5. Rules System - Complete Reference", "4. Rules System - Complete Reference")
                .Replace("6. Tag Rules vs Section Rules - CRITICAL DISTINCTION", "5. Tag Rules vs Section Rules - CRITICAL DISTINCTION")
                .Replace("7. Real-World Examples", "6. Real-World Examples")
                .Replace("8. Troubleshooting", "7. Troubleshooting")
                .Replace("9. Advanced Configurations", "8. Advanced Configurations")
                .Replace("2. QUICK START - GET RACING IN 5 MINUTES", "1. QUICK START - GET RACING IN 5 MINUTES")
                .Replace("3. CORE CONCEPTS - UNDERSTANDING THE SYSTEM", "2. CORE CONCEPTS - UNDERSTANDING THE SYSTEM")
                .Replace("4. CONFIGURATION WALKTHROUGH", "3. CONFIGURATION WALKTHROUGH")
                .Replace("5. RULES SYSTEM - COMPLETE REFERENCE", "4. RULES SYSTEM - COMPLETE REFERENCE")
                .Replace("6. TAG RULES vs SECTION RULES - CRITICAL DISTINCTION", "5. TAG RULES vs SECTION RULES - CRITICAL DISTINCTION")
                .Replace("7. REAL-WORLD EXAMPLES", "6. REAL-WORLD EXAMPLES")
                .Replace("8. TROUBLESHOOTING", "7. TROUBLESHOOTING")
                .Replace("9. ADVANCED CONFIGURATIONS", "8. ADVANCED CONFIGURATIONS");
        }

        private static string RemoveChangelog(string content)
        {
            int start = FindSectionStart(content, "1. CHANGELOG");
            int end = FindSectionStart(content, "2. QUICK START - GET RACING IN 5 MINUTES", start + 1);
            if (start < 0 || end <= start)
                return content;

            return content.Remove(start, end - start);
        }

        private static int FindSectionStart(string content, string heading, int startIndex = 0)
        {
            int headingIndex = content.IndexOf(heading, startIndex, StringComparison.Ordinal);
            if (headingIndex < 0)
                return -1;

            const string separator = "================================================================================";
            int separatorIndex = content.LastIndexOf(separator, headingIndex, StringComparison.Ordinal);
            return separatorIndex >= 0 ? separatorIndex : headingIndex;
        }

        private void InitializeComponent()
        {
            this.Text = windowTitle;
            this.Size = new Size(1000, 900);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(22, 26, 36);
            this.Dark = true;
            this.Mode = AntdUI.TAMode.Dark;
            this.Font = new Font("Cascadia Mono", 8.25f);
            this.TopMost = true;             // Always on top
            this.ShowIcon = false;           // Remove icon
            this.ControlBox = true;          // Keep the X close button

            // Top Panel with title and close button
            topPanel = new AntPanel
            {
                Dock = DockStyle.Top,
                Height = 88,
                Back = Color.FromArgb(16, 20, 28),
                BackColor = Color.FromArgb(16, 20, 28),
                BorderColor = Color.FromArgb(48, 56, 72),
                BorderWidth = 0F,
                Radius = 0,
                Shadow = 0,
                Padding = new Padding(10)
            };
            this.Controls.Add(topPanel);

            // Title
            titleLabel = new Label
            {
                Text = headingText,
                Font = new Font("Cascadia Mono", 13.5f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Location = new Point(10, 11),
                Size = new Size(650, 24),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            };
            topPanel.Controls.Add(titleLabel);

            // Search box
            var searchLabel = new Label
            {
                Text = "Search:",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Location = new Point(10, 52),
                AutoSize = true,
                Font = new Font("Cascadia Mono", 8.5f, FontStyle.Bold)
            };
            topPanel.Controls.Add(searchLabel);

            searchBox = new AntInput
            {
                Location = new Point(76, 43),
                Size = new Size(390, 34),
                BackColor = Color.FromArgb(33, 38, 50),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 9.25f),
                PlaceholderText = "Find text",
                BorderColor = Color.FromArgb(72, 84, 108),
                BorderHover = RaceTrade.ThemeManager.Colors.AccentCyan,
                BorderActive = RaceTrade.ThemeManager.Colors.AccentCyan,
                BorderWidth = 1F,
                PaddingLR = 10,
                Radius = 3
            };
            searchBox.KeyPress += SearchBox_KeyPress;
            topPanel.Controls.Add(searchBox);

            searchButton = new AntButton
            {
                Text = "Find",
                Location = new Point(480, 43),
                Size = new Size(92, 34),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.75f, FontStyle.Bold),
                Type = AntdUI.TTypeMini.Primary,
                Shape = AntdUI.TShape.Default,
                Radius = 3,
                BorderWidth = 1F
            };
            searchButton.Click += SearchButton_Click;
            topPanel.Controls.Add(searchButton);

            // Close button
            closeButton = new AntButton
            {
                Text = "Close",
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(876, 18),
                Size = new Size(96, 34),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.75f, FontStyle.Bold),
                Type = AntdUI.TTypeMini.Error,
                Shape = AntdUI.TShape.Default,
                Radius = 3,
                BorderWidth = 1F
            };
            closeButton.Click += (s, e) => this.Hide();
            topPanel.Controls.Add(closeButton);

            // Main help content
            // Main help content
            helpTextBox = new RichTextBox
            {
                Location = new Point(0, 88),
                Size = new Size(this.ClientSize.Width, this.ClientSize.Height - 88),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(13, 16, 24),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 9f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = true,
                Text = contentText
            };
            this.Controls.Add(helpTextBox);

            // Formatting is applied after the theme pass in the constructor.
        }

        private void ApplySyntaxHighlighting()
        {
            if (helpTextBox == null || string.IsNullOrEmpty(helpTextBox.Text))
                return;

            helpTextBox.SuspendLayout();

            // Reset all text to default colors first
            helpTextBox.SelectAll();
            helpTextBox.SelectionFont = new Font("Cascadia Mono", 9f, FontStyle.Regular);
            helpTextBox.SelectionColor = RaceTrade.ThemeManager.Colors.Foreground;
            helpTextBox.SelectionBackColor = helpTextBox.BackColor;
            helpTextBox.Select(0, 0);

            Color accent = RaceTrade.ThemeManager.Colors.AccentCyan;
            Color heading = Color.FromArgb(255, 211, 96);
            Color subheading = Color.FromArgb(112, 226, 170);
            Color muted = RaceTrade.ThemeManager.Colors.ForegroundMuted;
            Color info = Color.FromArgb(141, 211, 255);
            Color warning = RaceTrade.ThemeManager.Colors.WarningLight;
            Color danger = RaceTrade.ThemeManager.Colors.DangerLight;

            ApplyLineStyle(@"^={5,}\s*$", muted, FontStyle.Regular, 8.5f);
            ApplyLineStyle(@"^-{5,}\s*$", Color.FromArgb(82, 94, 118), FontStyle.Regular, 8.5f);
            ApplyLineStyle(@"^\s*(RACETRADE COMPLETE GUIDE|CHANGELOG)\s*$", accent, FontStyle.Bold, 15f);
            ApplyLineStyle(@"^\s*Rules, Mapping, and Configuration Manual\s*$", muted, FontStyle.Italic, 10f);
            ApplyLineStyle(@"^\d+\.\s+.*$", heading, FontStyle.Bold, 11f);
            ApplyLineStyle(@"^Version\s+.*$", Color.FromArgb(182, 204, 255), FontStyle.Bold, 10f);
            ApplyLineStyle(@"^[A-Z][A-Z0-9 /&()""'.,+-]+:$", subheading, FontStyle.Bold, 10f);
            ApplyLineStyle(@"^\s*\d+\.\s+.*$", Color.FromArgb(228, 233, 242), FontStyle.Bold, 9.3f);
            ApplyLineStyle(@"^\s*-\s+.*$", RaceTrade.ThemeManager.Colors.Foreground, FontStyle.Regular, 9f);
            ApplyLineStyle(@"^\s{2,}-\s+.*$", muted, FontStyle.Regular, 8.8f);

            HighlightPattern(@"CRITICAL|WARNING|IMPORTANT|DROP|FAILED|ERROR|BUG FIXES", danger, FontStyle.Bold);
            HighlightPattern(@"NEW FEATURES|IMPROVEMENTS|TECHNICAL CHANGES|MIGRATION NOTES", subheading, FontStyle.Bold);
            HighlightPattern(@"Example.*?:", warning, FontStyle.Bold);
            HighlightPattern(@"\b(ALLOW|DROP|SKIP|RACE|ENABLED|DISABLED)\b", warning, FontStyle.Bold);
            HighlightPattern(@"\[[^\]\r\n]+\]", info, FontStyle.Bold);
            HighlightPattern(@"\{[^\}\r\n]+\}", info, FontStyle.Bold);
            HighlightPattern(@"(?i)\b(regex|chat_keys|chan\d+|blowfish_key\d+|cbftp|imdb|tvmaze|prebot|sitebot)\b", Color.FromArgb(184, 193, 255), FontStyle.Regular);
            HighlightPattern(@"[✓✅]", RaceTrade.ThemeManager.Colors.SuccessLight, FontStyle.Bold);
            HighlightPattern(@"[✗❌]", danger, FontStyle.Bold);
            HighlightPattern(@"[⚠☐]", warning, FontStyle.Bold);

            helpTextBox.Select(0, 0);
            helpTextBox.SelectionLength = 0;
            helpTextBox.ResumeLayout();
        }

        private void ApplyLineStyle(string pattern, Color color, FontStyle style, float size)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                foreach (System.Text.RegularExpressions.Match match in regex.Matches(helpTextBox.Text))
                {
                    helpTextBox.Select(match.Index, match.Length);
                    helpTextBox.SelectionColor = color;
                    helpTextBox.SelectionFont = new Font("Cascadia Mono", size, style);
                }
            }
            catch
            {
                // Ignore regex/font errors
            }
        }

        private void HighlightPattern(string pattern, Color color, FontStyle style = FontStyle.Regular)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                foreach (System.Text.RegularExpressions.Match match in regex.Matches(helpTextBox.Text))
                {
                    helpTextBox.Select(match.Index, match.Length);
                    helpTextBox.SelectionColor = color;
                    helpTextBox.SelectionFont = new Font("Cascadia Mono", 9f, style);
                }

                helpTextBox.Select(0, 0); // Reset selection
            }
            catch
            {
                // Ignore regex errors
            }
        }

        private void SearchBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                e.Handled = true;
                PerformSearch();
            }
        }

        private void SearchButton_Click(object sender, EventArgs e)
        {
            PerformSearch();
        }

        private void PerformSearch()
        {
            var searchText = searchBox.Text.Trim();
            if (string.IsNullOrEmpty(searchText))
                return;

            // Start searching from current position or beginning
            int startIndex = helpTextBox.SelectionStart + helpTextBox.SelectionLength;
            if (startIndex >= helpTextBox.Text.Length)
                startIndex = 0;

            int index = helpTextBox.Text.IndexOf(searchText, startIndex,
                StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
            {
                // Found - highlight it
                helpTextBox.Select(index, searchText.Length);
                helpTextBox.ScrollToCaret();

                // Highlight the found text
                helpTextBox.SelectionBackColor = Color.Yellow;
                helpTextBox.SelectionColor = Color.Black;
            }
            else
            {
                // Not found - try from beginning
                index = helpTextBox.Text.IndexOf(searchText, 0,
                    StringComparison.OrdinalIgnoreCase);

                if (index >= 0)
                {
                    helpTextBox.Select(index, searchText.Length);
                    helpTextBox.ScrollToCaret();
                    helpTextBox.SelectionBackColor = Color.Yellow;
                    helpTextBox.SelectionColor = Color.Black;
                }
                else
                {
                    MessageBox.Show($"'{searchText}' not found in help content.",
                        "Search", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Don't close, just hide
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }
    }
}
