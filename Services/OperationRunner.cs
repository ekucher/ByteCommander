using System.Windows;

namespace ByteCommander.Services;

/// <summary>Чим закінчилась файлова операція.</summary>
public sealed record OperationOutcome(bool Completed, bool Canceled, ProgressState State)
{
    public string Summary =>
        Util.Count(State.DoneFiles, "файл", "файли", "файлів") + (State.TotalBytes > 0 ? $", {Util.FormatSize(State.DoneBytes)}" : "");
}

/// <summary>
/// Запуск файлової операції у фоні з вікном прогресу (з’являється, якщо операція довга) і звітом про помилки.
/// Спільне для головного вікна й вікна порівняння каталогів.
/// </summary>
public static class OperationRunner
{
    /// <param name="ask">Що робити при конфлікті імен; викликається з фонового потоку.</param>
    public static async Task<OperationOutcome> RunAsync(Window owner, string title,
        Func<string, string, ConflictChoice> ask, Action<FileOperation, CancellationToken> work)
    {
        var state = new ProgressState();
        using var cts = new CancellationTokenSource();
        var op = new FileOperation(state, ask);
        var pw = new ProgressWindow(owner, title, state, cts);
        pw.Start();
        string? fatal = null;
        bool canceled = false;
        try
        {
            await Task.Run(() => work(op, cts.Token));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }
        finally
        {
            pw.Finish();
        }
        canceled |= cts.IsCancellationRequested;

        if (fatal != null) Dialogs.Error(owner, fatal);
        else if (op.Errors.Count > 0)
        {
            var list = string.Join("\n", op.Errors.Take(15));
            if (op.Errors.Count > 15) list += $"\n… і ще {op.Errors.Count - 15}";
            Dialogs.Error(owner, $"{title}: завершено з помилками ({op.Errors.Count}):\n\n{list}");
        }
        return new OperationOutcome(fatal == null && op.Errors.Count == 0 && !canceled, canceled, state);
    }
}
