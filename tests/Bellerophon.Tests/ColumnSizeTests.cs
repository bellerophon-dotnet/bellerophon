// <copyright file="ColumnSizeTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Reflection;
    using Xunit;

    /// <summary>
    /// Tests that the ledger refuses values that would not fit their database columns (a string or byte array longer than its
    /// maximum length, a decimal outside its precision and scale), so the database never receives one: a write that failed
    /// there would stop the tenant, and a decimal rounded there would differ from memory after a restart.
    /// </summary>
    public class ColumnSizeTests
    {
        private const string Model = """
            {
              "access": { "read": ["Ledger.Read"], "write": ["Ledger.Write"] },
              "datatypes": {
                "amount": { "precision": 5, "scale": 2, "type": "System.Decimal" },
                "code": { "maxLength": 4, "type": "System.String" },
                "guid": "System.Guid",
                "tag": { "maxLength": 3, "type": "System.Byte[]" }
              },
              "name": "Ledger",
              "namespace": "Sizes.Model",
              "tables": {
                "Holding": {
                  "columns": { "Amount": "amount", "Cap": "amount?", "Code": "code", "HoldingId": "guid", "Tag": "tag?" },
                  "key": ["HoldingId"]
                }
              },
              "version": "1.0"
            }
            """;

        private const string Scenarios = TransactionTests.Common + """
            namespace Scenarios
            {
                using Sizes.Model.Primary;

                public static class Fit
                {
                    private static Holding New(string code, decimal amount) => new Holding { HoldingId = Guid.NewGuid(), Code = code, Amount = amount };

                    private static LedgerPartition NewPartition() => new global::Sizes.Model.Ledger().GetPartition(Guid.NewGuid());

                    public static void ValuesThatFitAreStored()
                    {
                        var partition = NewPartition();
                        partition.InsertHolding(New("ABCD", 999.99m));
                        partition.InsertHolding(New(string.Empty, -999.99m));
                        partition.InsertHolding(New("A", 1.500m));
                        var full = New("B", 0m);
                        full.Cap = 999.99m;
                        full.Tag = new byte[3];
                        partition.InsertHolding(full);
                        Check.That(partition.Holdings.Count == 4, "values at the limits fit");
                    }

                    public static void ValuesThatDoNotFitAreRefused()
                    {
                        var partition = NewPartition();
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(New("ABCDE", 1m)), "a string longer than its maximum length");
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(New("A", 1.005m)), "more decimal places than the scale");
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(New("A", 1000m)), "more digits before the point than the precision allows");
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(New("A", -1000m)), "the same for a negative value");
                        var capped = New("A", 1m);
                        capped.Cap = 0.001m;
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(capped), "a nullable decimal is checked when set");
                        var tagged = New("A", 1m);
                        tagged.Tag = new byte[4];
                        Check.Throws<ConstraintException>(() => partition.InsertHolding(tagged), "a byte array longer than its maximum length");

                        var stored = partition.InsertHolding(New("A", 1m));
                        Check.Throws<ConstraintException>(() => partition.UpdateHolding(new Holding { HoldingId = stored.HoldingId, Code = "TOOLONG", Amount = 1m, RowVersion = stored.RowVersion }), "an update is checked too");
                        Check.That(partition.Holdings.Count == 1 && partition.FindHolding(stored.HoldingId)!.Code == "A", "nothing refused was stored");
                    }
                }
            }
            """;

        private static readonly Lazy<Assembly> Server = new Lazy<Assembly>(() => TransactionTests.Compile(Model, "Primary", Scenarios, includeEnums: false));

        /// <summary>
        /// Values at the limits of their columns are stored.
        /// </summary>
        [Fact]
        public void ValuesThatFitAreStored() => TransactionTests.Run(Server, "Fit", nameof(this.ValuesThatFitAreStored));

        /// <summary>
        /// Values outside their columns are refused with a constraint error, on insert and on update, and change nothing.
        /// </summary>
        [Fact]
        public void ValuesThatDoNotFitAreRefused() => TransactionTests.Run(Server, "Fit", nameof(this.ValuesThatDoNotFitAreRefused));
    }
}
