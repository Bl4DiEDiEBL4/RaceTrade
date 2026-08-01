using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace RaceTrade
{
    public partial class CbftpSyncForm : AntdUI.Window
    {
        private List<CbftpSite> fetchedSites;

        public CbftpSyncForm()
        {
            InitializeComponent();
            RaceTrade.ThemeManager.ApplyTheme(this);
            LoadCbftpServers();
        }

        private void LoadCbftpServers()
        {
            cbftpServerCombo.Items.Clear();

            string filePath = "cbftp/cbftp_config.json";
            if (!File.Exists(filePath))
            {
                statusLabel.Text = "No CBFTP servers configured. Please add a CBFTP server first.";
                statusLabel.ForeColor = Color.Red;
                return;
            }

            try
            {
                var jsonContent = File.ReadAllText(filePath);
                var config = JsonConvert.DeserializeObject<Config>(jsonContent);

                if (config?.CbftpServers == null || !config.CbftpServers.Any())
                {
                    statusLabel.Text = "No CBFTP servers found in configuration.";
                    statusLabel.ForeColor = Color.Red;
                    return;
                }

                foreach (var server in config.CbftpServers)
                {
                    cbftpServerCombo.Items.Add(server);
                }

                cbftpServerCombo.DisplayMember = "Id";
                if (cbftpServerCombo.Items.Count > 0)
                {
                    cbftpServerCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error loading CBFTP servers: {ex.Message}";
                statusLabel.ForeColor = Color.Red;
            }
        }

        private async void SyncButton_Click(object sender, EventArgs e)
        {
            if (cbftpServerCombo.SelectedItem == null)
            {
                DialogHelper.ShowWarning("Please select a CBFTP server first.");
                return;
            }

            var server = (CbftpServer)cbftpServerCombo.SelectedItem;

            // Show progress
            syncButton.Enabled = false;
            progressBar.Visible = true;
            statusLabel.Text = $"Connecting to {server.Host}:{server.Port}...";
            statusLabel.ForeColor = Color.Yellow;
            sitesListBox.Items.Clear();
            importButton.Enabled = false;

            try
            {
                // Decrypt password
                string password = SecureConfig.Decrypt(server.Password);

                // Fetch sites from CBFTP
                var result = await CbftpSync.FetchSitesFromCbftp(server.Host, server.Port, password);

                if (!result.IsSuccess)
                {
                    statusLabel.Text = $"Error: {result.ErrorMessage}";
                    statusLabel.ForeColor = Color.Red;
                    DialogHelper.ShowError($"Failed to sync from CBFTP:\n{result.ErrorMessage}");
                    return;
                }

                fetchedSites = result.Sites;

                if (!fetchedSites.Any())
                {
                    statusLabel.Text = "No sites found in CBFTP.";
                    statusLabel.ForeColor = Color.Orange;
                    return;
                }

                // Populate list and check all by default
                foreach (var site in fetchedSites)
                {
                    string display =
                        $"{site.Name} ({site.PrimaryAddress}:{site.Port}) - {site.Sections.Count} section(s)";
                    sitesListBox.Items.Add(display, true);
                }

                // Enable import button if at least one checked item
                importButton.Enabled = sitesListBox.CheckedItems.Count > 0;

                statusLabel.Text = $"Found {fetchedSites.Count} site(s). Check the sites you want to import.";
                statusLabel.ForeColor = Color.LimeGreen;
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
                statusLabel.ForeColor = Color.Red;
                LogManager.Exception(ex, "Error during CBFTP sync");
            }
            finally
            {
                syncButton.Enabled = true;
                progressBar.Visible = false;
            }
        }

        private void ImportButton_Click(object sender, EventArgs e)
        {
            if (sitesListBox.CheckedIndices.Count == 0)
            {
                DialogHelper.ShowWarning("Please select at least one site to import.");
                return;
            }

            int imported = 0;
            int skipped = 0;
            int errors = 0;
            var allSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (int index in sitesListBox.CheckedIndices)
            {
                var site = fetchedSites[index];

                try
                {
                    string fileName = $"{site.Name}.json";
                    string filePath = Path.Combine("sites", fileName);

                    if (File.Exists(filePath))
                    {
                        var result = DialogHelper.ShowConfirmation(
                            $"Site '{site.Name}' already exists. Overwrite?");
                        if (result != DialogResult.Yes)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    // Create site configuration WITHOUT server credentials
                    var siteConfig = new
                    {
                        site_settings = new
                        {
                            sitename = site.Name,
                            bot_name = "",
                            disable_site = site.Disabled,
                            pre_announce = "Site",
                            new_regex_pattern = @"\bNEW\b",
                            release_regex_pattern = @"\].s*(.*?)\s",
                            section_regex_pattern = @"\[(.*?)\]",
                            section_prefix = "[",
                            section_suffix = "]",
                            ignore_words = ""
                        },
                        race_sections_enabled = site.Sections.Select(s => s.Name).ToList(),
                        sections = site.Sections.Select(s => new
                        {
                            irc_name = s.Name,
                            tags = new[]
                            {
                                new
                                {
                                    map_cbftp_section = s.Name,
                                    trigger_regex = "",
                                    rules = new string[] { }
                                }
                            },
                            rules = new string[] { }
                        }).ToArray()
                    };

                    // Save to file
                    Directory.CreateDirectory("sites");
                    AtomicFile.WriteAllText(filePath, JsonConvert.SerializeObject(siteConfig, Formatting.Indented));
                    // Drop the cached copy, otherwise the racer keeps serving the
                    // pre-import config until the app is restarted.
                    SiteConfigManager.Invalidate(site.Name);

                    // Collect all section names for sections.json
                    foreach (var section in site.Sections)
                    {
                        allSections.Add(section.Name);
                    }

                    LogManager.Success($"Imported site: {site.Name}");
                    imported++;
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error importing site {site.Name}: {ex.Message}");
                    errors++;
                }
            }

            // Update sections/cbftp_sections.json with new sections
            if (allSections.Count > 0)
            {
                try
                {
                    UpdateSectionsJson(allSections);
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error updating cbftp_sections.json: {ex.Message}");
                }
            }

            // Refresh the racer's snapshot so imported sites are usable immediately.
            if (imported > 0)
            {
                try { RaceHelper.LoadAllSiteConfigs(); } catch { }
            }

            string summary = $"Import complete!\n\nImported: {imported}\nSkipped: {skipped}\nErrors: {errors}";

            if (errors == 0)
            {
                DialogHelper.ShowSuccess(summary);
            }
            else
            {
                DialogHelper.ShowWarning(summary);
            }

            if (imported > 0)
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        private void UpdateSectionsJson(HashSet<string> newSections)
        {
            string sectionsDir = "sections";
            string sectionsFile = Path.Combine(sectionsDir, "cbftp_sections.json");

            Directory.CreateDirectory(sectionsDir);

            SectionData sectionData;

            if (File.Exists(sectionsFile))
            {
                try
                {
                    var json = File.ReadAllText(sectionsFile);
                    sectionData = JsonConvert.DeserializeObject<SectionData>(json) ?? new SectionData
                    {
                        Sections = new Dictionary<string, string>(),
                        CbftpSections = new Dictionary<string, string>()
                    };
                }
                catch (Exception ex)
                {
                    LogManager.Warning($"Could not read existing cbftp_sections.json: {ex.Message}");
                    sectionData = new SectionData
                    {
                        Sections = new Dictionary<string, string>(),
                        CbftpSections = new Dictionary<string, string>()
                    };
                }
            }
            else
            {
                sectionData = new SectionData
                {
                    Sections = new Dictionary<string, string>(),
                    CbftpSections = new Dictionary<string, string>()
                };
            }

            int added = 0;
            foreach (var section in newSections)
            {
                if (!sectionData.CbftpSections.Values.Contains(section, StringComparer.OrdinalIgnoreCase))
                {
                    int nextId = sectionData.CbftpSections.Count + 1;
                    string key = $"cbftp_section{nextId}";

                    while (sectionData.CbftpSections.ContainsKey(key))
                    {
                        nextId++;
                        key = $"cbftp_section{nextId}";
                    }

                    sectionData.CbftpSections[key] = section;
                    added++;
                }
            }

            AtomicFile.WriteAllText(sectionsFile, JsonConvert.SerializeObject(sectionData, Formatting.Indented));

            LogManager.Success(
                $"Updated cbftp_sections.json: {added} new section(s) added, {sectionData.CbftpSections.Count} total");
        }

        // === Designer-wired event helpers ===

        private void cancelButton_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void selectAllButton_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < sitesListBox.Items.Count; i++)
            {
                sitesListBox.SetItemChecked(i, true);
            }
        }

        private void deselectAllButton_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < sitesListBox.Items.Count; i++)
            {
                sitesListBox.SetItemChecked(i, false);
            }
        }

        private void sitesListBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // ItemCheck fires before the check state changes, so use BeginInvoke
            this.BeginInvoke((Action)(() =>
                importButton.Enabled = sitesListBox.CheckedItems.Count > 0));
        }

    }
}
