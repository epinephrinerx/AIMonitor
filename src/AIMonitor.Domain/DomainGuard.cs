using System.Collections.ObjectModel;

namespace AIMonitor.Domain;

internal static class DomainGuard
{
    /// <summary>
    /// Copies <paramref name="source"/> into a <see cref="ReadOnlyCollection{T}"/> backed by a
    /// private list, so casting the exposed property back to <see cref="IList{T}"/> and calling
    /// a mutator throws instead of silently mutating shared state.
    /// </summary>
    public static IReadOnlyList<T> ToReadOnlyCopy<T>(IReadOnlyList<T>? source) =>
        source is null ? Array.Empty<T>() : new ReadOnlyCollection<T>([.. source]);

    /// <summary>Same guarantee as <see cref="ToReadOnlyCopy{T}"/> but for dictionaries.</summary>
    public static IReadOnlyDictionary<TKey, TValue> ToReadOnlyCopy<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? source)
        where TKey : notnull =>
        source is null
            ? new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>())
            : new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(source));

    public static string RequireNonBlank(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }

        return value;
    }

    /// <summary>
    /// Rejects an enum value that is not one of the type's declared members, so a raw cast such as
    /// <c>(Severity)999</c> cannot slip past the public boundary and later outrank every known value.
    /// </summary>
    public static TEnum RequireDefined<TEnum>(TEnum value, string paramName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Unknown {typeof(TEnum).Name}.");
        }

        return value;
    }
}
