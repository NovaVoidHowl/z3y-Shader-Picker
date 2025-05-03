using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace novavoidhowl.z3yshaderpicker
{
  // Class to represent a shader package version
  [Serializable]
  public class ShaderVersion
  {
    public string Version { get; set; }
    public string DisplayName { get; set; }
    public string DownloadUrl { get; set; }
    public string MinUnityVersion { get; set; }
    public string MaxUnityVersion { get; set; } // Optional, can be null for "latest"
    public string Description { get; set; }
  }

  // Class to hold list of available versions
  [Serializable]
  public class ShaderVersionList
  {
    public List<ShaderVersion> Versions { get; set; }
  }

  // Custom dropdown for version selection with compatibility styling
  public class StyledVersionDropdown : DropdownField
  {
    // List tracking compatibility status of each dropdown option
    private List<bool> optionCompatibilityStatus = new List<bool>();

    // Dictionary mapping display names to compatibility status
    private Dictionary<string, bool> compatibilityMap = new Dictionary<string, bool>();

    // Set compatibility status for all options
    public void SetCompatibilityStatus(List<bool> compatibilityStatus)
    {
      optionCompatibilityStatus = compatibilityStatus;

      // Update the compatibility map
      compatibilityMap.Clear();
      for (int i = 0; i < choices.Count && i < compatibilityStatus.Count; i++)
      {
        compatibilityMap[choices[i]] = compatibilityStatus[i];
      }

      // Apply styling to the current selection
      UpdateDisplayStyle();

      // Register callback to apply styling when value changes
      this.RegisterValueChangedCallback(OnValueChanged);
    }

    // Handle value changed events
    private void OnValueChanged(ChangeEvent<string> evt)
    {
      UpdateDisplayStyle();
    }

    // Update the visual style of the displayed value
    private void UpdateDisplayStyle()
    {
      if (string.IsNullOrEmpty(value) || !compatibilityMap.ContainsKey(value))
        return;

      // Apply styling based on compatibility
      if (compatibilityMap[value])
      {
        // Compatible version - normal style
        RemoveFromClassList("incompatible-version");
        AddToClassList("compatible-version");
      }
      else
      {
        // Incompatible version - grayed out style
        RemoveFromClassList("compatible-version");
        AddToClassList("incompatible-version");
      }
    }
  }

  public class z3yShaderVersionWindow : EditorWindow
  {
    // base window settings
    private const int windowBaseWidth = 450;
    private const int windowBaseHeight = 660;

    // settings panel window settings
    private const int settingsPanelWidth = 400;

    // no height for the settings panel as it inherits the height of the main window


    // Configurable delay for the compatibility check animation (in seconds)
    private const float checkDelay = 2.0f;

    // Service for GitHub API interactions
    private GitHubApiService githubApiService;

    // Combined list for display and selection
    private List<VersionInfo> combinedVersions = new List<VersionInfo>();

    // UI elements we need to reference across methods
    private DropdownField versionDropdown;
    private Button installButton;
    private Button viewReleaseButton;
    private Label installStatusLabel;
    private Toggle apiSettingsToggle;

    // Currently selected version index
    private int selectedVersionIndex = -1;

    // Flag to track if a version check is in progress
    private bool isVersionCheckInProgress = false;

    // Base URL for GitHub releases
    private const string GitHubReleasesBaseUrl = "https://github.com/z3y/shaders/releases/tag/";

    // Reference to main UI container
    private VisualElement rootContainer;
    private VisualElement mainContentContainer;
    private VisualElement settingsPanelContainer;

    // Flag to track if we're in the process of handling a window closing
    private bool handlingWindowClose = false;

    // Flag to track if settings panel is visible
    private bool isSettingsPanelVisible = false;

    // Add delegate variable for api status updates
    private EditorApplication.CallbackFunction updateApiStatusDelegate;

    [MenuItem("NVH/z3y Shaders/Version Picker")]
    public static void ShowWindow()
    {
      // Create a standalone window with OS title bar by using GetWindowWithRect and setting utility flag to true
      z3yShaderVersionWindow wnd = GetWindowWithRect<z3yShaderVersionWindow>(
        new Rect(0, 0, windowBaseWidth, windowBaseHeight),
        true,
        "z3y Shader Version Picker"
      );
      wnd.minSize = new Vector2(windowBaseWidth, windowBaseHeight);
      wnd.maxSize = new Vector2(windowBaseWidth + settingsPanelWidth, windowBaseHeight * 2); // Allow window to grow for settings panel
    }

    public void CreateGUI()
    {
      // Initialize GitHub API service
      githubApiService = new GitHubApiService();

      // Subscribe to rate limit exceeded event
      githubApiService.OnRateLimitExceeded += HandleRateLimitExceeded;

      // Import UXML
      var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
        "Packages/com.novavoidhowl.z3y-shader-picker/Editor/UI/z3yShaderVersionWindow.uxml"
      );
      if (visualTree == null)
      {
        Debug.LogError("Failed to load UXML file for z3y Shader Version Window");
        return;
      }
      // Store the root element
      rootVisualElement.AddToClassList("main-window-with-docked-settings");

      // Create the main content container
      mainContentContainer = new VisualElement();
      mainContentContainer.AddToClassList("main-content-container");
      rootVisualElement.Add(mainContentContainer);

      // Clone the tree into the main content container instead of root
      visualTree.CloneTree(mainContentContainer);

      // Import USS
      var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
        "Packages/com.novavoidhowl.z3y-shader-picker/Editor/UI/z3yShaderVersionWindow.uss"
      );
      if (styleSheet != null)
      {
        rootVisualElement.styleSheets.Add(styleSheet);
      }

      // Create the settings panel container (initially hidden)
      settingsPanelContainer = new VisualElement();
      settingsPanelContainer.AddToClassList("settings-panel-container");
      rootVisualElement.Add(settingsPanelContainer);

      // Create API Settings toggle
      CreateApiSettingsToggle();

      // Get references to UI elements (now in the main content container)
      var versionInfoLabel = mainContentContainer.Q<Label>("version-info");
      var compatibilityInfoLabel = mainContentContainer.Q<Label>("compatibility-info");
      var checkButton = mainContentContainer.Q<Button>("check-button");
      var versionBox = mainContentContainer.Q<Box>("version-selection-box");

      // Find the standard dropdown from UXML
      var standardDropdown = mainContentContainer.Q<DropdownField>("version-dropdown");
      if (standardDropdown != null)
      {
        // Create our custom styled dropdown
        var styledDropdown = new StyledVersionDropdown();
        styledDropdown.name = "version-dropdown";

        // Copy any existing properties from the standard dropdown
        styledDropdown.label = standardDropdown.label;
        styledDropdown.choices = standardDropdown.choices;
        styledDropdown.value = standardDropdown.value;

        // Replace the standard dropdown with our styled one
        if (standardDropdown.parent != null)
        {
          int index = standardDropdown.parent.IndexOf(standardDropdown);
          standardDropdown.parent.Insert(index, styledDropdown);
          standardDropdown.parent.Remove(standardDropdown);
        }

        // Set our reference to use the styled dropdown
        versionDropdown = styledDropdown;
      }
      else
      {
        // Fallback to standard dropdown if we couldn't find it
        Debug.LogWarning("Could not find standard dropdown to replace, falling back to standard DropdownField");
        versionDropdown = new DropdownField();
        versionDropdown.name = "version-dropdown";

        // Find where to insert it
        if (versionBox != null)
        {
          var label = versionBox.Q<Label>(className: "info-label");
          if (label != null)
          {
            versionBox.Insert(versionBox.IndexOf(label) + 1, versionDropdown);
          }
          else
          {
            versionBox.Add(versionDropdown);
          }
        }
      }

      installButton = mainContentContainer.Q<Button>("install-button");
      viewReleaseButton = mainContentContainer.Q<Button>("view-release-button");
      installStatusLabel = mainContentContainer.Q<Label>("install-status");

      // Hide the "Show Full Release Info" button by default
      if (viewReleaseButton != null)
      {
        viewReleaseButton.style.display = DisplayStyle.None;
      }

      // Try multiple ways to find the footer label
      var footerLabel = mainContentContainer.Q<Label>("footer-text");
      if (footerLabel == null)
      {
        Debug.Log("Could not find footer label by name, trying by class...");
        footerLabel = mainContentContainer.Q<Label>(className: "footer-text");
      }

      if (footerLabel == null)
      {
        Debug.Log("Could not find footer label by class, trying by hierarchy...");
        var footerElement = mainContentContainer.Q<VisualElement>(className: "footer");
        if (footerElement != null)
        {
          footerLabel = footerElement.Q<Label>();
          Debug.Log($"Found footer label through hierarchy: {footerLabel != null}");
        }
      }

      // Register event callbacks
      if (checkButton != null)
      {
        checkButton.RegisterCallback<ClickEvent>(_ => CheckAndDisplayVersion(versionInfoLabel, compatibilityInfoLabel));
      }

      // Setup version dropdown
      if (versionDropdown != null)
      {
        versionDropdown.RegisterCallback<ChangeEvent<string>>(evt =>
        {
          OnVersionSelected(evt.newValue, installStatusLabel);
        });
      }

      // Setup install button
      if (installButton != null)
      {
        installButton.RegisterCallback<ClickEvent>(_ =>
        {
          InstallSelectedVersion(installStatusLabel);
        });
      }

      // Setup view release button
      if (viewReleaseButton != null)
      {
        viewReleaseButton.RegisterCallback<ClickEvent>(_ =>
        {
          ViewSelectedVersionReleaseInfo();
        });
      }

      // Load available versions
      if (versionDropdown != null)
      {
        LoadAvailableVersions(versionDropdown);
      }

      // Check version on window open
      CheckAndDisplayVersion(versionInfoLabel, compatibilityInfoLabel);

      // Update footer with package version
      UpdateFooterWithPackageVersion(footerLabel);

      // Start the animation update for rotating the spinner icon when in checking state
      EditorApplication.update += UpdateSpinningAnimation;

      // Add event callback to monitor when the API Settings window is closed
      EditorApplication.update += CheckSettingsWindowStatus;
    }

    private void OnDisable()
    {
      // Remove the update callbacks when the window is closed
      EditorApplication.update -= UpdateSpinningAnimation;
      EditorApplication.update -= CheckSettingsWindowStatus;

      // Clean up any resources if settings panel is open
      if (isSettingsPanelVisible)
      {
        CloseSettingsPanel();
      }

      // Remove the API status update callback if it exists
      if (updateApiStatusDelegate != null)
      {
        EditorApplication.update -= updateApiStatusDelegate;
        updateApiStatusDelegate = null;
      }
    }

    // Checks if the settings window has been closed manually
    private void CheckSettingsWindowStatus()
    {
      if (apiSettingsToggle != null && apiSettingsToggle.value && !handlingWindowClose)
      {
        // The settings window was closed manually, update the toggle
        handlingWindowClose = true;
        apiSettingsToggle.value = false;
        handlingWindowClose = false;
      }
    }

    private float spinAngle = 0f;
    private bool isAnimating = false;

    private void UpdateSpinningAnimation()
    {
      if (!isAnimating)
        return;

      var statusIcon = rootVisualElement.Q<VisualElement>("status-icon");
      if (statusIcon != null)
      {
        spinAngle += 0.8f;
        if (spinAngle >= 360f)
          spinAngle = 0f;

        // Apply rotation
        statusIcon.style.rotate = new StyleRotate(new Rotate(spinAngle));
      }
    }

    private void CheckAndDisplayVersion(Label versionInfoLabel, Label compatibilityInfoLabel)
    {
      if (versionInfoLabel == null)
        return;

      // Get references to the single status indicator elements
      var statusIndicator = rootVisualElement.Q<VisualElement>("status-indicator");
      var statusIcon = rootVisualElement.Q<VisualElement>("status-icon");
      var statusText = rootVisualElement.Q<Label>("status-text");
      var messageLabel = rootVisualElement.Q<Label>("compatibility-message");

      // Set the version check in progress flag
      isVersionCheckInProgress = true;

      // Disable install button while checking
      installButton.SetEnabled(false);

      // Set to checking state
      messageLabel.style.display = DisplayStyle.None;
      SetStatusIndicator(statusIndicator, statusIcon, statusText, "status-checking", "spinner-icon", "Checking...");

      // Start animation
      isAnimating = true;
      spinAngle = 0f;

      // Immediately get and display version information
      string versionInfo = z3yShaderVersionDetector.GetInstalledVersion();
      string unityVersionInfo = z3yShaderVersionDetector.GetUnityVersion();

      // Display version information immediately
      string versionDisplayText = $"{unityVersionInfo}\n{versionInfo}";
      versionInfoLabel.text = versionDisplayText;

      // Get compatibility result immediately but don't display it yet
      var compatibilityResult = z3yShaderVersionDetector.CheckVersionCompatibility();

      // Use a coroutine-like approach with EditorApplication.update for the artificial delay
      float startTime = (float)EditorApplication.timeSinceStartup;
      EditorApplication.update += DelayedCheck;

      void DelayedCheck()
      {
        // Check if the configured delay has passed
        float elapsedTime = (float)EditorApplication.timeSinceStartup - startTime;
        if (elapsedTime < checkDelay)
        {
          // Still waiting, continue animation
          return;
        }

        // Unregister the update callback once we've reached the delay
        EditorApplication.update -= DelayedCheck;

        // End the version check in progress
        isVersionCheckInProgress = false;

        // Update status indicator based on compatibility result
        if (compatibilityResult.IsCompatible)
        {
          // Stop animation
          isAnimating = false;
          // Reset the rotation
          statusIcon.style.rotate = new StyleRotate(new Rotate(0f));

          SetStatusIndicator(statusIndicator, statusIcon, statusText, "status-compatible", "tick-icon", "Compatible");
        }
        else if (compatibilityResult.IsUnknown)
        {
          // Stop animation
          isAnimating = false;
          // Reset the rotation
          statusIcon.style.rotate = new StyleRotate(new Rotate(0f));

          SetStatusIndicator(statusIndicator, statusIcon, statusText, "status-unknown", "question-icon", "Unknown");

          // Show the additional message
          messageLabel.text = compatibilityResult.Message;
          messageLabel.style.display = DisplayStyle.Flex;
        }
        else
        {
          // Stop animation
          isAnimating = false;
          // Reset the rotation
          statusIcon.style.rotate = new StyleRotate(new Rotate(0f));

          SetStatusIndicator(
            statusIndicator,
            statusIcon,
            statusText,
            "status-incompatible",
            "warning-icon",
            "Incompatible"
          );

          // Show the additional message
          messageLabel.text = compatibilityResult.Message;
          messageLabel.style.display = DisplayStyle.Flex;
        }

        // After the check is complete, check compatibility of the currently selected version
        // This ensures the install button is only enabled for compatible versions
        if (selectedVersionIndex >= 0 && selectedVersionIndex < combinedVersions.Count)
        {
          CheckVersionCompatibilityForInstall(combinedVersions[selectedVersionIndex]);
        }
        else
        {
          // No version selected, so disable the install button
          installButton.SetEnabled(false);
        }
      }
    }

    // Helper method to update the status indicator
    private void SetStatusIndicator(
      VisualElement indicator,
      VisualElement icon,
      Label text,
      string statusClass,
      string iconClass,
      string statusText
    )
    {
      // Remove all status classes
      indicator.RemoveFromClassList("status-compatible");
      indicator.RemoveFromClassList("status-incompatible");
      indicator.RemoveFromClassList("status-unknown");
      indicator.RemoveFromClassList("status-checking");

      // Add the new status class
      indicator.AddToClassList(statusClass);

      // Remove all icon classes
      icon.RemoveFromClassList("tick-icon");
      icon.RemoveFromClassList("warning-icon");
      icon.RemoveFromClassList("question-icon");
      icon.RemoveFromClassList("spinner-icon");

      // Add the new icon class
      icon.AddToClassList(iconClass);

      // Update the text
      text.text = statusText;
    }

    private void UpdateFooterWithPackageVersion(Label footerLabel)
    {
      Debug.Log("UpdateFooterWithPackageVersion called");

      if (footerLabel == null)
      {
        Debug.LogWarning("Footer label is null - cannot update package version in footer");
        return;
      }

      string packageVersion = GetPackageVersion();
      Debug.Log($"Package version found: {packageVersion}");
      footerLabel.text = $"z3y Shader Version Picker by NovaVoidHowl | v{packageVersion}";
    }

    private string GetPackageVersion()
    {
      try
      {
        // Try multiple ways to find the package.json file
        string packageJsonPath = null;

        // Method 1: Find the package.json relative to this script
        string scriptPath = AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("z3yShaderVersionWindow")[0]);
        string directoryPath = Path.GetDirectoryName(scriptPath);
        string possiblePath = Path.Combine(directoryPath, "../../package.json").Replace('\\', '/');

        if (File.Exists(possiblePath))
        {
          packageJsonPath = possiblePath;
        }
        else
        {
          // Method 2: Try to find it directly with AssetDatabase
          string[] packageJsonGuids = AssetDatabase.FindAssets(
            "package t:TextAsset",
            new[] { "Packages/com.novavoidhowl.z3y-shader-picker" }
          );
          if (packageJsonGuids.Length > 0)
          {
            packageJsonPath = AssetDatabase.GUIDToAssetPath(packageJsonGuids[0]);
          }
        }

        if (!string.IsNullOrEmpty(packageJsonPath) && File.Exists(packageJsonPath))
        {
          string jsonContent = File.ReadAllText(packageJsonPath);
          //Debug.Log($"Package.json content: {jsonContent}");
          JObject packageJson = JObject.Parse(jsonContent);
          return packageJson["version"]?.ToString() ?? "Unknown";
        }
        else
        {
          Debug.LogError($"Could not find package.json file. Searched at: {possiblePath}");
        }
      }
      catch (System.Exception e)
      {
        Debug.LogError($"Failed to read package version: {e.Message}");
      }

      return "Unknown";
    }

    // Handle GitHub API rate limit exceeded event
    private void HandleRateLimitExceeded(TimeSpan timeUntilReset)
    {
      // Update UI to show rate limit information
      var versionDescription = rootVisualElement.Q<Label>("version-description");
      versionDescription.text =
        $"GitHub API rate limit exceeded. Please wait about {timeUntilReset.TotalMinutes:F0} minutes and try again.";
    }

    private async void LoadAvailableVersions(DropdownField versionDropdown)
    {
      try
      {
        // Show loading state
        versionDropdown.value = "Loading versions...";
        versionDropdown.SetEnabled(false);
        installButton.SetEnabled(false);
        viewReleaseButton.style.display = DisplayStyle.None;

        var versionDescription = rootVisualElement.Q<Label>("version-description");
        versionDescription.text = "Fetching available versions from GitHub...";

        // Fetch versions from GitHub API
        combinedVersions = await githubApiService.FetchVersionsAsync();

        if (combinedVersions.Count == 0)
        {
          // No versions found
          versionDropdown.value = "No versions available";
          versionDescription.text = "Could not retrieve version information from GitHub.";
          return;
        }

        // Create compatibility status list for dropdown items
        List<bool> compatibilityStatus = new List<bool>();

        // Check compatibility for each version
        string unityVersion = Application.unityVersion;
        string unityMajorVersion = ExtractMajorVersion(unityVersion);
        TextAsset jsonAsset = Resources.Load<TextAsset>("z3ysp/versionsupport");

        // Load the compatibility data if available
        z3yShaderVersionDetector.CompatibilityData compatibilityData = null;
        if (jsonAsset != null)
        {
          try
          {
            compatibilityData = JsonConvert.DeserializeObject<z3yShaderVersionDetector.CompatibilityData>(
              jsonAsset.text
            );
          }
          catch (Exception ex)
          {
            Debug.LogError($"Error parsing compatibility data: {ex.Message}");
          }
        }

        // Find compatible Unity version entry
        z3yShaderVersionDetector.CompatibilityEntry compatibleUnityVersionEntry = null;
        if (compatibilityData != null && compatibilityData.CompatibilityList != null)
        {
          foreach (var entry in compatibilityData.CompatibilityList)
          {
            if (entry.SupportedUnityVersionsRange.Contains(unityMajorVersion))
            {
              compatibleUnityVersionEntry = entry;
              break;
            }
          }
        }

        // If we found compatibility data for this Unity version, check each shader version
        Version latestCompatibleVersion = null;
        int latestCompatibleIndex = 0;

        for (int i = 0; i < combinedVersions.Count; i++)
        {
          bool isCompatible = true;
          var version = combinedVersions[i];
          string versionNumber = ExtractVersionFromTag(version.RawTagName);

          if (!string.IsNullOrEmpty(versionNumber) && compatibleUnityVersionEntry != null)
          {
            isCompatible = IsVersionInRange(versionNumber, compatibleUnityVersionEntry);

            // Mark compatibility in version info object
            version.IsCompatible = isCompatible;

            // Track latest compatible version
            if (isCompatible)
            {
              try
              {
                Version currentVersion = new Version(versionNumber);
                if (latestCompatibleVersion == null || currentVersion > latestCompatibleVersion)
                {
                  latestCompatibleVersion = currentVersion;
                  latestCompatibleIndex = i;
                }
              }
              catch (Exception ex)
              {
                Debug.LogWarning($"Error parsing version number {versionNumber}: {ex.Message}");
              }
            }
          }

          compatibilityStatus.Add(isCompatible);
        }

        // Populate dropdown with version names
        List<string> choices = combinedVersions.Select(v => v.DisplayName).ToList();

        // Set the dropdown choices
        versionDropdown.choices = choices;
        versionDropdown.SetEnabled(true);

        // Apply compatibility styling if using our custom dropdown
        if (versionDropdown is StyledVersionDropdown styledDropdown)
        {
          styledDropdown.SetCompatibilityStatus(compatibilityStatus);
        }

        // Auto-select the latest compatible version or first version if none are compatible
        if (choices.Count > 0)
        {
          // If we found a compatible version, select it; otherwise select the first one
          if (latestCompatibleVersion != null)
          {
            versionDropdown.value = combinedVersions[latestCompatibleIndex].DisplayName;
            selectedVersionIndex = latestCompatibleIndex;
          }
          else
          {
            versionDropdown.value = choices[0];
            selectedVersionIndex = 0;
          }

          OnVersionSelected(versionDropdown.value, rootVisualElement.Q<Label>("install-status"));
        }
      }
      catch (System.Exception e)
      {
        Debug.LogError($"Error in LoadAvailableVersions: {e.Message}");
        versionDropdown.value = "Error loading versions";
        rootVisualElement.Q<Label>("version-description").text =
          "Error fetching version information. Please try again later.";
      }
    }

    private void OnVersionSelected(string selectedDisplayName, Label installStatusLabel)
    {
      // Find the selected version by display name
      selectedVersionIndex = combinedVersions.FindIndex(v => v.DisplayName == selectedDisplayName);

      // Update UI with selected version info
      if (selectedVersionIndex >= 0 && selectedVersionIndex < combinedVersions.Count)
      {
        var selectedVersion = combinedVersions[selectedVersionIndex];
        var versionDescription = rootVisualElement.Q<Label>("version-description");

        // Get full description
        string fullDescription = selectedVersion.Description ?? "";

        // Check if the description is long and needs to be truncated
        bool isDescriptionLong = fullDescription.Length > 150;

        // Show truncated version description (release notes)
        string displayDescription = isDescriptionLong ? fullDescription.Substring(0, 147) + "..." : fullDescription;
        versionDescription.text = displayDescription;

        // Show or hide the "Show Full Release Info" button based on description length
        viewReleaseButton.style.display = isDescriptionLong ? DisplayStyle.Flex : DisplayStyle.None;

        // Update install status
        if (installStatusLabel != null)
        {
          installStatusLabel.text = $"Selected: {selectedVersion.RawTagName}";
          installStatusLabel.style.display = DisplayStyle.Flex;
        }

        // Disable the install button if currently performing a version check
        if (isVersionCheckInProgress)
        {
          installButton.SetEnabled(false);
        }
        else
        {
          // Check if the selected version is compatible with the current Unity version
          CheckVersionCompatibilityForInstall(selectedVersion);
        }
      }
      else
      {
        rootVisualElement.Q<Label>("version-description").text = "";
        installButton.SetEnabled(false);
        viewReleaseButton.style.display = DisplayStyle.None;

        if (installStatusLabel != null)
        {
          installStatusLabel.text = "No version selected";
          installStatusLabel.style.display = DisplayStyle.Flex;
        }
      }
    }

    // Open the GitHub release page for the selected version
    private void ViewSelectedVersionReleaseInfo()
    {
      if (selectedVersionIndex < 0 || selectedVersionIndex >= combinedVersions.Count)
      {
        Debug.LogWarning("No version selected to view release info.");
        return;
      }

      var selectedVersion = combinedVersions[selectedVersionIndex];

      // Construct the URL to the GitHub release page
      string url = GitHubReleasesBaseUrl + selectedVersion.RawTagName;

      // Open the URL in the default browser
      Application.OpenURL(url);

      Debug.Log($"Opening GitHub release page: {url}");
    }

    private async void InstallSelectedVersion(Label installStatusLabel)
    {
      if (selectedVersionIndex < 0 || selectedVersionIndex >= combinedVersions.Count)
      {
        installStatusLabel.text = "No version selected to install.";
        installStatusLabel.style.display = DisplayStyle.Flex;
        return;
      }

      var selectedVersion = combinedVersions[selectedVersionIndex];

      try
      {
        // Update UI to show installation progress
        installStatusLabel.text = $"Installing {selectedVersion.RawTagName}...";
        installStatusLabel.style.display = DisplayStyle.Flex;
        installButton.SetEnabled(false);
        viewReleaseButton.SetEnabled(false);

        // Path to the manifest.json file
        string manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");

        if (!File.Exists(manifestPath))
        {
          Debug.LogError("Cannot find manifest.json file at path: " + manifestPath);
          installStatusLabel.text = "Error: Cannot find manifest.json file.";
          installButton.SetEnabled(true);
          viewReleaseButton.SetEnabled(true);
          return;
        }

        // Read the manifest.json file
        string manifestContent = File.ReadAllText(manifestPath);

        // Parse the manifest.json file
        JObject manifestJson = JObject.Parse(manifestContent);

        // Check if dependencies node exists
        if (manifestJson["dependencies"] == null)
        {
          Debug.LogError("Dependencies node doesn't exist in manifest.json");
          installStatusLabel.text = "Error: Invalid manifest.json structure.";
          installButton.SetEnabled(true);
          viewReleaseButton.SetEnabled(true);
          return;
        }

        // Get the dependencies node
        JObject dependencies = (JObject)manifestJson["dependencies"];

        // Check if the shader is already in the dependencies
        bool shaderAlreadyInstalled = dependencies["com.z3y.shaders"] != null;

        // Create the GitHub URL with the specific version tag
        string shaderUrl = $"https://github.com/z3y/shaders.git#{selectedVersion.RawTagName}";

        // Set or update the shader version
        dependencies["com.z3y.shaders"] = shaderUrl;

        // Save the updated manifest.json file
        File.WriteAllText(manifestPath, manifestJson.ToString());

        // Show success message
        string message = shaderAlreadyInstalled
          ? $"Updated z3y shaders to version {selectedVersion.RawTagName}. Unity will automatically download the package."
          : $"Added z3y shaders version {selectedVersion.RawTagName} to manifest. Unity will automatically download the package.";

        installStatusLabel.text = message;

        // Force Unity to refresh and detect the change to the manifest.json file
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        // Force Unity to reimport the package manifest specifically
        UnityEditor.PackageManager.Client.Resolve();

        // Show a dialog to inform the user (after the refresh calls to ensure Unity has started processing the update)
        EditorUtility.DisplayDialog(
          "Shader Update Complete",
          $"The z3y shader package has been {(shaderAlreadyInstalled ? "updated" : "added")} in your project's manifest.json with version {selectedVersion.RawTagName}.\n\nUnity is now refreshing the package manager to apply the changes.",
          "OK"
        );

        // Enable the buttons again
        installButton.SetEnabled(true);
        viewReleaseButton.SetEnabled(true);

        // Schedule a refresh of the version check (with a delay to allow Unity time to update the package)
        EditorApplication.delayCall += () =>
        {
          // Try refreshing again after a short delay to ensure the package has been updated
          EditorApplication.delayCall += () =>
          {
            var versionInfoLabel = rootVisualElement.Q<Label>("version-info");
            var compatibilityInfoLabel = rootVisualElement.Q<Label>("compatibility-info");
            CheckAndDisplayVersion(versionInfoLabel, compatibilityInfoLabel);
          };
        };
      }
      catch (System.Exception e)
      {
        Debug.LogError($"Error installing shader: {e.Message}");
        installStatusLabel.text = $"Error: {e.Message}";
        installButton.SetEnabled(true);
        viewReleaseButton.SetEnabled(true);
      }
    }

    // Create the API Settings toggle
    private void CreateApiSettingsToggle()
    {
      // Create container for the toggle
      var toggleContainer = new VisualElement();
      toggleContainer.AddToClassList("api-settings-container");

      // Create label for the toggle
      var settingsLabel = new Label("API Settings");
      settingsLabel.AddToClassList("api-settings-label");

      // Create a simple custom toggle with direct DOM manipulation
      var toggleElement = new VisualElement();
      toggleElement.AddToClassList("custom-toggle-container");

      // Create the thumb (circle) of the toggle
      var toggleThumb = new VisualElement();
      toggleThumb.AddToClassList("custom-toggle-thumb");

      // Add the thumb to the toggle container
      toggleElement.Add(toggleThumb);

      // Register click event directly on the toggle container
      toggleElement.RegisterCallback<MouseDownEvent>(evt =>
      {
        // Toggle state
        isSettingsPanelVisible = !isSettingsPanelVisible;

        // Update visual state immediately
        if (isSettingsPanelVisible)
        {
          toggleElement.AddToClassList("custom-toggle-on");

          // First make settings panel visible to ensure proper layout calculation
          settingsPanelContainer.AddToClassList("settings-panel-visible");

          // Calculate new width while keeping the same position
          float newWidth = windowBaseWidth + settingsPanelWidth;

          // Force layout update to get correct size
          rootVisualElement.style.width = newWidth;

          // Resize the window
          position = new Rect(position.x, position.y, newWidth, position.height);

          // Open the settings window
          EditorApplication.delayCall += () =>
          {
            OpenApiSettingsWindow();
          };
        }
        else
        {
          toggleElement.RemoveFromClassList("custom-toggle-on");

          // Hide settings panel
          settingsPanelContainer.RemoveFromClassList("settings-panel-visible");

          // Force layout update
          rootVisualElement.style.width = windowBaseWidth;

          // Resize window back to original size
          position = new Rect(position.x, position.y, windowBaseWidth, position.height);

          // Close settings panel
          EditorApplication.delayCall += () =>
          {
            CloseSettingsPanel();
          };
        }

        // Prevent event propagation
        evt.StopPropagation();
      });

      // Store reference to toggle for API compatibility
      apiSettingsToggle = new Toggle { value = false };
      apiSettingsToggle.style.display = DisplayStyle.None;

      // Add elements to container
      toggleContainer.Add(settingsLabel);
      toggleContainer.Add(toggleElement);
      toggleContainer.Add(apiSettingsToggle); // Hidden

      // Add to the main content container (not the root)
      mainContentContainer.Add(toggleContainer);
    }

    // Public method called when the API settings window is closed
    public void OnApiSettingsWindowClosed()
    {
      // Find and update toggle
      EditorApplication.delayCall += () =>
      {
        var toggleElement = rootVisualElement.Q<VisualElement>(className: "custom-toggle-container");
        if (toggleElement != null)
        {
          handlingWindowClose = true;
          toggleElement.RemoveFromClassList("custom-toggle-on");
          apiSettingsToggle.value = false;
          handlingWindowClose = false;
        }
      };
    }

    // Open the API settings window
    private void OpenApiSettingsWindow()
    {
      // Ensure the API service is initialized before opening the settings window
      if (githubApiService == null)
      {
        Debug.LogError("Cannot open API settings: GitHub API service is not initialized");
        EditorUtility.DisplayDialog("Error", "GitHub API service is not initialized. Try restarting the window.", "OK");
        return;
      }

      // Toggle the panel visibility
      isSettingsPanelVisible = true;

      // Make settings panel visible
      settingsPanelContainer.AddToClassList("settings-panel-visible");

      // Clear any existing content
      settingsPanelContainer.Clear();

      // Create title for the panel
      var titleLabel = new Label("GitHub API Settings");
      titleLabel.style.fontSize = 16;
      titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
      titleLabel.style.marginTop = 10;
      titleLabel.style.marginBottom = 15;
      titleLabel.style.alignSelf = Align.Center;
      settingsPanelContainer.Add(titleLabel);

      // Create main container with padding
      var mainContainer = new VisualElement();
      mainContainer.style.paddingLeft = 10;
      mainContainer.style.paddingRight = 10;
      mainContainer.style.paddingBottom = 10;
      settingsPanelContainer.Add(mainContainer);

      // Create a refresh section for GitHub API data
      var refreshSection = new VisualElement();
      refreshSection.style.marginTop = 10;
      refreshSection.style.marginBottom = 10;
      refreshSection.style.flexDirection = FlexDirection.Row;
      refreshSection.style.justifyContent = Justify.SpaceBetween;

      // Create the API status label
      var apiStatusLabel = new Label();
      apiStatusLabel.style.flexGrow = 1;
      apiStatusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      apiStatusLabel.text = githubApiService.GetRateLimitInfo();
      if (githubApiService.HasValidCache())
      {
        apiStatusLabel.text += "\n" + githubApiService.GetCacheTimeInfo();
      }

      // Create the refresh button
      var refreshButton = new Button(() => RefreshVersionData(apiStatusLabel));
      refreshButton.text = "Refresh";
      refreshButton.tooltip = "Refresh version data from GitHub API";

      refreshSection.Add(apiStatusLabel);
      refreshSection.Add(refreshButton);

      // Add the refresh section to the main container
      mainContainer.Add(refreshSection);

      // Create the token input field
      var tokenContainer = new VisualElement();
      tokenContainer.style.flexDirection = FlexDirection.Row;
      tokenContainer.style.marginTop = 15;

      var githubTokenField = new TextField("GitHub Token");
      githubTokenField.tooltip = "Enter your GitHub Personal Access Token to increase API rate limits";
      githubTokenField.style.flexGrow = 1;
      githubTokenField.isPasswordField = true; // Hide the token for security

      // Set initial value if token is already saved
      if (githubApiService.HasToken())
      {
        githubTokenField.value = "••••••••••••••••••••••"; // Masked placeholder
      }

      tokenContainer.Add(githubTokenField);
      mainContainer.Add(tokenContainer);

      // Create button container
      var buttonContainer = new VisualElement();
      buttonContainer.style.flexDirection = FlexDirection.Row;
      buttonContainer.style.marginTop = 10;

      // Create token status label
      var tokenStatusLabel = new Label();
      tokenStatusLabel.style.marginTop = 10;
      tokenStatusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      tokenStatusLabel.text = githubApiService.HasToken()
        ? "Using authenticated GitHub API (5,000 requests per hour)"
        : "Using unauthenticated GitHub API (60 requests per hour)";

      // Create save token button
      var saveTokenButton = new Button(() => SaveGitHubToken(githubTokenField, tokenStatusLabel));
      saveTokenButton.text = "Save Token";
      saveTokenButton.style.flexGrow = 1;

      // Create clear token button
      var clearTokenButton = new Button(() => ClearGitHubToken(githubTokenField, tokenStatusLabel));
      clearTokenButton.text = "Clear Token";
      clearTokenButton.style.flexGrow = 1;

      buttonContainer.Add(saveTokenButton);
      buttonContainer.Add(clearTokenButton);
      mainContainer.Add(buttonContainer);

      mainContainer.Add(tokenStatusLabel);

      // Add instructions
      var instructionsContainer = new VisualElement();
      instructionsContainer.style.marginTop = 20;
      instructionsContainer.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.2f);
      instructionsContainer.style.paddingTop = 5;
      instructionsContainer.style.paddingBottom = 5;
      instructionsContainer.style.paddingLeft = 10;
      instructionsContainer.style.paddingRight = 10;

      var instructionsLabel = new Label("You can create a GitHub token at: https://github.com/settings/tokens");
      instructionsLabel.style.fontSize = 11;
      instructionsLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      instructionsContainer.Add(instructionsLabel);

      // Add information about required scopes
      var scopesLabel = new Label("Only 'public_repo' scope is needed for this tool");
      scopesLabel.style.fontSize = 11;
      scopesLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      instructionsContainer.Add(scopesLabel);

      mainContainer.Add(instructionsContainer);

      // Create close button at the bottom
      var closeContainer = new VisualElement();
      closeContainer.style.marginTop = 20;
      closeContainer.style.alignItems = Align.Center;

      var closeButton = new Button(() => CloseSettingsPanel());
      closeButton.text = "Close";
      closeButton.style.width = 120;

      closeContainer.Add(closeButton);
      mainContainer.Add(closeContainer);

      // Setup update for the API status label
      EditorApplication.update += UpdateApiStatusLabel;

      // Store delegate to be able to remove it later
      updateApiStatusDelegate = UpdateApiStatusLabel;

      void UpdateApiStatusLabel()
      {
        // Only update once per second to avoid performance impact
        if (Time.frameCount % 30 == 0 && apiStatusLabel != null)
        {
          string status = githubApiService.GetRateLimitInfo();
          if (githubApiService.HasValidCache())
          {
            status += "\n" + githubApiService.GetCacheTimeInfo();
          }
          apiStatusLabel.text = status;
        }
      }
    }

    // Public method to refresh version list from the API settings window
    public void RefreshVersionList()
    {
      LoadAvailableVersions(versionDropdown);
    }

    // Method to close the settings panel
    private void CloseSettingsPanel()
    {
      // Hide the settings panel
      settingsPanelContainer.RemoveFromClassList("settings-panel-visible");
      isSettingsPanelVisible = false;

      // Remove the API status update callback
      if (updateApiStatusDelegate != null)
      {
        EditorApplication.update -= updateApiStatusDelegate;
        updateApiStatusDelegate = null;
      }

      // Update toggle state
      var toggleElement = rootVisualElement.Q<VisualElement>(className: "custom-toggle-container");
      if (toggleElement != null)
      {
        handlingWindowClose = true;
        toggleElement.RemoveFromClassList("custom-toggle-on");
        apiSettingsToggle.value = false;
        handlingWindowClose = false;
      }
    }

    // Method to refresh version data
    private async void RefreshVersionData(Label apiStatusLabel)
    {
      if (apiStatusLabel == null)
        return;

      // Show loading state
      apiStatusLabel.text = "Fetching latest data from GitHub...";

      try
      {
        // Force refresh data from GitHub
        await githubApiService.FetchRateLimitAsync();

        // Refresh versions with forceRefresh=true
        await githubApiService.FetchVersionsAsync(true);

        // Update status
        apiStatusLabel.text = "Data refreshed successfully\n" + githubApiService.GetRateLimitInfo();

        // Refresh the version list
        RefreshVersionList();
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error refreshing version data: {ex.Message}");
        apiStatusLabel.text = "Error refreshing data: " + ex.Message;
      }
    }

    // Save the GitHub token
    private void SaveGitHubToken(TextField githubTokenField, Label tokenStatusLabel)
    {
      if (string.IsNullOrWhiteSpace(githubTokenField.value) || githubTokenField.value == "••••••••••••••••••••••")
      {
        tokenStatusLabel.text = "Please enter a valid GitHub token";
        return;
      }

      // Save the token in the service
      githubApiService.SetGitHubToken(githubTokenField.value);

      // Update the UI
      tokenStatusLabel.text = "Token saved. Using authenticated GitHub API (5,000 requests per hour)";
      githubTokenField.value = "••••••••••••••••••••••"; // Mask the token

      // Refresh the version list
      RefreshVersionList();
    }

    // Clear the GitHub token
    private void ClearGitHubToken(TextField githubTokenField, Label tokenStatusLabel)
    {
      // Clear the token in the service
      githubApiService.SetGitHubToken("");

      // Update the UI
      tokenStatusLabel.text = "Token removed. Using unauthenticated GitHub API (60 requests per hour)";
      githubTokenField.value = "";

      // Refresh the version list
      RefreshVersionList();
    }

    // Check compatibility of a selected version against the current Unity version
    private void CheckVersionCompatibilityForInstall(VersionInfo selectedVersion)
    {
      // Get the current Unity version
      string unityVersion = Application.unityVersion;
      string unityMajorVersion = ExtractMajorVersion(unityVersion);

      // Extract version number from tag
      string versionNumber = ExtractVersionFromTag(selectedVersion.RawTagName);
      if (string.IsNullOrEmpty(versionNumber))
      {
        // If we can't determine version, allow installation
        installButton.SetEnabled(true);
        return;
      }

      // Get compatibility data from Resource
      TextAsset jsonAsset = Resources.Load<TextAsset>("z3ysp/versionsupport");
      if (jsonAsset == null)
      {
        // If we can't load compatibility data, allow installation
        installButton.SetEnabled(true);
        return;
      }

      try
      {
        // Parse the compatibility data
        var compatibilityData = JsonConvert.DeserializeObject<z3yShaderVersionDetector.CompatibilityData>(
          jsonAsset.text
        );
        if (compatibilityData == null || compatibilityData.CompatibilityList.Count == 0)
        {
          // If we can't parse compatibility data, allow installation
          installButton.SetEnabled(true);
          return;
        }

        bool isVersionCompatible = false;

        // Find applicable compatibility entry for the current Unity version
        foreach (var entry in compatibilityData.CompatibilityList)
        {
          // Check if this entry applies to our Unity version
          if (entry.SupportedUnityVersionsRange.Contains(unityMajorVersion))
          {
            // Check if the version is in the compatible range
            isVersionCompatible = IsVersionInRange(versionNumber, entry);
            break;
          }
        }

        // Enable/disable install button based on compatibility check
        installButton.SetEnabled(isVersionCompatible);

        // Update status label to indicate why button is disabled
        if (!isVersionCompatible)
        {
          var installStatusLabel = rootVisualElement.Q<Label>("install-status");
          if (installStatusLabel != null)
          {
            installStatusLabel.text =
              $"Selected: {selectedVersion.RawTagName} (incompatible with Unity {unityVersion})";
          }
        }
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error checking version compatibility: {ex.Message}");
        // If there was an error, allow installation
        installButton.SetEnabled(true);
      }
    }

    // Helper method to extract major version from Unity version
    private string ExtractMajorVersion(string unityVersion)
    {
      // Extract major version like "2022.3" from "2022.3.12f1"
      Match match = Regex.Match(unityVersion, @"(\d+\.\d+)");
      return match.Success ? match.Groups[1].Value : unityVersion;
    }

    // Helper method to extract version number from tag name
    private string ExtractVersionFromTag(string tagName)
    {
      // Try to match patterns like "v3.3.1" or "3.3.1"
      Match match = Regex.Match(tagName, @"v?(\d+\.\d+\.\d+)");
      return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // Check if a version is within the range specified in the compatibility entry
    private bool IsVersionInRange(string versionNumber, z3yShaderVersionDetector.CompatibilityEntry entry)
    {
      try
      {
        // Parse version strings to comparable values
        Version currentVersion = new Version(versionNumber);

        // If NoPackage is true, this version shouldn't be available
        if (entry.NoPackage)
        {
          return false;
        }

        // Check minimum version requirement
        if (!string.IsNullOrEmpty(entry.ShaderVersionRangeMin) && entry.ShaderVersionRangeMin != "N/A")
        {
          Version minVersion = new Version(entry.ShaderVersionRangeMin);
          if (currentVersion < minVersion)
          {
            return false;
          }
        }

        // Check maximum version constraint if specified
        if (!string.IsNullOrEmpty(entry.ShaderVersionRangeMax) && entry.ShaderVersionRangeMax != "N/A")
        {
          Version maxVersion = new Version(entry.ShaderVersionRangeMax);
          if (currentVersion > maxVersion)
          {
            return false;
          }
        }

        // If we passed all checks, the version is compatible
        return true;
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error comparing version numbers: {ex.Message}");
        // If there was an error parsing versions, allow installation
        return true;
      }
    }
  }
}
