using Velopack;
using Velopack.Sources;

namespace Callsum.App;

/// <summary>
/// Обновления: приложение обновляет себя само, из релизов на GitHub.
///
/// Установщика с правами администратора нет и не будет — приложение живёт
/// в профиле пользователя, — поэтому обновиться оно может только само.
/// Новая версия скачивается в стороне и применяется по нажатию: посреди
/// созвона перезапускаться нельзя.
/// </summary>
public sealed class Updates
{
    public const string Repository = "https://github.com/Heart-Machine/callsum";

    /// <summary>Чем закончилась проверка: версия, причина отказа — или ни то ни другое.</summary>
    /// <param name="Version">Скачанное обновление; null — обновлять нечего.</param>
    /// <param name="Error">Почему проверить не вышло; null — вышло.</param>
    public sealed record Check(string? Version, string? Error);

    private readonly Action<string> _log;
    private readonly UpdateManager _manager;

    private UpdateInfo? _ready;

    public Updates(Action<string> log)
    {
        _log = log;
        _manager = new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: false));
    }

    /// <summary>Установлено ли приложение установщиком: из папки сборки обновлять нечего.</summary>
    public bool Managed => _manager.IsInstalled;

    /// <summary>Версия, которая уже скачана и ждёт перезапуска.</summary>
    public string? Pending => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Проверить и сразу скачать обновление. Возвращает версию или null.
    ///
    /// Скачивание идёт молча: пока оно не закончилось, предлагать перезапуск
    /// нечестно — нажавший ждал бы непонятно чего.
    /// </summary>
    public async Task<string?> FetchAsync() => (await CheckAsync().ConfigureAwait(false)).Version;

    /// <summary>
    /// То же самое, но с причиной неудачи.
    ///
    /// Окно, где человек сам нажал «Проверить обновление», должно отличать
    /// «стоит последняя версия» от «не достучался до GitHub»: молчание в ответ
    /// на нажатие выглядит поломкой.
    /// </summary>
    public async Task<Check> CheckAsync()
    {
        if (!Managed)
        {
            return new Check(null, "Приложение запущено из папки сборки — обновлять нечего");
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new Check(null, null);
            }

            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            _ready = update;
            return new Check(Pending, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Нет сети, нет релизов, сломанный ответ GitHub — приложение
            // работает и без обновлений, поэтому это замечание, а не ошибка.
            _log($"Не удалось проверить обновления: {exception.Message}");
            return new Check(null, $"Не удалось проверить обновления: {exception.Message}");
        }
    }

    /// <summary>Применить скачанное и перезапуститься.</summary>
    public void Apply()
    {
        if (_ready is { } update)
        {
            _manager.ApplyUpdatesAndRestart(update);
        }
    }
}
