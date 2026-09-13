using Callsum.Core;

namespace Callsum.Core.Tests;

public class EngineStageTests
{
    [Fact]
    public void Скачивание_библиотек_подписано_по_человечески()
    {
        // Первое распознавание на машине занимает минуты: человек должен видеть,
        // что идёт скачивание, а не думать, что программа висит.
        Assert.Equal("Скачиваю библиотеки для видеокарты", EngineStage.Describe(EngineStage.Download));
    }

    [Fact]
    public void Незнакомая_стадия_показывается_как_есть()
    {
        // Ядро может оказаться новее приложения — пусть лучше покажет имя стадии,
        // чем промолчит.
        Assert.Equal("погода", EngineStage.Describe("погода"));
    }
}
