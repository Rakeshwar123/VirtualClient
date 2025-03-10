// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions
{
    using System;
    using System.Collections.Generic;
    using System.IO.Abstractions;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Platform;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// MemcachedMemtier workload executor
    /// </summary>
    [SupportedPlatforms("linux-arm64,linux-x64")]
    public class MemcachedExecutor : VirtualClientComponent
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ExampleClientServerExecutor"/> class.
        /// </summary>
        /// <param name="dependencies">Provides all of the required dependencies to the Virtual Client component.</param>
        /// <param name="parameters">
        /// Parameters defined in the execution profile or supplied to the Virtual Client on the command line.
        /// </param>
        public MemcachedExecutor(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters = null)
            : base(dependencies, parameters)
        {
            this.SystemManagement = dependencies.GetService<ISystemManagement>();
            this.ApiClientManager = dependencies.GetService<IApiClientManager>();
            this.FileSystem = this.SystemManagement.FileSystem;
            this.PackageManager = this.SystemManagement.PackageManager;
            this.ProcessManager = this.SystemManagement.ProcessManager;

            // Supported roles for this client/server workload.
            this.SupportedRoles = new List<string>
            {
                ClientRole.Client,
                ClientRole.Server
            };
        }

        /// <summary>
        /// Parameter defines the username to use for running both the Memcached server
        /// as well as the Memtier workload.
        /// </summary>
        public string Username
        {
            get
            {
                string username = this.Parameters.GetValue<string>(nameof(MemcachedExecutor.Username), string.Empty);
                if (string.IsNullOrWhiteSpace(username))
                {
                    username = this.PlatformSpecifics.GetLoggedInUser();
                }

                return username;
            }
        }

        /// <summary>
        /// The Memtier benchmark will return an exit code of 130 when it is interupted while
        /// trying to write to standard output. This happens when Ctrl-C is used for example.
        /// We handle this error for this reason.
        /// </summary>
        protected static IEnumerable<int> SuccessExitCodes { get; } = new List<int>(ProcessProxy.DefaultSuccessCodes) { 130 };

        /// <summary>
        /// Provides the ability to create API clients for interacting with local as well as remote instances
        /// of the Virtual Client API service.
        /// </summary>
        protected IApiClientManager ApiClientManager { get; }

        /// <summary>
        /// Enables access to file system operations.
        /// </summary>
        protected IFileSystem FileSystem { get; }

        /// <summary>
        /// Provides access to the dependency packages on the system.
        /// </summary>
        protected IPackageManager PackageManager { get; }

        /// <summary>
        /// Provides the ability to create isolated operating system processes for running
        /// applications (e.g. workloads) on the system separate from the runtime.
        /// </summary>
        protected ProcessManager ProcessManager { get; }

        /// <summary>
        /// Server IpAddress on which Redis Server runs.
        /// </summary>
        protected string ServerIpAddress { get; set; }

        /// <summary>
        /// Client used to communicate with the hosted instance of the
        /// Virtual Client API at server side.
        /// </summary>
        protected IApiClient ServerApiClient { get; set; }

        /// <summary>
        /// Provides access to dependencies required for interacting with the system, environment
        /// and runtime platform.
        /// </summary>
        protected ISystemManagement SystemManagement { get; }

        /// <summary>
        /// Executes the workload.
        /// </summary>
        /// <param name="telemetryContext">Provides context information that will be captured with telemetry events.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        protected override Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            // The derived classes are expected to implement this method.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Executes the commands.
        /// </summary>
        /// <param name="command">The command to run.</param>
        /// <param name="arguments">The command line arguments to supply to the command.</param>
        /// <param name="workingDir">The working directory for the command.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <param name="successCodes">Alternative exit codes to use to represent successful process exit.</param>
        protected async Task ExecuteCommandAsync(string command, string arguments, string workingDir, CancellationToken cancellationToken, IEnumerable<int> successCodes = null)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                this.Logger.LogTraceMessage($"Executing process '{command}' '{arguments}' at directory '{workingDir}'.");

                EventContext telemetryContext = EventContext.Persisted()
                    .AddContext("packagePath", workingDir)
                    .AddContext("command", command)
                    .AddContext("commandArguments", arguments);

                await this.Logger.LogMessageAsync($"{this.TypeName}.ExecuteProcess", telemetryContext, async () =>
                {
                    using (IProcessProxy process = this.ProcessManager.CreateElevatedProcess(this.Platform, command, arguments, workingDir))
                    {
                        this.CleanupTasks.Add(() => process.SafeKill());
                        await process.StartAndWaitAsync(cancellationToken).ConfigureAwait();

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await this.LogProcessDetailsAsync(process, telemetryContext);

                            process.ThrowIfErrored<WorkloadException>(
                                successCodes ?? ProcessProxy.DefaultSuccessCodes,
                                errorReason: ErrorReason.WorkloadFailed);
                        }
                    }
                }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Initializes the environment and dependencies for running the Memcached Memtier workload.
        /// </summary>
        protected override async Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            this.Logger.LogTraceMessage($"Username = '{this.Username}'");

            await this.ValidatePlatformSupportAsync(cancellationToken);
            await this.EvaluateParametersAsync(cancellationToken);

            if (this.IsMultiRoleLayout())
            {
                ClientInstance clientInstance = this.GetLayoutClientInstance();
                string layoutIPAddress = clientInstance.IPAddress;

                this.ThrowIfLayoutClientIPAddressNotFound(layoutIPAddress);
                this.ThrowIfRoleNotSupported(clientInstance.Role);
            }
        }

        /// <summary>
        /// Initializes API client.
        /// </summary>
        protected void InitializeApiClients()
        {
            IApiClientManager clientManager = this.Dependencies.GetService<IApiClientManager>();
            bool isSingleVM = !this.IsMultiRoleLayout();

            if (isSingleVM)
            {
                this.ServerApiClient = clientManager.GetOrCreateApiClient(IPAddress.Loopback.ToString(), IPAddress.Loopback);
            }
            else
            {
                ClientInstance serverInstance = this.GetLayoutClientInstances(ClientRole.Server).First();
                IPAddress.TryParse(serverInstance.IPAddress, out IPAddress serverIPAddress);

                this.ServerApiClient = clientManager.GetOrCreateApiClient(serverIPAddress.ToString(), serverIPAddress);
                this.RegisterToSendExitNotifications($"{this.TypeName}.ExitNotification", this.ServerApiClient);
            }
        }

        private async Task ValidatePlatformSupportAsync(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                switch (this.Platform)
                {
                    case PlatformID.Unix:
                        LinuxDistributionInfo distroInfo = await this.SystemManagement.GetLinuxDistributionAsync(cancellationToken);

                        switch (distroInfo.LinuxDistribution)
                        {
                            case LinuxDistribution.Ubuntu:
                            case LinuxDistribution.Debian:
                            case LinuxDistribution.CentOS8:
                            case LinuxDistribution.RHEL8:
                            case LinuxDistribution.AzLinux:
                            case LinuxDistribution.AwsLinux:
                                break;
                            default:
                                throw new WorkloadException(
                                    $"The workload/benchmark is not supported on the current Linux distro " +
                                    $"'{distroInfo.LinuxDistribution}'.  Supported distros include: " +
                                    $"{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.Ubuntu)},{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.Debian)}" +
                                    $"{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.CentOS8)},{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.RHEL8)},{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.AzLinux)},{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.AwsLinux)}",
                                    ErrorReason.LinuxDistributionNotSupported);
                        }

                        break;

                    default:
                        throw new WorkloadException(
                            $"The workload/benchmark workload is currently not supported on the current platform/architecture " +
                            $"'{this.PlatformArchitectureName}'. Supported platform/architectures include: " +
                            $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.X64)}, " +
                            $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.Arm64)}",
                            ErrorReason.PlatformNotSupported);
                }
            }
        }
    }
}
