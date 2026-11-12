using Ambit.Optima.Integrator.SqlServer.Logging;
using Microsoft.Extensions.Logging;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class SqlLoggerTests
{
    private static (ILogger logger, SqlLogChannel channel) Logger(string category = "Agent")
    {
        var channel = new SqlLogChannel();
        return (new SqlLoggerProvider(channel).CreateLogger(category), channel);
    }

    [Fact]
    public void An_exception_leaves_only_its_type()
    {
        var (logger, channel) = Logger();

        logger.LogWarning(new InvalidOperationException("PESEL 90052012345"), "Failed.");

        Assert.True(channel.Reader.TryRead(out var entry));
        Assert.Equal("Failed.", entry.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.ExceptionType);
    }

    [Fact]
    public void Below_information_stays_out()
    {
        var (logger, channel) = Logger();

        logger.LogDebug("Detail.");

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.Something")]
    [InlineData("Ambit.Optima.Integrator.SqlServer.Logging.SqlLogWriter")]
    public void The_log_does_not_log_its_own_connection(string category)
    {
        var (logger, channel) = Logger(category);

        logger.LogWarning("Failed.");

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void A_long_message_is_cut_to_the_column()
    {
        var (logger, channel) = Logger();

        logger.LogInformation("{Text}", new string('x', 5000));

        Assert.True(channel.Reader.TryRead(out var entry));
        Assert.Equal(4000, entry.Message.Length);
    }

    [Fact]
    public void A_full_channel_drops_the_oldest_and_never_blocks()
    {
        var (logger, channel) = Logger();

        foreach (var number in Enumerable.Range(0, 1001))
            logger.LogInformation("{Number}", number);

        Assert.True(channel.Reader.TryRead(out var oldest));
        Assert.Equal("1", oldest.Message);
    }
}
