using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Cmux.Core.Config;
using Cmux.Core.Services;

namespace Cmux.Views;

public partial class SettingsWindow : Window
{
    private const string SecretMask = "********";
    private bool _suppressTerminalColorEvents;
    private bool _suppressThemeSync;
    private bool _clearOpenAiKey;
    private bool _clearAnthropicKey;
    private bool _clearExaKey;
    private readonly List<AgentCustomToolConfig> _customToolsDraft = [];
    private readonly List<AgentMcpServerConfig> _mcpServersDraft = [];

    public SettingsWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        PopulateThemes();
        LoadSettings();
        ShowSection("Appearance");
    }

    private void PopulateThemes()
    {
        ThemeCombo.ItemsSource = TerminalThemes.Names;
        TerminalThemePresetCombo.ItemsSource = TerminalThemes.Names;
        CursorStyleCombo.ItemsSource = new[] { "bar", "block", "underline" };

        var fontFamilies = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name)
            .ToList();
        FontFamilyCombo.ItemsSource = fontFamilies;
        AgentChatFontFamilyCombo.ItemsSource = fontFamilies;

        // Detect available shells
        var shells = DetectShells();
        ShellCombo.ItemsSource = shells;
        ShellCombo.DisplayMemberPath = "Name";
        ShellCombo.SelectedValuePath = "Path";

        // Detect system theme
        var isLight = IsSystemLightTheme();
        SystemThemeText.Text = isLight ? "Light" : "Dark";
    }

    private void LoadSettings()
    {
        LoadSettingsFrom(SettingsService.Current);
    }

    private void LoadSettingsFrom(CmuxSettings s)
    {
        FontFamilyCombo.SelectedItem = s.FontFamily;
        if (FontFamilyCombo.SelectedItem == null)
            FontFamilyCombo.Text = s.FontFamily;

        FontSizeSlider.Value = Math.Clamp(s.FontSize, 9, 28);
        UpdateFontSizeText();

        _suppressThemeSync = true;
        ThemeCombo.SelectedItem = s.ThemeName;
        TerminalThemePresetCombo.SelectedItem = s.ThemeName;
        _suppressThemeSync = false;

        OpacitySlider.Value = s.Opacity;
        UpdateOpacityText();
        CursorStyleCombo.SelectedItem = s.CursorStyle;
        CursorBlinkCheck.IsChecked = s.CursorBlink;

        // Shell selection (set after PopulateThemes populates the combo)
        var shellPath = s.DefaultShell;
        var shells = ShellCombo.ItemsSource as List<ShellInfo>;
        var shellIndex = shells?.FindIndex(sh => sh.Path == shellPath) ?? -1;
        ShellCombo.SelectedIndex = shellIndex >= 0 ? shellIndex : 0;

        ShellArgsBox.Text = s.DefaultShellArgs;
        ScrollbackBox.Text = s.ScrollbackLines.ToString();
        VisualBellCheck.IsChecked = s.VisualBell;
        BracketedPasteCheck.IsChecked = s.BracketedPaste;

        RestoreSessionCheck.IsChecked = s.RestoreSessionOnStartup;
        ConfirmCloseCheck.IsChecked = s.ConfirmOnClose;
        AutoCopyCheck.IsChecked = s.AutoCopyOnSelect;
        CtrlClickUrlCheck.IsChecked = s.CtrlClickOpensUrls;
        AutoSaveBox.Text = s.AutoSaveIntervalSeconds.ToString();
        LogRetentionDaysBox.Text = Math.Clamp(s.CommandLogRetentionDays, 0, 3650).ToString();
        CaptureOnCloseCheck.IsChecked = s.CaptureTranscriptsOnClose;
        CaptureOnClearCheck.IsChecked = s.CaptureTranscriptsOnClear;
        TranscriptRetentionDaysBox.Text = Math.Clamp(s.TranscriptRetentionDays, 0, 3650).ToString();

        var agent = s.Agent ?? new AgentSettings();
        AgentEnabledCheck.IsChecked = agent.Enabled;
        AgentNameBox.Text = string.IsNullOrWhiteSpace(agent.AgentName) ? "assistant" : agent.AgentName;
        AgentHandlerBox.Text = string.IsNullOrWhiteSpace(agent.Handler) ? "/agent" : agent.Handler;
        AgentAdditionalHandlersBox.Text = agent.AdditionalHandlers ?? "";
        AgentSystemPromptBox.Text = string.IsNullOrWhiteSpace(agent.SystemPrompt)
            ? "You are a pragmatic engineering assistant running inside cmux. Keep responses concise and action-oriented."
            : agent.SystemPrompt;

        var activeProvider = string.IsNullOrWhiteSpace(agent.ActiveProvider) ? "openai" : agent.ActiveProvider;
        AgentProviderCombo.SelectedIndex = string.Equals(activeProvider, "anthropic", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        OpenAiBaseUrlBox.Text = string.IsNullOrWhiteSpace(agent.OpenAi.BaseUrl) ? "https://api.openai.com/v1" : agent.OpenAi.BaseUrl;
        OpenAiModelBox.Text = string.IsNullOrWhiteSpace(agent.OpenAi.Model) ? "gpt-4o-mini" : agent.OpenAi.Model;
        OpenAiApiKeyBox.Password = string.IsNullOrWhiteSpace(SecretStoreService.GetSecret(agent.OpenAi.ApiKeySecretName)) ? "" : SecretMask;

        AnthropicBaseUrlBox.Text = string.IsNullOrWhiteSpace(agent.Anthropic.BaseUrl) ? "https://api.anthropic.com" : agent.Anthropic.BaseUrl;
        AnthropicModelBox.Text = string.IsNullOrWhiteSpace(agent.Anthropic.Model) ? "claude-3-5-sonnet-latest" : agent.Anthropic.Model;
        AnthropicApiKeyBox.Password = string.IsNullOrWhiteSpace(SecretStoreService.GetSecret(agent.Anthropic.ApiKeySecretName)) ? "" : SecretMask;

        AgentBashToolCheck.IsChecked = agent.EnableBashTool;
        AgentBashTimeoutBox.Text = Math.Clamp(agent.BashTimeoutSeconds, 1, 1800).ToString();
        AgentWebSearchCheck.IsChecked = agent.EnableWebSearchTool;
        ExaBaseUrlBox.Text = string.IsNullOrWhiteSpace(agent.Exa.BaseUrl) ? "https://api.exa.ai" : agent.Exa.BaseUrl;
        ExaApiKeyBox.Password = string.IsNullOrWhiteSpace(SecretStoreService.GetSecret(agent.Exa.ApiKeySecretName)) ? "" : SecretMask;
        AgentDefaultSubmitKeyCombo.SelectedIndex = ResolveSubmitKeyComboIndex(agent.DefaultSubmitKey);
        AgentEnableSubmitFallbackCheck.IsChecked = agent.EnableSubmitFallback;
        AgentSubmitFallbackWaitMsBox.Text = Math.Clamp(agent.SubmitFallbackWaitMs, 0, 5000).ToString();
        AgentSubmitFallbackOrderBox.Text = string.IsNullOrWhiteSpace(agent.SubmitFallbackOrder) ? "enter,linefeed" : agent.SubmitFallbackOrder;
        AgentAutoDiscoverFilesCheck.IsChecked = agent.AutoDiscoverAgentFiles;
        AgentInstructionsPathBox.Text = agent.AgentInstructionsPath ?? "";
        AgentSkillsRootPathBox.Text = agent.SkillsRootPath ?? "";
        AgentMemoryCheck.IsChecked = agent.EnableConversationMemory;
        AgentStreamingCheck.IsChecked = agent.EnableStreaming;
        var chatFontFamily = string.IsNullOrWhiteSpace(agent.ChatFontFamily) ? s.FontFamily : agent.ChatFontFamily;
        AgentChatFontFamilyCombo.SelectedItem = chatFontFamily;
        if (AgentChatFontFamilyCombo.SelectedItem == null)
            AgentChatFontFamilyCombo.Text = chatFontFamily;
        AgentChatFontSizeBox.Text = Math.Clamp(agent.ChatFontSize, 9, 28).ToString();
        AgentAutoCompactCheck.IsChecked = agent.AutoCompactContext;
        AgentContextMaxMessagesBox.Text = Math.Clamp(agent.MaxContextMessages, 8, 500).ToString();
        AgentContextBudgetTokensBox.Text = Math.Clamp(agent.ContextBudgetTokens, 2048, 1_000_000).ToString();
        AgentCompactThresholdBox.Text = Math.Clamp(agent.CompactThresholdPercent, 50, 95).ToString();
        AgentKeepRecentOnCompactionBox.Text = Math.Clamp(agent.KeepRecentMessagesOnCompaction, 4, 400).ToString();

        _customToolsDraft.Clear();
        _customToolsDraft.AddRange(agent.CustomTools ?? []);
        _mcpServersDraft.Clear();
        _mcpServersDraft.AddRange(agent.McpServers ?? []);

        CustomToolsJsonBox.Text = JsonSerializer.Serialize(_customToolsDraft, new JsonSerializerOptions { WriteIndented = true });
        McpServersJsonBox.Text = JsonSerializer.Serialize(_mcpServersDraft, new JsonSerializerOptions { WriteIndented = true });

        CustomToolsModeCombo.SelectedIndex = agent.UseJsonForCustomTools ? 1 : 0;
        McpServersModeCombo.SelectedIndex = agent.UseJsonForMcpServers ? 1 : 0;
        RefreshCustomToolsList();
        RefreshMcpServersList();
        UpdateCustomToolsModeVisibility();
        UpdateMcpServersModeVisibility();

        _clearOpenAiKey = false;
        _clearAnthropicKey = false;
        _clearExaKey = false;

        UseCustomTerminalColorsCheck.IsChecked = s.UseCustomTerminalColors;

        var preset = TerminalThemes.Get(s.ThemeName);
        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = NormalizeHexColor(s.CustomTerminalBackground) ?? TerminalThemes.ToHex(preset.Background);
        TerminalForegroundHexBox.Text = NormalizeHexColor(s.CustomTerminalForeground) ?? TerminalThemes.ToHex(preset.Foreground);
        TerminalCursorHexBox.Text = NormalizeHexColor(s.CustomTerminalCursor) ?? TerminalThemes.ToHex(preset.CursorColor);
        TerminalSelectionHexBox.Text = NormalizeHexColor(s.CustomTerminalSelection) ?? TerminalThemes.ToHex(preset.SelectionBg);
        _suppressTerminalColorEvents = false;

        UpdateTerminalColorEditorsEnabledState();
        RefreshTerminalColorPreviews();
        UpdateThemePreview();
    }

    private bool SaveSettings()
    {
        var s = SettingsService.Current;
        s.FontFamily = FontFamilyCombo.SelectedItem as string ?? FontFamilyCombo.Text;
        s.FontSize = (int)Math.Round(FontSizeSlider.Value);
        s.ThemeName = TerminalThemePresetCombo.SelectedItem as string
            ?? ThemeCombo.SelectedItem as string
            ?? "Default Dark";
        s.Opacity = OpacitySlider.Value;
        s.CursorStyle = CursorStyleCombo.SelectedItem as string ?? "bar";
        s.CursorBlink = CursorBlinkCheck.IsChecked == true;

        s.DefaultShell = ShellCombo.SelectedValue as string ?? "";
        s.DefaultShellArgs = ShellArgsBox.Text;
        if (int.TryParse(ScrollbackBox.Text, out int sb)) s.ScrollbackLines = sb;
        s.VisualBell = VisualBellCheck.IsChecked == true;
        s.BracketedPaste = BracketedPasteCheck.IsChecked == true;

        s.RestoreSessionOnStartup = RestoreSessionCheck.IsChecked == true;
        s.ConfirmOnClose = ConfirmCloseCheck.IsChecked == true;
        s.AutoCopyOnSelect = AutoCopyCheck.IsChecked == true;
        s.CtrlClickOpensUrls = CtrlClickUrlCheck.IsChecked == true;
        if (int.TryParse(AutoSaveBox.Text, out int asv)) s.AutoSaveIntervalSeconds = asv;
        if (int.TryParse(LogRetentionDaysBox.Text, out int retentionDays))
            s.CommandLogRetentionDays = Math.Clamp(retentionDays, 0, 3650);
        s.CaptureTranscriptsOnClose = CaptureOnCloseCheck.IsChecked == true;
        s.CaptureTranscriptsOnClear = CaptureOnClearCheck.IsChecked == true;
        if (int.TryParse(TranscriptRetentionDaysBox.Text, out int transcriptRetention))
            s.TranscriptRetentionDays = Math.Clamp(transcriptRetention, 0, 3650);

        s.UseCustomTerminalColors = UseCustomTerminalColorsCheck.IsChecked == true;
        s.CustomTerminalBackground = NormalizeHexColor(TerminalBackgroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalForeground = NormalizeHexColor(TerminalForegroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalCursor = NormalizeHexColor(TerminalCursorHexBox.Text) ?? string.Empty;
        s.CustomTerminalSelection = NormalizeHexColor(TerminalSelectionHexBox.Text) ?? string.Empty;

        var agent = s.Agent ?? new AgentSettings();
        agent.Enabled = AgentEnabledCheck.IsChecked == true;
        agent.AgentName = string.IsNullOrWhiteSpace(AgentNameBox.Text) ? "assistant" : AgentNameBox.Text.Trim();
        agent.Handler = string.IsNullOrWhiteSpace(AgentHandlerBox.Text) ? "/agent" : AgentHandlerBox.Text.Trim();
        agent.AdditionalHandlers = AgentAdditionalHandlersBox.Text?.Trim() ?? "";
        agent.SystemPrompt = AgentSystemPromptBox.Text?.Trim() ?? "";
        agent.ActiveProvider = (AgentProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim().ToLowerInvariant() ?? "openai";

        agent.OpenAi ??= new OpenAiCompatibleAgentSettings();
        agent.OpenAi.BaseUrl = OpenAiBaseUrlBox.Text?.Trim() ?? "";
        agent.OpenAi.Model = OpenAiModelBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(agent.OpenAi.ApiKeySecretName))
            agent.OpenAi.ApiKeySecretName = "agent.openai.apiKey";

        agent.Anthropic ??= new AnthropicAgentSettings();
        agent.Anthropic.BaseUrl = AnthropicBaseUrlBox.Text?.Trim() ?? "";
        agent.Anthropic.Model = AnthropicModelBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(agent.Anthropic.ApiKeySecretName))
            agent.Anthropic.ApiKeySecretName = "agent.anthropic.apiKey";

        agent.Exa ??= new ExaSearchSettings();
        agent.Exa.BaseUrl = ExaBaseUrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(agent.Exa.ApiKeySecretName))
            agent.Exa.ApiKeySecretName = "agent.exa.apiKey";

        agent.EnableBashTool = AgentBashToolCheck.IsChecked == true;
        if (int.TryParse(AgentBashTimeoutBox.Text, out var bashTimeout))
            agent.BashTimeoutSeconds = Math.Clamp(bashTimeout, 1, 1800);
        agent.EnableWebSearchTool = AgentWebSearchCheck.IsChecked == true;
        agent.DefaultSubmitKey = ((AgentDefaultSubmitKeyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto")
            .Trim().ToLowerInvariant();
        if (agent.DefaultSubmitKey is not ("auto" or "enter" or "linefeed" or "crlf"))
            agent.DefaultSubmitKey = "auto";
        agent.EnableSubmitFallback = AgentEnableSubmitFallbackCheck.IsChecked == true;
        if (int.TryParse(AgentSubmitFallbackWaitMsBox.Text, out var submitFallbackWaitMs))
            agent.SubmitFallbackWaitMs = Math.Clamp(submitFallbackWaitMs, 0, 5000);
        agent.SubmitFallbackOrder = AgentSubmitFallbackOrderBox.Text?.Trim() ?? "enter,linefeed";
        agent.AutoDiscoverAgentFiles = AgentAutoDiscoverFilesCheck.IsChecked == true;
        agent.AgentInstructionsPath = AgentInstructionsPathBox.Text?.Trim() ?? "";
        agent.SkillsRootPath = AgentSkillsRootPathBox.Text?.Trim() ?? "";
        agent.EnableConversationMemory = AgentMemoryCheck.IsChecked == true;
        agent.EnableStreaming = AgentStreamingCheck.IsChecked == true;
        agent.ChatFontFamily = (AgentChatFontFamilyCombo.SelectedItem as string ?? AgentChatFontFamilyCombo.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(agent.ChatFontFamily))
            agent.ChatFontFamily = s.FontFamily;
        if (int.TryParse(AgentChatFontSizeBox.Text, out var chatFontSize))
            agent.ChatFontSize = Math.Clamp(chatFontSize, 9, 28);
        agent.AutoCompactContext = AgentAutoCompactCheck.IsChecked == true;
        if (int.TryParse(AgentContextMaxMessagesBox.Text, out var maxContextMessages))
            agent.MaxContextMessages = Math.Clamp(maxContextMessages, 8, 500);
        if (int.TryParse(AgentContextBudgetTokensBox.Text, out var budgetTokens))
            agent.ContextBudgetTokens = Math.Clamp(budgetTokens, 2048, 1_000_000);
        if (int.TryParse(AgentCompactThresholdBox.Text, out var compactThreshold))
            agent.CompactThresholdPercent = Math.Clamp(compactThreshold, 50, 95);
        if (int.TryParse(AgentKeepRecentOnCompactionBox.Text, out var keepRecent))
            agent.KeepRecentMessagesOnCompaction = Math.Clamp(keepRecent, 4, 400);
        agent.UseJsonForCustomTools = CustomToolsModeCombo.SelectedIndex == 1;
        agent.UseJsonForMcpServers = McpServersModeCombo.SelectedIndex == 1;

        if (agent.UseJsonForCustomTools)
        {
            if (!TryParseCustomToolsJson(CustomToolsJsonBox.Text, out var parsedTools, out var parseError))
            {
                MessageBox.Show(parseError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateCustomTools(parsedTools, out var validationError))
            {
                MessageBox.Show(validationError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            agent.CustomTools = parsedTools;
            _customToolsDraft.Clear();
            _customToolsDraft.AddRange(parsedTools);
            RefreshCustomToolsList();
        }
        else
        {
            if (!ValidateCustomTools(_customToolsDraft, out var validationError))
            {
                MessageBox.Show(validationError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            agent.CustomTools = _customToolsDraft
                .Select(CloneCustomTool)
                .ToList();
            CustomToolsJsonBox.Text = JsonSerializer.Serialize(agent.CustomTools, new JsonSerializerOptions { WriteIndented = true });
        }

        if (agent.UseJsonForMcpServers)
        {
            if (!TryParseMcpServersJson(McpServersJsonBox.Text, out var parsedServers, out var parseError))
            {
                MessageBox.Show(parseError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateMcpServers(parsedServers, out var validationError))
            {
                MessageBox.Show(validationError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            agent.McpServers = parsedServers;
            _mcpServersDraft.Clear();
            _mcpServersDraft.AddRange(parsedServers);
            RefreshMcpServersList();
        }
        else
        {
            if (!ValidateMcpServers(_mcpServersDraft, out var validationError))
            {
                MessageBox.Show(validationError, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            agent.McpServers = _mcpServersDraft
                .Select(CloneMcpServer)
                .ToList();
            McpServersJsonBox.Text = JsonSerializer.Serialize(agent.McpServers, new JsonSerializerOptions { WriteIndented = true });
        }

        ApplySecretUpdate(OpenAiApiKeyBox, agent.OpenAi.ApiKeySecretName, ref _clearOpenAiKey);
        ApplySecretUpdate(AnthropicApiKeyBox, agent.Anthropic.ApiKeySecretName, ref _clearAnthropicKey);
        ApplySecretUpdate(ExaApiKeyBox, agent.Exa.ApiKeySecretName, ref _clearExaKey);

        s.Agent = agent;

        SettingsService.Save();
        SettingsService.NotifyChanged();
        return true;
    }

    private void ShowSection(string section)
    {
        AppearanceSection.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        TerminalSection.Visibility = section == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorSection.Visibility = section == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        KeyboardSection.Visibility = section == "Keyboard" ? Visibility.Visible : Visibility.Collapsed;
        AgentSection.Visibility = section == "Agent" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;

        // Update nav button active state via Tag
        foreach (var btn in new[] { NavAppearance, NavTerminal, NavBehavior, NavKeyboard, NavAgent, NavAbout })
            btn.Tag = btn.Name == $"Nav{section}" ? "active" : null;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var section = btn.Name.Replace("Nav", "");
            ShowSection(section);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings())
            return;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Reset();
        LoadSettings();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void ApplySecretUpdate(PasswordBox box, string secretName, ref bool clearFlag)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return;

        if (clearFlag)
        {
            SecretStoreService.RemoveSecret(secretName);
            clearFlag = false;
            return;
        }

        var value = box.Password;
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (string.Equals(value, SecretMask, StringComparison.Ordinal))
            return;

        SecretStoreService.SetSecret(secretName, value);
        box.Password = SecretMask;
    }

    private void ClearOpenAiKey_Click(object sender, RoutedEventArgs e)
    {
        OpenAiApiKeyBox.Password = "";
        _clearOpenAiKey = true;
    }

    private void ClearAnthropicKey_Click(object sender, RoutedEventArgs e)
    {
        AnthropicApiKeyBox.Password = "";
        _clearAnthropicKey = true;
    }

    private void ClearExaKey_Click(object sender, RoutedEventArgs e)
    {
        ExaApiKeyBox.Password = "";
        _clearExaKey = true;
    }

    private void CustomToolsModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomToolsModeVisibility();
    }

    private void McpServersModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMcpServersModeVisibility();
    }

    private void UpdateCustomToolsModeVisibility()
    {
        bool jsonMode = CustomToolsModeCombo.SelectedIndex == 1;
        CustomToolsCreatorPanel.Visibility = jsonMode ? Visibility.Collapsed : Visibility.Visible;
        CustomToolsJsonPanel.Visibility = jsonMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMcpServersModeVisibility()
    {
        bool jsonMode = McpServersModeCombo.SelectedIndex == 1;
        McpServersCreatorPanel.Visibility = jsonMode ? Visibility.Collapsed : Visibility.Visible;
        McpServersJsonPanel.Visibility = jsonMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshCustomToolsList()
    {
        CustomToolsListBox.ItemsSource = null;
        CustomToolsListBox.ItemsSource = _customToolsDraft;
    }

    private void RefreshMcpServersList()
    {
        McpServersListBox.ItemsSource = null;
        McpServersListBox.ItemsSource = _mcpServersDraft;
    }

    private void CustomToolsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomToolsListBox.SelectedItem is not AgentCustomToolConfig tool)
            return;

        CustomToolNameBox.Text = tool.Name;
        CustomToolDescriptionBox.Text = tool.Description;
        CustomToolCommandTemplateBox.Text = tool.CommandTemplate;
        CustomToolEnabledCheck.IsChecked = tool.Enabled;
    }

    private void McpServersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpServersListBox.SelectedItem is not AgentMcpServerConfig server)
            return;

        McpServerNameBox.Text = server.Name;
        McpServerCommandBox.Text = server.Command;
        McpServerArgumentsBox.Text = server.Arguments;
        McpServerWorkingDirectoryBox.Text = server.WorkingDirectory;
        McpServerEnabledCheck.IsChecked = server.Enabled;
    }

    private void AddOrUpdateCustomTool_Click(object sender, RoutedEventArgs e)
    {
        var draft = new AgentCustomToolConfig
        {
            Enabled = CustomToolEnabledCheck.IsChecked == true,
            Name = (CustomToolNameBox.Text ?? "").Trim(),
            Description = (CustomToolDescriptionBox.Text ?? "").Trim(),
            CommandTemplate = (CustomToolCommandTemplateBox.Text ?? "").Trim(),
        };

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            MessageBox.Show("Custom tool name is required.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.CommandTemplate))
        {
            MessageBox.Show("Custom tool command template is required.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int idx = _customToolsDraft.FindIndex(t => string.Equals(t.Name, draft.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _customToolsDraft[idx] = draft;
        else
            _customToolsDraft.Add(draft);

        RefreshCustomToolsList();
        CustomToolsJsonBox.Text = JsonSerializer.Serialize(_customToolsDraft, new JsonSerializerOptions { WriteIndented = true });
    }

    private void RemoveSelectedCustomTool_Click(object sender, RoutedEventArgs e)
    {
        if (CustomToolsListBox.SelectedItem is not AgentCustomToolConfig selected)
            return;

        _customToolsDraft.Remove(selected);
        RefreshCustomToolsList();
        CustomToolsJsonBox.Text = JsonSerializer.Serialize(_customToolsDraft, new JsonSerializerOptions { WriteIndented = true });
    }

    private void AddOrUpdateMcpServer_Click(object sender, RoutedEventArgs e)
    {
        var draft = new AgentMcpServerConfig
        {
            Enabled = McpServerEnabledCheck.IsChecked == true,
            Name = (McpServerNameBox.Text ?? "").Trim(),
            Command = (McpServerCommandBox.Text ?? "").Trim(),
            Arguments = (McpServerArgumentsBox.Text ?? "").Trim(),
            WorkingDirectory = (McpServerWorkingDirectoryBox.Text ?? "").Trim(),
        };

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            MessageBox.Show("MCP server name is required.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Command))
        {
            MessageBox.Show("MCP server command is required.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int idx = _mcpServersDraft.FindIndex(s => string.Equals(s.Name, draft.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _mcpServersDraft[idx] = draft;
        else
            _mcpServersDraft.Add(draft);

        RefreshMcpServersList();
        McpServersJsonBox.Text = JsonSerializer.Serialize(_mcpServersDraft, new JsonSerializerOptions { WriteIndented = true });
    }

    private void RemoveSelectedMcpServer_Click(object sender, RoutedEventArgs e)
    {
        if (McpServersListBox.SelectedItem is not AgentMcpServerConfig selected)
            return;

        _mcpServersDraft.Remove(selected);
        RefreshMcpServersList();
        McpServersJsonBox.Text = JsonSerializer.Serialize(_mcpServersDraft, new JsonSerializerOptions { WriteIndented = true });
    }

    private static AgentCustomToolConfig CloneCustomTool(AgentCustomToolConfig source)
    {
        return new AgentCustomToolConfig
        {
            Enabled = source.Enabled,
            Name = source.Name,
            Description = source.Description,
            CommandTemplate = source.CommandTemplate,
        };
    }

    private static AgentMcpServerConfig CloneMcpServer(AgentMcpServerConfig source)
    {
        return new AgentMcpServerConfig
        {
            Enabled = source.Enabled,
            Name = source.Name,
            Command = source.Command,
            Arguments = source.Arguments,
            WorkingDirectory = source.WorkingDirectory,
        };
    }

    private static bool TryParseCustomToolsJson(string text, out List<AgentCustomToolConfig> tools, out string error)
    {
        tools = [];
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Custom tools JSON must be an array.";
                return false;
            }

            tools = JsonSerializer.Deserialize<List<AgentCustomToolConfig>>(text) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid custom tools JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseMcpServersJson(string text, out List<AgentMcpServerConfig> servers, out string error)
    {
        servers = [];
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "MCP servers JSON must be an array.";
                return false;
            }

            servers = JsonSerializer.Deserialize<List<AgentMcpServerConfig>>(text) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid MCP servers JSON: {ex.Message}";
            return false;
        }
    }

    private static bool ValidateCustomTools(IEnumerable<AgentCustomToolConfig> tools, out string error)
    {
        var list = tools.ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var row = i + 1;

            if (string.IsNullOrWhiteSpace(t.Name))
            {
                error = $"Custom tool at index {row} is missing 'name'.";
                return false;
            }

            if (!names.Add(t.Name.Trim()))
            {
                error = $"Duplicate custom tool name: '{t.Name}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(t.CommandTemplate))
            {
                error = $"Custom tool '{t.Name}' is missing 'commandTemplate'.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static bool ValidateMcpServers(IEnumerable<AgentMcpServerConfig> servers, out string error)
    {
        var list = servers.ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var row = i + 1;

            if (string.IsNullOrWhiteSpace(s.Name))
            {
                error = $"MCP server at index {row} is missing 'name'.";
                return false;
            }

            if (!names.Add(s.Name.Trim()))
            {
                error = $"Duplicate MCP server name: '{s.Name}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(s.Command))
            {
                error = $"MCP server '{s.Name}' is missing 'command'.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressThemeSync)
        {
            _suppressThemeSync = true;
            TerminalThemePresetCombo.SelectedItem = ThemeCombo.SelectedItem;
            _suppressThemeSync = false;
        }

        UpdateThemePreview();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void TerminalThemePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressThemeSync)
        {
            _suppressThemeSync = true;
            ThemeCombo.SelectedItem = TerminalThemePresetCombo.SelectedItem;
            _suppressThemeSync = false;
        }

        UpdateThemePreview();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void UpdateThemePreview()
    {
        var themeName = TerminalThemePresetCombo.SelectedItem as string
            ?? ThemeCombo.SelectedItem as string;

        if (themeName is null)
            return;

        var theme = TerminalThemes.Get(themeName);

        ThemePreview.Background = new SolidColorBrush(
            Color.FromRgb(theme.Background.R, theme.Background.G, theme.Background.B));
        ThemePreviewText.Foreground = new SolidColorBrush(
            Color.FromRgb(theme.Foreground.R, theme.Foreground.G, theme.Foreground.B));
    }

    private void UpdateOpacityText()
    {
        if (OpacityValueText != null)
            OpacityValueText.Text = $"{OpacitySlider.Value:P0}";
    }

    private void UpdateFontSizeText()
    {
        if (FontSizeValueText != null)
            FontSizeValueText.Text = $"{(int)Math.Round(FontSizeSlider.Value)} px";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontSizeText();
    }

    private static string? NormalizeHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        if (value.Length == 7)
            value = "#FF" + value[1..];

        if (value.Length != 9)
            return null;

        if (TerminalThemes.TryParseHexColor(value, out _))
            return value.ToUpperInvariant();

        return null;
    }

    private void SetColorField(TextBox box, Border preview, string? colorText)
    {
        var normalized = NormalizeHexColor(colorText);
        if (normalized == null)
            return;

        _suppressTerminalColorEvents = true;
        box.Text = normalized;
        _suppressTerminalColorEvents = false;

        if (TerminalThemes.TryParseHexColor(normalized, out var color))
            preview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private void RefreshTerminalColorPreviews()
    {
        SetColorField(TerminalBackgroundHexBox, TerminalBackgroundPreview, TerminalBackgroundHexBox.Text);
        SetColorField(TerminalForegroundHexBox, TerminalForegroundPreview, TerminalForegroundHexBox.Text);
        SetColorField(TerminalCursorHexBox, TerminalCursorPreview, TerminalCursorHexBox.Text);
        SetColorField(TerminalSelectionHexBox, TerminalSelectionPreview, TerminalSelectionHexBox.Text);
    }

    private void UpdateTerminalColorEditorsEnabledState()
    {
        var enabled = UseCustomTerminalColorsCheck.IsChecked == true;
        TerminalBackgroundColorPanel.IsEnabled = enabled;
        TerminalForegroundColorPanel.IsEnabled = enabled;
        TerminalCursorColorPanel.IsEnabled = enabled;
        TerminalSelectionColorPanel.IsEnabled = enabled;
    }

    private void RefreshCustomColorsFromPresetIfNeeded()
    {
        if (UseCustomTerminalColorsCheck.IsChecked == true)
            return;

        if (TerminalThemePresetCombo.SelectedItem is not string presetName)
            return;

        var theme = TerminalThemes.Get(presetName);

        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = TerminalThemes.ToHex(theme.Background);
        TerminalForegroundHexBox.Text = TerminalThemes.ToHex(theme.Foreground);
        TerminalCursorHexBox.Text = TerminalThemes.ToHex(theme.CursorColor);
        TerminalSelectionHexBox.Text = TerminalThemes.ToHex(theme.SelectionBg);
        _suppressTerminalColorEvents = false;

        RefreshTerminalColorPreviews();
    }

    private void UseCustomTerminalColorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTerminalColorEditorsEnabledState();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void TerminalColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTerminalColorEvents)
            return;

        RefreshTerminalColorPreviews();
    }

    private string PickColor(string initial)
    {
        var picker = new ColorPickerWindow(initial) { Owner = this };
        return picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedHex)
            ? picker.SelectedHex
            : initial;
    }

    private void PickTerminalBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalBackgroundHexBox, TerminalBackgroundPreview, PickColor(TerminalBackgroundHexBox.Text));
    }

    private void PickTerminalForegroundColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalForegroundHexBox, TerminalForegroundPreview, PickColor(TerminalForegroundHexBox.Text));
    }

    private void PickTerminalCursorColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalCursorHexBox, TerminalCursorPreview, PickColor(TerminalCursorHexBox.Text));
    }

    private void PickTerminalSelectionColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalSelectionHexBox, TerminalSelectionPreview, PickColor(TerminalSelectionHexBox.Text));
    }

    private void ResetTerminalColors_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalThemePresetCombo.SelectedItem is not string presetName)
            return;

        var theme = TerminalThemes.Get(presetName);
        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = TerminalThemes.ToHex(theme.Background);
        TerminalForegroundHexBox.Text = TerminalThemes.ToHex(theme.Foreground);
        TerminalCursorHexBox.Text = TerminalThemes.ToHex(theme.CursorColor);
        TerminalSelectionHexBox.Text = TerminalThemes.ToHex(theme.SelectionBg);
        _suppressTerminalColorEvents = false;
        RefreshTerminalColorPreviews();
    }

    private List<ShellInfo> DetectShells()
    {
        var shells = new List<ShellInfo>();

        try
        {
            // PowerShell 7+
            var pwshRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
            if (Directory.Exists(pwshRoot))
            {
                var pwshPaths = Directory.GetFiles(pwshRoot, "pwsh.exe", SearchOption.AllDirectories);
                foreach (var path in pwshPaths.OrderByDescending(p => p)) // newest first
                    shells.Add(new ShellInfo("PowerShell 7", path));
            }
        } catch { /* ignore */ }

        try
        {
            // Windows PowerShell
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(powershell))
                shells.Add(new ShellInfo("Windows PowerShell", powershell));

            // Command Prompt
            var cmd = Path.Combine(system32, "cmd.exe");
            if (File.Exists(cmd))
                shells.Add(new ShellInfo("Command Prompt", cmd));

            // WSL
            var wslPath = Path.Combine(system32, "wsl.exe");
            if (File.Exists(wslPath))
                shells.Add(new ShellInfo("WSL", wslPath));
        } catch { /* ignore */ }

        try
        {
            // Git Bash
            var gitBashPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            };
            foreach (var path in gitBashPaths)
            {
                if (File.Exists(path))
                {
                    shells.Add(new ShellInfo("Git Bash", path));
                    break;
                }
            }
        } catch { /* ignore */ }

        return shells;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; } // Default to dark
    }

    private static int ResolveSubmitKeyComboIndex(string? submitKey)
    {
        var normalized = (submitKey ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "enter" => 1,
            "linefeed" => 2,
            "crlf" => 3,
            _ => 0,
        };
    }

    private record ShellInfo(string Name, string Path);
}
