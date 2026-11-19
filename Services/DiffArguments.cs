namespace ByteCommander.Services;

/// <summary>Допоміжні методи для аргументів режиму <c>--diff</c>.</summary>
public static class DiffArguments
{
    /// <summary>
    /// Перевіряє, чи аргумент позначає null-пристрій (порожній «файл», який передає git для доданих або видалених файлів).
    /// Порівняння без урахування регістру.
    /// </summary>
    /// <param name="arg">Аргумент командного рядка.</param>
    /// <returns><c>true</c>, якщо аргумент є одним із варіантів null-пристрою Windows або POSIX.</returns>
    public static bool IsNullDevice(string arg) =>
        arg is not null && (
            string.Equals(arg, "nul", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/dev/null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, @"\\.\nul", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "NUL:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Повертає повний шлях для аргументу або <c>null</c>, якщо шлях некоректний
    /// (наприклад, містить недопустимі символи), замість винятку від <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    /// <param name="arg">Шлях із командного рядка.</param>
    /// <returns>Повний шлях або <c>null</c>, якщо шлях не можна нормалізувати.</returns>
    public static string? TryGetFullPath(string arg)
    {
        try
        {
            return Path.GetFullPath(arg);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
