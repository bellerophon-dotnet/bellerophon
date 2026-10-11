// <copyright file="TenantRulesEngine.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Rules
{
    using System;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs one tenant's rules: a single session, evaluated in cycles, that sees the tenant's committed data and latest
    /// values and commits its decisions in one transaction per cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each cycle reads the changes since the engine's position from the tenant's ordered change feed, applies them to
    /// working memory, fires the rules, and commits the decisions the rules recorded.  The engine starts from a consistent
    /// snapshot, so nothing between the snapshot and the first read is lost, and the feed is in commit order, so updates
    /// never arrive reordered.  The engine's own decisions come back through the feed in the next cycle, as facts.
    /// </para>
    /// <para>
    /// A cycle runs only when something changed, and no sooner than <see cref="RulesEngineOptions.MinimumInterval"/> after
    /// the previous one; changes in between are coalesced.  If the decisions are refused (a stale version, a broken
    /// constraint), nothing is committed and the next cycle decides again from the newer data.  If a cycle fails, the
    /// session is discarded and rebuilt from a snapshot.  The session is used by one thread at a time.
    /// </para>
    /// </remarks>
    public sealed class TenantRulesEngine
    {
        private readonly IRulesTenant tenant;

        private readonly IRulesSessionFactory sessionFactory;

        private readonly RulesEngineOptions options;

        private readonly SemaphoreSlim? concurrencyGate;

        private IRulesSession? session;

        private long appliedSequence;

        private long cycleCount;

        private int consecutiveRejections;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantRulesEngine"/> class.
        /// </summary>
        /// <param name="tenant">The tenant.</param>
        /// <param name="sessionFactory">Creates the tenant's session.</param>
        /// <param name="options">The cycle settings, or null for the defaults.</param>
        /// <param name="concurrencyGate">Limits how many tenants' cycles run at once, shared among engines; or null for no limit.</param>
        public TenantRulesEngine(IRulesTenant tenant, IRulesSessionFactory sessionFactory, RulesEngineOptions? options = null, SemaphoreSlim? concurrencyGate = null)
        {
            this.tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
            this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            this.options = options ?? new RulesEngineOptions();
            this.concurrencyGate = concurrencyGate;
        }

        /// <summary>
        /// Raised after each completed cycle, outside any transaction.
        /// </summary>
        public event EventHandler<RulesCycleEventArgs>? CycleCompleted;

        /// <summary>
        /// Raised after a failed cycle, outside any transaction.  The session is rebuilt before the next cycle.
        /// </summary>
        public event EventHandler<RulesCycleFailedEventArgs>? CycleFailed;

        /// <summary>
        /// Gets the tenant.
        /// </summary>
        public Guid TenantId => this.tenant.TenantId;

        /// <summary>
        /// Gets the sequence working memory reflects.
        /// </summary>
        public long AppliedSequence => Volatile.Read(ref this.appliedSequence);

        /// <summary>
        /// Gets the number of completed cycles.
        /// </summary>
        public long CycleCount => Volatile.Read(ref this.cycleCount);

        /// <summary>
        /// Runs cycles until cancelled: waits for changes, then runs a cycle, no more often than the minimum interval.
        /// </summary>
        /// <param name="cancellationToken">Stops the engine.</param>
        /// <returns>A task that completes when the engine stops.</returns>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var time = this.options.TimeProvider;
            var lastCycle = (long?)null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (this.session != null)
                    {
                        await this.tenant.WaitForChangesAsync(this.AppliedSequence, cancellationToken).ConfigureAwait(false);
                    }

                    if (lastCycle is { } started)
                    {
                        var wait = this.options.MinimumInterval - time.GetElapsedTime(started);
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (this.concurrencyGate != null)
                    {
                        await this.concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                lastCycle = time.GetTimestamp();
                Exception? failure = null;
                try
                {
                    var result = this.RunCycle();
                    this.consecutiveRejections = result.RejectedActions == null ? 0 : this.consecutiveRejections + 1;
                    this.CycleCompleted?.Invoke(this, new RulesCycleEventArgs(result));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    this.concurrencyGate?.Release();
                }

                var backoff = RejectionBackoff(this.consecutiveRejections, this.options.RetryDelay);
                if (failure == null && backoff > TimeSpan.Zero)
                {
                    // The same decision keeps being refused (a rule that breaks a constraint every time): each refusal rebuilds
                    // the session, so slow down rather than rebuild at the cycle rate. The first refusal retries at once.
                    try
                    {
                        await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }

                if (failure != null)
                {
                    this.CycleFailed?.Invoke(this, new RulesCycleFailedEventArgs(this.TenantId, failure));
                    try
                    {
                        await Task.Delay(this.options.RetryDelay, time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Runs one cycle now: reads the changes, applies them, fires the rules, and commits their decisions.  The first
        /// cycle, and the first after a failure or after falling behind the journal, rebuilds working memory from a snapshot.
        /// </summary>
        /// <returns>What the cycle did.</returns>
        /// <exception cref="Exception">The cycle failed: the session is discarded and rebuilt on the next cycle.</exception>
        public RulesCycleResult RunCycle()
        {
            var isRebuild = false;
            try
            {
                FactBatch batch;
                if (this.session == null)
                {
                    (batch, isRebuild) = (this.Rebuild(), true);
                }
                else
                {
                    batch = this.tenant.ReadChangesSince(this.AppliedSequence);
                    if (!batch.IsComplete)
                    {
                        (batch, isRebuild) = (this.Rebuild(), true);
                    }
                }

                var session = this.session!;
                session.Apply(batch.Changes);
                var fired = session.Fire();
                var committed = 0L;
                Exception? rejected = null;
                try
                {
                    committed = this.tenant.ApplyActions();
                }
                catch (Exception exception) when (exception is DBConcurrencyException or ConstraintException)
                {
                    // Nothing was committed: the refusal also undid every other decision of the cycle, and a Rete session
                    // does not fire a rule again until one of its facts changes.  Start the next cycle from a snapshot, so
                    // every rule is evaluated again against the committed data and nothing decided in this cycle is lost.
                    rejected = exception;
                    this.session = null;
                }

                Volatile.Write(ref this.appliedSequence, batch.ThroughSequence);
                Interlocked.Increment(ref this.cycleCount);
                return new RulesCycleResult(this.TenantId, batch.ThroughSequence, batch.Changes.Count, fired, committed, rejected, isRebuild);
            }
            catch
            {
                // Working memory may hold half a cycle: start again from a snapshot, and drop any half-recorded decisions.
                this.session = null;
                this.tenant.DiscardActions();
                throw;
            }
        }

        /// <summary>
        /// The pause before the next cycle after consecutive refused cycles: none after the first, which is usually a race
        /// with another writer, then the retry delay, doubling with each further refusal up to 32 times the retry delay.
        /// </summary>
        /// <param name="consecutiveRejections">The number of refused cycles in a row.</param>
        /// <param name="retryDelay">The configured retry delay.</param>
        /// <returns>The pause.</returns>
        private static TimeSpan RejectionBackoff(int consecutiveRejections, TimeSpan retryDelay)
        {
            if (consecutiveRejections < 2)
            {
                return TimeSpan.Zero;
            }

            return retryDelay * (1 << Math.Min(consecutiveRejections - 2, 5));
        }

        private FactBatch Rebuild()
        {
            this.tenant.DiscardActions();
            this.session = this.sessionFactory.CreateSession(this.tenant);
            return this.tenant.TakeSnapshot();
        }
    }
}
