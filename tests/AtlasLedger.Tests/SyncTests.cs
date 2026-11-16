// <copyright file="SyncTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model.Replica;
    using Xunit;
    using ReplicaLedger = client::AtlasLedger.Model.Ledger;

    /// <summary>
    /// Client sync end to end: a Replica kept current by the generated sync client against the sample server.  A snapshot
    /// first, then only what changed (deletions included), long polling, the tables a view asks for (with their parents),
    /// and a snapshot again when the epoch is not the partition's.
    /// </summary>
    public sealed class SyncTests : IClassFixture<ServerFixture>
    {
        private const string Node = "5a000000-0000-0000-0000-000000000001";

        private static readonly JsonSerializerOptions Json = CreateJson();

        private readonly ServerFixture server;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncTests"/> class.
        /// </summary>
        /// <param name="server">The server.</param>
        public SyncTests(ServerFixture server)
        {
            this.server = server;
        }

        /// <summary>
        /// The first sync is a snapshot of the tenant; later ones bring inserts, updates and deletions; Synced names the
        /// tables that changed.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task AReplicaFollowsTheServer()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tenant = await this.NewTenantAsync();
            using var http = this.Http();
            var ledger = new ReplicaLedger();
            var sync = new LedgerSyncClient(http, ledger, tenant, null, _ => ValueTask.FromResult<string?>(null), Json);
            var synced = new List<LedgerSyncedEventArgs>();
            sync.Synced += (_, change) => synced.Add(change);

            Assert.True(await sync.SyncAsync(TimeSpan.Zero, cancellationToken));
            Assert.Equal(2, sync.Partition.Nodes.Count);
            Assert.Equal(2, sync.Partition.Symbols.Count);
            Assert.True(synced.Single().IsReset);
            var sequence = sync.Sequence;
            Assert.True(sequence > 0);

            await this.ApplyAsync(tenant, $$"""{ "operations": [ { "action": "Insert", "table": "Symbol", "row": { "scheme": "Ticker", "code": "SMTH", "nodeId": "{{Node}}" } } ] }""");
            Assert.True(await sync.SyncAsync(TimeSpan.Zero, cancellationToken));
            Assert.Equal(3, sync.Partition.Symbols.Count);
            Assert.Equal(new[] { "Symbol" }, synced[^1].Tables);
            Assert.False(synced[^1].IsReset);

            var symbol = sync.Partition.Symbols.Single(row => row.Code == "SMTH");
            await this.ApplyAsync(tenant, $$"""{ "operations": [ { "action": "Delete", "table": "Symbol", "key": { "scheme": "Ticker", "code": "SMTH" }, "rowVersion": {{symbol.RowVersion}} } ] }""");
            Assert.True(await sync.SyncAsync(TimeSpan.Zero, cancellationToken));
            Assert.DoesNotContain(sync.Partition.Symbols, row => row.Code == "SMTH");

            Assert.False(await sync.SyncAsync(TimeSpan.Zero, cancellationToken));   // nothing new
        }

        /// <summary>
        /// A sync that finds nothing waits for the next durable commit and answers with it.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task ALongPollAnswersAtTheNextCommit()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tenant = await this.NewTenantAsync();
            using var http = this.Http();
            var sync = new LedgerSyncClient(http, new ReplicaLedger(), tenant, new[] { "Symbol" }, _ => ValueTask.FromResult<string?>(null), Json);
            await sync.SyncAsync(TimeSpan.Zero, cancellationToken);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var waiting = sync.SyncAsync(TimeSpan.FromSeconds(20), cancellationToken);
            await Task.Delay(300, cancellationToken);
            Assert.False(waiting.IsCompleted);
            await this.ApplyAsync(tenant, $$"""{ "operations": [ { "action": "Insert", "table": "Symbol", "row": { "scheme": "Ticker", "code": "LEE", "nodeId": "{{Node}}" } } ] }""");

            Assert.True(await waiting);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The long poll took {watch.Elapsed}.");
            Assert.Contains(sync.Partition.Symbols, row => row.Code == "LEE");
        }

        /// <summary>
        /// A view gets the tables it asks for and their parents, and nothing else.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task AViewGetsTheTablesItAsksForAndTheirParents()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tenant = await this.NewTenantAsync();
            using var http = this.Http();
            var sync = new LedgerSyncClient(http, new ReplicaLedger(), tenant, new[] { "Symbol" }, _ => ValueTask.FromResult<string?>(null), Json);

            Assert.Equal(new[] { "Node", "Symbol" }, sync.Tables);
            await sync.SyncAsync(TimeSpan.Zero, cancellationToken);
            Assert.Equal(2, sync.Partition.Symbols.Count);
            Assert.Equal(2, sync.Partition.Nodes.Count);
            Assert.Empty(sync.Partition.Accounts);
            Assert.Throws<ArgumentException>(() => new LedgerSyncClient(http, new ReplicaLedger(), tenant, new[] { "Price" }, _ => ValueTask.FromResult<string?>(null), Json));
        }

        /// <summary>
        /// A client following another epoch of the partition (a server since restarted) gets a snapshot.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task AnotherEpochGetsASnapshot()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tenant = await this.NewTenantAsync();
            using var http = this.Http();

            using var first = await http.GetAsync($"v1/tenants/{tenant}/changes?tables=Symbol", cancellationToken);
            using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync(cancellationToken));
            var through = firstBody.RootElement.GetProperty("through").GetInt64();
            var epoch = firstBody.RootElement.GetProperty("epoch").GetGuid();
            Assert.True(firstBody.RootElement.GetProperty("reset").GetBoolean());

            using var same = await http.GetAsync($"v1/tenants/{tenant}/changes?tables=Symbol&since={through}&epoch={epoch}", cancellationToken);
            using var sameBody = JsonDocument.Parse(await same.Content.ReadAsStringAsync(cancellationToken));
            Assert.False(sameBody.RootElement.GetProperty("reset").GetBoolean());

            using var other = await http.GetAsync($"v1/tenants/{tenant}/changes?tables=Symbol&since={through}&epoch={Guid.NewGuid()}", cancellationToken);
            using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync(cancellationToken));
            Assert.True(otherBody.RootElement.GetProperty("reset").GetBoolean());
            Assert.Equal(2, otherBody.RootElement.GetProperty("tables").GetProperty("symbol").GetProperty("rows").GetArrayLength());

            using var unknown = await http.GetAsync($"v1/tenants/{tenant}/changes?tables=Nothing", cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        private static JsonSerializerOptions CreateJson()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private HttpClient Http()
        {
            var http = new HttpClient { BaseAddress = this.server.HttpClient.BaseAddress };
            http.DefaultRequestHeaders.Add(DevelopmentCallers.Header, DevelopmentCallers.Tool);
            return http;
        }

        private async Task<Guid> NewTenantAsync()
        {
            var tenant = Guid.NewGuid();
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            await tool.ApplyAsync(tenant, await LedgerScript.ReadAsync(client::AtlasLedger.Client.Walkthrough.FirmAScript, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            return tenant;
        }

        private async Task ApplyAsync(Guid tenant, string script)
        {
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            await tool.ApplyAsync(tenant, JsonSerializer.Deserialize<LedgerScript>(script, LedgerApiClient.SerializerOptions)!, TestContext.Current.CancellationToken);
        }
    }
}
