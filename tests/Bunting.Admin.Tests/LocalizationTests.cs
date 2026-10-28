using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunting.Admin.Components.Pages;
using Bunting.Admin.Tests.Support;
using Bunting.Domain;

namespace Bunting.Admin.Tests;

public partial class LocalizationTests
{
    [Fact]
    public async Task Texts_follow_the_current_ui_culture()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            await using var ctx = new AdminTestContext(ProjectRole.Viewer);

            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var english = ctx.Render<Projects>();
            await english.WaitForAssertionAsync(() => Assert.Contains("Open", english.Markup));
            Assert.Contains("Projects", english.Markup);

            CultureInfo.CurrentUICulture = new CultureInfo("tr");
            var turkish = ctx.Render<Projects>();
            await turkish.WaitForAssertionAsync(() => Assert.NotEmpty(turkish.FindAll("[data-testid='project-card']")));
            Assert.Contains("Projeler", turkish.Markup);
            Assert.Contains("Aç", turkish.Markup);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void Every_turkish_text_is_filled_in_and_keeps_the_placeholders_of_its_english_key()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bunting.Admin", "Resources", "SharedResource.tr.resx");
        var entries = XDocument.Load(path).Root!.Elements("data").Select(d => (Key: (string)d.Attribute("name")!, Value: (string)d.Element("value")!)).ToList();

        Assert.NotEmpty(entries);
        foreach (var (key, value) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"'{key}' has no Turkish text.");
            Assert.Equal(Placeholders(key), Placeholders(value));
        }
    }

    private static string[] Placeholders(string text) => [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Order()];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
