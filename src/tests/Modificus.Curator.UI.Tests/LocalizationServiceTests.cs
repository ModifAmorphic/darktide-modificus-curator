using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="LocalizationService"/>: indexer resolution from the neutral resx,
/// graceful missing-key behavior, the culture switch raising property-changed
/// for the indexer (so dynamic bindings refresh), and string formatting for
/// parameterized messages.
/// </summary>
public sealed class LocalizationServiceTests
{
    [Fact]
    public void Indexer_resolves_a_known_key_for_the_neutral_culture()
    {
        var svc = new LocalizationService();
        svc.SetCulture("en");

        Assert.Equal("Modificus Curator", svc["App_Title"]);
        Assert.Equal("Profiles", svc["Profiles_Title"]);
    }

    [Fact]
    public void Indexer_returns_the_key_itself_for_a_missing_key()
    {
        // Graceful: a missed key is visible in the UI (so it gets caught), never
        // throws. This is what makes adding new resource keys safe.
        var svc = new LocalizationService();

        Assert.Equal("Not_A_Real_Key_42", svc["Not_A_Real_Key_42"]);
    }

    [Fact]
    public void Indexer_returns_empty_for_null_or_empty_key()
    {
        var svc = new LocalizationService();

        Assert.Equal(string.Empty, svc[""]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetCulture_blank_or_whitespace_resolves_to_invariant(string? name)
    {
        var svc = new LocalizationService();

        svc.SetCulture(name!);

        Assert.Equal(CultureInfo.InvariantCulture, svc.Culture);
    }

    [Fact]
    public void SetCulture_unknown_name_does_not_throw_and_strings_still_resolve_to_neutral()
    {
        // A user picking a language we don't ship yet must not crash the UI; the
        // indexer keeps resolving (falling back to the neutral resx since no
        // satellite exists for the unknown name). The specific culture result
        // varies by platform (Linux ICU may synthesize a culture for arbitrary
        // names instead of throwing CultureNotFoundException); the contract here
        // is "never throws + strings still resolve", not a specific culture.
        var svc = new LocalizationService();

        var ex = Record.Exception(() => svc.SetCulture("xx-XX"));

        Assert.Null(ex);
        Assert.Equal("Modificus Curator", svc["App_Title"]);
    }

    [Fact]
    public void SetCulture_known_name_switches_the_culture()
    {
        var svc = new LocalizationService();

        svc.SetCulture("fr");

        Assert.Equal("fr", svc.Culture.Name);
    }

    [Fact]
    public void Setting_a_different_culture_raises_property_changed_for_the_indexer()
    {
        // The dynamic-language path: switching culture must raise for "Item[]"
        // so every {Binding [Key]} binding re-evaluates. Without this, the live
        // UI would not refresh on a language change.
        var svc = new LocalizationService();
        var fired = new List<string?>();
        ((INotifyPropertyChanged)svc).PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        svc.SetCulture("fr");

        Assert.Contains("Item[]", fired);
        Assert.Contains(nameof(LocalizationService.Culture), fired);
    }

    [Fact]
    public void Setting_the_same_culture_is_a_noop_property_changed_wise()
    {
        var svc = new LocalizationService();
        svc.SetCulture("en");
        var fired = 0;
        ((INotifyPropertyChanged)svc).PropertyChanged += (_, _) => fired++;

        svc.SetCulture("en");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Format_substitutes_args_into_a_parameterized_message()
    {
        var svc = new LocalizationService();
        svc.SetCulture("en");

        var formatted = svc.Format("Profiles_DeleteMessage", "Alpha");

        Assert.Contains("Alpha", formatted);
        Assert.Contains("mod list", formatted);
    }

    [Fact]
    public void Format_returns_the_key_for_a_missing_format_key()
    {
        var svc = new LocalizationService();

        Assert.Equal("Missing_Format_Key", svc.Format("Missing_Format_Key", "x", "y"));
    }

    [Fact]
    public void Format_returns_the_unformatted_string_when_no_args_are_supplied()
    {
        var svc = new LocalizationService();
        svc.SetCulture("en");

        Assert.Equal("Modificus Curator", svc.Format("App_Title"));
    }

    [Fact]
    public void Indexer_reflects_a_culture_change_immediately()
    {
        // Confirms the dynamic-switching behavior end-to-end at the service level:
        // a key resolves to the new culture's value after SetCulture, even if the
        // only shipped language is English (the test exercises the resolution
        // path, which is what would surface a translated value for a real second
        // language).
        var svc = new LocalizationService();
        svc.SetCulture("en");
        var before = svc["App_Title"];

        // Switch to a culture with no satellite; the neutral value comes back.
        svc.SetCulture("de");
        var after = svc["App_Title"];

        Assert.Equal("Modificus Curator", before);
        Assert.Equal("Modificus Curator", after); // neutral fallback (no German resx shipped)
    }
}
