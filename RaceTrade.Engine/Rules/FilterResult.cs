using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RaceTrade
{
    /// <summary>
    /// Represents the result of filtering allowed sites for a release.
    /// Provides clear status codes instead of ambiguous null returns.
    /// </summary>
    public class FilterResult
    {
        /// <summary>
        /// List of sites allowed to race this release.
        /// Null if Status != Success.
        /// </summary>
        public List<string> AllowedSites { get; set; }

        /// <summary>
        /// Subset of AllowedSites that are download-only for this release because
        /// the release group is in the site's affils list. These sites still race
        /// (download side) but are not upload targets. Never null.
        /// </summary>
        public List<string> DlOnlySites { get; set; } = new List<string>();

        /// <summary>
        /// Status of the filter operation.
        /// </summary>
        public FilterStatus Status { get; set; }

        /// <summary>
        /// Human-readable message describing the result.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// The CBFTP section that was evaluated.
        /// </summary>
        public string CbftpSection { get; set; }

        /// <summary>
        /// The release name that was evaluated.
        /// </summary>
        public string ReleaseName { get; set; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static FilterResult Success(string releaseName, string cbftpSection, List<string> allowedSites, List<string> dlOnlySites = null)
        {
            return new FilterResult
            {
                Status = FilterStatus.Success,
                AllowedSites = allowedSites,
                DlOnlySites = dlOnlySites ?? new List<string>(),
                ReleaseName = releaseName,
                CbftpSection = cbftpSection,
                Message = $"Found [{allowedSites.Count}] allowed site(s)"
            };
        }

        /// <summary>
        /// Creates a duplicate result.
        /// </summary>
        public static FilterResult Duplicate(string releaseName)
        {
            return new FilterResult
            {
                Status = FilterStatus.Duplicate,
                AllowedSites = null,
                ReleaseName = releaseName,
                Message = "Already being processed"
            };
        }

        /// <summary>
        /// Creates a no-sites result.
        /// </summary>
        public static FilterResult NoSites(string releaseName, string cbftpSection, string reason)
        {
            return new FilterResult
            {
                Status = FilterStatus.NoSites,
                AllowedSites = new List<string>(),
                ReleaseName = releaseName,
                CbftpSection = cbftpSection,
                Message = $"No allowed sites in section [{cbftpSection}]: [{reason}]"
            };
        }

        /// <summary>
        /// Creates an insufficient sites result.
        /// </summary>
        public static FilterResult InsufficientSites(string releaseName, string cbftpSection, int siteCount, List<string> allowedSites = null)
        {
            string message;

            if (allowedSites != null && allowedSites.Any())
            {
                message = $"Insufficient sites: found [{siteCount}] ([{string.Join(", ", allowedSites)}]), need at least [2]";
            }
            else
            {
                message = $"Insufficient sites: found [{siteCount}], need at least [2]";
            }

            return new FilterResult
            {
                Status = FilterStatus.InsufficientSites,
                AllowedSites = allowedSites ?? new List<string>(),
                ReleaseName = releaseName,
                CbftpSection = cbftpSection,
                Message = message
            };
        }

        /// <summary>
        /// Creates an error result.
        /// </summary>
        public static FilterResult Error(string releaseName, string errorMessage)
        {
            return new FilterResult
            {
                Status = FilterStatus.Error,
                AllowedSites = null,
                ReleaseName = releaseName,
                Message = $"Error filtering sites: [{errorMessage}]"
            };
        }

        /// <summary>
        /// Creates a globally blacklisted result.
        /// </summary>
        public static FilterResult GloballyBlacklisted(string releaseName, string pattern)
        {
            return new FilterResult
            {
                Status = FilterStatus.GloballyBlacklisted,
                AllowedSites = null,
                ReleaseName = releaseName,
                Message = $"Matched global blacklist pattern: [{pattern}]"
            };
        }
    }

    /// <summary>
    /// Status codes for site filtering operations.
    /// </summary>
    public enum FilterStatus
    {
        /// <summary>
        /// Sites successfully filtered, ready to transfer.
        /// </summary>
        Success,

        /// <summary>
        /// Release is already being processed (duplicate).
        /// </summary>
        Duplicate,

        /// <summary>
        /// No sites matched the filtering criteria.
        /// </summary>
        NoSites,

        /// <summary>
        /// Some sites matched but not enough (need at least 2).
        /// </summary>
        InsufficientSites,

        /// <summary>
        /// An error occurred during filtering.
        /// </summary>
        Error,

        /// <summary>
        /// Release matched global blacklist pattern.
        /// </summary>
        GloballyBlacklisted
    }
}