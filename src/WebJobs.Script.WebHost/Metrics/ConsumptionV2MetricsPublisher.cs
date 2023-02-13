// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Metrics
{
    public class ConsumptionV2MetricsPublisher : IMetricsPublisher
    {
        private const int DefaultMetricsPublishIntervalMS = 5000; // 30 * 1000;
        private const int MinimumActivityIntervalMS = 100;

        private readonly ILogger _logger;
        private readonly IOptionsMonitor<StandbyOptions> _standbyOptions;
        private readonly IEnvironment _environment;
        private readonly IDisposable _standbyOptionsOnChangeSubscription;
        private readonly TimeSpan _metricPublishInterval = TimeSpan.FromMilliseconds(DefaultMetricsPublishIntervalMS);
        private readonly TimeSpan _timerStartDelay = TimeSpan.FromSeconds(2);
        private readonly HostNameProvider _hostNameProvider;
        private readonly object _lock = new object();

        private string _containerName;
        private Timer _metricsPublisherTimer;
        private HttpClient _httpClient;
        private string _tenant;
        private Process _process;
        private string _stampName;
        private bool _initialized = false;

        private ValueStopwatch _stopwatch;
        private long _activeFunctionCount = 0;
        private long _functionExecutionCount = 0;
        private long _functionExecutionTimeMS = 0;

        public ConsumptionV2MetricsPublisher(IEnvironment environment, IOptionsMonitor<StandbyOptions> standbyOptions, ILogger<ConsumptionV2MetricsPublisher> logger, HostNameProvider hostNameProvider, HttpClient httpClient = null)
        {
            _standbyOptions = standbyOptions ?? throw new ArgumentNullException(nameof(standbyOptions));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hostNameProvider = hostNameProvider ?? throw new ArgumentNullException(nameof(hostNameProvider));

            _containerName = _environment.GetEnvironmentVariable(EnvironmentSettingNames.ContainerName);

            _httpClient = (httpClient != null) ? httpClient : CreateMetricsPublisherHttpClient();
            if (_standbyOptions.CurrentValue.InStandbyMode)
            {
                _standbyOptionsOnChangeSubscription = _standbyOptions.OnChange(o => OnStandbyOptionsChange());
            }
            else
            {
                Start();
            }
        }

        public void OnFunctionStarted(string functionName, string invocationId)
        {
            if (!_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_activeFunctionCount == 0)
                {
                    // we're transitioning from inactive to active
                    _stopwatch = ValueStopwatch.StartNew();
                }

                _activeFunctionCount++;
            }
        }

        public void OnFunctionCompleted(string functionName, string invocationId)
        {
            if (!_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_activeFunctionCount > 0)
                {
                    _activeFunctionCount--;
                }

                if (_activeFunctionCount == 0)
                {
                    // we're transitioning from active to inactive accumulate the elapsed time,
                    // applying the minimum interval
                    var elapsedMS = _stopwatch.GetElapsedTime().TotalMilliseconds;
                    var duration = Math.Max(elapsedMS, MinimumActivityIntervalMS);
                    _functionExecutionTimeMS += (long)duration;
                }

                // for every completed invocation, increment our invocation count
                _functionExecutionCount++;
            }
        }

        public void AddFunctionExecutionActivity(string functionName, string invocationId, int concurrency, string executionStage, bool success, long executionTimeSpan, string executionId, DateTime eventTimeStamp, DateTime functionStartTime)
        {
            // nothing to do here - we only care about Started/Completed events.
        }

        public void Initialize()
        {
            _stampName = _environment.GetEnvironmentVariable(EnvironmentSettingNames.WebSiteHomeStampName);
            _tenant = _environment.GetEnvironmentVariable(EnvironmentSettingNames.WebSiteStampDeploymentId)?.ToLowerInvariant();
            _process = Process.GetCurrentProcess();
            _initialized = true;
        }

        public void Start()
        {
            Initialize();

            _metricsPublisherTimer = new Timer(OnFunctionMetricsPublishTimer, null, _timerStartDelay, _metricPublishInterval);

            _logger.LogInformation(string.Format("Starting metrics publisher for container : {0}.", _containerName));
        }

        internal async void OnFunctionMetricsPublishTimer(object state)
        {
            if (_functionExecutionCount == 0 && _functionExecutionTimeMS == 0)
            {
                // no activity to report
                return;
            }

            // we've been accumulating function activity for the entire period
            // publish this activity and reset
            Metrics metrics = null;
            lock (_lock)
            {
                metrics = new Metrics
                {
                    FunctionExecutionCount = _functionExecutionCount,
                    FunctionExecutionTimeMS = _functionExecutionTimeMS
                };

                _functionExecutionTimeMS = _functionExecutionCount = 0;
            }

            await PublishMetricsAsync(metrics);
        }

        private void OnStandbyOptionsChange()
        {
            if (!_standbyOptions.CurrentValue.InStandbyMode)
            {
                Start();
            }
        }

        private HttpClient CreateMetricsPublisherHttpClient()
        {
            var clientHandler = new HttpClientHandler();
            clientHandler.ServerCertificateCustomValidationCallback = ValidateRemoteCertificate;

            return new HttpClient(clientHandler);
        }

        private bool ValidateRemoteCertificate(HttpRequestMessage httpRequestMessage, X509Certificate2 certificate, X509Chain certificateChain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            bool validateCertificateResult = (sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch) && certificateChain.Build(certificate);

            if (!validateCertificateResult)
            {
                if (certificateChain.ChainStatus.Any(s => s.Status != X509ChainStatusFlags.NoError))
                {
                    _logger.LogError($"Failed to build remote certificate chain for {httpRequestMessage.RequestUri} with error {certificateChain.ChainStatus.First(chain => chain.Status != X509ChainStatusFlags.NoError).Status}");
                }
                else
                {
                    _logger.LogError($"Failed to validate certificate for {httpRequestMessage.RequestUri} with error {sslPolicyErrors}");
                }
            }
            return validateCertificateResult;
        }

        private async Task<(bool Success, string ErrorMessage)> PublishMetricsAsync(Metrics metrics)
        {
            string content = JsonConvert.SerializeObject(metrics);
            var token = SimpleWebTokenHelper.CreateToken(DateTime.UtcNow.AddMinutes(5));

            var protocol = "https";
            if (_environment.GetEnvironmentVariable(EnvironmentSettingNames.SkipSslValidation) == "1")
            {
                // On private stamps with no ssl certificate use http instead.
                protocol = "http";
            }

            var hostname = _hostNameProvider.Value;
            string url = $"{protocol}://{hostname}/operations/metrics";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                var requestId = Guid.NewGuid().ToString();
                request.Headers.Add(ScriptConstants.AntaresLogIdHeaderName, requestId);
                request.Headers.Add("User-Agent", ScriptConstants.FunctionsUserAgent);
                request.Headers.Add(ScriptConstants.SiteTokenHeaderName, token);
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");

                _logger.LogDebug($"Making metrics publish request (RequestId={requestId}, Uri={request.RequestUri.ToString()}, Content={content}).");

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Metrics publish call succeeded.");
                    return (true, null);
                }
                else
                {
                    string message = $"Metrics publish call failed (StatusCode={response.StatusCode}).";
                    _logger.LogDebug(message);
                    return (false, message);
                }
            }
        }

        internal class Metrics
        {
            public long FunctionExecutionTimeMS { get; set; }

            public long FunctionExecutionCount { get; set; }
        }
    }
}
