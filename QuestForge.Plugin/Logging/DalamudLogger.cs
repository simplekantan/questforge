using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace QuestForge.Plugin.Logging;

public sealed class DalamudLogger<T> : ILogger<T>
{
    private readonly IPluginLog _log;

    public DalamudLogger(IPluginLog log) => _log = log;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Trace or LogLevel.Debug:
                _log.Debug(exception, msg);
                break;
            case LogLevel.Information:
                _log.Info(exception, msg);
                break;
            case LogLevel.Warning:
                _log.Warning(exception, msg);
                break;
            case LogLevel.Error or LogLevel.Critical:
                _log.Error(exception, msg);
                break;
        }
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
