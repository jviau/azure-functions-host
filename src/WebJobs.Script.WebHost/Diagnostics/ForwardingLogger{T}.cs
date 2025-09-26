// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

#nullable enable

namespace Microsoft.Extensions.Logging
{
    [DebuggerDisplay("{_logger}")]
    public class ForwardingLogger<T> : ILogger<T>
    {
        private readonly ILogger _logger;

        public ForwardingLogger([FromKeyedServices(ForwardingLoggerFactory.Key)] ILoggerFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _logger = factory.CreateLogger<T>();
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => _logger.BeginScope(state);

        bool ILogger.IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
