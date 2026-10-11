// <copyright file="RulesEngineHost.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs a rules engine for each tenant, with a shared limit on how many tenants' cycles run at once.
    /// </summary>
    /// <remarks>
    /// Idle tenants cost nothing: an engine waits until its tenant changes.  The limit keeps the number of sessions firing at
    /// once near the number of cores, so one busy period never oversubscribes the machine.
    /// </remarks>
    public sealed class RulesEngineHost : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<Guid, Running> engines = new ConcurrentDictionary<Guid, Running>();

        private readonly IRulesSessionFactory sessionFactory;

        private readonly RulesEngineOptions options;

        private readonly SemaphoreSlim concurrencyGate;

        /// <summary>
        /// Initializes a new instance of the <see cref="RulesEngineHost"/> class.
        /// </summary>
        /// <param name="sessionFactory">Creates each tenant's session.</param>
        /// <param name="options">The cycle settings, or null for the defaults.</param>
        /// <param name="maximumConcurrentCycles">How many tenants' cycles may run at once; zero or less means the number of processors.</param>
        public RulesEngineHost(IRulesSessionFactory sessionFactory, RulesEngineOptions? options = null, int maximumConcurrentCycles = 0)
        {
            this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            this.options = options ?? new RulesEngineOptions();
            var limit = maximumConcurrentCycles > 0 ? maximumConcurrentCycles : Environment.ProcessorCount;
            this.concurrencyGate = new SemaphoreSlim(limit, limit);
        }

        /// <summary>
        /// Raised after each completed cycle of any tenant.
        /// </summary>
        public event EventHandler<RulesCycleEventArgs>? CycleCompleted;

        /// <summary>
        /// Raised after a failed cycle of any tenant.
        /// </summary>
        public event EventHandler<RulesCycleFailedEventArgs>? CycleFailed;

        /// <summary>
        /// Gets the tenants whose engines are running.
        /// </summary>
        public IReadOnlyCollection<Guid> Tenants => (IReadOnlyCollection<Guid>)this.engines.Keys;

        /// <summary>
        /// Starts a tenant's engine.  The host owns the tenant from now on and disposes it when the engine stops.
        /// </summary>
        /// <param name="tenant">The tenant.</param>
        /// <returns>True if started; false if the tenant's engine is already running.</returns>
        public bool Start(IRulesTenant tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            var engine = new TenantRulesEngine(tenant, this.sessionFactory, this.options, this.concurrencyGate);
            var cancellation = new CancellationTokenSource();
            var running = new Running(engine, tenant, cancellation);
            if (!this.engines.TryAdd(tenant.TenantId, running))
            {
                cancellation.Dispose();
                return false;
            }

            engine.CycleCompleted += (sender, eventArgs) => this.CycleCompleted?.Invoke(this, eventArgs);
            engine.CycleFailed += (sender, eventArgs) => this.CycleFailed?.Invoke(this, eventArgs);
            running.Task = Task.Run(() => engine.RunAsync(cancellation.Token));
            return true;
        }

        /// <summary>
        /// Finds a tenant's running engine, for monitoring.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The engine, or null.</returns>
        public TenantRulesEngine? FindEngine(Guid tenantId) => this.engines.TryGetValue(tenantId, out var running) ? running.Engine : null;

        /// <summary>
        /// Stops a tenant's engine, for example before its partition is discarded and reloaded.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>True if an engine was stopped.</returns>
        public async Task<bool> StopAsync(Guid tenantId)
        {
            if (!this.engines.TryRemove(tenantId, out var running))
            {
                return false;
            }

            await running.StopAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Stops every engine.
        /// </summary>
        /// <returns>A task that completes when every engine has stopped.</returns>
        public async ValueTask DisposeAsync()
        {
            var stopping = new List<Task>();
            foreach (var tenantId in this.engines.Keys)
            {
                stopping.Add(this.StopAsync(tenantId));
            }

            await Task.WhenAll(stopping).ConfigureAwait(false);
            this.concurrencyGate.Dispose();
        }

        private sealed class Running
        {
            public Running(TenantRulesEngine engine, IRulesTenant tenant, CancellationTokenSource cancellation)
            {
                this.Engine = engine;
                this.Tenant = tenant;
                this.Cancellation = cancellation;
            }

            public TenantRulesEngine Engine { get; }

            public IRulesTenant Tenant { get; }

            public CancellationTokenSource Cancellation { get; }

            public Task Task { get; set; } = Task.CompletedTask;

            public async Task StopAsync()
            {
                await this.Cancellation.CancelAsync().ConfigureAwait(false);
                await this.Task.ConfigureAwait(false);
                this.Cancellation.Dispose();
                (this.Tenant as IDisposable)?.Dispose();
            }
        }
    }
}
