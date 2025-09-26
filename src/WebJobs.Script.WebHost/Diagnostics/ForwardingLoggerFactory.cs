// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Microsoft.Extensions.Logging
{
    /// <summary>
    /// A logger factory that creates loggers which track the current active ScriptHost (if any), falling
    /// back to the WebHost logger if no ScriptHost is active.
    /// </summary>
    public sealed class ForwardingLoggerFactory : ILoggerFactory
    {
        public const string Key = "Forwarding";

        private readonly ILoggerFactory _inner;
        private readonly WebJobsScriptHostService _script;

        public ForwardingLoggerFactory(ILoggerFactory inner, WebJobsScriptHostService script)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(script);
            _inner = inner;
            _script = script;
        }

        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(categoryName, _inner.CreateLogger(categoryName), _script);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // no op.
        }

        private class Logger : ILogger
        {
            private readonly string _categoryName;
            private readonly ILogger _fallback;
            private readonly WebJobsScriptHostService _script;

            // we use weak references so a host shutting down will clean up this logger.
            private readonly WeakReference<ILogger> _current = new(null!);
            private readonly WeakReference<IServiceProvider> _services = new(null!);

            public Logger(string categoryName, ILogger inner, WebJobsScriptHostService script)
            {
                ArgumentNullException.ThrowIfNull(inner);
                ArgumentNullException.ThrowIfNull(script);
                _categoryName = categoryName;
                _fallback = inner;
                _script = script;
            }

            private ILogger Current
            {
                get
                {
                    // Check if the current ScriptHost logger is valid (also checks if we have one at all).
                    if (!IsLoggerCurrent())
                    {
                        // Intentional nested if, to avoid entering else-if when logger is not current.
                        if (_script.Services is { } services)
                        {
                            ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(_categoryName);
                            _current.SetTarget(logger);
                            return logger;
                        }
                    }
                    else if (_current.TryGetTarget(out ILogger? logger))
                    {
                        // If we have a current logger, return it. This only runs if the logger is valid.
                        return logger;
                    }

                    // No current ScriptHost logger, or the ScriptHost is gone. Use the fallback WebHost logger.
                    return _fallback;
                }
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => Current.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => Current.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Current.Log(logLevel, eventId, state, exception, formatter);

            private bool IsLoggerCurrent()
            {
                if (_services.TryGetTarget(out IServiceProvider? services))
                {
                    return ReferenceEquals(services, _script.Services);
                }

                return false;
            }
        }
    }
}
