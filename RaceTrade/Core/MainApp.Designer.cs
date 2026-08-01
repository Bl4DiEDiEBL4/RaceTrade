using System.Drawing;

namespace RaceTrade
{
    partial class MainApp
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainApp));
            this.sidebarPanel = new System.Windows.Forms.Panel();
            this.logoPanel = new System.Windows.Forms.Panel();
            this.titleLabel = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.statusCard = new AntdUI.Panel();
            this.statusTitleLabel = new System.Windows.Forms.Label();
            this.Enable_Disable_Racer_button = new System.Windows.Forms.Button();
            this.navMenuLabel = new System.Windows.Forms.Label();
            this.navDashboard = new System.Windows.Forms.Button();
            this.Ftp_button = new System.Windows.Forms.Button();
            this.OpenTabbedIRC = new System.Windows.Forms.Button();
            this.Pre_button = new System.Windows.Forms.Button();
            this.button1 = new System.Windows.Forms.Button();
            this.Help_button = new System.Windows.Forms.Button();
            this.Changelog_button = new System.Windows.Forms.Button();
            this.communityLinkLabel = new System.Windows.Forms.LinkLabel();
            this.contentPanel = new System.Windows.Forms.Panel();
            this.pageTitleLabel = new System.Windows.Forms.Label();
            this.lblDockLogs = new System.Windows.Forms.Label();
            this.cardCbftp = new AntdUI.Panel();
            this.cardCbftpTitle = new System.Windows.Forms.Label();
            this.EditDropdown_cbftp_comboBox = new System.Windows.Forms.ComboBox();
            this.Add_Cbftp_button = new AntdUI.Button();
            this.Add_Ccbftp_Sections = new AntdUI.Button();
            this.Add_cbftp_site = new AntdUI.Button();
            this.Cbftp_Edit_Site = new AntdUI.Button();
            this.Sync_From_Cbftp_Button = new AntdUI.Button();
            this.cardSites = new AntdUI.Panel();
            this.cardSitesTitle = new System.Windows.Forms.Label();
            this.add_sites_button = new AntdUI.Button();
            this.Edit_sites_button = new AntdUI.Button();
            this.cardPrebots = new AntdUI.Panel();
            this.cardPrebotsTitle = new System.Windows.Forms.Label();
            this.Add_PreBot_button = new AntdUI.Button();
            this.Prebot_edit_button = new AntdUI.Button();
            this.buttonImportPredb = new AntdUI.Button();
            this.cardLogs = new AntdUI.Panel();
            this.cardLogsTitle = new System.Windows.Forms.Label();
            this.ToggleCBFTPLog = new AntdUI.Button();
            this.ToggleIRCLog = new AntdUI.Button();
            this.ToggleApplicationLog = new AntdUI.Button();
            this.ToggleRaceLog = new AntdUI.Button();
            this.cardTools = new AntdUI.Panel();
            this.cardToolsTitle = new System.Windows.Forms.Label();
            this.button2 = new AntdUI.Button();
            this.blacklist_add = new AntdUI.Button();
            this.exitButton = new AntdUI.Button();
            this.sidebarPanel.SuspendLayout();
            this.statusCard.SuspendLayout();
            this.contentPanel.SuspendLayout();
            this.cardCbftp.SuspendLayout();
            this.cardSites.SuspendLayout();
            this.cardPrebots.SuspendLayout();
            this.cardLogs.SuspendLayout();
            this.cardTools.SuspendLayout();
            this.SuspendLayout();
            // 
            // sidebarPanel
            // 
            this.sidebarPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(9)))), ((int)(((byte)(11)))), ((int)(((byte)(17)))));
            this.sidebarPanel.Controls.Add(this.logoPanel);
            this.sidebarPanel.Controls.Add(this.titleLabel);
            this.sidebarPanel.Controls.Add(this.label1);
            this.sidebarPanel.Controls.Add(this.statusCard);
            this.sidebarPanel.Controls.Add(this.navMenuLabel);
            this.sidebarPanel.Controls.Add(this.navDashboard);
            this.sidebarPanel.Controls.Add(this.Ftp_button);
            this.sidebarPanel.Controls.Add(this.OpenTabbedIRC);
            this.sidebarPanel.Controls.Add(this.Pre_button);
            this.sidebarPanel.Controls.Add(this.button1);
            this.sidebarPanel.Controls.Add(this.Help_button);
            this.sidebarPanel.Controls.Add(this.Changelog_button);
            this.sidebarPanel.Controls.Add(this.communityLinkLabel);
            this.sidebarPanel.Dock = System.Windows.Forms.DockStyle.Left;
            this.sidebarPanel.Location = new System.Drawing.Point(0, 0);
            this.sidebarPanel.Name = "sidebarPanel";
            this.sidebarPanel.Size = new System.Drawing.Size(244, 584);
            this.sidebarPanel.TabIndex = 0;
            // 
            // logoPanel
            // 
            this.logoPanel.BackColor = System.Drawing.Color.Transparent;
            this.logoPanel.Location = new System.Drawing.Point(22, 26);
            this.logoPanel.Name = "logoPanel";
            this.logoPanel.Size = new System.Drawing.Size(24, 24);
            this.logoPanel.TabIndex = 0;
            // 
            // titleLabel
            // 
            this.titleLabel.AutoSize = true;
            this.titleLabel.BackColor = System.Drawing.Color.Transparent;
            this.titleLabel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(228)))), ((int)(((byte)(233)))), ((int)(((byte)(242)))));
            this.titleLabel.Location = new System.Drawing.Point(56, 28);
            this.titleLabel.Name = "titleLabel";
            this.titleLabel.Size = new System.Drawing.Size(72, 15);
            this.titleLabel.TabIndex = 1;
            this.titleLabel.Text = "RACETRADE";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.BackColor = System.Drawing.Color.Transparent;
            this.label1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.label1.Location = new System.Drawing.Point(24, 60);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(44, 15);
            this.label1.TabIndex = 2;
            this.label1.Text = "v1.0.9b";
            // 
            // statusCard
            // 
            this.statusCard.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.statusCard.Controls.Add(this.statusTitleLabel);
            this.statusCard.Controls.Add(this.Enable_Disable_Racer_button);
            this.statusCard.Location = new System.Drawing.Point(20, 92);
            this.statusCard.Name = "statusCard";
            this.statusCard.Size = new System.Drawing.Size(204, 134);
            this.statusCard.TabIndex = 3;
            // 
            // statusTitleLabel
            // 
            this.statusTitleLabel.BackColor = System.Drawing.Color.Transparent;
            this.statusTitleLabel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.statusTitleLabel.Location = new System.Drawing.Point(16, 12);
            this.statusTitleLabel.Name = "statusTitleLabel";
            this.statusTitleLabel.Size = new System.Drawing.Size(172, 16);
            this.statusTitleLabel.TabIndex = 0;
            this.statusTitleLabel.Text = "TRADER STATUS";
            // 
            // Enable_Disable_Racer_button
            // 
            this.Enable_Disable_Racer_button.FlatAppearance.BorderSize = 0;
            this.Enable_Disable_Racer_button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.Enable_Disable_Racer_button.Location = new System.Drawing.Point(16, 36);
            this.Enable_Disable_Racer_button.Name = "Enable_Disable_Racer_button";
            this.Enable_Disable_Racer_button.Size = new System.Drawing.Size(172, 52);
            this.Enable_Disable_Racer_button.TabIndex = 1;
            this.Enable_Disable_Racer_button.Text = "Start";
            this.Enable_Disable_Racer_button.UseVisualStyleBackColor = true;
            this.Enable_Disable_Racer_button.Click += new System.EventHandler(this.button3_Click);
            // 
            // navMenuLabel
            // 
            this.navMenuLabel.AutoSize = true;
            this.navMenuLabel.BackColor = System.Drawing.Color.Transparent;
            this.navMenuLabel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(96)))), ((int)(((byte)(104)))), ((int)(((byte)(120)))));
            this.navMenuLabel.Location = new System.Drawing.Point(24, 236);
            this.navMenuLabel.Name = "navMenuLabel";
            this.navMenuLabel.Size = new System.Drawing.Size(41, 15);
            this.navMenuLabel.TabIndex = 4;
            this.navMenuLabel.Text = "MENU";
            // 
            // navDashboard
            // 
            this.navDashboard.FlatAppearance.BorderSize = 0;
            this.navDashboard.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.navDashboard.Location = new System.Drawing.Point(14, 258);
            this.navDashboard.Name = "navDashboard";
            this.navDashboard.Size = new System.Drawing.Size(216, 36);
            this.navDashboard.TabIndex = 5;
            this.navDashboard.Text = "Dashboard";
            this.navDashboard.UseVisualStyleBackColor = true;
            // 
            // Ftp_button
            // 
            this.Ftp_button.FlatAppearance.BorderSize = 0;
            this.Ftp_button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.Ftp_button.Location = new System.Drawing.Point(14, 298);
            this.Ftp_button.Name = "Ftp_button";
            this.Ftp_button.Size = new System.Drawing.Size(216, 36);
            this.Ftp_button.TabIndex = 6;
            this.Ftp_button.Text = "FXP Client";
            this.Ftp_button.UseVisualStyleBackColor = true;
            this.Ftp_button.Click += new System.EventHandler(this.Ftp_button_Click);
            // 
            // OpenTabbedIRC
            // 
            this.OpenTabbedIRC.FlatAppearance.BorderSize = 0;
            this.OpenTabbedIRC.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.OpenTabbedIRC.Location = new System.Drawing.Point(14, 338);
            this.OpenTabbedIRC.Name = "OpenTabbedIRC";
            this.OpenTabbedIRC.Size = new System.Drawing.Size(216, 36);
            this.OpenTabbedIRC.TabIndex = 7;
            this.OpenTabbedIRC.Text = "Chat";
            this.OpenTabbedIRC.UseVisualStyleBackColor = true;
            this.OpenTabbedIRC.Click += new System.EventHandler(this.OpenTabbedIRC_Click_1);
            // 
            // Pre_button
            // 
            this.Pre_button.FlatAppearance.BorderSize = 0;
            this.Pre_button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.Pre_button.Location = new System.Drawing.Point(14, 378);
            this.Pre_button.Name = "Pre_button";
            this.Pre_button.Size = new System.Drawing.Size(216, 36);
            this.Pre_button.TabIndex = 8;
            this.Pre_button.Text = "Pre";
            this.Pre_button.UseVisualStyleBackColor = true;
            this.Pre_button.Click += new System.EventHandler(this.Pre_button_Click);
            // 
            // button1
            // 
            this.button1.FlatAppearance.BorderSize = 0;
            this.button1.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.button1.Location = new System.Drawing.Point(14, 418);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(216, 36);
            this.button1.TabIndex = 9;
            this.button1.Text = "Settings";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // Help_button
            // 
            this.Help_button.FlatAppearance.BorderSize = 0;
            this.Help_button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.Help_button.Location = new System.Drawing.Point(14, 458);
            this.Help_button.Name = "Help_button";
            this.Help_button.Size = new System.Drawing.Size(216, 36);
            this.Help_button.TabIndex = 10;
            this.Help_button.Text = "Help";
            this.Help_button.UseVisualStyleBackColor = true;
            this.Help_button.Click += new System.EventHandler(this.Help_button_Click_1);
            // 
            // Changelog_button
            // 
            this.Changelog_button.FlatAppearance.BorderSize = 0;
            this.Changelog_button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.Changelog_button.Location = new System.Drawing.Point(14, 498);
            this.Changelog_button.Name = "Changelog_button";
            this.Changelog_button.Size = new System.Drawing.Size(216, 36);
            this.Changelog_button.TabIndex = 11;
            this.Changelog_button.Text = "Changelog";
            this.Changelog_button.UseVisualStyleBackColor = true;
            this.Changelog_button.Click += new System.EventHandler(this.Changelog_button_Click);
            // 
            // communityLinkLabel
            // 
            this.communityLinkLabel.ActiveLinkColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(229)))), ((int)(((byte)(214)))));
            this.communityLinkLabel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(9)))), ((int)(((byte)(11)))), ((int)(((byte)(17)))));
            this.communityLinkLabel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.communityLinkLabel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(229)))), ((int)(((byte)(214)))));
            this.communityLinkLabel.LinkBehavior = System.Windows.Forms.LinkBehavior.HoverUnderline;
            this.communityLinkLabel.LinkColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(229)))), ((int)(((byte)(214)))));
            this.communityLinkLabel.Location = new System.Drawing.Point(0, 538);
            this.communityLinkLabel.Name = "communityLinkLabel";
            this.communityLinkLabel.Padding = new System.Windows.Forms.Padding(24, 0, 0, 0);
            this.communityLinkLabel.Size = new System.Drawing.Size(244, 46);
            this.communityLinkLabel.TabIndex = 12;
            this.communityLinkLabel.TabStop = true;
            this.communityLinkLabel.Text = "linknet / #racetrade";
            this.communityLinkLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.communityLinkLabel.VisitedLinkColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(168)))), ((int)(((byte)(255)))));
            // 
            // contentPanel
            // 
            this.contentPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(22)))), ((int)(((byte)(26)))), ((int)(((byte)(36)))));
            this.contentPanel.Controls.Add(this.pageTitleLabel);
            this.contentPanel.Controls.Add(this.lblDockLogs);
            this.contentPanel.Controls.Add(this.cardCbftp);
            this.contentPanel.Controls.Add(this.cardSites);
            this.contentPanel.Controls.Add(this.cardPrebots);
            this.contentPanel.Controls.Add(this.cardLogs);
            this.contentPanel.Controls.Add(this.cardTools);
            this.contentPanel.Controls.Add(this.exitButton);
            this.contentPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.contentPanel.Location = new System.Drawing.Point(244, 0);
            this.contentPanel.Name = "contentPanel";
            this.contentPanel.Size = new System.Drawing.Size(720, 584);
            this.contentPanel.TabIndex = 1;
            // 
            // pageTitleLabel
            // 
            this.pageTitleLabel.AutoSize = true;
            this.pageTitleLabel.BackColor = System.Drawing.Color.Transparent;
            this.pageTitleLabel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(228)))), ((int)(((byte)(233)))), ((int)(((byte)(242)))));
            this.pageTitleLabel.Location = new System.Drawing.Point(12, 44);
            this.pageTitleLabel.Name = "pageTitleLabel";
            this.pageTitleLabel.Size = new System.Drawing.Size(64, 15);
            this.pageTitleLabel.TabIndex = 0;
            this.pageTitleLabel.Text = "Dashboard";
            // 
            // lblDockLogs
            // 
            this.lblDockLogs.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblDockLogs.AutoSize = true;
            this.lblDockLogs.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.lblDockLogs.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(229)))), ((int)(((byte)(214)))));
            this.lblDockLogs.Location = new System.Drawing.Point(606, 16);
            this.lblDockLogs.Name = "lblDockLogs";
            this.lblDockLogs.Padding = new System.Windows.Forms.Padding(12, 6, 12, 6);
            this.lblDockLogs.Size = new System.Drawing.Size(102, 27);
            this.lblDockLogs.TabIndex = 2;
            this.lblDockLogs.Text = "Logs: Docked";
            // 
            // cardCbftp
            // 
            this.cardCbftp.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.cardCbftp.Controls.Add(this.cardCbftpTitle);
            this.cardCbftp.Controls.Add(this.EditDropdown_cbftp_comboBox);
            this.cardCbftp.Controls.Add(this.Add_Cbftp_button);
            this.cardCbftp.Controls.Add(this.Add_Ccbftp_Sections);
            this.cardCbftp.Controls.Add(this.Add_cbftp_site);
            this.cardCbftp.Controls.Add(this.Cbftp_Edit_Site);
            this.cardCbftp.Controls.Add(this.Sync_From_Cbftp_Button);
            this.cardCbftp.Location = new System.Drawing.Point(15, 104);
            this.cardCbftp.Name = "cardCbftp";
            this.cardCbftp.Size = new System.Drawing.Size(336, 202);
            this.cardCbftp.TabIndex = 3;
            // 
            // cardCbftpTitle
            // 
            this.cardCbftpTitle.BackColor = System.Drawing.Color.Transparent;
            this.cardCbftpTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.cardCbftpTitle.Location = new System.Drawing.Point(14, 12);
            this.cardCbftpTitle.Name = "cardCbftpTitle";
            this.cardCbftpTitle.Size = new System.Drawing.Size(308, 18);
            this.cardCbftpTitle.TabIndex = 0;
            this.cardCbftpTitle.Text = "CBFTP SERVERS";
            // 
            // EditDropdown_cbftp_comboBox
            // 
            this.EditDropdown_cbftp_comboBox.FormattingEnabled = true;
            this.EditDropdown_cbftp_comboBox.Location = new System.Drawing.Point(14, 38);
            this.EditDropdown_cbftp_comboBox.Name = "EditDropdown_cbftp_comboBox";
            this.EditDropdown_cbftp_comboBox.Size = new System.Drawing.Size(308, 23);
            this.EditDropdown_cbftp_comboBox.TabIndex = 1;
            this.EditDropdown_cbftp_comboBox.SelectedIndexChanged += new System.EventHandler(this.EditDropdown_cbftp_comboBox_SelectedIndexChanged);
            // 
            // Add_Cbftp_button
            // 
            this.Add_Cbftp_button.Location = new System.Drawing.Point(14, 68);
            this.Add_Cbftp_button.Name = "Add_Cbftp_button";
            this.Add_Cbftp_button.Size = new System.Drawing.Size(148, 28);
            this.Add_Cbftp_button.TabIndex = 2;
            this.Add_Cbftp_button.Text = "Add Server";
            this.Add_Cbftp_button.Click += new System.EventHandler(this.Add_Cbftp_button1_Click);
            // 
            // Add_Ccbftp_Sections
            // 
            this.Add_Ccbftp_Sections.Location = new System.Drawing.Point(174, 68);
            this.Add_Ccbftp_Sections.Name = "Add_Ccbftp_Sections";
            this.Add_Ccbftp_Sections.Size = new System.Drawing.Size(148, 28);
            this.Add_Ccbftp_Sections.TabIndex = 3;
            this.Add_Ccbftp_Sections.Text = "Sections";
            this.Add_Ccbftp_Sections.Click += new System.EventHandler(this.button2_Click);
            // 
            // Add_cbftp_site
            // 
            this.Add_cbftp_site.Location = new System.Drawing.Point(14, 102);
            this.Add_cbftp_site.Name = "Add_cbftp_site";
            this.Add_cbftp_site.Size = new System.Drawing.Size(148, 28);
            this.Add_cbftp_site.TabIndex = 4;
            this.Add_cbftp_site.Text = "Add CBFTP Site";
            this.Add_cbftp_site.Click += new System.EventHandler(this.Add_cbftp_site_Click);
            // 
            // Cbftp_Edit_Site
            // 
            this.Cbftp_Edit_Site.Location = new System.Drawing.Point(174, 102);
            this.Cbftp_Edit_Site.Name = "Cbftp_Edit_Site";
            this.Cbftp_Edit_Site.Size = new System.Drawing.Size(148, 28);
            this.Cbftp_Edit_Site.TabIndex = 5;
            this.Cbftp_Edit_Site.Text = "Edit CBFTP Site";
            this.Cbftp_Edit_Site.Click += new System.EventHandler(this.Cbftp_Edit_Site_Click);
            // 
            // Sync_From_Cbftp_Button
            // 
            this.Sync_From_Cbftp_Button.Location = new System.Drawing.Point(14, 136);
            this.Sync_From_Cbftp_Button.Name = "Sync_From_Cbftp_Button";
            this.Sync_From_Cbftp_Button.Size = new System.Drawing.Size(148, 28);
            this.Sync_From_Cbftp_Button.TabIndex = 6;
            this.Sync_From_Cbftp_Button.Text = "Sync Sites";
            this.Sync_From_Cbftp_Button.Click += new System.EventHandler(this.Sync_From_Cbftp_Button_Click);
            // 
            // cardSites
            // 
            this.cardSites.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.cardSites.Controls.Add(this.cardSitesTitle);
            this.cardSites.Controls.Add(this.add_sites_button);
            this.cardSites.Controls.Add(this.Edit_sites_button);
            this.cardSites.Location = new System.Drawing.Point(367, 104);
            this.cardSites.Name = "cardSites";
            this.cardSites.Size = new System.Drawing.Size(336, 78);
            this.cardSites.TabIndex = 4;
            // 
            // cardSitesTitle
            // 
            this.cardSitesTitle.BackColor = System.Drawing.Color.Transparent;
            this.cardSitesTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.cardSitesTitle.Location = new System.Drawing.Point(14, 12);
            this.cardSitesTitle.Name = "cardSitesTitle";
            this.cardSitesTitle.Size = new System.Drawing.Size(308, 18);
            this.cardSitesTitle.TabIndex = 0;
            this.cardSitesTitle.Text = "SITES";
            // 
            // add_sites_button
            // 
            this.add_sites_button.Location = new System.Drawing.Point(14, 42);
            this.add_sites_button.Name = "add_sites_button";
            this.add_sites_button.Size = new System.Drawing.Size(148, 28);
            this.add_sites_button.TabIndex = 1;
            this.add_sites_button.Text = "Add";
            this.add_sites_button.Click += new System.EventHandler(this.add_sites_button_Click);
            // 
            // Edit_sites_button
            // 
            this.Edit_sites_button.Location = new System.Drawing.Point(174, 42);
            this.Edit_sites_button.Name = "Edit_sites_button";
            this.Edit_sites_button.Size = new System.Drawing.Size(148, 28);
            this.Edit_sites_button.TabIndex = 2;
            this.Edit_sites_button.Text = "Edit";
            this.Edit_sites_button.Click += new System.EventHandler(this.Edit_sites_button_Click_1);
            // 
            // cardPrebots
            // 
            this.cardPrebots.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.cardPrebots.Controls.Add(this.cardPrebotsTitle);
            this.cardPrebots.Controls.Add(this.Add_PreBot_button);
            this.cardPrebots.Controls.Add(this.Prebot_edit_button);
            this.cardPrebots.Controls.Add(this.buttonImportPredb);
            this.cardPrebots.Location = new System.Drawing.Point(367, 192);
            this.cardPrebots.Name = "cardPrebots";
            this.cardPrebots.Size = new System.Drawing.Size(336, 114);
            this.cardPrebots.TabIndex = 5;
            // 
            // cardPrebotsTitle
            // 
            this.cardPrebotsTitle.BackColor = System.Drawing.Color.Transparent;
            this.cardPrebotsTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.cardPrebotsTitle.Location = new System.Drawing.Point(14, 12);
            this.cardPrebotsTitle.Name = "cardPrebotsTitle";
            this.cardPrebotsTitle.Size = new System.Drawing.Size(308, 18);
            this.cardPrebotsTitle.TabIndex = 0;
            this.cardPrebotsTitle.Text = "PREBOTS";
            // 
            // Add_PreBot_button
            // 
            this.Add_PreBot_button.Location = new System.Drawing.Point(14, 42);
            this.Add_PreBot_button.Name = "Add_PreBot_button";
            this.Add_PreBot_button.Size = new System.Drawing.Size(148, 28);
            this.Add_PreBot_button.TabIndex = 1;
            this.Add_PreBot_button.Text = "Add";
            this.Add_PreBot_button.Click += new System.EventHandler(this.Add_PreBot_button_Click);
            // 
            // Prebot_edit_button
            // 
            this.Prebot_edit_button.Location = new System.Drawing.Point(174, 42);
            this.Prebot_edit_button.Name = "Prebot_edit_button";
            this.Prebot_edit_button.Size = new System.Drawing.Size(148, 28);
            this.Prebot_edit_button.TabIndex = 2;
            this.Prebot_edit_button.Text = "Edit";
            this.Prebot_edit_button.Click += new System.EventHandler(this.Prebot_edit_button_Click);
            // 
            // buttonImportPredb
            // 
            this.buttonImportPredb.Location = new System.Drawing.Point(14, 76);
            this.buttonImportPredb.Name = "buttonImportPredb";
            this.buttonImportPredb.Size = new System.Drawing.Size(148, 28);
            this.buttonImportPredb.TabIndex = 3;
            this.buttonImportPredb.Text = "Import";
            this.buttonImportPredb.Click += new System.EventHandler(this.buttonImportPredb_Click);
            // 
            // cardLogs
            // 
            this.cardLogs.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.cardLogs.Controls.Add(this.cardLogsTitle);
            this.cardLogs.Controls.Add(this.ToggleCBFTPLog);
            this.cardLogs.Controls.Add(this.ToggleIRCLog);
            this.cardLogs.Controls.Add(this.ToggleApplicationLog);
            this.cardLogs.Controls.Add(this.ToggleRaceLog);
            this.cardLogs.Location = new System.Drawing.Point(15, 326);
            this.cardLogs.Name = "cardLogs";
            this.cardLogs.Size = new System.Drawing.Size(336, 112);
            this.cardLogs.TabIndex = 6;
            // 
            // cardLogsTitle
            // 
            this.cardLogsTitle.BackColor = System.Drawing.Color.Transparent;
            this.cardLogsTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.cardLogsTitle.Location = new System.Drawing.Point(14, 12);
            this.cardLogsTitle.Name = "cardLogsTitle";
            this.cardLogsTitle.Size = new System.Drawing.Size(308, 18);
            this.cardLogsTitle.TabIndex = 0;
            this.cardLogsTitle.Text = "LOGS";
            // 
            // ToggleCBFTPLog
            // 
            this.ToggleCBFTPLog.Location = new System.Drawing.Point(14, 40);
            this.ToggleCBFTPLog.Name = "ToggleCBFTPLog";
            this.ToggleCBFTPLog.Size = new System.Drawing.Size(146, 28);
            this.ToggleCBFTPLog.TabIndex = 1;
            this.ToggleCBFTPLog.Text = "Cbftp Log";
            this.ToggleCBFTPLog.Click += new System.EventHandler(this.ToggleCBFTPLog_Click);
            // 
            // ToggleIRCLog
            // 
            this.ToggleIRCLog.Location = new System.Drawing.Point(176, 40);
            this.ToggleIRCLog.Name = "ToggleIRCLog";
            this.ToggleIRCLog.Size = new System.Drawing.Size(146, 28);
            this.ToggleIRCLog.TabIndex = 2;
            this.ToggleIRCLog.Text = "IRC Log";
            this.ToggleIRCLog.Click += new System.EventHandler(this.ToggleIRCLog_Click);
            // 
            // ToggleApplicationLog
            // 
            this.ToggleApplicationLog.Location = new System.Drawing.Point(14, 74);
            this.ToggleApplicationLog.Name = "ToggleApplicationLog";
            this.ToggleApplicationLog.Size = new System.Drawing.Size(146, 28);
            this.ToggleApplicationLog.TabIndex = 3;
            this.ToggleApplicationLog.Text = "App";
            this.ToggleApplicationLog.Click += new System.EventHandler(this.ToggleApplicationLog_Click);
            // 
            // ToggleRaceLog
            // 
            this.ToggleRaceLog.Location = new System.Drawing.Point(176, 74);
            this.ToggleRaceLog.Name = "ToggleRaceLog";
            this.ToggleRaceLog.Size = new System.Drawing.Size(146, 28);
            this.ToggleRaceLog.TabIndex = 4;
            this.ToggleRaceLog.Text = "Race";
            this.ToggleRaceLog.Click += new System.EventHandler(this.ToggleRaceLog_Click);
            // 
            // cardTools
            // 
            this.cardTools.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(33)))), ((int)(((byte)(38)))), ((int)(((byte)(50)))));
            this.cardTools.Controls.Add(this.cardToolsTitle);
            this.cardTools.Controls.Add(this.button2);
            this.cardTools.Controls.Add(this.blacklist_add);
            this.cardTools.Location = new System.Drawing.Point(367, 326);
            this.cardTools.Name = "cardTools";
            this.cardTools.Size = new System.Drawing.Size(336, 112);
            this.cardTools.TabIndex = 7;
            // 
            // cardToolsTitle
            // 
            this.cardToolsTitle.BackColor = System.Drawing.Color.Transparent;
            this.cardToolsTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(160)))), ((int)(((byte)(178)))));
            this.cardToolsTitle.Location = new System.Drawing.Point(14, 12);
            this.cardToolsTitle.Name = "cardToolsTitle";
            this.cardToolsTitle.Size = new System.Drawing.Size(308, 18);
            this.cardToolsTitle.TabIndex = 0;
            this.cardToolsTitle.Text = "TOOLS";
            // 
            // button2
            // 
            this.button2.Location = new System.Drawing.Point(14, 40);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(146, 28);
            this.button2.TabIndex = 1;
            this.button2.Text = "Test Release";
            this.button2.Click += new System.EventHandler(this.button2_Click_2);
            // 
            // blacklist_add
            // 
            this.blacklist_add.Location = new System.Drawing.Point(176, 40);
            this.blacklist_add.Name = "blacklist_add";
            this.blacklist_add.Size = new System.Drawing.Size(146, 28);
            this.blacklist_add.TabIndex = 2;
            this.blacklist_add.Text = "Blacklist";
            this.blacklist_add.Click += new System.EventHandler(this.blacklist_add_Click);
            // 
            // exitButton
            // 
            this.exitButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.exitButton.Location = new System.Drawing.Point(573, 538);
            this.exitButton.Name = "exitButton";
            this.exitButton.Size = new System.Drawing.Size(130, 34);
            this.exitButton.TabIndex = 8;
            this.exitButton.Text = "Exit";
            this.exitButton.Click += new System.EventHandler(this.exitButton_Click_1);
            // 
            // MainApp
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(22)))), ((int)(((byte)(26)))), ((int)(((byte)(36)))));
            this.ClientSize = new System.Drawing.Size(964, 584);
            this.Controls.Add(this.contentPanel);
            this.Controls.Add(this.sidebarPanel);
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MinimumSize = new System.Drawing.Size(964, 560);
            this.Name = "MainApp";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "RaceTrade (2026) v1.0.9b  ▂▃▅▇█▓▒░۩۞۩ HiGH VOLTAGE ۩۞۩░▒▓█▇▅▃▂ ";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.sidebarPanel.ResumeLayout(false);
            this.sidebarPanel.PerformLayout();
            this.statusCard.ResumeLayout(false);
            this.contentPanel.ResumeLayout(false);
            this.contentPanel.PerformLayout();
            this.cardCbftp.ResumeLayout(false);
            this.cardSites.ResumeLayout(false);
            this.cardPrebots.ResumeLayout(false);
            this.cardLogs.ResumeLayout(false);
            this.cardTools.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Panel sidebarPanel;
        private System.Windows.Forms.Panel logoPanel;
        private System.Windows.Forms.Label titleLabel;
        private System.Windows.Forms.Label label1;
        private AntdUI.Panel statusCard;
        private System.Windows.Forms.Label statusTitleLabel;
        private System.Windows.Forms.Button Enable_Disable_Racer_button;
        private System.Windows.Forms.Label navMenuLabel;
        private System.Windows.Forms.Button navDashboard;
        private System.Windows.Forms.Button Ftp_button;
        private System.Windows.Forms.Button OpenTabbedIRC;
        private System.Windows.Forms.Button Pre_button;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.Button Help_button;
        private System.Windows.Forms.Button Changelog_button;
        private System.Windows.Forms.LinkLabel communityLinkLabel;
        private System.Windows.Forms.Panel contentPanel;
        private System.Windows.Forms.Label pageTitleLabel;
        private System.Windows.Forms.Label lblDockLogs;
        private AntdUI.Panel cardCbftp;
        private System.Windows.Forms.Label cardCbftpTitle;
        private System.Windows.Forms.ComboBox EditDropdown_cbftp_comboBox;
        private AntdUI.Button Add_Cbftp_button;
        private AntdUI.Button Add_Ccbftp_Sections;
        private AntdUI.Button Add_cbftp_site;
        private AntdUI.Button Cbftp_Edit_Site;
        private AntdUI.Button Sync_From_Cbftp_Button;
        private AntdUI.Panel cardSites;
        private System.Windows.Forms.Label cardSitesTitle;
        private AntdUI.Button add_sites_button;
        private AntdUI.Button Edit_sites_button;
        private AntdUI.Panel cardPrebots;
        private System.Windows.Forms.Label cardPrebotsTitle;
        private AntdUI.Button Add_PreBot_button;
        private AntdUI.Button Prebot_edit_button;
        private AntdUI.Button buttonImportPredb;
        private AntdUI.Panel cardLogs;
        private System.Windows.Forms.Label cardLogsTitle;
        private AntdUI.Button ToggleCBFTPLog;
        private AntdUI.Button ToggleIRCLog;
        private AntdUI.Button ToggleApplicationLog;
        private AntdUI.Button ToggleRaceLog;
        private AntdUI.Panel cardTools;
        private System.Windows.Forms.Label cardToolsTitle;
        private AntdUI.Button button2;
        private AntdUI.Button blacklist_add;
        private AntdUI.Button exitButton;
    }
}
