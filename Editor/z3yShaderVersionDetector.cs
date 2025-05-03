using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace novavoidhowl.z3yshaderpicker
{
  public static class z3yShaderVersionDetector
  {
    public static string GetInstalledVersion()
    {
      // Try to find the package in multiple possible locations
      string packagePath = Findz3yShaderPackage();
      if (string.IsNullOrEmpty(packagePath))
      {
        return "z3y Shaders not found in project.";
      }

      // Try to get version from package.json if it exists
      string packageJsonPath = Path.Combine(packagePath, "package.json");
      if (File.Exists(packageJsonPath))
      {
        string packageJson = File.ReadAllText(packageJsonPath);
        string version = ExtractVersionFromPackageJson(packageJson);
        if (!string.IsNullOrEmpty(version))
        {
          return $"z3y Shaders version: {version}";
        }
      }

      // Try alternative method - look for version in shader files
      string version2 = ExtractVersionFromShaderFiles(packagePath);
      if (!string.IsNullOrEmpty(version2))
      {
        return $"z3y Shaders version: {version2}";
      }

      return "z3y Shaders found, but version could not be determined.";
    }

    public static string GetUnityVersion()
    {
      return $"Unity version: {Application.unityVersion}";
    }

    public static CompatibilityResult CheckVersionCompatibility()
    {
      string unityVersion = Application.unityVersion;
      string unityMajorVersion = ExtractMajorVersion(unityVersion);

      string shaderVersionText = GetInstalledVersion();
      string shaderVersion = ExtractVersionNumber(shaderVersionText);

      if (string.IsNullOrEmpty(shaderVersion) || shaderVersion == "not found")
      {
        return new CompatibilityResult
        {
          IsCompatible = false,
          IsUnknown = true,
          Message = "Unable to determine z3y Shader version for compatibility check."
        };
      }

      // Load compatibility data from versionsupport.json
      CompatibilityData compatibilityData = LoadCompatibilityData();
      if (compatibilityData == null || compatibilityData.CompatibilityList.Count == 0)
      {
        return new CompatibilityResult
        {
          IsCompatible = false,
          Message = "Could not load compatibility data from versionsupport.json."
        };
      }

      // Find applicable compatibility entry
      foreach (var entry in compatibilityData.CompatibilityList)
      {
        // Check if this entry applies to our Unity version
        if (entry.SupportedUnityVersionsRange.Contains(unityMajorVersion))
        {
          // Check shader version compatibility
          bool isCompatible = IsShaderVersionCompatible(shaderVersion, entry);

          if (isCompatible)
          {
            return new CompatibilityResult
            {
              IsCompatible = true,
              Message = "The installed shader version is compatible with your Unity version."
            };
          }
          else
          {
            string recommendedVersion = entry.ShaderVersionRangeMin;
            if (entry.ShaderVersionRangeMax != "N/A")
            {
              recommendedVersion = $"{entry.ShaderVersionRangeMin} to {entry.ShaderVersionRangeMax}";
            }

            return new CompatibilityResult
            {
              IsCompatible = false,
              Message = $"Recommended version: {recommendedVersion}"
            };
          }
        }
      }

      // No matching entry found for Unity version
      return new CompatibilityResult
      {
        IsCompatible = false,
        IsUnknown = true,
        Message = $"No compatibility information found for Unity {unityVersion}."
      };
    }

    private static string ExtractMajorVersion(string unityVersion)
    {
      // Extract major version like "2022.3" from "2022.3.12f1"
      Match match = Regex.Match(unityVersion, @"(\d+\.\d+)");
      return match.Success ? match.Groups[1].Value : unityVersion;
    }

    private static string ExtractVersionNumber(string versionText)
    {
      // Extract version number from text like "z3y Shaders version: 3.3.1"
      Match match = Regex.Match(versionText, @"version: (\d+\.\d+\.\d+)");
      return match.Success ? match.Groups[1].Value : "not found";
    }

    private static bool IsShaderVersionCompatible(string shaderVersion, CompatibilityEntry entry)
    {
      // Safety check for null values
      if (string.IsNullOrEmpty(shaderVersion) || entry == null)
      {
        Debug.LogWarning("Shader version or compatibility entry is null or empty");
        return false;
      }

      if (entry.NoPackage)
      {
        return false;
      }

      // Safety check for min version
      if (string.IsNullOrEmpty(entry.ShaderVersionRangeMin))
      {
        Debug.LogWarning(
          $"ShaderVersionRangeMin is null or empty in compatibility entry for Unity {entry.SupportedUnityVersionsRange}"
        );
        return false;
      }

      try
      {
        // Parse version strings to comparable values
        Version currentVersion = new Version(shaderVersion);
        Version minVersion = new Version(entry.ShaderVersionRangeMin);

        // If max version is not specified (N/A), just check against min version
        if (string.IsNullOrEmpty(entry.ShaderVersionRangeMax) || entry.ShaderVersionRangeMax == "N/A")
        {
          return currentVersion >= minVersion;
        }

        // Otherwise check against range - important: use >= and <= to include the boundary values
        Version maxVersion = new Version(entry.ShaderVersionRangeMax);

        // Use inclusive comparison for both min and max
        return currentVersion >= minVersion && currentVersion <= maxVersion;
      }
      catch (Exception ex)
      {
        Debug.LogError(
          $"Error comparing shader versions: {ex.Message}\nCurrent: {shaderVersion}, Min: {entry.ShaderVersionRangeMin}, Max: {entry.ShaderVersionRangeMax}"
        );
        return false;
      }
    }

    private static CompatibilityData LoadCompatibilityData()
    {
      try
      {
        // Try to load the file from Resources first (most reliable method)
        TextAsset jsonAsset = Resources.Load<TextAsset>("z3ysp/versionsupport");

        if (jsonAsset != null)
        {
          string jsonContent = jsonAsset.text;
          Debug.Log("Successfully loaded versionsupport.json from Resources folder");
          return JsonConvert.DeserializeObject<CompatibilityData>(jsonContent);
        }

        // If all approaches fail
        Debug.LogError("Version support file (versionsupport.json) not found.");
        return null;
      }
      catch (Exception e)
      {
        Debug.LogError($"Error loading version support data: {e.Message}");
        return null;
      }
    }

    private static string Findz3yShaderPackage()
    {
      // First, specifically check for com.z3y.shaders package
      string packagesPath = Path.Combine(Application.dataPath, "..", "Packages");
      if (Directory.Exists(packagesPath))
      {
        // Check for specific z3y shaders package directory
        string specificPackagePath = Path.Combine(packagesPath, "com.z3y.shaders");
        if (Directory.Exists(specificPackagePath))
        {
          return specificPackagePath;
        }

        // Check manifest.json for the com.z3y.shaders package
        string manifestPath = Path.Combine(packagesPath, "manifest.json");
        if (File.Exists(manifestPath))
        {
          string manifestContent = File.ReadAllText(manifestPath);
          if (manifestContent.Contains("com.z3y.shaders"))
          {
            // If found in manifest, check Library/PackageCache for the actual files
            string packageCachePath = Path.Combine(Application.dataPath, "..", "Library", "PackageCache");
            if (Directory.Exists(packageCachePath))
            {
              foreach (var dir in Directory.GetDirectories(packageCachePath))
              {
                if (Path.GetFileName(dir).StartsWith("com.z3y.shaders@"))
                {
                  return dir;
                }
              }
            }
          }
        }

        // Check for package.json files, but specifically look for com.z3y.shaders
        foreach (var dir in Directory.GetDirectories(packagesPath))
        {
          string packageJsonPath = Path.Combine(dir, "package.json");
          if (File.Exists(packageJsonPath))
          {
            string packageJson = File.ReadAllText(packageJsonPath);
            if (packageJson.Contains("\"name\"") && packageJson.Contains("\"com.z3y.shaders\""))
            {
              return dir;
            }
          }
        }
      }

      // Check in Assets folder for z3y shader folders
      string[] guids = AssetDatabase.FindAssets("z3y");
      foreach (string guid in guids)
      {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if ((path.Contains("z3y") && path.Contains("shader")) && !path.Contains("picker"))
        {
          // Return the directory containing the found asset
          return Path.GetDirectoryName(Path.Combine(Application.dataPath, "..", path));
        }
      }

      return string.Empty;
    }

    private static string ExtractVersionFromPackageJson(string packageJson)
    {
      // Regular expression to extract version
      Match match = Regex.Match(packageJson, "\"version\"\\s*:\\s*\"([^\"]+)\"");
      return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string ExtractVersionFromShaderFiles(string packagePath)
    {
      // Look for version patterns in shader files
      var shaderFiles = Directory.GetFiles(packagePath, "*.shader", SearchOption.AllDirectories);
      if (shaderFiles.Length == 0)
      {
        // Try to find any .cginc or .hlsl files that might contain version info
        shaderFiles = Directory
          .GetFiles(packagePath, "*.cginc", SearchOption.AllDirectories)
          .Concat(Directory.GetFiles(packagePath, "*.hlsl", SearchOption.AllDirectories))
          .ToArray();
      }

      foreach (var file in shaderFiles)
      {
        string content = File.ReadAllText(file);

        // Common patterns for version information in shader files
        Match match = Regex.Match(content, @"[Vv]ersion\s*([\d\.]+)");
        if (match.Success)
        {
          return match.Groups[1].Value;
        }

        // Try alternative pattern
        match = Regex.Match(content, @"v([\d\.]+)");
        if (match.Success)
        {
          return match.Groups[1].Value;
        }
      }

      return string.Empty;
    }

    // Classes for JSON deserialization
    public class CompatibilityData
    {
      [JsonProperty("compatibilityList")]
      public List<CompatibilityEntry> CompatibilityList { get; set; }
    }

    public class CompatibilityEntry
    {
      [JsonProperty("NoPackage")]
      public bool NoPackage { get; set; }

      [JsonProperty("shaderVersionRangeMax")]
      public string ShaderVersionRangeMax { get; set; }

      [JsonProperty("shaderVersionRangeMin")]
      public string ShaderVersionRangeMin { get; set; }

      [JsonProperty("SupportedUnityVersionsRange")]
      public string SupportedUnityVersionsRange { get; set; }
    }

    public class CompatibilityResult
    {
      public bool IsCompatible { get; set; }
      public string Message { get; set; }
      public bool IsUnknown { get; set; }
    }
  }
}
