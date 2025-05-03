using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace novavoidhowl.z3yshaderpicker
{
  public class GitHubApiSettingsWindow : EditorWindow
  {
    // Service for GitHub API interactions - shared with main window
    private GitHubApiService githubApiService;

    // UI elements for token management
    private TextField githubTokenField;
    private Button saveTokenButton;
    private Button clearTokenButton;
    private Label tokenStatusLabel;
    private Label apiStatusLabel;
    private Button refreshButton;
    private Button closeButton;

    // Reference to the main window to update it when needed
    private z3yShaderVersionWindow mainWindow;

    // Flag to prevent double notification when window is closed
    private bool isClosing = false;

    // Public method to initialize the window with required references
    public void Initialize(GitHubApiService apiService, z3yShaderVersionWindow mainWnd)
    {
      // Save references before showing the window
      githubApiService = apiService;
      mainWindow = mainWnd;

      // Create the UI after the references are set
      CreateGUI();
    }

    public void OnEnable()
    {
      // Only create UI if we have a valid API service
      if (githubApiService != null && mainWindow != null)
      {
        CreateGUI();
      }
    }

    public void OnDisable()
    {
      // Remove update callback when window is closed
      EditorApplication.update -= UpdateApiStatusLabel;

      // Notify main window that this window was closed if not already doing so
      if (!isClosing && mainWindow != null)
      {
        isClosing = true;
        mainWindow.OnApiSettingsWindowClosed();
      }
    }

    private void CreateGUI()
    {
      // If API service isn't set, close window
      if (githubApiService == null)
      {
        Debug.LogError("GitHub API service is null in settings window");
        Close();
        return;
      }

      var root = rootVisualElement;

      // Clear any existing UI elements
      root.Clear();

      // Add title
      var titleLabel = new Label("GitHub API Settings");
      titleLabel.style.fontSize = 16;
      titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
      titleLabel.style.marginTop = 10;
      titleLabel.style.marginBottom = 15;
      titleLabel.style.alignSelf = Align.Center;
      root.Add(titleLabel);

      // Create main container with padding
      var mainContainer = new VisualElement();
      mainContainer.style.paddingLeft = 10;
      mainContainer.style.paddingRight = 10;
      mainContainer.style.paddingBottom = 10;
      root.Add(mainContainer);

      // Create a refresh section for GitHub API data
      var refreshSection = new VisualElement();
      refreshSection.style.marginTop = 10;
      refreshSection.style.marginBottom = 10;
      refreshSection.style.flexDirection = FlexDirection.Row;
      refreshSection.style.justifyContent = Justify.SpaceBetween;

      // Create the API status label
      apiStatusLabel = new Label();
      apiStatusLabel.style.flexGrow = 1;
      apiStatusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      apiStatusLabel.text = githubApiService.GetRateLimitInfo();
      if (githubApiService.HasValidCache())
      {
        apiStatusLabel.text += "\n" + githubApiService.GetCacheTimeInfo();
      }

      // Create the refresh button
      refreshButton = new Button(() => RefreshVersionData());
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

      githubTokenField = new TextField("GitHub Token");
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

      // Create save token button
      saveTokenButton = new Button(() => SaveGitHubToken());
      saveTokenButton.text = "Save Token";
      saveTokenButton.style.flexGrow = 1;

      // Create clear token button
      clearTokenButton = new Button(() => ClearGitHubToken());
      clearTokenButton.text = "Clear Token";
      clearTokenButton.style.flexGrow = 1;

      buttonContainer.Add(saveTokenButton);
      buttonContainer.Add(clearTokenButton);
      mainContainer.Add(buttonContainer);

      // Create token status label
      tokenStatusLabel = new Label();
      tokenStatusLabel.style.marginTop = 10;
      tokenStatusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
      tokenStatusLabel.text = githubApiService.HasToken()
        ? "Using authenticated GitHub API (5,000 requests per hour)"
        : "Using unauthenticated GitHub API (60 requests per hour)";
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

      closeButton = new Button(() => Close());
      closeButton.text = "Close";
      closeButton.style.width = 120;

      closeContainer.Add(closeButton);
      mainContainer.Add(closeContainer);

      // Schedule regular updates of the API status label
      EditorApplication.update += UpdateApiStatusLabel;
    }

    // Update the API status label with current rate limit information
    private void UpdateApiStatusLabel()
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

    // Handle refresh button click
    private async void RefreshVersionData()
    {
      // Show loading state
      refreshButton.SetEnabled(false);
      refreshButton.text = "Refreshing...";
      apiStatusLabel.text = "Fetching latest data from GitHub...";

      try
      {
        // Force refresh data from GitHub
        await FetchRateLimitAndUpdateDisplay();

        // Refresh versions with forceRefresh=true
        await githubApiService.FetchVersionsAsync(true);

        // Update status
        apiStatusLabel.text = "Data refreshed successfully\n" + githubApiService.GetRateLimitInfo();

        // Signal main window to refresh its UI
        if (mainWindow != null)
        {
          mainWindow.RefreshVersionList();
        }
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error refreshing version data: {ex.Message}");
        apiStatusLabel.text = "Error refreshing data: " + ex.Message;
      }
      finally
      {
        // Reset button state
        refreshButton.SetEnabled(true);
        refreshButton.text = "Refresh";
      }
    }

    // Fetch current rate limit information and update the display
    private async Task FetchRateLimitAndUpdateDisplay()
    {
      try
      {
        // Fetch current rate limit
        await githubApiService.FetchRateLimitAsync();

        // Update display
        apiStatusLabel.text = githubApiService.GetRateLimitInfo();
      }
      catch (Exception ex)
      {
        Debug.LogError($"Error fetching rate limit: {ex.Message}");
      }
    }

    // Save the GitHub token
    private void SaveGitHubToken()
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

      // Signal main window to refresh its UI
      if (mainWindow != null)
      {
        mainWindow.RefreshVersionList();
      }
    }

    // Clear the GitHub token
    private void ClearGitHubToken()
    {
      // Clear the token in the service
      githubApiService.SetGitHubToken("");

      // Update the UI
      tokenStatusLabel.text = "Token removed. Using unauthenticated GitHub API (60 requests per hour)";
      githubTokenField.value = "";

      // Signal main window to refresh its UI
      if (mainWindow != null)
      {
        mainWindow.RefreshVersionList();
      }
    }

    // Override Close to notify the main window
    public new void Close()
    {
      // Mark as closing to prevent double notification
      isClosing = true;

      // Notify main window before closing
      if (mainWindow != null)
      {
        mainWindow.OnApiSettingsWindowClosed();
      }

      // Call base Close method
      base.Close();
    }
  }
}
