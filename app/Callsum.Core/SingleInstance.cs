using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Callsum.Core;

/// <summary>
/// Одно приложение на одну установку.
///
/// Два окна подписываются на события OBS вдвоём и после остановки записи
/// берутся обрабатывать один и тот же файл, а два ядра держат в видеопамяти
/// по своей копии модели распознавания — вместе с Ollama туда не помещается
/// и одна. Поэтому второй запуск не поднимает своё окно, а просит показаться
/// то, которое уже открыто.
///
/// Место занимает именованный мьютекс, а просьба «покажись» идёт по
/// именованному каналу. Канал здесь не только средство связи, но и
/// доказательство жизни: занятое место при молчащем канале означает, что
/// держатель уходит или завис, — см. <see cref="Claim"/>.
///
/// То же самое умеет окно первой версии (`callsum/gui.py`), и по той же
/// причине; здесь это переписано средствами .NET.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Что второй экземпляр говорит первому.</summary>
    public const string Show = "show";

    /// <summary>Чем первый отвечает, подняв окно.</summary>
    public const string Done = "ok";

    private readonly string _key;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _serving;

    private SingleInstance(string key, Mutex mutex)
    {
        _key = key;
        _mutex = mutex;
    }

    /// <summary>
    /// Имя, по которому экземпляры узнают друг друга.
    ///
    /// В нём папка программы, а не одно общее слово: установленная копия и
    /// собранная из исходников — разные приложения, и разработка не должна
    /// требовать закрывать рабочую копию. Два ярлыка на одну установку ведут
    /// в одну папку, поэтому имя у них совпадает, — а это и есть тот случай,
    /// ради которого всё затевалось.
    ///
    /// Путь берётся не как есть: обратному слешу в имени мьютекса не место,
    /// а регистр Windows не различает. Поэтому от приведённого пути остаётся
    /// короткая свёртка.
    /// </summary>
    public static string KeyFor(string programFolder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder))
            .ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "callsum-" + Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Занять место. Возвращает null, если оно уже занято.
    ///
    /// Ожидание пригождается после обновления: Velopack перезапускает
    /// приложение, дождавшись ухода прежнего процесса, но если тот задержится,
    /// новый увидел бы место занятым и молча вышел — обновление не поднимало бы
    /// приложение обратно.
    ///
    /// Разделяет она именно процессы: мьютекс Windows пускает своего владельца
    /// повторно, поэтому из потока, который уже занял место, вызов вернёт ещё
    /// одно. Для нашей задачи это неважно — второй запуск всегда чужой процесс.
    /// </summary>
    public static SingleInstance? Claim(string key, TimeSpan? wait = null)
    {
        // Local\ — на сеанс пользователя: приложение ставится в профиль, и двум
        // людям за одной машиной мешать друг другу незачем.
        var mutex = new Mutex(false, @"Local\" + key);
        bool mine;
        try
        {
            mine = mutex.WaitOne(wait ?? TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Держатель умер, не отпустив мьютекс: место освободилось, и оно наше.
            mine = true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (mine)
        {
            return new SingleInstance(key, mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Попросить уже открытое приложение показаться. False — никто не ответил.
    ///
    /// Молчание означает, что занявший место не работает: уходит после
    /// обновления или завис. Тогда вызывающему стоит попробовать занять место
    /// самому, а не исчезать без следа.
    /// </summary>
    public static bool Signal(string key, TimeSpan? wait = null)
    {
        using var deadline = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(5));
        try
        {
            return AskAsync(key, deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or TimeoutException or IOException
                                              or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Слушать просьбы показаться.
    ///
    /// Обработчик вызывается из чужого потока: окно из него трогать нельзя,
    /// нужно переложить работу в поток окна.
    /// </summary>
    public void Listen(Action onShow) => _serving = Task.Run(() => ServeAsync(onShow));

    public void Dispose()
    {
        _stopping.Cancel();

        // Дождаться слушателя обязательно, и не из вежливости: он ждёт
        // соединения по токену, и если выдернуть источник токена из-под
        // незавершённого ожидания, отказ прилетает в поток завершения
        // ввода-вывода — а там его некому поймать, и падает весь процесс.
        try
        {
            _serving?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Слушатель мог уйти с отказом — на закрытие это не влияет.
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Мьютекс не наш или уже отпущен — отпускать нечего.
        }

        _mutex.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// Слова по каналу ходят голыми байтами, без StreamReader и StreamWriter.
    ///
    /// Это не экономия, а необходимость: те двое поверх двустороннего канала
    /// зажимают друг друга намертво — пишущий ждёт, пока прочитают, читающий
    /// ждёт, пока допишут, и обе стороны стоят так вечно. Проверено отдельной
    /// пробой: на строках виснет, на байтах проходит.
    ///
    /// Сообщение здесь в несколько байт, поэтому оно доходит одним куском,
    /// и собирать его из частей не нужно.
    /// </summary>
    private static async Task<bool> AskAsync(string key, CancellationToken cancellation)
    {
        // CurrentUserOnly с обеих сторон: канал наш, и разговаривать с чужими
        // пользователями ему незачем.
        using var pipe = new NamedPipeClientStream(
            ".", key, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellation).ConfigureAwait(false);

        await Say(pipe, Show, cancellation).ConfigureAwait(false);
        return await Hear(pipe, cancellation).ConfigureAwait(false) == Done;
    }

    private static async Task Say(PipeStream pipe, string word, CancellationToken cancellation)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(word), cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }

    private static async Task<string> Hear(PipeStream pipe, CancellationToken cancellation)
    {
        var buffer = new byte[16];
        var read = await pipe.ReadAsync(buffer, cancellation).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private async Task ServeAsync(Action onShow)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _key, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);

                if (await Hear(pipe, _stopping.Token).ConfigureAwait(false) == Show)
                {
                    onShow();
                    // Ответ нужен не для вежливости: пока он не пришёл, второй
                    // экземпляр жив, а с ним живо и право выводить окна вперёд,
                    // которое он нам отдал.
                    await Say(pipe, Done, _stopping.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                                  or ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                // Спросивший ушёл, не договорив, — ждём следующего.
            }
        }
    }
}
