using Microsoft.Data.Sqlite;
using RaceTrade;
using RaceTrader;
using SQLitePCL;
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace WinFormsTraderApp
{
    static class Program
    {
        [STAThread]  // Required for Windows Forms applications
        static void Main()
        {
            // All config/db paths in the app are relative ("sites", "cbftp", "db", ...).
            // Anchor them to the exe directory so launching from a shortcut/console with
            // a different working directory doesn't create a second empty config tree.
            System.IO.Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            ConfigureNetworkForLowLatency();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ConfigureAntdUi();

            try
            {
                Console.WriteLine("Creating required directories...");
                CreateRequiredDirectories();
                Console.WriteLine("Directories created successfully.");

                Console.WriteLine("Initializing database...");
                SQLiteHelper.InitializeDatabase();
                IMDBCache.Initialize();
                TVMazeCache.Initialize();
                Console.WriteLine("Database initialized successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during initialization: {ex.Message}");
                MessageBox.Show($"Error during initialization: {ex.Message}", "Initialization Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RaceHelper.LoadAllSiteConfigs();
            Application.Run(new RaceTrade.MainApp());
        }

        /// <summary>
        /// Race latency depends entirely on how fast the cbftp command leaves this
        /// process, so the .NET Framework network defaults have to be turned off:
        ///  - Expect100Continue: makes every POST wait a full round trip (or the
        ///    350ms ContinueTimeout) before the body is even sent.
        ///  - Nagle: buffers our small POST body waiting for more data, interacting
        ///    with delayed ACK for up to ~200ms of pure delay.
        ///  - DefaultConnectionLimit (2): serializes concurrent races to the same
        ///    cbftp host behind two connections.
        /// Also enlarge the static Regex cache (default 15) since the announce path
        /// rotates through more distinct patterns than that, causing re-parsing.
        /// </summary>
        private static void ConfigureNetworkForLowLatency()
        {
            try
            {
                System.Net.ServicePointManager.Expect100Continue = false;
                System.Net.ServicePointManager.UseNagleAlgorithm = false;
                System.Net.ServicePointManager.DefaultConnectionLimit = 64;
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                System.Text.RegularExpressions.Regex.CacheSize = 64;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply low-latency network settings: {ex.Message}");
            }
        }

        private static void ConfigureAntdUi()
        {
            AntdUI.Config.IsDark = true;
            AntdUI.Config.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            AntdUI.Config.TextRenderingHighQuality = true;
            AntdUI.Config.ShadowEnabled = true;
            AntdUI.Style.SetPrimary(Color.FromArgb(0, 229, 214));
            AntdUI.Style.SetSuccess(Color.FromArgb(42, 199, 122));
            AntdUI.Style.SetError(Color.FromArgb(255, 76, 92));
            AntdUI.Style.SetWarning(Color.FromArgb(245, 172, 70));
            AntdUI.Style.SetInfo(Color.FromArgb(0, 168, 255));
        }

        private static void CreateRequiredDirectories()
        {
            string[] requiredDirectories = new string[]
            {
                "sites",
                "cbftp",
                "sections",
                "db",
                "pre_bots",
                "settings"
            };

            foreach (string dir in requiredDirectories)
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    Console.WriteLine($"Created directory: {dir}");
                }
                else
                {
                    Console.WriteLine($"Directory already exists: {dir}");
                }
            }
        }
    }
}
