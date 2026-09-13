using System.Text;
using Callsum.Obs;

// Утилита для проверки связки с OBS до того, как появится окно приложения.
// Команды: doctor, setup, start, stop, watch.

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "doctor";
var settings = ObsSettings.Load();

await using var client = new ObsClient(settings);
try
{
    await client.ConnectAsync();
}
catch (ObsException exception)
{
    Console.Error.WriteLine($"! {exception.Message}");
    return 1;
}

var obs = new ObsService(client);

switch (command)
{
    case "doctor":
        var status = await obs.GetRecordStatusAsync();
        var (profile, profiles) = await obs.GetProfilesAsync();
        var (collection, _) = await obs.GetSceneCollectionsAsync();
        Console.WriteLine($"[ok] OBS на {settings.Host}:{settings.Port}");
        Console.WriteLine($"     профиль: {profile} (всего {profiles.Count})");
        Console.WriteLine($"     коллекция сцен: {collection}");
        Console.WriteLine($"     запись идёт: {(status.Active ? "да" : "нет")}");
        Console.WriteLine($"     папка записи: {await obs.GetRecordDirectoryAsync()}");
        foreach (var input in await obs.GetInputsAsync())
        {
            Console.WriteLine($"     источник: {input.Name} ({input.Kind})");
        }

        break;

    case "setup":
        var recordings = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "recordings"));
        await new ObsSetup(obs).RunAsync(ObsSetup.DefaultProfile, recordings);
        break;

    case "start":
        await obs.StartRecordAsync();
        Console.WriteLine("Запись начата");
        break;

    case "stop":
        Console.WriteLine($"Запись остановлена: {await obs.StopRecordAsync() ?? "путь не сообщён"}");
        break;

    case "watch":
        // Проверка событий: OBS сообщает о записи, начатой любым способом.
        var finished = new TaskCompletionSource();
        obs.OnRecordStateChanged((active, path) =>
        {
            Console.WriteLine(active ? "Запись начата" : $"Запись остановлена: {path ?? "путь не сообщён"}");
            if (!active)
            {
                finished.TrySetResult();
            }
        });
        Console.WriteLine("Жду событий записи из OBS. Прервать — Ctrl+C.");
        await finished.Task;
        break;

    default:
        Console.Error.WriteLine($"! Неизвестная команда: {command}. Доступны: doctor, setup, start, stop, watch");
        return 1;
}

return 0;
