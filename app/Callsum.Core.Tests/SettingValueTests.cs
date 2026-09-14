using System.Text.Json;
using Callsum.Core;

namespace Callsum.Core.Tests;

public class SettingValueTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Неизменённое_значение_не_уходит_в_файл()
    {
        // Иначе каждое сохранение переписывало бы весь config.toml, даже когда
        // человек только заглянул в настройки.
        Assert.True(SettingValue.Same(Json("8192"), 8192));
        Assert.True(SettingValue.Same(Json("8192"), 8192.0));
        Assert.True(SettingValue.Same(Json("0.2"), 0.2));
        Assert.True(SettingValue.Same(Json("true"), true));
        Assert.True(SettingValue.Same(Json("\"qwen3:14b\""), "qwen3:14b"));
        Assert.True(SettingValue.Same(Json("""[".mkv", ".mp4"]"""), new[] { ".mkv", ".mp4" }));
    }

    [Fact]
    public void Изменённое_значение_видно()
    {
        Assert.False(SettingValue.Same(Json("8192"), 16384));
        Assert.False(SettingValue.Same(Json("true"), false));
        Assert.False(SettingValue.Same(Json("\"ru\""), "en"));
        Assert.False(SettingValue.Same(Json("""[".mkv"]"""), new[] { ".mkv", ".mp4" }));
        // Число, набранное строкой, — не то же самое: в файле появилось бы
        // num_ctx = "8192", и ядро споткнулось бы об это при чтении.
        Assert.False(SettingValue.Same(Json("8192"), "8192"));
    }

    [Fact]
    public void Расширения_приводятся_к_виду_который_ждёт_ядро()
    {
        // Ядро сравнивает суффикс файла с этим списком буквально.
        Assert.Equal([".mp4", ".mkv", ".wav"], SettingValue.ParseExtensions("mp4, .MKV; wav"));
        Assert.Equal([".mp4"], SettingValue.ParseExtensions("..mp4  mp4"));
        Assert.Empty(SettingValue.ParseExtensions("  .  "));
    }

    [Fact]
    public void Список_показывается_одной_строкой_и_читается_обратно()
    {
        var written = SettingValue.FormatList([".mkv", ".mp4"]);

        Assert.Equal(".mkv, .mp4", written);
        Assert.Equal([".mkv", ".mp4"], SettingValue.ParseList(written));
    }

    [Fact]
    public void Своё_значение_попадает_в_список()
    {
        // Иначе поле показывало бы пустоту: видимую часть редактируемого списка
        // рисует выбранный пункт, а не набранный текст.
        Assert.Equal(
            ["своя-модель", "large-v3", "medium"],
            SettingValue.Options(["large-v3", "medium"], "своя-модель"));
    }

    [Fact]
    public void Известное_значение_список_не_меняет()
    {
        Assert.Equal(
            ["large-v3", "medium"], SettingValue.Options(["large-v3", "medium"], "medium"));
        Assert.Equal(
            ["large-v3", "medium"], SettingValue.Options(["large-v3", "medium"], ""));
        Assert.Equal(
            ["large-v3", "medium"], SettingValue.Options(["large-v3", "medium"], null));
    }

    [Fact]
    public void Значение_по_умолчанию_читается_словами()
    {
        Assert.Equal("8192", SettingValue.Describe(Json("8192")));
        Assert.Equal("да", SettingValue.Describe(Json("true")));
        Assert.Equal("нет", SettingValue.Describe(Json("false")));
        Assert.Equal("large-v3", SettingValue.Describe(Json("\"large-v3\"")));
        Assert.Equal(".mkv, .mp4", SettingValue.Describe(Json("""[".mkv", ".mp4"]""")));
        Assert.Equal("", SettingValue.Describe(null));
    }
}
