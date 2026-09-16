using Callsum.Core;

namespace Callsum.Core.Tests;

public class WelcomePreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public void Missing_preference_shows_the_welcome_screen()
    {
        var preferences = new WelcomePreferences(Path.Combine(_folder, "welcome.json"));

        Assert.True(preferences.ShouldShow());
    }

    [Fact]
    public void Saved_choice_hides_the_welcome_screen()
    {
        var path = Path.Combine(_folder, "welcome.json");
        var preferences = new WelcomePreferences(path);
        preferences.Save(dontShow: true);

        Assert.False(new WelcomePreferences(path).ShouldShow());
    }

    [Fact]
    public void Invalid_preference_does_not_hide_the_welcome_screen()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "welcome.json");
        File.WriteAllText(path, "not json");

        Assert.True(new WelcomePreferences(path).ShouldShow());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
