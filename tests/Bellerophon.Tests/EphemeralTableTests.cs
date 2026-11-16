// <copyright file="EphemeralTableTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Microsoft.CodeAnalysis;
    using Xunit;

    /// <summary>
    /// Ephemeral tables (kept in memory, never saved) are full members of the model: row versions, the change feed, facts,
    /// transfer objects, the API and sync.  Only the database is skipped.  A server-only table is read through the API and
    /// written only by the server (derived state, written by rules).
    /// </summary>
    public class EphemeralTableTests
    {
        private const string Model = """
            {
              "access": { "read": ["Ledger.Read"], "write": ["Ledger.Write"] },
              "datatypes": {
                "guid": "System.Guid",
                "quantity": { "precision": 18, "scale": 6, "type": "System.Decimal" },
                "symbol": { "maxLength": 16, "type": "System.String" },
                "time": "System.DateTimeOffset"
              },
              "name": "Holdings",
              "namespace": "Test.Holdings",
              "tables": {
                "Account": {
                  "columns": { "AccountId": "guid", "Name": "symbol" },
                  "key": ["AccountId"]
                },
                "Position": {
                  "columns": { "AccountId": "guid", "Quantity": "quantity", "Symbol": "symbol" },
                  "key": ["AccountId", "Symbol"],
                  "persistence": "Ephemeral",
                  "references": { "Account": { "columns": ["AccountId"] } },
                  "writeMode": "ServerOnly"
                },
                "Price": {
                  "columns": { "AsOf": "time", "Last": "quantity", "Symbol": "symbol" },
                  "concurrency": "LatestValue",
                  "key": ["Symbol"],
                  "persistence": "Ephemeral"
                }
              },
              "version": "1.0"
            }
            """;

        private static readonly Lazy<Assembly> Holdings = new Lazy<Assembly>(Compile);

        /// <summary>
        /// An ephemeral table gets everything a saved table gets except the database: a transfer object, a fact, a
        /// controller, a place in the client's Replica; a server-only table's controller only reads, and the batch endpoint
        /// leaves it out.
        /// </summary>
        [Fact]
        public void EphemeralTablesAreModeledButNotSaved()
        {
            var primary = Sources("Primary");
            var replica = Sources("Replica");

            Assert.Contains("Holdings.Primary.PositionDto.g.cs", primary.Keys);
            Assert.Contains("Holdings.Primary.PositionFact.g.cs", primary.Keys);
            Assert.Contains("HttpGet", primary["Holdings.Primary.PositionController.g.cs"]);
            Assert.DoesNotContain("HttpPost", primary["Holdings.Primary.PositionController.g.cs"]);
            Assert.DoesNotContain("HttpPut", primary["Holdings.Primary.PositionController.g.cs"]);
            Assert.DoesNotContain("HttpDelete", primary["Holdings.Primary.PositionController.g.cs"]);
            Assert.Contains("HttpPost", primary["Holdings.Primary.AccountController.g.cs"]);
            Assert.DoesNotContain("PositionTable", primary["Holdings.Primary.HoldingsBatchController.g.cs"]);
            Assert.DoesNotContain("Position", primary["Holdings.Primary.HoldingsCut.g.cs"]);
            Assert.Contains("Holdings.Replica.Position.g.cs", replica.Keys);
            Assert.DoesNotContain("Holdings.Replica.Price.g.cs", replica.Keys);
        }

        /// <summary>
        /// An ephemeral row takes the tenant's sequence as its version, refuses a stale update like a saved row, and
        /// reaches the change feed, deletions included.
        /// </summary>
        [Fact]
        public void EphemeralRowsHaveVersionsAndReachTheChangeFeed()
        {
            var partition = NewPartition(usesWriteBehind: false);
            var accountId = Guid.NewGuid();
            partition.InsertAccount(Make("Account", ("AccountId", accountId), ("Name", "Joe 10")));
            var position = partition.InsertPosition(Make("Position", ("AccountId", accountId), ("Symbol", "AAPL"), ("Quantity", 10m)));
            var inserted = (long)position.RowVersion;
            Assert.Equal((long)partition.CommittedSequence, inserted);

            var updated = partition.UpdatePosition(Make("Position", ("AccountId", accountId), ("Symbol", "AAPL"), ("Quantity", 12m), ("RowVersion", inserted)));
            Assert.Throws<System.Data.DBConcurrencyException>(() => partition.UpdatePosition(Make("Position", ("AccountId", accountId), ("Symbol", "AAPL"), ("Quantity", 13m), ("RowVersion", inserted))));

            var afterInsert = inserted - 1;
            var changes = partition.ReadChangesSince(afterInsert);
            Assert.Single((System.Collections.IEnumerable)changes.ChangedPositions);
            Assert.Equal(12m, (decimal)changes.ChangedPositions[0].Quantity);

            var beforeDelete = (long)partition.CommittedSequence;
            Assert.True((bool)partition.DeletePosition(accountId, "AAPL", (long)updated.RowVersion));
            var deletions = partition.ReadChangesSince(beforeDelete);
            Assert.Single((System.Collections.IEnumerable)deletions.DeletedPositions);
            Assert.Empty((System.Collections.IEnumerable)deletions.ChangedAccounts);
        }

        /// <summary>
        /// A commit that changes only ephemeral rows has nothing to save: with nothing waiting to be saved it is durable at
        /// once; with saved changes waiting, it becomes durable when the cut that covers them is.
        /// </summary>
        [Fact]
        public void EphemeralOnlyCommitsAreDurableWithoutASave()
        {
            var partition = NewPartition(usesWriteBehind: true);
            var accountId = Guid.NewGuid();
            var account = partition.InsertAccount(Make("Account", ("AccountId", accountId), ("Name", "Joe 10")));
            Assert.Equal(0L, (long)partition.DurableSequence);

            // Saved change waiting: the ephemeral commit after it waits with it.
            partition.InsertPosition(Make("Position", ("AccountId", accountId), ("Symbol", "AAPL"), ("Quantity", 10m)));
            Assert.Equal(0L, (long)partition.DurableSequence);

            var cut = partition.TakeCut();
            partition.InsertPosition(Make("Position", ("AccountId", accountId), ("Symbol", "MSFT"), ("Quantity", 5m)));
            Assert.Equal(0L, (long)partition.DurableSequence);   // a cut is being saved: not yet

            partition.MarkDurable((long)cut.Sequence);
            Assert.Equal((long)partition.CommittedSequence, (long)partition.DurableSequence);   // the ephemeral commits came with it

            // Nothing waiting: durable at once.
            partition.InsertPosition(Make("Position", ("AccountId", accountId), ("Symbol", "NVDA"), ("Quantity", 1m)));
            Assert.Equal((long)partition.CommittedSequence, (long)partition.DurableSequence);
            Assert.True(((System.Threading.Tasks.Task)partition.WaitUntilDurableAsync((long)partition.CommittedSequence)).IsCompleted);

            // A saved change waiting again: ephemeral commits wait for its cut.
            partition.UpdateAccount(Make("Account", ("AccountId", accountId), ("Name", "Joe 10 paper"), ("RowVersion", (long)account.RowVersion)));
            var waiting = (long)partition.CommittedSequence;
            partition.InsertPosition(Make("Position", ("AccountId", accountId), ("Symbol", "AMZN"), ("Quantity", 2m)));
            Assert.True((long)partition.DurableSequence < waiting);
        }

        /// <summary>
        /// Replacing a derived table touches only what differs: unchanged rows keep their versions and stay out of the
        /// change feed; changed rows are updated, new ones inserted, and rows no longer derived deleted.
        /// </summary>
        [Fact]
        public void ReplacingADerivedTableTouchesOnlyWhatDiffers()
        {
            var partition = NewPartition(usesWriteBehind: false);
            var accountId = Guid.NewGuid();
            partition.InsertAccount(Make("Account", ("AccountId", accountId), ("Name", "Joe 10")));
            Assert.Equal(2, Replace(partition, (accountId, "AAPL", 10m), (accountId, "MSFT", 5m)));
            var apple = (long)partition.FindPosition(accountId, "AAPL").RowVersion;
            var before = (long)partition.CommittedSequence;

            Assert.Equal(0, Replace(partition, (accountId, "AAPL", 10m), (accountId, "MSFT", 5m)));
            Assert.Equal(before, (long)partition.CommittedSequence);

            Assert.Equal(3, Replace(partition, (accountId, "AAPL", 10m), (accountId, "MSFT", 6m), (accountId, "NVDA", 1m)) + Replace(partition, (accountId, "MSFT", 6m), (accountId, "NVDA", 1m)));
            Assert.Null(partition.FindPosition(accountId, "AAPL"));
            Assert.Equal(6m, (decimal)partition.FindPosition(accountId, "MSFT").Quantity);
            var changes = partition.ReadChangesSince(before);
            Assert.Equal(2, ((System.Collections.ICollection)changes.ChangedPositions).Count);
            Assert.Single((System.Collections.IEnumerable)changes.DeletedPositions);
            Assert.True(apple <= before);
        }

        /// <summary>
        /// Every partition has its own epoch, so a reader can tell a restarted server (whose ephemeral rows were rebuilt
        /// with new sequences) from the one it was following.
        /// </summary>
        [Fact]
        public void EachPartitionHasItsOwnEpoch()
        {
            var tenantId = Guid.NewGuid();
            var partitionType = Holdings.Value.GetType("Test.Holdings.Primary.HoldingsPartition")!;
            dynamic first = Activator.CreateInstance(partitionType, tenantId, false)!;
            dynamic second = Activator.CreateInstance(partitionType, tenantId, false)!;

            Assert.NotEqual(Guid.Empty, (Guid)first.Epoch);
            Assert.NotEqual((Guid)first.Epoch, (Guid)second.Epoch);
        }

        /// <summary>
        /// A saved table may not reference an ephemeral one: the reference would dangle after a restart.
        /// </summary>
        [Fact]
        public void SavedTablesCannotReferenceEphemeralOnes()
        {
            var text = Model.Replace(
                "\"key\": [\"AccountId\"]\n",
                "\"key\": [\"AccountId\"],\n      \"references\": { \"Position\": { \"columns\": [\"AccountId\", \"Name\"] } }\n");

            var result = Bellerophon.Modeling.ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == Bellerophon.Modeling.Diagnostics.DiagnosticIds.DurableReferencesEphemeral);
        }

        // A transaction is a ref struct, so the replacement runs in C# compiled with the model.
        private static int Replace(dynamic partition, params (Guid AccountId, string Symbol, decimal Quantity)[] positions) =>
            (int)Holdings.Value.GetType("Scenarios.Derived")!.GetMethod("Replace")!.Invoke(null, new object[] { partition, positions })!;

        private static System.Collections.Generic.Dictionary<string, string> Sources(string role)
        {
            var run = GeneratorTests.Run(Model, role, includeEnums: false);
            Assert.Empty(run.Result.Diagnostics);
            return run.Result.GeneratedTrees.ToDictionary(tree => Path.GetFileName(tree.FilePath), tree => tree.ToString());
        }

        private static dynamic NewPartition(bool usesWriteBehind)
        {
            var partitionType = Holdings.Value.GetType("Test.Holdings.Primary.HoldingsPartition")!;
            return Activator.CreateInstance(partitionType, Guid.NewGuid(), usesWriteBehind)!;
        }

        private static dynamic Make(string type, params (string Name, object Value)[] values)
        {
            var rowType = Holdings.Value.GetType($"Test.Holdings.Primary.{type}")!;
            var row = Activator.CreateInstance(rowType)!;
            foreach (var (name, value) in values)
            {
                rowType.GetProperty(name)!.SetValue(row, value);
            }

            return row;
        }

        private static Assembly Compile() => TransactionTests.Compile(Model, "Primary", """
            namespace Scenarios
            {
                using System;
                using System.Linq;
                using Test.Holdings.Primary;

                public static class Derived
                {
                    public static int Replace(HoldingsPartition partition, (Guid AccountId, string Symbol, decimal Quantity)[] positions)
                    {
                        using var transaction = partition.BeginTransaction();
                        var changed = transaction.ReplacePositions(positions.Select(item => new Position { AccountId = item.AccountId, Symbol = item.Symbol, Quantity = item.Quantity }));
                        transaction.Commit();
                        return changed;
                    }
                }
            }
            """, includeEnums: false);
    }
}
