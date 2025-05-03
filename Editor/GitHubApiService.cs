using UnityEngine;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;
using System.IO;

namespace novavoidhowl.z3yshaderpicker
{
  // Class to represent a GitHub release/tag
  [System.Serializable]
  public class GitHubRelease
  {
    public string name;
    public string tag_name;
    public string zipball_url;
    public string tarball_url;
    public string html_url;
    public bool prerelease;
    public string published_at;
    public string body;
  }

  // Class to represent a GitHub tag
  [Serializable]
  public class GitHubTag
  {
    public string name;
    public string zipball_url;
    public string tarball_url;
    public string commit;
    public string node_id;
  }

  // Helper class to store combined version info
  public class VersionInfo
  {
    public string Version { get; set; } // The actual version number (without 'v' prefix)
    public string DisplayName { get; set; } // What's shown in the dropdown
    public string DownloadUrl { get; set; } // URL to download from
    public string Description { get; set; } // Release notes or description
    public bool IsRelease { get; set; } // Whether this is a release (true) or just a tag (false)
    public string RawTagName { get; set; } // The original tag name with 'v' prefix if present
    public bool IsCompatible { get; set; } = true; // Whether this version is compatible with current Unity version
  }

  // Class to represent GitHub rate limit information
  [Serializable]
  public class GitHubRateLimit
  {
    public RateLimitResources resources { get; set; }
    public RateLimitCore rate { get; set; }
  }

  [Serializable]
  public class RateLimitResources
  {
    public RateLimitCore core { get; set; }
    public RateLimitCore search { get; set; }
    public RateLimitCore graphql { get; set; }
    public RateLimitCore integration_manifest { get; set; }
    public RateLimitCore code_scanning_upload { get; set; }
    public RateLimitCore actions_runner_registration { get; set; }
    public RateLimitCore scim { get; set; }
    public RateLimitCore dependency_snapshots { get; set; }
  }

  [Serializable]
  public class RateLimitCore
  {
    public int limit { get; set; }
    public int used { get; set; }
    public int remaining { get; set; }
    public long reset { get; set; }
  }

  // Class to store the cache data
  [Serializable]
  internal class GitHubApiCache
  {
    public List<VersionInfo> cachedVersions { get; set; }
    public DateTime cacheTime { get; set; }
    public RateLimitCore rateLimitInfo { get; set; }
  }

  /// <summary>
  /// Service class for interacting with the GitHub API
  /// </summary>
  public class GitHubApiService
  {
    // debug bool
    private const bool DebugMode = false; // Set to true for verbose logging

    // GitHub repository information
    private const string RepoOwner = "z3y";
    private const string RepoName = "shaders";
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const int PerPage = 30; // Increased items per page to reduce total requests
    private const int MaxRetries = 3; // Number of times to retry failed requests
    private const int RetryDelayMs = 1000; // Delay between retries in milliseconds
    private const int RequestDelayMs = 500; // Delay between requests to avoid triggering rate limits
    private const int MaxApiCalls = 4; // Maximum number of API calls (pages) to fetch

    // GitHub token - if provided by the user, this will increase rate limits
    private static string GitHubToken = "";

    // Rate limiting tracking
    private DateTime? rateLimitReset = null;
    private int remainingRequests = 60; // Default unauthenticated limit
    private const string TokenPrefsKey = "GitHubApiToken"; // Key for storing token in EditorPrefs

    // Cache settings
    private const string CachePrefsKey = "GitHubApiVersionCache";
    private const int CacheExpirationHours = 24; // Cache expires after 24 hours
    private GitHubApiCache cache;

    // HttpClient instance - reusing a single instance is recommended for performance
    private HttpClient httpClient;

    // Event to notify rate limit exceeded
    public delegate void RateLimitExceededHandler(TimeSpan timeUntilReset);
    public event RateLimitExceededHandler OnRateLimitExceeded;

    /// <summary>
    /// Initialize the service and try to load saved token and cache
    /// </summary>
    public GitHubApiService()
    {
      // Try to load token from EditorPrefs
      if (UnityEditor.EditorPrefs.HasKey(TokenPrefsKey))
      {
        GitHubToken = UnityEditor.EditorPrefs.GetString(TokenPrefsKey);
        Debug.Log("GitHub API token loaded from preferences");
      }

      // Try to load cached data
      LoadCache();

      // Initialize HttpClient
      httpClient = new HttpClient();

      // Set default headers
      httpClient.DefaultRequestHeaders.Add(
        "User-Agent",
        "Z3Y-Shader-Picker/1.0 (Unity-Editor; Windows) github.com/novavoidhowl"
      );

      // Add authentication if token is provided
      if (!string.IsNullOrEmpty(GitHubToken))
      {
        httpClient.DefaultRequestHeaders.Add("Authorization", $"token {GitHubToken}");
      }
    }

    /// <summary>
    /// Load cached GitHub API data if available
    /// </summary>
    private void LoadCache()
    {
      try
      {
        if (UnityEditor.EditorPrefs.HasKey(CachePrefsKey))
        {
          string cacheJson = UnityEditor.EditorPrefs.GetString(CachePrefsKey);
          cache = JsonConvert.DeserializeObject<GitHubApiCache>(cacheJson);

          if (cache != null)
          {
            // Check if cache is still valid (not expired)
            if ((DateTime.UtcNow - cache.cacheTime).TotalHours < CacheExpirationHours)
            {
              Debug.Log(
                $"Loaded {cache.cachedVersions.Count} versions from cache (created {cache.cacheTime.ToLocalTime()})"
              );
            }
            else
            {
              Debug.Log("Cache exists but has expired");
              cache = null; // Invalidate expired cache
            }
          }
        }
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error loading cache: {ex.Message}");
        cache = null;
      }
    }

    /// <summary>
    /// Save data to the cache
    /// </summary>
    private void SaveCache(List<VersionInfo> versions, RateLimitCore rateLimitInfo)
    {
      try
      {
        cache = new GitHubApiCache
        {
          cachedVersions = versions,
          cacheTime = DateTime.UtcNow,
          rateLimitInfo = rateLimitInfo
        };

        string cacheJson = JsonConvert.SerializeObject(cache);
        UnityEditor.EditorPrefs.SetString(CachePrefsKey, cacheJson);
        Debug.Log($"Saved {versions.Count} versions to cache");
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error saving cache: {ex.Message}");
      }
    }

    /// <summary>
    /// Clear the API data cache
    /// </summary>
    public void ClearCache()
    {
      if (UnityEditor.EditorPrefs.HasKey(CachePrefsKey))
      {
        UnityEditor.EditorPrefs.DeleteKey(CachePrefsKey);
      }
      cache = null;
      Debug.Log("GitHub API cache cleared");
    }

    /// <summary>
    /// Check if there is valid cached data available
    /// </summary>
    public bool HasValidCache()
    {
      return cache != null && cache.cachedVersions != null && cache.cachedVersions.Count > 0;
    }

    /// <summary>
    /// Get cached versions without making API calls
    /// </summary>
    public List<VersionInfo> GetCachedVersions()
    {
      if (HasValidCache())
      {
        return cache.cachedVersions;
      }
      return new List<VersionInfo>();
    }

    /// <summary>
    /// Get the cache creation time as a formatted string
    /// </summary>
    public string GetCacheTimeInfo()
    {
      if (HasValidCache())
      {
        TimeSpan age = DateTime.UtcNow - cache.cacheTime;
        return $"Cache created {age.Hours} hours {age.Minutes} minutes ago";
      }
      return "No cache available";
    }

    /// <summary>
    /// Sets a GitHub access token to use for API requests
    /// </summary>
    /// <param name="token">The GitHub access token</param>
    public void SetGitHubToken(string token)
    {
      GitHubToken = token;

      // Save token to EditorPrefs if not empty
      if (!string.IsNullOrEmpty(token))
      {
        UnityEditor.EditorPrefs.SetString(TokenPrefsKey, token);
        Debug.Log("GitHub API token saved to preferences");

        // Update HttpClient
        if (httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
          httpClient.DefaultRequestHeaders.Remove("Authorization");
        }
        httpClient.DefaultRequestHeaders.Add("Authorization", $"token {GitHubToken}");
      }
      else
      {
        UnityEditor.EditorPrefs.DeleteKey(TokenPrefsKey);
        Debug.Log("GitHub API token removed from preferences");

        // Remove authorization header from HttpClient
        if (httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
          httpClient.DefaultRequestHeaders.Remove("Authorization");
        }
      }
    }

    /// <summary>
    /// Gets whether a GitHub token is currently set
    /// </summary>
    /// <returns>True if a token is set, false otherwise</returns>
    public bool HasToken()
    {
      return !string.IsNullOrEmpty(GitHubToken);
    }

    /// <summary>
    /// Gets the current rate limit information
    /// </summary>
    /// <returns>A string describing the current rate limit status</returns>
    public string GetRateLimitInfo()
    {
      // If we have cached rate limit info, use that
      if (cache != null && cache.rateLimitInfo != null)
      {
        // Calculate when the rate limit resets
        DateTime resetTime = DateTimeOffset.FromUnixTimeSeconds(cache.rateLimitInfo.reset).UtcDateTime;
        TimeSpan timeUntilReset = resetTime - DateTime.UtcNow;

        if (timeUntilReset.TotalSeconds > 0)
        {
          return $"API calls remaining: {cache.rateLimitInfo.remaining}/{cache.rateLimitInfo.limit} "
            + $"(resets in {timeUntilReset.TotalMinutes:F1} min)";
        }
      }

      // Fall back to the basic info
      if (rateLimitReset.HasValue)
      {
        TimeSpan timeUntilReset = rateLimitReset.Value - DateTime.UtcNow;
        if (timeUntilReset.TotalSeconds > 0)
        {
          return $"API calls remaining: {remainingRequests} (resets in {timeUntilReset.TotalMinutes:F1} min)";
        }
      }

      return HasToken()
        ? "Using authenticated GitHub API (5,000 requests per hour)"
        : "Using unauthenticated GitHub API (60 requests per hour)";
    }

    /// <summary>
    /// Fetches current GitHub API rate limit information
    /// </summary>
    public async Task<RateLimitCore> FetchRateLimitAsync()
    {
      try
      {
        string apiUrl = $"{GitHubApiBaseUrl}/rate_limit";

        // Debug print headers
        DebugPrintHttpClientHeaders(apiUrl, DebugMode);

        // Send request
        HttpResponseMessage response = await httpClient.GetAsync(apiUrl);

        if (response.IsSuccessStatusCode)
        {
          // Parse the response
          string responseJson = await response.Content.ReadAsStringAsync();
          var rateLimit = JsonConvert.DeserializeObject<GitHubRateLimit>(responseJson);

          if (rateLimit != null && rateLimit.resources != null)
          {
            // Update our cached rate limit info
            if (cache != null)
            {
              cache.rateLimitInfo = rateLimit.resources.core;
            }

            // Log the rate limit info
            Debug.Log(
              $"Rate limit: {rateLimit.resources.core.remaining}/{rateLimit.resources.core.limit} "
                + $"(resets at {DateTimeOffset.FromUnixTimeSeconds(rateLimit.resources.core.reset).ToLocalTime()})"
            );

            return rateLimit.resources.core;
          }
        }
        else
        {
          Debug.LogError($"Failed to fetch rate limit info: {response.StatusCode} {response.ReasonPhrase}");
        }
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error fetching rate limit: {ex.Message}");
      }

      return null;
    }

    /// <summary>
    /// Debug helper to print all headers in a HttpClient request
    /// </summary>
    private void DebugPrintHttpClientHeaders(string requestUrl, bool debugMode = false)
    {
      if (!debugMode)
      {
        return;
      }

      StringBuilder headerDebug = new StringBuilder();
      headerDebug.AppendLine($"DEBUG - HttpClient Headers for URL: {requestUrl}");

      // Print all default request headers
      foreach (var header in httpClient.DefaultRequestHeaders)
      {
        headerDebug.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
      }

      Debug.Log(headerDebug.ToString());
    }

    /// <summary>
    /// Helper method to track GitHub rate limiting
    /// </summary>
    /// <param name="headers">The response headers from a GitHub API request</param>
    private void CheckRateLimitHeaders(HttpResponseMessage response)
    {
      if (response == null)
        return;

      try
      {
        // Create a debug message with all rate limit information
        string rateLimitDebug = "GitHub API Rate Limit Information:\n";

        // Get rate limit headers
        IEnumerable<string> values;

        // x-ratelimit-limit: Maximum requests per hour
        if (response.Headers.TryGetValues("X-RateLimit-Limit", out values))
        {
          string limitValue = values.FirstOrDefault() ?? "unknown";
          rateLimitDebug += $"  Max Requests Per Hour (x-ratelimit-limit): {limitValue}\n";
        }

        // x-ratelimit-remaining: Remaining requests in current window
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out values))
        {
          string remainingValue = values.FirstOrDefault() ?? "unknown";
          rateLimitDebug += $"  Remaining Requests (x-ratelimit-remaining): {remainingValue}\n";

          int.TryParse(remainingValue, out remainingRequests);
        }

        // x-ratelimit-used: Used requests in current window
        if (response.Headers.TryGetValues("X-RateLimit-Used", out values))
        {
          string usedValue = values.FirstOrDefault() ?? "unknown";
          rateLimitDebug += $"  Used Requests (x-ratelimit-used): {usedValue}\n";
        }

        // x-ratelimit-reset: When current window resets (UTC epoch seconds)
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out values))
        {
          string resetValue = values.FirstOrDefault() ?? "unknown";
          rateLimitDebug += $"  Reset Time UTC (x-ratelimit-reset): {resetValue}\n";

          if (long.TryParse(resetValue, out long epochSeconds))
          {
            rateLimitReset = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;

            // Add human-readable reset time
            string resetTimeStr = rateLimitReset.Value.ToString("yyyy-MM-dd HH:mm:ss UTC");
            rateLimitDebug += $"  Reset Time Human-Readable: {resetTimeStr}\n";

            // Add time until reset
            TimeSpan timeUntilReset = rateLimitReset.Value - DateTime.UtcNow;
            rateLimitDebug += $"  Time Until Reset: {timeUntilReset.TotalMinutes:F1} minutes\n";
          }
        }

        // x-ratelimit-resource: Which rate limit bucket this counts against
        if (response.Headers.TryGetValues("X-RateLimit-Resource", out values))
        {
          string resourceValue = values.FirstOrDefault() ?? "unknown";
          rateLimitDebug += $"  Resource (x-ratelimit-resource): {resourceValue}\n";
        }

        // Log the complete rate limit information
        Debug.Log(rateLimitDebug);
      }
      catch (Exception e)
      {
        Debug.LogError($"Error parsing rate limit headers: {e.Message}");
      }
    }

    /// <summary>
    /// Fetches all GitHub releases for the repository with pagination support and rate limiting
    /// </summary>
    /// <returns>List of GitHub releases</returns>
    public async Task<List<GitHubRelease>> FetchGitHubReleasesAsync()
    {
      List<GitHubRelease> allReleases = new List<GitHubRelease>();
      int page = 1;
      bool hasMoreData = true;

      // Keep trying until we succeed or reach max retries
      for (int attempt = 0; attempt < MaxRetries; attempt++)
      {
        try
        {
          // Check if we need to wait for rate limit reset
          if (rateLimitReset.HasValue && DateTime.UtcNow < rateLimitReset.Value)
          {
            TimeSpan waitTime = rateLimitReset.Value - DateTime.UtcNow;
            Debug.LogWarning(
              $"GitHub API rate limit exceeded. Waiting for {waitTime.TotalSeconds:F0} seconds until reset..."
            );

            // Notify subscribers about rate limit
            OnRateLimitExceeded?.Invoke(waitTime);

            // Break out of retry loop - we'll have to try again later
            return allReleases;
          }

          // Fetch all pages of releases
          while (hasMoreData)
          {
            // Construct URL with pagination parameters
            string apiUrl = $"{GitHubApiBaseUrl}/repos/{RepoOwner}/{RepoName}/releases?per_page={PerPage}&page={page}";

            // Debug print all request headers
            DebugPrintHttpClientHeaders(apiUrl, DebugMode);

            // Send request
            HttpResponseMessage response = await httpClient.GetAsync(apiUrl);

            // Check if request was successful
            if (response.IsSuccessStatusCode)
            {
              // Check headers for rate limit info
              CheckRateLimitHeaders(response);

              // Parse the response
              string responseJson = await response.Content.ReadAsStringAsync();
              List<GitHubRelease> pageReleases = JsonConvert.DeserializeObject<List<GitHubRelease>>(responseJson);

              // Add this page's releases to our collection
              if (pageReleases != null && pageReleases.Count > 0)
              {
                allReleases.AddRange(pageReleases);
                page++;

                // Add a small delay between requests to be nice to GitHub's API
                await Task.Delay(RequestDelayMs);
              }
              else
              {
                hasMoreData = false;
              }

              // Safety check - stop if we've fetched a large number already
              if (page > MaxApiCalls)
              {
                Debug.LogWarning(
                  $"Stopped fetching releases after reaching the maximum number of API calls ({MaxApiCalls})"
                );
                hasMoreData = false;
              }
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
              // This is likely a rate limit issue
              Debug.LogError($"GitHub API Forbidden (403) Error Details:");
              Debug.LogError($"  Response Status: {response.ReasonPhrase}");

              // Try to read response body for more details
              try
              {
                string responseBody = await response.Content.ReadAsStringAsync();
                Debug.LogError($"  Response Body: {responseBody}");

                // Check if this is a specific rate limit message
                if (responseBody.Contains("rate limit"))
                {
                  Debug.LogError("  Error Type: Rate limit exceeded");
                }
                else if (responseBody.Contains("abuse detection"))
                {
                  Debug.LogError("  Error Type: Abuse detection mechanism triggered");
                  Debug.LogError(
                    "  Note: This happens when GitHub detects unusual patterns like too many requests in a short time"
                  );
                }
              }
              catch (Exception readEx)
              {
                Debug.LogError($"  Could not read response body: {readEx.Message}");
              }

              // Check rate limit headers
              CheckRateLimitHeaders(response);

              // Throw exception to trigger retry
              throw new HttpRequestException($"GitHub API request failed with status code {response.StatusCode}");
            }
            else
            {
              // Other error
              Debug.LogError(
                $"GitHub API request failed with status code {response.StatusCode}: {response.ReasonPhrase}"
              );

              // Try to read response body for more details
              try
              {
                string responseBody = await response.Content.ReadAsStringAsync();
                Debug.LogError($"  Response Body: {responseBody}");
              }
              catch (Exception readEx)
              {
                Debug.LogError($"  Could not read response body: {readEx.Message}");
              }

              // Throw exception to trigger retry
              throw new HttpRequestException($"GitHub API request failed with status code {response.StatusCode}");
            }
          }

          // If we get here, we succeeded, so break out of retry loop
          break;
        }
        catch (HttpRequestException e)
        {
          Debug.LogWarning(
            $"GitHub API request failed. Try {attempt + 1}/{MaxRetries}. Remaining attempts: {MaxRetries - attempt - 1}"
          );

          if (attempt < MaxRetries - 1 && remainingRequests <= 0 && rateLimitReset.HasValue)
          {
            // If this isn't our last attempt and we're rate limited, wait and try again
            TimeSpan waitTime = rateLimitReset.Value - DateTime.UtcNow;
            Debug.LogWarning($"Waiting for {waitTime.TotalSeconds:F0} seconds and will retry");

            // Notify subscribers about rate limit
            OnRateLimitExceeded?.Invoke(waitTime);

            // Wait for rate limit reset + 5 seconds buffer
            await Task.Delay((int)waitTime.TotalMilliseconds + 5000);
          }
          else if (attempt < MaxRetries - 1)
          {
            // If we have more retries, delay and try again
            await Task.Delay(RetryDelayMs * (attempt + 1)); // Exponential backoff
          }
          else
          {
            // On the last attempt, just report the error
            Debug.LogError($"GitHub API request failed after {MaxRetries} attempts: {e.Message}");
            return allReleases;
          }
        }
        catch (Exception e)
        {
          // Handle other errors
          Debug.LogError($"Unexpected error fetching GitHub releases: {e.Message}");
          return allReleases;
        }
      }

      Debug.Log($"Fetched a total of {allReleases.Count} releases");
      return allReleases;
    }

    /// <summary>
    /// Fetches GitHub releases and converts them to version info objects without needing separate tags API calls,
    /// filtering out versions that are marked as not compatible with the package manager.
    /// Uses cache if available and valid.
    /// </summary>
    /// <param name="forceRefresh">Force refresh from API even if cache is valid</param>
    /// <returns>A list of version information objects based on GitHub releases</returns>
    public async Task<List<VersionInfo>> FetchVersionsAsync(bool forceRefresh = false)
    {
      // If we have a valid cache and aren't forcing a refresh, use the cached data
      if (!forceRefresh && HasValidCache())
      {
        return cache.cachedVersions;
      }

      Debug.Log("Starting to fetch versions from GitHub releases API...");

      // Fetch rate limit info first to know how many requests we have left
      var rateLimit = await FetchRateLimitAsync();

      // Only continue if we have at least one request remaining
      if (rateLimit != null && rateLimit.remaining > 0)
      {
        // Fetch releases from GitHub API
        var releases = await FetchGitHubReleasesAsync();

        // Convert releases to version info objects
        var versions = ConvertReleasesToVersionInfo(releases);

        // Filter out versions that are marked as not compatible with package manager
        versions = FilterNonPackageVersions(versions);

        // Save to cache
        if (versions.Count > 0)
        {
          SaveCache(versions, rateLimit);
        }

        Debug.Log($"Successfully fetched and processed {versions.Count} versions");
        return versions;
      }
      else if (HasValidCache())
      {
        // If we're out of API calls but have a cache, use it
        Debug.LogWarning("Out of API requests, using cached data instead");
        return cache.cachedVersions;
      }
      else
      {
        Debug.LogError("Out of API requests and no cache available");
        return new List<VersionInfo>();
      }
    }

    /// <summary>
    /// Filters out versions that are marked as not compatible with package manager in the versionsupport.json file
    /// </summary>
    /// <param name="versions">The full list of versions</param>
    /// <returns>Filtered list of versions that can be installed via package manager</returns>
    private List<VersionInfo> FilterNonPackageVersions(List<VersionInfo> versions)
    {
      try
      {
        // Load the versionsupport.json file
        TextAsset versionSupportFile = Resources.Load<TextAsset>("z3ysp/versionsupport");
        if (versionSupportFile == null)
        {
          Debug.LogWarning("Could not load versionsupport.json file. All versions will be shown.");
          return versions;
        }

        // Parse the JSON
        var versionSupport = JsonConvert.DeserializeObject<VersionSupportData>(versionSupportFile.text);
        if (
          versionSupport == null
          || versionSupport.compatibilityList == null
          || versionSupport.compatibilityList.Count == 0
        )
        {
          Debug.LogWarning("versionsupport.json file has no compatibility data. All versions will be shown.");
          return versions;
        }

        // Get ranges where NoPackage is true
        var nonPackageRanges = versionSupport.compatibilityList
          .Where(c => c.NoPackage)
          .Select(
            c =>
              new
              {
                Min = string.IsNullOrEmpty(c.shaderVersionRangeMin) || c.shaderVersionRangeMin == "N/A"
                  ? null
                  : NormalizeVersion(c.shaderVersionRangeMin),
                Max = string.IsNullOrEmpty(c.shaderVersionRangeMax) || c.shaderVersionRangeMax == "N/A"
                  ? null
                  : NormalizeVersion(c.shaderVersionRangeMax)
              }
          )
          .ToList();

        // If there are no non-package ranges, return all versions
        if (nonPackageRanges.Count == 0)
        {
          return versions;
        }

        // Filter versions
        var filteredVersions = new List<VersionInfo>();
        int excludedCount = 0;

        foreach (var version in versions)
        {
          bool shouldExclude = false;
          Version versionObj;

          // Try to parse the version, normalizing incomplete semver
          try
          {
            versionObj = NormalizeVersion(version.Version);
          }
          catch
          {
            // If we can't parse the version, include it (better safe than sorry)
            filteredVersions.Add(version);
            continue;
          }

          // Check if this version falls into any non-package range
          foreach (var range in nonPackageRanges)
          {
            bool isAboveMin = range.Min == null || versionObj >= range.Min;
            bool isBelowMax = range.Max == null || versionObj <= range.Max;

            if (isAboveMin && isBelowMax)
            {
              shouldExclude = true;
              break;
            }
          }

          if (!shouldExclude)
          {
            filteredVersions.Add(version);
          }
          else
          {
            excludedCount++;
            Debug.Log($"Excluding version {version.RawTagName} as it falls into a non-package range");
          }
        }

        Debug.Log($"Filtered out {excludedCount} versions that cannot be installed via package manager");
        return filteredVersions;
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error filtering versions: {ex.Message}");
        return versions; // Return all versions if there's an error
      }
    }

    /// <summary>
    /// Normalizes version strings to handle incomplete semver data (e.g., "1.0" to "1.0.0")
    /// </summary>
    /// <param name="version">The version string to normalize</param>
    /// <returns>A Version object with normalized components</returns>
    private Version NormalizeVersion(string version)
    {
      if (string.IsNullOrEmpty(version))
        return new Version(0, 0, 0);

      // Count the number of dot separators
      int dotCount = version.Count(c => c == '.');

      // If we have a complete semver (at least major.minor.patch), just parse it
      if (dotCount >= 2)
        return new Version(version);

      // If we have major.minor (e.g., "1.0"), append ".0" for patch
      if (dotCount == 1)
        return new Version(version + ".0");

      // If we just have major (e.g., "1"), append ".0.0" for minor and patch
      return new Version(version + ".0.0");
    }

    // Class to deserialize the versionsupport.json file
    [Serializable]
    internal class VersionSupportData
    {
      public string info;
      public List<VersionCompatibility> compatibilityList;
    }

    [Serializable]
    internal class VersionCompatibility
    {
      public bool NoPackage;
      public string shaderVersionRangeMax;
      public string shaderVersionRangeMin;
      public string SupportedUnityVersionsRange;
    }

    /// <summary>
    /// Converts releases to version info objects without needing tags
    /// </summary>
    private List<VersionInfo> ConvertReleasesToVersionInfo(List<GitHubRelease> releases)
    {
      List<VersionInfo> versions = new List<VersionInfo>();

      foreach (var release in releases)
      {
        // Skip invalid releases
        if (string.IsNullOrEmpty(release.tag_name))
          continue;

        // Clean the version number by removing 'v' prefix if present
        string versionNumber = release.tag_name.TrimStart('v');

        // Create version info
        versions.Add(
          new VersionInfo
          {
            Version = versionNumber,
            DisplayName = versionNumber,
            DownloadUrl = release.zipball_url,
            Description = release.body ?? "",
            IsRelease = true,
            RawTagName = release.tag_name
          }
        );
      }

      // Sort by version number (descending)
      return versions
        .OrderByDescending(v =>
        {
          try
          {
            return new Version(v.Version);
          }
          catch
          {
            return new Version(0, 0);
          }
        })
        .ToList();
    }

    /// <summary>
    /// Downloads a file from a URL with proper headers for GitHub API
    /// </summary>
    /// <param name="url">The URL to download from</param>
    /// <param name="localFilePath">The local file path to save to</param>
    /// <returns>Async task</returns>
    public async Task DownloadFileAsync(string url, string localFilePath)
    {
      try
      {
        // Debug print all request headers before downloading
        DebugPrintHttpClientHeaders(url, DebugMode);

        // Send request to download file
        HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        // Check if request was successful
        if (response.IsSuccessStatusCode)
        {
          // Create directory if it doesn't exist
          string directory = Path.GetDirectoryName(localFilePath);
          if (!Directory.Exists(directory))
          {
            Directory.CreateDirectory(directory);
          }

          // Download file to disk
          using (var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
          {
            await response.Content.CopyToAsync(fileStream);
          }

          Debug.Log($"Successfully downloaded file to {localFilePath}");
        }
        else
        {
          // Error
          Debug.LogError($"Failed to download file: {response.StatusCode} {response.ReasonPhrase}");

          // Try to read response body for more details
          try
          {
            string responseBody = await response.Content.ReadAsStringAsync();
            Debug.LogError($"  Response Body: {responseBody}");
          }
          catch (Exception readEx)
          {
            Debug.LogError($"  Could not read response body: {readEx.Message}");
          }

          throw new HttpRequestException($"Failed to download file: {response.StatusCode}");
        }
      }
      catch (Exception e)
      {
        Debug.LogError($"Error downloading file: {e.Message}");
        throw;
      }
    }
  }
}
