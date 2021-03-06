// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if PLATFORM_OSX
using System.IO;
#endif

#if FEATURE_ARIA_TELEMETRY
#if !FEATURE_CORECLR
using Microsoft.Applications.Telemetry;
using Microsoft.Applications.Telemetry.Desktop;
#endif
#endif

namespace BuildXL.Utilities.Instrumentation.Common
{
    /// <summary>
    /// Static state needed for the AriaV2 logger
    /// </summary>
    public static class AriaV2StaticState
    {
        /// <nodoc />
        public const int AriaMaxPropertyLength = 100;

        private static bool s_hasBeenInitialized;
        private static readonly object s_syncRoot = new object();
        private static readonly TimeSpan s_defaultShutdownTimeout = TimeSpan.FromSeconds(20);

        private static string s_ariaTelemetryDBLocation;

#if FEATURE_ARIA_TELEMETRY
#if FEATURE_CORECLR
#if PLATFORM_OSX
        internal static IntPtr s_AriaLogger;
        private static readonly string s_ariaTelemetryDBName = "Aria.db";
#endif
#endif
#endif

        /// <summary>
        /// Used to determine whether AriaV2 logging should be enabled
        /// </summary>
        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// Enables AriaV2 in the application. Will automatically initialize the pipeline if necessary
        /// </summary>
        public static void Enable(string tenantToken, string offlineTelemetryDBPath = "")
        {
            s_ariaTelemetryDBLocation = offlineTelemetryDBPath;
            Initialize(tenantToken);
            IsEnabled = true;
        }

        /// <summary>
        /// Disables AriaV2 in the application
        /// </summary>
        public static void Disable()
        {
            IsEnabled = false;
        }

        private static void Initialize(string tenantToken)
        {
            lock (s_syncRoot)
            {
                // Initialization may only happen once per application lifetime so we need some static state to enforce this
                if (!s_hasBeenInitialized)
                {
#if FEATURE_ARIA_TELEMETRY
#if !FEATURE_CORECLR
                    LogConfiguration configuration = new LogConfiguration()
                    {
                        PerformanceCounter = new PerformanceCounterConfiguration()
                        {
                            Enabled = false,
                        }
                    };

                    LogManager.Initialize(tenantToken, configuration);
#else
#if PLATFORM_OSX
                    Contract.Requires(s_ariaTelemetryDBLocation != null);
                    if (s_ariaTelemetryDBLocation.Length > 0 && !Directory.Exists(s_ariaTelemetryDBLocation))
                    {
                        Directory.CreateDirectory(s_ariaTelemetryDBLocation);
                    }

                    // s_ariaTelemetryDBLocation is defaulting to an empty string when not passed when enabling telemetry, in that case
                    // this causes the DB to be created in the current working directory of the process
                    s_AriaLogger = AriaMacOS.CreateAriaLogger(tenantToken, System.IO.Path.Combine(s_ariaTelemetryDBLocation, s_ariaTelemetryDBName));
#endif
#endif
#endif
                    s_hasBeenInitialized = true;
                }
            }
        }

        /// <summary>
        /// Ensure that all events are sent and shuts down telemetry. This should be done before the application exits.
        /// It should not be called if the application will log any more telemetry events in the future
        /// </summary>
        public static ShutDownResult TryShutDown(out Exception exception)
        {
            return TryShutDown(s_defaultShutdownTimeout, out exception);
        }

        /// <summary>
        /// Ensure that all events are sent and shuts down telemetry. This should be done before the application exits.
        /// It should not be called if the application will log any more telemetry events in the future
        /// </summary>
        public static ShutDownResult TryShutDown(TimeSpan timeout, out Exception exception)
        {
            exception = null;

            lock (s_syncRoot)
            {
                if (s_hasBeenInitialized)
                {
                    Exception thrownException = null;
                    ShutDownResult shutDownResult = ShutDownResult.Failure;
                    Task shutdownTask = Task.Factory.StartNew(() =>
                    {
                        try
                        {
#if FEATURE_ARIA_TELEMETRY
#if !FEATURE_CORECLR
                            LogManager.FlushAndTearDown();
#else
#if PLATFORM_OSX
                            AriaMacOS.DisposeAriaLogger(s_AriaLogger);
                            s_AriaLogger = IntPtr.Zero;
#endif
#endif
#endif
                            shutDownResult = ShutDownResult.Success;
                        }
                        catch (Exception ex)
                        {
                            thrownException = ex;
                            shutDownResult = ShutDownResult.Failure;
                        }
                    });
                    bool finished = shutdownTask.Wait(timeout);

                    if (!finished)
                    {
                        // The telemetry client API doesn't provide a better way to cancel the shutdown. Leaving it
                        // dangling isn't great, but the process is about to shut down anyway.
                        shutDownResult = ShutDownResult.Timeout;
                    }

                    return shutDownResult;
                }
            }

            return ShutDownResult.Success;
        }

        /// <summary>
        /// Result of shutdown request
        /// </summary>
        public enum ShutDownResult
        {
            /// <nodoc/>
            Success,

            /// <nodoc/>
            Failure,

            /// <nodoc/>
            Timeout,
        }

        /// <summary>
        /// Aria only allows alphanumeric, and underscores in property names. Replace all others with underscores and limit the length of the property name.
        /// </summary>
        public static string ScrubEventProperty(string originalProperty, int maxLength = AriaMaxPropertyLength)
        {
            return ShortenEventPropertyIfNeeded(ScrubInvalidCharactersFromEventProperty(originalProperty), maxLength);
        }

        private static string ScrubInvalidCharactersFromEventProperty(string originalProperty)
        {
            var sb = new StringBuilder(originalProperty);

            foreach (char c in originalProperty)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    sb.Replace(c, '_');
                }
            }

            Contract.Assert(sb.Length > 0, "property name must be greater than 0");

            // property name must not start with a digit
            if (sb[0] >= '0' && sb[0] <= '9')
            {
                sb.Insert(0, "e_");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Aria has a length restriction. Truncate the name from the right (keeping the right-most and the most significant name in a property name).
        /// </summary>
        private static string ShortenEventPropertyIfNeeded(string originalProperty, int maxLength)
        {
            if (originalProperty.Length <= maxLength)
            {
                return originalProperty;
            }

            string[] parts = originalProperty.Split(new [] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var requiredChars = (parts.Sum(p => p.Length + 1) - 1) - maxLength;

            while (requiredChars > 0)
            {
                bool removedChars = false;
                for (int j = 0; j < parts.Length - 1; j++)
                {
                    var part = parts[j];
                    if (part.Length > 1)
                    {
                        parts[j] = part.Substring(0, part.Length - 1);
                        requiredChars--;
                        removedChars = true;

                        if (requiredChars == 0)
                        {
                            break;
                        }
                    }
                }

                if (!removedChars)
                {
                    break;
                }
            }

            string result = string.Join("_", parts);

            if (requiredChars > 0)
            {
                const string truncatePrefix = "t__";
                result = truncatePrefix + originalProperty.Substring(originalProperty.Length - maxLength + truncatePrefix.Length);
            }

            Contract.Assert(result.Length <= maxLength);
            return result;
        }
    }
}
