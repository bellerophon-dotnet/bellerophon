// <copyright file="TradingTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Xunit;

    /// <summary>
    /// Tests for the tables the real-time tier relies on: append-only orders with an idempotent client order id, and a
    /// lock-free latest-value price store.
    /// </summary>
    public class TradingTests
    {
        private const string Model = """
            {
              "access": { "read": ["Ledger.Read"], "write": ["Ledger.Write"] },
              "datatypes": {
                "guid": "System.Guid",
                "price": { "precision": 18, "scale": 6, "type": "System.Decimal" },
                "symbol": { "maxLength": 16, "type": "System.String" },
                "time": "System.DateTimeOffset"
              },
              "name": "Trading",
              "namespace": "Test.Trading",
              "tables": {
                "Order": {
                  "columns": { "ClientOrderId": "guid", "OrderId": "guid", "Quantity": "price", "Symbol": "symbol" },
                  "key": ["OrderId"],
                  "unique": { "ClientOrder": { "columns": ["ClientOrderId"], "idempotent": true } },
                  "writeMode": "AppendOnly"
                },
                "Price": {
                  "columns": { "AsOf": "time", "Last": "price", "Symbol": "symbol" },
                  "concurrency": "LatestValue",
                  "key": ["Symbol"],
                  "persistence": "Ephemeral"
                }
              },
              "version": "1.0"
            }
            """;

        private static readonly Lazy<Assembly> Trading = new Lazy<Assembly>(Compile);

        /// <summary>
        /// Append-only tables get inserts only: no update or delete in the partition, the store, or the controller.
        /// </summary>
        [Fact]
        public void AppendOnlyTablesCannotChange()
        {
            var run = GeneratorTests.Run(Model, "Primary", includeEnums: false);
            var sources = run.Result.GeneratedTrees.ToDictionary(tree => Path.GetFileName(tree.FilePath), tree => tree.ToString());

            Assert.Contains("public Order InsertOrder(", sources["Trading.Primary.TradingPartition.g.cs"]);
            Assert.DoesNotContain("UpdateOrder", sources["Trading.Primary.TradingPartition.g.cs"]);
            Assert.DoesNotContain("DeleteOrder", sources["Trading.Primary.TradingPartition.g.cs"]);
            Assert.DoesNotContain("UpdateAsync", sources["Trading.Primary.IOrderStore.g.cs"]);
            Assert.DoesNotContain("HttpPut", sources["Trading.Primary.OrderController.g.cs"]);
            Assert.DoesNotContain("HttpDelete", sources["Trading.Primary.OrderController.g.cs"]);
            Assert.Contains("public decimal Quantity { get; init; }", sources["Trading.Primary.Order.g.cs"]);
        }

        /// <summary>
        /// Inserting a duplicate of an idempotent unique value returns the order already stored, so a retried order
        /// (after a timeout, say) can never be placed twice.
        /// </summary>
        [Fact]
        public void IdempotentInsertReturnsTheStoredRow()
        {
            var partition = NewPartition();
            var clientOrderId = Guid.NewGuid();

            var first = partition.InsertOrder(Make("Order", ("OrderId", Guid.NewGuid()), ("ClientOrderId", clientOrderId), ("Quantity", 10m), ("Symbol", "AAPL")));
            var retried = partition.InsertOrder(Make("Order", ("OrderId", Guid.NewGuid()), ("ClientOrderId", clientOrderId), ("Quantity", 10m), ("Symbol", "AAPL")));

            Assert.Same(first, retried);
            Assert.Single((System.Collections.IEnumerable)partition.Orders);
        }

        /// <summary>
        /// Publishing a price replaces the previous one without taking the partition's lock, and notifies subscribers.
        /// </summary>
        [Fact]
        public void LatestValueStoreReplacesWithoutTheLock()
        {
            var partition = NewPartition();
            var published = 0;
            var eventInfo = ((object)partition).GetType().GetEvent("PricePublished")!;
            var handlerType = eventInfo.EventHandlerType!;
            var counter = new Counter();
            var handler = Delegate.CreateDelegate(handlerType, counter, typeof(Counter).GetMethod(nameof(Counter.Count))!.MakeGenericMethod(handlerType.GetGenericArguments()[0]));
            eventInfo.AddEventHandler(partition, handler);

            var partitionLock = Assert.IsType<System.Threading.Lock>((object)partition.SyncRoot);
            var holder = new System.Threading.Thread(() =>
            {
                lock (partitionLock)
                {
                    System.Threading.Thread.Sleep(200);
                }
            });
            holder.Start();
            System.Threading.Thread.Sleep(50);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            partition.PublishPrice(Make("Price", ("Symbol", "AAPL"), ("Last", 100m), ("AsOf", DateTimeOffset.UtcNow)));
            partition.PublishPrice(Make("Price", ("Symbol", "AAPL"), ("Last", 101m), ("AsOf", DateTimeOffset.UtcNow)));
            watch.Stop();
            holder.Join();
            published = counter.Calls;

            Assert.True(watch.ElapsedMilliseconds < 100, $"Publishing waited {watch.ElapsedMilliseconds} ms for the partition's lock.");
            Assert.Equal(101m, (decimal)partition.FindPrice("AAPL").Last);
            Assert.Single(((System.Collections.IEnumerable)partition.Prices).Cast<object>());
            Assert.Equal(2, published);
        }

        /// <summary>
        /// A latest-value table is looked up by key only: it can neither reference nor be referenced.
        /// </summary>
        [Fact]
        public void LatestValueTablesHaveNoRelations()
        {
            var text = Model.Replace(
                "\"key\": [\"OrderId\"],",
                "\"key\": [\"OrderId\"],\n      \"references\": { \"Price\": { \"columns\": [\"Symbol\"] } },");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidPolicy && diagnostic.Message.Contains("LatestValue"));
        }

        private static dynamic NewPartition()
        {
            dynamic model = Activator.CreateInstance(Trading.Value.GetType("Test.Trading.Trading")!)!;
            return model.GetPartition(Guid.NewGuid());
        }

        private static dynamic Make(string type, params (string Name, object Value)[] values)
        {
            var rowType = Trading.Value.GetType($"Test.Trading.Primary.{type}")!;
            var row = Activator.CreateInstance(rowType)!;
            foreach (var (name, value) in values)
            {
                rowType.GetProperty(name)!.SetValue(row, value);
            }

            return row;
        }

        private static Assembly Compile()
        {
            var run = GeneratorTests.Run(Model, "Primary", includeEnums: false);
            Assert.Empty(run.Result.Diagnostics);
            using var stream = new MemoryStream();
            var result = run.Output.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray());
        }

        /// <summary>
        /// Counts event notifications.
        /// </summary>
        public sealed class Counter
        {
            /// <summary>
            /// Gets the number of notifications.
            /// </summary>
            public int Calls { get; private set; }

            /// <summary>
            /// Counts one notification.
            /// </summary>
            /// <typeparam name="T">The row type.</typeparam>
            /// <param name="sender">The partition that published the row.</param>
            /// <param name="row">The published row.</param>
            public void Count<T>(object? sender, T row) => this.Calls++;
        }
    }
}
