using Kokuban;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace ILRecover.Helpers;

public static class Log
{
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _logger;
    private static ILogger? _successLogger;
    private static bool _isInitialized;

    public static ILogger? Global
    {
        get
        {
            EnsureInitialized();
            return _logger;
        }
    }

    private static void EnsureInitialized()
    {
        if (_isInitialized) return;
        InitializeLogger(LogLevel.Information);
    }

    private static void InitializeLogger(LogLevel minLevel)
    {
        _loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(minLevel);

            logging.AddZLoggerConsole(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0} {1} ",
                        (in template, in info) =>
                        {
                            var timestamp = Chalk.Gray + info.Timestamp.Local.ToString("HH:mm:ss");
                            var logLevel = info.Category.Name == "ILRecover.Success"
                                ? Chalk.Green + "[SUC]"
                                : GetColoredLogLevel(info.LogLevel);
                            template.Format(timestamp, logLevel);
                        });
                });
                options.LogToStandardErrorThreshold = LogLevel.Error;
            });
        });

        _logger = _loggerFactory.CreateLogger("ILRecover");
        _successLogger = _loggerFactory.CreateLogger("ILRecover.Success");
        _isInitialized = true;
    }

    private static string GetColoredLogLevel(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => Chalk.Magenta + "[TRC]",
            LogLevel.Debug => Chalk.Cyan + "[DBG]",
            LogLevel.Information => Chalk.Blue + "[INF]",
            LogLevel.Warning => Chalk.Yellow + "[WRN]",
            LogLevel.Error => Chalk.Red + "[ERR]",
            LogLevel.Critical => Chalk.BgRed.White + "[CRT]",
            _ => Chalk.White + "[???]"
        };

    public static void Info(string message)
    {
        EnsureInitialized();
        _logger?.ZLogInformation($"{message}");
    }

    public static void Success(string message)
    {
        EnsureInitialized();
        _successLogger?.ZLogInformation($"{message}");
    }

    public static void Error(string message)
    {
        EnsureInitialized();
        _logger?.ZLogError($"{message}");
    }

    public static void Error(Exception exception, string message)
    {
        EnsureInitialized();
        _logger?.ZLogError(exception, $"{message}");
    }

    public static void Warning(string message)
    {
        EnsureInitialized();
        _logger?.ZLogWarning($"{message}");
    }

    public static void Debug(string message)
    {
        EnsureInitialized();
        _logger?.ZLogDebug($"{message}");
    }

    public static void Verbose(string message)
    {
        EnsureInitialized();
        _logger?.ZLogTrace($"{message}");
    }

    public static void EnableDebugLogging()
    {
        if (_isInitialized) Shutdown();
        InitializeLogger(LogLevel.Debug);
    }

    public static void Shutdown()
    {
        if (!_isInitialized) return;
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        _logger = null;
        _successLogger = null;
        _isInitialized = false;
    }
}