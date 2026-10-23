using System.Text.RegularExpressions;
using GpuTweak.Core.Localization;

namespace GpuTweak.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishTableKeepsEveryPlaceholder()
    {
        var table = L.Table("en");
        Assert.NotEmpty(table);
        foreach (var (turkish, english) in table)
        {
            var a = Regex.Matches(turkish, @"\{\d+[^}]*\}").Select(m => m.Value).Order();
            var b = Regex.Matches(english, @"\{\d+[^}]*\}").Select(m => m.Value).Order();
            Assert.True(a.SequenceEqual(b), $"{turkish} → {english}");
        }
    }

    [Fact]
    public void EveryTextInTheSourceHasAnEnglishTranslation()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src");
        Assert.True(Directory.Exists(src), src);
        var table = L.Table("en");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"L\.[TF]\(""((?:[^""\\]|\\.)*)"""))
            {
                var key = Regex.Unescape(m.Groups[1].Value);
                if (!table.ContainsKey(key)) missing.Add(key);
            }
        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"local:Loc\.\w+=""([^""]*)""|\{local:T '?([^}']*)'?\}"))
            {
                var key = System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (!table.ContainsKey(key)) missing.Add(key);
            }
        Assert.Empty(missing);
    }
}
