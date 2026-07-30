using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;
using System.Threading.Tasks;

namespace RaceTrader
{
    /// <summary>
    /// Test form using ONLY existing production methods - zero duplication
    /// </summary>
    public class TestReleaseForm : AntdUI.Window
    {
        private TextBox releaseNameTextBox;
        private ComboBox siteComboBox;
        private ComboBox sectionComboBox;
        private Button testButton;
        private Button clearButton;
        private Button closeButton;
        private RichTextBox resultsTextBox;
        private JObject currentSiteConfig;
        private string currentSiteName;

        public TestReleaseForm()
        {
            InitializeComponent();
            RaceTrade.ThemeManager.ApplyTheme(this);
            LoadSites();
        }

        private void InitializeComponent()
        {
            this.Text = "Test Release - Full Validation Flow";
            this.Size = new Size(1000, 870);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(22, 26, 36);
            this.Font = new Font("Cascadia Mono", 8.25f);

            int yPos = 20;

            var titleLabel = new Label
            {
                Text = "Test Release Validation",
                Location = new Point(20, yPos),
                Size = new Size(960, 30),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Cascadia Mono", 12, FontStyle.Bold)
            };
            this.Controls.Add(titleLabel);

            yPos += 40;

            var releaseLabel = new Label
            {
                Text = "Release Name:",
                Location = new Point(20, yPos + 3),
                Size = new Size(120, 20),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            this.Controls.Add(releaseLabel);

            releaseNameTextBox = new TextBox
            {
                Location = new Point(140, yPos),
                Size = new Size(700, 25),
                BackColor = Color.FromArgb(33, 38, 50),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            releaseNameTextBox.KeyPress += (s, e) => {
                if (e.KeyChar == (char)Keys.Enter)
                {
                    // Respect the disabled button — otherwise Enter starts a second
                    // concurrent test while the first is still awaiting HTTP calls.
                    if (testButton != null && testButton.Enabled)
                        TestButton_Click(null, null);
                    e.Handled = true;
                }
            };
            this.Controls.Add(releaseNameTextBox);

            yPos += 35;

            var siteLabel = new Label
            {
                Text = "Site:",
                Location = new Point(20, yPos + 3),
                Size = new Size(120, 20),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            this.Controls.Add(siteLabel);

            siteComboBox = new ComboBox
            {
                Location = new Point(140, yPos),
                Size = new Size(250, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(33, 38, 50),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            siteComboBox.SelectedIndexChanged += SiteComboBox_SelectedIndexChanged;
            this.Controls.Add(siteComboBox);

            var sectionLabel = new Label
            {
                Text = "IRC Section:",
                Location = new Point(410, yPos + 3),
                Size = new Size(90, 20),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            this.Controls.Add(sectionLabel);

            sectionComboBox = new ComboBox
            {
                Location = new Point(510, yPos),
                Size = new Size(250, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(33, 38, 50),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            this.Controls.Add(sectionComboBox);

            yPos += 40;

            // Buttons with proper styling
            testButton = new Button
            {
                Text = "Run Full Test",
                Location = new Point(140, yPos),
                Size = new Size(140, 30),
                BackColor = Color.FromArgb(77, 166, 112),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            testButton.FlatAppearance.BorderSize = 1;
            testButton.FlatAppearance.BorderColor = Color.Black;
            testButton.Click += TestButton_Click;
            this.Controls.Add(testButton);

            clearButton = new Button
            {
                Text = "Clear",
                Location = new Point(290, yPos),
                Size = new Size(100, 30),
                BackColor = Color.FromArgb(72, 80, 98),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            clearButton.FlatAppearance.BorderSize = 1;
            clearButton.FlatAppearance.BorderColor = Color.Black;
            clearButton.Click += (s, e) => {
                releaseNameTextBox.Clear();
                resultsTextBox.Clear();
            };
            this.Controls.Add(clearButton);

            yPos += 50;

            var resultsLabel = new Label
            {
                Text = "Validation Results:",
                Location = new Point(20, yPos),
                Size = new Size(200, 20),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 9f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            this.Controls.Add(resultsLabel);

            yPos += 25;

            resultsTextBox = new RichTextBox
            {
                Location = new Point(20, yPos),
                Size = new Size(this.ClientSize.Width - 40, this.ClientSize.Height - yPos - 70),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(9, 11, 17),
                ForeColor = Color.White,
                Font = new Font("Cascadia Mono", 8.25f),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(resultsTextBox);

            closeButton = new Button
            {
                Text = "Close",
                Location = new Point(this.ClientSize.Width - 130, this.ClientSize.Height - 45),
                Size = new Size(110, 30),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
                Font = new Font("Cascadia Mono", 8.25f)
            };
            RaceTrade.ThemeManager.StyleDangerButton(closeButton);
            closeButton.Click += (s, e) => this.Close();
            this.Controls.Add(closeButton);
            this.CancelButton = closeButton;

        }

        private void LoadSites()
        {
            siteComboBox.Items.Clear();

            if (!Directory.Exists("sites"))
                return;

            var siteFiles = Directory.GetFiles("sites", "*.json");
            foreach (var file in siteFiles)
            {
                var siteName = Path.GetFileNameWithoutExtension(file);
                if (!siteName.Equals("default_site", StringComparison.OrdinalIgnoreCase))
                {
                    siteComboBox.Items.Add(siteName);
                }
            }

            if (siteComboBox.Items.Count > 0)
                siteComboBox.SelectedIndex = 0;
        }

        private void SiteComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (siteComboBox.SelectedItem == null)
                return;

            currentSiteName = siteComboBox.SelectedItem.ToString();
            var siteFile = Path.Combine("sites", $"{currentSiteName}.json");

            try
            {
                var json = File.ReadAllText(siteFile);
                currentSiteConfig = JObject.Parse(json);

                sectionComboBox.Items.Clear();
                var sections = currentSiteConfig["sections"] as JArray;
                if (sections != null)
                {
                    foreach (var section in sections)
                    {
                        var ircName = section["irc_name"]?.ToString();
                        if (!string.IsNullOrEmpty(ircName))
                        {
                            sectionComboBox.Items.Add(ircName);
                        }
                    }
                }

                if (sectionComboBox.Items.Count > 0)
                    sectionComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                AppendResult($"Error loading site: {ex.Message}", Color.Red);
            }
        }

        private async void TestButton_Click(object sender, EventArgs e)
        {
            resultsTextBox.Clear();

            var releaseName = releaseNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(releaseName))
            {
                AppendResult("ERROR: Please enter a release name", Color.Red);
                return;
            }

            if (siteComboBox.SelectedItem == null || sectionComboBox.SelectedItem == null)
            {
                AppendResult("ERROR: Please select a site and section", Color.Red);
                return;
            }

            testButton.Enabled = false;
            testButton.Text = "Testing...";

            try
            {
                var ircSection = sectionComboBox.SelectedItem.ToString();

                AppendResult("===============================================================", Color.Gray);
                AppendResult($"Testing Release: {releaseName}", Color.Cyan);
                AppendResult($"Site: {currentSiteName}", Color.Cyan);
                AppendResult($"IRC Section: {ircSection}", Color.Cyan);
                AppendResult("===============================================================\n", Color.Gray);

                bool allPassed = true;

                // ═════════════════════════════════════════════════════════════
                // STEP 1: CBFTP Section Mapping (uses RaceHelper)
                // ═════════════════════════════════════════════════════════════
                AppendResult("[ STEP 1: CBFTP Section Mapping ]\n", Color.Yellow);

                var sectionPrefix = currentSiteConfig["site_settings"]?["section_prefix"]?.ToString() ?? "";
                var sectionSuffix = currentSiteConfig["site_settings"]?["section_suffix"]?.ToString() ?? "";

                // USE EXISTING: RaceHelper.GetMappedCbftpSection()
                string cbftpSection = RaceHelper.GetMappedCbftpSection(
                    ircSection,
                    releaseName,
                    currentSiteConfig,
                    sectionPrefix,
                    sectionSuffix,
                    (msg, color) => { } // Silent
                );

                if (string.IsNullOrEmpty(cbftpSection))
                {
                    AppendResult("FAILED: Could not map to CBFTP section", Color.Red);
                    AppendResult("===============================================================\n", Color.Gray);
                    AppendResult("TEST FAILED - Cannot proceed without CBFTP mapping", Color.Red);
                    return;
                }

                AppendResult($"SUCCESS: Mapped to CBFTP Section: {cbftpSection}\n", Color.Green);

                // ═════════════════════════════════════════════════════════════
                // STEP 2: Rules Engine (uses RulesEngine)
                // ═════════════════════════════════════════════════════════════
                AppendResult("[ STEP 2: Rules Engine (Custom Filters) ]\n", Color.Yellow);

                // USE EXISTING: RulesEngine
                var rulesEngine = new RulesEngine();
                rulesEngine.LoadRules(currentSiteConfig, cbftpSection);

                var input = new Dictionary<string, string>
                {
                    ["release"] = releaseName.ToLower(),
                    ["rlsname"] = releaseName.ToLower(),
                    ["section"] = ircSection.ToLower(),
                    ["ircSection"] = ircSection.ToLower(),
                    ["cbftp_section"] = cbftpSection?.ToLower() ?? "",
                    ["cbftpSection"] = cbftpSection?.ToLower() ?? ""
                };

                // USE EXISTING: RaceHelper.ParseReleaseName()
                var parsed = RaceHelper.ParseReleaseName(releaseName);
                foreach (var kvp in parsed)
                {
                    input[kvp.Key] = kvp.Value.ToLower();
                }

                int globalRules = rulesEngine._sectionRules?.Count ?? 0;
                int tagRules = rulesEngine._tagRules?.ContainsKey(cbftpSection) == true
                    ? rulesEngine._tagRules[cbftpSection].Count : 0;

                AppendResult($"  Loaded {globalRules} global rule(s) + {tagRules} tag rule(s)", Color.White);

                if (globalRules == 0 && tagRules == 0)
                {
                    AppendResult("  No custom rules configured\n", Color.Gray);
                }
                else
                {
                    //USE EXISTING: RulesEngine.Evaluate()
                    var rulesResult = rulesEngine.Evaluate(input, cbftpSection);

                    if (rulesResult == "DROP")
                    {
                        AppendResult("BLOCKED by custom rules (check IRC log for details)\n", Color.Red);
                        allPassed = false;

                        AppendResult("===============================================================\n", Color.Gray);
                        AppendResult("TEST FAILED - Release blocked by rules engine", Color.Red);
                        return;
                    }

                    AppendResult("SUCCESS: Passed custom rules\n", Color.Green);
                }

                // ═════════════════════════════════════════════════════════════
                // STEP 3: Site Filtering (uses RaceHelper)
                // ═════════════════════════════════════════════════════════════
                AppendResult("[ STEP 3: Site Filtering ]\n", Color.Yellow);

                // USE EXISTING: RaceHelper.LoadAllSiteConfigs() and FilterAllowedSites()
                RaceHelper.LoadAllSiteConfigs();

                var raceSectionsDictionary = currentSiteConfig["race_sections_enabled"]
                    ?.ToObject<List<string>>()
                    ?.ToDictionary(s => s, s => string.Empty) ?? new Dictionary<string, string>();

                var filterResult = await RaceHelper.FilterAllowedSites(
                    raceSectionsDictionary,
                    new Dictionary<string, string>(),
                    new List<string>(),
                    cbftpSection,
                    releaseName,
                    "",
                    sectionPrefix,
                    sectionSuffix,
                    (msg, color) => { },
                    currentSiteName
                );

                switch (filterResult.Status)
                {
                    case FilterStatus.Success:
                        AppendResult($"SUCCESS: {filterResult.AllowedSites.Count} site(s) will race:", Color.Green);
                        var dlOnly = filterResult.DlOnlySites ?? new List<string>();
                        foreach (var site in filterResult.AllowedSites.Take(10))
                        {
                            bool isDlOnly = dlOnly.Contains(site, StringComparer.OrdinalIgnoreCase);
                            if (isDlOnly)
                                AppendResult($"  - {site}  [DL-Only: affil]", Color.Orange);
                            else
                                AppendResult($"  - {site}", Color.White);
                        }
                        if (filterResult.AllowedSites.Count > 10)
                        {
                            AppendResult($"  ... and {filterResult.AllowedSites.Count - 10} more", Color.Gray);
                        }
                        if (dlOnly.Any())
                        {
                            AppendResult($"  ({dlOnly.Count} site(s) download-only because the group is an affil)", Color.Orange);
                        }
                        break;

                    case FilterStatus.Duplicate:
                        AppendResult($"WARNING: Release already processed", Color.Yellow);
                        allPassed = false;
                        break;

                    case FilterStatus.NoSites:
                    case FilterStatus.InsufficientSites:
                        AppendResult($"WARNING: {filterResult.Message}", Color.Yellow);
                        allPassed = false;
                        break;

                    case FilterStatus.GloballyBlacklisted:
                        AppendResult($"ERROR: {filterResult.Message}", Color.Red);
                        allPassed = false;
                        AppendResult("", Color.White);
                        AppendResult("===============================================================", Color.Gray);
                        AppendResult("FAILED: GLOBAL SKIPLIST MATCH - Release would be blocked", Color.Red);
                        AppendResult("===============================================================", Color.Gray);
                        return;

                    case FilterStatus.Error:
                        AppendResult($"ERROR: {filterResult.Message}", Color.Red);
                        allPassed = false;
                        break;

                    default:
                        AppendResult($"ERROR: {filterResult.Message}", Color.Red);
                        allPassed = false;
                        break;
                }

                AppendResult("", Color.White);

                if (filterResult.Status != FilterStatus.Success)
                {
                    AppendResult("===============================================================", Color.Gray);
                    AppendResult("FAILED: SITE FILTERING FAILED - Release would be blocked", Color.Red);
                    AppendResult("===============================================================", Color.Gray);
                    return;
                }

                // ═════════════════════════════════════════════════════════════
                // STEP 4 & 5: IMDB/TVMaze (uses existing helpers)
                // ═════════════════════════════════════════════════════════════
                var sections = currentSiteConfig["sections"] as JArray;
                var section = sections?.FirstOrDefault(s => s["irc_name"]?.ToString() == ircSection) as JObject;

                if (section != null)
                {
                    //  IMDBHelper
                    var imdb = section["imdb"] as JObject;
                    if (imdb?["enabled"]?.Value<bool>() == true)
                    {
                        AppendResult("[ STEP 4: IMDB Validation (Movies) ]\n", Color.Yellow);
                        var imdbPassed = await TestIMDB(releaseName, imdb);
                        if (!imdbPassed) allPassed = false;
                        AppendResult("", Color.White);
                    }

                    // TVMazeHelper
                    var tvmaze = section["tvmaze"] as JObject;
                    if (tvmaze?["enabled"]?.Value<bool>() == true)
                    {
                        AppendResult("[ STEP 5: TVMaze Validation (TV Shows) ]\n", Color.Yellow);
                        var tvmazePassed = await TestTVMaze(releaseName, tvmaze);
                        if (!tvmazePassed) allPassed = false;
                        AppendResult("", Color.White);
                    }
                }

                // FINAL RESULT
                AppendResult("===============================================================", Color.Gray);
                if (allPassed)
                {
                    AppendResult("SUCCESS: ALL CHECKS PASSED - Release would be raced!", Color.LimeGreen);
                }
                else
                {
                    AppendResult("FAILED: VALIDATION FAILED - Release would be blocked", Color.Red);
                }
                AppendResult("===============================================================", Color.Gray);
            }
            catch (Exception ex)
            {
                AppendResult($"\nError: {ex.Message}", Color.Red);
                if (MainApp.DebugEnabled)
                {
                    AppendResult($"Stack: {ex.StackTrace}", Color.Gray);
                }
            }
            finally
            {
                if (!IsDisposed && !Disposing && testButton != null && !testButton.IsDisposed)
                {
                    testButton.Enabled = true;
                    testButton.Text = "Run Full Test";
                }
            }
        }

        // USE EXISTING: IMDBHelper.EnrichReleaseInfo()
        private async Task<bool> TestIMDB(string releaseName, JObject config)
        {
            try
            {
                var parsedTitle = IMDBHelper.ExtractTitleFromRelease(releaseName);
                var parsedYear = IMDBHelper.ExtractYearFromRelease(releaseName);
                AppendResult($"  Search: {parsedTitle} ({parsedYear?.ToString() ?? "unknown year"})", Color.Gray);

                var releaseInfo = await IMDBHelper.EnrichReleaseInfo(releaseName);

                if (releaseInfo?.Movie == null)
                {
                    bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                    AppendResult("  No IMDB data found", Color.Red);
                    AppendResult($"  Fallback: {(fallback ? "ALLOW (continue)" : "BLOCK (stop)")}", fallback ? Color.Yellow : Color.Red);
                    return fallback;
                }

                var movie = releaseInfo.Movie;
                AppendResult($"  Found: {movie.Title} ({movie.Year})", Color.Green);
                AppendResult($"    IMDB: {movie.ImdbID} | Rating: {movie.ImdbRating}/10 | Votes: {movie.ImdbVotes:N0}", Color.White);
                AppendResult($"    Source: {movie.DataSource ?? "IMDb"}", Color.White);
                AppendResult($"    Genres: {string.Join(", ", movie.Genres ?? new List<string>())}", Color.White);
                AppendResult($"    Language: {movie.Language} | Country: {movie.Country}", Color.White);

                bool passed = true;

                double minRating = config["min_rating"]?.Value<double>() ?? 0;
                if (minRating > 0 && !movie.ImdbRating.HasValue)
                {
                    AppendResult($"    FAIL: No IMDb rating available while minimum {minRating} is configured", Color.Red);
                    passed = false;
                }
                else if (minRating > 0 && movie.ImdbRating.HasValue)
                {
                    if (movie.ImdbRating.Value >= minRating)
                        AppendResult($"    CHECK: Rating {movie.ImdbRating.Value} >= {minRating}", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Rating {movie.ImdbRating.Value} < {minRating}", Color.Red);
                        passed = false;
                    }
                }

                int minVotes = config["min_votes"]?.Value<int>() ?? 0;
                if (minVotes > 0 && !movie.ImdbVotes.HasValue)
                {
                    AppendResult($"    FAIL: No IMDb vote count available while minimum {minVotes:N0} is configured", Color.Red);
                    passed = false;
                }
                else if (minVotes > 0 && movie.ImdbVotes.HasValue)
                {
                    if (movie.ImdbVotes.Value >= minVotes)
                        AppendResult($"    CHECK: Votes {movie.ImdbVotes.Value:N0} >= {minVotes:N0}", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Votes {movie.ImdbVotes.Value:N0} < {minVotes:N0}", Color.Red);
                        passed = false;
                    }
                }

                if (config["only_english"]?.Value<bool>() == true)
                {
                    if (movie.Languages?.Any(l => l.Equals("English", StringComparison.OrdinalIgnoreCase)) == true)
                        AppendResult("    CHECK: English language", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Not English ({movie.Language})", Color.Red);
                        passed = false;
                    }
                }

                if (config["only_us_country"]?.Value<bool>() == true)
                {
                    if (movie.Countries?.Any(c => c.ToLower().Contains("united states")) == true)
                        AppendResult("    CHECK: US production", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Not US ({movie.Country})", Color.Red);
                        passed = false;
                    }
                }

                var allowedGenres = config["allowed_genres"]?.ToObject<List<string>>() ?? new List<string>();
                if (allowedGenres.Any())
                {
                    if (movie.Genres == null || !movie.Genres.Any())
                    {
                        AppendResult("    FAIL: No genre data available while genre allow-list is configured", Color.Red);
                        passed = false;
                    }
                    else if (movie.Genres.Any(g => allowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                        AppendResult($"    CHECK: Genre allowed", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Genre not allowed", Color.Red);
                        passed = false;
                    }
                }

                if (config["no_documentary"]?.Value<bool>() == true && HasGenre(movie.Genres, "Documentary"))
                {
                    AppendResult("    FAIL: Documentary genre blocked", Color.Red);
                    passed = false;
                }

                if (config["no_music"]?.Value<bool>() == true &&
                    (HasGenre(movie.Genres, "Music") || HasGenre(movie.Genres, "Musical")))
                {
                    AppendResult("    FAIL: Music/Musical genre blocked", Color.Red);
                    passed = false;
                }

                if (config["no_comedy"]?.Value<bool>() == true && HasGenre(movie.Genres, "Comedy"))
                {
                    AppendResult("    FAIL: Comedy genre blocked", Color.Red);
                    passed = false;
                }

                if (config["no_show"]?.Value<bool>() == true &&
                    !string.Equals(movie.Type, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    AppendResult($"    FAIL: IMDb title type '{movie.Type}' blocked by movies-only filter", Color.Red);
                    passed = false;
                }

                var blockedGenres = config["blocked_genres"]?.ToObject<List<string>>() ?? new List<string>();
                if (blockedGenres.Any() && movie.Genres != null)
                {
                    if (!movie.Genres.Any(g => blockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                        AppendResult($"    CHECK: No blocked genres", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Genre blocked", Color.Red);
                        passed = false;
                    }
                }

                AppendResult($"  Result: {(passed ? "PASS" : "FAIL")}", passed ? Color.LimeGreen : Color.Red);
                return passed;
            }
            catch (Exception ex)
            {
                AppendResult($"  Error: {ex.Message}", Color.Orange);
                bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                return fallback;
            }
        }

        // TVMazeHelper.EnrichReleaseInfo()
        private async Task<bool> TestTVMaze(string releaseName, JObject config)
        {
            try
            {
                var releaseInfo = await TVMazeHelper.EnrichReleaseInfo(releaseName);

                if (releaseInfo?.Show == null)
                {
                    bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                    AppendResult("  No TVMaze data found", Color.Red);
                    AppendResult($"  Fallback: {(fallback ? "ALLOW (continue)" : "BLOCK (stop)")}", fallback ? Color.Yellow : Color.Red);
                    return fallback;
                }

                var show = releaseInfo.Show;
                AppendResult($"  Found: {show.Name}", Color.Green);
                AppendResult($"    TVMaze ID: {show.Id} | Status: {show.Status}", Color.White);
                AppendResult($"    Rating: {show.Rating?.Average?.ToString("F1") ?? "N/A"}", Color.White);
                AppendResult($"    Type: {show.Type ?? "N/A"}", Color.White);
                AppendResult($"    Genres: {string.Join(", ", show.Genres ?? new List<string>())}", Color.White);
                AppendResult($"    Network: {show.Network?.Name ?? show.WebChannel?.Name ?? "N/A"}", Color.White);

                bool passed = true;

                if (config["skip_ended_shows"]?.Value<bool>() == true && show.Status == "Ended")
                {
                    AppendResult("    FAIL: Show has ended", Color.Red);
                    passed = false;
                }
                else if (show.Status != "Ended")
                {
                    AppendResult($"    CHECK: Show is {show.Status}", Color.Green);
                }

                double minRating = config["min_rating"]?.Value<double>() ?? 0;
                if (minRating > 0 && show.Rating?.Average == null)
                {
                    AppendResult($"    FAIL: No TVMaze rating available while minimum {minRating} is configured", Color.Red);
                    passed = false;
                }
                else if (minRating > 0 && show.Rating?.Average != null)
                {
                    if (show.Rating.Average >= minRating)
                        AppendResult($"    CHECK: Rating {show.Rating.Average:F1} >= {minRating}", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Rating {show.Rating.Average:F1} < {minRating}", Color.Red);
                        passed = false;
                    }
                }

                var allowedGenres = config["allowed_genres"]?.ToObject<List<string>>() ?? new List<string>();
                if (allowedGenres.Any())
                {
                    if (show.Genres == null || !show.Genres.Any())
                    {
                        AppendResult("    FAIL: No genre data available while genre allow-list is configured", Color.Red);
                        passed = false;
                    }
                    else if (show.Genres.Any(g => allowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                        AppendResult($"    CHECK: Genre allowed", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Genre not allowed", Color.Red);
                        passed = false;
                    }
                }

                var blockedGenres = config["blocked_genres"]?.ToObject<List<string>>() ?? new List<string>();
                if (blockedGenres.Any() && show.Genres != null)
                {
                    if (!show.Genres.Any(g => blockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                        AppendResult($"    CHECK: No blocked genres", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Genre blocked", Color.Red);
                        passed = false;
                    }
                }

                var allowedNetworks = config["allowed_networks"]?.ToObject<List<string>>() ?? new List<string>();
                if (allowedNetworks.Any())
                {
                    var network = show.Network?.Name ?? show.WebChannel?.Name ?? "";
                    if (allowedNetworks.Contains(network, StringComparer.OrdinalIgnoreCase))
                        AppendResult($"    CHECK: Network '{network}' allowed", Color.Green);
                    else
                    {
                        AppendResult($"    FAIL: Network '{network}' not allowed", Color.Red);
                        passed = false;
                    }
                }

                var allowedShowTypes = GetStringList(config, "allowed_show_types", "show_types", "allowed_types", "allowed_showtypes");
                if (allowedShowTypes.Any())
                {
                    var showType = show.Type ?? "";
                    if (string.IsNullOrWhiteSpace(showType))
                    {
                        AppendResult($"    FAIL: Show type missing while allowed types are configured: {string.Join(", ", allowedShowTypes)}", Color.Red);
                        passed = false;
                    }
                    else if (allowedShowTypes.Contains(showType, StringComparer.OrdinalIgnoreCase))
                    {
                        AppendResult($"    CHECK: Show type '{showType}' allowed", Color.Green);
                    }
                    else
                    {
                        AppendResult($"    FAIL: Show type '{showType}' not allowed", Color.Red);
                        passed = false;
                    }
                }

                AppendResult($"  Result: {(passed ? "PASS" : "FAIL")}", passed ? Color.LimeGreen : Color.Red);
                return passed;
            }
            catch (Exception ex)
            {
                AppendResult($"  Error: {ex.Message}", Color.Orange);
                bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                return fallback;
            }
        }

        private static bool HasGenre(IEnumerable<string> genres, string genre)
        {
            return genres?.Any(g => g.Equals(genre, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static List<string> GetStringList(JObject config, params string[] keys)
        {
            if (config == null)
                return new List<string>();

            foreach (var key in keys)
            {
                var token = config[key];
                if (token == null)
                    continue;

                if (token is JArray)
                    return token.ToObject<List<string>>() ?? new List<string>();

                var single = token.ToString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    return single
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }
            }

            return new List<string>();
        }

        private void AppendResult(string text, Color color)
        {
            // The async test can complete after the dialog was closed and disposed.
            if (IsDisposed || Disposing || resultsTextBox == null || resultsTextBox.IsDisposed)
                return;

            resultsTextBox.SelectionStart = resultsTextBox.TextLength;
            resultsTextBox.SelectionLength = 0;
            resultsTextBox.SelectionColor = color;
            resultsTextBox.AppendText(text + "\n");
            resultsTextBox.SelectionColor = resultsTextBox.ForeColor;
            resultsTextBox.ScrollToCaret();
        }
    }
}
