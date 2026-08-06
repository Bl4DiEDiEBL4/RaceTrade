using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;

/// <summary>
/// Handles synchronization with CBFTP API to fetch sites and sections.
/// </summary>
/// 

public class CbftpSync
{
    /// <summary>
    /// Fetches all sites and their sections from CBFTP API.
    /// </summary>
    public static async Task<CbftpSyncResult> FetchSitesFromCbftp(string host, string port, string password)
    {
        try
        {
            LogManager.Info($"Connecting to CBFTP at {host}:{port} to fetch sites...");

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Setup authentication (UTF8 instead of ASCII)
            var authBytes = Encoding.UTF8.GetBytes(":" + password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            // Construct endpoint
            string endpoint = host.Contains("://") ? host : $"https://{host}";
            if (!endpoint.EndsWith($":{port}"))
            {
                endpoint = $"{endpoint}:{port}";
            }

            // Fetch all sites (array of strings)
            var response = await client.GetAsync($"{endpoint}/sites");
            if (!response.IsSuccessStatusCode)
            {
                return CbftpSyncResult.Failed($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var sitesArray = JArray.Parse(responseText);

            LogManager.Success($"Found {sitesArray.Count} site(s) in CBFTP");

            var syncedSites = new List<CbftpSite>();

            // Process each site
            foreach (var siteToken in sitesArray)
            {
                try
                {
                    // /sites returns just "MLO", "KOL", ...
                    var siteName = siteToken?.ToString();
                    if (string.IsNullOrEmpty(siteName))
                        continue;

                    LogManager.Debug($"Fetching details for site: {siteName}");

                    // Fetch detailed site info including sections
                    var siteNameEscaped = Uri.EscapeDataString(siteName);
                    var detailResponse = await client.GetAsync($"{endpoint}/sites/{siteNameEscaped}");
                    if (!detailResponse.IsSuccessStatusCode)
                    {
                        LogManager.Warning($"Could not fetch details for site {siteName}");
                        continue;
                    }

                    var detailText = await detailResponse.Content.ReadAsStringAsync();
                    var siteDetail = JObject.Parse(detailText);

                    var cbftpSite = new CbftpSite
                    {
                        Name = siteName,
                        Addresses = siteDetail["addresses"]?.ToObject<List<string>>() ?? new List<string>(),
                        User = siteDetail["user"]?.ToString(),
                        Password = siteDetail["password"]?.ToString(),
                        BasePath = siteDetail["base_path"]?.ToString() ?? "/",
                        Disabled = siteDetail["disabled"]?.ToObject<bool>() ?? false,
                        Sections = new List<CbftpSection>()
                    };

                    // Extract sections: array of { "name": "...", "path": "..." }
                    var sectionsArray = siteDetail["sections"] as JArray;
                    if (sectionsArray != null)
                    {
                        foreach (var sectionToken in sectionsArray)
                        {
                            var section = new CbftpSection
                            {
                                Name = sectionToken["name"]?.ToString(),
                                Path = sectionToken["path"]?.ToString()
                            };

                            if (!string.IsNullOrEmpty(section.Name))
                            {
                                cbftpSite.Sections.Add(section);
                            }
                        }
                    }

                    syncedSites.Add(cbftpSite);
                    LogManager.Success($"Synced site: {siteName} ({cbftpSite.Sections.Count} sections)");
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error processing site: {ex.Message}");
                }
            }

            LogManager.Success($"Successfully synced {syncedSites.Count} site(s) from CBFTP");
            return CbftpSyncResult.Success(syncedSites);
        }
        catch (TaskCanceledException)
        {
            return CbftpSyncResult.Failed("Connection timeout (30 seconds)");
        }
        catch (HttpRequestException ex)
        {
            return CbftpSyncResult.Failed($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CbftpSyncResult.Failed($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches all global sections from CBFTP.
    /// </summary>
    public static async Task<List<string>> FetchGlobalSections(string host, string port, string password)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var authBytes = Encoding.UTF8.GetBytes(":" + password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            string endpoint = host.Contains("://") ? host : $"https://{host}";
            if (!endpoint.EndsWith($":{port}"))
            {
                endpoint = $"{endpoint}:{port}";
            }

            var response = await client.GetAsync($"{endpoint}/sections");
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Warning("Could not fetch global sections from CBFTP");
                return new List<string>();
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var sectionsArray = JArray.Parse(responseText);

            // /sections returns ["SEC1", "SEC2", ...]
            var sections = sectionsArray
                .Select(s => s?.ToString())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            LogManager.Success($"Found {sections.Count} global section(s) in CBFTP");
            return sections;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error fetching global sections: {ex.Message}");
            return new List<string>();
        }
    }
}


/// <summary>
/// Result of CBFTP sync operation.
/// </summary>
public class CbftpSyncResult
{
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; }
    public List<CbftpSite> Sites { get; set; }

    public static CbftpSyncResult Success(List<CbftpSite> sites)
    {
        return new CbftpSyncResult
        {
            IsSuccess = true,
            Sites = sites
        };
    }

    public static CbftpSyncResult Failed(string error)
    {
        return new CbftpSyncResult
        {
            IsSuccess = false,
            ErrorMessage = error,
            Sites = new List<CbftpSite>()
        };
    }
}

/// <summary>
/// Represents a site from CBFTP.
/// </summary>
public class CbftpSite
{
    public string Name { get; set; }
    public List<string> Addresses { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
    public string BasePath { get; set; }
    public bool Disabled { get; set; }
    public List<CbftpSection> Sections { get; set; }

    /// <summary>
    /// Gets the primary address (first in list).
    /// </summary>
    public string PrimaryAddress
    {
        get
        {
            if (Addresses == null || !Addresses.Any())
                return "unknown";

            // Return first address, extracting just hostname/IP (no port)
            var addr = Addresses[0];
            return addr.Contains(":") ? addr.Split(':')[0] : addr;
        }
    }

    /// <summary>
    /// Gets the port from primary address, defaults to 21.
    /// </summary>
    public int Port
    {
        get
        {
            if (Addresses == null || !Addresses.Any())
                return 21;

            var addr = Addresses[0];
            if (addr.Contains(":"))
            {
                var parts = addr.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[1], out int port))
                    return port;
            }

            return 21;
        }
    }
}

/// <summary>
/// Represents a section from CBFTP.
/// </summary>
public class CbftpSection
{
    public string Name { get; set; }
    public string Path { get; set; }
}
