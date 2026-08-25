using System.IO;
using System.Text.RegularExpressions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The safety net that makes the shared culture-refresh base a fix rather
/// than a move: reads every view-model source file under src/ui/ViewModels,
/// finds every string-returning property getter that resolves through the
/// <c>_localization</c> field (the indexer or any member use, e.g.
/// <c>_localization.Format(...)</c>), and fails when such a getter is NOT
/// covered by its VM's registered refresh list (the
/// <see cref="LocalizedViewModel"/> registration or, for the parent-refreshed
/// <see cref="Modificus.Curator.UI.ViewModels.ModItemViewModel"/>, the
/// Refresh method's re-fire list). The forget-to-register failure becomes a
/// red test instead of silently stale UI text. Follows the
/// <see cref="GamingModeGatingXamlTests"/> source-text assertion pattern.
/// </summary>
public sealed class LocalizedViewModelRegistrationTests
{
    /// <summary>
    /// The VM classes whose localized getters must be registered: the
    /// <see cref="LocalizedViewModel"/> subscribers plus the row VM whose
    /// localized strings are refreshed by its parent (through Refresh).
    /// A new VM class with a localized property getter must be added here
    /// (the companion unknown-class assertion below makes the omission loud).
    /// </summary>
    private static readonly HashSet<string> RegisteredVms = new()
    {
        "ShellViewModel",
        "ModListViewModel",
        "SettingsViewModel",
        "IntegrationsViewModel",
        "PreferencesViewModel",
        "ProfilesViewModel",
        "ImportWorkflowViewModel",
        "EditImportDetailsViewModel",
        "DiscoveryEscapeHatchViewModel",
        "DiscoveryFieldRowViewModel",
        "ThemeOption",
        "LanguageOption",
        "ModItemViewModel",
        "DownloadRowViewModel",
    };

    [Fact]
    public void Every_localized_property_getter_is_registered_for_a_culture_refresh()
    {
        var failures = new List<string>();
        foreach (var vm in ScanVmClassesWithLocalizedGetters())
        {
            var registered = RegisteredNames(vm.ClassSource);
            foreach (var getter in vm.Getters)
            {
                if (!registered.Contains(getter))
                {
                    failures.Add(
                        $"{vm.ClassName} ({vm.File}): property '{getter}' resolves through " +
                        "_localization (the indexer or a member call, e.g. Format) but is not " +
                        "in the class's registered refresh list " +
                        "(LocalizedProperties for a LocalizedViewModel, or the Refresh() " +
                        "re-fire list for ModItemViewModel). Add it or the string goes stale " +
                        "on a culture switch.");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Unregistered localized property getters:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_class_with_localized_getters_is_a_known_vm()
    {
        // A brand-new VM class with a localized getter must join RegisteredVms
        // (and register its names); an unknown class is a loud failure, not a
        // silent gap in the culture refresh.
        var unknown = ScanVmClassesWithLocalizedGetters()
            .Select(vm => vm.ClassName)
            .Where(name => !RegisteredVms.Contains(name))
            .ToList();

        Assert.True(unknown.Count == 0,
            "VM classes with localized property getters outside the known set:\n" +
            string.Join("\n", unknown));
    }

    [Fact]
    public void A_format_only_localized_getter_is_detected()
    {
        // A getter that never touches the _localization indexer (it resolves
        // only through the Format member) must still count as localized: the
        // registration scan's detection has to cover every _localization
        // member use, or such a getter could ship unregistered and go stale
        // on a culture switch with the scan green. Pins the widened predicate
        // against both getter shapes (expression- + block-bodied) and guards
        // against over-widening (a plain string getter stays undetected).
        const string source = """
            public sealed class SampleViewModel
            {
                private readonly LocalizationService _localization;

                public string HeaderText =>
                    _localization.Format("Sample_Header");

                public string PlainText => "not localized";

                public string BlockText
                {
                    get { return _localization.Format("Sample_Block"); }
                }
            }
            """;

        var detected = FindLocalizedGetterNames(source);

        Assert.Contains("HeaderText", detected);
        Assert.Contains("BlockText", detected);
        Assert.DoesNotContain("PlainText", detected);
    }

    private sealed record ScannedVm(string File, string ClassName, string ClassSource, List<string> Getters);

    /// <summary>
    /// Splits each VM file into its class declarations, then collects every
    /// localized getter name inside each class (see
    /// <see cref="FindLocalizedGetterNames"/>).
    /// </summary>
    private static IEnumerable<ScannedVm> ScanVmClassesWithLocalizedGetters()
    {
        var dir = new DirectoryInfo(Path.Combine(RepoRoot(), "src", "ui", "ViewModels"));
        Assert.True(dir.Exists, $"ViewModel source directory missing: {dir}");
        foreach (var file in dir.GetFiles("*.cs"))
        {
            var source = File.ReadAllText(file.FullName);

            // Class regions: declaration match to the next class declaration
            // (or EOF). Good enough for these files: one class per file except
            // the picker-option records, which follow each other.
            var declarations = Regex.Matches(
                source,
                @"(?:public|internal)\s+(?:sealed\s+|partial\s+|abstract\s+|static\s+)*class\s+(\w+)");
            for (var c = 0; c < declarations.Count; c++)
            {
                var start = declarations[c].Index;
                var end = c + 1 < declarations.Count
                    ? declarations[c + 1].Index
                    : source.Length;
                var classSource = source[start..end];
                var className = declarations[c].Groups[1].Value;
                var fileLabel = file.Name;

                var getters = FindLocalizedGetterNames(classSource);
                if (getters.Count > 0)
                {
                    yield return new ScannedVm(fileLabel, className, classSource, getters);
                }
            }
        }
    }

    /// <summary>
    /// Finds every string-returning property getter in a class source that
    /// resolves through the <c>_localization</c> field: expression-bodied
    /// getters (<c>string X =&gt; ... _localization[...] ...</c>) and
    /// block-bodied getters (brace-counted from the get accessor).
    /// </summary>
    private static List<string> FindLocalizedGetterNames(string classSource)
    {
        var getters = new List<string>();
        foreach (Match m in Regex.Matches(
                     classSource,
                     @"(?:public|internal|protected)[^()={};]*\bstring\??\s+(\w+)\s*=>([^;]{0,2000});"))
        {
            if (ResolvesThroughLocalization(m.Groups[2].Value))
            {
                getters.Add(m.Groups[1].Value);
            }
        }

        foreach (Match m in Regex.Matches(
                     classSource,
                     @"(?:public|internal|protected)\s+string\??\s+(\w+)\s*\{"))
        {
            var getter = GetterBody(classSource, m.Index + m.Length);
            if (getter is not null && ResolvesThroughLocalization(getter))
            {
                getters.Add(m.Groups[1].Value);
            }
        }

        return getters;
    }

    /// <summary>
    /// Whether a getter body resolves localized text: the <c>_localization</c>
    /// indexer or ANY member use on the field (e.g.
    /// <c>_localization.Format(...)</c>). Both shapes go stale on a culture
    /// switch when unregistered, so both count.
    /// </summary>
    private static bool ResolvesThroughLocalization(string getterBody) =>
        getterBody.Contains("_localization[") || getterBody.Contains("_localization.");

    /// <summary>
    /// The names a VM class refreshes on a culture change: everything in its
    /// <c>LocalizedProperties</c> registration, or (ModItemViewModel) every
    /// <c>OnPropertyChanged(nameof(X))</c> inside its Refresh method.
    /// </summary>
    private static HashSet<string> RegisteredNames(string classSource)
    {
        var names = new HashSet<string>();

        var registration = Regex.Match(
            classSource,
            @"LocalizedProperties\s*\{\s*get;\s*\}\s*=\s*new\[\]\s*\{([\s\S]*?)\};");
        if (registration.Success)
        {
            foreach (Match m in Regex.Matches(registration.Groups[1].Value, @"nameof\((\w+)\)"))
            {
                names.Add(m.Groups[1].Value);
            }

            return names;
        }

        var refresh = Regex.Match(
            classSource,
            @"public void Refresh\(\)\s*\{([\s\S]*?)\n    \}");
        if (refresh.Success)
        {
            foreach (Match m in Regex.Matches(refresh.Groups[1].Value, @"OnPropertyChanged\(nameof\((\w+)\)\)"))
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// Extracts the first get accessor's body after a property declaration's
    /// opening brace, brace-counted. Returns null when the shape is not a
    /// recognizable getter (the expression-bodied pattern covers that case;
    /// auto-properties return null because the accessor has no body).
    /// </summary>
    private static string? GetterBody(string source, int fromIndex)
    {
        var i = fromIndex;
        // Skip whitespace + expect "get".
        while (i < source.Length && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        if (i + 3 > source.Length || source[i..(i + 3)] != "get")
        {
            return null;
        }

        i += 3;
        while (i < source.Length && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        if (i >= source.Length || source[i] != '{')
        {
            return null;
        }

        var depth = 0;
        var start = i;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "modificus-curator.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        Assert.Fail(
            "Could not locate the repository root (src/modificus-curator.sln) " +
            "from the test output directory. These are repository source tests " +
            "and must run from a build inside the repo.");
        return null!; // unreachable; the assertion above throws.
    }
}
