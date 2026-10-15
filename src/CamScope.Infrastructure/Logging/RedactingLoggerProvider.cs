using CamScope.Application.Security;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Logging;

public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _innerProvider;

    public RedactingLoggerProvider(ILoggerProvider innerProvider)
    {
        _innerProvider = innerProvider;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var innerLogger = _innerProvider.CreateLogger(categoryName);
        return new RedactingLogger(innerLogger);
    }

    public void Dispose()
    {
        _innerProvider.Dispose();
    }

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;

        public RedactingLogger(ILogger inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string RedactingFormatter(TState s, Exception? ex)
            {
                var raw = formatter(s, ex);
                return SecretRedactor.Redact(raw);
            }

            _inner.Log(logLevel, eventId, state, exception, RedactingFormatter);
        }
    }
}
