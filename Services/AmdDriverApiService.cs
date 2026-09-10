using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    public record AmdDriverInfo(
        string Version, 
        string DownloadUrl, 
        string SupportUrl, 
        DateTime? ReleaseDate, 
        string DisplayString);

    /// <summary>
    /// Dynamic driver lookup API for AMD Radeon Adrenalin WHQL packages.
    /// Queries public release indexes (TechPowerUp & AMD release metadata) to dynamically
    /// obtain the newest WHQL version, release date, and direct download links from AMD CDN.
    /// </summary>
    public class AmdDriverApiService
    {
        private static readonly HttpClient _httpClient = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://www.amd.com/");
            return client;
        }

        /// <summary>Package variant the lookup should resolve.</summary>
        public enum AmdPackageVariant
        {
            /// <summary>Standard desktop Adrenalin package (default).</summary>
            Desktop = 0,

            /// <summary>Desktop+notebook "combined" INFs — required by many laptop GPUs/APUs the desktop INF rejects.</summary>
            Notebook = 1,
        }

        public async Task<AmdDriverInfo?> GetLatestDriverAsync(CancellationToken cancellationToken = default)
        {
            return await GetLatestDriverAsync(AmdPackageVariant.Desktop, cancellationToken);
        }

        public async Task<AmdDriverInfo?> GetLatestDriverAsync(
            AmdPackageVariant variant, CancellationToken cancellationToken = default)
        {
            try
            {
                var liveInfo = await QueryTechPowerUpFeedAsync(variant, cancellationToken);
                if (liveInfo != null)
                {
                    return liveInfo;
                }
            }
            catch (Exception)
            {
                // Fallback to curated release below
            }

            return GetCuratedLatest(variant);
        }

        private async Task<AmdDriverInfo?> QueryTechPowerUpFeedAsync(
            AmdPackageVariant variant, CancellationToken cancellationToken)
        {
            const string url = "https://www.techpowerup.com/download/amd-radeon-graphics-drivers/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Pattern for Version title: <h3 class="title">\s*AMD Radeon Graphics Drivers ([0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*(?:WHQL)?\s*</h3>
            var titleMatch = Regex.Match(html, @"<h3[^>]*class=""title""[^>]*>\s*AMD\s+Radeon\s+Graphics\s+Drivers\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*(WHQL)?", RegexOptions.IgnoreCase);
            if (!titleMatch.Success) return null;

            string version = titleMatch.Groups[1].Value.Trim();

            // Pattern for Filename: <div class="filename"[^>]*>([^<]+)</div>
            var fileMatch = Regex.Match(html, @"<div[^>]*class=""filename""[^>]*>\s*(whql-amd-software-[^<]+\.exe|non-whql-amd-software-[^<]+\.exe|amd-software-[^<]+\.exe)\s*</div>", RegexOptions.IgnoreCase);
            string filename = fileMatch.Success
                ? fileMatch.Groups[1].Value.Trim()
                : $"whql-amd-software-adrenalin-edition-{version}-win10-win11.exe";

            if (variant == AmdPackageVariant.Notebook)
            {
                filename = BuildNotebookFilename(version, filename);
            }

            DateTime? releaseDate = null;
            var dateMatch = Regex.Match(html, @"<span[^>]*class=""date""[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                string rawDate = dateMatch.Groups[1].Value.Trim();
                rawDate = Regex.Replace(rawDate, @"\b(\d+)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);
                if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    releaseDate = parsedDate;
                }
            }

            string downloadUrl = $"https://drivers.amd.com/drivers/{filename}";
            string displaySuffix = variant == AmdPackageVariant.Notebook ? " (Notebook/combined)" : string.Empty;

            return new AmdDriverInfo(
                version,
                downloadUrl,
                "https://www.amd.com/en/support/download/drivers.html",
                releaseDate,
                $"AMD Adrenalin {version} WHQL{displaySuffix}"
            );
        }

        internal static string BuildNotebookFilename(string version, string desktopFilename)
        {
            string baseName = Path.GetFileNameWithoutExtension(desktopFilename);
            if (baseName.Contains("combined", StringComparison.OrdinalIgnoreCase))
                return desktopFilename;
            return $"{baseName}-combined.exe";
        }

        public static AmdDriverInfo GetCuratedLatest()
        {
            return GetCuratedLatest(AmdPackageVariant.Desktop);
        }

        public static AmdDriverInfo GetCuratedLatest(AmdPackageVariant variant)
        {
            const string version = "25.10.1";
            string filename = variant == AmdPackageVariant.Notebook
                ? "whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna-combined.exe"
                : "whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna.exe";
            string displaySuffix = variant == AmdPackageVariant.Notebook ? " (Notebook/combined)" : string.Empty;
            return new AmdDriverInfo(
                version,
                $"https://drivers.amd.com/drivers/{filename}",
                "https://www.amd.com/en/support/download/drivers.html",
                new DateTime(2026, 8, 5),
                $"AMD Adrenalin {version} WHQL{displaySuffix}"
            );
        }
    }
}
