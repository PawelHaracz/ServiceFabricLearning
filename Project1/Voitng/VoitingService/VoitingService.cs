using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using System.Fabric.Health;

namespace VoitingService
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class VoitingService : StatelessService
    {
        private TimeSpan _interval = TimeSpan.FromSeconds(30);
        private long _lastCount = 0L;
        private DateTime _lastReport = DateTime.UtcNow;
        private Timer _healthTimer = null;
        private FabricClient _client = null;

        public VoitingService(StatelessServiceContext context)
            : base(context)
        {
            _healthTimer = new Timer(ReportHealthAndLoad, null, Timeout.Infinite, Timeout.Infinite);
            context.CodePackageActivationContext.CodePackageModifiedEvent += CodePackageActivationContextOnCodePackageModifiedEvent;
        }

        private void CodePackageActivationContextOnCodePackageModifiedEvent(object sender, PackageModifiedEventArgs<CodePackage> packageModifiedEventArgs)
        {
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            var pkg = Context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            if (null != pkg)
            {
                if (pkg.Settings?.Sections?.Contains("Health") == true)
                {
                    var settings = pkg.Settings.Sections["Health"];
                    if (settings?.Parameters.Contains("HealthCheckIntervalSeconds") == true)
                    {
                        var prop = settings.Parameters["HealthCheckIntervalSeconds"];
                        if (long.TryParse(prop?.Value, out var value))
                        {
                            _interval = TimeSpan.FromSeconds(Math.Max(30, value));
                            _healthTimer?.Dispose();
                            _healthTimer = new Timer(ReportHealthAndLoad, null, _interval, _interval);
                        }
                    }
                }

            }
        }

        protected override Task OnOpenAsync(CancellationToken cancellationToken)
        {
            _client = new FabricClient();
            LoadConfiguration();
            _healthTimer = new Timer(ReportHealthAndLoad, null, _interval, _interval);
            return base.OnOpenAsync(cancellationToken);
        }

        private void ReportHealthAndLoad(object state)
        {
            var total = Controllers.VotesController.RequestCount;
            var diff = total - _lastCount;
            var duration = Math.Max((long) DateTime.UtcNow.Subtract(_lastReport).TotalSeconds, 1L);
            var rps = diff / duration;
            _lastCount = total;
            _lastReport = DateTime.UtcNow;

            var hi = new HealthInformation("VotingServiceHealth", "Heartbeat", HealthState.Ok)
            {
                TimeToLive = _interval.Add(_interval),
                Description = $"{diff} requests since last report. RPS: {rps} Total requests: {total}.",
                RemoveWhenExpired = false,
                SequenceNumber = HealthInformation.AutoSequenceNumber
            };
            var sshr = new StatelessServiceInstanceHealthReport(Context.PartitionId, Context.InstanceId, hi);
            _client.HealthManager.ReportHealth(sshr);

            Partition.ReportLoad(new[] { new LoadMetric("RPS", (int) rps) });

            ServiceEventSource.Current.HealthReport(hi.SourceId, hi.Property, Enum.GetName(typeof(HealthState), hi.HealthState), Context.PartitionId, Context.ReplicaOrInstanceId, hi.Description);

            var nodeList = _client.QueryManager.GetNodeListAsync(Context.NodeContext.NodeName).GetAwaiter().GetResult();
            var node = nodeList[0];
            if ("4" == node.UpgradeDomain || "3" == node.UpgradeDomain || "2" == node.UpgradeDomain)
            {
                hi = new HealthInformation("VotingServiceHealth", "Heartbeat", HealthState.Error);
                hi.TimeToLive = _interval.Add(_interval);
                hi.Description = $"Bogus health error to force rollback.";
                hi.RemoveWhenExpired = true;
                hi.SequenceNumber = HealthInformation.AutoSequenceNumber;
                sshr = new StatelessServiceInstanceHealthReport(Context.PartitionId, Context.InstanceId, hi);
                _client.HealthManager.ReportHealth(sshr);
            }

        }


        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[]
            {
                new ServiceInstanceListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        return new WebHostBuilder()
                                    .UseKestrel()
                                    .ConfigureServices(
                                        services => services
                                            .AddSingleton<StatelessServiceContext>(serviceContext))
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseStartup<Startup>()
                                    .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
                                    .UseUrls(url)
                                    .Build();
                    }))
            };
        }
    }
}
