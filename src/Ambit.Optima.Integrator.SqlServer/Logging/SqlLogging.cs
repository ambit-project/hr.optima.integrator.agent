using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Ambit.Optima.Integrator.SqlServer.Logging;

public sealed record SqlLogEntry(
    string Level,
    string Category,
    string Message,
    string? ExceptionType
);

/// <summary>
/// Stands between logging and SQL Server. It is bounded and drops the oldest entry, so a database that
/// is down costs log lines and never memory or the Worker's time.
/// </summary>
public sealed class SqlLogChannel
{
    private const int Capacity = 1000;

    private readonly Channel<SqlLogEntry> _channel = Channel.CreateBounded<SqlLogEntry>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }
    );

    public ChannelReader<SqlLogEntry> Reader => _channel.Reader;

    public void Write(SqlLogEntry entry) => _channel.Writer.TryWrite(entry);
}

[ProviderAlias("IntegratorDatabase")]
public sealed class SqlLoggerProvider(SqlLogChannel channel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SqlLogger(categoryName, channel);

    public void Dispose() { }
}

/// <summary>
/// Stores the formatted message and only the type of an exception, whose text can hold values. Messages
/// must carry identifiers, not people: nothing here can tell a PESEL from an id.
/// </summary>
internal sealed class SqlLogger(string category, SqlLogChannel channel) : ILogger
{
    private const int MaxMessageLength = 4000;
    private const int MaxCategoryLength = 200;
    private const int MaxExceptionTypeLength = 200;

    // Logging about the log's own connection would feed itself.
    private static readonly string[] SilentCategories =
    [
        "Microsoft.Data.SqlClient",
        typeof(SqlLogWriter).FullName!,
    ];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel is >= LogLevel.Information and < LogLevel.None
        && !SilentCategories.Any(silent => category.StartsWith(silent, StringComparison.Ordinal));

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (!IsEnabled(logLevel))
            return;

        channel.Write(
            new SqlLogEntry(
                logLevel.ToString(),
                Cut(category, MaxCategoryLength),
                Cut(formatter(state, null), MaxMessageLength),
                exception?.GetType().FullName is { } type ? Cut(type, MaxExceptionTypeLength) : null
            )
        );
    }

    private static string Cut(string text, int length) =>
        text.Length > length ? text[..length] : text;
}
