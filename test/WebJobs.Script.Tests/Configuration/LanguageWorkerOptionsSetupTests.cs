// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Workers.Profiles;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class LanguageWorkerOptionsSetupTests
    {
        [Theory]
        [InlineData("DotNet")]
        [InlineData("dotnet")]
        [InlineData(null)]
        [InlineData("node")]
        public void LanguageWorkerOptions_Expected_ListOfConfigs(string workerRuntime)
        {
            var runtimeInfo = new TestSystemRuntimeInformation();
            var testEnvironment = new TestEnvironment();
            var testMetricLogger = new TestMetricsLogger();
            var configurationBuilder = new ConfigurationBuilder()
                .Add(new ScriptEnvironmentVariablesConfigurationSource());
            var configuration = configurationBuilder.Build();
            var testProfileManager = new Mock<IWorkerProfileManager>();

            if (!string.IsNullOrEmpty(workerRuntime))
            {
                testEnvironment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, workerRuntime);
            }

            LanguageWorkerOptionsSetup setup = new LanguageWorkerOptionsSetup(configuration, NullLoggerFactory.Instance, testEnvironment, testMetricLogger, testProfileManager.Object);
            LanguageWorkerOptions options = new LanguageWorkerOptions();

            setup.Configure(options);

            if (string.IsNullOrEmpty(workerRuntime))
            {
                Assert.Equal(5, options.WorkerConfigs.Count);
            }
            else if (workerRuntime.Equals(RpcWorkerConstants.DotNetLanguageWorkerName, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Empty(options.WorkerConfigs);
            }
            else
            {
                Assert.Equal(1, options.WorkerConfigs.Count);
            }
        }

        [Theory]
        [InlineData("DotNet")]
        [InlineData("dotnet")]
        [InlineData(null)]
        [InlineData("node")]
        [InlineData("DOTNET", true)]
        [InlineData("dotnet", true)]
        [InlineData(null, true)]
        [InlineData("java", true)]
        public void LanguageWorkerOptions_Expected_ListOfConfigs_PlaceholderMode(string workerRuntime, bool dotnetIsolatedPlaceHolderEnabled = false)
        {
            var testEnvironment = new TestEnvironment();
            testEnvironment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            if (!string.IsNullOrEmpty(workerRuntime))
            {
                testEnvironment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, workerRuntime);
            }

            var testMetricLogger = new TestMetricsLogger();
            var configurationBuilder = new ConfigurationBuilder()
                .Add(new ScriptEnvironmentVariablesConfigurationSource());
            var testProfileManager = new Mock<IWorkerProfileManager>();

            var configurationDataDict = new Dictionary<string, string>();
            if (dotnetIsolatedPlaceHolderEnabled)
            {
                configurationDataDict.Add(RpcWorkerConstants.FunctionIsolatedPlaceHolderSettingName, "1");
            }
            configurationBuilder.AddInMemoryCollection(configurationDataDict);
            var configuration = configurationBuilder.Build();

            LanguageWorkerOptionsSetup setup = new LanguageWorkerOptionsSetup(configuration, NullLoggerFactory.Instance, testEnvironment, testMetricLogger, testProfileManager.Object);
            LanguageWorkerOptions options = new LanguageWorkerOptions();

            setup.Configure(options);

            if (string.Equals(workerRuntime, RpcWorkerConstants.DotNetLanguageWorkerName, StringComparison.OrdinalIgnoreCase) && dotnetIsolatedPlaceHolderEnabled)
            {
                // we expect all worker configs including the dotnet isolated one.
                Assert.Equal(5, options.WorkerConfigs.Count);
                var isolatedConfig = options.WorkerConfigs.Single(c => c.Description.Language == RpcWorkerConstants.DotNetIsolatedLanguageWorkerName);
                Assert.NotNull(isolatedConfig);
                Assert.True(isolatedConfig.Description.DefaultExecutablePath.EndsWith("FunctionsNetHost.exe"));
            }
            else if (string.Equals(workerRuntime, RpcWorkerConstants.DotNetLanguageWorkerName, StringComparison.OrdinalIgnoreCase) && !dotnetIsolatedPlaceHolderEnabled)
            {
                Assert.Empty(options.WorkerConfigs);
            }
            else
            {
                // any other runtime (node,java, null etc.)
                Assert.Equal(5, options.WorkerConfigs.Count);
            }
        }
    }
}
