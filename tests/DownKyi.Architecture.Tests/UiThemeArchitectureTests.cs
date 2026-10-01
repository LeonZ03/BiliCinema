using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class UiThemeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopUsesOneFluentThemeAndCentralDesignTokens()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml");
        var themeSource = ReadSource("src", "DownKyi.Desktop", "Themes", "ThemeDefault.axaml");
        var tokenSource = ReadSource("src", "DownKyi.Desktop", "Themes", "DesignTokens.axaml");
        var projectSource = ReadSource("src", "DownKyi.Desktop", "DownKyi.Desktop.csproj");

        Assert.Contains("<FluentTheme />", appSource, StringComparison.Ordinal);
        Assert.Contains("Themes/Fluent.xaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SimpleTheme", appSource, StringComparison.Ordinal);
        Assert.Contains("/Themes/DesignTokens.axaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/Themes/DesignTokens.axaml", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Default\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Light\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Dark\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeBody", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiSpacingMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiRadiusMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiElevationLow", tokenSource, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Themes.Fluent", projectSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Themes.Simple", projectSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultAndDarkPalettesDeclareTheSameSemanticColorKeys()
    {
        var defaultKeys = ExtractKeys(ReadSource(
                "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDefault.axaml"))
            .Where(static key => key.StartsWith("Color", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var darkKeys = ExtractKeys(ReadSource(
                "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDark.axaml"))
            .Where(static key => key.StartsWith("Color", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(defaultKeys);
        Assert.Equal(defaultKeys, darkKeys);
    }

    [Fact]
    public void DarkReadingPaletteSeparatesSurfacesTextAndInteractionStates()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml");
        var windowSource = ReadSource("src", "DownKyi.Desktop", "Views", "MainWindow.axaml");
        var darkPalette = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Colors", "ColorDark.axaml");

        Assert.Contains(
            "Segoe UI Variable Text,Segoe UI,Microsoft JhengHei UI,Microsoft YaHei UI",
            appSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(">Simsun,", appSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Background=\"{DynamicResource BrushSurfaceBase}\"",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceBase\">#FF0F0F0F</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceRaised\">#FF212121</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorSurfaceOverlay\">#FF282828</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextPrimary\">#FFF1F1F1</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextSecondary\">#FFAAAAAA</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorTextDark\">#FFC7C7C7</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Color x:Key=\"ColorTextDark\">white</Color>",
            darkPalette,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "<Color x:Key=\"ColorSelectionFill\">#33FFFFFF</Color>",
            darkPalette,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Color x:Key=\"ColorControlStrokeFocus\">#FF1C62B9</Color>",
            darkPalette,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControlsReserveAccentForFocusLinksAndActiveState()
    {
        var buttonTheme = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Styles", "StyleBtn.axaml");
        var navigationTheme = ReadSource(
            "src", "DownKyi.Desktop", "Themes", "Styles", "StyleListBox.axaml");
        var indexView = ReadSource("src", "DownKyi.Desktop", "Views", "ViewIndex.axaml");

        Assert.Contains("BrushControlFill", buttonTheme, StringComparison.Ordinal);
        Assert.Contains("BrushControlStroke", buttonTheme, StringComparison.Ordinal);
        Assert.DoesNotContain("BrushPrimaryTranslucent", buttonTheme, StringComparison.Ordinal);
        Assert.Contains("BrushSelectionFill", navigationTheme, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionIndicator", navigationTheme, StringComparison.Ordinal);
        Assert.Contains("BrushControlStrokeFocus", indexView, StringComparison.Ordinal);
        Assert.Contains("BrushTextSecondary", indexView, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsUseSemanticSurfacesAndReadableTypography()
    {
        var settingsRoot = Path.Combine(
            RepositoryRoot, "src", "DownKyi.Desktop", "Views", "Settings");
        var settingsSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(settingsRoot, "*.axaml").Select(File.ReadAllText));
        var languageSource = ReadSource(
            "src", "DownKyi.Desktop", "Languages", "Default.axaml");

        Assert.DoesNotContain("Background=\"LightGray\"", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=\"12\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeBody", settingsSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeTitle", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<system:String x:Key=\"FontName\">弹幕字体：</system:String>", languageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolboxSurfacesUseSemanticThemeResources()
    {
        var toolboxViews = new[]
        {
            ReadSource("src", "DownKyi.Desktop", "Views", "ViewToolbox.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewBiliHelper.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewExtractMedia.axaml"),
            ReadSource("src", "DownKyi.Desktop", "Views", "Toolbox", "ViewDelogo.axaml")
        };

        Assert.All(
            toolboxViews,
            source => Assert.Contains("BrushSurfaceBase", source, StringComparison.Ordinal));

        var combinedSource = string.Join(Environment.NewLine, toolboxViews);
        Assert.DoesNotContain("Background=\"LightGray\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"Black\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"White\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"Gray\"", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#FF1E1E1E\"", combinedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReferencedSemanticThemeResourceIsDeclared()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var declaredKeys = Directory
            .EnumerateFiles(Path.Combine(desktopRoot, "Themes"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(static path => ExtractKeys(File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        var referencedKeys = Directory
            .EnumerateFiles(desktopRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .SelectMany(static path => ExtractSemanticResourceReferences(File.ReadAllText(path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingKeys = referencedKeys
            .Where(key => !declaredKeys.Contains(key))
            .ToArray();

        Assert.True(
            missingKeys.Length == 0,
            $"Missing semantic theme resources: {string.Join(", ", missingKeys)}");
    }

    [Fact]
    public void DesktopThemeControllerIsTheOnlyThemeSwitchOwner()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var desktopSources = Directory
            .EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .ToArray();
        var requestedThemeOwners = desktopSources
            .Where(path => File.ReadAllText(path).Contains("RequestedThemeVariant", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var presentationSources = desktopSources
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}ViewModels{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Presentation{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Equal(
            ["src/DownKyi.Desktop/Appearance/DesktopThemeController.cs"],
            requestedThemeOwners);
        Assert.DoesNotContain(
            desktopSources,
            path => File.ReadAllText(path).Contains("ActualThemeVariantChanged", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("DictionaryResource.GetColor", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("Application.Current", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("ActualThemeVariant", StringComparison.Ordinal));
        Assert.DoesNotContain(
            presentationSources,
            source => source.Contains("ResourceDictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsThemeChoicesUseExplicitThemeModes()
    {
        var settingsView = ReadSource(
            "src", "DownKyi.Desktop", "Views", "Settings", "ViewBasic.axaml");
        var resourceLookup = ReadSource(
            "src", "DownKyi.Desktop", "Utils", "DictionaryResource.cs");

        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Light}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Dark}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{x:Static settings:ThemeMode.Default}\"",
            settingsView,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"Auto\"", settingsView, StringComparison.Ordinal);
        Assert.DoesNotContain("GetColor", resourceLookup, StringComparison.Ordinal);
        Assert.DoesNotContain("#00000000", resourceLookup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VectorImageFillBindingsAreLimitedToFixedStatusSemantics()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var fillBindingOwners = Directory
            .EnumerateFiles(Path.Combine(desktopRoot, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                "Fill=\\\"\\{Binding [^\\\"]+\\.Fill\\}\\\""))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "src/DownKyi.Desktop/Views/Dialogs/ViewAlertDialog.axaml",
                "src/DownKyi.Desktop/Views/Dialogs/ViewAlreadyDownloadedDialog.axaml"
            ],
            fillBindingOwners);
    }

    [Fact]
    public void FluentThemeDependencyStaysInsideDesktopProject()
    {
        var consumers = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath)
            .Where(path => File.ReadAllText(path).Contains("Avalonia.Themes.Fluent", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["src/DownKyi.Desktop/DownKyi.Desktop.csproj"], consumers);
    }

    [Theory]
    [InlineData("src", "DownKyi.Desktop", "Views", "DownloadManager", "ViewDownloading.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "DownloadManager", "ViewDownloadFinished.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewMyHistory.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewMyToViewVideo.axaml")]
    [InlineData("src", "DownKyi.Desktop", "Views", "ViewPublicFavorites.axaml")]
    public void LargeListsKeepVirtualizingPanels(params string[] pathParts)
    {
        Assert.Contains("VirtualizingStackPanel", ReadSource(pathParts), StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathParts]));
    }

    private static IEnumerable<string> ExtractKeys(string source)
    {
        return Regex.Matches(source, "x:Key=\\\"(?<key>[^\\\"]+)\\\"")
            .Select(static match => match.Groups["key"].Value);
    }

    private static IEnumerable<string> ExtractSemanticResourceReferences(string source)
    {
        return Regex.Matches(
                source,
                "\\{(?:Dynamic|Static)Resource\\s+(?<key>(?:Color|Brush)[A-Za-z0-9]+)\\s*\\}")
            .Select(static match => match.Groups["key"].Value);
    }

    private static bool IsRepositorySourcePath(string path)
    {
        var relativePath = Path.GetRelativePath(RepositoryRoot, path);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return !segments.Contains(".git", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains(".tools", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("bin", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
